using Meshmakers.Octo.Sdk.ServiceClient.BotServices;
using ModelContextProtocol.Server;

namespace Meshmakers.Octo.Backend.McpServices.Services;

/// <summary>
///     Shared bootstrapping for tools that talk to the Bot service. Bot service is system-scoped (no
///     tenant routing), but each call still passes the resolved tenantId as parameter.
/// </summary>
internal sealed record BotClientContext(
    IBotServicesClient? Client,
    string? TenantId,
    string? Error)
{
    /// <summary>
    ///     Resolves the access token (including lazy refresh) and builds a Bot client.
    /// </summary>
    public static async Task<BotClientContext> TryBuildAsync(McpServer server, string? tenantIdParam)
    {
        var accessToken = await McpSessionContext.TryGetAccessTokenAsync(server);
        if (accessToken == null)
        {
            return new BotClientContext(null, null, "Not authenticated. Call 'authenticate' first.");
        }

        try
        {
            var tenantResolver = server.Services!.GetRequiredService<ITenantResolutionService>();
            var tenantId = tenantResolver.ResolveTenantId(tenantIdParam);
            var factory = server.Services!.GetRequiredService<IOctoServiceClientFactory>();
            return new BotClientContext(factory.CreateBotClient(accessToken), tenantId, null);
        }
        catch (Exception ex)
        {
            return new BotClientContext(null, null, ex.Message);
        }
    }
}
