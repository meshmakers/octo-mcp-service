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
        var accessToken = await McpSessionContext.TryGetAccessTokenAsync(server);
        if (accessToken == null)
        {
            return new StreamDataClientContext(null, null, "Not authenticated. Call 'authenticate' first.");
        }

        try
        {
            var tenantResolver = server.Services!.GetRequiredService<ITenantResolutionService>();
            var tenantId = tenantResolver.ResolveTenantId(tenantIdParam);

            var factory = server.Services!.GetRequiredService<IOctoServiceClientFactory>();
            return new StreamDataClientContext(factory.CreateStreamDataClient(tenantId, accessToken), tenantId, null);
        }
        catch (Exception ex)
        {
            return new StreamDataClientContext(null, null, ex.Message);
        }
    }
}
