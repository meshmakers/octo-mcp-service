using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Backend.McpServices.Services;

internal sealed class FileTransferStore : IFileTransferStore
{
    private readonly ConcurrentDictionary<string, FileTransferUpload> _uploads = new();
    private readonly ConcurrentDictionary<string, FileTransferDownload> _downloads = new();

    /// <summary>
    ///     Holds an upload before it's committed: owner, target path and expiry. <see cref="Claimed" /> is set by the
    ///     first PUT (compare-and-swap), so a second PUT on the same id cannot write to the same file.
    /// </summary>
    private sealed record PendingUpload(
        string SessionId,
        string? OwnerPrincipal,
        string TargetPath,
        string FileName,
        DateTime ExpiresAtUtc,
        bool Claimed);

    private readonly ConcurrentDictionary<string, PendingUpload> _pending = new();

    /// <summary>
    ///     The live one-time key of a transfer, keyed by transfer id. Only the SHA-256 hash of the key is held;
    ///     using the key removes the entry.
    /// </summary>
    private sealed record TransferKeyEntry(
        byte[] KeyHash,
        FileTransferDirection Direction,
        string OwnerPrincipal,
        string TenantId,
        DateTime ExpiresAtUtc);

    private readonly ConcurrentDictionary<string, TransferKeyEntry> _keys = new();

    // Serialises check-and-spend of keys, so two requests with the same key cannot both pass.
    private readonly Lock _keyLock = new();

    private readonly string _baseDir;
    private readonly TimeSpan _keyLifetime;
    private static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(30);

    /// <summary>Default lifetime of a one-time transfer key.</summary>
    internal static readonly TimeSpan DefaultKeyLifetime = TimeSpan.FromMinutes(5);

    /// <summary>Longest lifetime a transfer key may be configured to.</summary>
    internal static readonly TimeSpan MaxKeyLifetime = TimeSpan.FromMinutes(10);

    public FileTransferStore() : this(DefaultKeyLifetime)
    {
    }

    /// <param name="keyLifetime">Lifetime of a transfer key; greater than zero and at most <see cref="MaxKeyLifetime" />.</param>
    public FileTransferStore(TimeSpan keyLifetime)
    {
        if (keyLifetime <= TimeSpan.Zero || keyLifetime > MaxKeyLifetime)
        {
            throw new ArgumentOutOfRangeException(nameof(keyLifetime),
                $"The file-transfer key lifetime must be greater than zero and at most {MaxKeyLifetime}.");
        }

        _keyLifetime = keyLifetime;
        _baseDir = Path.Combine(Path.GetTempPath(), "octo-mcp-file-transfer", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_baseDir);
    }

    /// <summary>The lifetime a newly issued transfer key gets.</summary>
    internal TimeSpan KeyLifetime => _keyLifetime;

    public (string TransferId, string TargetPath) ReserveUpload(
        FileTransferOwner owner, string fileName, TimeSpan? ttl = null)
    {
        var id = Guid.NewGuid().ToString("N");
        // Strip path separators from the suggested file name; keep just the extension as a hint. The disk name
        // carries no transfer id: it is a fresh random value.
        var safeName = string.IsNullOrWhiteSpace(fileName) ? "upload.bin" : Path.GetFileName(fileName);
        var target = Path.Combine(_baseDir, Guid.NewGuid().ToString("N") + "_" + safeName);
        _pending[id] = new PendingUpload(owner.SessionLabel, owner.Principal, target, safeName,
            DateTime.UtcNow.Add(ttl ?? DefaultTtl), Claimed: false);
        return (id, target);
    }

    public (UploadClaimStatus Status, string? TargetPath) TryClaimUpload(string transferId, string? callerPrincipal)
    {
        while (true)
        {
            // The owner check comes first: a foreign principal never learns whether the id exists or is busy.
            if (!_pending.TryGetValue(transferId, out var p) || !IsPrincipalOwner(p.OwnerPrincipal, callerPrincipal))
            {
                return (UploadClaimStatus.NotFound, null);
            }

            if (DateTime.UtcNow >= p.ExpiresAtUtc)
            {
                if (_pending.TryRemove(new KeyValuePair<string, PendingUpload>(transferId, p)))
                {
                    TryDeleteFile(p.TargetPath);
                }

                return (UploadClaimStatus.NotFound, null);
            }

            if (p.Claimed)
            {
                return (UploadClaimStatus.InProgress, null);
            }

            // Compare-and-swap: exactly one concurrent PUT wins; a loser re-reads and sees Claimed (or the entry gone).
            if (_pending.TryUpdate(transferId, p with { Claimed = true }, p))
            {
                return (UploadClaimStatus.Claimed, p.TargetPath);
            }
        }
    }

    public bool CompleteUpload(string transferId, long sizeBytes)
    {
        if (!_pending.TryGetValue(transferId, out var pending) || !pending.Claimed
            || !_pending.TryRemove(new KeyValuePair<string, PendingUpload>(transferId, pending)))
        {
            return false;
        }

        // The slot cannot be written again, so its key has nothing left to unlock.
        _keys.TryRemove(transferId, out _);
        _uploads[transferId] = new FileTransferUpload(
            transferId, pending.SessionId, pending.TargetPath, pending.FileName, sizeBytes, pending.ExpiresAtUtc)
        {
            OwnerPrincipal = pending.OwnerPrincipal
        };
        return true;
    }

    public void AbortUpload(string transferId)
    {
        if (_pending.TryGetValue(transferId, out var p) && p.Claimed
            && _pending.TryRemove(new KeyValuePair<string, PendingUpload>(transferId, p)))
        {
            _keys.TryRemove(transferId, out _);
            TryDeleteFile(p.TargetPath);
        }
    }

    public FileTransferUpload? GetUpload(string transferId, FileTransferOwner caller)
    {
        // AB#6989: a foreign (or unidentifiable) caller gets exactly the answer an unknown id gets, so the
        // existence of someone else's upload is not observable.
        if (!_uploads.TryGetValue(transferId, out var u) || !IsCallerOwner(u.SessionId, u.OwnerPrincipal, caller))
        {
            return null;
        }

        if (DateTime.UtcNow >= u.ExpiresAtUtc)
        {
            DeleteUpload(transferId);
            return null;
        }

        return u;
    }

    public void DeleteUpload(string transferId)
    {
        _keys.TryRemove(transferId, out _);
        if (_uploads.TryRemove(transferId, out var u))
        {
            TryDeleteFile(u.FilePath);
        }
        if (_pending.TryRemove(transferId, out var p))
        {
            TryDeleteFile(p.TargetPath);
        }
    }

    public string RegisterDownload(FileTransferOwner owner, string filePath, string fileName, TimeSpan? ttl = null)
    {
        var id = Guid.NewGuid().ToString("N");
        var size = File.Exists(filePath) ? new FileInfo(filePath).Length : 0L;
        _downloads[id] = new FileTransferDownload(
            id, owner.SessionLabel, filePath, fileName, size, DateTime.UtcNow.Add(ttl ?? DefaultTtl))
        {
            OwnerPrincipal = owner.Principal
        };
        return id;
    }

    public void CancelTransfer(string transferId, FileTransferOwner caller)
    {
        if (_uploads.TryGetValue(transferId, out var u) && IsCallerOwner(u.SessionId, u.OwnerPrincipal, caller))
        {
            DeleteUpload(transferId);
        }
        else if (_pending.TryGetValue(transferId, out var p) && IsCallerOwner(p.SessionId, p.OwnerPrincipal, caller))
        {
            DeleteUpload(transferId);
        }

        if (_downloads.TryGetValue(transferId, out var d) && IsCallerOwner(d.SessionId, d.OwnerPrincipal, caller))
        {
            DeleteDownload(transferId);
        }
    }

    public FileTransferDownload? GetDownload(string transferId, string? callerPrincipal)
    {
        // A foreign principal gets exactly the answer an unknown id gets.
        if (!_downloads.TryGetValue(transferId, out var d) || !IsPrincipalOwner(d.OwnerPrincipal, callerPrincipal))
        {
            return null;
        }

        if (DateTime.UtcNow >= d.ExpiresAtUtc)
        {
            DeleteDownload(transferId);
            return null;
        }

        return d;
    }

    public void DeleteDownload(string transferId)
    {
        _keys.TryRemove(transferId, out _);
        if (_downloads.TryRemove(transferId, out var d))
        {
            TryDeleteFile(d.FilePath);
        }
    }

    public IssuedTransferKey? IssueTransferKey(string transferId, FileTransferDirection direction,
        FileTransferOwner owner, TimeSpan? ttl = null)
    {
        if (!owner.CanOwnTransfer)
        {
            return null;
        }

        var now = DateTime.UtcNow;
        DateTime transferExpiry;
        if (direction == FileTransferDirection.Upload)
        {
            if (!_pending.TryGetValue(transferId, out var p) || p.Claimed
                || !IsPrincipalOwner(p.OwnerPrincipal, owner.Principal))
            {
                return null;
            }

            transferExpiry = p.ExpiresAtUtc;
        }
        else
        {
            if (!_downloads.TryGetValue(transferId, out var d) || !IsPrincipalOwner(d.OwnerPrincipal, owner.Principal))
            {
                return null;
            }

            transferExpiry = d.ExpiresAtUtc;
        }

        var lifetime = ttl.HasValue && ttl.Value < _keyLifetime ? ttl.Value : _keyLifetime;
        var expires = now.Add(lifetime);
        if (expires > transferExpiry)
        {
            expires = transferExpiry;
        }

        var key = Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        _keys[transferId] = new TransferKeyEntry(HashKey(key), direction, owner.Principal!, owner.TenantId!, expires);
        return new IssuedTransferKey(key, expires);
    }

    public (UploadClaimStatus Status, string? TargetPath) TryClaimUploadWithKey(string transferId, string transferKey,
        TransferKeyCaller caller)
    {
        lock (_keyLock)
        {
            var key = MatchKey(transferId, transferKey, FileTransferDirection.Upload, caller);
            if (key == null)
            {
                return (UploadClaimStatus.NotFound, null);
            }

            // The slot must still belong to the key's owner; the claim itself is the same compare-and-swap the bearer
            // path uses, so a bearer PUT and a key PUT cannot both win either.
            var (status, target) = TryClaimUpload(transferId, key.OwnerPrincipal);
            switch (status)
            {
                case UploadClaimStatus.Claimed:
                    _keys.TryRemove(transferId, out _); // spent
                    return (status, target);
                case UploadClaimStatus.InProgress:
                    return (status, null); // key stays unused
                default:
                    _keys.TryRemove(transferId, out _); // its transfer is gone
                    return (UploadClaimStatus.NotFound, null);
            }
        }
    }

    public FileTransferDownload? ConsumeDownloadKey(string transferId, string transferKey, TransferKeyCaller caller)
    {
        lock (_keyLock)
        {
            var key = MatchKey(transferId, transferKey, FileTransferDirection.Download, caller);
            if (key == null)
            {
                return null;
            }

            // Spent either way: on success it is used, otherwise its transfer is gone.
            _keys.TryRemove(transferId, out _);
            return GetDownload(transferId, key.OwnerPrincipal);
        }
    }

    /// <summary>
    ///     The key entry when <paramref name="transferKey" /> is the live key of the transfer for
    ///     <paramref name="direction" /> and <paramref name="caller" /> may present it; else null. Called under
    ///     <see cref="_keyLock" />. Drops an expired key; leaves every other mismatch untouched, so a guess cannot burn
    ///     the owner's key.
    /// </summary>
    private TransferKeyEntry? MatchKey(string transferId, string transferKey, FileTransferDirection direction,
        TransferKeyCaller caller)
    {
        if (string.IsNullOrEmpty(transferKey) || !_keys.TryGetValue(transferId, out var entry))
        {
            return null;
        }

        if (DateTime.UtcNow >= entry.ExpiresAtUtc)
        {
            _keys.TryRemove(new KeyValuePair<string, TransferKeyEntry>(transferId, entry));
            return null;
        }

        if (entry.Direction != direction
            || !CryptographicOperations.FixedTimeEquals(HashKey(transferKey), entry.KeyHash))
        {
            return null;
        }

        // A token on the request must be the key's owner (same principal and tenant); an authenticated caller
        // without a principal (a client-credentials token without tenant_id) never matches.
        if (caller.IsAuthenticated
            && (!IsPrincipalOwner(entry.OwnerPrincipal, caller.Principal)
                || !string.Equals(entry.TenantId, caller.TenantId, StringComparison.Ordinal)))
        {
            return null;
        }

        return entry;
    }

    private static byte[] HashKey(string key) => SHA256.HashData(Encoding.UTF8.GetBytes(key));

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Test hook: true while a transfer key is live for the id.</summary>
    internal bool HasTransferKey(string transferId) => _keys.ContainsKey(transferId);

    /// <summary>Test hook: true while a reservation (claimed or not) exists for the id.</summary>
    internal bool IsUploadPending(string transferId) => _pending.ContainsKey(transferId);

    /// <summary>Test hook: true while a PUT holds the claim of the id.</summary>
    internal bool IsUploadClaimed(string transferId) =>
        _pending.TryGetValue(transferId, out var p) && p.Claimed;

    internal void SweepExpired()
    {
        var now = DateTime.UtcNow;

        foreach (var (id, u) in _uploads)
        {
            if (now >= u.ExpiresAtUtc)
            {
                DeleteUpload(id);
            }
        }

        foreach (var (id, p) in _pending)
        {
            if (now >= p.ExpiresAtUtc)
            {
                _pending.TryRemove(id, out _);
                _keys.TryRemove(id, out _);
                TryDeleteFile(p.TargetPath);
            }
        }

        foreach (var (id, d) in _downloads)
        {
            if (now >= d.ExpiresAtUtc)
            {
                DeleteDownload(id);
            }
        }

        foreach (var (id, k) in _keys)
        {
            if (now >= k.ExpiresAtUtc)
            {
                _keys.TryRemove(new KeyValuePair<string, TransferKeyEntry>(id, k));
            }
        }
    }

    /// <summary>
    ///     True when <paramref name="callerSessionId" /> is the session that created the entry. Fails closed
    ///     for the <see cref="McpSessionContext.AnonymousCallerLabel" />: that label is a shared constant for
    ///     unidentifiable callers, so it must never act as an identity.
    /// </summary>
    internal static bool IsOwner(string ownerSessionId, string callerSessionId)
    {
        return !string.IsNullOrEmpty(callerSessionId)
               && callerSessionId != McpSessionContext.AnonymousCallerLabel
               && string.Equals(ownerSessionId, callerSessionId, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Tool-side owner check: the session label (the MCP session binding) and the principal (which carries the
    ///     tenant of a client-credentials caller, unlike the label) must both match. Fails closed like its parts.
    /// </summary>
    internal static bool IsCallerOwner(string ownerSessionId, string? ownerPrincipal, FileTransferOwner caller)
    {
        return IsOwner(ownerSessionId, caller.SessionLabel) && IsPrincipalOwner(ownerPrincipal, caller.Principal);
    }

    /// <summary>
    ///     True when the HTTP caller's <paramref name="callerPrincipal" /> is the principal that created the entry.
    ///     Fails closed: a missing principal on either side, or the shared anonymous label, never matches.
    /// </summary>
    internal static bool IsPrincipalOwner(string? ownerPrincipal, string? callerPrincipal)
    {
        return !string.IsNullOrEmpty(ownerPrincipal)
               && !string.IsNullOrEmpty(callerPrincipal)
               && ownerPrincipal != McpSessionContext.AnonymousCallerLabel
               && callerPrincipal != McpSessionContext.AnonymousCallerLabel
               && string.Equals(ownerPrincipal, callerPrincipal, StringComparison.Ordinal);
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Swallow — file may already be gone or held open; sweeper will catch it on next pass.
        }
    }
}

/// <summary>Periodically purges expired transfer entries + their on-disk files.</summary>
internal sealed class FileTransferSweeper : BackgroundService
{
    private readonly FileTransferStore _store;
    private readonly ILogger<FileTransferSweeper> _logger;
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    public FileTransferSweeper(IFileTransferStore store, ILogger<FileTransferSweeper> logger)
    {
        // Cast to the concrete type — sweep is an implementation detail not exposed on the interface.
        _store = (FileTransferStore)store;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                _store.SweepExpired();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "File-transfer sweep failed");
            }

            try { await Task.Delay(Interval, stoppingToken); }
            catch (TaskCanceledException) { break; }
        }
    }
}
