using System.ComponentModel;
using Meshmakers.Octo.Backend.McpServices.Models;
using Meshmakers.Octo.Backend.McpServices.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts.Serialization;
using Meshmakers.Octo.Services.Infrastructure.Services;
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
    /// <summary>Import a compiled Construction Kit model file (YAML, JSON, or zipped variant; no source zips yet, AB#8501). Caller must prepare_file_upload first.</summary>
    [McpServerTool(Name = "import_ck_model")]
    [McpRisk(McpRiskLevel.Medium)]
    [Description(
        "Import a CK model from an uploaded file. Call prepare_file_upload first, PUT the file to the " +
        "returned URL, then invoke this tool with the transferId. Accepted formats: (1) a single compiled " +
        "YAML built by the CK MSBuild task (typically at bin/<config>/net10.0/octo-ck-libraries/<Project>/" +
        "out/ck-<modelname>-<major>.yaml) — this is the easiest path from a `dotnet build` of a CK project; " +
        "(2) a single compiled JSON in the same catalog format as ~/.octo/local-catalog files; (3) a zip " +
        "of such a compiled model. Compiled models only (source zips: validate with validate_ck_model; " +
        "import support follows AB#8501). The model's `dependencies` must already be loaded in the target " +
        "tenant or the job fails — for service-managed deps (System.Communication / StreamData / Reporting " +
        "/ UI / Ai) use enable_<feature> tools, for catalog deps use import_ck_from_catalog. " +
        "Equivalent to octo-cli ImportConstructionKitModel. " +
        "Requires the tenant role DataModelManagement (AB#8529); without it the call fails with ErrorCode " +
        "DataModelManagementRequired.")]
    public static async Task<JobStartedResponse> ImportCkModel(
        McpServer server,
        [Description("Transfer id from prepare_file_upload.")] string transferId,
        [Description("Tenant to operate on. Falls back to URL route.")] string? tenantId = null,
        [Description("Wait timeout in minutes (default 10).")] int waitTimeoutMinutes = 10)
        => await RunImport(server, transferId, tenantId, waitTimeoutMinutes,
            (asset, tid, path) => asset.ImportCkModelAsync(tid, path),
            opName: "CK model import");

    /// <summary>Import a runtime model file (YAML, JSON, or zipped variant). Caller must prepare_file_upload first.</summary>
    [McpServerTool(Name = "import_runtime_model")]
    [McpRisk(McpRiskLevel.Medium)]
    [Description(
        "Import a runtime model from an uploaded file. Call prepare_file_upload first, PUT the file to " +
        "the returned URL, then invoke this tool with the transferId. Accepted formats: a single runtime " +
        "model YAML (with `$schema: https://schemas.meshmakers.cloud/runtime-model.schema.json` and an " +
        "`entities:` list), the equivalent JSON, or a zip containing one such file. importStrategy " +
        "controls whether existing entities are replaced ('Upsert') or rejected ('InsertOnly'). Equivalent " +
        "to octo-cli ImportRuntimeModel. A file that writes System.Ai KnowledgePage, KnowledgePageVersion (or a type derived from them) or the " +
        "knowledge associations (LinksTo, GeneratedFrom, VersionOf) is refused with error code " +
        "GENERIC_WRITE_REPLACED_BY_KNOWLEDGE_TOOL and nothing is imported: write knowledge pages with the knowledge tools.")]
    public static async Task<JobStartedResponse> ImportRuntimeModel(
        McpServer server,
        [Description("Transfer id from prepare_file_upload.")] string transferId,
        [Description("Import strategy: 'InsertOnly' or 'Upsert'.")] ImportStrategyDto importStrategy = ImportStrategyDto.InsertOnly,
        [Description("Tenant to operate on. Falls back to URL route.")] string? tenantId = null,
        [Description("Wait timeout in minutes (default 10).")] int waitTimeoutMinutes = 10)
        => await RunImport(server, transferId, tenantId, waitTimeoutMinutes,
            (asset, tid, path) => asset.ImportRtModelAsync(tid, importStrategy, path),
            opName: $"Runtime model import ({importStrategy})",
            // AB#8568: knowledge pages, versions and their links are written only through the knowledge tools.
            precheck: new ImportPrecheck(KnowledgeImportPrecheckAsync, KnowledgeImportCheckFailed));

    /// <summary>
    ///     A check that runs on the uploaded file before it is sent to the service. <see cref="Failed" /> is the refusal
    ///     when the file cannot even be prepared for the check (fail closed).
    /// </summary>
    internal sealed record ImportPrecheck(
        Func<McpServer, string, string, Task<JobStartedResponse?>> CheckAsync,
        Func<JobStartedResponse> Failed);

    private static JobStartedResponse KnowledgeImportCheckFailed() => new()
    {
        IsSuccess = false, ErrorCode = KnowledgeWriteRefusal.Code,
        ErrorMessage = KnowledgeWriteRefusal.ImportCheckFailedMessage
    };

    /// <summary>
    ///     AB#8568: refuses a runtime model that writes knowledge pages, versions or their links, judged on the decoded
    ///     ids against the target tenant's CK types (<see cref="KnowledgeImportCheck" />). Fail closed: a file that cannot
    ///     be read or checked is refused with a generic message; the exception stays in the server log.
    /// </summary>
    internal static async Task<JobStartedResponse?> KnowledgeImportPrecheckAsync(McpServer server, string tenantId,
        string filePath)
    {
        string? refusal;
        try
        {
            var services = server.Services!;
            var ckCache = services.GetRequiredService<ICkCacheService>();
            var tenantRepository = await services.GetRequiredService<ITenantResolutionService>()
                .GetTenantRepositoryAsync(tenantId);
            await tenantRepository.LoadCacheForTenantAsync(ckCache);
            refusal = await new KnowledgeImportCheck(services.GetRequiredService<IRtJsonSerializer>(), ckCache,
                    tenantRepository.TenantId)
                .FindRefusalAsync(filePath);
        }
        catch (Exception ex)
        {
            server.Services?.GetService<ILoggerFactory>()?.CreateLogger<CkModelFileTools>()
                .LogWarning(ex, "Runtime model upload for tenant {TenantId} could not be checked for knowledge types",
                    tenantId);
            refusal = KnowledgeWriteRefusal.ImportCheckFailedMessage;
        }

        return refusal == null
            ? null
            : new JobStartedResponse { IsSuccess = false, ErrorCode = KnowledgeWriteRefusal.Code, ErrorMessage = refusal };
    }

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

        var asset = await AssetClientContext.TryBuildAsync(server, tenantId);
        if (asset.Error != null)
        {
            return new FileDownloadResponse { IsSuccess = false, ErrorMessage = asset.Error };
        }

        var bot = await BotClientContext.TryBuildAsync(server, tenantId);
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

        var asset = await AssetClientContext.TryBuildAsync(server, tenantId);
        if (asset.Error != null)
        {
            return new FileDownloadResponse { IsSuccess = false, ErrorMessage = asset.Error };
        }

        var bot = await BotClientContext.TryBuildAsync(server, tenantId);
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
        string opName, ImportPrecheck? precheck = null)
    {
        if (string.IsNullOrWhiteSpace(transferId))
        {
            return new JobStartedResponse { IsSuccess = false, ErrorMessage = "transferId is required." };
        }

        var store = server.Services!.GetRequiredService<IFileTransferStore>();
        var upload = store.GetUpload(transferId, McpSessionContext.GetCallerLabel(server));
        if (upload == null)
        {
            return new JobStartedResponse
            {
                IsSuccess = false,
                ErrorMessage = $"Upload '{transferId}' not found or expired."
            };
        }

        var asset = await AssetClientContext.TryBuildAsync(server, tenantId);
        if (asset.Error != null)
        {
            return new JobStartedResponse { IsSuccess = false, ErrorMessage = asset.Error };
        }

        if (precheck == null)
        {
            return await ImportFileAsync(upload.FilePath);
        }

        // AB#8568 (CWE-367): the upload slot stays writable for a PUT that started before the upload completed, so the
        // check and the import never read the slot itself. Both run on one private snapshot the PUT route cannot reach.
        return await CheckAndImportSnapshotAsync(upload.FilePath, upload.FileName,
            path => precheck.CheckAsync(server, asset.TenantId!, path), ImportFileAsync,
            ex =>
            {
                server.Services?.GetService<ILoggerFactory>()?.CreateLogger<CkModelFileTools>()
                    .LogWarning(ex, "Upload for tenant {TenantId} could not be copied for the import check",
                        asset.TenantId);
                return precheck.Failed();
            });

        async Task<JobStartedResponse> ImportFileAsync(string filePath)
        {
            var bot = await BotClientContext.TryBuildAsync(server, tenantId);
            if (bot.Error != null)
            {
                return new JobStartedResponse { IsSuccess = false, ErrorMessage = bot.Error };
            }

            try
            {
                var jobId = await sdkCall(asset.Client!, asset.TenantId!, filePath);
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
                var problem = ServiceProblemMessage.From(ex);
                return new JobStartedResponse { IsSuccess = false, ErrorMessage = problem.Message, ErrorCode = problem.Code };
            }
        }
    }

    /// <summary>Name prefix of the per-import snapshot directories in the temp path.</summary>
    internal const string ImportSnapshotDirectoryPrefix = "octo-mcp-import-";

    /// <summary>
    ///     AB#8568 (CWE-367): copies the completed upload to a private snapshot, runs <paramref name="check" /> and then
    ///     <paramref name="import" /> on that same snapshot. The snapshot lives in a fresh directory of its own with an
    ///     unpredictable name, created atomically and owner-only (<see cref="PrivateTempDirectory" />), and keeps the
    ///     upload's extension (the readers pick the format by it). The whole directory is deleted on every exit path. A
    ///     refusal keeps the upload (the caller may fix and re-upload; the buffer expires on its own). A directory that
    ///     cannot be created or verified, or a copy that fails, is refused through <paramref name="snapshotFailed" />,
    ///     never imported unchecked. <paramref name="createDirectory" /> replaces the directory creation; tests only.
    /// </summary>
    internal static async Task<JobStartedResponse> CheckAndImportSnapshotAsync(string uploadPath, string uploadFileName,
        Func<string, Task<JobStartedResponse?>> check, Func<string, Task<JobStartedResponse>> import,
        Func<Exception, JobStartedResponse> snapshotFailed, Func<DirectoryInfo>? createDirectory = null)
    {
        DirectoryInfo? directory = null;
        try
        {
            string snapshot;
            try
            {
                directory = createDirectory?.Invoke() ?? PrivateTempDirectory.Create(ImportSnapshotDirectoryPrefix);
                snapshot = Path.Combine(directory.FullName, "snapshot" + Path.GetExtension(uploadFileName));
                await using var source = new FileStream(uploadPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                await using var target = new FileStream(snapshot, SnapshotFileOptions());
                await source.CopyToAsync(target);
            }
            catch (Exception ex)
            {
                return snapshotFailed(ex);
            }

            // The check never throws (fail closed); a refusal stops here.
            if (await check(snapshot) is { } refusal)
            {
                return refusal;
            }

            return await import(snapshot);
        }
        finally
        {
            if (directory != null)
            {
                PrivateTempDirectory.TryDelete(directory.FullName);
            }
        }
    }

    private static FileStreamOptions SnapshotFileOptions()
    {
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        return options;
    }

    private static async Task<FileDownloadResponse> PublishJobDownload(
        McpServer server,
        Sdk.ServiceClient.BotServices.IBotServicesClient bot,
        string tenantId, string jobId, string fileName, string opName)
    {
        var store = server.Services!.GetRequiredService<IFileTransferStore>();

        var tempPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}_{fileName}");
        await bot.DownloadDumpToFileAsync(tenantId, jobId, tempPath);

        var sessionId = McpSessionContext.GetCallerLabel(server);
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
