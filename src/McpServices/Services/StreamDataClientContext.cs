using Meshmakers.Octo.Sdk.ServiceClient.AssetRepositoryServices.StreamData;
using Meshmakers.Octo.Sdk.ServiceClient.AssetRepositoryServices.Tenants;
using ModelContextProtocol.Server;

namespace Meshmakers.Octo.Backend.McpServices.Services;

/// <summary>
///     Shared bootstrapping for tools that talk to the Stream Data service (asset-repository hosted).
///     Counterpart to <see cref="IdentityClientContext"/>.
/// </summary>
/// <param name="Client">The Stream Data client (reads and the operations that stay REST).</param>
/// <param name="TenantId">The resolved tenant.</param>
/// <param name="Error">Why the context could not be built; <c>null</c> on success.</param>
/// <param name="TenantClient">
///     The asset repository client of the tenant with the same token, for the tools that run the Archive methods of
///     <c>System.StreamData</c>; built when the caller asks for it with <c>withTenantClient: true</c>.
/// </param>
internal sealed record StreamDataClientContext(
    IStreamDataServicesClient? Client,
    string? TenantId,
    string? Error,
    ITenantClient? TenantClient = null)
{
    /// <summary>
    ///     Resolves the access token (including lazy refresh) and builds a Stream Data client for
    ///     <paramref name="tenantIdParam"/>.
    /// </summary>
    /// <param name="server">The MCP server of the request.</param>
    /// <param name="tenantIdParam">The tenant of the tool call, or <c>null</c> for the route tenant.</param>
    /// <param name="withTenantClient">Also build <see cref="TenantClient"/> with the same token (CK method calls).</param>
    public static async Task<StreamDataClientContext> TryBuildAsync(McpServer server, string? tenantIdParam,
        bool withTenantClient = false)
    {
        try
        {
            var tenantResolver = server.Services!.GetRequiredService<ITenantResolutionService>();
            var tenantId = tenantResolver.ResolveTenantId(tenantIdParam);

            // Tenant-aware token (AB#4338): home tenant → session token; other tenant → exchanged B token.
            // AB#5036: a stored session token that does not belong to the request principal is refused
            // with its own message instead of being silently swapped for the request's bearer.
            var token = await McpSessionContext.ResolveAccessTokenAsync(server, tenantId);
            if (token.Error != null || token.AccessToken == null)
            {
                return new StreamDataClientContext(null, null, token.Error ?? Constants.NotAuthenticatedError);
            }

            var accessToken = token.AccessToken;

            var factory = server.Services!.GetRequiredService<IOctoServiceClientFactory>();
            var tenantClient = withTenantClient ? factory.CreateTenantClient(tenantId, accessToken) : null;
            return new StreamDataClientContext(factory.CreateStreamDataClient(tenantId, accessToken), tenantId, null,
                tenantClient);
        }
        catch (Exception ex)
        {
            return new StreamDataClientContext(null, null, ex.Message);
        }
    }
}
