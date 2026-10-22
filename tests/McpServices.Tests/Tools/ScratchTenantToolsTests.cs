using FluentAssertions;
using Meshmakers.Octo.Backend.McpServices.Services;
using Meshmakers.Octo.Backend.McpServices.Tools;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Moq;
using Xunit;

namespace McpServices.Tests.Tools;

/// <summary>
///     AB#7192: <c>create_tenant scratch</c>, <c>restore_tenant scratch</c>, <c>delete_tenant disableFeatures</c> and the
///     AI toggle tools <c>enable_ai</c> / <c>disable_ai</c>.
/// </summary>
public class ScratchTenantToolsTests : ToolTestBase
{
    private const string Child = "scratch1";

    public ScratchTenantToolsTests()
    {
        GivenAuthenticated();
    }

    private static TenantFeaturesStatusDto Status(bool ai, bool communication, bool reporting, bool streamData) => new()
    {
        AiServices = new TenantFeatureStatusDto { TenantEnabled = ai },
        Communication = new TenantFeatureStatusDto { TenantEnabled = communication },
        Reporting = new TenantFeatureStatusDto { TenantEnabled = reporting },
        StreamData = new StreamDataFeatureStatusDto { InstanceEnabled = true, TenantEnabled = streamData }
    };

    // ----- create_tenant scratch -----

    [Fact]
    public async Task CreateTenant_Scratch_PassesTheFlagToTheSdk()
    {
        var result = await TenantManagementTools.CreateTenant(MockServer.Object, "Scratch1", "Scratch1_Db",
            scratch: true);

        result.IsSuccess.Should().BeTrue();
        result.Message.Should().Contain("scratch tenant");
        MockAssetClient.Verify(c => c.CreateTenantAsync("scratch1", "scratch1_db", true), Times.Once);
        MockAssetClient.Verify(c => c.CreateTenantAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task CreateTenant_WithoutScratch_IsTheUnchangedCall()
    {
        var result = await TenantManagementTools.CreateTenant(MockServer.Object, "plain1", "plain1_db");

        result.IsSuccess.Should().BeTrue();
        MockAssetClient.Verify(c => c.CreateTenantAsync("plain1", "plain1_db"), Times.Once);
        MockAssetClient.Verify(c => c.CreateTenantAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>()),
            Times.Never);
    }

    [Fact]
    public async Task CreateTenant_Scratch_RefusedByTheService_SurfacesTheReason()
    {
        MockAssetClient.Setup(c => c.CreateTenantAsync("scratch1", "scratch1_db", true))
            .ThrowsAsync(new InvalidOperationException("Forbidden: A scratch tenant can only be created by a client-credentials client"));

        var result = await TenantManagementTools.CreateTenant(MockServer.Object, "scratch1", "scratch1_db",
            scratch: true);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("client-credentials");
    }

    // ----- delete_tenant disableFeatures -----

    [Fact]
    public async Task DeleteTenant_DisableFeatures_SwitchesOffWhatIsEnabled_WithTheChildToken_ThenDeletes()
    {
        MockAssetClient.Setup(c => c.GetTenantFeaturesStatusAsync()).ReturnsAsync(Status(true, true, false, true));
        var calls = new List<string>();
        MockAiClient.Setup(c => c.DisableAsync(Child)).Callback(() => calls.Add("ai")).Returns(Task.CompletedTask);
        MockCommunicationClient.Setup(c => c.DisableAsync(Child)).Callback(() => calls.Add("communication"))
            .Returns(Task.CompletedTask);
        MockStreamDataClient.Setup(c => c.DisableAsync(Child)).Callback(() => calls.Add("streamdata"))
            .Returns(Task.CompletedTask);
        MockAssetClient.Setup(c => c.DeleteTenantAsync(Child)).Callback(() => calls.Add("delete"))
            .Returns(Task.CompletedTask);

        var result = await TenantManagementTools.DeleteTenant(MockServer.Object, Child, confirm: true,
            disableFeatures: true);

        result.IsSuccess.Should().BeTrue();
        calls.Should().Equal("ai", "communication", "streamdata", "delete");
        result.DisabledFeatures.Should().Equal("AI Services", "Communication", "Stream Data");
        result.FeaturesStillEnabled.Should().BeEmpty();
        MockReportingClient.Verify(c => c.DisableAsync(It.IsAny<string>()), Times.Never);
        // The child's own token is requested for the capability calls, not only the parent token.
        MockTokenExchanger.Verify(e => e.ExchangeForTenantAsync(It.IsAny<string>(), Child, It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);
    }

    [Fact]
    public async Task DeleteTenant_DisableFeatures_ActivatedArchives_AreReported_AndTheTenantIsNotDeleted()
    {
        MockAssetClient.Setup(c => c.GetTenantFeaturesStatusAsync()).ReturnsAsync(Status(false, true, false, true));
        MockStreamDataClient.Setup(c => c.DisableAsync(Child))
            .ThrowsAsync(new InvalidOperationException("Conflict: archives still activated: rawdata"));

        var result = await TenantManagementTools.DeleteTenant(MockServer.Object, Child, confirm: true,
            disableFeatures: true);

        result.IsSuccess.Should().BeFalse();
        result.DisabledFeatures.Should().Equal("Communication");
        result.FeaturesStillEnabled.Should().ContainSingle().Which.Should().Contain("Stream Data")
            .And.Contain("rawdata");
        result.ErrorMessage.Should().Contain("never deleted");
        MockAssetClient.Verify(c => c.DeleteTenantAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task DeleteTenant_DisableFeatures_NothingEnabled_JustDeletes()
    {
        MockAssetClient.Setup(c => c.GetTenantFeaturesStatusAsync()).ReturnsAsync(Status(false, false, false, false));

        var result = await TenantManagementTools.DeleteTenant(MockServer.Object, Child, confirm: true,
            disableFeatures: true);

        result.IsSuccess.Should().BeTrue();
        result.DisabledFeatures.Should().BeEmpty();
        MockAssetClient.Verify(c => c.DeleteTenantAsync(Child), Times.Once);
    }

    [Fact]
    public async Task DeleteTenant_DisableFeatures_WithoutConfirm_IsRefused_BeforeAnything()
    {
        var result = await TenantManagementTools.DeleteTenant(MockServer.Object, Child, confirm: false,
            disableFeatures: true);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("confirm");
        MockAssetClient.Verify(c => c.GetTenantFeaturesStatusAsync(), Times.Never);
        MockAssetClient.Verify(c => c.DeleteTenantAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task DeleteTenant_DisableFeatures_NoTokenForTheChild_FailsAndDeletesNothing()
    {
        // A client-credentials session cannot be exchanged for a child tenant; the tool says so instead of falling
        // back to the parent token (which the capability endpoints refuse anyway).
        MockTokenExchanger
            .Setup(e => e.ExchangeForTenantAsync(It.IsAny<string>(), Child, It.IsAny<CancellationToken>()))
            .Returns(Task.FromResult<McpSessionTokens?>(null));

        var result = await TenantManagementTools.DeleteTenant(MockServer.Object, Child, confirm: true,
            disableFeatures: true);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain($"token for tenant '{Child}'");
        MockAssetClient.Verify(c => c.DeleteTenantAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task DeleteTenant_WithoutDisableFeatures_IsUnchanged()
    {
        var result = await TenantManagementTools.DeleteTenant(MockServer.Object, Child, confirm: true);

        result.IsSuccess.Should().BeTrue();
        result.DisabledFeatures.Should().BeEmpty();
        MockAssetClient.Verify(c => c.GetTenantFeaturesStatusAsync(), Times.Never);
        MockAssetClient.Verify(c => c.DeleteTenantAsync(Child), Times.Once);
    }

    // ----- enable_ai / disable_ai -----

    [Fact]
    public async Task EnableAi_CallsTheAiClientForTheResolvedTenant()
    {
        var result = await AiLifecycleTools.EnableAi(MockServer.Object);

        result.IsSuccess.Should().BeTrue();
        result.TenantId.Should().Be(DefaultTenantId);
        MockAiClient.Verify(c => c.EnableAsync(DefaultTenantId), Times.Once);
    }

    [Fact]
    public async Task EnableAi_Unauthenticated_ReturnsAuthError()
    {
        GivenUnauthenticated();

        var result = await AiLifecycleTools.EnableAi(MockServer.Object);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Not authenticated");
    }

    [Fact]
    public async Task EnableAi_ServiceRefuses_SurfacesTheReason()
    {
        MockAiClient.Setup(c => c.EnableAsync(DefaultTenantId))
            .ThrowsAsync(new InvalidOperationException("Conflict: Communication is not enabled"));

        var result = await AiLifecycleTools.EnableAi(MockServer.Object);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Communication is not enabled");
    }

    [Fact]
    public async Task DisableAi_WithoutConfirm_Refuses()
    {
        var result = await AiLifecycleTools.DisableAi(MockServer.Object);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("confirm=true");
        MockAiClient.Verify(c => c.DisableAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task DisableAi_WithConfirm_CallsTheAiClientForTheResolvedTenant()
    {
        var result = await AiLifecycleTools.DisableAi(MockServer.Object, confirm: true);

        result.IsSuccess.Should().BeTrue();
        result.TenantId.Should().Be(DefaultTenantId);
        MockAiClient.Verify(c => c.DisableAsync(DefaultTenantId), Times.Once);
    }
}
