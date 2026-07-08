using System.ComponentModel;
using Meshmakers.Octo.Backend.McpServices.Models;
using Meshmakers.Octo.Backend.McpServices.Services;
using ModelContextProtocol.Server;

// ReSharper disable UnusedMember.Global

namespace Meshmakers.Octo.Backend.McpServices.Tools;

/// <summary>
///     Foundation tools for file-IO. Tools that need to receive a file (import / restore) start with
///     <c>prepare_file_upload</c>, then the caller PUTs the file body to the returned URL, then a follow-up
///     tool (import_* / restore_*) acts on the transfer id. Tools that produce a file (export / dump) return
///     a download URL and the caller GETs it.
/// </summary>
[McpServerToolType]
public sealed class FileTransferTools
{
    /// <summary>Reserve an upload slot. Caller PUTs the file body to the returned URL path.</summary>
    [McpServerTool(Name = "prepare_file_upload")]
    [Description(
        "Reserve a slot for an upcoming file upload. Returns an opaque transferId and the relative URL path " +
        "the caller must PUT the file body to. The slot expires after ~30 minutes. After PUTting the body, " +
        "pass the transferId to the actual import / restore tool (e.g. import_ck_model, restore_tenant).")]
    public static async Task<PrepareFileUploadResponse> PrepareFileUpload(
        McpServer server,
        [Description("Logical file name (for extension hint + Content-Disposition).")] string fileName)
    {
        var accessToken = await McpSessionContext.TryGetAccessTokenAsync(server);
        if (accessToken == null)
        {
            return new PrepareFileUploadResponse
            {
                IsSuccess = false,
                ErrorMessage = Constants.NotAuthenticatedError
            };
        }

        if (string.IsNullOrWhiteSpace(fileName))
        {
            return new PrepareFileUploadResponse
            {
                IsSuccess = false,
                ErrorMessage = "fileName is required."
            };
        }

        var store = server.Services!.GetRequiredService<IFileTransferStore>();
        var sessionId = McpSessionContext.GetSessionId(server);
        var (transferId, _) = store.ReserveUpload(sessionId, fileName);

        return new PrepareFileUploadResponse
        {
            IsSuccess = true,
            TransferId = transferId,
            UploadUrlPath = $"/file-transfer/upload/{transferId}",
            ExpiresAtUtc = DateTime.UtcNow.AddMinutes(30),
            Message =
                $"Reserved upload slot. PUT the file body to '/file-transfer/upload/{transferId}' " +
                "and then call the matching import / restore tool with this transferId."
        };
    }

    /// <summary>Cancel an upload reservation or discard a finished upload buffer.</summary>
    [McpServerTool(Name = "cancel_file_transfer")]
    [Description(
        "Cancel an upload reservation or discard a finished upload / pending download buffer associated with " +
        "the given transferId. Use when an import / restore tool errored out and the buffer is no longer " +
        "needed before TTL expiry.")]
    public static Task<FileTransferResponse> CancelFileTransfer(
        McpServer server,
        [Description("Transfer id returned by prepare_file_upload or a download-producing tool.")]
        string transferId)
    {
        if (string.IsNullOrWhiteSpace(transferId))
        {
            return Task.FromResult(new FileTransferResponse
            {
                IsSuccess = false,
                ErrorMessage = "transferId is required."
            });
        }

        var store = server.Services!.GetRequiredService<IFileTransferStore>();
        store.DeleteUpload(transferId);
        store.DeleteDownload(transferId);

        return Task.FromResult(new FileTransferResponse
        {
            IsSuccess = true,
            Message = $"Transfer '{transferId}' cancelled."
        });
    }

    /// <summary>Trigger run-fixup-scripts for the resolved tenant. Destructive: requires confirm=true.</summary>
    [McpServerTool(Name = "run_fixup_scripts")]
    [McpRisk(McpRiskLevel.High)]
    [Description(
        "Start the run-fixup-scripts job for the resolved tenant. DESTRUCTIVE — scripts execute against tenant " +
        "data. Requires confirm=true. Returns the job id; poll bot-service to track progress. Equivalent to " +
        "octo-cli RunFixupScripts without the -w wait flag.")]
    public static async Task<JobStartedResponse> RunFixupScripts(
        McpServer server,
        [Description("Must be true to actually start.")] bool confirm = false,
        [Description("Tenant to operate on. Falls back to URL route.")] string? tenantId = null)
    {
        if (!confirm)
        {
            return new JobStartedResponse
            {
                IsSuccess = false,
                ErrorMessage = "Refusing to run fixup scripts without confirm=true."
            };
        }

        var ctx = await BotClientContext.TryBuildAsync(server, tenantId);
        if (ctx.Error != null)
        {
            return new JobStartedResponse { IsSuccess = false, ErrorMessage = ctx.Error };
        }

        try
        {
            var response = await ctx.Client!.StartRunFixupScriptAsync(ctx.TenantId!);
            return new JobStartedResponse
            {
                IsSuccess = true,
                TenantId = ctx.TenantId,
                JobId = response.JobId,
                Message = $"Fixup-script job '{response.JobId}' started for tenant '{ctx.TenantId}'."
            };
        }
        catch (Exception ex)
        {
            return new JobStartedResponse { IsSuccess = false, ErrorMessage = ex.Message };
        }
    }
}
