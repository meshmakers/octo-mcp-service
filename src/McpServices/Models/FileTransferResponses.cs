namespace Meshmakers.Octo.Backend.McpServices.Models;

/// <summary>Common envelope for file-IO tool responses.</summary>
public class FileTransferResponse
{
    /// <summary>True when the underlying service call succeeded.</summary>
    public bool IsSuccess { get; set; }

    /// <summary>Error message when <see cref="IsSuccess"/> is false.</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>Optional human-readable status message.</summary>
    public string? Message { get; set; }

    /// <summary>Tenant the operation was executed against.</summary>
    public string? TenantId { get; set; }
}

/// <summary>Response of prepare_file_upload.</summary>
public class PrepareFileUploadResponse : FileTransferResponse
{
    /// <summary>Opaque transfer id. Pass back to import_* tools after the upload completes.</summary>
    public string? TransferId { get; set; }

    /// <summary>Relative URL the client must PUT the file body to (against the MCP-server's public URL).</summary>
    public string? UploadUrlPath { get; set; }

    /// <summary>UTC time when the reservation expires.</summary>
    public DateTime ExpiresAtUtc { get; set; }
}

/// <summary>Response of file-producing tools (downloads).</summary>
public class FileDownloadResponse : FileTransferResponse
{
    /// <summary>Opaque transfer id. Use it in the GET /file-transfer/download/{id} URL.</summary>
    public string? TransferId { get; set; }

    /// <summary>Relative URL the client must GET to fetch the bytes.</summary>
    public string? DownloadUrlPath { get; set; }

    /// <summary>File name the controller will serve as Content-Disposition.</summary>
    public string? FileName { get; set; }

    /// <summary>Size in bytes.</summary>
    public long SizeBytes { get; set; }

    /// <summary>UTC time when the download expires.</summary>
    public DateTime ExpiresAtUtc { get; set; }
}

/// <summary>Response for tools that start an async job (Import / FixupRun / Dump).</summary>
public class JobStartedResponse : FileTransferResponse
{
    /// <summary>Job id (poll asset/bot service to track progress).</summary>
    public string? JobId { get; set; }
}
