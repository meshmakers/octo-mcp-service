using System.ComponentModel;
using Meshmakers.Octo.Backend.McpServices.Models;
using Meshmakers.Octo.Backend.McpServices.Services;
using Meshmakers.Octo.Sdk.ServiceClient.AssetRepositoryServices.Files;
using ModelContextProtocol.Server;

// ReSharper disable UnusedMember.Global

namespace Meshmakers.Octo.Backend.McpServices.Tools;

/// <summary>
///     Tools for the platform file system of a tenant (System.Files, AB#6171). Bytes move over the MCP
///     file-transfer channel (<c>prepare_file_upload</c> / <c>/file-transfer/download/{id}</c>), never through
///     tool parameters. Mirrors octo-cli GetFiles, UploadFile, DownloadFile. Files are addressed by the
///     well-known name of a folder root (default <c>Files</c>) plus a '/'-separated path.
/// </summary>
[McpServerToolType]
public sealed class PlatformFileTools
{
    /// <summary>Lists the folder roots or the children of a root or folder.</summary>
    [McpServerTool(Name = "list_files")]
    [Description(
        "List the platform file system (System.Files) of a tenant. Without 'root' it lists the folder roots " +
        "(the default root is 'Files'). With 'root' (and optional 'path' of a folder below it) it lists the " +
        "direct children, folders and files sorted by name, with rtId, size and content type. Paged: pass " +
        "the returned endCursor as 'after'. Equivalent to octo-cli GetFiles.")]
    public static async Task<ListFilesResponse> ListFiles(
        McpServer server,
        [Description("Well-known name of the folder root, e.g. 'Files'. Omit to list the roots.")] string? root = null,
        [Description("Folder path below the root ('/'-separated). Omit for the root itself.")] string? path = null,
        [Description("Page size (1-500, default 100).")] int first = 100,
        [Description("Cursor from a previous page (endCursor).")] string? after = null,
        [Description("Tenant to operate on. Falls back to URL route.")] string? tenantId = null)
    {
        if (first is < 1 or > 500)
        {
            return new ListFilesResponse { IsSuccess = false, ErrorMessage = "first must be between 1 and 500." };
        }

        var ctx = await AssetClientContext.TryBuildAsync(server, tenantId);
        if (ctx.Error != null)
        {
            return new ListFilesResponse { IsSuccess = false, ErrorMessage = ctx.Error };
        }

        try
        {
            var files = ctx.Client!.Files;
            var page = string.IsNullOrWhiteSpace(root)
                ? await files.ListRootsAsync(first, after)
                : await files.ListChildrenAsync(root, NormalizePath(path), first, after);

            return new ListFilesResponse
            {
                IsSuccess = true,
                TenantId = ctx.TenantId,
                Items = page.Items,
                TotalCount = page.TotalCount,
                EndCursor = page.EndCursor,
                HasNextPage = page.HasNextPage,
                Message = $"{page.Items.Count} of {page.TotalCount} entries."
            };
        }
        catch (Exception ex)
        {
            return new ListFilesResponse { IsSuccess = false, ErrorMessage = ex.Message, ErrorCode = CodeOf(ex) };
        }
    }

    /// <summary>Uploads a previously transferred file into the platform file system.</summary>
    [McpServerTool(Name = "upload_file")]
    [McpRisk(McpRiskLevel.Medium)]
    [Description(
        "Store an uploaded file in the platform file system (System.Files) of a tenant. Call " +
        "prepare_file_upload first, PUT the bytes to the returned URL, then call this tool with the " +
        "transferId. 'path' is the target file path below the root including the file name. conflict: " +
        "'fail' (default, NAME_CONFLICT when the name exists), 'replace' (new content for the existing file, " +
        "rtId and links stay) or 'keepBoth' (next free name). createFolders=true creates missing folders. " +
        "Equivalent to octo-cli UploadFile.")]
    public static async Task<FileEntryResponse> UploadFile(
        McpServer server,
        [Description("Transfer id from prepare_file_upload.")] string transferId,
        [Description("Well-known name of the folder root, e.g. 'Files'.")] string root,
        [Description("Target path below the root, ending with the file name, e.g. 'reports/2026/a.pdf'.")] string path,
        [Description("What to do when the name exists: 'fail' (default), 'replace' or 'keepBoth'.")] string conflict = "fail",
        [Description("Create missing folders on the way (default false).")] bool createFolders = false,
        [Description("Content type; omit to let the server derive it from the file name.")] string? contentType = null,
        [Description("Tenant to operate on. Falls back to URL route.")] string? tenantId = null)
    {
        if (string.IsNullOrWhiteSpace(transferId) || string.IsNullOrWhiteSpace(root) ||
            string.IsNullOrWhiteSpace(NormalizePath(path)))
        {
            return new FileEntryResponse
            {
                IsSuccess = false,
                ErrorMessage = "transferId, root and path (ending with the file name) are required."
            };
        }

        if (!TryParseConflict(conflict, out var mode))
        {
            return new FileEntryResponse
            {
                IsSuccess = false,
                ErrorMessage = $"Unknown conflict mode '{conflict}'. Use 'fail', 'replace' or 'keepBoth'."
            };
        }

        var store = server.Services!.GetRequiredService<IFileTransferStore>();
        var upload = store.GetUpload(transferId);
        if (upload == null)
        {
            return new FileEntryResponse
            {
                IsSuccess = false,
                ErrorMessage = $"Upload '{transferId}' not found or expired."
            };
        }

        var ctx = await AssetClientContext.TryBuildAsync(server, tenantId);
        if (ctx.Error != null)
        {
            return new FileEntryResponse { IsSuccess = false, ErrorMessage = ctx.Error };
        }

        try
        {
            FileEntryDto entry;
            await using (var stream = File.OpenRead(upload.FilePath))
            {
                entry = await ctx.Client!.Files.UploadAsync(root, NormalizePath(path)!, stream, contentType, mode,
                    createFolders);
            }

            store.DeleteUpload(transferId);
            return new FileEntryResponse
            {
                IsSuccess = true,
                TenantId = ctx.TenantId,
                Entry = entry,
                Message = entry.Replaced
                    ? $"Replaced content of '{entry.Path}' (rtId {entry.RtId})."
                    : $"Stored '{entry.Path}' (rtId {entry.RtId})."
            };
        }
        catch (Exception ex)
        {
            return new FileEntryResponse { IsSuccess = false, ErrorMessage = ex.Message, ErrorCode = CodeOf(ex) };
        }
    }

    /// <summary>Makes a file of the platform file system available through the download channel.</summary>
    [McpServerTool(Name = "download_file")]
    [Description(
        "Fetch a file of the platform file system (System.Files) of a tenant. Address it either by 'root' + " +
        "'path' or by 'rtId'. The bytes are staged on the MCP server; the result carries a downloadUrlPath " +
        "to GET (valid ~30 minutes) — they never travel inside the tool result. Equivalent to octo-cli " +
        "DownloadFile.")]
    public static async Task<DownloadPlatformFileResponse> DownloadFile(
        McpServer server,
        [Description("Well-known name of the folder root, e.g. 'Files' (with 'path').")] string? root = null,
        [Description("Path of the file below the root (with 'root').")] string? path = null,
        [Description("Runtime id of the file (alternative to root + path).")] string? rtId = null,
        [Description("Tenant to operate on. Falls back to URL route.")] string? tenantId = null)
    {
        var byId = !string.IsNullOrWhiteSpace(rtId);
        var byPath = !string.IsNullOrWhiteSpace(root) && !string.IsNullOrWhiteSpace(NormalizePath(path));
        if (byId == byPath)
        {
            return new DownloadPlatformFileResponse
            {
                IsSuccess = false,
                ErrorMessage = "Provide either rtId or root + path (not both, not neither)."
            };
        }

        var ctx = await AssetClientContext.TryBuildAsync(server, tenantId);
        if (ctx.Error != null)
        {
            return new DownloadPlatformFileResponse { IsSuccess = false, ErrorMessage = ctx.Error };
        }

        string? tempPath = null;
        try
        {
            var files = ctx.Client!.Files;
            await using var download = byId
                ? await files.DownloadByIdAsync(rtId!)
                : await files.DownloadAsync(root!, NormalizePath(path)!);

            var fileName = SafeFileName(download.FileName ?? (byPath ? Path.GetFileName(NormalizePath(path)!) : rtId!));
            tempPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}_{fileName}");
            await using (var target = File.Create(tempPath))
            {
                await download.Content.CopyToAsync(target);
            }

            var store = server.Services!.GetRequiredService<IFileTransferStore>();
            var transferId = store.RegisterDownload(McpSessionContext.GetCallerLabel(server), tempPath, fileName);
            tempPath = null; // owned by the store now
            var size = store.GetDownload(transferId)?.SizeBytes ?? download.ContentLength ?? 0;

            return new DownloadPlatformFileResponse
            {
                IsSuccess = true,
                TenantId = ctx.TenantId,
                TransferId = transferId,
                DownloadUrlPath = $"/file-transfer/download/{transferId}",
                FileName = fileName,
                ContentType = download.ContentType,
                SizeBytes = size,
                ExpiresAtUtc = DateTime.UtcNow.AddMinutes(30),
                Message = $"Ready. GET '/file-transfer/download/{transferId}' to fetch ({size:N0} bytes)."
            };
        }
        catch (Exception ex)
        {
            return new DownloadPlatformFileResponse
            {
                IsSuccess = false,
                ErrorMessage = ex.Message,
                ErrorCode = CodeOf(ex)
            };
        }
        finally
        {
            if (tempPath != null && File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    /// <summary>Creates a folder in the platform file system.</summary>
    [McpServerTool(Name = "create_folder")]
    [Description(
        "Create a folder in the platform file system (System.Files) of a tenant, directly in a root or below " +
        "a folder path. Fails with NAME_CONFLICT when the name exists in the parent, INVALID_NAME for names " +
        "with '/', '\\\\' or control characters.")]
    public static async Task<FileEntryResponse> CreateFolder(
        McpServer server,
        [Description("Well-known name of the folder root, e.g. 'Files'.")] string root,
        [Description("Name of the new folder.")] string name,
        [Description("Path of the parent folder below the root. Omit to create directly in the root.")] string? parentPath = null,
        [Description("Tenant to operate on. Falls back to URL route.")] string? tenantId = null)
    {
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(name))
        {
            return new FileEntryResponse { IsSuccess = false, ErrorMessage = "root and name are required." };
        }

        var ctx = await AssetClientContext.TryBuildAsync(server, tenantId);
        if (ctx.Error != null)
        {
            return new FileEntryResponse { IsSuccess = false, ErrorMessage = ctx.Error };
        }

        try
        {
            var entry = await ctx.Client!.Files.CreateFolderAsync(root, NormalizePath(parentPath), name);
            return new FileEntryResponse
            {
                IsSuccess = true,
                TenantId = ctx.TenantId,
                Entry = entry,
                Message = $"Created folder '{entry.Name}' (rtId {entry.RtId})."
            };
        }
        catch (Exception ex)
        {
            return new FileEntryResponse { IsSuccess = false, ErrorMessage = ex.Message, ErrorCode = CodeOf(ex) };
        }
    }

    /// <summary>Deletes a file, folder (recursively) or root.</summary>
    [McpServerTool(Name = "delete_file")]
    [McpRisk(McpRiskLevel.High)]
    [Description(
        "DESTRUCTIVE. Delete a file or folder of the platform file system (System.Files). Address the entry " +
        "by 'rtId' or by 'root' + 'path'. A folder is deleted RECURSIVELY with everything below it, all or " +
        "nothing; above the server's delete cap (default 2,000 entries) it is refused with LIMIT_EXCEEDED. " +
        "Roots of services and blueprints are protected (PROTECTED_ROOT). Requires confirm=true; without it " +
        "the call refuses and reports what would be deleted (counts, linked files).")]
    public static async Task<PlatformFileResponse> DeleteFile(
        McpServer server,
        [Description("Runtime id of the entry (alternative to root + path).")] string? rtId = null,
        [Description("Well-known name of the folder root (with 'path').")] string? root = null,
        [Description("Path of the file or folder below the root (with 'root').")] string? path = null,
        [Description("Must be true to actually delete.")] bool confirm = false,
        [Description("Tenant to operate on. Falls back to URL route.")] string? tenantId = null)
    {
        var byId = !string.IsNullOrWhiteSpace(rtId);
        var byPath = !string.IsNullOrWhiteSpace(root) && !string.IsNullOrWhiteSpace(NormalizePath(path));
        if (byId == byPath)
        {
            return new PlatformFileResponse
            {
                IsSuccess = false,
                ErrorMessage = "Provide either rtId or root + path (not both, not neither). Roots cannot be deleted by path."
            };
        }

        var ctx = await AssetClientContext.TryBuildAsync(server, tenantId);
        if (ctx.Error != null)
        {
            return new PlatformFileResponse { IsSuccess = false, ErrorMessage = ctx.Error };
        }

        try
        {
            var files = ctx.Client!.Files;
            var target = byId ? rtId! : (await files.GetEntryAsync(root!, NormalizePath(path))).RtId;
            var label = byId ? $"rtId '{rtId}'" : $"'{root}/{NormalizePath(path)}'";

            if (!confirm)
            {
                var impact = await files.GetDeleteImpactAsync(target);
                return new PlatformFileResponse
                {
                    IsSuccess = false,
                    TenantId = ctx.TenantId,
                    ErrorMessage = $"Refusing to delete {label} without confirm=true. {Describe(impact)}"
                };
            }

            await files.DeleteAsync(target);
            return new PlatformFileResponse
            {
                IsSuccess = true,
                TenantId = ctx.TenantId,
                Message = $"Deleted {label} (rtId {target})."
            };
        }
        catch (Exception ex)
        {
            return new PlatformFileResponse { IsSuccess = false, ErrorMessage = ex.Message, ErrorCode = CodeOf(ex) };
        }
    }

    private static string Describe(DeleteImpactDto impact)
    {
        var e = impact.Entry;
        if (impact.Stats is { } s)
        {
            return $"'{e.Name}' ({e.Kind}) would take {s.Folders} folders, {s.Files} files, {s.Bytes:N0} bytes " +
                   $"with it ({s.LinkedFiles} linked files, {s.HiddenEntries} hidden entries).";
        }

        return $"'{e.Name}' ({e.Kind}, {e.Size?.ToString("N0") ?? "?"} bytes) has {impact.LinkedCount ?? 0} links.";
    }

    private static string? NormalizePath(string? path)
    {
        var p = path?.Trim().Trim('/');
        return string.IsNullOrEmpty(p) ? null : p;
    }

    private static string? CodeOf(Exception ex) => (ex as FilesApiException)?.Code;

    private static string SafeFileName(string name)
    {
        var clean = Path.GetFileName(name);
        foreach (var c in Path.GetInvalidFileNameChars())
        {
            clean = clean.Replace(c, '_');
        }

        return string.IsNullOrWhiteSpace(clean) ? "download.bin" : clean;
    }

    private static bool TryParseConflict(string? value, out FileConflictMode mode)
    {
        switch ((value ?? "fail").Trim().ToLowerInvariant())
        {
            case "fail": mode = FileConflictMode.Fail; return true;
            case "replace": mode = FileConflictMode.Replace; return true;
            case "keepboth": mode = FileConflictMode.KeepBoth; return true;
            default: mode = FileConflictMode.Fail; return false;
        }
    }
}
