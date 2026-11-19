using Meshmakers.Octo.Backend.McpServices.Configuration;
using Meshmakers.Octo.Backend.McpServices.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Meshmakers.Octo.Backend.McpServices.Routing;

/// <summary>
///     Out-of-band file transfer endpoints used by File-IO MCP tools. The MCP tool issues an upload reservation
///     or a download id; the client PUTs/GETs <see cref="FileTransferRoutes.UploadPath" /> /
///     <see cref="FileTransferRoutes.DownloadPath" /> with the id in the <see cref="FileTransferRoutes.TransferIdHeader" />
///     header and the same bearer token it uses for the MCP endpoint.
///     <para>
///     Security: the endpoints require the MCP transport policy (authenticated, platform API scope), and an entry is
///     served only to the principal that created it (<see cref="McpCallerPrincipal.StoreKeyPrefix" />); any other
///     caller gets the answer an unknown id gets. The id never appears in a route, so neither access logs nor span
///     attributes carry it, and no response or log line of this controller echoes it on an error. The first PUT claims
///     the slot atomically (a concurrent PUT gets 409); a failed or aborted PUT ends the reservation. A download is
///     single-use: the entry and its file are deleted after the first complete GET (an aborted or partial read keeps
///     it until TTL).
///     </para>
/// </summary>
[ApiController]
[Route("file-transfer")]
[Authorize(Policy = McpAuthorizationPolicy.PolicyName)]
public sealed class FileTransferController : ControllerBase
{
    private readonly IFileTransferStore _store;
    private const long MaxUploadBytes = 5L * 1024 * 1024 * 1024; // 5 GiB safety cap

    private static readonly object UnknownTransfer = new { error = "Unknown or expired transfer." };

    /// <summary>Constructor.</summary>
    public FileTransferController(IFileTransferStore store)
    {
        _store = store;
    }

    /// <summary>Stream the request body into the caller's upload slot named by the transfer id header.</summary>
    [HttpPut("upload")]
    [DisableRequestSizeLimit]
    public async Task<IActionResult> UploadAsync(CancellationToken cancellationToken)
    {
        if (!TryGetTransferId(out var transferId))
        {
            return MissingTransferId();
        }

        var caller = McpCallerPrincipal.FromHttpContext(HttpContext)?.StoreKeyPrefix;
        var (status, target) = _store.TryClaimUpload(transferId, caller);
        switch (status)
        {
            case UploadClaimStatus.InProgress:
                return Conflict(new { error = "An upload for this transfer is already in progress." });
            case UploadClaimStatus.Claimed when target != null:
                break;
            default:
                return NotFound(UnknownTransfer);
        }

        var completed = false;
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
                        return StatusCode(StatusCodes.Status413PayloadTooLarge,
                            new { error = $"Upload exceeded {MaxUploadBytes:N0} byte cap." });
                    }

                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
            }

            if (!_store.CompleteUpload(transferId, total))
            {
                // Cancelled or expired while the body was streaming.
                return NotFound(UnknownTransfer);
            }

            completed = true;
            return Ok(new { transferId, sizeBytes = total });
        }
        catch (OperationCanceledException)
        {
            return StatusCode(StatusCodes.Status499ClientClosedRequest, new { error = "Upload cancelled." });
        }
        catch (Exception ex) when (ex is IOException or BadHttpRequestException)
        {
            // The client went away mid-body (Kestrel reports it as a bad request or an I/O error).
            return StatusCode(StatusCodes.Status400BadRequest, new { error = "Upload aborted." });
        }
        finally
        {
            if (!completed)
            {
                // Deterministic: a PUT that did not complete ends the reservation and deletes the partial file.
                // The client needs a new prepare_file_upload.
                _store.AbortUpload(transferId);
                TryDeleteFile(target);
            }
        }
    }

    /// <summary>Stream the caller's download named by the transfer id header.</summary>
    [HttpGet("download")]
    public IActionResult Download()
    {
        if (!TryGetTransferId(out var transferId))
        {
            return MissingTransferId();
        }

        var caller = McpCallerPrincipal.FromHttpContext(HttpContext)?.StoreKeyPrefix;
        var download = _store.GetDownload(transferId, caller);
        if (download == null || !System.IO.File.Exists(download.FilePath))
        {
            return NotFound(UnknownTransfer);
        }

        // Single use: delete entry + file once the last byte was served and the client did not abort.
        // OnCompleted runs after the response finished, when the stream is already disposed. A partial read
        // (client abort, range that stops early) leaves the entry for a resume; the sweeper ends it at TTL.
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

    private bool TryGetTransferId(out string transferId)
    {
        // Header only: a transfer id in the path or the query would land in access logs and span attributes.
        transferId = Request.Headers[FileTransferRoutes.TransferIdHeader].ToString().Trim();
        return transferId.Length > 0 && !transferId.Contains(',');
    }

    private BadRequestObjectResult MissingTransferId() =>
        BadRequest(new { error = $"Send the transfer id in exactly one '{FileTransferRoutes.TransferIdHeader}' header." });

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (System.IO.File.Exists(path))
            {
                System.IO.File.Delete(path);
            }
        }
        catch
        {
            // The sweeper catches leftovers.
        }
    }
}
