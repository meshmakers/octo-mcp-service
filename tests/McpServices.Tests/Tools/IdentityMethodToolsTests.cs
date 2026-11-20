using System.Text.Json;
using FluentAssertions;
using Meshmakers.Octo.Backend.McpServices.MethodClients.DataTransferObjects.System.Identity.v3;
using Meshmakers.Octo.Backend.McpServices.Tools;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Moq;
using Xunit;

namespace McpServices.Tests.Tools;

/// <summary>
///     Identity wave 2: every identity write tool runs its CK method through a generated method client of
///     <c>System.Identity</c> (operation name and target of the request), makes no REST write on that path, reports a
///     refusal as <c>CODE: message (params)</c> without a REST call, and has no REST fallback: a method the identity
///     service does not offer (no live handler, a schema without the method, a target without runtime id) is reported with
///     the identity version the tool needs, and nothing else is called.
/// </summary>
public class IdentityMethodToolsTests : ToolTestBase
{
    private const string ClientRtId = "65f1c0ffee00000000000c01";
    private const string RoleRtId = "65f1c0ffee0000000000aa01";
    private const string GroupRtId = "65f1c0ffee0000000000bb01";
    private const string ChildGroupRtId = "65f1c0ffee0000000000bb02";
    private const string UserRtId = "65f1c0ffee0000000000cc01";
    private const string ScopeRtId = "65f1c0ffee0000000000dd01";
    private const string ResourceRtId = "65f1c0ffee0000000000ee01";
    private const string RuleRtId = "65f1c0ffee0000000000ff01";
    private const string MappingRtId = "65f1c0ffee0000000000ab01";
    private const string ProviderRtId = "65f1c0ffee0000000000ac01";

    public IdentityMethodToolsTests()
    {
        GivenAuthenticated();

        // Lookups (REST reads and GraphQL queries) the tools make to address the method target by runtime id.
        MockIdentityClient.Setup(c => c.GetClient(It.IsAny<string>()))
            .ReturnsAsync((string id) => new ClientDto { ClientId = id, RtId = ClientRtId, AllowedScopes = ["octo_api"] });
        MockIdentityClient.Setup(c => c.GetRoleByName(It.IsAny<string>()))
            .ReturnsAsync((string name) => new RoleDto { Id = RoleRtId, Name = name });
        MockIdentityClient.Setup(c => c.GetRoles())
            .ReturnsAsync([new RoleDto { Id = RoleRtId, Name = "Reader" }]);
        MockIdentityClient.Setup(c => c.GetIdentityProvider(It.IsAny<OctoObjectId>()))
            .ReturnsAsync(new GoogleIdentityProviderDto { Name = "google", ClientId = "cid", IsEnabled = true });
        MockIdentityClient.Setup(c => c.GetExternalTenantUserMapping(It.IsAny<OctoObjectId>()))
            .ReturnsAsync(new ExternalTenantUserMappingDto
            {
                Id = new OctoObjectId(MappingRtId), SourceTenantId = "parent", SourceUserId = UserRtId,
                SourceUserName = "alice"
            });
        CkMethods.GivenQueryReturns(new RtApiScopeDto { RtId = new OctoObjectId(ScopeRtId), Name = "scope1" });
        CkMethods.GivenQueryReturns(new RtApiResourceDto { RtId = new OctoObjectId(ResourceRtId), Name = "res1" });

        // Answers of the REST fallback where the tool reads a result.
        MockIdentityClient.Setup(c => c.ApplyClientOverlay(It.IsAny<string>(), It.IsAny<ApplyOverlayUrisDto>()))
            .ReturnsAsync(new ApplyOverlayUrisResultDto
            {
                OverlayName = "dev", ClientId = "app",
                RedirectUris = new ApplyOverlayUrisListCountDto { Added = 1 },
                PostLogoutRedirectUris = new ApplyOverlayUrisListCountDto(),
                AllowedCorsOrigins = new ApplyOverlayUrisListCountDto()
            });
        MockIdentityClient.Setup(c => c.CleanOverlayEntries(It.IsAny<string?>()))
            .ReturnsAsync(new CleanOverlayEntriesResultDto { ClientResults = [] });
        MockIdentityClient.Setup(c => c.ProvisionClientInExistingTenants(It.IsAny<string>()))
            .ReturnsAsync(new ClientMirrorBackfillResponseDto(2, 1, 1));
        MockIdentityClient.Setup(c => c.ProvisionClientInTenant(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(new ClientMirrorProvisionResponseDto(1, 1, 0));
        MockIdentityClient.Setup(c => c.GrantExternalTenantUser(It.IsAny<CreateExternalTenantUserMappingDto>()))
            .ReturnsAsync(new ExternalTenantUserMappingDto { Id = new OctoObjectId(MappingRtId), SourceUserName = "alice" });
        MockIdentityClient.Setup(c => c.ProvisionCurrentUser(It.IsAny<string>()))
            .ReturnsAsync(new ExternalTenantUserMappingDto { SourceTenantId = "parent" });
        MockIdentityClient.Setup(c => c.CreateApiSecretForClient(It.IsAny<string>(), It.IsAny<ApiSecretDto>()))
            .ReturnsAsync(new ApiSecretDto { ValueEncrypted = "stored", ValueClearText = "clear" });
        MockIdentityClient.Setup(c => c.CreateApiSecretForApiResource(It.IsAny<string>(), It.IsAny<ApiSecretDto>()))
            .ReturnsAsync(new ApiSecretDto { ValueEncrypted = "stored", ValueClearText = "clear" });
    }

    /// <summary>A tool call: the tool, the operation it must send, its result for a success and whether it carries a secret.</summary>
    private sealed record ToolCase(
        Func<IdentityMethodToolsTests, Task<object>> Invoke,
        string Operation,
        string? ExpectedRtId = null,
        object? Result = null,
        bool Secret = false);

    private static readonly Dictionary<string, ToolCase> Cases = new()
    {
        // Groups
        ["create_group"] = new(t => Box(GroupManagementTools.CreateGroup(t.MockServer.Object, "ops", "d", [RoleRtId])),
            "SystemIdentityGroupCreateGroup", Result: GroupRtId),
        ["update_group"] = new(t => Box(GroupManagementTools.UpdateGroup(t.MockServer.Object, GroupRtId, "ops2")),
            "SystemIdentityGroupUpdateGroup", GroupRtId),
        ["delete_group"] = new(t => Box(GroupManagementTools.DeleteGroup(t.MockServer.Object, GroupRtId, confirm: true)),
            "SystemIdentityGroupDeleteGroup", GroupRtId),
        ["update_group_roles"] = new(t => Box(GroupManagementTools.UpdateGroupRoles(t.MockServer.Object, GroupRtId, [RoleRtId])),
            "SystemIdentityGroupSetRoles", GroupRtId),
        ["add_user_to_group"] = new(t => Box(GroupManagementTools.AddUserToGroup(t.MockServer.Object, GroupRtId, UserRtId)),
            "SystemIdentityGroupAddMember", GroupRtId),
        ["remove_user_from_group"] = new(
            t => Box(GroupManagementTools.RemoveUserFromGroup(t.MockServer.Object, GroupRtId, UserRtId, confirm: true)),
            "SystemIdentityGroupRemoveMember", GroupRtId),
        ["add_group_to_group"] = new(t => Box(GroupManagementTools.AddGroupToGroup(t.MockServer.Object, GroupRtId, ChildGroupRtId)),
            "SystemIdentityGroupAddChildGroup", GroupRtId),
        ["remove_group_from_group"] = new(
            t => Box(GroupManagementTools.RemoveGroupFromGroup(t.MockServer.Object, GroupRtId, ChildGroupRtId, confirm: true)),
            "SystemIdentityGroupRemoveChildGroup", GroupRtId),
        // Roles
        ["create_role"] = new(t => Box(RoleManagementTools.CreateRole(t.MockServer.Object, "Auditor")),
            "SystemIdentityRoleCreateRole", Result: RoleRtId),
        ["update_role"] = new(t => Box(RoleManagementTools.UpdateRole(t.MockServer.Object, "Auditor", "Auditors")),
            "SystemIdentityRoleUpdateRole", RoleRtId),
        ["delete_role"] = new(t => Box(RoleManagementTools.DeleteRole(t.MockServer.Object, "Auditor", confirm: true)),
            "SystemIdentityRoleDeleteRole", RoleRtId),
        // API scopes and API resources
        ["create_api_scope"] = new(t => Box(ApiScopeTools.CreateApiScope(t.MockServer.Object, "scope1")),
            "SystemIdentityApiScopeCreateApiScope", Result: ScopeRtId),
        ["update_api_scope"] = new(t => Box(ApiScopeTools.UpdateApiScope(t.MockServer.Object, "scope1", "Scope 1")),
            "SystemIdentityApiScopeUpdateApiScope", ScopeRtId),
        ["delete_api_scope"] = new(t => Box(ApiScopeTools.DeleteApiScope(t.MockServer.Object, "scope1", confirm: true)),
            "SystemIdentityApiScopeDeleteApiScope", ScopeRtId),
        ["create_api_resource"] = new(t => Box(ApiResourceTools.CreateApiResource(t.MockServer.Object, "res1", scopes: ["scope1"])),
            "SystemIdentityApiResourceCreateApiResource", Result: ResourceRtId),
        ["update_api_resource"] = new(t => Box(ApiResourceTools.UpdateApiResource(t.MockServer.Object, "res1", "Res 1")),
            "SystemIdentityApiResourceUpdateApiResource", ResourceRtId),
        ["delete_api_resource"] = new(t => Box(ApiResourceTools.DeleteApiResource(t.MockServer.Object, "res1", confirm: true)),
            "SystemIdentityApiResourceDeleteApiResource", ResourceRtId),
        // E-mail domain group rules
        ["create_email_domain_group_rule"] = new(
            t => Box(EmailDomainGroupRuleTools.CreateRule(t.MockServer.Object, "*.example.com", GroupRtId)),
            "SystemIdentityEmailDomainGroupRuleCreateEmailDomainGroupRule", Result: RuleRtId),
        ["update_email_domain_group_rule"] = new(
            t => Box(EmailDomainGroupRuleTools.UpdateRule(t.MockServer.Object, RuleRtId, "*.example.org", GroupRtId)),
            "SystemIdentityEmailDomainGroupRuleUpdateEmailDomainGroupRule", RuleRtId),
        ["delete_email_domain_group_rule"] = new(
            t => Box(EmailDomainGroupRuleTools.DeleteRule(t.MockServer.Object, RuleRtId, confirm: true)),
            "SystemIdentityEmailDomainGroupRuleDeleteEmailDomainGroupRule", RuleRtId),
        // External tenant user mappings and admin provisioning
        ["create_external_tenant_user_mapping"] = new(
            t => Box(ExternalTenantUserMappingTools.CreateMapping(t.MockServer.Object, "parent", UserRtId, "alice", ["Reader"])),
            "SystemIdentityExternalTenantUserMappingCreateMapping",
            Result: new RtAdminProvisioningResultRecordDto { MappingRtId = MappingRtId }),
        ["update_external_tenant_user_mapping"] = new(
            t => Box(ExternalTenantUserMappingTools.UpdateMapping(t.MockServer.Object, MappingRtId, ["Reader"])),
            "SystemIdentityExternalTenantUserMappingUpdateMapping", MappingRtId),
        ["delete_external_tenant_user_mapping"] = new(
            t => Box(ExternalTenantUserMappingTools.DeleteMapping(t.MockServer.Object, MappingRtId, confirm: true)),
            "SystemIdentityExternalTenantUserMappingDeleteMapping", MappingRtId),
        ["create_admin_provisioning_mapping"] = new(
            t => Box(AdminProvisioningTools.CreateMapping(t.MockServer.Object, "child", "parent", UserRtId, "alice")),
            "SystemIdentityExternalTenantUserMappingProvisionAdmin",
            Result: new RtAdminProvisioningResultRecordDto { MappingRtId = MappingRtId }),
        ["provision_current_user_as_admin"] = new(
            t => Box(AdminProvisioningTools.ProvisionCurrentUser(t.MockServer.Object, "child")),
            "SystemIdentityExternalTenantUserMappingProvisionCurrentUser",
            Result: new RtAdminProvisioningResultRecordDto { SourceTenantId = "parent", Outcome = "created" }),
        ["delete_admin_provisioning_mapping"] = new(
            t => Box(AdminProvisioningTools.DeleteMapping(t.MockServer.Object, "child", MappingRtId, confirm: true)),
            "SystemIdentityExternalTenantUserMappingRemoveAdminProvisioning"),
        // Clients (no secret)
        ["delete_client"] = new(t => Box(ClientManagementTools.DeleteClient(t.MockServer.Object, "app", confirm: true)),
            "SystemIdentityClientDeleteClient", ClientRtId),
        ["set_client_auto_provision"] = new(t => Box(ClientManagementTools.SetClientAutoProvision(t.MockServer.Object, "app", true)),
            "SystemIdentityClientSetMirrorAutoProvision", ClientRtId),
        ["add_client_to_role"] = new(t => Box(ClientManagementTools.AddClientToRole(t.MockServer.Object, "app", "Reader")),
            "SystemIdentityClientAddRole", ClientRtId),
        ["remove_client_from_role"] = new(
            t => Box(ClientManagementTools.RemoveClientFromRole(t.MockServer.Object, "app", "Reader", confirm: true)),
            "SystemIdentityClientRemoveRole", ClientRtId),
        ["update_client_roles"] = new(t => Box(ClientManagementTools.UpdateClientRoles(t.MockServer.Object, "app", [RoleRtId])),
            "SystemIdentityClientSetRoles", ClientRtId),
        ["add_client_to_group"] = new(t => Box(ClientManagementTools.AddClientToGroup(t.MockServer.Object, GroupRtId, "app")),
            "SystemIdentityGroupAddMember", GroupRtId),
        ["remove_client_from_group"] = new(
            t => Box(ClientManagementTools.RemoveClientFromGroup(t.MockServer.Object, GroupRtId, "app", confirm: true)),
            "SystemIdentityGroupRemoveMember", GroupRtId),
        ["apply_client_overlay"] = new(
            t => Box(ClientOverlayTools.ApplyClientOverlay(t.MockServer.Object, "app", "dev", ["https://dev.example.com/"])),
            "SystemIdentityClientApplyOverlayUris", ClientRtId),
        ["clean_client_overlays"] = new(t => Box(ClientOverlayTools.CleanClientOverlays(t.MockServer.Object, confirm: true)),
            "SystemIdentityClientCleanOverlayEntries"),
        ["provision_client_in_existing_tenants"] = new(
            t => Box(ClientManagementTools.ProvisionClientInExistingTenants(t.MockServer.Object, "app")),
            "SystemIdentityClientProvisionMirrors", ClientRtId),
        ["provision_client_in_tenant"] = new(
            t => Box(ClientManagementTools.ProvisionClientInTenant(t.MockServer.Object, "app", "child")),
            "SystemIdentityClientProvisionMirrorInTenant", ClientRtId),
        ["unprovision_client_from_tenant"] = new(
            t => Box(ClientManagementTools.UnprovisionClientFromTenant(t.MockServer.Object, "app", "child", confirm: true)),
            "SystemIdentityClientRemoveMirror", ClientRtId),
        // Identity providers (delete carries no secret)
        ["delete_identity_provider"] = new(
            t => Box(IdentityProviderTools.DeleteIdentityProvider(t.MockServer.Object, ProviderRtId, confirm: true)),
            "SystemIdentityGoogleIdentityProviderDeleteIdentityProvider", ProviderRtId),
        // Secret-carrying methods
        ["add_client_credentials_client"] = new(
            t => Box(ClientManagementTools.AddClientCredentialsClient(t.MockServer.Object, "ci", "CI", "s3cr3t-value")),
            "SystemIdentityClientCreateClient", Result: ClientRtId, Secret: true),
        ["add_device_code_client"] = new(
            t => Box(ClientManagementTools.AddDeviceCodeClient(t.MockServer.Object, "cli", "CLI", "s3cr3t-value")),
            "SystemIdentityClientCreateClient", Result: ClientRtId, Secret: true),
        ["add_authorization_code_client"] = new(
            t => Box(ClientManagementTools.AddAuthorizationCodeClient(t.MockServer.Object, "web", "Web", "https://web.example.com")),
            "SystemIdentityClientCreateClient", Result: ClientRtId, Secret: true),
        ["add_scope_to_client"] = new(t => Box(ClientManagementTools.AddScopeToClient(t.MockServer.Object, "app", "scope1")),
            "SystemIdentityClientUpdateClient", ClientRtId, Secret: true),
        ["create_client_secret"] = new(t => Box(ApiSecretTools.CreateClientSecret(t.MockServer.Object, "app")),
            "SystemIdentityClientAddSecret", ClientRtId, "generated", Secret: true),
        ["update_client_secret"] = new(t => Box(ApiSecretTools.UpdateClientSecret(t.MockServer.Object, "app", "stored")),
            "SystemIdentityClientUpdateSecret", ClientRtId, Secret: true),
        ["delete_client_secret"] = new(
            t => Box(ApiSecretTools.DeleteClientSecret(t.MockServer.Object, "app", "stored", confirm: true)),
            "SystemIdentityClientRemoveSecret", ClientRtId, Secret: true),
        ["create_api_resource_secret"] = new(t => Box(ApiSecretTools.CreateApiResourceSecret(t.MockServer.Object, "res1")),
            "SystemIdentityApiResourceAddSecret", ResourceRtId, "generated", Secret: true),
        ["update_api_resource_secret"] = new(
            t => Box(ApiSecretTools.UpdateApiResourceSecret(t.MockServer.Object, "res1", "stored")),
            "SystemIdentityApiResourceUpdateSecret", ResourceRtId, Secret: true),
        ["delete_api_resource_secret"] = new(
            t => Box(ApiSecretTools.DeleteApiResourceSecret(t.MockServer.Object, "res1", "stored", confirm: true)),
            "SystemIdentityApiResourceRemoveSecret", ResourceRtId, Secret: true),
        ["add_oauth_identity_provider"] = new(
            t => Box(IdentityProviderTools.AddOAuthIdentityProvider(t.MockServer.Object, "google", "google", "cid", "s3cr3t-value")),
            "SystemIdentityGoogleIdentityProviderCreateIdentityProvider", Result: ProviderRtId, Secret: true),
        ["add_azure_entra_id_identity_provider"] = new(
            t => Box(IdentityProviderTools.AddAzureEntraIdIdentityProvider(t.MockServer.Object, "entra", "tid", "cid", "s3cr3t-value")),
            "SystemIdentityAzureEntraIdIdentityProviderCreateIdentityProvider", Result: ProviderRtId, Secret: true),
        ["add_open_ldap_identity_provider"] = new(
            t => Box(IdentityProviderTools.AddOpenLdapIdentityProvider(t.MockServer.Object, "ldap", "ldap.example.com", "dc=example")),
            "SystemIdentityOpenLdapIdentityProviderCreateIdentityProvider", Result: ProviderRtId, Secret: true),
        ["add_active_directory_identity_provider"] = new(
            t => Box(IdentityProviderTools.AddActiveDirectoryIdentityProvider(t.MockServer.Object, "ad", "ad.example.com")),
            "SystemIdentityMicrosoftAdIdentityProviderCreateIdentityProvider", Result: ProviderRtId, Secret: true),
        ["add_octo_tenant_identity_provider"] = new(
            t => Box(IdentityProviderTools.AddOctoTenantIdentityProvider(t.MockServer.Object, "parent", "parent")),
            "SystemIdentityOctoTenantIdentityProviderCreateIdentityProvider", Result: ProviderRtId, Secret: true),
        ["update_identity_provider"] = new(
            t => Box(IdentityProviderTools.UpdateIdentityProvider(t.MockServer.Object, ProviderRtId, "google", true)),
            "SystemIdentityGoogleIdentityProviderUpdateIdentityProvider", ProviderRtId, Secret: true)
    };

    public static TheoryData<string> AllTools => new(Cases.Keys);

    [Theory]
    [MemberData(nameof(AllTools))]
    public async Task Tool_RunsItsCkMethod_AndMakesNoRestWrite(string tool)
    {
        var c = Cases[tool];
        CkMethods.Succeeds(c.Result);

        var result = await c.Invoke(this);

        Outcome(result).IsSuccess.Should().BeTrue(Outcome(result).ErrorMessage);
        CkMethods.OperationNames.Should().Equal(c.Operation);
        if (c.ExpectedRtId != null)
        {
            CkMethods.SingleVariables(c.Operation).GetProperty("rtId").GetString().Should().Be(c.ExpectedRtId);
        }

        AssertNoRestWrite();
    }

    [Theory]
    [MemberData(nameof(AllTools))]
    public async Task Tool_MethodForbidden_IsReportedWithCodeAndMessage_NothingElseIsCalled(string tool)
    {
        var c = Cases[tool];
        CkMethods.Fails("METHOD_FORBIDDEN", "The caller needs the role UserManagement.");

        var result = await c.Invoke(this);

        Outcome(result).IsSuccess.Should().BeFalse();
        Outcome(result).ErrorMessage.Should().Be("METHOD_FORBIDDEN: The caller needs the role UserManagement.");
        CkMethods.Requests.Should().ContainSingle();
        AssertNoRestWrite();
    }

    [Theory]
    [MemberData(nameof(AllTools))]
    public async Task Tool_MethodHandlerNotAvailable_NamesTheNeededIdentity_NoRestFallback(string tool)
    {
        var c = Cases[tool];
        CkMethods.HandlerNotAvailable();

        var result = await c.Invoke(this);

        Outcome(result).IsSuccess.Should().BeFalse();
        Outcome(result).ErrorMessage.Should()
            .Contain("System.Identity 3.8").And.Contain("Nothing was changed").And.Contain("does not fall back");
        if (c.Secret)
        {
            Outcome(result).ErrorMessage.Should().Contain("Methods:Identity:EnablePasswordMethods");
        }
        else
        {
            Outcome(result).ErrorMessage.Should().NotContain("EnablePasswordMethods");
        }

        CkMethods.Requests.Should().ContainSingle();
        AssertNoRestWrite();
    }

    [Theory]
    [InlineData("create_group")]
    [InlineData("delete_client")]
    [InlineData("create_client_secret")]
    [InlineData("update_identity_provider")]
    public async Task Tool_SchemaWithoutTheMethod_NamesTheNeededIdentity_NoRestFallback(string tool)
    {
        CkMethods.MissingInSchema();

        var result = await Cases[tool].Invoke(this);

        Outcome(result).IsSuccess.Should().BeFalse();
        Outcome(result).ErrorMessage.Should().Contain("System.Identity 3.8");
        AssertNoRestWrite();
    }

    [Theory]
    [InlineData("add_group_to_group", "GROUP_CYCLE", "The group would contain itself.")]
    [InlineData("create_role", "ROLE_ALREADY_EXISTS", "A role named Auditor exists.")]
    [InlineData("add_client_to_role", "ROLE_NOT_FOUND", "No role Reader.")]
    [InlineData("apply_client_overlay", "INVALID_OVERLAY_NAME", "Bad name.")]
    [InlineData("create_client_secret", "CLIENT_SECRET_NOT_REQUIRED", "The client is public.")]
    [InlineData("unprovision_client_from_tenant", "MIRROR_NOT_FOUND", "No mirror.")]
    [InlineData("create_admin_provisioning_mapping", "SOURCE_TENANT_NOT_ANCESTOR", "Not an ancestor.")]
    [InlineData("add_oauth_identity_provider", "IDENTITY_PROVIDER_ALREADY_EXISTS", "Exists.")]
    public async Task Tool_DeclaredError_IsReportedAsCodeMessageAndParameters_NoRestCall(string tool, string code,
        string message)
    {
        CkMethods.Fails(code, message, new Dictionary<string, string> { ["name"] = "x" });

        var result = await Cases[tool].Invoke(this);

        Outcome(result).IsSuccess.Should().BeFalse();
        Outcome(result).ErrorMessage.Should().Be($"{code}: {message} (name=x)");
        AssertNoRestWrite();
    }

    [Fact]
    public async Task CreateGroup_PassesRoleNames_ForRoleIds()
    {
        CkMethods.Succeeds(GroupRtId);

        await GroupManagementTools.CreateGroup(MockServer.Object, "ops", "d", [RoleRtId, "Writer"]);

        var input = CkMethods.SingleVariables("SystemIdentityGroupCreateGroup").GetProperty("input");
        input.GetProperty("groupName").GetString().Should().Be("ops");
        input.GetProperty("roleNames").EnumerateArray().Select(e => e.GetString()).Should().Equal("Reader", "Writer");
    }

    [Fact]
    public async Task AddClientToGroup_SendsTheClientIdAsMember()
    {
        await ClientManagementTools.AddClientToGroup(MockServer.Object, GroupRtId, "app");

        var input = CkMethods.SingleVariables("SystemIdentityGroupAddMember").GetProperty("input");
        input.GetProperty("memberId").GetString().Should().Be("app");
    }

    [Fact]
    public async Task CreateExternalTenantUserMapping_DeclaredError_KeepsTheFormerErrorCode()
    {
        CkMethods.Fails(RtExternalTenantUserMappingMethodClient.CreateMappingErrors.UnknownGroup, "Unknown group.");

        var result = await ExternalTenantUserMappingTools.CreateMapping(MockServer.Object, "parent", UserRtId, "alice",
            groupIds: ["nope"]);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("UnknownGroup");
        result.ErrorMessage.Should().Be("UNKNOWN_GROUP: Unknown group.");
    }

    [Fact]
    public async Task CreateExternalTenantUserMapping_ReturnsTheStoredMapping()
    {
        CkMethods.Succeeds(new RtAdminProvisioningResultRecordDto { MappingRtId = MappingRtId });

        var result = await ExternalTenantUserMappingTools.CreateMapping(MockServer.Object, "parent", UserRtId, "alice");

        result.RtId.Should().Be(MappingRtId);
        result.Mapping!.SourceUserName.Should().Be("alice");
    }

    [Fact]
    public async Task ApplyClientOverlay_MapsTheMethodResult()
    {
        CkMethods.Succeeds(new RtClientOverlayUrisResultRecordDto
        {
            OverlayName = "dev", ClientId = "app", RedirectUrisAdded = 2, RedirectUrisSkipped = 1
        });

        var result = await ClientOverlayTools.ApplyClientOverlay(MockServer.Object, "app", "dev", ["https://a/"]);

        result.Result!.RedirectUris.Added.Should().Be(2);
        result.Result.RedirectUris.SkippedDuplicate.Should().Be(1);
    }

    [Fact]
    public async Task ProvisionCurrentUser_ReportsTheSourceIdentityOfTheMethodResult()
    {
        CkMethods.Succeeds(new RtAdminProvisioningResultRecordDto
        {
            SourceTenantId = "parent", SourceUserId = UserRtId, SourceUserName = "alice", Outcome = "created"
        });

        var result = await AdminProvisioningTools.ProvisionCurrentUser(MockServer.Object, "child");

        result.SourceTenantId.Should().Be("parent");
        result.SourceUserName.Should().Be("alice");
    }

    [Fact]
    public async Task ClientWithoutRuntimeId_IsReportedAsMissingWave2Identity_NoRestCall()
    {
        // An identity service before the wave 2 methods returns no rtId for the client: the method cannot address it.
        MockIdentityClient.Setup(c => c.GetClient("legacy")).ReturnsAsync(new ClientDto { ClientId = "legacy" });

        var result = await ClientManagementTools.DeleteClient(MockServer.Object, "legacy", confirm: true);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("System.Identity 3.8");
        CkMethods.Requests.Should().BeEmpty();
        AssertNoRestWrite();
    }

    [Fact]
    public async Task UnknownRole_IsReportedAsNotFound_NoMethodCall()
    {
        MockIdentityClient.Setup(c => c.GetRoleByName("Ghost")).ReturnsAsync((RoleDto)null!);

        var result = await RoleManagementTools.DeleteRole(MockServer.Object, "Ghost", confirm: true);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Be("Role 'Ghost' not found.");
        CkMethods.Requests.Should().BeEmpty();
    }

    [Theory]
    [InlineData("add_user_to_group", "MEMBER_ALREADY_IN_GROUP")]
    [InlineData("remove_user_from_group", "MEMBER_NOT_IN_GROUP")]
    [InlineData("remove_user_from_group", "MEMBER_NOT_FOUND")]
    [InlineData("add_client_to_group", "MEMBER_ALREADY_IN_GROUP")]
    [InlineData("remove_client_from_group", "MEMBER_NOT_IN_GROUP")]
    [InlineData("add_group_to_group", "CHILD_GROUP_ALREADY_IN_GROUP")]
    [InlineData("remove_group_from_group", "CHILD_GROUP_NOT_IN_GROUP")]
    [InlineData("remove_client_from_role", "ROLE_NOT_FOUND")]
    public async Task Tool_DeclaredErrorTheRestEndpointTreatedAsDone_IsSuccess(string tool, string code)
    {
        CkMethods.Fails(code, "nothing to do");

        var result = await Cases[tool].Invoke(this);

        Outcome(result).IsSuccess.Should().BeTrue(Outcome(result).ErrorMessage);
        AssertNoRestWrite();
    }

    [Theory]
    [InlineData("app", false)]
    [InlineData(ClientRtId, true)]
    public async Task RemoveClientFromGroup_UnknownMember_IsDoneOnlyForARuntimeId(string clientId, bool success)
    {
        CkMethods.Fails("MEMBER_NOT_FOUND", "No such member.");

        var result = await ClientManagementTools.RemoveClientFromGroup(MockServer.Object, GroupRtId, clientId,
            confirm: true);

        result.IsSuccess.Should().Be(success);
    }

    // ── Secret parameters: sent only in the method call, never in a message (canaries) ──────────────────────────

    private const string Canary = "canary-Qm5Tz8Lw2Xc7Rv4Np1Kd";

    private static readonly Dictionary<string, Func<IdentityMethodToolsTests, Task<object>>> SecretInputTools = new()
    {
        ["add_client_credentials_client"] = t =>
            Box(ClientManagementTools.AddClientCredentialsClient(t.MockServer.Object, "ci", "CI", Canary)),
        ["add_device_code_client"] = t =>
            Box(ClientManagementTools.AddDeviceCodeClient(t.MockServer.Object, "cli", "CLI", Canary)),
        ["add_oauth_identity_provider"] = t =>
            Box(IdentityProviderTools.AddOAuthIdentityProvider(t.MockServer.Object, "google", "google", "cid", Canary)),
        ["add_azure_entra_id_identity_provider"] = t =>
            Box(IdentityProviderTools.AddAzureEntraIdIdentityProvider(t.MockServer.Object, "entra", "tid", "cid", Canary)),
        ["update_identity_provider"] = t =>
            Box(IdentityProviderTools.UpdateIdentityProvider(t.MockServer.Object, ProviderRtId, "google", true,
                clientSecret: Canary))
    };

    public static TheoryData<string> SecretInputToolNames => new(SecretInputTools.Keys);

    [Theory]
    [MemberData(nameof(SecretInputToolNames))]
    public async Task SecretParameter_IsSentOnlyInTheMethodCall_AndNotReturned(string tool)
    {
        CkMethods.Succeeds(ProviderRtId);

        var result = await SecretInputTools[tool](this);

        Outcome(result).IsSuccess.Should().BeTrue(Outcome(result).ErrorMessage);
        CkMethodFake.Str(CkMethods.InputOf(CkMethods.Requests.Single().OperationName!), "clientSecret").Should().Be(Canary);
        JsonSerializer.Serialize(result).Should().NotContain(Canary);
        AssertNoRestWrite();
    }

    [Theory]
    [MemberData(nameof(SecretInputToolNames))]
    public async Task SecretEchoedInADeclaredOrPlatformError_IsRedacted(string tool)
    {
        CkMethods.Fails("METHOD_VALIDATION_FAILED", $"'{Canary}' is not valid.",
            new Dictionary<string, string> { ["clientSecret"] = Canary });

        var result = await SecretInputTools[tool](this);

        Outcome(result).IsSuccess.Should().BeFalse();
        Outcome(result).ErrorMessage.Should().StartWith("METHOD_VALIDATION_FAILED: '[REDACTED]' is not valid.");
        JsonSerializer.Serialize(result).Should().NotContain(Canary);
    }

    [Theory]
    [MemberData(nameof(SecretInputToolNames))]
    public async Task SecretEchoedInAnUnexpectedException_IsRedacted(string tool)
    {
        CkMethods.Throws(new InvalidOperationException($"boom {Canary}"));

        var result = await SecretInputTools[tool](this);

        Outcome(result).ErrorMessage.Should().Be("boom [REDACTED]");
        JsonSerializer.Serialize(result).Should().NotContain(Canary);
    }

    [Theory]
    [MemberData(nameof(SecretInputToolNames))]
    public async Task SecretTool_MethodForbidden_ForAnotherTenantsToken_NothingElseIsCalled(string tool)
    {
        // The asset repository refuses the call (token of another tenant, or a caller without the role).
        CkMethods.Fails("METHOD_FORBIDDEN", "The token was not issued for this tenant.");

        var result = await SecretInputTools[tool](this);

        Outcome(result).IsSuccess.Should().BeFalse();
        Outcome(result).ErrorMessage.Should().Be("METHOD_FORBIDDEN: The token was not issued for this tenant.");
        CkMethods.Requests.Should().ContainSingle();
        AssertNoRestWrite();
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────────────────────────

    private void AssertNoRestWrite() => RestWrites().Should().BeEmpty();

    /// <summary>Every call on the identity REST client that is not a read.</summary>
    private List<string> RestWrites() => MockIdentityClient.Invocations
        .Select(i => i.Method.Name)
        .Where(n => !n.StartsWith("Get", StringComparison.Ordinal))
        .ToList();

    private static async Task<object> Box<T>(Task<T> task) => (await task)!;

    private static (bool IsSuccess, string? ErrorMessage) Outcome(object response)
    {
        var type = response.GetType();
        return ((bool)type.GetProperty("IsSuccess")!.GetValue(response)!,
            (string?)type.GetProperty("ErrorMessage")!.GetValue(response));
    }
}
