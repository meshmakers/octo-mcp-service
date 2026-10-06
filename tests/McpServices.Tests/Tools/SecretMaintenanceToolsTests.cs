using FluentAssertions;
using Meshmakers.Octo.Backend.McpServices.Tools;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Sdk.ServiceClient.BotServices;
using Moq;
using Xunit;

namespace McpServices.Tests.Tools;

/// <summary>AB#5543: get_secret_status / start_secret_sweep over the bot service secret sweep API.</summary>
public class SecretMaintenanceToolsTests : ToolTestBase
{
    private const string Tenant = "test-tenant";

    public SecretMaintenanceToolsTests()
    {
        GivenAuthenticated();
        MockBotClient.Setup(c => c.GetSecretEnvironmentStatusAsync(Tenant)).ReturnsAsync(Environment());
        MockBotClient.Setup(c => c.GetSecretSweepRunsAsync(Tenant, It.IsAny<int>()))
            .ReturnsAsync(new List<SecretSweepRunDto>());
    }

    private static SecretEnvironmentStatusDto Environment(bool configured = true) => new()
    {
        KeyRingConfigured = configured,
        ActiveKeyId = configured ? "k2" : null,
        KnownKeyIds = configured ? ["k1", "k2"] : [],
        StrictMode = true,
        StrictModeSince = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc),
        RecurringVerifyCron = "0 3 * * *",
        LastVerifyAt = new DateTime(2026, 10, 6, 3, 0, 0, DateTimeKind.Utc)
    };

    private static SecretSweepReportDto Report(string tenantId, long plaintext = 0, int reEnter = 0) => new()
    {
        TenantId = tenantId,
        Mode = SecretSweepModeDto.Verify,
        Trigger = SecretSweepTriggerDto.Recurring,
        Outcome = SecretSweepOutcomeDto.Succeeded,
        ActiveKeyId = "k1",
        RemainingLegacyValues = plaintext,
        Steps =
        [
            new SecretSweepStepReportDto
            {
                Mode = SecretSweepModeDto.Verify,
                Success = true,
                Totals = new SecretFormCountsReportDto { Plaintext = plaintext, EncV2 = 5, Total = 5 + plaintext }
            }
        ],
        SecretsToReEnter = Enumerable.Range(0, reEnter).Select(i => new SecretValueReferenceDto
        {
            CkTypeId = "System.Communication/EMailSenderConfiguration", RtId = $"507f1f77bcf86cd79943901{i}",
            AttributePath = "Password", PreviousForm = SecretValueFormDto.UnknownKeyId, KeyId = "k0"
        }).ToList(),
        Unreadable =
        [
            new SecretUnreadableValueDto
            {
                CkTypeId = "System.Communication/SftpConfiguration", RtId = "507f1f77bcf86cd799439099",
                AttributePath = "password", KeyId = "src1"
            }
        ],
        PlaceholdersNormalized = 3
    };

    // ── get_secret_status ─────────────────────────────────────────────────

    [Fact]
    public async Task GetSecretStatus_Tenant_ReturnsReportAndSummary()
    {
        MockBotClient.Setup(c => c.GetSecretSweepReportAsync(Tenant)).ReturnsAsync(Report(Tenant, plaintext: 2, reEnter: 1));

        var result = await SecretMaintenanceTools.GetSecretStatus(MockServer.Object, tenantId: Tenant);

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        result.TenantId.Should().Be(Tenant);
        result.Report.Should().NotBeNull();
        result.Summaries.Should().ContainSingle();
        result.Summaries[0].Totals!.Plaintext.Should().Be(2);
        result.Summaries[0].SecretsToReEnterCount.Should().Be(1);
        result.Summaries[0].UnreadableCount.Should().Be(1);
        result.Summaries[0].Unreadable.Single().KeyId.Should().Be("src1");
        result.Summaries[0].PlaceholdersNormalized.Should().Be(3);
        result.Message.Should().Contain("unreadable (re-entry needed)=1");
        MockBotClient.Verify(c => c.GetSecretSweepReportsAsync(), Times.Never);
    }

    [Fact]
    public async Task GetSecretStatus_Tenant_ReturnsEnvironmentStatusAndRecentRunsWithDumpState()
    {
        var run = new SecretSweepRunDto
        {
            RunId = "run-7", Mode = SecretSweepModeDto.Encrypt, Trigger = SecretSweepTriggerDto.Manual,
            Outcome = SecretSweepOutcomeDto.Succeeded, UnreadableCount = 1,
            Dump = new SecretSweepDumpDto { FileName = "t-presweep.tar.gz", Exists = true, SizeBytes = 42 }
        };
        MockBotClient.Setup(c => c.GetSecretSweepRunsAsync(Tenant, SecretMaintenanceTools.RecentRunLimit))
            .ReturnsAsync(new List<SecretSweepRunDto> { run });
        MockBotClient.Setup(c => c.GetSecretSweepReportAsync(Tenant)).ReturnsAsync(Report(Tenant));

        var result = await SecretMaintenanceTools.GetSecretStatus(MockServer.Object, tenantId: Tenant);

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        result.Environment!.ActiveKeyId.Should().Be("k2");
        result.Environment.KnownKeyIds.Should().Equal("k1", "k2");
        result.RecentRuns.Should().ContainSingle().Which.Dump!.Exists.Should().BeTrue();
        result.Message.Should().Contain("active key 'k2'").And.Contain("strict mode on").And.Contain("1 recent run");
        MockBotClient.Verify(c => c.GetSecretEnvironmentStatusAsync(Tenant), Times.Once);
    }

    [Fact]
    public async Task GetSecretStatus_Tenant_KeyRingNotConfigured_SaysSo()
    {
        MockBotClient.Setup(c => c.GetSecretEnvironmentStatusAsync(Tenant)).ReturnsAsync(Environment(false));
        MockBotClient.Setup(c => c.GetSecretSweepReportAsync(Tenant)).ReturnsAsync((SecretSweepReportDto?)null);

        var result = await SecretMaintenanceTools.GetSecretStatus(MockServer.Object, tenantId: Tenant);

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        result.Environment!.KeyRingConfigured.Should().BeFalse();
        result.Message.Should().Contain("NOT configured");
    }

    [Fact]
    public async Task GetSecretStatus_Tenant_NoReport_IsSuccessWithHint()
    {
        MockBotClient.Setup(c => c.GetSecretSweepReportAsync(Tenant)).ReturnsAsync((SecretSweepReportDto?)null);

        var result = await SecretMaintenanceTools.GetSecretStatus(MockServer.Object, tenantId: Tenant);

        result.IsSuccess.Should().BeTrue();
        result.Report.Should().BeNull();
        result.Message.Should().Contain("start_secret_sweep");
    }

    [Fact]
    public async Task GetSecretStatus_AllTenants_ReturnsAllReports()
    {
        MockBotClient.Setup(c => c.GetSecretSweepReportsAsync())
            .ReturnsAsync(new List<SecretSweepReportDto> { Report("a", plaintext: 1), Report("b") });

        var result = await SecretMaintenanceTools.GetSecretStatus(MockServer.Object, allTenants: true);

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        result.Reports.Should().HaveCount(2);
        result.Summaries.Select(s => s.TenantId).Should().BeEquivalentTo("a", "b");
        result.Environment.Should().BeNull();
        MockBotClient.Verify(c => c.GetSecretEnvironmentStatusAsync(It.IsAny<string>()), Times.Never);
        result.Message.Should().Contain("plaintext=1");
        MockBotClient.Verify(c => c.GetSecretSweepReportAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task GetSecretStatus_Unauthenticated_ReturnsAuthError()
    {
        GivenUnauthenticated();

        var result = await SecretMaintenanceTools.GetSecretStatus(MockServer.Object, tenantId: Tenant);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Not authenticated");
        MockBotClient.Verify(c => c.GetSecretSweepReportAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task GetSecretStatus_ServiceError_IsReported()
    {
        MockBotClient.Setup(c => c.GetSecretSweepReportsAsync()).ThrowsAsync(new InvalidOperationException("403"));

        var result = await SecretMaintenanceTools.GetSecretStatus(MockServer.Object, allTenants: true);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("403");
    }

    // ── start_secret_sweep ────────────────────────────────────────────────

    [Fact]
    public async Task StartSecretSweep_VerifyWithoutConfirm_StartsJob()
    {
        MockBotClient.Setup(c => c.StartSecretSweepAsync(Tenant, SecretSweepModeDto.Verify, false))
            .ReturnsAsync(new JobResponseDto("job-1"));

        var result = await SecretMaintenanceTools.StartSecretSweep(MockServer.Object, tenantId: Tenant);

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        result.JobId.Should().Be("job-1");
        result.Mode.Should().Be(SecretSweepModeDto.Verify);
        MockBotClient.Verify(c => c.GetImportJobStatus(It.IsAny<string>()), Times.Never);
    }

    [Theory]
    [InlineData("Encrypt")]
    [InlineData("reprotect")]
    [InlineData("CleanupUnreadable")]
    public async Task StartSecretSweep_WritingModeWithoutConfirm_IsRefused(string mode)
    {
        var result = await SecretMaintenanceTools.StartSecretSweep(MockServer.Object, mode, tenantId: Tenant);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("confirm=true");
        MockBotClient.Verify(c => c.StartSecretSweepAsync(It.IsAny<string>(), It.IsAny<SecretSweepModeDto>(), It.IsAny<bool>()),
            Times.Never);
    }

    [Fact]
    public async Task StartSecretSweep_ReprotectWithConfirm_StartsJob()
    {
        MockBotClient.Setup(c => c.StartSecretSweepAsync(Tenant, SecretSweepModeDto.Reprotect, true))
            .ReturnsAsync(new JobResponseDto("job-2"));

        var result = await SecretMaintenanceTools.StartSecretSweep(MockServer.Object, "Reprotect", confirm: true,
            tenantId: Tenant);

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        MockBotClient.Verify(c => c.StartSecretSweepAsync(Tenant, SecretSweepModeDto.Reprotect, true), Times.Once);
    }

    [Theory]
    [InlineData("CleanupUnreadable", SecretSweepModeDto.CleanupUnreadable)]
    [InlineData("encrypt", SecretSweepModeDto.Encrypt)]
    public async Task StartSecretSweep_WritingModeWithConfirm_PassesConfirmToBot(string mode,
        SecretSweepModeDto expected)
    {
        MockBotClient.Setup(c => c.StartSecretSweepAsync(Tenant, expected, true))
            .ReturnsAsync(new JobResponseDto("job-c"));

        var result = await SecretMaintenanceTools.StartSecretSweep(MockServer.Object, mode, confirm: true,
            tenantId: Tenant);

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        result.Mode.Should().Be(expected);
        MockBotClient.Verify(c => c.StartSecretSweepAsync(Tenant, expected, true), Times.Once);
    }

    [Fact]
    public async Task StartSecretSweep_CleanupUnreadableWithoutConfirm_ExplainsIrreversibility()
    {
        var result = await SecretMaintenanceTools.StartSecretSweep(MockServer.Object, "CleanupUnreadable",
            tenantId: Tenant);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("pre-sweep dump");
    }

    [Theory]
    [InlineData("Decrypt")]
    [InlineData("4")]
    [InlineData("Wipe")]
    [InlineData("ClearUnknownKid")]
    public async Task StartSecretSweep_DecryptOrUnknownMode_IsRefused(string mode)
    {
        var result = await SecretMaintenanceTools.StartSecretSweep(MockServer.Object, mode, confirm: true,
            tenantId: Tenant);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Verify, Encrypt, Reprotect or CleanupUnreadable");
        MockBotClient.Verify(c => c.StartSecretSweepAsync(It.IsAny<string>(), It.IsAny<SecretSweepModeDto>(), It.IsAny<bool>()),
            Times.Never);
        MockBotClient.Verify(c => c.StartSecretSweepAllTenantsAsync(It.IsAny<SecretSweepModeDto>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public async Task StartSecretSweep_AllTenants_UsesSystemEndpoint()
    {
        MockBotClient.Setup(c => c.StartSecretSweepAllTenantsAsync(SecretSweepModeDto.Encrypt, true))
            .ReturnsAsync(new JobResponseDto("job-all"));

        var result = await SecretMaintenanceTools.StartSecretSweep(MockServer.Object, "Encrypt", allTenants: true,
            confirm: true);

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        result.AllTenants.Should().BeTrue();
        result.TenantId.Should().BeNull();
        MockBotClient.Verify(c => c.StartSecretSweepAsync(It.IsAny<string>(), It.IsAny<SecretSweepModeDto>(), It.IsAny<bool>()),
            Times.Never);
    }

    [Fact]
    public async Task StartSecretSweep_WaitForCompletion_ReturnsTenantReport()
    {
        MockBotClient.Setup(c => c.StartSecretSweepAsync(Tenant, SecretSweepModeDto.Verify, false))
            .ReturnsAsync(new JobResponseDto("job-3"));
        MockBotClient.Setup(c => c.GetImportJobStatus("job-3"))
            .ReturnsAsync(new JobDto { Id = "job-3", Status = "Succeeded" });
        MockBotClient.Setup(c => c.GetSecretSweepReportAsync(Tenant)).ReturnsAsync(Report(Tenant));

        var result = await SecretMaintenanceTools.StartSecretSweep(MockServer.Object, waitForCompletion: true,
            tenantId: Tenant);

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        result.Report.Should().NotBeNull();
        result.Message.Should().Contain("Succeeded");
    }

    [Fact]
    public async Task StartSecretSweep_FailedJob_ReportsFailureWithJobId()
    {
        MockBotClient.Setup(c => c.StartSecretSweepAsync(Tenant, SecretSweepModeDto.Verify, false))
            .ReturnsAsync(new JobResponseDto("job-4"));
        MockBotClient.Setup(c => c.GetImportJobStatus("job-4"))
            .ReturnsAsync(new JobDto { Id = "job-4", Status = "Failed", ErrorMessage = "no key ring" });

        var result = await SecretMaintenanceTools.StartSecretSweep(MockServer.Object, waitForCompletion: true,
            tenantId: Tenant);

        result.IsSuccess.Should().BeFalse();
        result.JobId.Should().Be("job-4");
        result.ErrorMessage.Should().Contain("no key ring");
    }

    [Fact]
    public async Task StartSecretSweep_InvalidTimeout_IsRefused()
    {
        var result = await SecretMaintenanceTools.StartSecretSweep(MockServer.Object, waitForCompletion: true,
            waitTimeoutMinutes: 0, tenantId: Tenant);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("waitTimeoutMinutes");
    }

    [Fact]
    public async Task StartSecretSweep_Unauthenticated_ReturnsAuthError()
    {
        GivenUnauthenticated();

        var result = await SecretMaintenanceTools.StartSecretSweep(MockServer.Object, allTenants: true);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Not authenticated");
        MockBotClient.Verify(c => c.StartSecretSweepAllTenantsAsync(It.IsAny<SecretSweepModeDto>(), It.IsAny<bool>()), Times.Never);
    }

    // ── get_secret_status: dump key ids (AB#5559) ─────────────────────────

    [Fact]
    public async Task GetSecretStatus_Tenant_ShowsRequiredKeyIdsAndDumpKeyMissing()
    {
        var environment = Environment();
        environment.RequiredKeyIds = ["k0", "k2"];
        environment.Warnings = [SecretEnvironmentWarningCodes.DumpKeyMissing];
        MockBotClient.Setup(c => c.GetSecretEnvironmentStatusAsync(Tenant)).ReturnsAsync(environment);

        var result = await SecretMaintenanceTools.GetSecretStatus(MockServer.Object, tenantId: Tenant);

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        result.Environment!.RequiredKeyIds.Should().Equal("k0", "k2");
        result.Message.Should().Contain("Encrypted dumps need key ids [k0, k2]")
            .And.Contain("DumpKeyMissing").And.Contain("([k0])");
    }

    [Fact]
    public async Task GetSecretStatus_Tenant_NoEncryptedDumps_MentionsNoDumpKeys()
    {
        var result = await SecretMaintenanceTools.GetSecretStatus(MockServer.Object, tenantId: Tenant);

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        result.Message.Should().NotContain("Encrypted dumps").And.NotContain("DumpKeyMissing");
    }

    // ── restore_secret_sweep_dump (AB#5559) ───────────────────────────────

    [Fact]
    public async Task RestoreSecretSweepDump_WithConfirm_StartsTheRestoreJob()
    {
        MockBotClient.Setup(c => c.RestoreSecretSweepDumpAsync(Tenant, "run-1", true))
            .ReturnsAsync(new JobResponseDto("job-9"));

        var result = await SecretMaintenanceTools.RestoreSecretSweepDump(MockServer.Object, Tenant, "run-1", true);

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        result.JobId.Should().Be("job-9");
        result.TenantId.Should().Be(Tenant);
        result.Completed.Should().BeFalse();
        result.Message.Should().Contain("Encrypt");
        MockBotClient.Verify(c => c.GetImportJobStatus(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task RestoreSecretSweepDump_WithoutConfirm_IsRefusedWithoutCallingTheBot()
    {
        var result = await SecretMaintenanceTools.RestoreSecretSweepDump(MockServer.Object, Tenant, "run-1", false);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("confirm=true").And.Contain("plaintext");
        MockBotClient.Verify(c => c.RestoreSecretSweepDumpAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>()),
            Times.Never);
    }

    [Theory]
    [InlineData("", "run-1", "tenantId")]
    [InlineData(Tenant, " ", "runId")]
    public async Task RestoreSecretSweepDump_MissingArguments_AreRefused(string tenantId, string runId, string expected)
    {
        var result = await SecretMaintenanceTools.RestoreSecretSweepDump(MockServer.Object, tenantId, runId, true);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain(expected);
        MockBotClient.Verify(c => c.RestoreSecretSweepDumpAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>()),
            Times.Never);
    }

    [Theory]
    [InlineData(SecretSweepDumpRestoreFailure.DumpKeyMissing, System.Net.HttpStatusCode.Conflict, "DumpKeyMissing")]
    [InlineData(SecretSweepDumpRestoreFailure.DumpDeleted, System.Net.HttpStatusCode.Conflict, "DumpDeleted")]
    [InlineData(SecretSweepDumpRestoreFailure.NotFound, System.Net.HttpStatusCode.NotFound, "Not found")]
    public async Task RestoreSecretSweepDump_Refusals_AreReportedWithTheReason(SecretSweepDumpRestoreFailure reason,
        System.Net.HttpStatusCode status, string expected)
    {
        MockBotClient.Setup(c => c.RestoreSecretSweepDumpAsync(Tenant, "run-1", true))
            .ThrowsAsync(new SecretSweepDumpRestoreException(reason, status));

        var result = await SecretMaintenanceTools.RestoreSecretSweepDump(MockServer.Object, Tenant, "run-1", true);

        result.IsSuccess.Should().BeFalse();
        result.RefusalReason.Should().Be(reason.ToString());
        result.ErrorMessage.Should().Contain(expected);
    }

    [Fact]
    public async Task RestoreSecretSweepDump_WaitForCompletion_WaitsForTheJob()
    {
        MockBotClient.Setup(c => c.RestoreSecretSweepDumpAsync(Tenant, "run-1", true))
            .ReturnsAsync(new JobResponseDto("job-10"));
        MockBotClient.Setup(c => c.GetImportJobStatus("job-10"))
            .ReturnsAsync(new JobDto { Id = "job-10", Status = "Succeeded" });

        var result = await SecretMaintenanceTools.RestoreSecretSweepDump(MockServer.Object, Tenant, "run-1", true,
            waitForCompletion: true);

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        result.Completed.Should().BeTrue();
        result.Message.Should().Contain("restored");
    }

    [Fact]
    public async Task RestoreSecretSweepDump_FailedJob_ReportsFailureWithJobId()
    {
        MockBotClient.Setup(c => c.RestoreSecretSweepDumpAsync(Tenant, "run-1", true))
            .ReturnsAsync(new JobResponseDto("job-11"));
        MockBotClient.Setup(c => c.GetImportJobStatus("job-11"))
            .ReturnsAsync(new JobDto { Id = "job-11", Status = "Failed", ErrorMessage = "mongorestore failed" });

        var result = await SecretMaintenanceTools.RestoreSecretSweepDump(MockServer.Object, Tenant, "run-1", true,
            waitForCompletion: true);

        result.IsSuccess.Should().BeFalse();
        result.JobId.Should().Be("job-11");
        result.ErrorMessage.Should().Contain("mongorestore failed");
    }

    [Fact]
    public async Task RestoreSecretSweepDump_Unauthenticated_ReturnsAuthError()
    {
        GivenUnauthenticated();

        var result = await SecretMaintenanceTools.RestoreSecretSweepDump(MockServer.Object, Tenant, "run-1", true);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Not authenticated");
        MockBotClient.Verify(c => c.RestoreSecretSweepDumpAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>()),
            Times.Never);
    }
}
