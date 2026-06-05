using FluentAssertions;
using Meshmakers.Octo.Backend.McpServices.Options;
using Meshmakers.Octo.Backend.McpServices.Services;
using Meshmakers.Octo.Sdk.ServiceClient;
using Microsoft.Extensions.Options;
using Xunit;

namespace McpServices.Tests.Services;

public class OctoServiceClientFactoryTests
{
    private static OctoServiceClientFactory CreateFactory(
        string? assetUrl = "https://asset.test/",
        string? identityUrl = "https://identity.test/",
        string? communicationUrl = "https://communication.test/",
        string? reportingUrl = "https://reporting.test/",
        string? botUrl = "https://bot.test/",
        string? adminPanelUrl = "https://adminpanel.test/")
    {
        var opts = Options.Create(new OctoServiceUrlOptions
        {
            AssetServiceUrl = assetUrl,
            IdentityServiceUrl = identityUrl,
            CommunicationServiceUrl = communicationUrl,
            ReportingServiceUrl = reportingUrl,
            BotServiceUrl = botUrl,
            AdminPanelUrl = adminPanelUrl
        });
        return new OctoServiceClientFactory(opts);
    }

    [Fact]
    public void CreateAssetClient_WithValidConfig_ReturnsClientWithTokenSet()
    {
        var factory = CreateFactory();

        var client = factory.CreateAssetClient("octosystem", "bearer-abc");

        client.Should().NotBeNull();
        client.AccessToken.AccessToken.Should().Be("bearer-abc");
        client.ServiceUri.ToString().Should().Contain("octosystem");
    }

    [Fact]
    public void CreateAssetClient_WithMissingAssetUrl_ThrowsConfigurationMissing()
    {
        var factory = CreateFactory(assetUrl: null);

        var act = () => factory.CreateAssetClient("octosystem", "bearer-abc");

        act.Should().Throw<ServiceConfigurationMissingException>()
            .WithMessage("*AssetServiceUrl*");
    }

    [Fact]
    public void CreateIdentityClient_WithValidConfig_ReturnsClientWithTokenSet()
    {
        var factory = CreateFactory();

        var client = factory.CreateIdentityClient("octosystem", "bearer-xyz");

        client.Should().NotBeNull();
        client.AccessToken.AccessToken.Should().Be("bearer-xyz");
        client.ServiceUri.ToString().Should().Contain("octosystem");
    }

    [Fact]
    public void CreateIdentityClient_WithMissingIdentityUrl_ThrowsConfigurationMissing()
    {
        var factory = CreateFactory(identityUrl: null);

        var act = () => factory.CreateIdentityClient("octosystem", "bearer-xyz");

        act.Should().Throw<ServiceConfigurationMissingException>()
            .WithMessage("*IdentityServiceUrl*");
    }

    [Fact]
    public void CreateCommunicationClient_WithTenant_ReturnsClientWithTokenSet()
    {
        var factory = CreateFactory();

        var client = factory.CreateCommunicationClient("octosystem", "bearer-comm");

        client.Should().NotBeNull();
        client.AccessToken.AccessToken.Should().Be("bearer-comm");
        client.ServiceUri.ToString().Should().Contain("octosystem");
    }

    [Fact]
    public void CreateCommunicationClient_WithNullTenant_FallsBackToSystemScope()
    {
        var factory = CreateFactory();

        var client = factory.CreateCommunicationClient(tenantId: null, "bearer-comm");

        client.ServiceUri.ToString().Should().Contain("system");
    }

    [Fact]
    public void CreateCommunicationClient_WithMissingUrl_ThrowsConfigurationMissing()
    {
        var factory = CreateFactory(communicationUrl: null);

        var act = () => factory.CreateCommunicationClient("octosystem", "bearer");

        act.Should().Throw<ServiceConfigurationMissingException>()
            .WithMessage("*CommunicationServiceUrl*");
    }

    [Fact]
    public void CreateStreamDataClient_WithValidConfig_UsesAssetEndpointPerTenant()
    {
        var factory = CreateFactory();

        var client = factory.CreateStreamDataClient("octosystem", "tok-sd");

        client.Should().NotBeNull();
        client.AccessToken.AccessToken.Should().Be("tok-sd");
        // StreamData routes via "api/v1" (no tenant in URI); tenant is passed per call.
        client.ServiceUri.ToString().Should().Contain("asset.test");
    }

    [Fact]
    public void CreateStreamDataClient_WithMissingAssetUrl_ThrowsConfigurationMissing()
    {
        var factory = CreateFactory(assetUrl: null);

        var act = () => factory.CreateStreamDataClient("octosystem", "tok-sd");

        act.Should().Throw<ServiceConfigurationMissingException>()
            .WithMessage("*AssetServiceUrl*");
    }

    [Fact]
    public void CreateReportingClient_WithValidConfig_ReturnsClientWithTokenSet()
    {
        var factory = CreateFactory();

        var client = factory.CreateReportingClient("octosystem", "tok-rep");

        client.Should().NotBeNull();
        client.AccessToken.AccessToken.Should().Be("tok-rep");
        client.ServiceUri.ToString().Should().Contain("octosystem");
    }

    [Fact]
    public void CreateReportingClient_WithMissingUrl_ThrowsConfigurationMissing()
    {
        var factory = CreateFactory(reportingUrl: null);

        var act = () => factory.CreateReportingClient("octosystem", "tok-rep");

        act.Should().Throw<ServiceConfigurationMissingException>()
            .WithMessage("*ReportingServiceUrl*");
    }

    [Fact]
    public void CreateBotClient_WithValidConfig_ReturnsClientWithTokenSet()
    {
        var factory = CreateFactory();

        var client = factory.CreateBotClient("tok-bot");

        client.Should().NotBeNull();
        client.AccessToken.AccessToken.Should().Be("tok-bot");
    }

    [Fact]
    public void CreateBotClient_WithMissingUrl_ThrowsConfigurationMissing()
    {
        var factory = CreateFactory(botUrl: null);

        var act = () => factory.CreateBotClient("tok-bot");

        act.Should().Throw<ServiceConfigurationMissingException>()
            .WithMessage("*BotServiceUrl*");
    }

    [Fact]
    public void CreateAdminPanelClient_WithValidConfig_ReturnsClientWithTokenSet()
    {
        var factory = CreateFactory();

        var client = factory.CreateAdminPanelClient("tok-admin");

        client.Should().NotBeNull();
        client.AccessToken.AccessToken.Should().Be("tok-admin");
    }

    [Fact]
    public void CreateAdminPanelClient_WithMissingUrl_ThrowsConfigurationMissing()
    {
        var factory = CreateFactory(adminPanelUrl: null);

        var act = () => factory.CreateAdminPanelClient("tok-admin");

        act.Should().Throw<ServiceConfigurationMissingException>()
            .WithMessage("*AdminPanelUrl*");
    }

    [Fact]
    public void CreateAssetClient_TwoCallsDifferentTenants_ReturnIndependentClients()
    {
        var factory = CreateFactory();

        var a = factory.CreateAssetClient("tenant-a", "token-1");
        var b = factory.CreateAssetClient("tenant-b", "token-2");

        a.Should().NotBeSameAs(b);
        a.AccessToken.AccessToken.Should().Be("token-1");
        b.AccessToken.AccessToken.Should().Be("token-2");
        a.ServiceUri.ToString().Should().Contain("tenant-a");
        b.ServiceUri.ToString().Should().Contain("tenant-b");
    }
}
