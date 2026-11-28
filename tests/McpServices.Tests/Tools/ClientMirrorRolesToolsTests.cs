using FluentAssertions;
using GraphQL;
using Meshmakers.Octo.Sdk.ServiceClient.AssetRepositoryServices.Tenants;
using Meshmakers.Octo.Backend.McpServices.MethodClients.DataTransferObjects.System.Identity.v3;
using Meshmakers.Octo.Backend.McpServices.Models;
using Meshmakers.Octo.Backend.McpServices.Services;
using Meshmakers.Octo.Backend.McpServices.Tools;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Sdk.ServiceClient;
using Moq;
using System.Net;
using Xunit;

namespace McpServices.Tests.Tools;

/// <summary>
///     <c>set_client_mirror_roles</c> and <c>get_client_mirror_roles</c> (System.Identity 3.9.0): the roles a client hands to
///     its mirrors. The set tool is a dry run unless <c>dryRun</c> is false, returns one row per child tenant with the same
///     markers as octo-cli <c>SetClientMirrorRoles</c>, names the number of affected child tenants, reports a failed mirror
///     and every refusal as a failure, and is classified High.
/// </summary>
public class ClientMirrorRolesToolsTests : ToolTestBase
{
    private const string ClientRtId = "65f1c0ffee0000000000c1c1";
    private const string SetMirrorRoles = "SystemIdentityClientSetMirrorRoles";

    public ClientMirrorRolesToolsTests()
    {
        GivenAuthenticated();
        MockIdentityClient.Setup(c => c.GetClient(It.IsAny<string>()))
            .ReturnsAsync((string id) => new ClientDto { ClientId = id, RtId = ClientRtId });
    }

    private static RtClientMirrorRolesResultRecordDto Plan(bool dryRun, params RtClientMirrorRoleTenantResultRecordDto[] tenants) =>
        new() { DryRun = dryRun, RoleNames = ["CommunicationManagement"], Tenants = tenants.ToList() };

    private static RtClientMirrorRoleTenantResultRecordDto Tenant(string id, string outcome, string[]? added = null,
        string[]? removed = null, string[]? present = null, string[]? adopted = null, string[]? handSet = null,
        string[]? missing = null, string[]? declined = null, string[]? refused = null) =>
        new()
        {
            ChildTenantId = id, Outcome = outcome, Added = added ?? [], Removed = removed ?? [], AlreadyPresent = present ?? [],
            Adopted = adopted ?? [], HandSet = handSet ?? [], RoleMissing = missing ?? [], Declined = declined ?? [],
            RoleRefused = refused ?? []
        };

    [Fact]
    public async Task SetClientMirrorRoles_DefaultsToADryRun_AndNamesTheNumberOfChildTenants()
    {
        CkMethods.Succeeds(Plan(true,
            Tenant("plant-b", "Planned", missing: ["CommunicationManagement"]),
            Tenant("plant-a", "Planned", added: ["CommunicationManagement"], handSet: ["Reader"])));

        var result = await ClientManagementTools.SetClientMirrorRoles(MockServer.Object, "ci-deploy",
            ["CommunicationManagement"]);

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        result.DryRun.Should().BeTrue();
        CkMethods.RtIdOf(SetMirrorRoles).Should().Be(ClientRtId);
        CkMethodFake.Bool(CkMethods.InputOf(SetMirrorRoles), "dryRun").Should().BeTrue();
        CkMethodFake.Strings(CkMethods.InputOf(SetMirrorRoles).GetProperty("roleNames"))
            .Should().Equal("CommunicationManagement");
        result.Tenants.Select(t => t.ChildTenantId).Should().Equal("plant-a", "plant-b");
        result.Tenants[0].Status.Should().Equal("WOULD-ADD", "HAND-SET");
        result.Tenants[0].RolesBefore.Should().Equal("Reader");
        result.Tenants[0].RolesAfter.Should().Equal("CommunicationManagement", "Reader");
        result.Tenants[1].Status.Should().Equal("ROLE-MISSING");
        result.Message.Should().Contain("2 child tenant(s) affected").And.Contain("1 with a role change")
            .And.Contain("nothing was written").And.Contain("dryRun=false");
        // One method call, no other write.
        CkMethods.OperationNames.Should().Equal(SetMirrorRoles);
    }

    [Fact]
    public async Task SetClientMirrorRoles_DryRunFalse_Applies_AndReportsWhatHappened()
    {
        CkMethods.Succeeds(Plan(false,
            Tenant("plant-a", "Applied", added: ["CommunicationManagement"], removed: ["Old"]),
            Tenant("plant-c", "InSync", present: ["CommunicationManagement"], adopted: ["CommunicationManagement"])));

        var result = await ClientManagementTools.SetClientMirrorRoles(MockServer.Object, "ci-deploy",
            ["CommunicationManagement"], dryRun: false);

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        CkMethodFake.Bool(CkMethods.InputOf(SetMirrorRoles), "dryRun").Should().BeFalse();
        result.Tenants[0].Status.Should().Equal("ADDED", "REMOVED");
        result.Tenants[1].Status.Should().Equal("ADOPTED");
        result.Message.Should().Contain("2 child tenant(s), 1 changed");
    }

    [Fact]
    public async Task SetClientMirrorRoles_AnyBuiltInRoleName_GoesThroughTheList_AndAnEmptyListClears()
    {
        CkMethods.Succeeds(Plan(true));

        await ClientManagementTools.SetClientMirrorRoles(MockServer.Object, "ci-deploy", [" WorkloadDeployment ", "reader", "Reader"]);
        CkMethodFake.Strings(CkMethods.InputOf(SetMirrorRoles).GetProperty("roleNames"))
            .Should().Equal("WorkloadDeployment", "reader");
    }

    [Fact]
    public async Task SetClientMirrorRoles_EmptyList_IsSentAsClear()
    {
        CkMethods.Succeeds(Plan(true));

        var result = await ClientManagementTools.SetClientMirrorRoles(MockServer.Object, "ci-deploy", []);

        result.IsSuccess.Should().BeTrue();
        CkMethodFake.Strings(CkMethods.InputOf(SetMirrorRoles).GetProperty("roleNames")).Should().BeEmpty();
    }

    [Fact]
    public async Task SetClientMirrorRoles_MissingArguments_CallNothing()
    {
        (await ClientManagementTools.SetClientMirrorRoles(MockServer.Object, " ", ["Reader"])).IsSuccess.Should().BeFalse();
        var noList = await ClientManagementTools.SetClientMirrorRoles(MockServer.Object, "ci-deploy", null);

        noList.IsSuccess.Should().BeFalse();
        noList.ErrorMessage.Should().Contain("roleNames").And.Contain("[]");
        CkMethods.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task SetClientMirrorRoles_Unauthenticated_ReturnsAuthError()
    {
        GivenUnauthenticated();

        var result = await ClientManagementTools.SetClientMirrorRoles(MockServer.Object, "ci-deploy", ["Reader"]);

        result.IsSuccess.Should().BeFalse();
        CkMethods.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task SetClientMirrorRoles_AFailedMirror_IsAFailure_NamingTheTenant_WithTheRows()
    {
        CkMethods.Succeeds(Plan(false,
            Tenant("plant-a", "Applied", added: ["CommunicationManagement"]),
            Tenant("plant-x", "Failed", present: ["Reader"])));

        var result = await ClientManagementTools.SetClientMirrorRoles(MockServer.Object, "ci-deploy",
            ["CommunicationManagement"], dryRun: false);

        result.IsSuccess.Should().BeFalse();
        result.FailedTenants.Should().Equal("plant-x");
        result.ErrorMessage.Should().Contain("plant-x").And.Contain("1 of 2");
        result.Tenants.Should().HaveCount(2);
        result.Tenants[1].Status.Should().Equal("FAILED", "ALREADY-PRESENT");
        result.Tenants[1].RolesAfter.Should().Equal("Reader");
    }

    [Theory]
    [InlineData("MIRROR_ROLE_NOT_ALLOWED", "TenantManagement")]
    [InlineData("MIRROR_ROLE_NOT_FOUND", "Unknown")]
    [InlineData("CLIENT_NOT_CONFIDENTIAL", null)]
    [InlineData("CONCURRENT_CHANGE", null)]
    public async Task SetClientMirrorRoles_ADeclaredError_IsAFailure_WithCodeAndNames(string code, string? roleNames)
    {
        CkMethods.Fails(code, "refused",
            roleNames == null ? null : new Dictionary<string, string> { ["roleNames"] = roleNames });

        var result = await ClientManagementTools.SetClientMirrorRoles(MockServer.Object, "ci-deploy", [roleNames ?? "Reader"]);

        result.IsSuccess.Should().BeFalse();
        result.DryRun.Should().BeTrue("the refused call was a dry run");
        result.ErrorMessage.Should().Contain(code);
        if (roleNames != null)
        {
            result.ErrorMessage.Should().Contain($"roleNames={roleNames}");
        }

        result.Tenants.Should().BeEmpty();
    }

    [Fact]
    public async Task SetClientMirrorRoles_WithoutTenantManagement_IsRefused()
    {
        CkMethods.Fails("METHOD_FORBIDDEN", "forbidden", new Dictionary<string, string> { ["reason"] = "role-required" });

        var result = await ClientManagementTools.SetClientMirrorRoles(MockServer.Object, "ci-deploy", ["Reader"]);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("METHOD_FORBIDDEN").And.Contain("reason=role-required");
    }

    [Fact]
    public async Task SetClientMirrorRoles_UnknownClient_IsRefused_BeforeTheCall()
    {
        // The identity REST answer of an unknown client: a problem body without a message (seen on the lane stack).
        MockIdentityClient.Setup(c => c.GetClient("missing")).ThrowsAsync(new ServiceClientResultException(
            "{\"title\":\"Not Found\",\"status\":404}", HttpStatusCode.NotFound, null));

        var result = await ClientManagementTools.SetClientMirrorRoles(MockServer.Object, "missing", ["Reader"]);

        result.IsSuccess.Should().BeFalse();
        result.DryRun.Should().BeTrue();
        result.ErrorMessage.Should().Be("Client 'missing' not found in tenant 'test-tenant'; nothing was changed.");
        CkMethods.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task SetClientMirrorRoles_OnAnOlderIdentityService_NamesSystemIdentity39()
    {
        CkMethods.HandlerNotAvailable();

        var result = await ClientManagementTools.SetClientMirrorRoles(MockServer.Object, "ci-deploy", ["Reader"]);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("System.Identity 3.9").And.Contain("Nothing was changed");
    }

    [Fact]
    public async Task SetClientMirrorRoles_OnASchemaWithoutTheMethod_NamesSystemIdentity39()
    {
        // The common case of an older identity service: the tenant holds System.Identity 3.8 and the asset repository
        // rejects the document in GraphQL validation.
        CkMethods.MissingInSchema();

        var result = await ClientManagementTools.SetClientMirrorRoles(MockServer.Object, "ci-deploy", ["Reader"]);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("System.Identity 3.9 (the mirror role list)").And.NotContain("3.8");
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public async Task SetClientMirrorRoles_ABlankEntry_IsRefused_NeverReadAsClear(string blank)
    {
        CkMethods.Succeeds(Plan(false));

        var result = await ClientManagementTools.SetClientMirrorRoles(MockServer.Object, "ci-deploy", [blank], dryRun: false);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("blank entry");
        CkMethods.Requests.Should().BeEmpty();
    }

    [Theory]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    public async Task ClientIdsThatAreNoPathSegment_AreRefused_BeforeAnyCall(string clientId)
    {
        CkMethods.Succeeds(Plan(true));

        (await ClientManagementTools.SetClientMirrorRoles(MockServer.Object, clientId, ["Reader"])).IsSuccess.Should().BeFalse();
        (await ClientManagementTools.GetClientMirrorRoles(MockServer.Object, clientId)).IsSuccess.Should().BeFalse();

        CkMethods.Requests.Should().BeEmpty();
        MockIdentityClient.Verify(c => c.GetClient(It.IsAny<string>()), Times.Never);
        MockTenantClient.Verify(c => c.SendQueryAsync<RtClientDto>(It.IsAny<GraphQLRequest>()), Times.Never);
    }

    [Fact]
    public async Task GetClientMirrorRoles_ReturnsTheSortedListOfExactlyThatClient()
    {
        var queries = new List<GraphQLRequest>();
        MockTenantClient.Setup(c => c.SendQueryAsync<RtClientDto>(It.IsAny<GraphQLRequest>()))
            .Callback<GraphQLRequest>(queries.Add)
            .ReturnsAsync(new QlItemsContainer<RtClientDto>
            {
                Items =
                [
                    new RtClientDto { ClientId = "CI-DEPLOY", MirrorRoleNames = ["Other"] },
                    new RtClientDto { ClientId = "ci-deploy", MirrorRoleNames = ["Reader", "CommunicationManagement"] }
                ]
            });

        var result = await ClientManagementTools.GetClientMirrorRoles(MockServer.Object, "ci-deploy");

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        result.MirrorRoleNames.Should().Equal("CommunicationManagement", "Reader");
        var query = queries.Should().ContainSingle().Subject;
        query.OperationName.Should().Be("clientMirrorRoleNames");
        query.Query.Should().Contain("mirrorRoleNames").And.Contain("attributePath: \"clientId\"");
        System.Text.Json.JsonSerializer.Serialize(query.Variables).Should().Be("{\"clientId\":\"ci-deploy\"}");
    }

    [Fact]
    public async Task GetClientMirrorRoles_Unauthenticated_ReturnsAuthError()
    {
        GivenUnauthenticated();

        var result = await ClientManagementTools.GetClientMirrorRoles(MockServer.Object, "ci-deploy");

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Not authenticated");
        MockTenantClient.Verify(c => c.SendQueryAsync<RtClientDto>(It.IsAny<GraphQLRequest>()), Times.Never);
    }

    [Fact]
    public async Task GetClientMirrorRoles_OnAnOlderIdentityModel_NamesSystemIdentity39()
    {
        MockTenantClient.Setup(c => c.SendQueryAsync<RtClientDto>(It.IsAny<GraphQLRequest>())).ThrowsAsync(
            new QlQueryErrorException("GraphQL error",
            [
                new GraphQLError
                {
                    Message = "Cannot query field 'mirrorRoleNames' on type 'SystemIdentityClient'.",
                    Extensions = new Map { ["code"] = "FIELDS_ON_CORRECT_TYPE", ["number"] = "5.3.1" }
                }
            ]));

        var result = await ClientManagementTools.GetClientMirrorRoles(MockServer.Object, "ci-deploy");

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("System.Identity 3.9 (the mirror role list)");
    }

    [Fact]
    public async Task GetClientMirrorRoles_UnknownClient_AndMissingArgument_AreFailures()
    {
        CkMethods.GivenQueryReturns<RtClientDto>();

        (await ClientManagementTools.GetClientMirrorRoles(MockServer.Object, "missing")).ErrorMessage.Should().Contain("not found");
        (await ClientManagementTools.GetClientMirrorRoles(MockServer.Object, "")).IsSuccess.Should().BeFalse();
    }

    [Fact]
    public void Risk_SetIsHigh_GetIsLow()
    {
        var registry = new ToolRiskRegistry(typeof(ClientManagementTools).Assembly);

        registry.GetRiskLevel("set_client_mirror_roles").Should().Be(McpRiskLevel.High);
        registry.GetRiskLevel("get_client_mirror_roles").Should().Be(McpRiskLevel.Low);
    }
}
