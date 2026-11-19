namespace Meshmakers.Octo.Backend.McpServices.Routing;

/// <summary>
///     Paths and header of the file-transfer endpoints. The transfer id travels only in the
///     <see cref="TransferIdHeader" /> request header, never in the path or the query, so it does not reach access
///     logs, the <c>url.*</c> / <c>http.route</c> span attributes or proxy logs.
/// </summary>
public static class FileTransferRoutes
{
    /// <summary>Path a client PUTs the body of a reserved upload to.</summary>
    public const string UploadPath = "/file-transfer/upload";

    /// <summary>Path a client GETs a prepared download from.</summary>
    public const string DownloadPath = "/file-transfer/download";

    /// <summary>Request header that carries the transfer id.</summary>
    public const string TransferIdHeader = "X-Transfer-Id";

    /// <summary>How to fetch a download, for the <c>Message</c> of a download-producing tool.</summary>
    /// <param name="transferId">The download's transfer id.</param>
    public static string DownloadInstruction(string transferId) =>
        $"GET '{DownloadPath}' with the headers '{TransferIdHeader}: {transferId}' and " +
        "'Authorization: Bearer <the access token of this MCP connection>'";

    /// <summary>How to fill an upload slot, for the <c>Message</c> of <c>prepare_file_upload</c>.</summary>
    /// <param name="transferId">The upload's transfer id.</param>
    public static string UploadInstruction(string transferId) =>
        $"PUT the file body to '{UploadPath}' with the headers '{TransferIdHeader}: {transferId}', " +
        "'Authorization: Bearer <the access token of this MCP connection>' and a 'Content-Type' " +
        "(e.g. application/octet-stream)";
}
