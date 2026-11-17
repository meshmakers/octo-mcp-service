using Meshmakers.Octo.Backend.McpServices.Options;
using Meshmakers.Octo.Services.Infrastructure.Configuration;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Backend.McpServices.Configuration;

/// <summary>
///     AB#9188: trusts the AI service's MCP service client (<see cref="McpServiceOptions.AiServiceClientId" />, default
///     <c>octo-ai-adapter</c>) for cross-tenant service calls by default. The AI service mints one token of the system
///     tenant and calls <c>knowledge_adopt_platform_updates</c> on every AI tenant's route <c>/{tenantId}/mcp</c>; the
///     transport tenant gate (<c>UseOctoTenantAuthorization</c>, <c>ServiceTokenEnforcement=Enforce</c>) admits such a
///     token only when its client id is in <see cref="TenantAuthorizationOptions.CrossTenantServiceClientIds" />.
/// </summary>
/// <remarks>
///     <para>
///         Decision Gerald 2026-11-16 (AB#9168, "a"): an explicit allow-list entry for this one client, never a blanket
///         exemption. Hence a post-configuration that appends exactly one id after the section
///         <c>TenantAuthorization</c> was bound: entries an operator configured stay, the id is not added twice
///         (case-insensitive, like the match), and a value containing <c>*</c> is refused at startup, since a pattern
///         (or a lone <c>*</c>) would exempt every client.
///     </para>
///     <para>
///         The default lives here because it is a property of this service (it trusts the platform's own AI service),
///         not of the estate: octo-common-services keeps the list empty, the chart needs no per-cluster value.
///     </para>
/// </remarks>
internal sealed class ConfigureTenantAuthorizationOptions(IOptions<McpServiceOptions> mcpServiceOptions)
    : IPostConfigureOptions<TenantAuthorizationOptions>
{
    /// <inheritdoc />
    public void PostConfigure(string? name, TenantAuthorizationOptions options)
    {
        var clientId = mcpServiceOptions.Value.AiServiceClientId?.Trim();
        if (string.IsNullOrEmpty(clientId))
        {
            return;
        }

        if (clientId.Contains('*'))
        {
            throw new OptionsValidationException(nameof(McpServiceOptions.AiServiceClientId), typeof(McpServiceOptions),
            [
                $"Mcp:AiServiceClientId '{clientId}' must be an exact client id; a '*' pattern would exempt more " +
                "clients than the AI service from the tenant check."
            ]);
        }

        if (options.CrossTenantServiceClientIds.Any(entry =>
                string.Equals(entry?.Trim(), clientId, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        options.CrossTenantServiceClientIds.Add(clientId);
    }
}
