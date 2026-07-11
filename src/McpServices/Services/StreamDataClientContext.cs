using Meshmakers.Octo.Sdk.ServiceClient.AssetRepositoryServices.StreamData;
using ModelContextProtocol.Server;

namespace Meshmakers.Octo.Backend.McpServices.Services;

/// <summary>
///     Shared bootstrapping for tools that talk to the Stream Data service (asset-repository hosted).
///     Counterpart to <see cref="IdentityClientContext"/>.
/// </summary>
internal sealed record StreamDataClientContext(
    IStreamDataServicesClient? Client,
    string? TenantId,
    string? Error)
{
    /// <summary>
    ///     Resolves the access token (including lazy refresh) and builds a Stream Data client for
    ///     <paramref name="tenantIdParam"/>.
    /// </summary>
    public static async Task<StreamDataClientContext> TryBuildAsync(McpServer server, string? tenantIdParam)
    {
        try
        {
            var tenantResolver = server.Services!.GetRequiredService<ITenantResolutionService>();
            var tenantId = tenantResolver.ResolveTenantId(tenantIdParam);

            // Tenant-aware token (AB#4338): home tenant → session token; other tenant → exchanged B token.
            var accessToken = await McpSessionContext.TryGetAccessTokenAsync(server, tenantId);
            if (accessToken == null)
            {
                return new StreamDataClientContext(null, null, Constants.NotAuthenticatedError);
            }

            var factory = server.Services!.GetRequiredService<IOctoServiceClientFactory>();
            return new StreamDataClientContext(factory.CreateStreamDataClient(tenantId, accessToken), tenantId, null);
        }
        catch (Exception ex)
        {
            return new StreamDataClientContext(null, null, ex.Message);
        }
    }
}
