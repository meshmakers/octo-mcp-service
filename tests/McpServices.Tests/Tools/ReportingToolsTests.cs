using FluentAssertions;
using Meshmakers.Octo.Backend.McpServices.Tools;
using Moq;
using Xunit;

namespace McpServices.Tests.Tools;

public class ReportingToolsTests : ToolTestBase
{
    public ReportingToolsTests()
    {
        GivenAuthenticated();
    }

    [Fact]
    public async Task EnableReporting_HappyPath_CallsSdk()
    {
        var result = await ReportingTools.EnableReporting(MockServer.Object);

        result.IsSuccess.Should().BeTrue();
        MockReportingClient.Verify(c => c.EnableAsync(DefaultTenantId), Times.Once);
    }

    [Fact]
    public async Task EnableReporting_Unauthenticated_ReturnsAuthError()
    {
        GivenUnauthenticated();
        var result = await ReportingTools.EnableReporting(MockServer.Object);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Not authenticated");
    }

    [Fact]
    public async Task DisableReporting_WithoutConfirm_Refuses()
    {
        var result = await ReportingTools.DisableReporting(MockServer.Object);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("confirm=true");
        MockReportingClient.Verify(c => c.DisableAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task DisableReporting_WithConfirm_CallsSdk()
    {
        var result = await ReportingTools.DisableReporting(MockServer.Object, confirm: true);

        result.IsSuccess.Should().BeTrue();
        MockReportingClient.Verify(c => c.DisableAsync(DefaultTenantId), Times.Once);
    }
}
