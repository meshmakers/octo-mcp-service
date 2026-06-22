using System.ComponentModel;
using Meshmakers.Octo.Backend.McpServices.Models;
using Meshmakers.Octo.Backend.McpServices.Services;
using ModelContextProtocol.Server;

// ReSharper disable UnusedMember.Global

namespace Meshmakers.Octo.Backend.McpServices.Tools;

/// <summary>
///     Tenant backup tools — dump + restore via the Bot service. Mirrors octo-cli DumpTenant + RestoreTenant.
///     Both operations can move multi-GB files; uploads use the standard prepare_file_upload flow, downloads
///     produce a downloadId you can fetch via /file-transfer/download/{id}.
/// </summary>
[McpServerToolType]
public sealed class TenantBackupTools
{
    /// <summary>Dump a tenant — starts the dump job, waits for completion, publishes the result as a download.</summary>
    [McpServerTool(Name = "dump_tenant")]
    [McpRisk(McpRiskLevel.Medium)]
    [Description(
        "Create a dump of a tenant's data and publish it as a downloadable .tar.gz file. Waits for the dump " +
        "job to complete (default 30 min) then registers the file for download. GET " +
        "/file-transfer/download/{downloadId} to fetch. Equivalent to octo-cli DumpTenant.")]
    public static async Task<FileDownloadResponse> DumpTenant(
        McpServer server,
        [Description("Tenant ID to dump.")] string targetTenantId,
        [Description("Optional file name for the download (default '{tenant}-dump.tar.gz').")] string? fileName = null,
        [Description("Wait timeout in minutes (default 30).")] int waitTimeoutMinutes = 30,
        [Description("Calling/system tenant context. Falls back to URL route.")] string? tenantId = null)
    {
        if (string.IsNullOrWhiteSpace(targetTenantId))
        {
            return new FileDownloadResponse
            {
                IsSuccess = false,
                ErrorMessage = "targetTenantId is required."
            };
        }

        var bot = await BotClientContext.TryBuildAsync(server, tenantId);
        if (bot.Error != null)
        {
            return new FileDownloadResponse { IsSuccess = false, ErrorMessage = bot.Error };
        }

        try
        {
            var startResponse = await bot.Client!.StartDumpRepositoryAsync(targetTenantId);
            await JobPollingHelper.WaitForJobAsync(bot.Client, startResponse.JobId,
                TimeSpan.FromMinutes(waitTimeoutMinutes));

            var effectiveFileName = string.IsNullOrWhiteSpace(fileName)
                ? $"{targetTenantId}-dump.tar.gz"
                : fileName!;

            var store = server.Services!.GetRequiredService<IFileTransferStore>();
            var tempPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}_{effectiveFileName}");

            await bot.Client.DownloadDumpToFileAsync(targetTenantId, startResponse.JobId, tempPath);

            var sessionId = McpSessionContext.GetSessionId(server);
            var downloadId = store.RegisterDownload(sessionId, tempPath, effectiveFileName);
            var size = new FileInfo(tempPath).Length;

            return new FileDownloadResponse
            {
                IsSuccess = true,
                TenantId = bot.TenantId,
                TransferId = downloadId,
                DownloadUrlPath = $"/file-transfer/download/{downloadId}",
                FileName = effectiveFileName,
                SizeBytes = size,
                ExpiresAtUtc = DateTime.UtcNow.AddMinutes(30),
                Message =
                    $"Dump of tenant '{targetTenantId}' ready ({size:N0} bytes). " +
                    $"GET '/file-transfer/download/{downloadId}' to fetch."
            };
        }
        catch (Exception ex)
        {
            return new FileDownloadResponse { IsSuccess = false, ErrorMessage = ex.Message };
        }
    }

    /// <summary>Restore a tenant from an uploaded dump. Destructive: requires confirm.</summary>
    [McpServerTool(Name = "restore_tenant")]
    [McpRisk(McpRiskLevel.Medium)]
    [Description(
        "Restore a tenant from an uploaded dump file. Call prepare_file_upload first, PUT the .tar.gz to the " +
        "returned URL, then invoke this tool with the transferId. DESTRUCTIVE — overwrites tenant data. " +
        "Requires confirm=true. Uses TUS resumable upload from server side. Equivalent to octo-cli RestoreTenant.")]
    public static async Task<JobStartedResponse> RestoreTenant(
        McpServer server,
        [Description("Transfer id from prepare_file_upload (the dump file).")] string transferId,
        [Description("Target tenant ID to restore into.")] string targetTenantId,
        [Description("Database name to restore.")] string databaseName,
        [Description("Optional original database name (when restoring under a different name).")]
        string? oldDatabaseName = null,
        [Description("Must be true to actually restore.")] bool confirm = false,
        [Description("Wait timeout in minutes (default 30).")] int waitTimeoutMinutes = 30,
        [Description("Calling/system tenant context. Falls back to URL route.")] string? tenantId = null)
    {
        if (string.IsNullOrWhiteSpace(transferId) ||
            string.IsNullOrWhiteSpace(targetTenantId) ||
            string.IsNullOrWhiteSpace(databaseName))
        {
            return new JobStartedResponse
            {
                IsSuccess = false,
                ErrorMessage = "transferId, targetTenantId and databaseName are required."
            };
        }

        if (!confirm)
        {
            return new JobStartedResponse
            {
                IsSuccess = false,
                ErrorMessage = $"Refusing to restore tenant '{targetTenantId}' without confirm=true."
            };
        }

        var store = server.Services!.GetRequiredService<IFileTransferStore>();
        var upload = store.GetUpload(transferId);
        if (upload == null)
        {
            return new JobStartedResponse
            {
                IsSuccess = false,
                ErrorMessage = $"Upload '{transferId}' not found or expired."
            };
        }

        var bot = await BotClientContext.TryBuildAsync(server, tenantId);
        if (bot.Error != null)
        {
            return new JobStartedResponse { IsSuccess = false, ErrorMessage = bot.Error };
        }

        try
        {
            var startResponse = await bot.Client!.RestoreRepositoryWithTusAsync(
                targetTenantId, databaseName, upload.FilePath, oldDatabaseName);

            await JobPollingHelper.WaitForJobAsync(bot.Client, startResponse.JobId,
                TimeSpan.FromMinutes(waitTimeoutMinutes));

            // Success — clean up the upload buffer.
            store.DeleteUpload(transferId);

            return new JobStartedResponse
            {
                IsSuccess = true,
                TenantId = bot.TenantId,
                JobId = startResponse.JobId,
                Message =
                    $"Tenant '{targetTenantId}' restored from upload (job '{startResponse.JobId}'). " +
                    $"Database '{databaseName}'" +
                    (oldDatabaseName != null ? $" (old: '{oldDatabaseName}')" : string.Empty) + "."
            };
        }
        catch (Exception ex)
        {
            return new JobStartedResponse { IsSuccess = false, ErrorMessage = ex.Message };
        }
    }
}
