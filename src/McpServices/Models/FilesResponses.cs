using Meshmakers.Octo.Sdk.ServiceClient.AssetRepositoryServices.Files;

namespace Meshmakers.Octo.Backend.McpServices.Models;

/// <summary>Response of the platform file system tools (System.Files).</summary>
public class PlatformFileResponse : FileTransferResponse
{
    /// <summary>Stable file API error code (e.g. NAME_CONFLICT, PATH_NOT_FOUND) when the call failed.</summary>
    public string? ErrorCode { get; set; }
}

/// <summary>Response of list_files.</summary>
public class ListFilesResponse : PlatformFileResponse
{
    /// <summary>Entries of the requested page (roots when no root was given, otherwise children).</summary>
    public List<FileEntryDto> Items { get; set; } = [];

    /// <summary>Total number of entries of the listing.</summary>
    public long TotalCount { get; set; }

    /// <summary>Cursor to pass as <c>after</c> for the next page.</summary>
    public string? EndCursor { get; set; }

    /// <summary>True when another page follows.</summary>
    public bool HasNextPage { get; set; }
}

/// <summary>Response of upload_file and create_folder: the stored entry.</summary>
public class FileEntryResponse : PlatformFileResponse
{
    /// <summary>The stored file or folder.</summary>
    public FileEntryDto? Entry { get; set; }
}

/// <summary>Response of download_file: the entry plus the file-transfer download.</summary>
public class DownloadPlatformFileResponse : FileDownloadResponse
{
    /// <summary>Stable file API error code when the call failed.</summary>
    public string? ErrorCode { get; set; }

    /// <summary>Content type reported by the server.</summary>
    public string? ContentType { get; set; }
}
