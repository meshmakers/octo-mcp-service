using FluentAssertions;
using Meshmakers.Octo.Backend.McpServices.Tools;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Moq;
using Xunit;

namespace McpServices.Tests.Tools;

public class TenantBackupToolsTests : ToolTestBase
{
    public TenantBackupToolsTests()
    {
        GivenAuthenticated();
    }

    private void GivenJobSucceeds(string jobId) =>
        MockBotClient.Setup(c => c.GetImportJobStatus(jobId))
            .ReturnsAsync(new JobDto { Id = jobId, Status = "Succeeded" });

    // ── dump_tenant ─────────────────────────────────────────────────────────

    [Fact]
    public async Task DumpTenant_HappyPath_RegistersDownload()
    {
        MockBotClient.Setup(c => c.StartDumpRepositoryAsync("target-tenant"))
            .ReturnsAsync(new JobResponseDto("dump-job-1"));
        GivenJobSucceeds("dump-job-1");
        MockBotClient.Setup(c => c.DownloadDumpToFileAsync(
                "target-tenant", "dump-job-1", It.IsAny<string>(),
                It.IsAny<Action<long>?>(), It.IsAny<CancellationToken>()))
            .Returns((string _, string _, string path, Action<long>? _, CancellationToken _) =>
            {
                File.WriteAllText(path, "dump-bytes");
                return Task.CompletedTask;
            });

        var result = await TenantBackupTools.DumpTenant(MockServer.Object, "target-tenant");

        result.IsSuccess.Should().BeTrue(result.ErrorMessage ?? "");
        result.TransferId.Should().NotBeNullOrEmpty();
        result.FileName.Should().Be("target-tenant-dump.tar.gz");
        result.DownloadUrlPath.Should().StartWith("/file-transfer/download/");
        FileTransferStore.GetDownload(result.TransferId!).Should().NotBeNull();
    }

    [Fact]
    public async Task DumpTenant_CustomFileName_IsUsed()
    {
        MockBotClient.Setup(c => c.StartDumpRepositoryAsync("t1"))
            .ReturnsAsync(new JobResponseDto("dump-job-2"));
        GivenJobSucceeds("dump-job-2");
        MockBotClient.Setup(c => c.DownloadDumpToFileAsync(
                "t1", "dump-job-2", It.IsAny<string>(),
                It.IsAny<Action<long>?>(), It.IsAny<CancellationToken>()))
            .Returns((string _, string _, string path, Action<long>? _, CancellationToken _) =>
            {
                File.WriteAllText(path, "x");
                return Task.CompletedTask;
            });

        var result = await TenantBackupTools.DumpTenant(MockServer.Object, "t1",
            fileName: "my-backup.tar.gz");

        result.IsSuccess.Should().BeTrue();
        result.FileName.Should().Be("my-backup.tar.gz");
    }

    [Fact]
    public async Task DumpTenant_MissingTargetTenant_ReturnsValidationError()
    {
        var result = await TenantBackupTools.DumpTenant(MockServer.Object, "");
        result.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task DumpTenant_Unauthenticated_ReturnsAuthError()
    {
        GivenUnauthenticated();
        var result = await TenantBackupTools.DumpTenant(MockServer.Object, "t1");
        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Not authenticated");
    }

    [Fact]
    public async Task DumpTenant_JobFails_ReturnsErrorWithoutDownload()
    {
        MockBotClient.Setup(c => c.StartDumpRepositoryAsync("t1"))
            .ReturnsAsync(new JobResponseDto("dump-fail"));
        MockBotClient.Setup(c => c.GetImportJobStatus("dump-fail"))
            .ReturnsAsync(new JobDto { Status = "Failed", ErrorMessage = "out of disk" });

        var result = await TenantBackupTools.DumpTenant(MockServer.Object, "t1");

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("out of disk");
    }

    // ── restore_tenant ──────────────────────────────────────────────────────

    private (string transferId, string filePath) StageRestoreUpload()
    {
        var (id, path) = FileTransferStore.ReserveUpload("test-session", "dump.tar.gz");
        File.WriteAllBytes(path, [0, 1, 2]);
        FileTransferStore.CompleteUpload(id, 3);
        return (id, path);
    }

    [Fact]
    public async Task RestoreTenant_WithoutConfirm_Refuses()
    {
        var (transferId, _) = StageRestoreUpload();

        var result = await TenantBackupTools.RestoreTenant(MockServer.Object,
            transferId, "target", "newdb");

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("confirm=true");
        MockBotClient.Verify(c => c.RestoreRepositoryWithTusAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<Action<double>?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task RestoreTenant_WithConfirm_CallsTusAndWaits()
    {
        var (transferId, _) = StageRestoreUpload();
        MockBotClient.Setup(c => c.RestoreRepositoryWithTusAsync(
                "target", "newdb", It.IsAny<string>(),
                null, It.IsAny<Action<double>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new JobResponseDto("restore-1"));
        GivenJobSucceeds("restore-1");

        var result = await TenantBackupTools.RestoreTenant(MockServer.Object,
            transferId, "target", "newdb", confirm: true);

        result.IsSuccess.Should().BeTrue(result.ErrorMessage ?? "");
        result.JobId.Should().Be("restore-1");
        // Upload buffer cleaned up on success.
        FileTransferStore.GetUpload(transferId).Should().BeNull();
    }

    [Fact]
    public async Task RestoreTenant_WithOldDatabaseName_PassesItToSdk()
    {
        var (transferId, _) = StageRestoreUpload();
        MockBotClient.Setup(c => c.RestoreRepositoryWithTusAsync(
                "target", "newdb", It.IsAny<string>(),
                "olddb", It.IsAny<Action<double>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new JobResponseDto("restore-2"));
        GivenJobSucceeds("restore-2");

        var result = await TenantBackupTools.RestoreTenant(MockServer.Object,
            transferId, "target", "newdb", oldDatabaseName: "olddb", confirm: true);

        result.IsSuccess.Should().BeTrue();
        MockBotClient.Verify(c => c.RestoreRepositoryWithTusAsync(
            "target", "newdb", It.IsAny<string>(),
            "olddb", It.IsAny<Action<double>?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RestoreTenant_UnknownTransfer_ReturnsError()
    {
        var result = await TenantBackupTools.RestoreTenant(MockServer.Object,
            "nope", "target", "newdb", confirm: true);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("not found");
    }

    [Fact]
    public async Task RestoreTenant_MissingArgs_ReturnsValidationError()
    {
        var result = await TenantBackupTools.RestoreTenant(MockServer.Object,
            "", "", "");
        result.IsSuccess.Should().BeFalse();
    }
}
