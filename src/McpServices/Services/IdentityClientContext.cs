using Meshmakers.Octo.Sdk.ServiceClient.AssetRepositoryServices.Tenants;
using Meshmakers.Octo.Sdk.ServiceClient.IdentityServices;
using ModelContextProtocol.Server;

namespace Meshmakers.Octo.Backend.McpServices.Services;

/// <summary>
///     Shared bootstrapping for tools that talk to the Identity service: extract session token, resolve the
///     tenant, build an <see cref="IIdentityServicesClient"/>. Tools that share this pattern call
///     <see cref="TryBuildAsync"/> and either propagate <see cref="Error"/> or use <see cref="Client"/>.
/// </summary>
/// <param name="Client">The Identity REST client of the tenant.</param>
/// <param name="TenantId">The resolved tenant.</param>
/// <param name="Error">The reason the context could not be built, or <c>null</c>.</param>
/// <param name="TenantClient">
///     The tenant's GraphQL client (same token) for CK method calls such as <c>User.AddRole</c> (AB#8440); only
///     built when the caller asks for it with <c>withTenantClient: true</c>.
/// </param>
internal sealed record IdentityClientContext(
    IIdentityServicesClient? Client,
    string? TenantId,
    string? Error,
    ITenantClient? TenantClient = null)
{
    /// <summary>
    ///     Resolves the access token (including lazy refresh / cross-tenant exchange) and builds an
    ///     Identity client for <paramref name="tenantIdParam"/>. Async because
    ///     <see cref="McpSessionContext.TryGetAccessTokenAsync(McpServer, string?, System.Threading.CancellationToken)"/>
    ///     may perform an OAuth2 refresh-token grant or an RFC 8693 token exchange.
    /// </summary>
    /// <param name="server">The MCP server of the request.</param>
    /// <param name="tenantIdParam">The tenant of the tool call, or <c>null</c> for the route tenant.</param>
    /// <param name="withTenantClient">
    ///     Also build <see cref="TenantClient"/> with the same token, for tools that call CK methods (AB#8440).
    /// </param>
    public static async Task<IdentityClientContext> TryBuildAsync(McpServer server, string? tenantIdParam,
        bool withTenantClient = false)
    {
        try
        {
            var tenantResolver = server.Services!.GetRequiredService<ITenantResolutionService>();
            var tenantId = tenantResolver.ResolveTenantId(tenantIdParam);

            // Tenant-aware token (AB#4338): for the home tenant this returns the session token;
            // for a different tenant it transparently exchanges a B-scoped token.
            // AB#5036: a stored session token that does not belong to the request principal is refused
            // with its own message instead of being silently swapped for the request's bearer.
            var token = await McpSessionContext.ResolveAccessTokenAsync(server, tenantId);
            if (token.Error != null || token.AccessToken == null)
            {
                return new IdentityClientContext(null, null, token.Error ?? Constants.NotAuthenticatedError);
            }

            var accessToken = token.AccessToken;

            var factory = server.Services!.GetRequiredService<IOctoServiceClientFactory>();
            var tenantClient = withTenantClient ? factory.CreateTenantClient(tenantId, accessToken) : null;
            return new IdentityClientContext(factory.CreateIdentityClient(tenantId, accessToken), tenantId, null,
                tenantClient);
        }
        catch (Exception ex)
        {
            return new IdentityClientContext(null, null, ex.Message);
        }
    }
}
