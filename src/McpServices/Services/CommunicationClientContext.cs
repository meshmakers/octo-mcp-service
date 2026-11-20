using Meshmakers.Octo.Sdk.ServiceClient.AssetRepositoryServices.Tenants;
using Meshmakers.Octo.Sdk.ServiceClient.CommunicationControllerServices;
using ModelContextProtocol.Server;

namespace Meshmakers.Octo.Backend.McpServices.Services;

/// <summary>
///     Shared bootstrapping for tools that talk to the Communication Controller. Counterpart to
///     <see cref="IdentityClientContext"/> and <see cref="AssetClientContext"/>.
/// </summary>
/// <param name="Client">The Communication Controller client (reads and the operations that stay REST).</param>
/// <param name="TenantId">The resolved tenant.</param>
/// <param name="Error">Why the context could not be built; <c>null</c> on success.</param>
/// <param name="TenantClient">
///     The asset repository client of the tenant with the same token, for the tools that run the CK methods of
///     <c>System.Communication</c>; built when the caller asks for it with <c>withTenantClient: true</c>.
/// </param>
internal sealed record CommunicationClientContext(
    ICommunicationServicesClient? Client,
    string? TenantId,
    string? Error,
    ITenantClient? TenantClient = null)
{
    /// <summary>
    ///     Resolves the access token (including lazy refresh) and builds a Communication client for
    ///     <paramref name="tenantIdParam"/>.
    /// </summary>
    /// <param name="server">The MCP server of the request.</param>
    /// <param name="tenantIdParam">The tenant of the tool call, or <c>null</c> for the route tenant.</param>
    /// <param name="withTenantClient">Also build <see cref="TenantClient"/> with the same token (CK method calls).</param>
    public static async Task<CommunicationClientContext> TryBuildAsync(McpServer server, string? tenantIdParam,
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
                return new CommunicationClientContext(null, null, token.Error ?? Constants.NotAuthenticatedError);
            }

            var accessToken = token.AccessToken;

            var factory = server.Services!.GetRequiredService<IOctoServiceClientFactory>();
            var tenantClient = withTenantClient ? factory.CreateTenantClient(tenantId, accessToken) : null;
            return new CommunicationClientContext(
                factory.CreateCommunicationClient(tenantId, accessToken),
                tenantId,
                null,
                tenantClient);
        }
        catch (Exception ex)
        {
            return new CommunicationClientContext(null, null, ex.Message);
        }
    }
}
