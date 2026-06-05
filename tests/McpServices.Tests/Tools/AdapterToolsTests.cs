using FluentAssertions;
using Meshmakers.Octo.Backend.McpServices.Tools;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Moq;
using Xunit;

namespace McpServices.Tests.Tools;

public class AdapterToolsTests : ToolTestBase
{
    private const string AdapterId = "69cfa838092b710403248acd";

    public AdapterToolsTests()
    {
        GivenAuthenticated();
    }

    private static AdapterSummaryDto MakeAdapter(string rtId, string name) => new()
    {
        RtId = rtId,
        Name = name,
        CommunicationState = CommunicationState.Online,
        ConfigurationState = ConfigurationState.Configured,
        DeploymentState = EntityDeploymentState.Deployed
    };

    [Fact]
    public async Task GetAdapters_HappyPath_ReturnsList()
    {
        MockCommunicationClient.Setup(c => c.GetAdaptersAsync())
            .ReturnsAsync(new[] { MakeAdapter(AdapterId, "A1") });

        var result = await AdapterTools.GetAdapters(MockServer.Object);

        result.IsSuccess.Should().BeTrue();
        result.Adapters.Should().HaveCount(1);
        result.TotalCount.Should().Be(1);
    }

    [Fact]
    public async Task GetAdapters_Unauthenticated_ReturnsAuthError()
    {
        GivenUnauthenticated();
        var result = await AdapterTools.GetAdapters(MockServer.Object);
        result.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task GetAdapter_HappyPath_ReturnsConfig()
    {
        var config = new AdapterConfigurationDto(
            new RtEntityId(new RtCkId<CkTypeId>("Sys-1/Adapter-1"), new OctoObjectId(AdapterId)),
            "{}",
            []);
        MockCommunicationClient.Setup(c => c.GetAdapterConfigurationAsync(AdapterId))
            .ReturnsAsync(config);

        var result = await AdapterTools.GetAdapter(MockServer.Object, AdapterId);

        result.IsSuccess.Should().BeTrue();
        result.Adapter.Should().NotBeNull();
        result.AdapterId.Should().Be(AdapterId);
    }

    [Fact]
    public async Task GetAdapter_MissingId_ReturnsValidationError()
    {
        var result = await AdapterTools.GetAdapter(MockServer.Object, "");

        result.IsSuccess.Should().BeFalse();
        MockCommunicationClient.Verify(c => c.GetAdapterConfigurationAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task GetAdapterNodes_HappyPath_ReturnsJson()
    {
        MockCommunicationClient.Setup(c => c.GetAdapterNodesAsync())
            .ReturnsAsync("[{\"id\":\"n1\"}]");

        var result = await AdapterTools.GetAdapterNodes(MockServer.Object);

        result.IsSuccess.Should().BeTrue();
        result.NodesJson.Should().Contain("n1");
    }

    [Fact]
    public async Task GetPipelineSchema_HappyPath_ReturnsSchema()
    {
        MockCommunicationClient.Setup(c => c.GetPipelineSchemaAsync(AdapterId))
            .ReturnsAsync("{\"$schema\":\"http://json-schema.org/draft-07/schema#\"}");

        var result = await AdapterTools.GetPipelineSchema(MockServer.Object, AdapterId);

        result.IsSuccess.Should().BeTrue();
        result.SchemaJson.Should().Contain("$schema");
        result.AdapterId.Should().Be(AdapterId);
    }

    [Fact]
    public async Task GetPipelineSchema_MissingId_ReturnsValidationError()
    {
        var result = await AdapterTools.GetPipelineSchema(MockServer.Object, "");
        result.IsSuccess.Should().BeFalse();
    }
}
