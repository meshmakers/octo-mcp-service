using System.Text.Json;
using FluentAssertions;
using Meshmakers.Octo.Backend.McpServices.Tools;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Moq;
using Xunit;

namespace McpServices.Tests.Tools;

public class IdentityProviderToolsTests : ToolTestBase
{
    private const string ProviderId = "507f1f77bcf86cd799439099";

    public IdentityProviderToolsTests()
    {
        GivenAuthenticated();
    }

    /// <summary>The input of the single create call, sent through the client of the concrete provider type.</summary>
    private JsonElement CreateInput(string providerType) =>
        CkMethods.InputOf($"SystemIdentity{providerType}IdentityProviderCreateIdentityProvider");

    /// <summary>The input of the single update call; asserts the target runtime id.</summary>
    private JsonElement UpdateInput(string providerType)
    {
        var operation = $"SystemIdentity{providerType}IdentityProviderUpdateIdentityProvider";
        CkMethods.RtIdOf(operation).Should().Be(ProviderId);
        return CkMethods.InputOf(operation);
    }

    private void AssertNoRestWrite() =>
        MockIdentityClient.Invocations.Select(i => i.Method.Name).Should().OnlyContain(n => n.StartsWith("Get"));

    [Fact]
    public async Task GetIdentityProviders_HappyPath_ReturnsList()
    {
        MockIdentityClient.Setup(c => c.GetIdentityProviders())
            .ReturnsAsync(new[]
            {
                new GoogleIdentityProviderDto { Name = "Google" }
            });

        var result = await IdentityProviderTools.GetIdentityProviders(MockServer.Object);

        result.IsSuccess.Should().BeTrue();
        result.Providers.Should().HaveCount(1);
    }

    [Fact]
    public async Task GetIdentityProviders_Unauthenticated_ReturnsAuthError()
    {
        GivenUnauthenticated();
        var result = await IdentityProviderTools.GetIdentityProviders(MockServer.Object);
        result.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task DeleteIdentityProvider_WithoutConfirm_Refuses()
    {
        var result = await IdentityProviderTools.DeleteIdentityProvider(MockServer.Object, ProviderId);

        result.IsSuccess.Should().BeFalse();
        MockIdentityClient.Verify(c => c.DeleteIdentityProvider(It.IsAny<OctoObjectId>()), Times.Never);
    }

    [Fact]
    public async Task DeleteIdentityProvider_WithConfirm_CallsSdk()
    {
        // The delete runs on the client of the stored provider type.
        MockIdentityClient.Setup(c => c.GetIdentityProvider(It.IsAny<OctoObjectId>()))
            .ReturnsAsync(new OpenLdapProviderDto { Name = "LDAP" });
        CkMethods.Succeeds();

        var result = await IdentityProviderTools.DeleteIdentityProvider(MockServer.Object,
            ProviderId, confirm: true);

        result.IsSuccess.Should().BeTrue();
        CkMethods.RtIdOf("SystemIdentityOpenLdapIdentityProviderDeleteIdentityProvider").Should().Be(ProviderId);
        AssertNoRestWrite();
    }

    [Fact]
    public async Task AddOAuthIdentityProvider_Google_BuildsGoogleDto()
    {
        CkMethods.Succeeds();

        await IdentityProviderTools.AddOAuthIdentityProvider(MockServer.Object,
            name: "G", providerType: "google", clientId: "cid", clientSecret: "csec");

        var input = CreateInput("Google");
        CkMethodFake.Str(input, "name").Should().Be("G");
        CkMethodFake.Str(input, "clientId").Should().Be("cid");
        CkMethodFake.Str(input, "clientSecret").Should().Be("csec");
        AssertNoRestWrite();
    }

    [Fact]
    public async Task AddOAuthIdentityProvider_Microsoft_BuildsMicrosoftDto()
    {
        CkMethods.Succeeds();

        await IdentityProviderTools.AddOAuthIdentityProvider(MockServer.Object,
            "M", "microsoft", "cid", "csec");

        CkMethodFake.Str(CreateInput("Microsoft"), "name").Should().Be("M");
        AssertNoRestWrite();
    }

    [Fact]
    public async Task AddOAuthIdentityProvider_Facebook_BuildsFacebookDto()
    {
        CkMethods.Succeeds();

        await IdentityProviderTools.AddOAuthIdentityProvider(MockServer.Object,
            "F", "facebook", "cid", "csec");

        CkMethodFake.Str(CreateInput("Facebook"), "name").Should().Be("F");
        AssertNoRestWrite();
    }

    [Fact]
    public async Task AddOAuthIdentityProvider_UnknownType_ReturnsValidationError()
    {
        var result = await IdentityProviderTools.AddOAuthIdentityProvider(MockServer.Object,
            "X", "twitter", "cid", "csec");

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Unsupported providerType");
        MockIdentityClient.Verify(c => c.CreateIdentityProvider(It.IsAny<IdentityProviderDto>()), Times.Never);
    }

    [Fact]
    public async Task AddOAuthIdentityProvider_AppliesOptionalDefaultGroup()
    {
        CkMethods.Succeeds();

        await IdentityProviderTools.AddOAuthIdentityProvider(MockServer.Object,
            "G", "google", "cid", "csec",
            allowSelfRegistration: false, defaultGroupRtId: "grp-123");

        var input = CreateInput("Google");
        CkMethodFake.Bool(input, "allowSelfRegistration").Should().BeFalse();
        CkMethodFake.Str(input, "defaultGroupRtId").Should().Be("grp-123");
    }

    [Fact]
    public async Task AddAzureEntraIdIdentityProvider_BuildsAzureDto()
    {
        CkMethods.Succeeds();

        await IdentityProviderTools.AddAzureEntraIdIdentityProvider(MockServer.Object,
            name: "Az", azureTenantId: "azure-tid", clientId: "cid", clientSecret: "csec",
            authority: "https://my-authority/");

        var input = CreateInput("AzureEntraId");
        CkMethodFake.Str(input, "name").Should().Be("Az");
        CkMethodFake.Str(input, "tenantId").Should().Be("azure-tid");
        CkMethodFake.Str(input, "clientId").Should().Be("cid");
        CkMethodFake.Str(input, "clientSecret").Should().Be("csec");
        CkMethodFake.Str(input, "authority").Should().Be("https://my-authority/");
        AssertNoRestWrite();
    }

    [Fact]
    public async Task AddOpenLdapIdentityProvider_BuildsOpenLdapDto()
    {
        CkMethods.Succeeds();

        await IdentityProviderTools.AddOpenLdapIdentityProvider(MockServer.Object,
            name: "LDAP", host: "ldap.example.com", userBaseDn: "ou=users,dc=example,dc=com",
            port: 389, userNameAttribute: "cn", useTls: false);

        var input = CreateInput("OpenLdap");
        CkMethodFake.Str(input, "name").Should().Be("LDAP");
        CkMethodFake.Str(input, "host").Should().Be("ldap.example.com");
        CkMethodFake.Prop(input, "port")!.Value.GetInt32().Should().Be(389);
        CkMethodFake.Str(input, "userBaseDn").Should().Be("ou=users,dc=example,dc=com");
        CkMethodFake.Str(input, "userNameAttribute").Should().Be("cn");
        CkMethodFake.Bool(input, "useTls").Should().BeFalse();
        AssertNoRestWrite();
    }

    [Fact]
    public async Task AddOpenLdapIdentityProvider_DefaultsPortAndAttribute()
    {
        CkMethods.Succeeds();

        await IdentityProviderTools.AddOpenLdapIdentityProvider(MockServer.Object,
            "LDAP", "ldap.example.com", "dc=example");

        var input = CreateInput("OpenLdap");
        CkMethodFake.Prop(input, "port")!.Value.GetInt32().Should().Be(636);
        CkMethodFake.Str(input, "userNameAttribute").Should().Be("uid");
        CkMethodFake.Bool(input, "useTls").Should().BeTrue();
    }

    [Fact]
    public async Task AddActiveDirectoryIdentityProvider_BuildsAdDto()
    {
        CkMethods.Succeeds();

        await IdentityProviderTools.AddActiveDirectoryIdentityProvider(MockServer.Object,
            name: "AD", host: "dc.example.com", port: 389, useTls: true);

        var input = CreateInput("MicrosoftAd");
        CkMethodFake.Str(input, "name").Should().Be("AD");
        CkMethodFake.Str(input, "host").Should().Be("dc.example.com");
        CkMethodFake.Prop(input, "port")!.Value.GetInt32().Should().Be(389);
        CkMethodFake.Bool(input, "useTls").Should().BeTrue();
        AssertNoRestWrite();
    }

    [Fact]
    public async Task AddOctoTenantIdentityProvider_BuildsOctoTenantDto()
    {
        CkMethods.Succeeds();

        await IdentityProviderTools.AddOctoTenantIdentityProvider(MockServer.Object,
            name: "Parent", parentTenantId: "octosystem");

        var input = CreateInput("OctoTenant");
        CkMethodFake.Str(input, "name").Should().Be("Parent");
        CkMethodFake.Str(input, "parentTenantId").Should().Be("octosystem");
        AssertNoRestWrite();
    }

    [Fact]
    public async Task AddOctoTenantIdentityProvider_MissingParentId_ReturnsValidationError()
    {
        var result = await IdentityProviderTools.AddOctoTenantIdentityProvider(MockServer.Object, "P", "");

        result.IsSuccess.Should().BeFalse();
        MockIdentityClient.Verify(c => c.CreateIdentityProvider(It.IsAny<IdentityProviderDto>()), Times.Never);
    }

    // ── UpdateIdentityProvider — polymorphic fetch+patch+write ──────────────

    [Fact]
    public async Task UpdateIdentityProvider_Google_PreservesTypeAndPatches()
    {
        CkMethods.Succeeds();

        MockIdentityClient.Setup(c => c.GetIdentityProvider(
                It.Is<OctoObjectId>(o => o.ToString() == ProviderId)))
            .ReturnsAsync(new GoogleIdentityProviderDto
            {
                Name = "Old", ClientId = "old-cid", ClientSecret = "old-sec"
            });

        await IdentityProviderTools.UpdateIdentityProvider(MockServer.Object,
            providerId: ProviderId, name: "New", isEnabled: true,
            clientId: "new-cid", clientSecret: "new-sec");

        var input = UpdateInput("Google");
        CkMethodFake.Str(input, "name").Should().Be("New");
        CkMethodFake.Bool(input, "enabled").Should().BeTrue();
        CkMethodFake.Str(input, "clientId").Should().Be("new-cid");
        CkMethodFake.Str(input, "clientSecret").Should().Be("new-sec");
        AssertNoRestWrite();
    }

    [Fact]
    public async Task UpdateIdentityProvider_AzureEntra_PreservesTenantAndAuthority()
    {
        CkMethods.Succeeds();

        // AB#5543: GET no longer returns the client secret (write-only, clientSecretIsSet instead).
        MockIdentityClient.Setup(c => c.GetIdentityProvider(It.IsAny<OctoObjectId>()))
            .ReturnsAsync(new AzureEntraIdProviderDto
            {
                Name = "Old",
                TenantId = "azure-tid",
                Authority = "https://my-authority/",
                ClientId = "old-cid",
                ClientSecret = null,
                ClientSecretIsSet = true
            });

        await IdentityProviderTools.UpdateIdentityProvider(MockServer.Object,
            providerId: ProviderId, name: "New", isEnabled: true);

        // No new secret supplied -> null on the wire = "keep the stored secret".
        var input = UpdateInput("AzureEntraId");
        CkMethodFake.Str(input, "name").Should().Be("New");
        CkMethodFake.Str(input, "tenantId").Should().Be("azure-tid");
        CkMethodFake.Str(input, "authority").Should().Be("https://my-authority/");
        CkMethodFake.Str(input, "clientId").Should().Be("old-cid");
        CkMethodFake.Str(input, "clientSecret").Should().BeNull();
        AssertNoRestWrite();
    }

    [Fact]
    public async Task UpdateIdentityProvider_AzureEntra_WithNewSecret_RotatesSecret()
    {
        CkMethods.Succeeds();

        MockIdentityClient.Setup(c => c.GetIdentityProvider(It.IsAny<OctoObjectId>()))
            .ReturnsAsync(new AzureEntraIdProviderDto
            {
                Name = "Old", TenantId = "azure-tid", ClientId = "old-cid", ClientSecretIsSet = true
            });

        await IdentityProviderTools.UpdateIdentityProvider(MockServer.Object,
            providerId: ProviderId, name: "New", isEnabled: true, clientSecret: "rotated-test-secret");

        CkMethodFake.Str(UpdateInput("AzureEntraId"), "clientSecret").Should().Be("rotated-test-secret");
    }

    [Fact]
    public async Task UpdateIdentityProvider_Google_WithoutSecret_SendsNullAndKeepsClientId()
    {
        CkMethods.Succeeds();

        // Even if a (legacy) server still echoed a secret, the tool must never send it back.
        MockIdentityClient.Setup(c => c.GetIdentityProvider(It.IsAny<OctoObjectId>()))
            .ReturnsAsync(new GoogleIdentityProviderDto
            {
                Name = "Old", ClientId = "old-cid", ClientSecret = "echoed-by-old-server", ClientSecretIsSet = true
            });

        await IdentityProviderTools.UpdateIdentityProvider(MockServer.Object,
            providerId: ProviderId, name: "New", isEnabled: true);

        var input = UpdateInput("Google");
        CkMethodFake.Str(input, "clientId").Should().Be("old-cid");
        CkMethodFake.Str(input, "clientSecret").Should().BeNull();
        JsonSerializer.Serialize(CkMethods.Requests).Should().NotContain("echoed-by-old-server");
    }

    [Fact]
    public async Task UpdateIdentityProvider_EmptySecret_IsTreatedAsUnchanged()
    {
        CkMethods.Succeeds();

        MockIdentityClient.Setup(c => c.GetIdentityProvider(It.IsAny<OctoObjectId>()))
            .ReturnsAsync(new MicrosoftIdentityProviderDto { Name = "Old", ClientId = "cid" });

        await IdentityProviderTools.UpdateIdentityProvider(MockServer.Object,
            providerId: ProviderId, name: "New", isEnabled: true, clientSecret: "");

        CkMethodFake.Str(UpdateInput("Microsoft"), "clientSecret").Should().BeNull();
    }

    [Fact]
    public async Task GetIdentityProviders_PassesClientSecretIsSetThrough()
    {
        MockIdentityClient.Setup(c => c.GetIdentityProviders())
            .ReturnsAsync(new List<IdentityProviderDto>
            {
                new GoogleIdentityProviderDto { Name = "G", ClientId = "cid", ClientSecretIsSet = true }
            });

        var result = await IdentityProviderTools.GetIdentityProviders(MockServer.Object);

        result.IsSuccess.Should().BeTrue();
        var google = result.Providers.Should().ContainSingle().Which.Should().BeOfType<GoogleIdentityProviderDto>()
            .Subject;
        google.ClientSecretIsSet.Should().BeTrue();
        google.ClientSecret.Should().BeNull();
    }

    [Fact]
    public async Task GetIdentityProviders_ClientSecretFromOlderService_IsScrubbed()
    {
        // AB#5543 defense in depth: an identity service that still echoes client secrets must not leak them
        // into the AI transcript.
        const string fakeSecret = "fake-client-secret-from-old-service";
        MockIdentityClient.Setup(c => c.GetIdentityProviders())
            .ReturnsAsync(new List<IdentityProviderDto>
            {
                new GoogleIdentityProviderDto { Name = "G", ClientId = "g", ClientSecret = fakeSecret },
                new MicrosoftIdentityProviderDto { Name = "M", ClientId = "m", ClientSecret = fakeSecret },
                new FacebookIdentityProviderDto { Name = "F", ClientId = "f", ClientSecret = fakeSecret },
                new AzureEntraIdProviderDto { Name = "A", ClientId = "a", TenantId = "t", ClientSecret = fakeSecret }
            });

        var result = await IdentityProviderTools.GetIdentityProviders(MockServer.Object);

        result.IsSuccess.Should().BeTrue();
        result.Providers.Should().HaveCount(4);
        System.Text.Json.JsonSerializer.Serialize<object>(result).Should().NotContain(fakeSecret);
        System.Text.Json.JsonSerializer.Serialize(result.Providers.Cast<object>().ToList())
            .Should().NotContain(fakeSecret);
    }

    [Fact]
    public async Task UpdateIdentityProvider_OpenLdap_PreservesHostAndUserBaseDn()
    {
        CkMethods.Succeeds();

        MockIdentityClient.Setup(c => c.GetIdentityProvider(It.IsAny<OctoObjectId>()))
            .ReturnsAsync(new OpenLdapProviderDto
            {
                Name = "Old",
                Host = "ldap.example.com",
                Port = 389,
                UserBaseDn = "ou=u,dc=ex,dc=com",
                UserNameAttribute = "cn",
                UseTls = false
            });

        await IdentityProviderTools.UpdateIdentityProvider(MockServer.Object,
            providerId: ProviderId, name: "New", isEnabled: false);

        var input = UpdateInput("OpenLdap");
        CkMethodFake.Str(input, "name").Should().Be("New");
        CkMethodFake.Bool(input, "enabled").Should().BeFalse();
        CkMethodFake.Str(input, "host").Should().Be("ldap.example.com");
        CkMethodFake.Prop(input, "port")!.Value.GetInt32().Should().Be(389);
        CkMethodFake.Str(input, "userBaseDn").Should().Be("ou=u,dc=ex,dc=com");
        CkMethodFake.Str(input, "userNameAttribute").Should().Be("cn");
        CkMethodFake.Bool(input, "useTls").Should().BeFalse();
        AssertNoRestWrite();
    }

    [Fact]
    public async Task UpdateIdentityProvider_OctoTenant_PreservesParentTenantId()
    {
        CkMethods.Succeeds();

        MockIdentityClient.Setup(c => c.GetIdentityProvider(It.IsAny<OctoObjectId>()))
            .ReturnsAsync(new OctoTenantIdentityProviderDto
            {
                Name = "Old", ParentTenantId = "parent-t"
            });

        await IdentityProviderTools.UpdateIdentityProvider(MockServer.Object,
            providerId: ProviderId, name: "New", isEnabled: true);

        CkMethodFake.Str(UpdateInput("OctoTenant"), "parentTenantId").Should().Be("parent-t");
    }

    [Fact]
    public async Task UpdateIdentityProvider_AppliesOptionalAllowSelfRegistration()
    {
        CkMethods.Succeeds();

        MockIdentityClient.Setup(c => c.GetIdentityProvider(It.IsAny<OctoObjectId>()))
            .ReturnsAsync(new GoogleIdentityProviderDto { Name = "Old" });

        await IdentityProviderTools.UpdateIdentityProvider(MockServer.Object,
            providerId: ProviderId, name: "New", isEnabled: true,
            allowSelfRegistration: false, defaultGroupRtId: "grp-1");

        var input = UpdateInput("Google");
        CkMethodFake.Bool(input, "allowSelfRegistration").Should().BeFalse();
        CkMethodFake.Str(input, "defaultGroupRtId").Should().Be("grp-1");
    }

    [Fact]
    public async Task UpdateIdentityProvider_NotFound_ReturnsError()
    {
        MockIdentityClient.Setup(c => c.GetIdentityProvider(It.IsAny<OctoObjectId>()))
            .ReturnsAsync((IdentityProviderDto)null!);

        var result = await IdentityProviderTools.UpdateIdentityProvider(MockServer.Object,
            providerId: ProviderId, name: "New", isEnabled: true);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("not found");
    }

    [Fact]
    public async Task UpdateIdentityProvider_MissingArgs_ReturnsValidationError()
    {
        var result = await IdentityProviderTools.UpdateIdentityProvider(MockServer.Object,
            providerId: "", name: "", isEnabled: true);

        result.IsSuccess.Should().BeFalse();
        MockIdentityClient.Verify(c => c.GetIdentityProvider(It.IsAny<OctoObjectId>()), Times.Never);
    }
}
