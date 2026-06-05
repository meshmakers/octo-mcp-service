using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Backend.McpServices.Services;

internal sealed class FileTransferStore : IFileTransferStore
{
    private readonly ConcurrentDictionary<string, FileTransferUpload> _uploads = new();
    private readonly ConcurrentDictionary<string, FileTransferDownload> _downloads = new();

    /// <summary>Holds an upload before it's committed: target path + session + expiry.</summary>
    private sealed record PendingUpload(string SessionId, string TargetPath, string FileName, DateTime ExpiresAtUtc);

    private readonly ConcurrentDictionary<string, PendingUpload> _pending = new();

    private readonly string _baseDir;
    private static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(30);

    public FileTransferStore()
    {
        _baseDir = Path.Combine(Path.GetTempPath(), "octo-mcp-file-transfer", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_baseDir);
    }

    public (string TransferId, string TargetPath) ReserveUpload(
        string sessionId, string fileName, TimeSpan? ttl = null)
    {
        var id = Guid.NewGuid().ToString("N");
        // Strip path separators from the suggested file name; keep just the extension as a hint.
        var safeName = string.IsNullOrWhiteSpace(fileName) ? "upload.bin" : Path.GetFileName(fileName);
        var target = Path.Combine(_baseDir, id + "_" + safeName);
        _pending[id] = new PendingUpload(sessionId, target, safeName, DateTime.UtcNow.Add(ttl ?? DefaultTtl));
        return (id, target);
    }

    public void CompleteUpload(string transferId, long sizeBytes)
    {
        if (!_pending.TryRemove(transferId, out var pending))
        {
            return;
        }

        _uploads[transferId] = new FileTransferUpload(
            transferId, pending.SessionId, pending.TargetPath, pending.FileName, sizeBytes, pending.ExpiresAtUtc);
    }

    public FileTransferUpload? GetUpload(string transferId)
    {
        if (!_uploads.TryGetValue(transferId, out var u))
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

    public string RegisterDownload(string sessionId, string filePath, string fileName, TimeSpan? ttl = null)
    {
        var id = Guid.NewGuid().ToString("N");
        var size = File.Exists(filePath) ? new FileInfo(filePath).Length : 0L;
        _downloads[id] = new FileTransferDownload(
            id, sessionId, filePath, fileName, size, DateTime.UtcNow.Add(ttl ?? DefaultTtl));
        return id;
    }

    public FileTransferDownload? GetDownload(string transferId)
    {
        if (!_downloads.TryGetValue(transferId, out var d))
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

    /// <summary>
    ///     Look up the on-disk path the controller should write to for the given reservation.
    ///     Returns null when the reservation is unknown or expired.
    /// </summary>
    internal string? TryReserveTarget(string transferId)
    {
        if (!_pending.TryGetValue(transferId, out var p))
        {
            return null;
        }

        if (DateTime.UtcNow >= p.ExpiresAtUtc)
        {
            _pending.TryRemove(transferId, out _);
            TryDeleteFile(p.TargetPath);
            return null;
        }

        return p.TargetPath;
    }

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
