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
        var result = await IdentityProviderTools.DeleteIdentityProvider(MockServer.Object,
            ProviderId, confirm: true);

        result.IsSuccess.Should().BeTrue();
        MockIdentityClient.Verify(c => c.DeleteIdentityProvider(
            It.Is<OctoObjectId>(o => o.ToString() == ProviderId)), Times.Once);
    }

    [Fact]
    public async Task AddOAuthIdentityProvider_Google_BuildsGoogleDto()
    {
        await IdentityProviderTools.AddOAuthIdentityProvider(MockServer.Object,
            name: "G", providerType: "google", clientId: "cid", clientSecret: "csec");

        MockIdentityClient.Verify(c => c.CreateIdentityProvider(
            It.Is<GoogleIdentityProviderDto>(p => p.Name == "G" && p.ClientId == "cid" && p.ClientSecret == "csec")),
            Times.Once);
    }

    [Fact]
    public async Task AddOAuthIdentityProvider_Microsoft_BuildsMicrosoftDto()
    {
        await IdentityProviderTools.AddOAuthIdentityProvider(MockServer.Object,
            "M", "microsoft", "cid", "csec");

        MockIdentityClient.Verify(c => c.CreateIdentityProvider(
            It.Is<MicrosoftIdentityProviderDto>(p => p.Name == "M")), Times.Once);
    }

    [Fact]
    public async Task AddOAuthIdentityProvider_Facebook_BuildsFacebookDto()
    {
        await IdentityProviderTools.AddOAuthIdentityProvider(MockServer.Object,
            "F", "facebook", "cid", "csec");

        MockIdentityClient.Verify(c => c.CreateIdentityProvider(
            It.Is<FacebookIdentityProviderDto>(p => p.Name == "F")), Times.Once);
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
        await IdentityProviderTools.AddOAuthIdentityProvider(MockServer.Object,
            "G", "google", "cid", "csec",
            allowSelfRegistration: false, defaultGroupRtId: "grp-123");

        MockIdentityClient.Verify(c => c.CreateIdentityProvider(
            It.Is<GoogleIdentityProviderDto>(p =>
                p.AllowSelfRegistration == false && p.DefaultGroupRtId == "grp-123")), Times.Once);
    }

    [Fact]
    public async Task AddAzureEntraIdIdentityProvider_BuildsAzureDto()
    {
        await IdentityProviderTools.AddAzureEntraIdIdentityProvider(MockServer.Object,
            name: "Az", azureTenantId: "azure-tid", clientId: "cid", clientSecret: "csec",
            authority: "https://my-authority/");

        MockIdentityClient.Verify(c => c.CreateIdentityProvider(
            It.Is<AzureEntraIdProviderDto>(p =>
                p.Name == "Az" && p.TenantId == "azure-tid" &&
                p.ClientId == "cid" && p.ClientSecret == "csec" &&
                p.Authority == "https://my-authority/")), Times.Once);
    }

    [Fact]
    public async Task AddOpenLdapIdentityProvider_BuildsOpenLdapDto()
    {
        await IdentityProviderTools.AddOpenLdapIdentityProvider(MockServer.Object,
            name: "LDAP", host: "ldap.example.com", userBaseDn: "ou=users,dc=example,dc=com",
            port: 389, userNameAttribute: "cn", useTls: false);

        MockIdentityClient.Verify(c => c.CreateIdentityProvider(
            It.Is<OpenLdapProviderDto>(p =>
                p.Name == "LDAP" && p.Host == "ldap.example.com" && p.Port == 389 &&
                p.UserBaseDn == "ou=users,dc=example,dc=com" && p.UserNameAttribute == "cn" &&
                p.UseTls == false)), Times.Once);
    }

    [Fact]
    public async Task AddOpenLdapIdentityProvider_DefaultsPortAndAttribute()
    {
        await IdentityProviderTools.AddOpenLdapIdentityProvider(MockServer.Object,
            "LDAP", "ldap.example.com", "dc=example");

        MockIdentityClient.Verify(c => c.CreateIdentityProvider(
            It.Is<OpenLdapProviderDto>(p =>
                p.Port == 636 && p.UserNameAttribute == "uid" && p.UseTls == true)), Times.Once);
    }

    [Fact]
    public async Task AddActiveDirectoryIdentityProvider_BuildsAdDto()
    {
        await IdentityProviderTools.AddActiveDirectoryIdentityProvider(MockServer.Object,
            name: "AD", host: "dc.example.com", port: 389, useTls: true);

        MockIdentityClient.Verify(c => c.CreateIdentityProvider(
            It.Is<MicrosoftAdProviderDto>(p =>
                p.Name == "AD" && p.Host == "dc.example.com" && p.Port == 389 && p.UseTls == true)), Times.Once);
    }

    [Fact]
    public async Task AddOctoTenantIdentityProvider_BuildsOctoTenantDto()
    {
        await IdentityProviderTools.AddOctoTenantIdentityProvider(MockServer.Object,
            name: "Parent", parentTenantId: "octosystem");

        MockIdentityClient.Verify(c => c.CreateIdentityProvider(
            It.Is<OctoTenantIdentityProviderDto>(p =>
                p.Name == "Parent" && p.ParentTenantId == "octosystem")), Times.Once);
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
        MockIdentityClient.Setup(c => c.GetIdentityProvider(
                It.Is<OctoObjectId>(o => o.ToString() == ProviderId)))
            .ReturnsAsync(new GoogleIdentityProviderDto
            {
                Name = "Old", ClientId = "old-cid", ClientSecret = "old-sec"
            });

        await IdentityProviderTools.UpdateIdentityProvider(MockServer.Object,
            providerId: ProviderId, name: "New", isEnabled: true,
            clientId: "new-cid", clientSecret: "new-sec");

        MockIdentityClient.Verify(c => c.UpdateIdentityProvider(
            It.Is<OctoObjectId>(o => o.ToString() == ProviderId),
            It.Is<GoogleIdentityProviderDto>(p =>
                p.Name == "New" && p.IsEnabled == true &&
                p.ClientId == "new-cid" && p.ClientSecret == "new-sec")), Times.Once);
    }

    [Fact]
    public async Task UpdateIdentityProvider_AzureEntra_PreservesTenantAndAuthority()
    {
        MockIdentityClient.Setup(c => c.GetIdentityProvider(It.IsAny<OctoObjectId>()))
            .ReturnsAsync(new AzureEntraIdProviderDto
            {
                Name = "Old",
                TenantId = "azure-tid",
                Authority = "https://my-authority/",
                ClientId = "old-cid",
                ClientSecret = "old-sec"
            });

        await IdentityProviderTools.UpdateIdentityProvider(MockServer.Object,
            providerId: ProviderId, name: "New", isEnabled: true);

        MockIdentityClient.Verify(c => c.UpdateIdentityProvider(
            It.IsAny<OctoObjectId>(),
            It.Is<AzureEntraIdProviderDto>(p =>
                p.Name == "New" && p.TenantId == "azure-tid" &&
                p.Authority == "https://my-authority/" &&
                p.ClientId == "old-cid" && p.ClientSecret == "old-sec")),
            Times.Once);
    }

    [Fact]
    public async Task UpdateIdentityProvider_OpenLdap_PreservesHostAndUserBaseDn()
    {
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

        MockIdentityClient.Verify(c => c.UpdateIdentityProvider(
            It.IsAny<OctoObjectId>(),
            It.Is<OpenLdapProviderDto>(p =>
                p.Name == "New" && p.IsEnabled == false &&
                p.Host == "ldap.example.com" && p.Port == 389 &&
                p.UserBaseDn == "ou=u,dc=ex,dc=com" && p.UserNameAttribute == "cn" &&
                p.UseTls == false)), Times.Once);
    }

    [Fact]
    public async Task UpdateIdentityProvider_OctoTenant_PreservesParentTenantId()
    {
        MockIdentityClient.Setup(c => c.GetIdentityProvider(It.IsAny<OctoObjectId>()))
            .ReturnsAsync(new OctoTenantIdentityProviderDto
            {
                Name = "Old", ParentTenantId = "parent-t"
            });

        await IdentityProviderTools.UpdateIdentityProvider(MockServer.Object,
            providerId: ProviderId, name: "New", isEnabled: true);

        MockIdentityClient.Verify(c => c.UpdateIdentityProvider(
            It.IsAny<OctoObjectId>(),
            It.Is<OctoTenantIdentityProviderDto>(p => p.ParentTenantId == "parent-t")),
            Times.Once);
    }

    [Fact]
    public async Task UpdateIdentityProvider_AppliesOptionalAllowSelfRegistration()
    {
        MockIdentityClient.Setup(c => c.GetIdentityProvider(It.IsAny<OctoObjectId>()))
            .ReturnsAsync(new GoogleIdentityProviderDto { Name = "Old" });

        await IdentityProviderTools.UpdateIdentityProvider(MockServer.Object,
            providerId: ProviderId, name: "New", isEnabled: true,
            allowSelfRegistration: false, defaultGroupRtId: "grp-1");

        MockIdentityClient.Verify(c => c.UpdateIdentityProvider(
            It.IsAny<OctoObjectId>(),
            It.Is<IdentityProviderDto>(p =>
                p.AllowSelfRegistration == false && p.DefaultGroupRtId == "grp-1")),
            Times.Once);
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
