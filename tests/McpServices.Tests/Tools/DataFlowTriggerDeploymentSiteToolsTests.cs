using FluentAssertions;
using Meshmakers.Octo.Backend.McpServices.Tools;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Moq;
using Xunit;

namespace McpServices.Tests.Tools;

public class DataFlowTriggerDeploymentSiteToolsTests : ToolTestBase
{
    private const string DataFlowId = "cc0000000000000000000002";

    public DataFlowTriggerDeploymentSiteToolsTests()
    {
        GivenAuthenticated();
        _ = CkMethods; // the communication writes run as CK methods on the tenant client
    }

    private const string PoolId = "cc0000000000000000000009";

    // ── Data Flows ──────────────────────────────────────────────────────────

    [Fact]
    public async Task DeployDataFlow_HappyPath_CallsSdk()
    {
        var result = await DataFlowTriggerDeploymentSiteTools.DeployDataFlow(MockServer.Object, DataFlowId);

        result.IsSuccess.Should().BeTrue();
        CkMethods.OperationNames.Should().ContainSingle().Which.Should().Be("SystemCommunicationDataFlowDeploy");
        CkMethods.RtIdOf("SystemCommunicationDataFlowDeploy").Should().Be(DataFlowId);
        AssertNoCommunicationRestWrite();
    }

    [Fact]
    public async Task DeployDataFlow_MissingId_ReturnsValidationError()
    {
        var result = await DataFlowTriggerDeploymentSiteTools.DeployDataFlow(MockServer.Object, "");
        result.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task UndeployDataFlow_WithoutConfirm_Refuses()
    {
        var result = await DataFlowTriggerDeploymentSiteTools.UndeployDataFlow(MockServer.Object, DataFlowId);

        result.IsSuccess.Should().BeFalse();
        CkMethods.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task UndeployDataFlow_WithConfirm_CallsSdk()
    {
        var result = await DataFlowTriggerDeploymentSiteTools.UndeployDataFlow(MockServer.Object, DataFlowId, confirm: true);

        result.IsSuccess.Should().BeTrue();
        CkMethods.OperationNames.Should().ContainSingle().Which.Should().Be("SystemCommunicationDataFlowUndeploy");
        CkMethods.RtIdOf("SystemCommunicationDataFlowUndeploy").Should().Be(DataFlowId);
        AssertNoCommunicationRestWrite();
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

        var result = await DataFlowTriggerDeploymentSiteTools.GetDataFlowStatus(MockServer.Object, DataFlowId);

        result.IsSuccess.Should().BeTrue();
        result.Status.Should().NotBeNull();
        result.DataFlowId.Should().Be(DataFlowId);
    }

    [Fact]
    public async Task GetDataFlowStatus_MissingId_ReturnsValidationError()
    {
        var result = await DataFlowTriggerDeploymentSiteTools.GetDataFlowStatus(MockServer.Object, "");
        result.IsSuccess.Should().BeFalse();
    }

    // ── Triggers ────────────────────────────────────────────────────────────

    [Fact]
    public async Task DeployTriggers_HappyPath_CallsSdk()
    {
        var result = await DataFlowTriggerDeploymentSiteTools.DeployTriggers(MockServer.Object);

        result.IsSuccess.Should().BeTrue();
        CkMethods.OperationNames.Should().ContainSingle().Which.Should().Be("SystemCommunicationPipelineTriggerDeployAll");
        AssertNoCommunicationRestWrite();
    }

    [Fact]
    public async Task DeployTriggers_Unauthenticated_ReturnsAuthError()
    {
        GivenUnauthenticated();
        var result = await DataFlowTriggerDeploymentSiteTools.DeployTriggers(MockServer.Object);
        result.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task UndeployTriggers_WithoutConfirm_Refuses()
    {
        var result = await DataFlowTriggerDeploymentSiteTools.UndeployTriggers(MockServer.Object);

        result.IsSuccess.Should().BeFalse();
        CkMethods.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task UndeployTriggers_WithConfirm_CallsSdk()
    {
        var result = await DataFlowTriggerDeploymentSiteTools.UndeployTriggers(MockServer.Object, confirm: true);

        result.IsSuccess.Should().BeTrue();
        CkMethods.OperationNames.Should().ContainSingle().Which.Should().Be("SystemCommunicationPipelineTriggerUndeployAll");
        AssertNoCommunicationRestWrite();
    }

    // ── Pools ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetPools_HappyPath_ReturnsList()
    {
        MockCommunicationClient.Setup(c => c.GetDeploymentSitesAsync())
            .ReturnsAsync(new[]
            {
                new DeploymentSiteSummaryDto
                {
                    RtId = "pool-1",
                    Name = "Pool A",
                    CommunicationState = CommunicationState.Online,
                    ConfigurationState = ConfigurationState.Configured,
                    DeploymentState = EntityDeploymentState.Deployed
                }
            });

        var result = await DataFlowTriggerDeploymentSiteTools.GetDeploymentSites(MockServer.Object);

        result.IsSuccess.Should().BeTrue();
        result.DeploymentSites.Should().HaveCount(1);
    }

    [Fact]
    public async Task GetPools_Unauthenticated_ReturnsAuthError()
    {
        GivenUnauthenticated();
        var result = await DataFlowTriggerDeploymentSiteTools.GetDeploymentSites(MockServer.Object);
        result.IsSuccess.Should().BeFalse();
    }

    // ── Pools ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task UndeployPool_WithoutConfirm_Refuses()
    {
        var result = await DataFlowTriggerDeploymentSiteTools.UndeployDeploymentSite(MockServer.Object, PoolId);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("confirm=true");
        CkMethods.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task UndeployPool_WithConfirm_CallsSdk()
    {
        var result = await DataFlowTriggerDeploymentSiteTools.UndeployDeploymentSite(MockServer.Object, PoolId, confirm: true);

        result.IsSuccess.Should().BeTrue();
        result.ResourceId.Should().Be(PoolId);
        CkMethods.OperationNames.Should().ContainSingle().Which.Should().Be("SystemCommunicationDeploymentSiteUndeploy");
        CkMethods.RtIdOf("SystemCommunicationDeploymentSiteUndeploy").Should().Be(PoolId);
        AssertNoCommunicationRestWrite();
    }

    [Fact]
    public async Task UndeployPool_Unauthenticated_ReturnsAuthError()
    {
        GivenUnauthenticated();

        var result = await DataFlowTriggerDeploymentSiteTools.UndeployDeploymentSite(MockServer.Object, PoolId, confirm: true);

        result.IsSuccess.Should().BeFalse();
        CkMethods.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task UndeployPool_MissingId_ReturnsValidationError()
    {
        var result = await DataFlowTriggerDeploymentSiteTools.UndeployDeploymentSite(MockServer.Object, "", confirm: true);

        result.IsSuccess.Should().BeFalse();
        CkMethods.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task UndeployPool_WhenSdkThrows_ReturnsErrorMessage()
    {
        CkMethods.Fails("DEPLOYMENT_SITE_UNDEPLOY_FAILED", "The deployment site is not deployed.");

        var result = await DataFlowTriggerDeploymentSiteTools.UndeployDeploymentSite(MockServer.Object, PoolId, confirm: true);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Be("DEPLOYMENT_SITE_UNDEPLOY_FAILED: The deployment site is not deployed.");
        AssertNoCommunicationRestWrite();
    }
}
