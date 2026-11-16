using System.IO.Compression;
using System.Text;
using Meshmakers.Octo.Backend.McpServices.Models;

namespace Meshmakers.Octo.Backend.McpServices.Services;

/// <summary>
///     AB#9005: turns a CK model draft passed inline to <c>validate_ck_model</c> (name/content file pairs or a base64
///     zip) into a temporary zip file the SDK can post, so an MCP-only client needs no separate upload.
///     <para>
///     Memory stays bounded per call (AB#9015): the size limit is checked before anything is decoded, file contents
///     are streamed into a zip on disk, and the caller deletes the file when the call is over. Nothing is cached or
///     registered process-wide.
///     </para>
/// </summary>
internal static class CkDraftInlinePackager
{
    /// <summary>Most bytes an inline draft may have: the UTF-8 contents of all files, or the decoded zip.</summary>
    public const int MaxInlineBytes = 1024 * 1024;

    /// <summary>Most characters of <c>zipBase64</c> that are decoded at all (base64 needs 4/3 of the bytes, plus line breaks).</summary>
    private const int MaxEncodedChars = MaxInlineBytes / 3 * 4 + MaxInlineBytes / 8;

    /// <summary>Most files an inline draft may have (the asset repository's limit for a source zip).</summary>
    public const int MaxInlineFiles = 2_000;

    /// <summary>
    ///     Message number of the refusal of an inline draft over <see cref="MaxInlineBytes" /> or
    ///     <see cref="MaxInlineFiles" /> (numbered after the work item, like the asset repository's 8492).
    /// </summary>
    public const int InlineDraftTooLargeNumber = 9005;

    /// <summary>Stable error code of that refusal.</summary>
    public const string InlineDraftTooLargeCode = "InlineDraftTooLarge";

    /// <summary>Result of <see cref="Package" />: the temporary zip, or why the draft was refused.</summary>
    /// <param name="FilePath">Path of the temporary zip; the caller deletes it.</param>
    /// <param name="Error">Argument error; no file was written.</param>
    /// <param name="TooLarge">Refusal text when the draft is over the limit; no file was written.</param>
    public sealed record Result(string? FilePath, string? Error, string? TooLarge);

    /// <summary>
    ///     Writes the inline draft (exactly one of <paramref name="files" /> and <paramref name="zipBase64" />) into a
    ///     temporary zip in <paramref name="tempDirectory" />.
    /// </summary>
    public static Result Package(IReadOnlyList<CkDraftFileDto>? files, string? zipBase64, string tempDirectory)
    {
        return files is { Count: > 0 } ? PackageFiles(files, tempDirectory) : PackageZip(zipBase64!, tempDirectory);
    }

    private static Result PackageFiles(IReadOnlyList<CkDraftFileDto> files, string tempDirectory)
    {
        if (files.Count > MaxInlineFiles)
        {
            return TooLarge($"The inline draft has {files.Count} files; at most {MaxInlineFiles} are allowed inline.");
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
                return TooLarge($"The inline draft has more than {MaxInlineBytes} bytes of file content.");
            }
        }

        var path = NewTempPath(tempDirectory);
        try
        {
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
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

    private static Result PackageZip(string zipBase64, string tempDirectory)
    {
        // A first check on the encoded length, before decoding, so an oversized payload is never decoded into memory.
        // It leaves room for line breaks in the base64 text; the exact limit is checked on the decoded bytes below.
        var trimmed = zipBase64.Trim();
        if (trimmed.Length > MaxEncodedChars)
        {
            return TooLarge($"The inline zip has more than {MaxInlineBytes} bytes.");
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(trimmed);
        }
        catch (FormatException)
        {
            return new Result(null, "zipBase64 is not valid base64.", null);
        }

        if (bytes.Length > MaxInlineBytes)
        {
            return TooLarge($"The inline zip has {bytes.Length} bytes.");
        }

        var path = NewTempPath(tempDirectory);
        try
        {
            File.WriteAllBytes(path, bytes);
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

    private static Result TooLarge(string reason) =>
        new(null, null,
            $"{reason} Inline drafts are limited to {MaxInlineBytes} bytes and {MaxInlineFiles} files " +
            $"(message {InlineDraftTooLargeNumber}). Upload a larger draft with prepare_file_upload, PUT the zip to the " +
            "returned URL and call validate_ck_model with the transferId.");

    private static string NewTempPath(string tempDirectory) =>
        Path.Combine(tempDirectory, $"octo-mcp-ck-inline-{Guid.NewGuid():N}.zip");
}
