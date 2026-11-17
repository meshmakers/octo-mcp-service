using Meshmakers.Octo.Backend.McpServices.Options;
using Meshmakers.Octo.Backend.McpServices.Services;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Services.Infrastructure;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Backend.McpServices.Configuration;

/// <summary>
///     AB#9188: binds the HTTP gate's default allow-list entry for the AI service's client
///     (<see cref="McpServiceOptions.AiServiceClientId" />, added by <see cref="ConfigureTenantAuthorizationOptions" />) to the
///     trusted pair of the in-tool gate (<see cref="AiServiceClientTrust" />, AB#9189). The shared route gate of
///     octo-common-services matches <c>CrossTenantServiceClientIds</c> by <c>client_id</c> alone and case-insensitively, and
///     client ids are unique only within one tenant. Without this guard a tenant administrator could create a client
///     named <c>octo-ai-adapter</c> in their own tenant and pass every other tenant's route (the tools would then still be
///     refused by the in-tool gate, but the route gate must not open wider than the trust). It runs right before
///     <c>UseOctoTenantAuthorization</c> and answers 403 for a service token whose client id the entry would admit but
///     that is not the trusted pair, when it addresses a foreign route tenant. On its own tenant's route such a client
///     works like any tenant client.
/// </summary>
internal sealed class AiServiceClientTenantGuard(
    RequestDelegate next,
    IOptions<McpServiceOptions> mcpServiceOptions,
    IOptions<OctoSystemConfiguration> systemConfiguration,
    ILogger<AiServiceClientTenantGuard> logger)
{
    /// <summary>Middleware entry point.</summary>
    public async Task InvokeAsync(HttpContext context)
    {
        var aiClientId = mcpServiceOptions.Value.AiServiceClientId?.Trim();
        // The same claim reader as the in-tool gate (sub/NameIdentifier, client_id, tenant_id); only bearer principals,
        // like the shared route gate.
        var caller = context.User.Identity?.AuthenticationType is { } authType &&
                     authType.Equals("Bearer", StringComparison.OrdinalIgnoreCase)
            ? McpCallerPrincipal.FromHttpContext(context)
            : null;
        if (string.IsNullOrEmpty(aiClientId) || caller == null || caller.IsUserPrincipal ||
            !string.Equals(caller.ClientId, aiClientId, StringComparison.OrdinalIgnoreCase) ||
            AiServiceClientTrust.IsTrustedPair(caller, aiClientId, systemConfiguration.Value.SystemTenantId))
        {
            await next(context);
            return;
        }

        var routeTenantId = context.GetTenantId();
        if (string.IsNullOrEmpty(routeTenantId) ||
            string.Equals(caller.TenantId, routeTenantId, StringComparison.OrdinalIgnoreCase))
        {
            await next(context);
            return;
        }

        logger.LogWarning(
            "Denied: service token of client '{ClientId}' was issued for tenant '{TokenTenantId}', not as the AI service " +
            "client of the system tenant, and addresses tenant '{RouteTenantId}' (AB#9188)",
            Sanitize(caller.ClientId), Sanitize(caller.TenantId), Sanitize(routeTenantId));
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await context.Response.WriteAsJsonAsync(new
        {
            message = $"Access to tenant '{routeTenantId}' is denied: client '{aiClientId}' is trusted across tenants only " +
                      "with a token of the system tenant."
        });
    }

    private static string Sanitize(string? value) =>
        value == null ? "<none>" : new string(value.Where(c => !char.IsControl(c)).Take(128).ToArray());
}
