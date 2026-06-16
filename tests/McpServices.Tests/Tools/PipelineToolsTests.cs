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
        MockCommunicationClient.Setup(c => c.ExecutePipelineAsync(PipelineId, null, false))
            .ReturnsAsync("exec-1234");

        var result = await PipelineTools.ExecutePipeline(MockServer.Object, PipelineId);

        result.IsSuccess.Should().BeTrue();
        result.ExecutionId.Should().Be("exec-1234");
    }

    [Fact]
    public async Task ExecutePipeline_WithInput_PassesInput()
    {
        MockCommunicationClient.Setup(c => c.ExecutePipelineAsync(PipelineId, "{\"k\":1}", false))
            .ReturnsAsync("exec-2");

        var result = await PipelineTools.ExecutePipeline(MockServer.Object, PipelineId, "{\"k\":1}");

        result.IsSuccess.Should().BeTrue();
        result.ExecutionId.Should().Be("exec-2");
    }

    [Fact]
    public async Task DryRunPipeline_HappyPath_CallsSdkWithIsDryRunTrue()
    {
        MockCommunicationClient.Setup(c => c.ExecutePipelineAsync(PipelineId, null, true))
            .ReturnsAsync("dryrun-1");

        var result = await PipelineTools.DryRunPipeline(MockServer.Object, PipelineId);

        result.IsSuccess.Should().BeTrue();
        result.ExecutionId.Should().Be("dryrun-1");
        result.SdkHonouredLoadNodes.Should().NotBeNullOrEmpty();
        result.SdkHonouredLoadNodes!.Should().Contain("ApplyChanges@1");
        MockCommunicationClient.Verify(c => c.ExecutePipelineAsync(PipelineId, null, true), Times.Once);
        MockCommunicationClient.Verify(c => c.ExecutePipelineAsync(It.IsAny<string>(), It.IsAny<string>(), false),
            Times.Never);
    }

    [Fact]
    public async Task DryRunPipeline_WithInput_PassesInputAndIsDryRunTrue()
    {
        MockCommunicationClient.Setup(c => c.ExecutePipelineAsync(PipelineId, "{\"k\":1}", true))
            .ReturnsAsync("dryrun-2");

        var result = await PipelineTools.DryRunPipeline(MockServer.Object, PipelineId, "{\"k\":1}");

        result.IsSuccess.Should().BeTrue();
        result.ExecutionId.Should().Be("dryrun-2");
    }

    [Fact]
    public async Task DryRunPipeline_MissingPipelineId_ReturnsValidationError()
    {
        var result = await PipelineTools.DryRunPipeline(MockServer.Object, string.Empty);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("pipelineId");
        MockCommunicationClient.Verify(c => c.ExecutePipelineAsync(It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public async Task DryRunPipeline_Unauthenticated_ReturnsAuthError()
    {
        GivenUnauthenticated();

        var result = await PipelineTools.DryRunPipeline(MockServer.Object, PipelineId);

        result.IsSuccess.Should().BeFalse();
        MockCommunicationClient.Verify(c => c.ExecutePipelineAsync(It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public void DryRunPipeline_SdkHonouredCatalog_HasExpectedSize()
    {
        // Pin the catalog size — Commit 2's DryRunHonouredLoadNodes.All in octo-mesh-adapter
        // ships exactly 10 retrofitted Load nodes; this MCP-side mirror must match.
        PipelineTools.SdkHonouredLoadNodesCatalog.Should().HaveCount(10);
        PipelineTools.SdkHonouredLoadNodesCatalog.Should().BeEquivalentTo(new[]
        {
            "ApplyChanges@1", "ApplyChanges@2", "DeployPipeline@1", "SendEMail@1",
            "GrafanaProvisionTenant@1", "GrafanaDeprovisionTenant@1",
            "SaveStreamDataInArchive@1", "SaveTimeRangeStreamDataInArchive@1",
            "SftpUpload@1", "ToDiscord@1"
        });
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

    // ===== validate_pipeline_definition (M4-B.1) ==================================
    // Tool composes get_pipeline_schema + a JSON-Schema validator (JsonSchema.Net).
    // Mock the schema endpoint with a minimal-but-real JSON Schema; assert the tool
    // distinguishes "tool call succeeded" (IsSuccess) from "definition is valid"
    // (IsValid).

    private const string MinimalPipelineSchema = """
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "type": "object",
          "required": ["name", "nodes"],
          "properties": {
            "name":  { "type": "string" },
            "nodes": {
              "type": "array",
              "items": {
                "type": "object",
                "required": ["nodeType", "id"],
                "properties": {
                  "nodeType": { "type": "string" },
                  "id":       { "type": "string" }
                }
              }
            }
          }
        }
        """;

    [Fact]
    public async Task ValidatePipelineDefinition_ValidYaml_ReturnsIsValidTrueWithNodeCount()
    {
        MockCommunicationClient.Setup(c => c.GetPipelineSchemaAsync(AdapterId))
            .ReturnsAsync(MinimalPipelineSchema);

        const string yaml = """
            name: my-pipeline
            nodes:
              - nodeType: FromHttpRequest@1
                id: trigger
              - nodeType: ApplyChanges@1
                id: load
            """;

        var result = await PipelineTools.ValidatePipelineDefinition(MockServer.Object, AdapterId, yaml);

        result.IsSuccess.Should().BeTrue("the tool call itself completed");
        result.IsValid.Should().BeTrue("the YAML satisfies the schema");
        result.NodeCount.Should().Be(2);
        result.Errors.Should().BeEmpty();
        result.AdapterId.Should().Be(AdapterId);
    }

    [Fact]
    public async Task ValidatePipelineDefinition_ValidJson_AlsoParses()
    {
        // Auto-detect kicks in on the leading `{` and skips the YAML path.
        MockCommunicationClient.Setup(c => c.GetPipelineSchemaAsync(AdapterId))
            .ReturnsAsync(MinimalPipelineSchema);

        const string json =
            """{"name":"my-pipeline","nodes":[{"nodeType":"FromHttpRequest@1","id":"t"}]}""";

        var result = await PipelineTools.ValidatePipelineDefinition(MockServer.Object, AdapterId, json);

        result.IsSuccess.Should().BeTrue();
        result.IsValid.Should().BeTrue();
        result.NodeCount.Should().Be(1);
    }

    [Fact]
    public async Task ValidatePipelineDefinition_MissingRequiredField_ReportsErrorAndIsValidFalse()
    {
        MockCommunicationClient.Setup(c => c.GetPipelineSchemaAsync(AdapterId))
            .ReturnsAsync(MinimalPipelineSchema);

        // Missing `name` at the root — `nodes` is present, so node count should still
        // make it into the response.
        const string yaml = """
            nodes:
              - nodeType: ApplyChanges@1
                id: load
            """;

        var result = await PipelineTools.ValidatePipelineDefinition(MockServer.Object, AdapterId, yaml);

        result.IsSuccess.Should().BeTrue("the tool ran cleanly, the definition just doesn't validate");
        result.IsValid.Should().BeFalse();
        result.Errors.Should().NotBeEmpty();
        result.Errors.Should().Contain(e => e.Message.Contains("name", StringComparison.OrdinalIgnoreCase));
        result.NodeCount.Should().Be(1, "the validator still counts nodes even when other fields fail");
    }

    [Fact]
    public async Task ValidatePipelineDefinition_BadlyFormedYaml_ReportsParseErrorAtRoot()
    {
        MockCommunicationClient.Setup(c => c.GetPipelineSchemaAsync(AdapterId))
            .ReturnsAsync(MinimalPipelineSchema);

        // YAML that can't be parsed at all.
        const string broken = "name: foo\n  nodes: [   ]   bad indentation here\n";

        var result = await PipelineTools.ValidatePipelineDefinition(MockServer.Object, AdapterId, broken);

        result.IsSuccess.Should().BeTrue("tool itself runs; we just couldn't parse");
        result.IsValid.Should().BeFalse();
        result.Errors.Should().HaveCount(1);
        result.Errors[0].Path.Should().Be("$");
    }

    [Fact]
    public async Task ValidatePipelineDefinition_EmptyAdapterId_RefusesBeforeApiCall()
    {
        var result = await PipelineTools.ValidatePipelineDefinition(MockServer.Object, "", "name: x");

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("adapterId");
        MockCommunicationClient.Verify(c => c.GetPipelineSchemaAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ValidatePipelineDefinition_EmptyDefinition_RefusesBeforeApiCall()
    {
        var result = await PipelineTools.ValidatePipelineDefinition(MockServer.Object, AdapterId, "");

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("pipelineDefinition");
        MockCommunicationClient.Verify(c => c.GetPipelineSchemaAsync(It.IsAny<string>()), Times.Never);
    }
}
