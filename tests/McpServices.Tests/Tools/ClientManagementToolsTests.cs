using FluentAssertions;
using Duende.IdentityModel;
using Meshmakers.Octo.Backend.McpServices.Tools;
using Meshmakers.Octo.Communication.Contracts;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Sdk.ServiceClient.IdentityServices;
using Moq;
using Xunit;

namespace McpServices.Tests.Tools;

public class ClientManagementToolsTests : ToolTestBase
{
    private const string ClientRtId = "65f1c0ffee0000000000c1c1";

    public ClientManagementToolsTests()
    {
        GivenAuthenticated();
        // The client methods address the client by runtime id; the tools look it up by client id.
        MockIdentityClient.Setup(c => c.GetClient(It.IsAny<string>()))
            .ReturnsAsync((string id) => new ClientDto { ClientId = id, RtId = ClientRtId });
        MockIdentityClient.Setup(c => c.GetRoles()).ReturnsAsync(
        [
            new RoleDto { Id = "660000000000000000000002", Name = "DataAnalyst" },
            new RoleDto { Id = "660000000000000000000009", Name = "Viewer" }
        ]);
    }

    private const string CreateClient = "SystemIdentityClientCreateClient";

    [Fact]
    public async Task GetClients_HappyPath_ReturnsList()
    {
        MockIdentityClient.Setup(c => c.GetClients())
            .ReturnsAsync(new[] { new ClientDto { ClientId = "c1" }, new ClientDto { ClientId = "c2" } });

        var result = await ClientManagementTools.GetClients(MockServer.Object);

        result.IsSuccess.Should().BeTrue();
        result.Clients.Should().HaveCount(2);
    }

    [Fact]
    public async Task GetClients_Unauthenticated_ReturnsAuthError()
    {
        GivenUnauthenticated();
        var result = await ClientManagementTools.GetClients(MockServer.Object);
        result.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task GetClient_HappyPath_ReturnsClient()
    {
        MockIdentityClient.Setup(c => c.GetClient("c1"))
            .ReturnsAsync(new ClientDto { ClientId = "c1", ClientName = "Test" });

        var result = await ClientManagementTools.GetClient(MockServer.Object, "c1");

        result.IsSuccess.Should().BeTrue();
        result.Client!.ClientName.Should().Be("Test");
    }

    [Fact]
    public async Task AddClientCredentialsClient_BuildsCorrectDto()
    {
        CkMethods.Succeeds();

        var result = await ClientManagementTools.AddClientCredentialsClient(MockServer.Object,
            clientId: "ci-deploy", clientName: "CI", clientSecret: "s3cret",
            autoProvisionInChildTenants: true);

        result.IsSuccess.Should().BeTrue();
        var input = CkMethods.InputOf(CreateClient);
        CkMethodFake.Str(input, "clientId").Should().Be("ci-deploy");
        CkMethodFake.Str(input, "clientName").Should().Be("CI");
        CkMethodFake.Str(input, "clientSecret").Should().Be("s3cret");
        CkMethodFake.Bool(input, "requireClientSecret").Should().BeTrue();
        CkMethodFake.Bool(input, "enabled").Should().BeTrue();
        CkMethodFake.Bool(input, "autoProvisionInChildTenants").Should().BeTrue();
        CkMethodFake.Strings(input.GetProperty("allowedGrantTypes")).Should()
            .Contain(OidcConstants.GrantTypes.ClientCredentials);
        CkMethodFake.Strings(input.GetProperty("allowedScopes")).Should().Contain(CommonConstants.OctoApiFullAccess);
        MockIdentityClient.ShouldNotHaveCalled(nameof(IIdentityServicesClient.CreateClient));
    }

    [Fact]
    public async Task AddClientCredentialsClient_WithoutAutoProvision_SetsFlagNull()
    {
        CkMethods.Succeeds();

        await ClientManagementTools.AddClientCredentialsClient(MockServer.Object,
            "id", "name", "secret");

        CkMethodFake.Bool(CkMethods.InputOf(CreateClient), "autoProvisionInChildTenants").Should().BeNull();
    }

    [Fact]
    public async Task AddClientCredentialsClient_MissingFields_ReturnsValidationError()
    {
        var result = await ClientManagementTools.AddClientCredentialsClient(MockServer.Object, "", "", "");
        result.IsSuccess.Should().BeFalse();
        MockIdentityClient.ShouldNotHaveCalled(nameof(IIdentityServicesClient.CreateClient));
    }

    [Fact]
    public async Task AddDeviceCodeClient_BuildsCorrectDto()
    {
        CkMethods.Succeeds();

        await ClientManagementTools.AddDeviceCodeClient(MockServer.Object, "dev", "Device", "sec");

        var input = CkMethods.InputOf(CreateClient);
        CkMethodFake.Str(input, "clientId").Should().Be("dev");
        CkMethodFake.Bool(input, "enabled").Should().BeTrue();
        CkMethodFake.Strings(input.GetProperty("allowedGrantTypes")).Should().Contain(OidcConstants.GrantTypes.DeviceCode);
        MockIdentityClient.ShouldNotHaveCalled(nameof(IIdentityServicesClient.CreateClient));
    }

    [Fact]
    public async Task AddAuthorizationCodeClient_DefaultsRedirectToClientUri()
    {
        CkMethods.Succeeds();

        await ClientManagementTools.AddAuthorizationCodeClient(MockServer.Object,
            clientId: "web", clientName: "Web App", clientUri: "https://app.example.com");

        var input = CkMethods.InputOf(CreateClient);
        CkMethodFake.Str(input, "clientId").Should().Be("web");
        CkMethodFake.Str(input, "clientUri").Should().Be("https://app.example.com");
        CkMethodFake.Strings(input.GetProperty("allowedGrantTypes")).Should()
            .Contain(OidcConstants.GrantTypes.AuthorizationCode);
        CkMethodFake.Strings(input.GetProperty("redirectUris")).First().Should().Be("https://app.example.com/");
        MockIdentityClient.ShouldNotHaveCalled(nameof(IIdentityServicesClient.CreateClient));
    }

    [Fact]
    public async Task AddAuthorizationCodeClient_WithExplicitRedirect_UsesIt()
    {
        CkMethods.Succeeds();

        await ClientManagementTools.AddAuthorizationCodeClient(MockServer.Object,
            "web", "Web App", "https://app.example.com",
            redirectUri: "https://app.example.com/callback",
            frontChannelLogoutUri: "https://app.example.com/logout");

        var input = CkMethods.InputOf(CreateClient);
        CkMethodFake.Strings(input.GetProperty("redirectUris")).First().Should().Be("https://app.example.com/callback");
        CkMethodFake.Str(input, "frontChannelLogoutUri").Should().Be("https://app.example.com/logout");
        CkMethodFake.Bool(input, "frontChannelLogoutSessionRequired").Should().BeTrue();
    }

    [Fact]
    public async Task DeleteClient_WithoutConfirm_Refuses()
    {
        var result = await ClientManagementTools.DeleteClient(MockServer.Object, "c1");
        result.IsSuccess.Should().BeFalse();
        MockIdentityClient.ShouldNotHaveCalled(nameof(IIdentityServicesClient.DeleteClient));
    }

    [Fact]
    public async Task DeleteClient_WithConfirm_CallsSdk()
    {
        CkMethods.Succeeds();

        var result = await ClientManagementTools.DeleteClient(MockServer.Object, "c1", confirm: true);
        result.IsSuccess.Should().BeTrue();
        CkMethods.RtIdOf("SystemIdentityClientDeleteClient").Should().Be(ClientRtId);
        MockIdentityClient.ShouldNotHaveCalled(nameof(IIdentityServicesClient.DeleteClient));
    }

    [Fact]
    public async Task GetClientMirrors_HappyPath_ReturnsList()
    {
        MockIdentityClient.Setup(c => c.GetClientMirrors("c1"))
            .ReturnsAsync(new[] { new ClientMirrorDto("c1", "parent", "child-a", DateTime.UtcNow, 1), new ClientMirrorDto("c1", "parent", "child-a", DateTime.UtcNow, 1) });

        var result = await ClientManagementTools.GetClientMirrors(MockServer.Object, "c1");

        result.IsSuccess.Should().BeTrue();
        result.Mirrors.Should().HaveCount(2);
        result.TotalCount.Should().Be(2);
    }

    [Fact]
    public async Task ProvisionClientInExistingTenants_HappyPath_CallsSdk()
    {
        CkMethods.Succeeds(new Meshmakers.Octo.Backend.McpServices.MethodClients.DataTransferObjects.System.Identity.v3
            .RtClientMirrorBackfillResultRecordDto { ChildTenantsConsidered = 3, NewlyProvisioned = 2, AlreadyPresent = 1 });

        var result = await ClientManagementTools.ProvisionClientInExistingTenants(MockServer.Object, "c1");

        result.IsSuccess.Should().BeTrue();
        result.ClientId.Should().Be("c1");
        result.Backfill.Should().Be(new ClientMirrorBackfillResponseDto(3, 2, 1));
        CkMethods.RtIdOf("SystemIdentityClientProvisionMirrors").Should().Be(ClientRtId);
    }

    [Fact]
    public async Task ProvisionClientInTenant_HappyPath_CallsSdk()
    {
        CkMethods.Succeeds(new Meshmakers.Octo.Backend.McpServices.MethodClients.DataTransferObjects.System.Identity.v3
            .RtClientMirrorProvisionResultRecordDto { FlaggedClientsConsidered = 1, NewlyProvisioned = 1, AlreadyPresent = 0 });

        var result = await ClientManagementTools.ProvisionClientInTenant(MockServer.Object, "c1", "child-a");

        result.IsSuccess.Should().BeTrue();
        result.ChildTenantId.Should().Be("child-a");
        result.Provision.Should().Be(new ClientMirrorProvisionResponseDto(1, 1, 0));
        CkMethods.RtIdOf("SystemIdentityClientProvisionMirrorInTenant").Should().Be(ClientRtId);
        CkMethodFake.Str(CkMethods.InputOf("SystemIdentityClientProvisionMirrorInTenant"), "childTenantId")
            .Should().Be("child-a");
    }

    [Fact]
    public async Task UnprovisionClientFromTenant_WithoutConfirm_Refuses()
    {
        var result = await ClientManagementTools.UnprovisionClientFromTenant(MockServer.Object, "c1", "child-a");
        result.IsSuccess.Should().BeFalse();
        MockIdentityClient.ShouldNotHaveCalled(nameof(IIdentityServicesClient.UnprovisionClientFromTenant));
    }

    [Fact]
    public async Task UnprovisionClientFromTenant_WithConfirm_CallsSdk()
    {
        CkMethods.Succeeds();

        var result = await ClientManagementTools.UnprovisionClientFromTenant(MockServer.Object,
            "c1", "child-a", confirm: true);

        result.IsSuccess.Should().BeTrue();
        CkMethods.RtIdOf("SystemIdentityClientRemoveMirror").Should().Be(ClientRtId);
        CkMethodFake.Str(CkMethods.InputOf("SystemIdentityClientRemoveMirror"), "childTenantId").Should().Be("child-a");
    }

    [Fact]
    public async Task SetClientAutoProvision_True_CallsSdk()
    {
        CkMethods.Succeeds();

        var result = await ClientManagementTools.SetClientAutoProvision(MockServer.Object, "c1", true);

        result.IsSuccess.Should().BeTrue();
        CkMethodFake.Bool(CkMethods.InputOf("SystemIdentityClientSetMirrorAutoProvision"), "enabled").Should().BeTrue();
    }

    [Fact]
    public async Task SetClientAutoProvision_False_CallsSdk()
    {
        CkMethods.Succeeds();

        var result = await ClientManagementTools.SetClientAutoProvision(MockServer.Object, "c1", false);

        result.IsSuccess.Should().BeTrue();
        CkMethods.RtIdOf("SystemIdentityClientSetMirrorAutoProvision").Should().Be(ClientRtId);
        CkMethodFake.Bool(CkMethods.InputOf("SystemIdentityClientSetMirrorAutoProvision"), "enabled").Should().BeFalse();
    }

    [Fact]
    public async Task AddScopeToClient_HappyPath_AppendsScopeAndCallsUpdate()
    {
        MockIdentityClient.Setup(c => c.GetClient("c1"))
            .ReturnsAsync(new ClientDto { ClientId = "c1", RtId = ClientRtId, AllowedScopes = ["existing"] });
        CkMethods.Succeeds();

        var result = await ClientManagementTools.AddScopeToClient(MockServer.Object, "c1", "octo_api");

        result.IsSuccess.Should().BeTrue();
        var input = CkMethods.InputOf("SystemIdentityClientUpdateClient");
        CkMethodFake.Strings(input.GetProperty("allowedScopes")).Should().Equal("existing", "octo_api");
        // Only the scopes change: no secret and no other field is sent.
        CkMethodFake.Str(input, "clientSecret").Should().BeNull();
        MockIdentityClient.ShouldNotHaveCalled(nameof(IIdentityServicesClient.UpdateClient));
    }

    [Fact]
    public async Task AddScopeToClient_ClientWithNoScopes_StartsFromEmptyList()
    {
        MockIdentityClient.Setup(c => c.GetClient("c1"))
            .ReturnsAsync(new ClientDto { ClientId = "c1", RtId = ClientRtId, AllowedScopes = null });
        CkMethods.Succeeds();

        var result = await ClientManagementTools.AddScopeToClient(MockServer.Object, "c1", "first");

        result.IsSuccess.Should().BeTrue();
        CkMethods.RtIdOf("SystemIdentityClientUpdateClient").Should().Be(ClientRtId);
        CkMethodFake.Strings(CkMethods.InputOf("SystemIdentityClientUpdateClient").GetProperty("allowedScopes"))
            .Should().Equal("first");
    }

    [Fact]
    public async Task AddScopeToClient_MissingArgs_ReturnsValidationError()
    {
        var result = await ClientManagementTools.AddScopeToClient(MockServer.Object, "", "");

        result.IsSuccess.Should().BeFalse();
        MockIdentityClient.ShouldNotHaveCalled(nameof(IIdentityServicesClient.UpdateClient));
    }

    // ---------- AB#4183: client role / group assignment ----------

    [Fact]
    public async Task GetClientRoles_HappyPath_ReturnsRoleIds()
    {
        MockIdentityClient.Setup(c => c.GetClientDirectRoles("c1"))
            .ReturnsAsync(new[] { "660000000000000000000002" });

        var result = await ClientManagementTools.GetClientRoles(MockServer.Object, "c1");

        result.IsSuccess.Should().BeTrue();
        result.RoleIds.Should().ContainSingle().Which.Should().Be("660000000000000000000002");
    }

    [Fact]
    public async Task GetClientRoles_Unauthenticated_ReturnsAuthError()
    {
        GivenUnauthenticated();
        var result = await ClientManagementTools.GetClientRoles(MockServer.Object, "c1");
        result.IsSuccess.Should().BeFalse();
        MockIdentityClient.Verify(c => c.GetClientDirectRoles(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task AddClientToRole_HappyPath_CallsSdk()
    {
        CkMethods.Succeeds();

        var result = await ClientManagementTools.AddClientToRole(MockServer.Object, "c1", "DataAnalyst");

        result.IsSuccess.Should().BeTrue();
        CkMethods.RtIdOf("SystemIdentityClientAddRole").Should().Be(ClientRtId);
        CkMethodFake.Str(CkMethods.InputOf("SystemIdentityClientAddRole"), "roleName").Should().Be("DataAnalyst");
        MockIdentityClient.ShouldNotHaveCalled(nameof(IIdentityServicesClient.AddRoleToClient));
    }

    [Fact]
    public async Task AddClientToRole_MissingArgs_ReturnsValidationError()
    {
        var result = await ClientManagementTools.AddClientToRole(MockServer.Object, "", "");
        result.IsSuccess.Should().BeFalse();
        MockIdentityClient.ShouldNotHaveCalled(nameof(IIdentityServicesClient.AddRoleToClient));
    }

    [Fact]
    public async Task AddClientToRole_Unauthenticated_ReturnsAuthError()
    {
        GivenUnauthenticated();
        var result = await ClientManagementTools.AddClientToRole(MockServer.Object, "c1", "DataAnalyst");
        result.IsSuccess.Should().BeFalse();
        MockIdentityClient.ShouldNotHaveCalled(nameof(IIdentityServicesClient.AddRoleToClient));
    }

    [Fact]
    public async Task RemoveClientFromRole_WithoutConfirm_Refuses()
    {
        var result = await ClientManagementTools.RemoveClientFromRole(MockServer.Object, "c1", "DataAnalyst");
        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("confirm=true");
        MockIdentityClient.ShouldNotHaveCalled(nameof(IIdentityServicesClient.RemoveRoleFromClient));
    }

    [Fact]
    public async Task RemoveClientFromRole_WithConfirm_CallsSdk()
    {
        CkMethods.Succeeds();

        var result = await ClientManagementTools.RemoveClientFromRole(
            MockServer.Object, "c1", "DataAnalyst", confirm: true);
        result.IsSuccess.Should().BeTrue();
        CkMethods.RtIdOf("SystemIdentityClientRemoveRole").Should().Be(ClientRtId);
        CkMethodFake.Str(CkMethods.InputOf("SystemIdentityClientRemoveRole"), "roleName").Should().Be("DataAnalyst");
    }

    [Fact]
    public async Task UpdateClientRoles_HappyPath_CallsSdkReplaceAll()
    {
        CkMethods.Succeeds();
        var roleIds = new List<string> { "660000000000000000000002", "660000000000000000000009" };
        var result = await ClientManagementTools.UpdateClientRoles(MockServer.Object, "c1", roleIds);

        result.IsSuccess.Should().BeTrue();
        CkMethods.RtIdOf("SystemIdentityClientSetRoles").Should().Be(ClientRtId);
        // Client.SetRoles-1 takes role names: the tool maps the role ids of the tenant to their names.
        CkMethodFake.Strings(CkMethods.InputOf("SystemIdentityClientSetRoles").GetProperty("roleNames"))
            .Should().Equal("DataAnalyst", "Viewer");
    }

    [Fact]
    public async Task AddClientToGroup_HappyPath_CallsSdk()
    {
        CkMethods.Succeeds();

        const string groupId = "507f1f77bcf86cd799439011";
        var result = await ClientManagementTools.AddClientToGroup(MockServer.Object, groupId, "c1");

        result.IsSuccess.Should().BeTrue();
        CkMethods.RtIdOf("SystemIdentityGroupAddMember").Should().Be(groupId);
        CkMethodFake.Str(CkMethods.InputOf("SystemIdentityGroupAddMember"), "memberId").Should().Be("c1");
        MockIdentityClient.ShouldNotHaveCalled(nameof(IIdentityServicesClient.AddClientToGroup));
    }

    [Fact]
    public async Task RemoveClientFromGroup_WithoutConfirm_Refuses()
    {
        const string groupId = "507f1f77bcf86cd799439011";
        var result = await ClientManagementTools.RemoveClientFromGroup(MockServer.Object, groupId, "c1");
        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("confirm=true");
        MockIdentityClient.ShouldNotHaveCalled(nameof(IIdentityServicesClient.RemoveClientFromGroup));
    }

    [Fact]
    public async Task RemoveClientFromGroup_WithConfirm_CallsSdk()
    {
        CkMethods.Succeeds();

        const string groupId = "507f1f77bcf86cd799439011";
        var result = await ClientManagementTools.RemoveClientFromGroup(
            MockServer.Object, groupId, "c1", confirm: true);
        result.IsSuccess.Should().BeTrue();
        CkMethods.RtIdOf("SystemIdentityGroupRemoveMember").Should().Be(groupId);
        CkMethodFake.Str(CkMethods.InputOf("SystemIdentityGroupRemoveMember"), "memberId").Should().Be("c1");
    }
}
