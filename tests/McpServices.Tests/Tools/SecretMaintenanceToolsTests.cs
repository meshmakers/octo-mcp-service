using FluentAssertions;
using Meshmakers.Octo.Backend.McpServices.Tools;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
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
    }

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
        }).ToList()
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
        MockBotClient.Verify(c => c.GetSecretSweepReportsAsync(), Times.Never);
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
        MockBotClient.Setup(c => c.StartSecretSweepAsync(Tenant, SecretSweepModeDto.Verify))
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
    [InlineData("ClearUnknownKid")]
    public async Task StartSecretSweep_WritingModeWithoutConfirm_IsRefused(string mode)
    {
        var result = await SecretMaintenanceTools.StartSecretSweep(MockServer.Object, mode, tenantId: Tenant);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("confirm=true");
        MockBotClient.Verify(c => c.StartSecretSweepAsync(It.IsAny<string>(), It.IsAny<SecretSweepModeDto>()),
            Times.Never);
    }

    [Fact]
    public async Task StartSecretSweep_ReprotectWithConfirm_StartsJob()
    {
        MockBotClient.Setup(c => c.StartSecretSweepAsync(Tenant, SecretSweepModeDto.Reprotect))
            .ReturnsAsync(new JobResponseDto("job-2"));

        var result = await SecretMaintenanceTools.StartSecretSweep(MockServer.Object, "Reprotect", confirm: true,
            tenantId: Tenant);

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        MockBotClient.Verify(c => c.StartSecretSweepAsync(Tenant, SecretSweepModeDto.Reprotect), Times.Once);
    }

    [Theory]
    [InlineData("Decrypt")]
    [InlineData("4")]
    [InlineData("Wipe")]
    public async Task StartSecretSweep_DecryptOrUnknownMode_IsRefused(string mode)
    {
        var result = await SecretMaintenanceTools.StartSecretSweep(MockServer.Object, mode, confirm: true,
            tenantId: Tenant);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Verify, Encrypt, Reprotect or ClearUnknownKid");
        MockBotClient.Verify(c => c.StartSecretSweepAsync(It.IsAny<string>(), It.IsAny<SecretSweepModeDto>()),
            Times.Never);
        MockBotClient.Verify(c => c.StartSecretSweepAllTenantsAsync(It.IsAny<SecretSweepModeDto>()), Times.Never);
    }

    [Fact]
    public async Task StartSecretSweep_AllTenants_UsesSystemEndpoint()
    {
        MockBotClient.Setup(c => c.StartSecretSweepAllTenantsAsync(SecretSweepModeDto.Encrypt))
            .ReturnsAsync(new JobResponseDto("job-all"));

        var result = await SecretMaintenanceTools.StartSecretSweep(MockServer.Object, "Encrypt", allTenants: true,
            confirm: true);

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        result.AllTenants.Should().BeTrue();
        result.TenantId.Should().BeNull();
        MockBotClient.Verify(c => c.StartSecretSweepAsync(It.IsAny<string>(), It.IsAny<SecretSweepModeDto>()),
            Times.Never);
    }

    [Fact]
    public async Task StartSecretSweep_WaitForCompletion_ReturnsTenantReport()
    {
        MockBotClient.Setup(c => c.StartSecretSweepAsync(Tenant, SecretSweepModeDto.Verify))
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
        MockBotClient.Setup(c => c.StartSecretSweepAsync(Tenant, SecretSweepModeDto.Verify))
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
        MockBotClient.Verify(c => c.StartSecretSweepAllTenantsAsync(It.IsAny<SecretSweepModeDto>()), Times.Never);
    }
}
