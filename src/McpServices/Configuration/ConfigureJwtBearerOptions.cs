using Meshmakers.Common.Shared;
using Meshmakers.Octo.Backend.McpServices.Options;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Backend.McpServices.Configuration;

internal class ConfigureJwtBearerOptions(
    IOptions<McpServiceOptions> mcpServiceOptions)
    : IConfigureNamedOptions<JwtBearerOptions>
{
    public void Configure(JwtBearerOptions options)
    {
        Configure(Microsoft.Extensions.Options.Options.DefaultName, options);
    }

    public void Configure(string? name, JwtBearerOptions options)
    {
        var authorityUrl = mcpServiceOptions.Value.AuthorityUrl.EnsureEndsWith("/");
        options.Authority = authorityUrl;

        // Explicitly set the valid issuer so token validation does not depend on fetching
        // the OIDC discovery document. This prevents IDX10204 errors when the identity
        // service is temporarily unreachable (e.g. during rolling updates).
        options.TokenValidationParameters.ValidIssuer = authorityUrl;
    }
}