using System.Security.Claims;
using Meshmakers.Octo.Backend.McpServices.Options;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Services.Infrastructure;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Backend.McpServices.Configuration;

/// <summary>
///     AB#9188: binds the default allow-list entry for the AI service's client (<see cref="McpServiceOptions.AiServiceClientId" />)
///     to the <b>system tenant</b>. The transport tenant gate of octo-common-services matches
///     <c>CrossTenantServiceClientIds</c> by <c>client_id</c> alone, and client ids are unique only within one tenant: a
///     tenant administrator could create a client named <c>octo-ai-adapter</c> in their own tenant and its token would
///     then pass every other tenant's route. This middleware runs right before <c>UseOctoTenantAuthorization</c> and
///     refuses (403) a service token of that client id that was issued for another tenant than the system tenant when it
///     addresses a foreign route tenant. Such a client still works on its own tenant's route, like any tenant client.
/// </summary>
internal sealed class AiServiceClientTenantGuard(
    RequestDelegate next,
    IOptions<McpServiceOptions> mcpServiceOptions,
    IOptions<OctoSystemConfiguration> systemConfiguration,
    ILogger<AiServiceClientTenantGuard> logger)
{
    private const string ClientIdClaimType = "client_id";
    private const string TenantIdClaimType = "tenant_id";

    /// <summary>Middleware entry point.</summary>
    public async Task InvokeAsync(HttpContext context)
    {
        var aiClientId = mcpServiceOptions.Value.AiServiceClientId?.Trim();
        var user = context.User;
        if (string.IsNullOrEmpty(aiClientId) ||
            user.Identity is not { IsAuthenticated: true, AuthenticationType: { } authType } ||
            !authType.Equals("Bearer", StringComparison.OrdinalIgnoreCase) ||
            user.HasClaim(c => c.Type == "sub" || c.Type == ClaimTypes.NameIdentifier) ||
            !string.Equals(user.FindFirstValue(ClientIdClaimType), aiClientId, StringComparison.OrdinalIgnoreCase))
        {
            await next(context);
            return;
        }

        var tokenTenantId = user.FindFirstValue(TenantIdClaimType);
        var routeTenantId = context.GetTenantId();
        if (string.IsNullOrEmpty(routeTenantId) ||
            string.Equals(tokenTenantId, systemConfiguration.Value.SystemTenantId, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(tokenTenantId, routeTenantId, StringComparison.OrdinalIgnoreCase))
        {
            await next(context);
            return;
        }

        logger.LogWarning(
            "Denied: service token of client '{ClientId}' was issued for tenant '{TokenTenantId}', not the system tenant, " +
            "and addresses tenant '{RouteTenantId}' (AB#9188)", aiClientId, tokenTenantId, routeTenantId);
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await context.Response.WriteAsJsonAsync(new
        {
            message = $"Access to tenant '{routeTenantId}' is denied: client '{aiClientId}' is trusted across tenants only " +
                      "with a token of the system tenant."
        });
    }
}
