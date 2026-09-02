using Meshmakers.Octo.Sdk.ServiceClient.BotServices;
using ModelContextProtocol.Server;

namespace Meshmakers.Octo.Backend.McpServices.Services;

/// <summary>
///     Shared bootstrapping for tools that talk to the Bot service. The resolved tenantId travels as an
///     SDK call parameter; since AB#5060 the SDK turns it into a route segment for the five job verbs,
///     while job status, download and the log-level call stay system-scoped.
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

            // Use the home/session token; the resolved tenantId travels as an SDK call parameter.
            // Deliberately no cross-tenant exchange here (AB#4338) — but note the reason changed with
            // AB#5060. It used to be that bot was not tenant-routed at all, so the middleware had no
            // route tenant and nothing gated the call. That is no longer true: the five job verbs
            // (dump, fixup, archive export/import, restore) now post to {tenantId}/v1/jobs/… and the
            // gate does check them. The home token is still the right one anyway, because the case
            // these routes were added for — an administrator acting on a child tenant — is exactly
            // what [AllowParentTenantAdministration] admits for a *user* token bearing the parent's
            // tenant_id. Exchanging to the target tenant would present the call as native to it and
            // route around that design, and it would still fail for a tenant the user has no claim
            // on. What legitimately changes is that a cross-tenant call with no ancestor relation is
            // now refused instead of silently executed. Everything else on this client (job status,
            // download, log level) stays on system/v1 and remains ungated.
            // AB#5036: a stored session token that does not belong to the request principal is refused
            // with its own message instead of being silently swapped for the request's bearer.
            var token = await McpSessionContext.ResolveAccessTokenAsync(server);
            if (token.Error != null || token.AccessToken == null)
            {
                return new BotClientContext(null, null, token.Error ?? Constants.NotAuthenticatedError);
            }

            var accessToken = token.AccessToken;

            var factory = server.Services!.GetRequiredService<IOctoServiceClientFactory>();
            return new BotClientContext(factory.CreateBotClient(accessToken), tenantId, null);
        }
        catch (Exception ex)
        {
            return new BotClientContext(null, null, ex.Message);
        }
    }
}
