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

        var result = await AiSpendTools.SetAiSpendCap(MockServer.Object, "ai-sandbox", 20m, "pilot", confirm: true);

        result.IsSuccess.Should().BeTrue();
        sent!.CapMonthlyEur.Should().Be(20m);
        sent.KeySource.Should().Be("CentralKey");
        sent.Reason.Should().Be("pilot");
    }

    [Fact]
    public async Task SetAiSpendCap_RefusedByTheService_SurfacesTheReason()
    {
        MockAiClient.Setup(c => c.SetAiSpendCapAsync(It.IsAny<string>(), It.IsAny<SetAiSpendCapRequestDto>()))
            .ThrowsAsync(new InvalidOperationException("Forbidden: AiBudgetAdminRequired"));

        var result = await AiSpendTools.SetAiSpendCap(MockServer.Object, "ai-sandbox", 20m, "pilot", confirm: true);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("AiBudgetAdminRequired");
    }
}
