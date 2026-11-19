namespace Meshmakers.Octo.Backend.McpServices.Models;

/// <summary>Common envelope for file-IO tool responses.</summary>
public class FileTransferResponse
{
    /// <summary>True when the underlying service call succeeded.</summary>
    public bool IsSuccess { get; set; }

    /// <summary>Error message when <see cref="IsSuccess"/> is false.</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    ///     Stable error code of the service answer when <see cref="IsSuccess"/> is false, e.g.
    ///     <c>DataModelManagementRequired</c> (AB#8529: the caller lacks the tenant role <c>DataModelManagement</c>).
    /// </summary>
    public string? ErrorCode { get; set; }

    /// <summary>Optional human-readable status message.</summary>
    public string? Message { get; set; }

    /// <summary>Tenant the operation was executed against.</summary>
    public string? TenantId { get; set; }
}

/// <summary>Response of prepare_file_upload.</summary>
public class PrepareFileUploadResponse : FileTransferResponse
{
    /// <summary>
    ///     Opaque transfer id, a secret for 30 minutes. Send it in the <see cref="TransferIdHeader" /> header of the
    ///     PUT and pass it back to the import_* tools after the upload completes.
    /// </summary>
    public string? TransferId { get; set; }

    /// <summary>
    ///     Relative URL the client must PUT the file body to (against the MCP-server's public URL). It carries no id:
    ///     the id goes into the <see cref="TransferIdHeader" /> header.
    /// </summary>
    public string? UploadUrlPath { get; set; }

    /// <summary>
    ///     Name of the request header that carries <see cref="TransferId" /> (<c>X-Transfer-Id</c>). The id is never
    ///     part of the URL path; send it in this header together with the transfer key or the bearer token of the MCP
    ///     connection.
    /// </summary>
    public string? TransferIdHeader { get; set; }

    /// <summary>
    ///     One-time transfer key: send it in the <see cref="TransferKeyHeader" /> header instead of a bearer
    ///     token. Works once, for this transfer and direction only, until <see cref="TransferKeyExpiresAtUtc" />. A secret:
    ///     never put it into a URL or a log.
    /// </summary>
    public string? TransferKey { get; set; }

    /// <summary>Name of the request header that carries <see cref="TransferKey" /> (<c>X-Transfer-Key</c>).</summary>
    public string? TransferKeyHeader { get; set; }

    /// <summary>UTC time when <see cref="TransferKey" /> stops working (default 5 minutes after issue).</summary>
    public DateTime? TransferKeyExpiresAtUtc { get; set; }

    /// <summary>UTC time when the reservation expires.</summary>
    public DateTime ExpiresAtUtc { get; set; }
}

/// <summary>Response of file-producing tools (downloads).</summary>
public class FileDownloadResponse : FileTransferResponse
{
    /// <summary>Opaque transfer id, a secret for 30 minutes. Send it in the <see cref="TransferIdHeader" /> header of the GET.</summary>
    public string? TransferId { get; set; }

    /// <summary>Relative URL the client must GET to fetch the bytes. It carries no id.</summary>
    public string? DownloadUrlPath { get; set; }

    /// <summary>
    ///     Name of the request header that carries <see cref="TransferId" /> (<c>X-Transfer-Id</c>). The id is never
    ///     part of the URL path; send it in this header together with the transfer key or the bearer token of the MCP
    ///     connection.
    /// </summary>
    public string? TransferIdHeader { get; set; }

    /// <summary>
    ///     One-time transfer key: send it in the <see cref="TransferKeyHeader" /> header instead of a bearer
    ///     token. Works once, for this transfer and direction only, until <see cref="TransferKeyExpiresAtUtc" />. A secret:
    ///     never put it into a URL or a log.
    /// </summary>
    public string? TransferKey { get; set; }

    /// <summary>Name of the request header that carries <see cref="TransferKey" /> (<c>X-Transfer-Key</c>).</summary>
    public string? TransferKeyHeader { get; set; }

    /// <summary>UTC time when <see cref="TransferKey" /> stops working (default 5 minutes after issue).</summary>
    public DateTime? TransferKeyExpiresAtUtc { get; set; }

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
