using Meshmakers.Common.Shared;
using Meshmakers.Octo.Backend.McpServices.Options;
using Microsoft.Extensions.Options;
using ModelContextProtocol.AspNetCore.Authentication;
using ModelContextProtocol.Authentication;

namespace Meshmakers.Octo.Backend.McpServices.Configuration;

/// <summary>
///     Configures the MCP authentication scheme's Protected Resource Metadata (RFC 9728) so that an
///     interactive MCP client (e.g. Claude Code) can discover how to authenticate. When a request to a
///     gated <c>/mcp</c> endpoint is unauthenticated, the MCP challenge handler answers 401 with a
///     <c>WWW-Authenticate: Bearer resource_metadata="…"</c> header and serves the metadata document at
///     <c>/.well-known/oauth-protected-resource</c>. The client then runs the OAuth2 Authorization Code
///     + PKCE flow against the advertised authorization server (Duende) and presents the resulting
///     bearer on subsequent requests — the same token the transport gate (AB#4315) validates.
/// </summary>
internal class ConfigureMcpAuthenticationOptions(
    IOptions<McpServiceOptions> mcpServiceOptions)
    : IConfigureNamedOptions<McpAuthenticationOptions>
{
    public void Configure(McpAuthenticationOptions options)
    {
        Configure(Microsoft.Extensions.Options.Options.DefaultName, options);
    }

    public void Configure(string? name, McpAuthenticationOptions options)
    {
        // The resource identifier is THIS service's public base URL — the address clients actually
        // reach the MCP transport at. The authorization server is the same Duende authority the JWT
        // bearer handler validates against, pinned with a trailing slash to match the issuer in
        // ConfigureJwtBearerOptions.
        var resourceUrl = mcpServiceOptions.Value.PublicUrl;
        var authorityUrl = mcpServiceOptions.Value.AuthorityUrl.EnsureEndsWith("/");

        options.ResourceMetadata = new ProtectedResourceMetadata
        {
            Resource = resourceUrl,
            AuthorizationServers = { authorityUrl },
            // Scopes the interactive client should request; mirrors the octo-mcpServices device/swagger
            // clients. offline_access yields a refresh token so Claude Code can refresh silently.
            ScopesSupported = { "openid", "profile", "email", "role", "octo_api", "offline_access" }
            // BearerMethodsSupported already defaults to ["header"] — don't re-add it (would duplicate).
        };
    }
}
