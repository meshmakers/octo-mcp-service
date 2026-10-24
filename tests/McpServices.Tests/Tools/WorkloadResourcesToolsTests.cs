using System.Net;
using System.Text.Json.Nodes;
using FluentAssertions;
using Meshmakers.Octo.Backend.McpServices.Services.WorkloadResources;
using Meshmakers.Octo.Backend.McpServices.Tools;
using Meshmakers.Octo.Sdk.ServiceClient.AssetRepositoryServices.System;
using Moq;
using Xunit;

namespace McpServices.Tests.Tools;

/// <summary>
///     AB#7429: <c>get_workload_resources</c> and <c>update_workload_resources</c>. The tenant's GraphQL endpoint is
///     answered with canned JSON, so the tests run without the published System.Communication 4.10.0 model.
/// </summary>
public class WorkloadResourcesToolsTests : ToolTestBase
{
    private const string RtId = "670000000000000000000001";

    private readonly List<(string Query, string? Variables)> _mutations = [];

    public WorkloadResourcesToolsTests()
    {
        GivenAuthenticated();
    }

    private static string Items(string? cr = null, string? cl = null, string? mr = null, string? ml = null,
        string? warning = null) =>
        new JsonArray(new JsonObject
        {
            ["rtId"] = RtId, ["name"] = "demo", ["cpuRequest"] = cr, ["cpuLimit"] = cl, ["memoryRequest"] = mr,
            ["memoryLimit"] = ml, ["lastDeploymentWarning"] = warning
        }).ToJsonString();

    private void GivenWorkload(string adapterItems, string applicationItems = "[]")
    {
        MockAssetClient
            .Setup(c => c.SendGraphQlAsync(It.IsAny<string>(), It.Is<string>(q => q.StartsWith("query")),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GraphQlRawResponse(
                "{\"data\":{\"runtime\":{\"adapters\":{\"items\":" + adapterItems +
                "},\"applications\":{\"items\":" + applicationItems + "}}}}", HttpStatusCode.OK));
        MockAssetClient
            .Setup(c => c.SendGraphQlAsync(It.IsAny<string>(), It.Is<string>(q => q.StartsWith("mutation")),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, string query, string? variables, string? _, CancellationToken _) =>
            {
                _mutations.Add((query, variables));
                var item = JsonNode.Parse(variables!)!["entity"]!["item"]!.AsObject();
                var field = query.Contains("systemCommunicationAdapters")
                    ? "systemCommunicationAdapters"
                    : "systemCommunicationApplications";
                var stored = new JsonObject
                {
                    ["rtId"] = RtId, ["name"] = "demo", ["cpuRequest"] = null, ["cpuLimit"] = null,
                    ["memoryRequest"] = null, ["memoryLimit"] = null, ["lastDeploymentWarning"] = null
                };
                foreach (var (key, value) in item)
                {
                    stored[key] = value?.DeepClone();
                }

                return new GraphQlRawResponse(new JsonObject
                {
                    ["data"] = new JsonObject
                    {
                        ["runtime"] = new JsonObject
                            { [field] = new JsonObject { ["update"] = new JsonArray(stored) } }
                    }
                }.ToJsonString(), HttpStatusCode.OK);
            });
    }

    private JsonObject SentItem()
    {
        var mutation = _mutations.Should().ContainSingle().Subject;
        return JsonNode.Parse(mutation.Variables!)!["entity"]!["item"]!.AsObject();
    }

    // ── update_workload_resources ───────────────────────────────────────────

    [Fact]
    public async Task Update_OneValue_WritesOnlyThatValue_AndSaysNothingWasDeployed()
    {
        GivenWorkload(Items(ml: "1Gi"));

        var result = await WorkloadTools.UpdateWorkloadResources(MockServer.Object, RtId, memoryRequest: "512Mi");

        result.IsSuccess.Should().BeTrue();
        SentItem().Select(p => p.Key).Should().Equal("memoryRequest");
        result.WorkloadKind.Should().Be("Adapter");
        result.Resources!.MemoryRequest.Should().Be("512Mi");
        result.Message.Should().Contain("deploy_workload");
        _mutations[0].Query.Should().Be(WorkloadResourcesAccess.AdapterMutation);
    }

    [Fact]
    public async Task Update_EmptyString_ClearsTheValue()
    {
        GivenWorkload(Items(ml: "1Gi"));

        var result = await WorkloadTools.UpdateWorkloadResources(MockServer.Object, RtId, memoryLimit: "");

        result.IsSuccess.Should().BeTrue();
        var item = SentItem();
        item.ContainsKey("memoryLimit").Should().BeTrue();
        item["memoryLimit"].Should().BeNull();
        result.Resources!.MemoryLimit.Should().BeNull();
    }

    [Fact]
    public async Task Update_Application_UsesTheApplicationMutation()
    {
        GivenWorkload("[]", Items());

        var result = await WorkloadTools.UpdateWorkloadResources(MockServer.Object, RtId, cpuRequest: "250m");

        result.IsSuccess.Should().BeTrue();
        result.WorkloadKind.Should().Be("Application");
        _mutations.Should().ContainSingle().Which.Query.Should().Be(WorkloadResourcesAccess.ApplicationMutation);
    }

    [Fact]
    public async Task Update_InvalidQuantity_IsRejected_NothingWritten()
    {
        GivenWorkload(Items());

        var result = await WorkloadTools.UpdateWorkloadResources(MockServer.Object, RtId, memoryRequest: "512 MB");

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("MemoryRequest '512 MB' is not a valid Kubernetes quantity");
        _mutations.Should().BeEmpty();
    }

    [Fact]
    public async Task Update_RequestAboveLimit_IsRejected_NothingWritten()
    {
        GivenWorkload(Items());

        var result = await WorkloadTools.UpdateWorkloadResources(MockServer.Object, RtId, memoryRequest: "2Gi",
            memoryLimit: "1Gi");

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("MemoryRequest 2Gi exceeds MemoryLimit 1Gi");
        _mutations.Should().BeEmpty();
    }

    [Fact]
    public async Task Update_RequestAboveTheStoredLimit_IsRejected()
    {
        GivenWorkload(Items(cl: "250m"));

        var result = await WorkloadTools.UpdateWorkloadResources(MockServer.Object, RtId, cpuRequest: "500m");

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("CpuRequest 500m exceeds CpuLimit 250m");
        _mutations.Should().BeEmpty();
    }

    [Fact]
    public async Task Update_NoValues_IsRejected_WithoutAnyCall()
    {
        var result = await WorkloadTools.UpdateWorkloadResources(MockServer.Object, RtId);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("at least one");
        MockAssetClient.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Update_MissingWorkloadId_IsRejected()
    {
        var result = await WorkloadTools.UpdateWorkloadResources(MockServer.Object, " ", memoryRequest: "512Mi");

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("workloadId is required");
    }

    [Fact]
    public async Task Update_UnknownWorkload_IsReported()
    {
        GivenWorkload("[]");

        var result = await WorkloadTools.UpdateWorkloadResources(MockServer.Object, RtId, memoryRequest: "512Mi");

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain(RtId);
        _mutations.Should().BeEmpty();
    }

    [Fact]
    public async Task Update_Unauthenticated_ReturnsAuthError_AndCallsNothing()
    {
        GivenUnauthenticated();

        var result = await WorkloadTools.UpdateWorkloadResources(MockServer.Object, RtId, memoryRequest: "512Mi");

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Not authenticated");
        MockAssetClient.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Update_TenantWithoutTheAttributes_GetsAnUpdateHint()
    {
        MockAssetClient
            .Setup(c => c.SendGraphQlAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(),
                It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GraphQlRawResponse(
                "{\"errors\":[{\"message\":\"Cannot query field \\\"cpuRequest\\\" on type \\\"SystemCommunicationAdapter\\\".\"}]}",
                HttpStatusCode.BadRequest));

        var result = await WorkloadTools.UpdateWorkloadResources(MockServer.Object, RtId, memoryRequest: "512Mi");

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("4.10.0");
    }

    [Fact]
    public async Task Update_MutationWithoutUpdatedItem_IsReported()
    {
        GivenWorkload(Items());
        MockAssetClient
            .Setup(c => c.SendGraphQlAsync(It.IsAny<string>(), It.Is<string>(q => q.StartsWith("mutation")),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GraphQlRawResponse(
                "{\"data\":{\"runtime\":{\"systemCommunicationAdapters\":{\"update\":[]}}}}", HttpStatusCode.OK));

        var result = await WorkloadTools.UpdateWorkloadResources(MockServer.Object, RtId, memoryRequest: "512Mi");

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("did not return the updated");
    }

    // ── get_workload_resources ──────────────────────────────────────────────

    [Fact]
    public async Task Get_ReturnsTheFourValues_AndTheWarning()
    {
        GivenWorkload(Items(mr: "512Mi", ml: "1Gi", warning: "MemoryRequest 512Mi is overridden by an explicit Helm value"));

        var result = await WorkloadTools.GetWorkloadResources(MockServer.Object, RtId);

        result.IsSuccess.Should().BeTrue();
        result.WorkloadKind.Should().Be("Adapter");
        result.Resources!.MemoryRequest.Should().Be("512Mi");
        result.Resources.MemoryLimit.Should().Be("1Gi");
        result.Resources.CpuRequest.Should().BeNull();
        result.Resources.LastDeploymentWarning.Should().Contain("overridden");
        _mutations.Should().BeEmpty();
    }

    [Fact]
    public async Task Get_Unauthenticated_ReturnsAuthError()
    {
        GivenUnauthenticated();

        var result = await WorkloadTools.GetWorkloadResources(MockServer.Object, RtId);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Not authenticated");
    }

    [Fact]
    public async Task Get_MissingWorkloadId_IsRejected()
    {
        var result = await WorkloadTools.GetWorkloadResources(MockServer.Object, "");

        result.IsSuccess.Should().BeFalse();
        MockAssetClient.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Get_UnknownWorkload_IsReported()
    {
        GivenWorkload("[]");

        var result = await WorkloadTools.GetWorkloadResources(MockServer.Object, RtId);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain(RtId);
    }

    // ── get_adapter ─────────────────────────────────────────────────────────

    [Fact]
    public async Task GetAdapter_CarriesTheResources()
    {
        GivenWorkload(Items(mr: "512Mi"));
        MockCommunicationClient.Setup(c => c.GetAdapterConfigurationAsync(RtId))
            .ReturnsAsync(new Meshmakers.Octo.Communication.Contracts.DataTransferObjects.AdapterConfigurationDto(
                new Meshmakers.Octo.ConstructionKit.Contracts.RtEntityId(
                    new Meshmakers.Octo.ConstructionKit.Contracts.RtCkId<Meshmakers.Octo.ConstructionKit.Contracts.CkTypeId>("Sys-1/Adapter-1"),
                    new Meshmakers.Octo.ConstructionKit.Contracts.OctoObjectId(RtId)), "{}", []));

        var result = await AdapterTools.GetAdapter(MockServer.Object, RtId);

        result.IsSuccess.Should().BeTrue();
        result.Resources!.MemoryRequest.Should().Be("512Mi");
    }
}
