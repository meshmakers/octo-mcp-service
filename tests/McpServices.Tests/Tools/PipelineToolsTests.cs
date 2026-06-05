using FluentAssertions;
using Meshmakers.Octo.Backend.McpServices.Tools;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Moq;
using Xunit;

namespace McpServices.Tests.Tools;

public class PipelineToolsTests : ToolTestBase
{
    private const string AdapterId = "69cfa838092b710403248acd";
    private const string PipelineId = "cc0000000000000000000003";

    public PipelineToolsTests()
    {
        GivenAuthenticated();
    }

    [Fact]
    public async Task GetPipelineStatus_HappyPath_ReturnsDeploymentResult()
    {
        MockCommunicationClient.Setup(c => c.GetPipelineDeploymentStateAsync(PipelineId))
            .ReturnsAsync(new DeploymentResultDto(
                new RtEntityId(new RtCkId<CkTypeId>("Sys-1/Pipeline-1"), new OctoObjectId(PipelineId)),
                DeploymentState.Success,
                stateMessage: "ok"));

        var result = await PipelineTools.GetPipelineStatus(MockServer.Object, PipelineId);

        result.IsSuccess.Should().BeTrue();
        result.DeploymentResult.Should().NotBeNull();
        result.PipelineId.Should().Be(PipelineId);
    }

    [Fact]
    public async Task GetPipelineStatus_MissingId_ReturnsValidationError()
    {
        var result = await PipelineTools.GetPipelineStatus(MockServer.Object, "");
        result.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task DeployPipeline_HappyPath_PassesAllArgs()
    {
        var result = await PipelineTools.DeployPipeline(MockServer.Object,
            AdapterId, PipelineId, "name: my-pipeline\nnodes: []");

        result.IsSuccess.Should().BeTrue();
        MockCommunicationClient.Verify(c => c.DeployPipelineAsync(AdapterId, PipelineId,
            "name: my-pipeline\nnodes: []"), Times.Once);
    }

    [Fact]
    public async Task DeployPipeline_MissingDefinition_ReturnsValidationError()
    {
        var result = await PipelineTools.DeployPipeline(MockServer.Object, AdapterId, PipelineId, "");
        result.IsSuccess.Should().BeFalse();
        MockCommunicationClient.Verify(c => c.DeployPipelineAsync(It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task DeployPipeline_Unauthenticated_ReturnsAuthError()
    {
        GivenUnauthenticated();
        var result = await PipelineTools.DeployPipeline(MockServer.Object,
            AdapterId, PipelineId, "{}");
        result.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task ExecutePipeline_HappyPath_ReturnsExecutionId()
    {
        MockCommunicationClient.Setup(c => c.ExecutePipelineAsync(PipelineId, null))
            .ReturnsAsync("exec-1234");

        var result = await PipelineTools.ExecutePipeline(MockServer.Object, PipelineId);

        result.IsSuccess.Should().BeTrue();
        result.ExecutionId.Should().Be("exec-1234");
    }

    [Fact]
    public async Task ExecutePipeline_WithInput_PassesInput()
    {
        MockCommunicationClient.Setup(c => c.ExecutePipelineAsync(PipelineId, "{\"k\":1}"))
            .ReturnsAsync("exec-2");

        var result = await PipelineTools.ExecutePipeline(MockServer.Object, PipelineId, "{\"k\":1}");

        result.IsSuccess.Should().BeTrue();
        result.ExecutionId.Should().Be("exec-2");
    }

    [Fact]
    public async Task SetPipelineDebug_Enable_CallsSdkWithTrue()
    {
        MockCommunicationClient.Setup(c => c.SetPipelineDebuggingAsync(PipelineId, true))
            .ReturnsAsync(new SetPipelineDebugResultDto(true, true));

        var result = await PipelineTools.SetPipelineDebug(MockServer.Object, PipelineId, true);

        result.IsSuccess.Should().BeTrue();
        result.Result!.Enabled.Should().BeTrue();
        result.Result.AppliedToRunningAdapter.Should().BeTrue();
    }

    [Fact]
    public async Task SetPipelineDebug_Disable_CallsSdkWithFalse()
    {
        MockCommunicationClient.Setup(c => c.SetPipelineDebuggingAsync(PipelineId, false))
            .ReturnsAsync(new SetPipelineDebugResultDto(false, false));

        var result = await PipelineTools.SetPipelineDebug(MockServer.Object, PipelineId, false);

        result.IsSuccess.Should().BeTrue();
        MockCommunicationClient.Verify(c => c.SetPipelineDebuggingAsync(PipelineId, false), Times.Once);
    }

    [Fact]
    public async Task GetPipelineDebug_HappyPath_ReturnsState()
    {
        MockCommunicationClient.Setup(c => c.GetPipelineDebuggingAsync(PipelineId))
            .ReturnsAsync(new PipelineDebugStateDto(true));

        var result = await PipelineTools.GetPipelineDebug(MockServer.Object, PipelineId);

        result.IsSuccess.Should().BeTrue();
        result.State!.Enabled.Should().BeTrue();
    }

    [Fact]
    public async Task GetPipelineExecutions_HappyPath_ReturnsList()
    {
        MockCommunicationClient.Setup(c => c.GetPipelineExecutionsAsync(PipelineId))
            .ReturnsAsync(new[]
            {
                new PipelineExecutionDataDto { Id = Guid.NewGuid(), DateTime = DateTime.UtcNow }
            });

        var result = await PipelineTools.GetPipelineExecutions(MockServer.Object, PipelineId);

        result.IsSuccess.Should().BeTrue();
        result.Executions.Should().HaveCount(1);
    }

    [Fact]
    public async Task GetLatestPipelineExecution_HappyPath_ReturnsLatest()
    {
        var execId = Guid.NewGuid();
        MockCommunicationClient.Setup(c => c.GetLatestPipelineExecutionAsync(PipelineId))
            .ReturnsAsync(new PipelineExecutionDataDto { Id = execId, DateTime = DateTime.UtcNow });

        var result = await PipelineTools.GetLatestPipelineExecution(MockServer.Object, PipelineId);

        result.IsSuccess.Should().BeTrue();
        result.Execution!.Id.Should().Be(execId);
    }

    [Fact]
    public async Task GetPipelineDebugPoints_HappyPath_ReturnsJson()
    {
        var execId = Guid.NewGuid();
        MockCommunicationClient.Setup(c => c.GetPipelineExecutionDebugPointsAsync(PipelineId, execId))
            .ReturnsAsync("[{\"node\":\"n1\"}]");

        var result = await PipelineTools.GetPipelineDebugPoints(MockServer.Object, PipelineId, execId);

        result.IsSuccess.Should().BeTrue();
        result.DebugPointsJson.Should().Contain("n1");
        result.ExecutionId.Should().Be(execId);
    }
}
