using Meshmakers.Octo.Sdk.ServiceClient.AiServices;
using ModelContextProtocol.Server;

namespace Meshmakers.Octo.Backend.McpServices.Services;

/// <summary>
///     Shared bootstrapping for tools that talk to the AI service (AB#7192). Counterpart to
///     <see cref="CommunicationClientContext"/> and <see cref="ReportingClientContext"/>.
/// </summary>
internal sealed record AiClientContext(
    IAiServicesClient? Client,
    string? TenantId,
    string? Error)
{
    /// <summary>
    ///     Resolves the access token (including lazy refresh) and builds an AI client for
    ///     <paramref name="tenantIdParam"/>.
    /// </summary>
    public static async Task<AiClientContext> TryBuildAsync(McpServer server, string? tenantIdParam)
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
                return new AiClientContext(null, null, token.Error ?? Constants.NotAuthenticatedError);
            }

            var factory = server.Services!.GetRequiredService<IOctoServiceClientFactory>();
            return new AiClientContext(factory.CreateAiClient(tenantId, token.AccessToken), tenantId, null);
        }
        catch (Exception ex)
        {
            return new AiClientContext(null, null, ex.Message);
        }
    }
}
