using FluentAssertions;
using GraphQL;
using Meshmakers.Octo.Backend.McpServices.MethodClients.DataTransferObjects.System.Communication.v4;
using Meshmakers.Octo.Backend.McpServices.Tools;
using Meshmakers.Octo.Sdk.ServiceClient.AssetRepositoryServices.Tenants;
using Meshmakers.Octo.Sdk.ServiceClient.AssetRepositoryServices.Tenants.Methods;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Moq;
using Xunit;

namespace McpServices.Tests.Tools;

public class WorkloadToolsTests : ToolTestBase
{
    private const string WorkloadId = "65f0c0ffee00000000000201";
    private const string Pipeline1 = "65f0c0ffee00000000000211";
    private const string Pipeline2 = "65f0c0ffee00000000000212";
    private const string AdapterId = "69cfa838092b710403248acd";

    public WorkloadToolsTests()
    {
        GivenAuthenticated();
        // The communication writes run as CK methods on the tenant client; the workload is an Application by default.
        CkMethods.GivenQueryReturns(new CommunicationMethodCall.WorkloadTypeDto
            { RtId = WorkloadId, CkTypeId = "System.Communication/Application-1" });
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
    public async Task UpdateWorkloadChartVersion_HappyPath_CallsTheMethod()
    {
        var result = await WorkloadTools.UpdateWorkloadChartVersion(MockServer.Object,
            WorkloadId, "1.2.4");

        result.IsSuccess.Should().BeTrue();
        result.ResourceId.Should().Be(WorkloadId);
        CkMethods.RtIdOf("SystemCommunicationApplicationSetChartVersion").Should().Be(WorkloadId);
        CkMethodFake.Str(CkMethods.InputOf("SystemCommunicationApplicationSetChartVersion"), "chartVersion")
            .Should().Be("1.2.4");
        AssertNoCommunicationRestWrite();
    }

    [Fact]
    public async Task UpdateWorkloadChartVersion_MissingArgs_ReturnsValidationError()
    {
        var result = await WorkloadTools.UpdateWorkloadChartVersion(MockServer.Object, "", "");
        result.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task DeployWorkload_HappyPath_CallsTheMethod()
    {
        var result = await WorkloadTools.DeployWorkload(MockServer.Object, WorkloadId);

        result.IsSuccess.Should().BeTrue();
        CkMethods.RtIdOf("SystemCommunicationApplicationDeploy").Should().Be(WorkloadId);
        AssertNoCommunicationRestWrite();
    }

    [Theory]
    [InlineData("System.Communication/Adapter", "SystemCommunicationAdapterDeploy")]
    [InlineData("System.Communication/Adapter-1", "SystemCommunicationAdapterDeploy")]
    [InlineData("System.Communication/AdapterPool-2", "SystemCommunicationAdapterPoolDeploy")]
    [InlineData("System.Communication/Application-2", "SystemCommunicationApplicationDeploy")]
    public async Task DeployWorkload_UsesTheClientOfTheConcreteType_WhateverTheElementVersion(string ckTypeId,
        string operationName)
    {
        CkMethods.GivenQueryReturns(new CommunicationMethodCall.WorkloadTypeDto
            { RtId = WorkloadId, CkTypeId = ckTypeId });

        var result = await WorkloadTools.DeployWorkload(MockServer.Object, WorkloadId);

        result.IsSuccess.Should().BeTrue();
        CkMethods.OperationNames.Should().ContainSingle().Which.Should().Be(operationName);
    }

    [Theory]
    [InlineData("System.Communication/Adapter-2", "Adapter")]
    [InlineData("System.Communication/AdapterPool", "AdapterPool")]
    [InlineData("System.Communication/Application-17", "Application")]
    [InlineData("System.Communication/Pipeline-1", null)]
    [InlineData("System.Communication/Adapter-", null)]
    [InlineData("Other.Model/Adapter-1", null)]
    public void ParseWorkloadKind_ComparesTheVersionLessType(string ckTypeId, string? expected)
    {
        CommunicationMethodCall.ParseWorkloadKind(ckTypeId)?.ToString().Should().Be(expected);
        if (expected == null)
        {
            CommunicationMethodCall.ParseWorkloadKind(ckTypeId).Should().BeNull();
        }
    }

    [Fact]
    public async Task DeployWorkload_UnknownWorkload_FailsBeforeAnyWrite()
    {
        CkMethods.GivenQueryReturns<CommunicationMethodCall.WorkloadTypeDto>();

        var result = await WorkloadTools.DeployWorkload(MockServer.Object, WorkloadId);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Be($"Workload '{WorkloadId}' not found.");
        CkMethods.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task DeployWorkload_InvalidRuntimeId_FailsBeforeAnyCall()
    {
        var result = await WorkloadTools.DeployWorkload(MockServer.Object, "wl-123");

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Be("workloadId 'wl-123' is not a valid runtime id.");
        CkMethods.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task DeployWorkload_RefusedWithoutTheRole_ReportsTheCode()
    {
        CkMethods.Fails("METHOD_FORBIDDEN", "The caller lacks a role.",
            new Dictionary<string, string> { ["reason"] = "role-missing" });

        var result = await WorkloadTools.DeployWorkload(MockServer.Object, WorkloadId);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().StartWith("METHOD_FORBIDDEN").And.Contain("reason=role-missing");
        AssertNoCommunicationRestWrite();
    }

    [Fact]
    public async Task DeployWorkload_ControllerWithoutTheMethods_NamesTheVersion()
    {
        CkMethods.HandlerNotAvailable();

        var result = await WorkloadTools.DeployWorkload(MockServer.Object, WorkloadId);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("System.Communication 4.19").And
            .Contain("does not fall back to the REST endpoint");
        AssertNoCommunicationRestWrite();
    }

    [Fact]
    public async Task UndeployWorkload_WithoutConfirm_Refuses()
    {
        var result = await WorkloadTools.UndeployWorkload(MockServer.Object, WorkloadId);

        result.IsSuccess.Should().BeFalse();
        CkMethods.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task UndeployWorkload_WithConfirm_CallsTheMethod()
    {
        var result = await WorkloadTools.UndeployWorkload(MockServer.Object, WorkloadId, confirm: true);

        result.IsSuccess.Should().BeTrue();
        CkMethods.RtIdOf("SystemCommunicationApplicationUndeploy").Should().Be(WorkloadId);
        AssertNoCommunicationRestWrite();
    }

    [Fact]
    public async Task MovePipelines_WithoutConfirm_Refuses()
    {
        var result = await WorkloadTools.MovePipelines(MockServer.Object,
            [Pipeline1, Pipeline2], AdapterId);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("confirm=true");
        CkMethods.Requests.Should().BeEmpty();
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
    public async Task MovePipelines_AllSuccess_ReturnsCleanMessage_OneCallPerPipeline()
    {
        CkMethods.Succeeds(new RtPipelineMoveResultRecordDto { OldAdapterRtId = "old-a", NewAdapterRtId = AdapterId });

        var result = await WorkloadTools.MovePipelines(MockServer.Object,
            [Pipeline1, Pipeline2], AdapterId, confirm: true);

        result.IsSuccess.Should().BeTrue();
        result.SuccessCount.Should().Be(2);
        result.FailureCount.Should().Be(0);
        result.Message.Should().NotContain("failed");
        CkMethods.Requests.Select(r => r.OperationName).Should()
            .Equal("SystemCommunicationPipelineMoveToAdapter", "SystemCommunicationPipelineMoveToAdapter");
        result.Result!.Results.Select(r => r.PipelineRtId).Should().Equal(Pipeline1, Pipeline2);
        result.Result.Results.Should().OnlyContain(r => r.OldAdapterRtId == "old-a" && r.NewAdapterRtId == AdapterId);
        AssertNoCommunicationRestWrite();
    }

    [Fact]
    public async Task MovePipelines_PartialFailure_ReportsCounts()
    {
        var calls = 0;
        MockTenantClient
            .Setup(c => c.SendMutationAsync<It.IsAnyType>(It.IsAny<GraphQLRequest>()))
            .Returns(new InvocationFunc(invocation =>
            {
                var responseType = invocation.Method.GetGenericArguments()[0];
                if (Interlocked.Increment(ref calls) == 1)
                {
                    var response = (CkMethodCallResponse)Activator.CreateInstance(responseType, nonPublic: true)!;
                    response.InvocationId = "inv-1";
                    responseType.GetProperty("Result")?.SetValue(response,
                        new RtPipelineMoveResultRecordDto { OldAdapterRtId = "old-a", NewAdapterRtId = AdapterId });
                    return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(responseType)
                        .Invoke(null, [response])!;
                }

                var error = new QlQueryErrorException("CkTypeId mismatch",
                [
                    new GraphQLError
                    {
                        Message = "CkTypeId mismatch", Extensions = new Map { ["code"] = "PIPELINE_MOVE_FAILED" }
                    }
                ]);
                return typeof(Task).GetMethod(nameof(Task.FromException), 1, [typeof(Exception)])!
                    .MakeGenericMethod(responseType).Invoke(null, [error])!;
            }));

        var result = await WorkloadTools.MovePipelines(MockServer.Object,
            [Pipeline1, Pipeline2], AdapterId, confirm: true);

        result.IsSuccess.Should().BeTrue();
        result.SuccessCount.Should().Be(1);
        result.FailureCount.Should().Be(1);
        result.Message.Should().Contain("failed");
        result.Result!.Results[1].ErrorMessage.Should().Be("PIPELINE_MOVE_FAILED: CkTypeId mismatch");
    }

    [Fact]
    public async Task MovePipelines_AnUnknownOrInvalidPipeline_IsAFailedItem()
    {
        CkMethods.Fails("METHOD_TARGET_NOT_FOUND", "No such pipeline.");

        var result = await WorkloadTools.MovePipelines(MockServer.Object,
            [Pipeline1, "p2"], AdapterId, confirm: true);

        result.IsSuccess.Should().BeTrue();
        result.FailureCount.Should().Be(2);
        result.Result!.Results[0].ErrorMessage.Should().Be($"Pipeline '{Pipeline1}' was not found");
        result.Result.Results[1].ErrorMessage.Should().Be("PipelineRtId 'p2' is not a valid id");
        CkMethods.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task MovePipelines_RefusedCaller_EndsTheBatch()
    {
        CkMethods.Fails("METHOD_FORBIDDEN", "The caller lacks a role.");

        var result = await WorkloadTools.MovePipelines(MockServer.Object,
            [Pipeline1, Pipeline2], AdapterId, confirm: true);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().StartWith("METHOD_FORBIDDEN");
        CkMethods.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task MovePipelines_WithRedeploy_PassesFlag()
    {
        await WorkloadTools.MovePipelines(MockServer.Object,
            [Pipeline1], AdapterId, redeploy: true, confirm: true);

        var input = CkMethods.InputOf("SystemCommunicationPipelineMoveToAdapter");
        CkMethodFake.Bool(input, "redeploy").Should().BeTrue();
        CkMethodFake.Str(input, "targetAdapterRtId").Should().Be(AdapterId);
    }
}
