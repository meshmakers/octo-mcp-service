using FluentAssertions;
using Meshmakers.Octo.Backend.McpServices.Tools;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Moq;
using Xunit;

namespace McpServices.Tests.Tools;

public class ArchiveDataToolsTests : ToolTestBase
{
    public ArchiveDataToolsTests()
    {
        GivenAuthenticated();
    }

    private void GivenJobSucceeds(string jobId) =>
        MockBotClient.Setup(c => c.GetImportJobStatus(jobId))
            .ReturnsAsync(new JobDto { Id = jobId, Status = "Succeeded" });

    // ── export_archive_data ─────────────────────────────────────────────────

    [Fact]
    public async Task ExportArchiveData_WholeArchive_RegistersDownload()
    {
        MockBotClient.Setup(c => c.StartExportArchiveDataAsync("test-tenant", "arch-1", null, null))
            .ReturnsAsync(new JobResponseDto("exp-job-1"));
        GivenJobSucceeds("exp-job-1");
        MockBotClient.Setup(c => c.DownloadDumpToFileAsync(
                "test-tenant", "exp-job-1", It.IsAny<string>(),
                It.IsAny<Action<long>?>(), It.IsAny<CancellationToken>()))
            .Returns((string _, string _, string path, Action<long>? _, CancellationToken _) =>
            {
                File.WriteAllText(path, "archive-bytes");
                return Task.CompletedTask;
            });

        var result = await ArchiveDataTools.ExportArchiveData(MockServer.Object, "arch-1");

        result.IsSuccess.Should().BeTrue(result.ErrorMessage ?? "");
        result.TransferId.Should().NotBeNullOrEmpty();
        result.FileName.Should().Be("archive-export.zip");
        result.DownloadUrlPath.Should().StartWith("/file-transfer/download/");
        FileTransferStore.GetDownload(result.TransferId!).Should().NotBeNull();
        MockBotClient.Verify(c => c.StartExportArchiveDataAsync("test-tenant", "arch-1", null, null), Times.Once);
    }

    [Fact]
    public async Task ExportArchiveData_WithWindow_PassesParsedUtcBounds()
    {
        var from = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var to = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc);
        MockBotClient.Setup(c => c.StartExportArchiveDataAsync("test-tenant", "arch-1", from, to))
            .ReturnsAsync(new JobResponseDto("exp-job-2"));
        GivenJobSucceeds("exp-job-2");
        MockBotClient.Setup(c => c.DownloadDumpToFileAsync(
                "test-tenant", "exp-job-2", It.IsAny<string>(),
                It.IsAny<Action<long>?>(), It.IsAny<CancellationToken>()))
            .Returns((string _, string _, string path, Action<long>? _, CancellationToken _) =>
            {
                File.WriteAllText(path, "x");
                return Task.CompletedTask;
            });

        var result = await ArchiveDataTools.ExportArchiveData(MockServer.Object, "arch-1",
            fromUtc: "2026-01-01T00:00:00Z", toUtc: "2026-02-01T00:00:00Z");

        result.IsSuccess.Should().BeTrue(result.ErrorMessage ?? "");
        MockBotClient.Verify(c => c.StartExportArchiveDataAsync("test-tenant", "arch-1", from, to), Times.Once);
    }

    [Fact]
    public async Task ExportArchiveData_MissingArchiveRtId_ReturnsValidationError()
    {
        var result = await ArchiveDataTools.ExportArchiveData(MockServer.Object, "");
        result.IsSuccess.Should().BeFalse();
        MockBotClient.Verify(c => c.StartExportArchiveDataAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTime?>(), It.IsAny<DateTime?>()), Times.Never);
    }

    [Fact]
    public async Task ExportArchiveData_InvalidFromUtc_ReturnsValidationError()
    {
        var result = await ArchiveDataTools.ExportArchiveData(MockServer.Object, "arch-1",
            fromUtc: "not-a-date");
        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Invalid ISO-8601");
    }

    [Fact]
    public async Task ExportArchiveData_FromNotBeforeTo_ReturnsValidationError()
    {
        var result = await ArchiveDataTools.ExportArchiveData(MockServer.Object, "arch-1",
            fromUtc: "2026-02-01T00:00:00Z", toUtc: "2026-01-01T00:00:00Z");
        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("strictly less than");
    }

    [Fact]
    public async Task ExportArchiveData_Unauthenticated_ReturnsAuthError()
    {
        GivenUnauthenticated();
        var result = await ArchiveDataTools.ExportArchiveData(MockServer.Object, "arch-1");
        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Not authenticated");
    }

    [Fact]
    public async Task ExportArchiveData_JobFails_SurfacesErrorWithoutDownload()
    {
        MockBotClient.Setup(c => c.StartExportArchiveDataAsync("test-tenant", "arch-1", null, null))
            .ReturnsAsync(new JobResponseDto("exp-fail"));
        MockBotClient.Setup(c => c.GetImportJobStatus("exp-fail"))
            .ReturnsAsync(new JobDto { Status = "Failed", ErrorMessage = "crate read error" });

        var result = await ArchiveDataTools.ExportArchiveData(MockServer.Object, "arch-1");

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("crate read error");
    }

    // ── import_archive_data ─────────────────────────────────────────────────

    private string StageImportUpload()
    {
        var (id, path) = FileTransferStore.ReserveUpload("test-session", "archive-export.zip");
        File.WriteAllBytes(path, [0, 1, 2]);
        FileTransferStore.CompleteUpload(id, 3);
        return id;
    }

    [Fact]
    public async Task ImportArchiveData_HappyPath_CallsTusAndWaits()
    {
        var transferId = StageImportUpload();
        MockBotClient.Setup(c => c.StartImportArchiveDataWithTusAsync(
                "test-tenant", "arch-1", It.IsAny<string>(), ArchiveImportMode.InsertOnly,
                It.IsAny<Action<double>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new JobResponseDto("imp-1"));
        GivenJobSucceeds("imp-1");

        var result = await ArchiveDataTools.ImportArchiveData(MockServer.Object, "arch-1", transferId);

        result.IsSuccess.Should().BeTrue(result.ErrorMessage ?? "");
        result.JobId.Should().Be("imp-1");
        // Upload buffer cleaned up on success.
        FileTransferStore.GetUpload(transferId).Should().BeNull();
    }

    [Fact]
    public async Task ImportArchiveData_UpsertMode_PassedToSdk()
    {
        var transferId = StageImportUpload();
        MockBotClient.Setup(c => c.StartImportArchiveDataWithTusAsync(
                "test-tenant", "arch-1", It.IsAny<string>(), ArchiveImportMode.Upsert,
                It.IsAny<Action<double>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new JobResponseDto("imp-2"));
        GivenJobSucceeds("imp-2");

        var result = await ArchiveDataTools.ImportArchiveData(MockServer.Object, "arch-1", transferId,
            mode: ArchiveImportMode.Upsert);

        result.IsSuccess.Should().BeTrue(result.ErrorMessage ?? "");
        MockBotClient.Verify(c => c.StartImportArchiveDataWithTusAsync(
            "test-tenant", "arch-1", It.IsAny<string>(), ArchiveImportMode.Upsert,
            It.IsAny<Action<double>?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ImportArchiveData_UnknownTransfer_ReturnsError()
    {
        var result = await ArchiveDataTools.ImportArchiveData(MockServer.Object, "arch-1", "nope");
        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("not found");
    }

    [Fact]
    public async Task ImportArchiveData_MissingArgs_ReturnsValidationError()
    {
        var result = await ArchiveDataTools.ImportArchiveData(MockServer.Object, "", "");
        result.IsSuccess.Should().BeFalse();
        MockBotClient.Verify(c => c.StartImportArchiveDataWithTusAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<ArchiveImportMode>(),
            It.IsAny<Action<double>?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ImportArchiveData_Unauthenticated_ReturnsAuthError()
    {
        GivenUnauthenticated();
        var transferId = StageImportUpload();
        var result = await ArchiveDataTools.ImportArchiveData(MockServer.Object, "arch-1", transferId);
        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Not authenticated");
    }

    [Fact]
    public async Task ImportArchiveData_JobFails_SurfacesBotErrorVerbatim()
    {
        var transferId = StageImportUpload();
        MockBotClient.Setup(c => c.StartImportArchiveDataWithTusAsync(
                "test-tenant", "arch-1", It.IsAny<string>(), ArchiveImportMode.InsertOnly,
                It.IsAny<Action<double>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new JobResponseDto("imp-fail"));
        MockBotClient.Setup(c => c.GetImportJobStatus("imp-fail"))
            .ReturnsAsync(new JobDto { Status = "Failed", ErrorMessage = "archive is not Disabled" });

        var result = await ArchiveDataTools.ImportArchiveData(MockServer.Object, "arch-1", transferId);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("archive is not Disabled");
    }
}
