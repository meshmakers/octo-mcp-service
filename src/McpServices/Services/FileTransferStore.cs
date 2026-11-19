using System.Collections.Concurrent;
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

    private readonly string _baseDir;
    private static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(30);

    public FileTransferStore()
    {
        _baseDir = Path.Combine(Path.GetTempPath(), "octo-mcp-file-transfer", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_baseDir);
    }

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
        if (_downloads.TryRemove(transferId, out var d))
        {
            TryDeleteFile(d.FilePath);
        }
    }

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
