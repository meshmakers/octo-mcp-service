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

        // Client-credentials and CLI tokens are issued for the "octoAPI" resource, not for this
        // service specifically. Mirror the other Octo services (asset-repo, identity, bot) and skip
        // audience validation — access is authorized by scope + tenant, not by audience.
        options.TokenValidationParameters.ValidateAudience = false;

        // Label the authenticated identity "Bearer" so TenantAuthorizationMiddleware runs its
        // route-tenant vs tenant_id check (it deliberately skips non-"Bearer" auth types to avoid
        // false positives on cookie principals). Without this, the JWT handler's default
        // "AuthenticationTypes.Federation" would make the tenant check a silent no-op — the MCP
        // server is JWT-only, so every authenticated identity is a bearer token (AB#4315).
        options.TokenValidationParameters.AuthenticationType = JwtBearerDefaults.AuthenticationScheme;

        // Explicitly set the valid issuer so token validation does not depend on fetching
        // the OIDC discovery document. This prevents IDX10204 errors when the identity
        // service is temporarily unreachable (e.g. during rolling updates).
        options.TokenValidationParameters.ValidIssuer = authorityUrl;
    }
}