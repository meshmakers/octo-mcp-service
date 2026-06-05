using System.ComponentModel;
using Meshmakers.Octo.Backend.McpServices.Models;
using Meshmakers.Octo.Backend.McpServices.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using ModelContextProtocol.Server;

// ReSharper disable UnusedMember.Global

namespace Meshmakers.Octo.Backend.McpServices.Tools;

/// <summary>
///     File-based CK model + runtime model import/export tools. Mirrors octo-cli
///     ImportConstructionKitModel, ImportRuntimeModel, ExportRtByQuery, ExportRtByDeepGraph.
///     Imports wait for the job synchronously (default 10 min); exports also wait, then publish the file via
///     the file-transfer download endpoint.
/// </summary>
[McpServerToolType]
public sealed class CkModelFileTools
{
    /// <summary>Import a Construction Kit model file (JSON or zipped JSON). Caller must prepare_file_upload first.</summary>
    [McpServerTool(Name = "import_ck_model")]
    [Description(
        "Import a CK model from an uploaded file. Call prepare_file_upload first, PUT the JSON/zip to the " +
        "returned URL, then invoke this tool with the transferId. Equivalent to octo-cli " +
        "ImportConstructionKitModel.")]
    public static async Task<JobStartedResponse> ImportCkModel(
        McpServer server,
        [Description("Transfer id from prepare_file_upload.")] string transferId,
        [Description("Tenant to operate on. Falls back to URL route.")] string? tenantId = null,
        [Description("Wait timeout in minutes (default 10).")] int waitTimeoutMinutes = 10)
        => await RunImport(server, transferId, tenantId, waitTimeoutMinutes,
            (asset, tid, path) => asset.ImportCkModelAsync(tid, path),
            opName: "CK model import");

    /// <summary>Import a runtime model file. Caller must prepare_file_upload first.</summary>
    [McpServerTool(Name = "import_runtime_model")]
    [Description(
        "Import a runtime model from an uploaded file. Call prepare_file_upload first, PUT the JSON/zip to " +
        "the returned URL, then invoke this tool with the transferId. importStrategy controls whether " +
        "existing entities are replaced ('Upsert') or rejected ('InsertOnly'). Equivalent to octo-cli " +
        "ImportRuntimeModel.")]
    public static async Task<JobStartedResponse> ImportRuntimeModel(
        McpServer server,
        [Description("Transfer id from prepare_file_upload.")] string transferId,
        [Description("Import strategy: 'InsertOnly' or 'Upsert'.")] ImportStrategyDto importStrategy = ImportStrategyDto.InsertOnly,
        [Description("Tenant to operate on. Falls back to URL route.")] string? tenantId = null,
        [Description("Wait timeout in minutes (default 10).")] int waitTimeoutMinutes = 10)
        => await RunImport(server, transferId, tenantId, waitTimeoutMinutes,
            (asset, tid, path) => asset.ImportRtModelAsync(tid, importStrategy, path),
            opName: $"Runtime model import ({importStrategy})");

    /// <summary>Export runtime entities matched by a query. Returns a downloadable file via downloadId.</summary>
    [McpServerTool(Name = "export_runtime_model_by_query")]
    [Description(
        "Export runtime entities matched by a query to a zip file. Waits for the export job to complete, then " +
        "returns a downloadId — fetch the file via GET /file-transfer/download/{downloadId}. Equivalent to " +
        "octo-cli ExportRtByQuery.")]
    public static async Task<FileDownloadResponse> ExportRuntimeModelByQuery(
        McpServer server,
        [Description("Query runtime ID identifying which entities to export.")] string queryId,
        [Description("Logical file name for the download (default 'export.zip').")] string fileName = "export.zip",
        [Description("Tenant to operate on. Falls back to URL route.")] string? tenantId = null,
        [Description("Wait timeout in minutes (default 10).")] int waitTimeoutMinutes = 10)
    {
        if (string.IsNullOrWhiteSpace(queryId))
        {
            return new FileDownloadResponse { IsSuccess = false, ErrorMessage = "queryId is required." };
        }

        var asset = AssetClientContext.TryBuild(server, tenantId);
        if (asset.Error != null)
        {
            return new FileDownloadResponse { IsSuccess = false, ErrorMessage = asset.Error };
        }

        var bot = BotClientContext.TryBuild(server, tenantId);
        if (bot.Error != null)
        {
            return new FileDownloadResponse { IsSuccess = false, ErrorMessage = bot.Error };
        }

        try
        {
            var jobId = await asset.Client!.ExportRtModelByQueryAsync(asset.TenantId!, new OctoObjectId(queryId));
            await JobPollingHelper.WaitForJobAsync(bot.Client!, jobId,
                TimeSpan.FromMinutes(waitTimeoutMinutes));

            return await PublishJobDownload(server, bot.Client!, asset.TenantId!, jobId, fileName,
                opName: $"Runtime export by query '{queryId}'");
        }
        catch (Exception ex)
        {
            return new FileDownloadResponse { IsSuccess = false, ErrorMessage = ex.Message };
        }
    }

    /// <summary>Export the deep graph starting at given runtime ids of a given CK type.</summary>
    [McpServerTool(Name = "export_runtime_model_by_deep_graph")]
    [Description(
        "Export the deep graph starting from the given runtime IDs of a CK type to a zip file. Waits for the " +
        "export job, returns a downloadId. Equivalent to octo-cli ExportRtByDeepGraph.")]
    public static async Task<FileDownloadResponse> ExportRuntimeModelByDeepGraph(
        McpServer server,
        [Description("List of starting runtime IDs.")] List<string> originRtIds,
        [Description("Construction Kit type ID of the starting entities (e.g. 'MyNs-1.0.0/MyType-1.0.0').")]
        string originCkTypeId,
        [Description("Logical file name for the download (default 'export.zip').")] string fileName = "export.zip",
        [Description("Tenant to operate on. Falls back to URL route.")] string? tenantId = null,
        [Description("Wait timeout in minutes (default 10).")] int waitTimeoutMinutes = 10)
    {
        if (originRtIds == null || originRtIds.Count == 0 || string.IsNullOrWhiteSpace(originCkTypeId))
        {
            return new FileDownloadResponse
            {
                IsSuccess = false,
                ErrorMessage = "originRtIds (non-empty) and originCkTypeId are required."
            };
        }

        var asset = AssetClientContext.TryBuild(server, tenantId);
        if (asset.Error != null)
        {
            return new FileDownloadResponse { IsSuccess = false, ErrorMessage = asset.Error };
        }

        var bot = BotClientContext.TryBuild(server, tenantId);
        if (bot.Error != null)
        {
            return new FileDownloadResponse { IsSuccess = false, ErrorMessage = bot.Error };
        }

        try
        {
            var ids = originRtIds.Select(s => new OctoObjectId(s)).ToList();
            var ckTypeId = new RtCkId<CkTypeId>(originCkTypeId);

            var jobId = await asset.Client!.ExportRtModelByDeepGraphAsync(asset.TenantId!, ids, ckTypeId);
            await JobPollingHelper.WaitForJobAsync(bot.Client!, jobId,
                TimeSpan.FromMinutes(waitTimeoutMinutes));

            return await PublishJobDownload(server, bot.Client!, asset.TenantId!, jobId, fileName,
                opName: $"Runtime export by deep graph ({originRtIds.Count} root(s))");
        }
        catch (Exception ex)
        {
            return new FileDownloadResponse { IsSuccess = false, ErrorMessage = ex.Message };
        }
    }

    private static async Task<JobStartedResponse> RunImport(
        McpServer server, string transferId, string? tenantId, int waitTimeoutMinutes,
        Func<Sdk.ServiceClient.AssetRepositoryServices.System.IAssetServicesClient, string, string, Task<string>> sdkCall,
        string opName)
    {
        if (string.IsNullOrWhiteSpace(transferId))
        {
            return new JobStartedResponse { IsSuccess = false, ErrorMessage = "transferId is required." };
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

        var asset = AssetClientContext.TryBuild(server, tenantId);
        if (asset.Error != null)
        {
            return new JobStartedResponse { IsSuccess = false, ErrorMessage = asset.Error };
        }

        var bot = BotClientContext.TryBuild(server, tenantId);
        if (bot.Error != null)
        {
            return new JobStartedResponse { IsSuccess = false, ErrorMessage = bot.Error };
        }

        try
        {
            var jobId = await sdkCall(asset.Client!, asset.TenantId!, upload.FilePath);
            await JobPollingHelper.WaitForJobAsync(bot.Client!, jobId,
                TimeSpan.FromMinutes(waitTimeoutMinutes));

            // Successful → cleanup upload buffer.
            store.DeleteUpload(transferId);

            return new JobStartedResponse
            {
                IsSuccess = true,
                TenantId = asset.TenantId,
                JobId = jobId,
                Message = $"{opName} completed (job '{jobId}')."
            };
        }
        catch (Exception ex)
        {
            return new JobStartedResponse { IsSuccess = false, ErrorMessage = ex.Message };
        }
    }

    private static async Task<FileDownloadResponse> PublishJobDownload(
        McpServer server,
        Sdk.ServiceClient.BotServices.IBotServicesClient bot,
        string tenantId, string jobId, string fileName, string opName)
    {
        var store = server.Services!.GetRequiredService<IFileTransferStore>();

        var tempPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}_{fileName}");
        await bot.DownloadDumpToFileAsync(tenantId, jobId, tempPath);

        var sessionId = McpSessionContext.GetSessionId(server);
        var downloadId = store.RegisterDownload(sessionId, tempPath, fileName);
        var size = new FileInfo(tempPath).Length;

        return new FileDownloadResponse
        {
            IsSuccess = true,
            TenantId = tenantId,
            TransferId = downloadId,
            DownloadUrlPath = $"/file-transfer/download/{downloadId}",
            FileName = fileName,
            SizeBytes = size,
            ExpiresAtUtc = DateTime.UtcNow.AddMinutes(30),
            Message =
                $"{opName} ready. GET '/file-transfer/download/{downloadId}' to fetch ({size:N0} bytes)."
        };
    }
}
