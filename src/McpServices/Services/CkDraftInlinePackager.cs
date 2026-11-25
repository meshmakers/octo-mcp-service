using System.IO.Compression;
using System.Text;
using Meshmakers.Octo.Backend.McpServices.Models;
using Meshmakers.Octo.Sdk.ServiceClient.AssetRepositoryServices.CkModelCatalog;

namespace Meshmakers.Octo.Backend.McpServices.Services;

/// <summary>
///     AB#9005: turns a CK model draft passed inline to <c>validate_ck_model</c> or <c>import_ck_model</c> (name/content
///     file pairs) into a temporary zip file the SDK can post, so an MCP-only client needs no separate upload. Base64 file content is not
///     accepted (CLAUDE.md: binary payloads go through the file-transfer endpoints).
///     <para>
///     Memory stays bounded per call (AB#9015): the size limit is checked before anything is decoded, file contents
///     are streamed into a zip on disk, and the caller deletes the file when the call is over. Nothing is cached or
///     registered process-wide.
///     </para>
/// </summary>
internal static class CkDraftInlinePackager
{
    /// <summary>Most bytes an inline draft may have: the UTF-8 contents of all files.</summary>
    public const int MaxInlineBytes = 1024 * 1024;

    /// <summary>Most files an inline draft may have (the asset repository's limit for a source zip).</summary>
    public const int MaxInlineFiles = 2_000;

    /// <summary>
    ///     Message number of the refusal of an inline draft over <see cref="MaxInlineBytes" /> or
    ///     <see cref="MaxInlineFiles" />: CkModelInlineDraftTooLarge of the construction kit engine message catalog.
    /// </summary>
    public const int InlineDraftTooLargeNumber = 148;

    /// <summary>Stable error code of that refusal.</summary>
    public const string InlineDraftTooLargeCode = "InlineDraftTooLarge";

    /// <summary>Result of <see cref="Package" />: the temporary zip, or why the draft was refused.</summary>
    /// <param name="FilePath">Path of the temporary zip; the caller deletes it.</param>
    /// <param name="Error">Argument error; no file was written.</param>
    /// <param name="TooLarge">Refusal text when the draft is over the limit; no file was written.</param>
    public sealed record Result(string? FilePath, string? Error, string? TooLarge);

    /// <summary>
    ///     Outcome of <see cref="Stage" />: the temporary zip, or the refusal a tool returns as is. <see cref="Finding" />
    ///     is set for a draft over the limit (message <see cref="InlineDraftTooLargeNumber" />).
    /// </summary>
    /// <param name="FilePath">Path of the temporary zip; the caller deletes it with <see cref="TryDelete" />.</param>
    /// <param name="ErrorMessage">Refusal text; no file was written.</param>
    /// <param name="ErrorCode"><see cref="InlineDraftTooLargeCode" /> for a draft over the limit, else null.</param>
    /// <param name="Finding">The error finding of a draft over the limit, else null.</param>
    public sealed record Staged(string? FilePath, string? ErrorMessage, string? ErrorCode,
        CkModelValidationMessageDto? Finding);

    /// <summary>
    ///     Stages an inline draft for <paramref name="toolName" /> in the server's temp folder: the size check, the
    ///     name check and the zip, with the refusals both CK draft tools return the same way. A failure of the temp
    ///     storage is refused without the exception text (it names the server's temp path).
    /// </summary>
    public static Staged Stage(IReadOnlyList<CkDraftFileDto> files, string toolName)
    {
        Result packaged;
        try
        {
            packaged = Package(files, Path.GetTempPath(), toolName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new Staged(null, "The inline draft could not be staged on the MCP server (temporary storage).",
                null, null);
        }

        if (packaged.TooLarge != null)
        {
            return new Staged(null, packaged.TooLarge, InlineDraftTooLargeCode,
                new CkModelValidationMessageDto
                {
                    MessageNumber = InlineDraftTooLargeNumber, Level = "Error", Message = packaged.TooLarge,
                    Parameters = new Dictionary<string, string> { ["detail"] = packaged.TooLarge }
                });
        }

        return packaged.Error != null
            ? new Staged(null, packaged.Error, null, null)
            : new Staged(packaged.FilePath, null, null, null);
    }

    /// <summary>
    ///     Writes the inline draft files into a temporary zip in <paramref name="tempDirectory" />.
    ///     <paramref name="toolName" /> is named in the refusal of a draft over the limit (the tool to call with an upload).
    /// </summary>
    public static Result Package(IReadOnlyList<CkDraftFileDto> files, string tempDirectory,
        string toolName = "validate_ck_model")
    {
        if (files.Count > MaxInlineFiles)
        {
            return TooLarge($"The inline draft has {files.Count} files; at most {MaxInlineFiles} are allowed inline.",
                toolName);
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var file in files)
        {
            var name = NormalizeName(file?.Name);
            if (name == null)
            {
                return new Result(null,
                    $"Invalid file name '{file?.Name}': use a relative path below ConstructionKit/ " +
                    "(e.g. ckModel.yaml, types/pump.yaml) without '..' segments.", null);
            }

            if (!names.Add(name))
            {
                return new Result(null, $"The file '{name}' is given more than once.", null);
            }

            total += Encoding.UTF8.GetByteCount(file!.Content ?? string.Empty);
            if (total > MaxInlineBytes)
            {
                return TooLarge($"The inline draft has more than {MaxInlineBytes} bytes of file content.", toolName);
            }
        }

        var path = NewTempPath(tempDirectory);
        try
        {
            using var stream = CreatePrivateFile(path);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
            foreach (var file in files)
            {
                var entry = archive.CreateEntry(NormalizeName(file.Name)!, CompressionLevel.Fastest);
                using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                writer.Write(file.Content ?? string.Empty);
            }
        }
        catch
        {
            TryDelete(path);
            throw;
        }

        return new Result(path, null, null);
    }

    /// <summary>Deletes the temporary zip; never throws.</summary>
    public static void TryDelete(string? path)
    {
        if (path == null)
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Best effort: the temp folder is cleaned up by the OS.
        }
        catch (UnauthorizedAccessException)
        {
            // Same.
        }
    }

    /// <summary>The relative zip entry name for <paramref name="name" />, or null when it is not a safe relative path.</summary>
    internal static string? NormalizeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var normalized = name.Trim().Replace('\\', '/');
        if (normalized.StartsWith('/') || normalized.Contains(':'))
        {
            return null;
        }

        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(s => s is "." or ".."))
        {
            return null;
        }

        return string.Join('/', segments);
    }

    private static Result TooLarge(string reason, string toolName) =>
        new(null, null,
            $"{reason} Inline drafts are limited to {MaxInlineBytes} bytes and {MaxInlineFiles} files " +
            $"(message {InlineDraftTooLargeNumber}). Upload a larger draft with prepare_file_upload, PUT the zip to the " +
            $"returned URL and call {toolName} with the transferId.");

    /// <summary>Creates the temp zip readable by the service user only (the shared temp folder is world-readable).</summary>
    private static FileStream CreatePrivateFile(string path)
    {
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        return new FileStream(path, options);
    }

    private static string NewTempPath(string tempDirectory) =>
        Path.Combine(tempDirectory, $"octo-mcp-ck-inline-{Guid.NewGuid():N}.zip");
}
