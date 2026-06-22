using Meshmakers.Octo.Sdk.ServiceClient.AssetRepositoryServices.System;
using ModelContextProtocol.Server;

namespace Meshmakers.Octo.Backend.McpServices.Services;

/// <summary>
///     Shared bootstrapping for tools that talk to the Asset Repository service. Counterpart to
///     <see cref="IdentityClientContext"/>.
/// </summary>
internal sealed record AssetClientContext(
    IAssetServicesClient? Client,
    string? TenantId,
    string? Error)
{
    /// <summary>
    ///     Resolves the access token (including lazy refresh) and builds an Asset client for
    ///     <paramref name="tenantIdParam"/>.
    /// </summary>
    public static async Task<AssetClientContext> TryBuildAsync(McpServer server, string? tenantIdParam)
    {
        var accessToken = await McpSessionContext.TryGetAccessTokenAsync(server);
        if (accessToken == null)
        {
            return new AssetClientContext(null, null, "Not authenticated. Call 'authenticate' first.");
        }

        try
        {
            var tenantResolver = server.Services!.GetRequiredService<ITenantResolutionService>();
            var tenantId = tenantResolver.ResolveTenantId(tenantIdParam);

            var factory = server.Services!.GetRequiredService<IOctoServiceClientFactory>();
            return new AssetClientContext(factory.CreateAssetClient(tenantId, accessToken), tenantId, null);
        }
        catch (Exception ex)
        {
            return new AssetClientContext(null, null, ex.Message);
        }
    }
}
