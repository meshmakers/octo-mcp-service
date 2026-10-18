using Meshmakers.Octo.Backend.McpServices.Services;
using Microsoft.AspNetCore.Mvc;

namespace Meshmakers.Octo.Backend.McpServices.Routing;

/// <summary>
///     Out-of-band file transfer endpoints used by File-IO MCP tools. The MCP tool issues an upload reservation
///     or a download id, and the MCP client (or any HTTP client) PUTs/GETs against these routes using the
///     opaque transfer id from the tool response.
///
///     Security: transfer ids are random 128-bit GUIDs only visible inside the MCP response to the
///     authenticated client. The endpoints accept any transfer id that's still alive (no extra auth) because
///     guessability is computationally infeasible and entries expire in 30 minutes. For stricter setups, put
///     this service behind your own gateway. AB#6989 narrows the window: the tools that consume an upload or
///     cancel a transfer only accept ids of the calling session, and a download is single-use — the entry and
///     its file are deleted after the first complete GET (an aborted or partial read keeps it until TTL).
/// </summary>
[ApiController]
[Route("file-transfer")]
public sealed class FileTransferController : ControllerBase
{
    private readonly IFileTransferStore _store;
    private const long MaxUploadBytes = 5L * 1024 * 1024 * 1024; // 5 GiB safety cap

    /// <summary>Constructor.</summary>
    public FileTransferController(IFileTransferStore store)
    {
        _store = store;
    }

    /// <summary>Stream the request body into the upload slot identified by <paramref name="transferId"/>.</summary>
    [HttpPut("upload/{transferId}")]
    [DisableRequestSizeLimit]
    public async Task<IActionResult> UploadAsync(string transferId, CancellationToken cancellationToken)
    {
        // We don't materialise the upload entry here — only the pre-reservation knows the target path.
        // The store exposes that via ReserveUpload + CompleteUpload; we use a tiny helper to look it up.
        var target = ((FileTransferStore)_store).TryReserveTarget(transferId);
        if (target == null)
        {
            return NotFound(new { error = "Unknown or expired transferId." });
        }

        try
        {
            long total = 0;
            await using (var output = System.IO.File.Create(target))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await Request.Body.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    total += read;
                    if (total > MaxUploadBytes)
                    {
                        output.Close();
                        try { System.IO.File.Delete(target); } catch { /* ignore */ }
                        return StatusCode(StatusCodes.Status413PayloadTooLarge,
                            new { error = $"Upload exceeded {MaxUploadBytes:N0} byte cap." });
                    }
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
            }

            _store.CompleteUpload(transferId, total);
            return Ok(new { transferId, sizeBytes = total });
        }
        catch (OperationCanceledException)
        {
            try { System.IO.File.Delete(target); } catch { /* ignore */ }
            return StatusCode(StatusCodes.Status499ClientClosedRequest, new { error = "Upload cancelled." });
        }
    }

    /// <summary>Stream the file backing <paramref name="transferId"/> to the client.</summary>
    [HttpGet("download/{transferId}")]
    public IActionResult Download(string transferId)
    {
        var download = _store.GetDownload(transferId);
        if (download == null)
        {
            return NotFound(new { error = "Unknown or expired transferId." });
        }

        if (!System.IO.File.Exists(download.FilePath))
        {
            return NotFound(new { error = "Download file is no longer available." });
        }

        // Single use (AB#6989): delete entry + file once the last byte was served and the client did not
        // abort. OnCompleted runs after the response finished, when the stream is already disposed. A partial
        // read (client abort, range that stops early) leaves the entry for a resume; the sweeper ends it at TTL.
        var stream = new EndTrackingStream(System.IO.File.OpenRead(download.FilePath));
        var requestAborted = HttpContext.RequestAborted;
        Response.OnCompleted(() =>
        {
            if (stream.ReachedEnd && !requestAborted.IsCancellationRequested)
            {
                _store.DeleteDownload(transferId);
            }

            return Task.CompletedTask;
        });
        return File(stream, "application/octet-stream", download.FileName, enableRangeProcessing: true);
    }
}
