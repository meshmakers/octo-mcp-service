using FluentAssertions;
using Meshmakers.Octo.Backend.McpServices.Configuration;
using Meshmakers.Octo.Backend.McpServices.Options;
using Microsoft.Extensions.Options;
using ModelContextProtocol.AspNetCore.Authentication;
using Xunit;

namespace McpServices.Tests.Configuration;

/// <summary>
/// Pins the Protected Resource Metadata (RFC 9728) the MCP challenge scheme advertises so interactive
/// clients (Claude Code) can discover how to authenticate. A regression here — wrong resource, wrong
/// authorization server, or a dropped scope — would send the client to the wrong place or make it
/// request a token the transport gate (AB#4315) then rejects.
/// </summary>
public class ConfigureMcpAuthenticationOptionsTests
{
    private static McpAuthenticationOptions Configure(string publicUrl, string authorityUrl)
    {
        var mcpOptions = Options.Create(new McpServiceOptions
        {
            PublicUrl = publicUrl,
            AuthorityUrl = authorityUrl
        });
        var sut = new ConfigureMcpAuthenticationOptions(mcpOptions);
        var options = new McpAuthenticationOptions();

        sut.Configure(McpAuthenticationDefaults.AuthenticationScheme, options);
        return options;
    }

    [Fact]
    public void Configure_AdvertisesPublicUrlAsResource()
    {
        var options = Configure("https://localhost:5017", "https://localhost:5003");

        // The resource identifier is THIS service's public URL — the address the client actually
        // reaches the transport at, and the resource indicator it sends to the token endpoint.
        options.ResourceMetadata.Should().NotBeNull();
        options.ResourceMetadata!.Resource.Should().Be("https://localhost:5017");
    }

    [Fact]
    public void Configure_PointsAtAuthorityWithTrailingSlashIssuer()
    {
        var options = Configure("https://localhost:5017", "https://identity.example.com");

        // Must match the issuer the JWT bearer handler pins (ConfigureJwtBearerOptions adds the slash),
        // otherwise the token the client obtains would be rejected on validation.
        options.ResourceMetadata!.AuthorizationServers.Should().ContainSingle()
            .Which.Should().Be("https://identity.example.com/");
    }

    [Fact]
    public void Configure_AdvertisesOctoApiAndOfflineAccessScopes()
    {
        var options = Configure("https://localhost:5017", "https://localhost:5003");

        // octo_api is the scope MCP tokens are authorized by; offline_access yields a refresh token so
        // the client can refresh silently. openid is required for the OIDC login.
        options.ResourceMetadata!.ScopesSupported.Should()
            .Contain(new[] { "openid", "octo_api", "offline_access" });
    }

    [Fact]
    public void Configure_AdvertisesHeaderBearerMethod()
    {
        var options = Configure("https://localhost:5017", "https://localhost:5003");

        options.ResourceMetadata!.BearerMethodsSupported.Should().Contain("header");
    }
}
