using FluentAssertions;
using Meshmakers.Octo.Backend.McpServices.Tools;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Moq;
using Xunit;

namespace McpServices.Tests.Tools;

public class WorkloadToolsTests : ToolTestBase
{
    private const string WorkloadId = "wl-123";
    private const string AdapterId = "69cfa838092b710403248acd";

    public WorkloadToolsTests()
    {
        GivenAuthenticated();
    }

    [Fact]
    public async Task GetWorkloadsByChart_HappyPath_ReturnsList()
    {
        MockCommunicationClient.Setup(c => c.GetWorkloadsByChartAsync("octo-mesh-adapter"))
            .ReturnsAsync(new[]
            {
                new WorkloadSummaryDto(WorkloadId, "W1", "Sys-1/Workload-1",
                    "octo-mesh-adapter", "1.2.3", "Deployed")
            });

        var result = await WorkloadTools.GetWorkloadsByChart(MockServer.Object, "octo-mesh-adapter");

        result.IsSuccess.Should().BeTrue();
        result.Workloads.Should().HaveCount(1);
        result.ChartName.Should().Be("octo-mesh-adapter");
    }

    [Fact]
    public async Task GetWorkloadsByChart_NoMatch_ReturnsEmptyWithSkipHint()
    {
        MockCommunicationClient.Setup(c => c.GetWorkloadsByChartAsync("nonexistent"))
            .ReturnsAsync([]);

        var result = await WorkloadTools.GetWorkloadsByChart(MockServer.Object, "nonexistent");

        result.IsSuccess.Should().BeTrue();
        result.Workloads.Should().BeEmpty();
        result.Message.Should().Contain("No workloads use chart 'nonexistent'");
    }

    [Fact]
    public async Task GetWorkloadsByChart_MissingArg_ReturnsValidationError()
    {
        var result = await WorkloadTools.GetWorkloadsByChart(MockServer.Object, "");
        result.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task UpdateWorkloadChartVersion_HappyPath_CallsSdk()
    {
        var result = await WorkloadTools.UpdateWorkloadChartVersion(MockServer.Object,
            WorkloadId, "1.2.4");

        result.IsSuccess.Should().BeTrue();
        result.ResourceId.Should().Be(WorkloadId);
        MockCommunicationClient.Verify(c => c.UpdateWorkloadChartVersionAsync(WorkloadId, "1.2.4"), Times.Once);
    }

    [Fact]
    public async Task UpdateWorkloadChartVersion_MissingArgs_ReturnsValidationError()
    {
        var result = await WorkloadTools.UpdateWorkloadChartVersion(MockServer.Object, "", "");
        result.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task DeployWorkload_HappyPath_CallsSdk()
    {
        var result = await WorkloadTools.DeployWorkload(MockServer.Object, WorkloadId);

        result.IsSuccess.Should().BeTrue();
        MockCommunicationClient.Verify(c => c.DeployWorkloadAsync(WorkloadId), Times.Once);
    }

    [Fact]
    public async Task UndeployWorkload_WithoutConfirm_Refuses()
    {
        var result = await WorkloadTools.UndeployWorkload(MockServer.Object, WorkloadId);

        result.IsSuccess.Should().BeFalse();
        MockCommunicationClient.Verify(c => c.UndeployWorkloadAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task UndeployWorkload_WithConfirm_CallsSdk()
    {
        var result = await WorkloadTools.UndeployWorkload(MockServer.Object, WorkloadId, confirm: true);

        result.IsSuccess.Should().BeTrue();
        MockCommunicationClient.Verify(c => c.UndeployWorkloadAsync(WorkloadId), Times.Once);
    }

    [Fact]
    public async Task MovePipelines_WithoutConfirm_Refuses()
    {
        var result = await WorkloadTools.MovePipelines(MockServer.Object,
            ["p1", "p2"], AdapterId);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("confirm=true");
        MockCommunicationClient.Verify(c => c.MovePipelinesToAdapterAsync(
            It.IsAny<MovePipelinesToAdapterRequestDto>()), Times.Never);
    }

    [Fact]
    public async Task MovePipelines_EmptyList_ReturnsValidationError()
    {
        var result = await WorkloadTools.MovePipelines(MockServer.Object, [], AdapterId, confirm: true);
        result.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task MovePipelines_NullList_ReturnsValidationError()
    {
        var result = await WorkloadTools.MovePipelines(MockServer.Object, null!, AdapterId, confirm: true);
        result.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task MovePipelines_AllSuccess_ReturnsCleanMessage()
    {
        MockCommunicationClient.Setup(c => c.MovePipelinesToAdapterAsync(
                It.Is<MovePipelinesToAdapterRequestDto>(r =>
                    r.PipelineRtIds.Count == 2 && r.TargetAdapterRtId == AdapterId && r.Redeploy == false)))
            .ReturnsAsync(new MovePipelinesToAdapterResponseDto(
            [
                new MovePipelineResultDto("p1", true, "old-a", AdapterId, null),
                new MovePipelineResultDto("p2", true, "old-a", AdapterId, null)
            ]));

        var result = await WorkloadTools.MovePipelines(MockServer.Object,
            ["p1", "p2"], AdapterId, confirm: true);

        result.IsSuccess.Should().BeTrue();
        result.SuccessCount.Should().Be(2);
        result.FailureCount.Should().Be(0);
        result.Message.Should().NotContain("failed");
    }

    [Fact]
    public async Task MovePipelines_PartialFailure_ReportsCounts()
    {
        MockCommunicationClient.Setup(c => c.MovePipelinesToAdapterAsync(
                It.IsAny<MovePipelinesToAdapterRequestDto>()))
            .ReturnsAsync(new MovePipelinesToAdapterResponseDto(
            [
                new MovePipelineResultDto("p1", true, "old-a", AdapterId, null),
                new MovePipelineResultDto("p2", false, "old-a", null, "CkTypeId mismatch")
            ]));

        var result = await WorkloadTools.MovePipelines(MockServer.Object,
            ["p1", "p2"], AdapterId, confirm: true);

        result.IsSuccess.Should().BeTrue();
        result.SuccessCount.Should().Be(1);
        result.FailureCount.Should().Be(1);
        result.Message.Should().Contain("failed");
    }

    [Fact]
    public async Task MovePipelines_WithRedeploy_PassesFlag()
    {
        MockCommunicationClient.Setup(c => c.MovePipelinesToAdapterAsync(
                It.IsAny<MovePipelinesToAdapterRequestDto>()))
            .ReturnsAsync(new MovePipelinesToAdapterResponseDto([]));

        await WorkloadTools.MovePipelines(MockServer.Object,
            ["p1"], AdapterId, redeploy: true, confirm: true);

        MockCommunicationClient.Verify(c => c.MovePipelinesToAdapterAsync(
            It.Is<MovePipelinesToAdapterRequestDto>(r => r.Redeploy == true)), Times.Once);
    }
}
