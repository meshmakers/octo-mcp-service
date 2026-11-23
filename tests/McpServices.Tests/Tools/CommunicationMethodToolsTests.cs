using FluentAssertions;
using GraphQL;
using Meshmakers.Octo.Backend.McpServices.MethodClients.DataTransferObjects.System.Communication.v4;
using Meshmakers.Octo.Backend.McpServices.Tools;
using Meshmakers.Octo.Sdk.ServiceClient.AssetRepositoryServices.Tenants;
using Meshmakers.Octo.Sdk.ServiceClient.AssetRepositoryServices.Tenants.Methods;
using Moq;
using Xunit;

namespace McpServices.Tests.Tools;

/// <summary>
///     The communication write tools run the CK methods of <c>System.Communication</c> through the generated method clients
///     on the tenant client (<see cref="CommunicationMethodCall" />): every tool sends its method, never a REST write; a
///     refusal comes back as <c>CODE: message (params)</c>, a controller without the methods as a text that names the
///     needed System.Communication version, an invalid runtime id before any call.
/// </summary>
public class CommunicationMethodToolsTests : ToolTestBase
{
    private const string AdapterId = "65f2c0ffee00000000000a01";
    private const string PipelineId = "65f2c0ffee00000000000a02";
    private const string SecondPipelineId = "65f2c0ffee00000000000a03";
    private const string ThirdPipelineId = "65f2c0ffee00000000000a04";
    private const string DataFlowId = "65f2c0ffee00000000000a05";
    private const string SiteId = "65f2c0ffee00000000000a06";
    private const string WorkloadId = "65f2c0ffee00000000000a07";
    private const string PoolId = "65f2c0ffee00000000000a08";

    /// <summary>Tool name, the call with valid arguments and the operation name of its method.</summary>
    private static readonly Dictionary<string, (Func<CommunicationMethodToolsTests, Task<(bool Ok, string? Error)>> Call,
        string Operation)> Tools = new()
    {
        ["deploy_pipeline"] = (async t => Of(await PipelineTools.DeployPipeline(t.MockServer.Object, AdapterId,
            PipelineId, "triggers: []")), "SystemCommunicationPipelineDeploy"),
        ["execute_pipeline"] = (async t => Of(await PipelineTools.ExecutePipeline(t.MockServer.Object, PipelineId)),
            "SystemCommunicationPipelineExecute"),
        ["dry_run_pipeline"] = (async t => Of(await PipelineTools.DryRunPipeline(t.MockServer.Object, PipelineId)),
            "SystemCommunicationPipelineExecute"),
        ["set_pipeline_debug"] = (async t => Of(await PipelineTools.SetPipelineDebug(t.MockServer.Object, PipelineId,
            true)), "SystemCommunicationPipelineSetDebugging"),
        ["deploy_data_flow"] = (async t => Of(await DataFlowTriggerDeploymentSiteTools.DeployDataFlow(
            t.MockServer.Object, DataFlowId)), "SystemCommunicationDataFlowDeploy"),
        ["undeploy_data_flow"] = (async t => Of(await DataFlowTriggerDeploymentSiteTools.UndeployDataFlow(
            t.MockServer.Object, DataFlowId, confirm: true)), "SystemCommunicationDataFlowUndeploy"),
        ["deploy_triggers"] = (async t => Of(await DataFlowTriggerDeploymentSiteTools.DeployTriggers(
            t.MockServer.Object)), "SystemCommunicationPipelineTriggerDeployAll"),
        ["undeploy_triggers"] = (async t => Of(await DataFlowTriggerDeploymentSiteTools.UndeployTriggers(
            t.MockServer.Object, confirm: true)), "SystemCommunicationPipelineTriggerUndeployAll"),
        ["undeploy_deployment_site"] = (async t => Of(await DataFlowTriggerDeploymentSiteTools.UndeployDeploymentSite(
            t.MockServer.Object, SiteId, confirm: true)), "SystemCommunicationDeploymentSiteUndeploy"),
        ["cancel_queued_execution"] = (async t => Of(await DataFlowTriggerDeploymentSiteTools.CancelQueuedExecution(
            t.MockServer.Object, PoolId, "e-1", confirm: true)), "SystemCommunicationAdapterPoolCancelQueuedExecution"),
        ["update_workload_chart_version"] = (async t => Of(await WorkloadTools.UpdateWorkloadChartVersion(
            t.MockServer.Object, WorkloadId, "1.2.3")), "SystemCommunicationAdapterSetChartVersion"),
        ["deploy_workload"] = (async t => Of(await WorkloadTools.DeployWorkload(t.MockServer.Object, WorkloadId)),
            "SystemCommunicationAdapterDeploy"),
        ["undeploy_workload"] = (async t => Of(await WorkloadTools.UndeployWorkload(t.MockServer.Object, WorkloadId,
            confirm: true)), "SystemCommunicationAdapterUndeploy"),
        ["move_pipelines"] = (async t => Of(await WorkloadTools.MovePipelines(t.MockServer.Object, [PipelineId],
            AdapterId, confirm: true)), "SystemCommunicationPipelineMoveToAdapter"),
    };

    /// <summary>The tool names.</summary>
    public static TheoryData<string> ToolNames => new(Tools.Keys);

    /// <summary>Authenticated session; the workload is an adapter.</summary>
    public CommunicationMethodToolsTests()
    {
        GivenAuthenticated();
        CkMethods.GivenQueryReturns(new CommunicationMethodCall.WorkloadTypeDto
            { RtId = WorkloadId, CkTypeId = "System.Communication/Adapter-1" });
    }

    [Theory]
    [MemberData(nameof(ToolNames))]
    public async Task Tool_RunsItsMethod_AndCallsNoRestWrite(string name)
    {
        CkMethods.Succeeds(ResultFor(name));

        var (ok, error) = await Tools[name].Call(this);

        ok.Should().BeTrue(error);
        CkMethods.OperationNames.Should().ContainSingle().Which.Should().Be(Tools[name].Operation);
        AssertNoCommunicationRestWrite();
    }

    [Theory]
    [MemberData(nameof(ToolNames))]
    public async Task Tool_RefusedWithoutTheRole_ReportsTheCode_AndCallsNoRestWrite(string name)
    {
        CkMethods.Fails("METHOD_FORBIDDEN", "The caller lacks a role.",
            new Dictionary<string, string> { ["reason"] = "role-missing" });

        var (ok, error) = await Tools[name].Call(this);

        ok.Should().BeFalse();
        error.Should().StartWith("METHOD_FORBIDDEN").And.Contain("reason=role-missing");
        AssertNoCommunicationRestWrite();
    }

    [Theory]
    [MemberData(nameof(ToolNames))]
    public async Task Tool_ControllerWithoutTheMethods_NamesTheVersion_AndCallsNoRestWrite(string name)
    {
        CkMethods.MissingInSchema();

        var (ok, error) = await Tools[name].Call(this);

        ok.Should().BeFalse();
        error.Should().Contain("System.Communication 4.19").And.Contain("does not fall back to the REST endpoint")
            .And.NotContain("restarting");
        AssertNoCommunicationRestWrite();
    }

    [Theory]
    [MemberData(nameof(ToolNames))]
    public async Task Tool_NoHandlerOfTheMethod_NamesTheVersionAndARestart(string name)
    {
        CkMethods.HandlerNotAvailable();

        var (ok, error) = await Tools[name].Call(this);

        ok.Should().BeFalse();
        error.Should().Contain("System.Communication 4.19").And.Contain("restarting");
        AssertNoCommunicationRestWrite();
    }

    [Fact]
    public async Task DeclaredError_IsReportedWithCodeMessageAndParameters()
    {
        CkMethods.Fails("DATA_FLOW_DEPLOY_FAILED", "The adapter rejected it.",
            new Dictionary<string, string> { ["reason"] = "adapter-rejected" });

        var result = await DataFlowTriggerDeploymentSiteTools.DeployDataFlow(MockServer.Object, DataFlowId);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Be("DATA_FLOW_DEPLOY_FAILED: The adapter rejected it. (reason=adapter-rejected)");
    }

    [Fact]
    public async Task AnInvalidRuntimeId_IsRefusedBeforeAnyCall()
    {
        var result = await DataFlowTriggerDeploymentSiteTools.DeployDataFlow(MockServer.Object, "df-1");

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Be("resource id 'df-1' is not a valid runtime id.");
        CkMethods.Requests.Should().BeEmpty();
    }

    [Theory]
    [InlineData("execute_pipeline")]
    [InlineData("dry_run_pipeline")]
    [InlineData("set_pipeline_debug")]
    public async Task ASuccessWithoutTheResultRecord_IsAFailure(string name)
    {
        CkMethods.Succeeds();

        var (ok, error) = await Tools[name].Call(this);

        ok.Should().BeFalse();
        error.Should().EndWith("returned no result.");
    }

    [Theory]
    [InlineData("METHOD_TIMEOUT")]
    [InlineData("METHOD_HANDLER_FAILED")]
    [InlineData("METHOD_VALIDATION_FAILED")]
    public async Task MovePipelines_APlatformErrorOfOnePipeline_IsAFailedItem_AndTheRestStillMove(string code)
    {
        var calls = 0;
        MockTenantClient
            .Setup(c => c.SendMutationAsync<It.IsAnyType>(It.IsAny<GraphQLRequest>()))
            .Returns(new InvocationFunc(invocation =>
            {
                CkMethods.Requests.Add((GraphQLRequest)invocation.Arguments[0]!);
                var responseType = invocation.Method.GetGenericArguments()[0];
                if (Interlocked.Increment(ref calls) == 2)
                {
                    var error = new QlQueryErrorException("failed",
                        [new GraphQLError { Message = "The second move failed.", Extensions = new Map { ["code"] = code } }]);
                    return typeof(Task).GetMethod(nameof(Task.FromException), 1, [typeof(Exception)])!
                        .MakeGenericMethod(responseType).Invoke(null, [error])!;
                }

                var response = (CkMethodCallResponse)Activator.CreateInstance(responseType, nonPublic: true)!;
                response.InvocationId = "inv-1";
                responseType.GetProperty("Result")?.SetValue(response,
                    new RtPipelineMoveResultRecordDto { OldAdapterRtId = "old", NewAdapterRtId = AdapterId });
                return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(responseType)
                    .Invoke(null, [response])!;
            }));

        var result = await WorkloadTools.MovePipelines(MockServer.Object,
            [PipelineId, SecondPipelineId, ThirdPipelineId], AdapterId, confirm: true);

        result.IsSuccess.Should().BeTrue();
        result.SuccessCount.Should().Be(2);
        result.FailureCount.Should().Be(1);
        CkMethods.Requests.Should().HaveCount(3);
        result.Result!.Results[1].ErrorMessage.Should().StartWith(code);
        result.Result.Results[2].Success.Should().BeTrue();
    }

    [Fact]
    public async Task MovePipelines_AControllerRestartAfterAMove_KeepsTheList()
    {
        AnswerMovesInTurn(
            () => Moved(),
            () => new QlQueryErrorException("no handler",
                [new GraphQLError { Message = "No live handler.", Extensions = new Map { ["code"] = "METHOD_HANDLER_NOT_AVAILABLE" } }]),
            () => Moved());

        var result = await WorkloadTools.MovePipelines(MockServer.Object,
            [PipelineId, SecondPipelineId, ThirdPipelineId], AdapterId, confirm: true);

        result.IsSuccess.Should().BeTrue();
        result.SuccessCount.Should().Be(2);
        result.Result!.Results[1].Success.Should().BeFalse();
        result.Result.Results[1].ErrorMessage.Should().Contain("restarting");
    }

    [Fact]
    public async Task MovePipelines_AControllerWithoutTheMethodBeforeAnyMove_EndsTheBatch()
    {
        CkMethods.HandlerNotAvailable();

        var result = await WorkloadTools.MovePipelines(MockServer.Object,
            [PipelineId, SecondPipelineId], AdapterId, confirm: true);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("System.Communication 4.19");
        CkMethods.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task MovePipelines_ASuccessWithoutTheMoveRecord_IsAFailedItem()
    {
        CkMethods.Succeeds();

        var result = await WorkloadTools.MovePipelines(MockServer.Object, [PipelineId], AdapterId, confirm: true);

        result.IsSuccess.Should().BeTrue();
        result.FailureCount.Should().Be(1);
        result.Result!.Results[0].ErrorMessage.Should().EndWith("returned no result.");
    }

    [Theory]
    [InlineData("execute")]
    [InlineData("dry_run")]
    [InlineData("deploy")]
    public async Task AnErrorOutsideTheMethodProtocol_ThatEchoesTheSensitiveValue_IsScrubbed(string tool)
    {
        const string canary = "canary-value-0123456789";
        CkMethods.Throws(new InvalidOperationException($"The server said: {canary}"));

        var error = tool switch
        {
            "execute" => (await PipelineTools.ExecutePipeline(MockServer.Object, PipelineId, canary)).ErrorMessage,
            "dry_run" => (await PipelineTools.DryRunPipeline(MockServer.Object, PipelineId, canary)).ErrorMessage,
            _ => (await PipelineTools.DeployPipeline(MockServer.Object, AdapterId, PipelineId, canary)).ErrorMessage
        };

        error.Should().NotContain(canary).And.Contain("[REDACTED]");
    }

    [Fact]
    public void TheSensitiveParameters_AreMarkedSensitive()
    {
        var parameters = typeof(PipelineTools).GetMethods()
            .SelectMany(m => m.GetParameters())
            .Where(p => p.Name is "pipelineInput" ||
                        (p.Name == "pipelineDefinition" && p.Member.Name == nameof(PipelineTools.DeployPipeline)))
            .ToList();

        parameters.Should().HaveCount(3);
        parameters.Should().OnlyContain(p => ((System.ComponentModel.DescriptionAttribute)p
            .GetCustomAttributes(typeof(System.ComponentModel.DescriptionAttribute), false).Single())
            .Description.StartsWith("SENSITIVE:"));
    }

    private static RtPipelineMoveResultRecordDto Moved() =>
        new() { OldAdapterRtId = "old", NewAdapterRtId = AdapterId };

    /// <summary>The n-th method call answers the n-th entry: a record (success) or an exception.</summary>
    private void AnswerMovesInTurn(params Func<object>[] answers)
    {
        var calls = 0;
        MockTenantClient
            .Setup(c => c.SendMutationAsync<It.IsAnyType>(It.IsAny<GraphQLRequest>()))
            .Returns(new InvocationFunc(invocation =>
            {
                CkMethods.Requests.Add((GraphQLRequest)invocation.Arguments[0]!);
                var responseType = invocation.Method.GetGenericArguments()[0];
                var answer = answers[Math.Min(Interlocked.Increment(ref calls), answers.Length) - 1]();
                if (answer is Exception error)
                {
                    return typeof(Task).GetMethod(nameof(Task.FromException), 1, [typeof(Exception)])!
                        .MakeGenericMethod(responseType).Invoke(null, [error])!;
                }

                var response = (CkMethodCallResponse)Activator.CreateInstance(responseType, nonPublic: true)!;
                response.InvocationId = "inv-1";
                responseType.GetProperty("Result")?.SetValue(response, answer);
                return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(responseType)
                    .Invoke(null, [response])!;
            }));
    }

    [Fact]
    public async Task DeployPipeline_ARefusalThatEchoesTheDefinition_IsScrubbed()
    {
        const string definition = "triggers: []\npassword: s3cr3t-value-long";
        CkMethods.Fails("PIPELINE_DEPLOY_REJECTED", $"Rejected: {definition}");

        var result = await PipelineTools.DeployPipeline(MockServer.Object, AdapterId, PipelineId, definition);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().NotContain("s3cr3t-value-long").And.Contain("[REDACTED]");
    }

    [Fact]
    public void ScrubSensitive_LeavesShortLinesAndRedactsLongOnes()
    {
        var text = CommunicationMethodCall.ScrubSensitive("bad input {} near line limit: abcdefgh1",
            "{}\nlimit: abcdefgh1");

        text.Should().Contain("bad input {} near line").And.NotContain("abcdefgh1");
    }

    private static object? ResultFor(string name) => name switch
    {
        "execute_pipeline" or "dry_run_pipeline" => new RtPipelineExecutionStartResultRecordDto { ExecutionId = "e-1" },
        "set_pipeline_debug" => new RtPipelineDebugResultRecordDto { Enabled = true, AppliedToRunningAdapter = true },
        "move_pipelines" => new RtPipelineMoveResultRecordDto { OldAdapterRtId = "old", NewAdapterRtId = AdapterId },
        _ => null
    };

    private static (bool, string?) Of(Meshmakers.Octo.Backend.McpServices.Models.CommunicationResponse response) =>
        (response.IsSuccess, response.ErrorMessage);
}
