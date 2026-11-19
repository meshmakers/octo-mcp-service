using Meshmakers.Octo.Backend.McpServices.Configuration;
using Meshmakers.Octo.Backend.McpServices.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;

namespace Meshmakers.Octo.Backend.McpServices.Routing;

/// <summary>
///     Out-of-band file transfer endpoints used by File-IO MCP tools. The MCP tool issues an upload reservation
///     or a download id plus a one-time transfer key; the client PUTs/GETs <see cref="FileTransferRoutes.UploadPath" /> /
///     <see cref="FileTransferRoutes.DownloadPath" /> with the id in the <see cref="FileTransferRoutes.TransferIdHeader" />
///     header and either the key in the <see cref="FileTransferRoutes.TransferKeyHeader" /> header or the same bearer
///     token it uses for the MCP endpoint.
///     <para>
///     Security: a request is served when (1) its bearer token passes the MCP transport policy (authenticated, platform
///     API scope) and its principal created the entry (<see cref="McpCallerPrincipal.TransferOwnerKey" />; any other
///     principal gets the answer an unknown id gets, 404), or (2) it carries the entry's one-time key: bound to
///     the transfer, its direction, the creator's principal and tenant, at most 10 minutes valid, spent atomically on
///     first use, stored only as a hash. A key that is missing, wrong, expired, used, foreign or for the other direction,
///     or presented with the token of another principal, gets one identical 401 answer. A token without an owner key
///     (a client-credentials token without <c>tenant_id</c>, or one with neither subject nor client id) is refused with
///     that 401 too, on both paths. Neither id nor key appears in a route,
///     so neither access logs nor span attributes carry them, and no response or log line of this controller echoes
///     them on an error. The first PUT claims
///     the slot atomically (a concurrent PUT gets 409); a failed or aborted PUT ends the reservation. A download is
///     single-use: the entry and its file are deleted after the first complete GET (an aborted or partial read keeps
///     it until TTL).
///     </para>
/// </summary>
[ApiController]
[Route("file-transfer")]
// Anonymous at the routing level on purpose: a request may authenticate with the transfer key instead of a token.
// Every action checks the token against the transport policy itself (CheckCaller) before it touches the store.
[AllowAnonymous]
public sealed class FileTransferController : ControllerBase
{
    private readonly IFileTransferStore _store;
    private readonly IAuthorizationService _authorization;
    private const long MaxUploadBytes = 5L * 1024 * 1024 * 1024; // 5 GiB safety cap

    private static readonly object UnknownTransfer = new { error = "Unknown or expired transfer." };

    // One body for every 401: no token, an invalid token, and a missing, wrong, expired, used, foreign or
    // wrong-direction key all look the same, so a caller learns nothing about the transfer or the key.
    private static readonly object Unauthenticated =
        new { error = "Authentication required: a valid bearer token of the transfer's creator or its one-time transfer key." };

    /// <summary>Constructor.</summary>
    public FileTransferController(IFileTransferStore store, IAuthorizationService authorization)
    {
        _store = store;
        _authorization = authorization;
    }

    /// <summary>The credentials of the request, read before the store is touched.</summary>
    /// <param name="BearerOwner">Owner key of a token that passed the transport policy, else null.</param>
    /// <param name="TransferKey">The <c>X-Transfer-Key</c> header, or null when absent.</param>
    /// <param name="KeyCaller">Who presents the key (the token's principal and tenant when there is a valid token).</param>
    /// <param name="Refusal">The answer when the request may not go on (401/403/400), else null.</param>
    private sealed record CallerCheck(string? BearerOwner, string? TransferKey, TransferKeyCaller KeyCaller,
        IActionResult? Refusal);

    private async Task<CallerCheck> CheckCaller()
    {
        var keyValues = Request.Headers[FileTransferRoutes.TransferKeyHeader];
        string? transferKey = null;
        if (keyValues.Count > 0)
        {
            // Exactly one value; anything else counts as a wrong key (same 401 body, no hint).
            transferKey = keyValues.Count == 1 ? keyValues.ToString().Trim() : string.Empty;
        }

        var principal = McpCallerPrincipal.FromHttpContext(HttpContext);
        var keyCaller = principal == null
            ? TransferKeyCaller.Anonymous
            : new TransferKeyCaller(true, principal.TransferOwnerKey, principal.TenantId);

        string? bearerOwner = null;
        var policyFailed = false;
        if (principal != null)
        {
            var result = await _authorization.AuthorizeAsync(User, McpAuthorizationPolicy.PolicyName);
            if (result.Succeeded)
            {
                bearerOwner = principal.TransferOwnerKey;
            }
            else
            {
                policyFailed = true;
            }
        }

        IActionResult? refusal = null;
        if (transferKey == null)
        {
            if (principal == null)
            {
                refusal = Refuse401();
            }
            else if (policyFailed)
            {
                // Same policy as the MCP endpoint: an authenticated token without the platform API scope.
                refusal = StatusCode(StatusCodes.Status403Forbidden);
            }
            else if (principal.TransferOwnerKey == null)
            {
                // A token that identifies nobody who can own a transfer (no subject and no client id, or a
                // client-credentials token without tenant_id): fail closed, like a missing token.
                refusal = Refuse401();
            }
        }

        return new CallerCheck(bearerOwner, transferKey, keyCaller, refusal);
    }

    private ObjectResult Refuse401()
    {
        Response.Headers[HeaderNames.WWWAuthenticate] = "Bearer";
        return StatusCode(StatusCodes.Status401Unauthorized, Unauthenticated);
    }

    /// <summary>Stream the request body into the caller's upload slot named by the transfer id header.</summary>
    [HttpPut("upload")]
    [DisableRequestSizeLimit]
    public async Task<IActionResult> UploadAsync(CancellationToken cancellationToken)
    {
        var check = await CheckCaller();
        if (check.Refusal != null)
        {
            return check.Refusal;
        }

        if (!TryGetTransferId(out var transferId))
        {
            return MissingTransferId();
        }

        // The creator's token first: it needs no key, and a key sent along stays unused.
        var (status, target) = check.BearerOwner != null
            ? _store.TryClaimUpload(transferId, check.BearerOwner)
            : (UploadClaimStatus.NotFound, null);
        if (status == UploadClaimStatus.NotFound && check.TransferKey != null)
        {
            (status, target) = _store.TryClaimUploadWithKey(transferId, check.TransferKey, check.KeyCaller);
            if (status == UploadClaimStatus.NotFound)
            {
                return Refuse401();
            }
        }

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
        catch (Exception ex) when (ex is BadHttpRequestException
                                   || (ex is IOException && HttpContext.RequestAborted.IsCancellationRequested))
        {
            // The client went away mid-body. A server-side I/O error (full disk) is not caught here and surfaces as a
            // server error; the finally block cleans up either way.
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
    public async Task<IActionResult> Download()
    {
        var check = await CheckCaller();
        if (check.Refusal != null)
        {
            return check.Refusal;
        }

        if (!TryGetTransferId(out var transferId))
        {
            return MissingTransferId();
        }

        // The creator's token first: it needs no key, and a key sent along stays unused.
        var download = check.BearerOwner != null ? _store.GetDownload(transferId, check.BearerOwner) : null;
        if (download == null && check.TransferKey != null)
        {
            download = _store.ConsumeDownloadKey(transferId, check.TransferKey, check.KeyCaller);
            if (download == null)
            {
                return Refuse401();
            }
        }

        if (download == null || !System.IO.File.Exists(download.FilePath))
        {
            return NotFound(UnknownTransfer);
        }

        // Single use: delete entry + file once the last byte was served and the client did not abort.
        // OnCompleted runs after the response finished, when the stream is already disposed. A partial read
        // (client abort, range that stops early) leaves the entry for a resume; the sweeper ends it at TTL.
        var stream = new EndTrackingStream(System.IO.File.OpenRead(download.FilePath));
        var requestAborted = HttpContext.RequestAborted;
        // RequestAborted alone is not enough: Kestrel discards writes after an abort without an error and cancels the
        // token asynchronously, so the copy can read the file to its end before the token says so. The delivery
        // stream notices the lost connection from the flush result of the very write that failed.
        var delivery = new DeliveryTrackingResponseStream(Response.BodyWriter);
        Response.Body = delivery;
        Response.OnCompleted(() =>
        {
            if (stream.ReachedEnd && !delivery.ConnectionLost && !requestAborted.IsCancellationRequested)
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
