using System.ComponentModel;
using System.Globalization;
using Meshmakers.Octo.Backend.McpServices.Models;
using Meshmakers.Octo.Backend.McpServices.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using ModelContextProtocol.Server;

// ReSharper disable UnusedMember.Global

namespace Meshmakers.Octo.Backend.McpServices.Tools;

/// <summary>
///     Archive data export/import tools (AB#4230). Exposes the bot-services archive data jobs — which run
///     directly against CrateDB — to MCP clients. Export starts the job, waits for completion, then publishes
///     the result ZIP as a download; import consumes an uploaded ZIP (via prepare_file_upload), starts the
///     import job, waits, and returns the job outcome. Mirrors the dump/restore file-transfer pattern.
/// </summary>
[McpServerToolType]
public sealed class ArchiveDataTools
{
    /// <summary>Export an archive's row data to a downloadable ZIP. Starts the export job, waits, publishes the file.</summary>
    [McpServerTool(Name = "export_archive_data")]
    [McpRisk(McpRiskLevel.Medium)]
    [Description(
        "Export the row data of an archive (CkArchive entity) to a downloadable ZIP file. The bot-services " +
        "export job reads the rows directly from CrateDB. When both fromUtc and toUtc are omitted the whole " +
        "archive is exported; when supplied (ISO-8601 UTC, e.g. '2026-01-01T00:00:00Z') the half-open window " +
        "[fromUtc, toUtc) is exported. Waits for the job to complete (default 30 min) then registers the ZIP " +
        "for download — GET /file-transfer/download/{downloadId} to fetch. Pair with import_archive_data to " +
        "move archive data between tenants/environments.")]
    public static async Task<FileDownloadResponse> ExportArchiveData(
        McpServer server,
        [Description("Runtime id of the CkArchive entity to export.")] string archiveRtId,
        [Description("Inclusive lower bound of the window (ISO-8601 UTC). Omit (with toUtc) for the whole archive.")]
        string? fromUtc = null,
        [Description("Exclusive upper bound of the window (ISO-8601 UTC). Omit (with fromUtc) for the whole archive.")]
        string? toUtc = null,
        [Description("Logical file name for the download (default 'archive-export.zip').")]
        string fileName = "archive-export.zip",
        [Description("Tenant that owns the archive. Falls back to URL route.")] string? tenantId = null,
        [Description("Wait timeout in minutes (default 30).")] int waitTimeoutMinutes = 30)
    {
        if (string.IsNullOrWhiteSpace(archiveRtId))
        {
            return new FileDownloadResponse { IsSuccess = false, ErrorMessage = "archiveRtId is required." };
        }

        if (!TryParseUtc(fromUtc, out var from, out var fromError))
        {
            return new FileDownloadResponse { IsSuccess = false, ErrorMessage = fromError };
        }

        if (!TryParseUtc(toUtc, out var to, out var toError))
        {
            return new FileDownloadResponse { IsSuccess = false, ErrorMessage = toError };
        }

        if (from.HasValue && to.HasValue && from.Value >= to.Value)
        {
            return new FileDownloadResponse
            {
                IsSuccess = false,
                ErrorMessage = "fromUtc must be strictly less than toUtc."
            };
        }

        var bot = await BotClientContext.TryBuildAsync(server, tenantId);
        if (bot.Error != null)
        {
            return new FileDownloadResponse { IsSuccess = false, ErrorMessage = bot.Error };
        }

        try
        {
            var startResponse = await bot.Client!.StartExportArchiveDataAsync(bot.TenantId!, archiveRtId, from, to);
            await JobPollingHelper.WaitForJobAsync(bot.Client, startResponse.JobId,
                TimeSpan.FromMinutes(waitTimeoutMinutes));

            var effectiveFileName = string.IsNullOrWhiteSpace(fileName) ? "archive-export.zip" : fileName;

            var store = server.Services!.GetRequiredService<IFileTransferStore>();
            var tempPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}_{effectiveFileName}");

            await bot.Client.DownloadDumpToFileAsync(bot.TenantId!, startResponse.JobId, tempPath);

            var sessionId = McpSessionContext.GetSessionId(server);
            var downloadId = store.RegisterDownload(sessionId, tempPath, effectiveFileName);
            var size = new FileInfo(tempPath).Length;

            var window = from.HasValue || to.HasValue
                ? $"window [{(from?.ToString("O") ?? "-∞")}, {(to?.ToString("O") ?? "+∞")})"
                : "whole archive";

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
                    $"Archive '{archiveRtId}' export ready ({window}, {size:N0} bytes). " +
                    $"GET '/file-transfer/download/{downloadId}' to fetch."
            };
        }
        catch (Exception ex)
        {
            return new FileDownloadResponse { IsSuccess = false, ErrorMessage = ex.Message };
        }
    }

    /// <summary>Import an archive data ZIP into a target archive. Consumes an uploaded file via transferId.</summary>
    [McpServerTool(Name = "import_archive_data")]
    [McpRisk(McpRiskLevel.Medium)]
    [Description(
        "Import a previously exported archive data ZIP into a target archive (CkArchive entity). Call " +
        "prepare_file_upload first, PUT the ZIP to the returned URL, then invoke this tool with the " +
        "transferId. The bot-services import job validates that the export's schema matches the target " +
        "archive, then streams the rows into CrateDB. mode = 'InsertOnly' (default; conflicts on the natural " +
        "key are an error — for raw archives) or 'Upsert' (insert-or-update — required for windowed time-range " +
        "/ rollup archives). IMPORTANT: the target archive MUST be Disabled during the import (concept §7.1) — " +
        "the job rejects the import otherwise. On job failure the bot's error message (schema mismatch, " +
        "archive-not-Disabled, etc.) is surfaced verbatim. Waits for the job to complete (default 30 min).")]
    public static async Task<JobStartedResponse> ImportArchiveData(
        McpServer server,
        [Description("Runtime id of the target CkArchive entity to import into.")] string archiveRtId,
        [Description("Transfer id from prepare_file_upload (the export ZIP).")] string transferId,
        [Description("Import mode: 'InsertOnly' (default) or 'Upsert'.")]
        ArchiveImportMode mode = ArchiveImportMode.InsertOnly,
        [Description("Tenant that owns the archive. Falls back to URL route.")] string? tenantId = null,
        [Description("Wait timeout in minutes (default 30).")] int waitTimeoutMinutes = 30)
    {
        if (string.IsNullOrWhiteSpace(archiveRtId) || string.IsNullOrWhiteSpace(transferId))
        {
            return new JobStartedResponse
            {
                IsSuccess = false,
                ErrorMessage = "archiveRtId and transferId are required."
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
            var startResponse = await bot.Client!.StartImportArchiveDataWithTusAsync(
                bot.TenantId!, archiveRtId, upload.FilePath, mode);

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
                    $"Archive '{archiveRtId}' import completed (mode {mode}, job '{startResponse.JobId}')."
            };
        }
        catch (Exception ex)
        {
            return new JobStartedResponse { IsSuccess = false, ErrorMessage = ex.Message };
        }
    }

    /// <summary>Parse an optional ISO-8601 UTC timestamp. Returns false with an error message on a bad value.</summary>
    private static bool TryParseUtc(string? value, out DateTime? result, out string? error)
    {
        result = null;
        error = null;

        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        if (DateTime.TryParse(value, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed))
        {
            result = parsed;
            return true;
        }

        error = $"Invalid ISO-8601 UTC timestamp: '{value}'.";
        return false;
    }
}
