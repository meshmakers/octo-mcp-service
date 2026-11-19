namespace Meshmakers.Octo.Backend.McpServices.Services;

/// <summary>
///     Who created a transfer. The tools compare <see cref="SessionLabel" /> (the MCP session binding); the HTTP
///     endpoints compare <see cref="Principal" />, because a plain PUT or GET carries no <c>Mcp-Session-Id</c>.
/// </summary>
/// <param name="SessionLabel">
///     <c>McpSessionContext.GetCallerLabel(server)</c>: the caller's store key plus <c>|&lt;Mcp-Session-Id&gt;</c>
///     when the client sent that header, or the shared anonymous label for an unidentifiable caller.
/// </param>
/// <param name="Principal">
///     <see cref="McpCallerPrincipal.StoreKeyPrefix" /> of the validated caller (<c>u:&lt;tenant&gt;:&lt;sub&gt;</c> or
///     <c>c:&lt;client_id&gt;</c>), or null when the caller cannot be identified. A null principal matches nobody.
/// </param>
public sealed record FileTransferOwner(string SessionLabel, string? Principal);

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
    DateTime ExpiresAtUtc)
{
    /// <summary>Principal that created the download; the GET endpoint serves it to this principal only.</summary>
    public string? OwnerPrincipal { get; init; }
}

/// <summary>Outcome of <see cref="IFileTransferStore.TryClaimUpload" />.</summary>
public enum UploadClaimStatus
{
    /// <summary>Unknown, expired, already used or owned by another principal — answered alike (404).</summary>
    NotFound,

    /// <summary>The owner's slot is already being written by another PUT (409).</summary>
    InProgress,

    /// <summary>The caller holds the slot now and may write to the target path.</summary>
    Claimed
}

/// <summary>
///     Short-lived storage for files moving between MCP clients and the SDK. Uploads land here from the HTTP
///     PUT endpoint; tools read them, materialise to a temp path the SDK can consume. For downloads, tools
///     write into the store and hand the caller a GET path plus the transfer id for the request header.
///
///     Files are persisted to disk (not RAM) so dumps and large model exports work. Entries expire after a
///     configurable TTL; a background sweeper deletes expired files.
/// </summary>
public interface IFileTransferStore
{
    /// <summary>Reserve an upload slot. Returns the transfer id and the disk path the HTTP endpoint will write to.</summary>
    /// <param name="owner">The caller that reserves the slot (session label and principal).</param>
    /// <param name="fileName">Logical file name (for downstream SDK calls + extension detection).</param>
    /// <param name="ttl">Optional override for how long the slot stays valid.</param>
    /// <returns>The transfer id and the absolute target path the controller writes to.</returns>
    (string TransferId, string TargetPath) ReserveUpload(FileTransferOwner owner, string fileName, TimeSpan? ttl = null);

    /// <summary>
    ///     Atomically claims the reserved slot for one PUT. Only the principal that reserved the slot can claim it;
    ///     any other principal, an expired, unknown or already used id gets <see cref="UploadClaimStatus.NotFound" />.
    ///     A second claim while the first PUT still runs gets <see cref="UploadClaimStatus.InProgress" />.
    /// </summary>
    /// <param name="transferId">Transfer id from the request header.</param>
    /// <param name="callerPrincipal"><see cref="McpCallerPrincipal.StoreKeyPrefix" /> of the HTTP caller.</param>
    /// <returns>The claim status and, when claimed, the target path to write to.</returns>
    (UploadClaimStatus Status, string? TargetPath) TryClaimUpload(string transferId, string? callerPrincipal);

    /// <summary>Commits a claimed upload with the bytes written. False when the slot is gone (cancelled or expired
    /// meanwhile); the caller then deletes what it wrote.</summary>
    bool CompleteUpload(string transferId, long sizeBytes);

    /// <summary>Ends a claimed upload whose PUT failed or was aborted: the reservation and the partial file are
    /// removed, a new <c>prepare_file_upload</c> is needed.</summary>
    void AbortUpload(string transferId);

    /// <summary>Returns the upload entry — or null if expired / not found / not finished / owned by another
    /// session (a foreign caller is answered exactly like an unknown id).</summary>
    /// <param name="transferId">Transfer id from <c>prepare_file_upload</c>.</param>
    /// <param name="callerSessionId">The calling session, <c>McpSessionContext.GetCallerLabel(server)</c>.</param>
    FileTransferUpload? GetUpload(string transferId, string callerSessionId);

    /// <summary>Cancel an upload reservation / finished upload / download, but only when it belongs to
    /// <paramref name="callerSessionId" />; a foreign or unknown id is a silent no-op.</summary>
    void CancelTransfer(string transferId, string callerSessionId);

    /// <summary>Removes the upload entry and deletes its file.</summary>
    void DeleteUpload(string transferId);

    /// <summary>Register a download. The caller has already written <paramref name="filePath"/> to disk.
    /// The store takes ownership of the file (it gets deleted on TTL or DeleteDownload).</summary>
    /// <param name="owner">The caller the download belongs to (session label and principal).</param>
    /// <param name="filePath">File to serve.</param>
    /// <param name="fileName">Name for the Content-Disposition header.</param>
    /// <param name="ttl">Optional override for how long the download stays valid.</param>
    string RegisterDownload(FileTransferOwner owner, string filePath, string fileName, TimeSpan? ttl = null);

    /// <summary>Returns the download entry — or null if expired, not found or created by another principal (all
    /// answered alike). The entry is single-use: the controller deletes it once the file was served completely.</summary>
    /// <param name="transferId">Transfer id from the request header.</param>
    /// <param name="callerPrincipal"><see cref="McpCallerPrincipal.StoreKeyPrefix" /> of the HTTP caller.</param>
    FileTransferDownload? GetDownload(string transferId, string? callerPrincipal);

    /// <summary>Removes the download entry and deletes its file.</summary>
    void DeleteDownload(string transferId);
}
