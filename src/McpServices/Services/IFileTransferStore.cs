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
///     <see cref="McpCallerPrincipal.TransferOwnerKey" /> of the validated caller (<c>u:&lt;tenant&gt;:&lt;sub&gt;</c> or
///     <c>c:&lt;client_id&gt;:&lt;tenant&gt;</c>), or null when the caller cannot be identified (also a client-credentials
///     token without <c>tenant_id</c>). A null principal matches nobody.
/// </param>
/// <param name="TenantId">The caller's <c>tenant_id</c> claim; a transfer key is bound to it.</param>
public sealed record FileTransferOwner(string SessionLabel, string? Principal, string? TenantId = null)
{
    /// <summary>
    ///     True when this caller may own a file transfer: it has a principal and a tenant. A client-credentials token
    ///     without <c>tenant_id</c> has neither a principal nor a tenant here and is refused (fail closed).
    /// </summary>
    public bool CanOwnTransfer => !string.IsNullOrEmpty(Principal)
                                  && Principal != McpSessionContext.AnonymousCallerLabel
                                  && !string.IsNullOrEmpty(TenantId);
}

/// <summary>Direction of a file transfer; a transfer key is valid for one direction only.</summary>
public enum FileTransferDirection
{
    /// <summary>The client PUTs a file to <c>/file-transfer/upload</c>.</summary>
    Upload,

    /// <summary>The client GETs a file from <c>/file-transfer/download</c>.</summary>
    Download
}

/// <summary>
///     A freshly issued one-time transfer key. <see cref="Key" /> is the only copy of the clear value: the store keeps
///     just its SHA-256 hash. Return it to the creating client and nowhere else (no log, span, URL or error body).
/// </summary>
/// <param name="Key">Opaque, high-entropy key (256 random bits, base64url) for the <c>X-Transfer-Key</c> header.</param>
/// <param name="ExpiresAtUtc">When the key stops working; never later than the transfer itself.</param>
public sealed record IssuedTransferKey(string Key, DateTime ExpiresAtUtc);

/// <summary>
///     Who presents a transfer key over HTTP. A key needs no bearer token; but when the request does carry a valid
///     token, its principal and tenant must be the key's, so a key never works for another identity.
/// </summary>
/// <param name="IsAuthenticated">True when the request carries a valid bearer token.</param>
/// <param name="Principal"><see cref="McpCallerPrincipal.TransferOwnerKey" /> of that token, or null.</param>
/// <param name="TenantId">The token's <c>tenant_id</c> claim, or null.</param>
public sealed record TransferKeyCaller(bool IsAuthenticated, string? Principal, string? TenantId)
{
    /// <summary>A request without a (valid) bearer token.</summary>
    public static TransferKeyCaller Anonymous { get; } = new(false, null, null);
}

/// <summary>
///     Represents a buffered upload — its bytes-on-disk path and the MCP session that owns it.
/// </summary>
public sealed record FileTransferUpload(
    string TransferId,
    string SessionId,
    string FilePath,
    string FileName,
    long SizeBytes,
    DateTime ExpiresAtUtc)
{
    /// <summary>Principal that reserved the upload; a consuming tool must be called by this principal too.</summary>
    public string? OwnerPrincipal { get; init; }
}

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
    /// <param name="callerPrincipal"><see cref="McpCallerPrincipal.TransferOwnerKey" /> of the HTTP caller.</param>
    /// <returns>The claim status and, when claimed, the target path to write to.</returns>
    (UploadClaimStatus Status, string? TargetPath) TryClaimUpload(string transferId, string? callerPrincipal);

    /// <summary>Commits a claimed upload with the bytes written. False when the slot is gone (cancelled or expired
    /// meanwhile); the caller then deletes what it wrote.</summary>
    bool CompleteUpload(string transferId, long sizeBytes);

    /// <summary>Ends a claimed upload whose PUT failed or was aborted: the reservation and the partial file are
    /// removed, a new <c>prepare_file_upload</c> is needed.</summary>
    void AbortUpload(string transferId);

    /// <summary>Returns the upload entry — or null if expired / not found / not finished / owned by another
    /// session or principal (a foreign caller is answered exactly like an unknown id).</summary>
    /// <param name="transferId">Transfer id from <c>prepare_file_upload</c>.</param>
    /// <param name="caller">The calling tool's owner tag, <c>McpSessionContext.GetFileTransferOwner(server)</c>: both
    /// its session label and its principal must match the ones stored at reservation.</param>
    FileTransferUpload? GetUpload(string transferId, FileTransferOwner caller);

    /// <summary>Cancel an upload reservation / finished upload / download, but only when it belongs to
    /// <paramref name="caller" /> (session label and principal); a foreign or unknown id is a silent no-op.</summary>
    void CancelTransfer(string transferId, FileTransferOwner caller);

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
    /// <param name="callerPrincipal"><see cref="McpCallerPrincipal.TransferOwnerKey" /> of the HTTP caller.</param>
    FileTransferDownload? GetDownload(string transferId, string? callerPrincipal);

    /// <summary>Removes the download entry and deletes its file.</summary>
    void DeleteDownload(string transferId);

    /// <summary>
    ///     Issues the one-time transfer key of a transfer: a client without the raw bearer token of its MCP
    ///     connection sends it in the <c>X-Transfer-Key</c> header instead. The key is bound to the transfer, its
    ///     direction, the owner's principal and tenant; it lives at most the configured key lifetime (and never longer
    ///     than the transfer), works once, and only its hash is stored. A new key replaces an earlier one.
    /// </summary>
    /// <param name="transferId">An upload reservation or a download of <paramref name="owner" />.</param>
    /// <param name="direction">The direction the key may be used for.</param>
    /// <param name="owner">The creating caller; must be the transfer's principal and carry a tenant.</param>
    /// <param name="ttl">Optional shorter lifetime (tests); capped at the configured key lifetime.</param>
    /// <returns>The key, or null when the transfer does not exist for this owner or the owner cannot own a transfer.</returns>
    IssuedTransferKey? IssueTransferKey(string transferId, FileTransferDirection direction, FileTransferOwner owner,
        TimeSpan? ttl = null);

    /// <summary>
    ///     Claims an upload slot with its transfer key instead of the owner's bearer token. The key check and the claim
    ///     are one atomic step: exactly one request can use a key. A wrong, expired, used, foreign or wrong-direction
    ///     key, or a <paramref name="caller" /> token that is not the key's owner, gets
    ///     <see cref="UploadClaimStatus.NotFound" /> and leaves the key as it was (an expired key is dropped).
    ///     <see cref="UploadClaimStatus.InProgress" /> leaves the key unused.
    /// </summary>
    (UploadClaimStatus Status, string? TargetPath) TryClaimUploadWithKey(string transferId, string transferKey,
        TransferKeyCaller caller);

    /// <summary>
    ///     Returns a download for its transfer key and spends the key in the same atomic step; null for a wrong,
    ///     expired, used, foreign or wrong-direction key, or a <paramref name="caller" /> token that is not the key's
    ///     owner. The download itself stays single-use as with a bearer token.
    /// </summary>
    FileTransferDownload? ConsumeDownloadKey(string transferId, string transferKey, TransferKeyCaller caller);
}
