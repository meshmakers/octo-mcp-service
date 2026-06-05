using FluentAssertions;
using Meshmakers.Octo.Backend.McpServices.Tools;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Moq;
using Xunit;

namespace McpServices.Tests.Tools;

public class CkModelFileToolsTests : ToolTestBase
{
    private const string QueryId = "507f1f77bcf86cd799439555";
    private const string OriginRtId = "507f1f77bcf86cd799439666";

    public CkModelFileToolsTests()
    {
        GivenAuthenticated();
    }

    private (string transferId, string filePath) StageUpload(string content = "{}", string fileName = "model.json")
    {
        var (id, path) = FileTransferStore.ReserveUpload("test-session", fileName);
        File.WriteAllText(path, content);
        FileTransferStore.CompleteUpload(id, content.Length);
        return (id, path);
    }

    private void GivenJobSucceeds(string jobId) =>
        MockBotClient.Setup(c => c.GetImportJobStatus(jobId))
            .ReturnsAsync(new JobDto { Id = jobId, Status = "Succeeded" });

    // ── import_ck_model ─────────────────────────────────────────────────────

    [Fact]
    public async Task ImportCkModel_HappyPath_CallsSdkAndWaitsForJob()
    {
        var (transferId, _) = StageUpload();
        MockAssetClient.Setup(c => c.ImportCkModelAsync(DefaultTenantId, It.IsAny<string>()))
            .ReturnsAsync("job-1");
        GivenJobSucceeds("job-1");

        var result = await CkModelFileTools.ImportCkModel(MockServer.Object, transferId);

        result.IsSuccess.Should().BeTrue();
        result.JobId.Should().Be("job-1");
        // The upload buffer was cleaned up on success.
        FileTransferStore.GetUpload(transferId).Should().BeNull();
    }

    [Fact]
    public async Task ImportCkModel_MissingTransferId_ReturnsValidationError()
    {
        var result = await CkModelFileTools.ImportCkModel(MockServer.Object, "");
        result.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task ImportCkModel_UnknownTransferId_ReturnsError()
    {
        var result = await CkModelFileTools.ImportCkModel(MockServer.Object, "nope");

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("not found");
        MockAssetClient.Verify(c => c.ImportCkModelAsync(It.IsAny<string>(), It.IsAny<string>()),
            Times.Never);
    }

    [Fact]
    public async Task ImportCkModel_JobFails_ReturnsErrorAndKeepsBuffer()
    {
        var (transferId, _) = StageUpload();
        MockAssetClient.Setup(c => c.ImportCkModelAsync(DefaultTenantId, It.IsAny<string>()))
            .ReturnsAsync("job-fail");
        MockBotClient.Setup(c => c.GetImportJobStatus("job-fail"))
            .ReturnsAsync(new JobDto { Status = "Failed", ErrorMessage = "schema mismatch" });

        var result = await CkModelFileTools.ImportCkModel(MockServer.Object, transferId);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("schema mismatch");
        // Buffer still there for inspection / retry.
        FileTransferStore.GetUpload(transferId).Should().NotBeNull();
    }

    // ── import_runtime_model ────────────────────────────────────────────────

    [Fact]
    public async Task ImportRuntimeModel_PassesImportStrategy()
    {
        var (transferId, _) = StageUpload();
        MockAssetClient.Setup(c => c.ImportRtModelAsync(DefaultTenantId,
                ImportStrategyDto.Upsert, It.IsAny<string>()))
            .ReturnsAsync("job-2");
        GivenJobSucceeds("job-2");

        var result = await CkModelFileTools.ImportRuntimeModel(MockServer.Object,
            transferId, importStrategy: ImportStrategyDto.Upsert);

        result.IsSuccess.Should().BeTrue();
        MockAssetClient.Verify(c => c.ImportRtModelAsync(DefaultTenantId,
            ImportStrategyDto.Upsert, It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task ImportRuntimeModel_DefaultsToInsertOnly()
    {
        var (transferId, _) = StageUpload();
        MockAssetClient.Setup(c => c.ImportRtModelAsync(DefaultTenantId,
                ImportStrategyDto.InsertOnly, It.IsAny<string>()))
            .ReturnsAsync("job-3");
        GivenJobSucceeds("job-3");

        var result = await CkModelFileTools.ImportRuntimeModel(MockServer.Object, transferId);

        result.IsSuccess.Should().BeTrue();
        MockAssetClient.Verify(c => c.ImportRtModelAsync(DefaultTenantId,
            ImportStrategyDto.InsertOnly, It.IsAny<string>()), Times.Once);
    }

    // ── export_runtime_model_by_query ───────────────────────────────────────

    [Fact]
    public async Task ExportRuntimeModelByQuery_HappyPath_RegistersDownload()
    {
        MockAssetClient.Setup(c => c.ExportRtModelByQueryAsync(DefaultTenantId,
                It.Is<OctoObjectId>(o => o.ToString() == QueryId)))
            .ReturnsAsync("job-export-1");
        GivenJobSucceeds("job-export-1");

        // Mock DownloadDumpToFileAsync to actually create the temp file (the SDK writes streaming bytes).
        MockBotClient.Setup(c => c.DownloadDumpToFileAsync(
                DefaultTenantId, "job-export-1", It.IsAny<string>(),
                It.IsAny<Action<long>?>(), It.IsAny<CancellationToken>()))
            .Returns((string _, string _, string path, Action<long>? _, CancellationToken _) =>
            {
                File.WriteAllText(path, "exported-content");
                return Task.CompletedTask;
            });

        var result = await CkModelFileTools.ExportRuntimeModelByQuery(MockServer.Object,
            QueryId, fileName: "myexport.zip");

        result.IsSuccess.Should().BeTrue();
        result.TransferId.Should().NotBeNullOrEmpty();
        result.DownloadUrlPath.Should().Be($"/file-transfer/download/{result.TransferId}");
        result.FileName.Should().Be("myexport.zip");
        result.SizeBytes.Should().BeGreaterThan(0);

        // The download is queryable from the store.
        FileTransferStore.GetDownload(result.TransferId!).Should().NotBeNull();
    }

    [Fact]
    public async Task ExportRuntimeModelByQuery_MissingQueryId_ReturnsValidationError()
    {
        var result = await CkModelFileTools.ExportRuntimeModelByQuery(MockServer.Object, "");
        result.IsSuccess.Should().BeFalse();
    }

    // ── export_runtime_model_by_deep_graph ──────────────────────────────────

    [Fact]
    public async Task ExportRuntimeModelByDeepGraph_HappyPath_RegistersDownload()
    {
        MockAssetClient.Setup(c => c.ExportRtModelByDeepGraphAsync(
                It.IsAny<string>(),
                It.IsAny<IEnumerable<OctoObjectId>>(),
                It.IsAny<RtCkId<CkTypeId>>()))
            .ReturnsAsync("job-export-2");
        GivenJobSucceeds("job-export-2");
        MockBotClient.Setup(c => c.DownloadDumpToFileAsync(
                DefaultTenantId, "job-export-2", It.IsAny<string>(),
                It.IsAny<Action<long>?>(), It.IsAny<CancellationToken>()))
            .Returns((string _, string _, string path, Action<long>? _, CancellationToken _) =>
            {
                File.WriteAllText(path, "graph-content");
                return Task.CompletedTask;
            });

        // CkTypeId uses Name-VersionNumber (uint), not SemVer.
        var result = await CkModelFileTools.ExportRuntimeModelByDeepGraph(MockServer.Object,
            [OriginRtId], "MyNs-1/MyType-1");

        result.IsSuccess.Should().BeTrue(result.ErrorMessage ?? "");
        result.TransferId.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task ExportRuntimeModelByDeepGraph_MissingArgs_ReturnsValidationError()
    {
        var result = await CkModelFileTools.ExportRuntimeModelByDeepGraph(MockServer.Object, [], "");
        result.IsSuccess.Should().BeFalse();
    }
}
