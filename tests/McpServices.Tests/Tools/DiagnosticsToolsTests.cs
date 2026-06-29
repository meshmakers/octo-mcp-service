using FluentAssertions;
using Meshmakers.Octo.Backend.McpServices.Tools;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Moq;
using Xunit;

namespace McpServices.Tests.Tools;

public class DiagnosticsToolsTests : ToolTestBase
{
    public DiagnosticsToolsTests()
    {
        GivenAuthenticated();
    }

    [Fact]
    public async Task ReconfigureLogLevel_Identity_DispatchesToIdentityClient()
    {
        var result = await DiagnosticsTools.ReconfigureLogLevel(MockServer.Object,
            serviceName: "Identity",
            loggerName: "Meshmakers.*",
            minLogLevel: LogLevelDto.Debug,
            maxLogLevel: LogLevelDto.Error);

        result.IsSuccess.Should().BeTrue();
        MockIdentityClient.Verify(c => c.ReconfigureLogLevelAsync(
            "Meshmakers.*", LogLevelDto.Debug, LogLevelDto.Error), Times.Once);
        MockAssetClient.Verify(c => c.ReconfigureLogLevelAsync(
            It.IsAny<string>(), It.IsAny<LogLevelDto>(), It.IsAny<LogLevelDto>()), Times.Never);
    }

    [Fact]
    public async Task ReconfigureLogLevel_AssetRepository_DispatchesToAssetClient()
    {
        var result = await DiagnosticsTools.ReconfigureLogLevel(MockServer.Object,
            "AssetRepository", "*", LogLevelDto.Info, LogLevelDto.Error);

        result.IsSuccess.Should().BeTrue();
        MockAssetClient.Verify(c => c.ReconfigureLogLevelAsync(
            "*", LogLevelDto.Info, LogLevelDto.Error), Times.Once);
    }

    [Fact]
    public async Task ReconfigureLogLevel_Communication_DispatchesToCommunicationClient()
    {
        var result = await DiagnosticsTools.ReconfigureLogLevel(MockServer.Object,
            "Communication", "*", LogLevelDto.Trace, LogLevelDto.Warn);

        result.IsSuccess.Should().BeTrue();
        MockCommunicationClient.Verify(c => c.ReconfigureLogLevelAsync(
            "*", LogLevelDto.Trace, LogLevelDto.Warn), Times.Once);
    }

    [Fact]
    public async Task ReconfigureLogLevel_Reporting_DispatchesToReportingClient()
    {
        var result = await DiagnosticsTools.ReconfigureLogLevel(MockServer.Object,
            "Reporting", "*", LogLevelDto.Warn, LogLevelDto.Error);

        result.IsSuccess.Should().BeTrue();
        MockReportingClient.Verify(c => c.ReconfigureLogLevelAsync(
            "*", LogLevelDto.Warn, LogLevelDto.Error), Times.Once);
    }

    [Fact]
    public async Task ReconfigureLogLevel_ServiceNameCaseInsensitive_StillWorks()
    {
        var result = await DiagnosticsTools.ReconfigureLogLevel(MockServer.Object,
            "identity", "*", LogLevelDto.Info, LogLevelDto.Error);

        result.IsSuccess.Should().BeTrue();
        MockIdentityClient.Verify(c => c.ReconfigureLogLevelAsync(
            It.IsAny<string>(), It.IsAny<LogLevelDto>(), It.IsAny<LogLevelDto>()), Times.Once);
    }

    [Fact]
    public async Task ReconfigureLogLevel_Bot_DispatchesToBotClient()
    {
        var result = await DiagnosticsTools.ReconfigureLogLevel(MockServer.Object,
            "Bot", "*", LogLevelDto.Info, LogLevelDto.Error);

        result.IsSuccess.Should().BeTrue();
        MockBotClient.Verify(c => c.ReconfigureLogLevelAsync(
            "*", LogLevelDto.Info, LogLevelDto.Error), Times.Once);
        MockClientFactory.Verify(f => f.CreateBotClient(It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task ReconfigureLogLevel_UnknownService_ReturnsValidationError()
    {
        var result = await DiagnosticsTools.ReconfigureLogLevel(MockServer.Object,
            "Frobnicator", "*", LogLevelDto.Info, LogLevelDto.Error);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Unknown serviceName");
    }

    [Fact]
    public async Task ReconfigureLogLevel_Unauthenticated_ReturnsAuthError()
    {
        GivenUnauthenticated();
        var result = await DiagnosticsTools.ReconfigureLogLevel(MockServer.Object,
            "Identity", "*", LogLevelDto.Info, LogLevelDto.Error);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Not authenticated");
    }

    [Fact]
    public async Task ReconfigureLogLevel_MissingArgs_ReturnsValidationError()
    {
        var result = await DiagnosticsTools.ReconfigureLogLevel(MockServer.Object,
            "", "", LogLevelDto.Info, LogLevelDto.Error);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("required");
    }
}
