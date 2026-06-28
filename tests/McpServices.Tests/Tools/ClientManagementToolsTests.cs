using FluentAssertions;
using IdentityModel;
using Meshmakers.Octo.Backend.McpServices.Tools;
using Meshmakers.Octo.Communication.Contracts;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Moq;
using Xunit;

namespace McpServices.Tests.Tools;

public class ClientManagementToolsTests : ToolTestBase
{
    public ClientManagementToolsTests()
    {
        GivenAuthenticated();
    }

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
        var result = await ClientManagementTools.AddClientCredentialsClient(MockServer.Object,
            clientId: "ci-deploy", clientName: "CI", clientSecret: "s3cret",
            autoProvisionInChildTenants: true);

        result.IsSuccess.Should().BeTrue();
        MockIdentityClient.Verify(c => c.CreateClient(It.Is<ClientDto>(d =>
            d.ClientId == "ci-deploy" &&
            d.ClientName == "CI" &&
            d.ClientSecret == "s3cret" &&
            d.RequireClientSecret == true &&
            d.IsEnabled == true &&
            d.AutoProvisionInChildTenants == true &&
            d.AllowedGrantTypes!.Contains(OidcConstants.GrantTypes.ClientCredentials) &&
            d.AllowedScopes!.Contains(CommonConstants.OctoApiFullAccess))), Times.Once);
    }

    [Fact]
    public async Task AddClientCredentialsClient_WithoutAutoProvision_SetsFlagNull()
    {
        await ClientManagementTools.AddClientCredentialsClient(MockServer.Object,
            "id", "name", "secret");

        MockIdentityClient.Verify(c => c.CreateClient(It.Is<ClientDto>(d =>
            d.AutoProvisionInChildTenants == null)), Times.Once);
    }

    [Fact]
    public async Task AddClientCredentialsClient_MissingFields_ReturnsValidationError()
    {
        var result = await ClientManagementTools.AddClientCredentialsClient(MockServer.Object, "", "", "");
        result.IsSuccess.Should().BeFalse();
        MockIdentityClient.Verify(c => c.CreateClient(It.IsAny<ClientDto>()), Times.Never);
    }

    [Fact]
    public async Task AddDeviceCodeClient_BuildsCorrectDto()
    {
        await ClientManagementTools.AddDeviceCodeClient(MockServer.Object, "dev", "Device", "sec");

        MockIdentityClient.Verify(c => c.CreateClient(It.Is<ClientDto>(d =>
            d.ClientId == "dev" &&
            d.IsEnabled == true &&
            d.AllowedGrantTypes!.Contains(OidcConstants.GrantTypes.DeviceCode))), Times.Once);
    }

    [Fact]
    public async Task AddAuthorizationCodeClient_DefaultsRedirectToClientUri()
    {
        await ClientManagementTools.AddAuthorizationCodeClient(MockServer.Object,
            clientId: "web", clientName: "Web App", clientUri: "https://app.example.com");

        MockIdentityClient.Verify(c => c.CreateClient(It.Is<ClientDto>(d =>
            d.ClientId == "web" &&
            d.ClientUri == "https://app.example.com" &&
            d.AllowedGrantTypes!.Contains(OidcConstants.GrantTypes.AuthorizationCode) &&
            d.RedirectUris!.First() == "https://app.example.com/")), Times.Once);
    }

    [Fact]
    public async Task AddAuthorizationCodeClient_WithExplicitRedirect_UsesIt()
    {
        await ClientManagementTools.AddAuthorizationCodeClient(MockServer.Object,
            "web", "Web App", "https://app.example.com",
            redirectUri: "https://app.example.com/callback",
            frontChannelLogoutUri: "https://app.example.com/logout");

        MockIdentityClient.Verify(c => c.CreateClient(It.Is<ClientDto>(d =>
            d.RedirectUris!.First() == "https://app.example.com/callback" &&
            d.FrontChannelLogoutUri == "https://app.example.com/logout" &&
            d.FrontChannelLogoutSessionRequired == true)), Times.Once);
    }

    [Fact]
    public async Task DeleteClient_WithoutConfirm_Refuses()
    {
        var result = await ClientManagementTools.DeleteClient(MockServer.Object, "c1");
        result.IsSuccess.Should().BeFalse();
        MockIdentityClient.Verify(c => c.DeleteClient(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task DeleteClient_WithConfirm_CallsSdk()
    {
        var result = await ClientManagementTools.DeleteClient(MockServer.Object, "c1", confirm: true);
        result.IsSuccess.Should().BeTrue();
        MockIdentityClient.Verify(c => c.DeleteClient("c1"), Times.Once);
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
        MockIdentityClient.Setup(c => c.ProvisionClientInExistingTenants("c1"))
            .ReturnsAsync(new ClientMirrorBackfillResponseDto(3, 2, 1));

        var result = await ClientManagementTools.ProvisionClientInExistingTenants(MockServer.Object, "c1");

        result.IsSuccess.Should().BeTrue();
        result.ClientId.Should().Be("c1");
    }

    [Fact]
    public async Task ProvisionClientInTenant_HappyPath_CallsSdk()
    {
        MockIdentityClient.Setup(c => c.ProvisionClientInTenant("c1", "child-a"))
            .ReturnsAsync(new ClientMirrorProvisionResponseDto(1, 1, 0));

        var result = await ClientManagementTools.ProvisionClientInTenant(MockServer.Object, "c1", "child-a");

        result.IsSuccess.Should().BeTrue();
        result.ChildTenantId.Should().Be("child-a");
    }

    [Fact]
    public async Task UnprovisionClientFromTenant_WithoutConfirm_Refuses()
    {
        var result = await ClientManagementTools.UnprovisionClientFromTenant(MockServer.Object, "c1", "child-a");
        result.IsSuccess.Should().BeFalse();
        MockIdentityClient.Verify(c => c.UnprovisionClientFromTenant(It.IsAny<string>(), It.IsAny<string>()),
            Times.Never);
    }

    [Fact]
    public async Task UnprovisionClientFromTenant_WithConfirm_CallsSdk()
    {
        var result = await ClientManagementTools.UnprovisionClientFromTenant(MockServer.Object,
            "c1", "child-a", confirm: true);

        result.IsSuccess.Should().BeTrue();
        MockIdentityClient.Verify(c => c.UnprovisionClientFromTenant("c1", "child-a"), Times.Once);
    }

    [Fact]
    public async Task SetClientAutoProvision_True_CallsSdk()
    {
        var result = await ClientManagementTools.SetClientAutoProvision(MockServer.Object, "c1", true);

        result.IsSuccess.Should().BeTrue();
        MockIdentityClient.Verify(c => c.SetClientAutoProvisionInChildTenants("c1", true), Times.Once);
    }

    [Fact]
    public async Task SetClientAutoProvision_False_CallsSdk()
    {
        var result = await ClientManagementTools.SetClientAutoProvision(MockServer.Object, "c1", false);

        result.IsSuccess.Should().BeTrue();
        MockIdentityClient.Verify(c => c.SetClientAutoProvisionInChildTenants("c1", false), Times.Once);
    }

    [Fact]
    public async Task AddScopeToClient_HappyPath_AppendsScopeAndCallsUpdate()
    {
        MockIdentityClient.Setup(c => c.GetClient("c1"))
            .ReturnsAsync(new ClientDto { ClientId = "c1", AllowedScopes = ["existing"] });

        var result = await ClientManagementTools.AddScopeToClient(MockServer.Object, "c1", "octo_api");

        result.IsSuccess.Should().BeTrue();
        MockIdentityClient.Verify(c => c.UpdateClient("c1", It.Is<ClientDto>(d =>
            d.AllowedScopes != null &&
            d.AllowedScopes.Contains("existing") && d.AllowedScopes.Contains("octo_api"))), Times.Once);
    }

    [Fact]
    public async Task AddScopeToClient_ClientWithNoScopes_StartsFromEmptyList()
    {
        MockIdentityClient.Setup(c => c.GetClient("c1"))
            .ReturnsAsync(new ClientDto { ClientId = "c1", AllowedScopes = null });

        var result = await ClientManagementTools.AddScopeToClient(MockServer.Object, "c1", "first");

        result.IsSuccess.Should().BeTrue();
        MockIdentityClient.Verify(c => c.UpdateClient("c1", It.Is<ClientDto>(d =>
            d.AllowedScopes!.Single() == "first")), Times.Once);
    }

    [Fact]
    public async Task AddScopeToClient_MissingArgs_ReturnsValidationError()
    {
        var result = await ClientManagementTools.AddScopeToClient(MockServer.Object, "", "");

        result.IsSuccess.Should().BeFalse();
        MockIdentityClient.Verify(c => c.UpdateClient(It.IsAny<string>(), It.IsAny<ClientDto>()),
            Times.Never);
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
        var result = await ClientManagementTools.AddClientToRole(MockServer.Object, "c1", "DataAnalyst");

        result.IsSuccess.Should().BeTrue();
        MockIdentityClient.Verify(c => c.AddRoleToClient("c1", "DataAnalyst"), Times.Once);
    }

    [Fact]
    public async Task AddClientToRole_MissingArgs_ReturnsValidationError()
    {
        var result = await ClientManagementTools.AddClientToRole(MockServer.Object, "", "");
        result.IsSuccess.Should().BeFalse();
        MockIdentityClient.Verify(c => c.AddRoleToClient(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task AddClientToRole_Unauthenticated_ReturnsAuthError()
    {
        GivenUnauthenticated();
        var result = await ClientManagementTools.AddClientToRole(MockServer.Object, "c1", "DataAnalyst");
        result.IsSuccess.Should().BeFalse();
        MockIdentityClient.Verify(c => c.AddRoleToClient(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task RemoveClientFromRole_WithoutConfirm_Refuses()
    {
        var result = await ClientManagementTools.RemoveClientFromRole(MockServer.Object, "c1", "DataAnalyst");
        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("confirm=true");
        MockIdentityClient.Verify(c => c.RemoveRoleFromClient(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task RemoveClientFromRole_WithConfirm_CallsSdk()
    {
        var result = await ClientManagementTools.RemoveClientFromRole(
            MockServer.Object, "c1", "DataAnalyst", confirm: true);
        result.IsSuccess.Should().BeTrue();
        MockIdentityClient.Verify(c => c.RemoveRoleFromClient("c1", "DataAnalyst"), Times.Once);
    }

    [Fact]
    public async Task UpdateClientRoles_HappyPath_CallsSdkReplaceAll()
    {
        var roleIds = new List<string> { "660000000000000000000002", "660000000000000000000009" };
        var result = await ClientManagementTools.UpdateClientRoles(MockServer.Object, "c1", roleIds);

        result.IsSuccess.Should().BeTrue();
        MockIdentityClient.Verify(c => c.UpdateClientRoles("c1", roleIds), Times.Once);
    }

    [Fact]
    public async Task AddClientToGroup_HappyPath_CallsSdk()
    {
        const string groupId = "507f1f77bcf86cd799439011";
        var result = await ClientManagementTools.AddClientToGroup(MockServer.Object, groupId, "c1");

        result.IsSuccess.Should().BeTrue();
        MockIdentityClient.Verify(c => c.AddClientToGroup(It.IsAny<OctoObjectId>(), "c1"), Times.Once);
    }

    [Fact]
    public async Task RemoveClientFromGroup_WithoutConfirm_Refuses()
    {
        const string groupId = "507f1f77bcf86cd799439011";
        var result = await ClientManagementTools.RemoveClientFromGroup(MockServer.Object, groupId, "c1");
        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("confirm=true");
        MockIdentityClient.Verify(c => c.RemoveClientFromGroup(It.IsAny<OctoObjectId>(), It.IsAny<string>()),
            Times.Never);
    }

    [Fact]
    public async Task RemoveClientFromGroup_WithConfirm_CallsSdk()
    {
        const string groupId = "507f1f77bcf86cd799439011";
        var result = await ClientManagementTools.RemoveClientFromGroup(
            MockServer.Object, groupId, "c1", confirm: true);
        result.IsSuccess.Should().BeTrue();
        MockIdentityClient.Verify(c => c.RemoveClientFromGroup(It.IsAny<OctoObjectId>(), "c1"), Times.Once);
    }
}
