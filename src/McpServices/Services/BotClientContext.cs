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
        try
        {
            var tenantResolver = server.Services!.GetRequiredService<ITenantResolutionService>();
            var tenantId = tenantResolver.ResolveTenantId(tenantIdParam);

            // Bot is NOT tenant-routed (CreateBotClient takes no tenantId), so TenantAuthorizationMiddleware
            // has no route tenant to check — the token's tenant_id does not gate it. Use the home/session
            // token; the resolved tenantId travels as an SDK call parameter. Deliberately do NOT do a
            // cross-tenant exchange here (AB#4338): unlike the five tenant-routed clients, an exchange could
            // fail for a tenant the user isn't cross-tenant-authorised for and would break bot operations
            // that the home token already serves against the not-tenant-routed bot service.
            var accessToken = await McpSessionContext.TryGetAccessTokenAsync(server);
            if (accessToken == null)
            {
                return new BotClientContext(null, null, Constants.NotAuthenticatedError);
            }

            var factory = server.Services!.GetRequiredService<IOctoServiceClientFactory>();
            return new BotClientContext(factory.CreateBotClient(accessToken), tenantId, null);
        }
        catch (Exception ex)
        {
            return new BotClientContext(null, null, ex.Message);
        }
    }
}
