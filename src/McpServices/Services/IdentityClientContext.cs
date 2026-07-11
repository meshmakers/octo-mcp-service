using Meshmakers.Octo.Sdk.ServiceClient.IdentityServices;
using ModelContextProtocol.Server;

namespace Meshmakers.Octo.Backend.McpServices.Services;

/// <summary>
///     Shared bootstrapping for tools that talk to the Identity service: extract session token, resolve the
///     tenant, build an <see cref="IIdentityServicesClient"/>. Tools that share this pattern call
///     <see cref="TryBuildAsync"/> and either propagate <see cref="Error"/> or use <see cref="Client"/>.
/// </summary>
internal sealed record IdentityClientContext(
    IIdentityServicesClient? Client,
    string? TenantId,
    string? Error)
{
    /// <summary>
    ///     Resolves the access token (including lazy refresh / cross-tenant exchange) and builds an
    ///     Identity client for <paramref name="tenantIdParam"/>. Async because
    ///     <see cref="McpSessionContext.TryGetAccessTokenAsync(McpServer, string?, System.Threading.CancellationToken)"/>
    ///     may perform an OAuth2 refresh-token grant or an RFC 8693 token exchange.
    /// </summary>
    public static async Task<IdentityClientContext> TryBuildAsync(McpServer server, string? tenantIdParam)
    {
        try
        {
            var tenantResolver = server.Services!.GetRequiredService<ITenantResolutionService>();
            var tenantId = tenantResolver.ResolveTenantId(tenantIdParam);

            // Tenant-aware token (AB#4338): for the home tenant this returns the session token;
            // for a different tenant it transparently exchanges a B-scoped token.
            var accessToken = await McpSessionContext.TryGetAccessTokenAsync(server, tenantId);
            if (accessToken == null)
            {
                return new IdentityClientContext(null, null, Constants.NotAuthenticatedError);
            }

            var factory = server.Services!.GetRequiredService<IOctoServiceClientFactory>();
            return new IdentityClientContext(factory.CreateIdentityClient(tenantId, accessToken), tenantId, null);
        }
        catch (Exception ex)
        {
            return new IdentityClientContext(null, null, ex.Message);
        }
    }
}
