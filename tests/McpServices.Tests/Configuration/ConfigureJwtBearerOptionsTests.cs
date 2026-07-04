using FluentAssertions;
using Meshmakers.Octo.Backend.McpServices.Configuration;
using Meshmakers.Octo.Backend.McpServices.Options;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Xunit;

namespace McpServices.Tests.Configuration;

/// <summary>
/// Pins the JWT bearer validation contract for the MCP transport (AB#4315). The MCP endpoints
/// gained <c>.RequireAuthorization()</c>; this only protects tenant data if the bearer handler is
/// actually configured to validate tokens. A regression here (e.g. audience validation flipped back
/// on, or the issuer left unpinned) would either reject every valid Octo token or accept tokens the
/// service should not.
/// </summary>
public class ConfigureJwtBearerOptionsTests
{
    private static JwtBearerOptions Configure(string authorityUrl)
    {
        var mcpOptions = Options.Create(new McpServiceOptions { AuthorityUrl = authorityUrl });
        var sut = new ConfigureJwtBearerOptions(mcpOptions);
        var options = new JwtBearerOptions();

        sut.Configure(JwtBearerDefaults.AuthenticationScheme, options);
        return options;
    }

    [Fact]
    public void Configure_PinsAuthorityAndIssuerWithTrailingSlash()
    {
        var options = Configure("https://identity.example.com");

        // Octo IdentityServer issues tokens with a trailing-slash issuer; Authority + ValidIssuer
        // must match that slash form exactly or validation fails (IDX10205).
        options.Authority.Should().Be("https://identity.example.com/");
        options.TokenValidationParameters.ValidIssuer.Should().Be("https://identity.example.com/");
    }

    [Fact]
    public void Configure_DisablesAudienceValidation()
    {
        var options = Configure("https://identity.example.com/");

        // Client-credentials / CLI tokens target the shared "octoAPI" resource, not this service —
        // access is authorized by scope + tenant, not audience. Mirrors asset-repo / identity / bot.
        options.TokenValidationParameters.ValidateAudience.Should().BeFalse();
    }

    [Fact]
    public void Configure_LabelsIdentityAsBearerSoTenantMiddlewareRuns()
    {
        var options = Configure("https://identity.example.com/");

        // TenantAuthorizationMiddleware only validates route-tenant vs tenant_id for "Bearer"
        // identities; the JWT default ("AuthenticationTypes.Federation") would silently skip it.
        options.TokenValidationParameters.AuthenticationType.Should().Be(JwtBearerDefaults.AuthenticationScheme);
    }

    [Fact]
    public void Configure_IsIdempotentAcrossSchemeNames()
    {
        // The configurator ignores the scheme name (applies to the "Bearer" named options the same
        // as the default), so AddJwtBearer() picks it up regardless of how the scheme is registered.
        var mcpOptions = Options.Create(new McpServiceOptions { AuthorityUrl = "https://id/" });
        var sut = new ConfigureJwtBearerOptions(mcpOptions);
        var options = new JwtBearerOptions();

        sut.Configure(options);

        options.Authority.Should().Be("https://id/");
        options.TokenValidationParameters.ValidateAudience.Should().BeFalse();
    }
}
