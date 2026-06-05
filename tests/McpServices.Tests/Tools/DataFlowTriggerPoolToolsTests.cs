using FluentAssertions;
using Meshmakers.Octo.Backend.McpServices.Tools;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Moq;
using Xunit;

namespace McpServices.Tests.Tools;

public class DataFlowTriggerPoolToolsTests : ToolTestBase
{
    private const string DataFlowId = "cc0000000000000000000002";

    public DataFlowTriggerPoolToolsTests()
    {
        GivenAuthenticated();
    }

    // ── Data Flows ──────────────────────────────────────────────────────────

    [Fact]
    public async Task DeployDataFlow_HappyPath_CallsSdk()
    {
        var result = await DataFlowTriggerPoolTools.DeployDataFlow(MockServer.Object, DataFlowId);

        result.IsSuccess.Should().BeTrue();
        MockCommunicationClient.Verify(c => c.DeployDataFlowAsync(DataFlowId), Times.Once);
    }

    [Fact]
    public async Task DeployDataFlow_MissingId_ReturnsValidationError()
    {
        var result = await DataFlowTriggerPoolTools.DeployDataFlow(MockServer.Object, "");
        result.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task UndeployDataFlow_WithoutConfirm_Refuses()
    {
        var result = await DataFlowTriggerPoolTools.UndeployDataFlow(MockServer.Object, DataFlowId);

        result.IsSuccess.Should().BeFalse();
        MockCommunicationClient.Verify(c => c.UndeployDataFlowAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task UndeployDataFlow_WithConfirm_CallsSdk()
    {
        var result = await DataFlowTriggerPoolTools.UndeployDataFlow(MockServer.Object, DataFlowId, confirm: true);

        result.IsSuccess.Should().BeTrue();
        MockCommunicationClient.Verify(c => c.UndeployDataFlowAsync(DataFlowId), Times.Once);
    }

    [Fact]
    public async Task GetDataFlowStatus_HappyPath_ReturnsStatus()
    {
        MockCommunicationClient.Setup(c => c.GetDataFlowStatusAsync(DataFlowId))
            .ReturnsAsync(new DataFlowStatusDto
            {
                DataFlowRtId = new OctoObjectId(DataFlowId),
                State = DataFlowExecutionState.Idle,
                Pipelines = []
            });

        var result = await DataFlowTriggerPoolTools.GetDataFlowStatus(MockServer.Object, DataFlowId);

        result.IsSuccess.Should().BeTrue();
        result.Status.Should().NotBeNull();
        result.DataFlowId.Should().Be(DataFlowId);
    }

    [Fact]
    public async Task GetDataFlowStatus_MissingId_ReturnsValidationError()
    {
        var result = await DataFlowTriggerPoolTools.GetDataFlowStatus(MockServer.Object, "");
        result.IsSuccess.Should().BeFalse();
    }

    // ── Triggers ────────────────────────────────────────────────────────────

    [Fact]
    public async Task DeployTriggers_HappyPath_CallsSdk()
    {
        var result = await DataFlowTriggerPoolTools.DeployTriggers(MockServer.Object);

        result.IsSuccess.Should().BeTrue();
        MockCommunicationClient.Verify(c => c.DeployTriggersAsync(), Times.Once);
    }

    [Fact]
    public async Task DeployTriggers_Unauthenticated_ReturnsAuthError()
    {
        GivenUnauthenticated();
        var result = await DataFlowTriggerPoolTools.DeployTriggers(MockServer.Object);
        result.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task UndeployTriggers_WithoutConfirm_Refuses()
    {
        var result = await DataFlowTriggerPoolTools.UndeployTriggers(MockServer.Object);

        result.IsSuccess.Should().BeFalse();
        MockCommunicationClient.Verify(c => c.UndeployTriggersAsync(), Times.Never);
    }

    [Fact]
    public async Task UndeployTriggers_WithConfirm_CallsSdk()
    {
        var result = await DataFlowTriggerPoolTools.UndeployTriggers(MockServer.Object, confirm: true);

        result.IsSuccess.Should().BeTrue();
        MockCommunicationClient.Verify(c => c.UndeployTriggersAsync(), Times.Once);
    }

    // ── Pools ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetPools_HappyPath_ReturnsList()
    {
        MockCommunicationClient.Setup(c => c.GetPoolsAsync())
            .ReturnsAsync(new[]
            {
                new PoolSummaryDto
                {
                    RtId = "pool-1",
                    Name = "Pool A",
                    CommunicationState = CommunicationState.Online,
                    ConfigurationState = ConfigurationState.Configured,
                    DeploymentState = EntityDeploymentState.Deployed
                }
            });

        var result = await DataFlowTriggerPoolTools.GetPools(MockServer.Object);

        result.IsSuccess.Should().BeTrue();
        result.Pools.Should().HaveCount(1);
    }

    [Fact]
    public async Task GetPools_Unauthenticated_ReturnsAuthError()
    {
        GivenUnauthenticated();
        var result = await DataFlowTriggerPoolTools.GetPools(MockServer.Object);
        result.IsSuccess.Should().BeFalse();
    }
}
