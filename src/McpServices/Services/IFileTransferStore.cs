namespace Meshmakers.Octo.Backend.McpServices.Services;

/// <summary>
///     Represents a buffered upload — its bytes-on-disk path and the MCP session that owns it.
/// </summary>
public sealed record FileTransferUpload(
    string TransferId,
    string SessionId,
    string FilePath,
    string FileName,
    long SizeBytes,
    DateTime ExpiresAtUtc);

/// <summary>
///     Represents a buffered download — bytes ready for the client to fetch.
/// </summary>
public sealed record FileTransferDownload(
    string TransferId,
    string SessionId,
    string FilePath,
    string FileName,
    long SizeBytes,
    DateTime ExpiresAtUtc);

/// <summary>
///     Short-lived storage for files moving between MCP clients and the SDK. Uploads land here from the HTTP
///     PUT endpoint; tools read them, materialise to a temp path the SDK can consume. For downloads, tools
///     write into the store and hand the caller a GET URL.
///
///     Files are persisted to disk (not RAM) so dumps and large model exports work. Entries expire after a
///     configurable TTL; a background sweeper deletes expired files.
/// </summary>
public interface IFileTransferStore
{
    /// <summary>Reserve an upload slot. Returns the transfer id (used in the upload URL) and the disk path
    /// the HTTP endpoint will write to.</summary>
    /// <param name="sessionId">MCP session id that will commit the upload.</param>
    /// <param name="fileName">Logical file name (for downstream SDK calls + extension detection).</param>
    /// <param name="ttl">Optional override for how long the slot stays valid.</param>
    /// <returns>The transfer id and the absolute target path the controller writes to.</returns>
    (string TransferId, string TargetPath) ReserveUpload(string sessionId, string fileName, TimeSpan? ttl = null);

    /// <summary>Mark an upload as complete with the bytes written so far.</summary>
    void CompleteUpload(string transferId, long sizeBytes);

    /// <summary>Returns the upload entry — or null if expired / not found / not finished.</summary>
    FileTransferUpload? GetUpload(string transferId);

    /// <summary>Removes the upload entry and deletes its file.</summary>
    void DeleteUpload(string transferId);

    /// <summary>Register a download. The caller has already written <paramref name="filePath"/> to disk.
    /// The store takes ownership of the file (it gets deleted on TTL or DeleteDownload).</summary>
    string RegisterDownload(string sessionId, string filePath, string fileName, TimeSpan? ttl = null);

    /// <summary>Returns the download entry — or null if expired / not found.</summary>
    FileTransferDownload? GetDownload(string transferId);

    /// <summary>Removes the download entry and deletes its file.</summary>
    void DeleteDownload(string transferId);
}
