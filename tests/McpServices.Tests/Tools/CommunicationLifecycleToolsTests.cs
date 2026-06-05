using FluentAssertions;
using Meshmakers.Octo.Backend.McpServices.Tools;
using Moq;
using Xunit;

namespace McpServices.Tests.Tools;

public class CommunicationLifecycleToolsTests : ToolTestBase
{
    public CommunicationLifecycleToolsTests()
    {
        GivenAuthenticated();
    }

    [Fact]
    public async Task EnableCommunication_HappyPath_CallsSdk()
    {
        var result = await CommunicationLifecycleTools.EnableCommunication(MockServer.Object);

        result.IsSuccess.Should().BeTrue();
        result.TargetTenantId.Should().Be(DefaultTenantId);
        MockCommunicationClient.Verify(c => c.EnableAsync(DefaultTenantId), Times.Once);
    }

    [Fact]
    public async Task EnableCommunication_Unauthenticated_ReturnsAuthError()
    {
        GivenUnauthenticated();

        var result = await CommunicationLifecycleTools.EnableCommunication(MockServer.Object);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Not authenticated");
        MockCommunicationClient.Verify(c => c.EnableAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task DisableCommunication_WithoutConfirm_Refuses()
    {
        var result = await CommunicationLifecycleTools.DisableCommunication(MockServer.Object);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("confirm=true");
        MockCommunicationClient.Verify(c => c.DisableAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task DisableCommunication_WithConfirm_CallsSdk()
    {
        var result = await CommunicationLifecycleTools.DisableCommunication(MockServer.Object, confirm: true);

        result.IsSuccess.Should().BeTrue();
        MockCommunicationClient.Verify(c => c.DisableAsync(DefaultTenantId), Times.Once);
    }

    [Fact]
    public async Task EnableCommunication_WhenSdkThrows_ReturnsErrorMessage()
    {
        MockCommunicationClient.Setup(c => c.EnableAsync(It.IsAny<string>()))
            .ThrowsAsync(new InvalidOperationException("comm down"));

        var result = await CommunicationLifecycleTools.EnableCommunication(MockServer.Object);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Be("comm down");
    }
}
