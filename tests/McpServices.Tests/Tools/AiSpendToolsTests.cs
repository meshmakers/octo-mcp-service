using FluentAssertions;
using Meshmakers.Octo.Backend.McpServices.Tools;
using Meshmakers.Octo.Sdk.ServiceClient.AiServices;
using Moq;
using Xunit;

namespace McpServices.Tests.Tools;

/// <summary>AB#8690: <c>get_ai_spend</c> and <c>set_ai_spend_cap</c>.</summary>
public class AiSpendToolsTests : ToolTestBase
{
    public AiSpendToolsTests()
    {
        GivenAuthenticated();
    }

    [Fact]
    public async Task GetAiSpend_WithoutTarget_ReadsTheResolvedTenant()
    {
        MockAiClient.Setup(c => c.GetAiSpendAsync())
            .ReturnsAsync(new AiSpendReportDto { TenantId = DefaultTenantId, KeySource = "CentralKey", CapMonthlyEur = 20m });

        var result = await AiSpendTools.GetAiSpend(MockServer.Object);

        result.IsSuccess.Should().BeTrue();
        result.Spend!.CapMonthlyEur.Should().Be(20m);
        MockAiClient.Verify(c => c.GetAiSpendCapAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task GetAiSpend_WithTarget_UsesTheCapAdministration()
    {
        MockAiClient.Setup(c => c.GetAiSpendCapAsync("ai-sandbox"))
            .ReturnsAsync(new AiSpendReportDto { TenantId = "ai-sandbox" });

        var result = await AiSpendTools.GetAiSpend(MockServer.Object, targetTenantId: "ai-sandbox");

        result.IsSuccess.Should().BeTrue();
        MockAiClient.Verify(c => c.GetAiSpendAsync(), Times.Never);
    }

    [Fact]
    public async Task SetAiSpendCap_WithoutConfirm_IsRefusedBeforeAnyCall()
    {
        var result = await AiSpendTools.SetAiSpendCap(MockServer.Object, "ai-sandbox", 20m, "pilot");

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("confirm=true");
        MockAiClient.Verify(c => c.SetAiSpendCapAsync(It.IsAny<string>(), It.IsAny<SetAiSpendCapRequestDto>()),
            Times.Never);
    }

    [Theory]
    [InlineData(-1, "pilot")]
    [InlineData(5, " ")]
    public async Task SetAiSpendCap_InvalidInput_IsRefusedBeforeAnyCall(decimal cap, string reason)
    {
        var result = await AiSpendTools.SetAiSpendCap(MockServer.Object, "ai-sandbox", cap, reason, confirm: true);

        result.IsSuccess.Should().BeFalse();
        MockAiClient.Verify(c => c.SetAiSpendCapAsync(It.IsAny<string>(), It.IsAny<SetAiSpendCapRequestDto>()),
            Times.Never);
    }

    [Fact]
    public async Task SetAiSpendCap_Confirmed_SendsCapKeySourceAndReason()
    {
        SetAiSpendCapRequestDto? sent = null;
        MockAiClient.Setup(c => c.SetAiSpendCapAsync("ai-sandbox", It.IsAny<SetAiSpendCapRequestDto>()))
            .Callback((string _, SetAiSpendCapRequestDto r) => sent = r)
            .ReturnsAsync(new AiSpendCapChangeDto { TenantId = "ai-sandbox", NewKeySource = "CentralKey", NewCapMonthlyEur = 20m });

        var result = await AiSpendTools.SetAiSpendCap(MockServer.Object, "ai-sandbox", 20m, "pilot", confirm: true,
            keySource: "CentralKey");

        result.IsSuccess.Should().BeTrue();
        sent!.CapMonthlyEur.Should().Be(20m);
        sent.KeySource.Should().Be("CentralKey");
        sent.Reason.Should().Be("pilot");
    }

    [Fact]
    public async Task GetAiSpend_Unauthenticated_ReturnsAnErrorWithoutAnyCall()
    {
        GivenUnauthenticated();

        var result = await AiSpendTools.GetAiSpend(MockServer.Object);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Not authenticated");
        MockAiClient.Verify(c => c.GetAiSpendAsync(), Times.Never);
    }

    [Fact]
    public async Task SetAiSpendCap_Unauthenticated_ReturnsAnErrorWithoutAnyCall()
    {
        GivenUnauthenticated();

        var result = await AiSpendTools.SetAiSpendCap(MockServer.Object, "ai-sandbox", 20m, "pilot", confirm: true);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Not authenticated");
        MockAiClient.Verify(c => c.SetAiSpendCapAsync(It.IsAny<string>(), It.IsAny<SetAiSpendCapRequestDto>()),
            Times.Never);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("a/b")]
    [InlineData("a%2Fb")]
    public async Task SetAiSpendCap_MissingOrInvalidTarget_IsRefusedBeforeAnyCall(string target)
    {
        var result = await AiSpendTools.SetAiSpendCap(MockServer.Object, target, 20m, "pilot", confirm: true);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("targetTenantId");
        MockAiClient.Verify(c => c.SetAiSpendCapAsync(It.IsAny<string>(), It.IsAny<SetAiSpendCapRequestDto>()),
            Times.Never);
        MockAiClient.Verify(c => c.GetAiSpendCapAsync(It.IsAny<string>()), Times.Never);
    }

    [Theory]
    [InlineData(".")]
    [InlineData("..")]
    public async Task GetAiSpend_DotSegmentTarget_IsRefusedBeforeAnyCall(string target)
    {
        var result = await AiSpendTools.GetAiSpend(MockServer.Object, targetTenantId: target);

        result.IsSuccess.Should().BeFalse();
        MockAiClient.Verify(c => c.GetAiSpendCapAsync(It.IsAny<string>()), Times.Never);
        MockAiClient.Verify(c => c.GetAiSpendAsync(), Times.Never);
    }

    [Fact]
    public async Task SetAiSpendCap_ReasonOver500Characters_IsRefusedBeforeAnyCall()
    {
        var result = await AiSpendTools.SetAiSpendCap(MockServer.Object, "ai-sandbox", 20m, new string('r', 501),
            confirm: true);

        result.IsSuccess.Should().BeFalse();
        MockAiClient.Verify(c => c.SetAiSpendCapAsync(It.IsAny<string>(), It.IsAny<SetAiSpendCapRequestDto>()),
            Times.Never);
    }

    [Fact]
    public async Task SetAiSpendCap_UnknownKeySource_IsRefusedBeforeAnyCall()
    {
        var result = await AiSpendTools.SetAiSpendCap(MockServer.Object, "ai-sandbox", 20m, "pilot", confirm: true,
            keySource: "Free");

        result.IsSuccess.Should().BeFalse();
        MockAiClient.Verify(c => c.SetAiSpendCapAsync(It.IsAny<string>(), It.IsAny<SetAiSpendCapRequestDto>()),
            Times.Never);
    }

    [Theory]
    [InlineData("ByoKey", false, "ByoKey")]
    [InlineData("Subscription", false, "Subscription")]
    [InlineData("None", true, "None")]
    [InlineData("None", false, "CentralKey")]
    public async Task SetAiSpendCap_WithoutKeySource_KeepsTheStoredKeySource(string stored, bool hasEntry,
        string expected)
    {
        SetAiSpendCapRequestDto? sent = null;
        MockAiClient.Setup(c => c.GetAiSpendCapAsync("ai-sandbox")).ReturnsAsync(new AiSpendReportDto
        {
            TenantId = "ai-sandbox",
            KeySource = stored,
            CapChangedAt = hasEntry ? DateTimeOffset.UnixEpoch : null
        });
        MockAiClient.Setup(c => c.SetAiSpendCapAsync("ai-sandbox", It.IsAny<SetAiSpendCapRequestDto>()))
            .Callback((string _, SetAiSpendCapRequestDto r) => sent = r)
            .ReturnsAsync(new AiSpendCapChangeDto { TenantId = "ai-sandbox" });

        var result = await AiSpendTools.SetAiSpendCap(MockServer.Object, "ai-sandbox", 5m, "raise", confirm: true);

        result.IsSuccess.Should().BeTrue();
        sent!.KeySource.Should().Be(expected);
    }

    [Fact]
    public async Task SetAiSpendCap_ExplicitKeySource_IsCanonical_AndSkipsTheRead()
    {
        SetAiSpendCapRequestDto? sent = null;
        MockAiClient.Setup(c => c.SetAiSpendCapAsync("ai-sandbox", It.IsAny<SetAiSpendCapRequestDto>()))
            .Callback((string _, SetAiSpendCapRequestDto r) => sent = r)
            .ReturnsAsync(new AiSpendCapChangeDto { TenantId = "ai-sandbox" });

        var result = await AiSpendTools.SetAiSpendCap(MockServer.Object, "ai-sandbox", 5m, "own key", confirm: true,
            keySource: "byokey");

        result.IsSuccess.Should().BeTrue();
        sent!.KeySource.Should().Be("ByoKey");
        MockAiClient.Verify(c => c.GetAiSpendCapAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task GetAiSpend_ServiceFailure_SurfacesTheError()
    {
        MockAiClient.Setup(c => c.GetAiSpendAsync())
            .ThrowsAsync(new InvalidOperationException("Service unavailable"));

        var result = await AiSpendTools.GetAiSpend(MockServer.Object);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Service unavailable");
    }

    [Fact]
    public async Task SetAiSpendCap_RefusedByTheService_SurfacesTheReason()
    {
        MockAiClient.Setup(c => c.SetAiSpendCapAsync(It.IsAny<string>(), It.IsAny<SetAiSpendCapRequestDto>()))
            .ThrowsAsync(new InvalidOperationException("Forbidden: AiBudgetAdminRequired"));

        var result = await AiSpendTools.SetAiSpendCap(MockServer.Object, "ai-sandbox", 20m, "pilot", confirm: true,
            keySource: "CentralKey");

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("AiBudgetAdminRequired");
    }
}
