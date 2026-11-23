using System.IO.Compression;
using System.Net;
using FluentAssertions;
using Meshmakers.Octo.Backend.McpServices.Models;
using Meshmakers.Octo.Backend.McpServices.Services;
using Meshmakers.Octo.Backend.McpServices.Tools;
using Meshmakers.Octo.Sdk.ServiceClient;
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

    // AB#8568: import_runtime_model reads the file before it is sent, so it must be a runtime model.
    private const string RuntimeModelJson =
        "{\"$schema\":\"https://schemas.meshmakers.cloud/runtime-model.schema.json\",\"dependencies\":[],\"entities\":[]}";

    private (string transferId, string filePath) StageUpload(string content = "{}", string fileName = "model.json")
    {
        var (id, path) = FileTransferStore.ReserveUpload(CallerOwner, fileName);
        File.WriteAllText(path, content);
        CompleteStagedUpload(id, content.Length);
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
        FileTransferStore.GetUpload(transferId, CallerOwner).Should().BeNull();
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
        FileTransferStore.GetUpload(transferId, CallerOwner).Should().NotBeNull();
    }

    // ── import_ck_model: inline draft ───────────────────────────────────────

    private static List<CkDraftFileDto> SourceDraft() =>
    [
        new() { Name = "ckModel.yaml", Content = "modelId: Demo-1.0.0\n" },
        new() { Name = "types/pump.yaml", Content = "types:\n  - typeId: Pump\n" }
    ];

    private const string CompileFailedProblem =
        "{\"type\":\"https://schemas.meshmakers.cloud/errors/ck-model-compile-failed\"," +
        "\"title\":\"The CK model source does not compile\",\"status\":400," +
        "\"detail\":\"The CK model source in draft.zip does not compile: 1 error(s).\"," +
        "\"code\":\"CK_MODEL_COMPILE_FAILED\",\"message\":\"The CK model source in draft.zip does not compile.\"," +
        "\"fileName\":\"draft.zip\"," +
        "\"errors\":[{\"messageNumber\":3,\"level\":\"Error\",\"file\":\"types/pump.yaml\"," +
        "\"elementPath\":\"Demo-1.0.0/Pump-1.Demo/NoSuchAttribute\",\"message\":\"unknown attribute\"," +
        "\"parameters\":{}}]," +
        "\"warnings\":[{\"messageNumber\":7,\"level\":\"Warning\",\"file\":\"ckModel.yaml\",\"message\":\"deprecated\"}]}";

    private void GivenCompileFails() =>
        MockAssetClient.Setup(c => c.ImportCkModelAsync(DefaultTenantId, It.IsAny<string>()))
            .ThrowsAsync(new ServiceClientResultException("Bad request", HttpStatusCode.BadRequest)
            {
                ResponseContent = CompileFailedProblem
            });

    [Fact]
    public async Task ImportCkModel_InlineSourceDraft_PostsAZipOfTheFiles_WaitsForTheJob_AndDeletesTheZip()
    {
        string? postedPath = null;
        List<string>? entries = null;
        string? pumpContent = null;
        MockAssetClient.Setup(c => c.ImportCkModelAsync(DefaultTenantId, It.IsAny<string>()))
            .Callback<string, string>((_, path) =>
            {
                postedPath = path;
                using var zip = ZipFile.OpenRead(path);
                entries = zip.Entries.Select(e => e.FullName).ToList();
                using var reader = new StreamReader(zip.GetEntry("types/pump.yaml")!.Open());
                pumpContent = reader.ReadToEnd();
            })
            .ReturnsAsync("job-inline");
        GivenJobSucceeds("job-inline");

        var result = await CkModelFileTools.ImportCkModel(MockServer.Object, files: SourceDraft());

        result.IsSuccess.Should().BeTrue();
        result.JobId.Should().Be("job-inline");
        result.TenantId.Should().Be(DefaultTenantId);
        result.Errors.Should().BeEmpty();
        entries.Should().BeEquivalentTo("ckModel.yaml", "types/pump.yaml");
        pumpContent.Should().Contain("typeId: Pump");
        postedPath.Should().EndWith(".zip");
        File.Exists(postedPath).Should().BeFalse("an inline draft lives only for the call");
        MockBotClient.Verify(c => c.GetImportJobStatus("job-inline"), Times.AtLeastOnce);
    }

    [Fact]
    public async Task ImportCkModel_InlineCompiledModel_PostsAZipWithTheOneFile()
    {
        List<string>? entries = null;
        MockAssetClient.Setup(c => c.ImportCkModelAsync(DefaultTenantId, It.IsAny<string>()))
            .Callback<string, string>((_, path) =>
            {
                using var zip = ZipFile.OpenRead(path);
                entries = zip.Entries.Select(e => e.FullName).ToList();
            })
            .ReturnsAsync("job-compiled");
        GivenJobSucceeds("job-compiled");

        var result = await CkModelFileTools.ImportCkModel(MockServer.Object,
            files: [new CkDraftFileDto { Name = "ck-demo-1.yaml", Content = "modelId: Demo-1.0.0\ntypes: []\n" }]);

        result.IsSuccess.Should().BeTrue();
        result.JobId.Should().Be("job-compiled");
        entries.Should().Equal("ck-demo-1.yaml");
    }

    [Fact]
    public async Task ImportCkModel_InlineSourceThatDoesNotCompile_ReturnsTheFindings_AndQueuesNothing()
    {
        string? postedPath = null;
        MockAssetClient.Setup(c => c.ImportCkModelAsync(DefaultTenantId, It.IsAny<string>()))
            .Callback<string, string>((_, path) => postedPath = path)
            .ThrowsAsync(new ServiceClientResultException("Bad request", HttpStatusCode.BadRequest)
            {
                ResponseContent = CompileFailedProblem
            });

        var result = await CkModelFileTools.ImportCkModel(MockServer.Object, files: SourceDraft());

        result.IsSuccess.Should().BeFalse();
        result.JobId.Should().BeNull();
        result.ErrorCode.Should().Be("CK_MODEL_COMPILE_FAILED");
        result.ErrorMessage.Should().Contain("does not compile");
        var error = result.Errors.Should().ContainSingle().Subject;
        error.MessageNumber.Should().Be(3);
        error.Level.Should().Be("Error");
        error.File.Should().Be("types/pump.yaml");
        error.ElementPath.Should().Be("Demo-1.0.0/Pump-1.Demo/NoSuchAttribute");
        error.Message.Should().Be("unknown attribute");
        result.Warnings.Should().ContainSingle().Which.MessageNumber.Should().Be(7);
        // No job was started, so nothing is polled; the staged zip is gone.
        MockBotClient.Verify(c => c.GetImportJobStatus(It.IsAny<string>()), Times.Never);
        File.Exists(postedPath).Should().BeFalse();
    }

    [Fact]
    public async Task ImportCkModel_UploadedSourceThatDoesNotCompile_ReturnsTheFindings_AndKeepsTheUpload()
    {
        var (transferId, _) = StageUpload("PK", "draft.zip");
        GivenCompileFails();

        var result = await CkModelFileTools.ImportCkModel(MockServer.Object, transferId);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("CK_MODEL_COMPILE_FAILED");
        result.Errors.Should().ContainSingle().Which.File.Should().Be("types/pump.yaml");
        FileTransferStore.GetUpload(transferId, CallerOwner).Should().NotBeNull("the draft can be fixed and retried");
    }

    [Fact]
    public async Task ImportCkModel_OtherServiceProblem_HasNoFindings()
    {
        MockAssetClient.Setup(c => c.ImportCkModelAsync(DefaultTenantId, It.IsAny<string>()))
            .ThrowsAsync(new ServiceClientResultException("Bad request", HttpStatusCode.BadRequest)
            {
                ResponseContent = "{\"code\":\"SOMETHING_ELSE\",\"detail\":\"no\",\"errors\":[{\"messageNumber\":1}]}"
            });

        var result = await CkModelFileTools.ImportCkModel(MockServer.Object, files: SourceDraft());

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("SOMETHING_ELSE");
        result.Errors.Should().BeEmpty();
    }

    [Fact]
    public async Task ImportCkModel_InlineDraftOverTheByteLimit_IsRefusedWithTheMessageNumber_AndCallsNothing()
    {
        var files = new List<CkDraftFileDto>
        {
            new() { Name = "ckModel.yaml", Content = "modelId: Demo-1.0.0" },
            new() { Name = "types/big.yaml", Content = new string('x', CkDraftInlinePackager.MaxInlineBytes) }
        };

        var result = await CkModelFileTools.ImportCkModel(MockServer.Object, files: files);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("InlineDraftTooLarge");
        result.ErrorMessage.Should().Contain("prepare_file_upload").And.Contain("9005")
            .And.Contain("import_ck_model with the transferId");
        result.Errors.Should().ContainSingle().Which.MessageNumber.Should().Be(9005);
        MockAssetClient.Verify(c => c.ImportCkModelAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ImportCkModel_TooManyInlineFiles_AreRefused_AndCallNothing()
    {
        var files = Enumerable.Range(0, CkDraftInlinePackager.MaxInlineFiles + 1)
            .Select(i => new CkDraftFileDto { Name = $"types/t{i}.yaml", Content = "x" }).ToList();

        var result = await CkModelFileTools.ImportCkModel(MockServer.Object, files: files);

        result.ErrorCode.Should().Be("InlineDraftTooLarge");
        MockAssetClient.Verify(c => c.ImportCkModelAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ImportCkModel_TransferIdAndFiles_AreRefused_AndCallNothing()
    {
        var (transferId, _) = StageUpload();

        var result = await CkModelFileTools.ImportCkModel(MockServer.Object, transferId, SourceDraft());

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("exactly one way");
        MockAssetClient.Verify(c => c.ImportCkModelAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        FileTransferStore.GetUpload(transferId, CallerOwner).Should().NotBeNull();
    }

    [Fact]
    public async Task ImportCkModel_NeitherTransferIdNorFiles_IsRefused()
    {
        var result = await CkModelFileTools.ImportCkModel(MockServer.Object, files: []);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("transferId is required").And.Contain("files");
        MockAssetClient.Verify(c => c.ImportCkModelAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Theory]
    [InlineData("../ckModel.yaml")]
    [InlineData("/etc/passwd")]
    [InlineData("types/../../x.yaml")]
    [InlineData("C:\\x.yaml")]
    [InlineData("..\\..\\x.yaml")]
    [InlineData(" ")]
    public async Task ImportCkModel_UnsafeInlineFileName_IsRejected_AndCallsNothing(string name)
    {
        var result = await CkModelFileTools.ImportCkModel(MockServer.Object,
            files: [new CkDraftFileDto { Name = "ckModel.yaml", Content = "x" }, new CkDraftFileDto { Name = name, Content = "x" }]);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Invalid file name");
        MockAssetClient.Verify(c => c.ImportCkModelAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ImportCkModel_InlineUnauthenticated_IsRefused_AndCallsNothing()
    {
        GivenUnauthenticated();

        var result = await CkModelFileTools.ImportCkModel(MockServer.Object, files: SourceDraft());

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Not authenticated");
        MockAssetClient.Verify(c => c.ImportCkModelAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ImportCkModel_InlineJobFails_ReturnsTheError_AndDeletesTheZip()
    {
        string? postedPath = null;
        MockAssetClient.Setup(c => c.ImportCkModelAsync(DefaultTenantId, It.IsAny<string>()))
            .Callback<string, string>((_, path) => postedPath = path)
            .ReturnsAsync("job-fail-inline");
        MockBotClient.Setup(c => c.GetImportJobStatus("job-fail-inline"))
            .ReturnsAsync(new JobDto { Status = "Failed", ErrorMessage = "dependency missing" });

        var result = await CkModelFileTools.ImportCkModel(MockServer.Object, files: SourceDraft());

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("dependency missing");
        File.Exists(postedPath).Should().BeFalse();
    }

    // ── import_runtime_model ────────────────────────────────────────────────

    [Fact]
    public async Task ImportRuntimeModel_PassesImportStrategy()
    {
        var (transferId, _) = StageUpload(RuntimeModelJson);
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
        var (transferId, _) = StageUpload(RuntimeModelJson);
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
        result.DownloadUrlPath.Should().Be("/file-transfer/download", "the transfer id never travels in the URL");
        result.TransferIdHeader.Should().Be("X-Transfer-Id");
        result.FileName.Should().Be("myexport.zip");
        result.SizeBytes.Should().BeGreaterThan(0);

        // The download is queryable from the store.
        FileTransferStore.GetDownload(result.TransferId!, CallerPrincipal).Should().NotBeNull();
        AssertOneTimeDownloadKey(result.TransferId, result.TransferKey, result.TransferKeyHeader,
            result.TransferKeyExpiresAtUtc, result.Message);
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
        AssertOneTimeDownloadKey(result.TransferId, result.TransferKey, result.TransferKeyHeader,
            result.TransferKeyExpiresAtUtc, result.Message);
    }

    [Fact]
    public async Task ExportRuntimeModelByDeepGraph_MissingArgs_ReturnsValidationError()
    {
        var result = await CkModelFileTools.ExportRuntimeModelByDeepGraph(MockServer.Object, [], "");
        result.IsSuccess.Should().BeFalse();
    }
}
