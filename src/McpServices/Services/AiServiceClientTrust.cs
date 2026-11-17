namespace Meshmakers.Octo.Backend.McpServices.Services;

/// <summary>
///     The one cross-tenant pair for client-credentials tokens (AB#9189, AB#9188): a service token of the AI service
///     client (<c>Mcp:AiServiceClientId</c>, default <c>octo-ai-adapter</c>) issued for the system tenant. Both halves are
///     needed, because client ids are unique per tenant only. Used by the in-tool gate
///     (<see cref="RuntimeSecurityContextResolver" />) and the HTTP route guard
///     (<see cref="Configuration.AiServiceClientTenantGuard" />), so both gates trust exactly the same tokens.
/// </summary>
internal static class AiServiceClientTrust
{
    /// <summary>
    ///     True when <paramref name="caller" /> is a service token of <paramref name="aiClientId" /> (exact,
    ///     case-sensitive) whose <c>tenant_id</c> is <paramref name="systemTenantId" /> (case-insensitive, like tenant ids
    ///     everywhere). False when either setting is empty.
    /// </summary>
    internal static bool IsTrustedPair(McpCallerPrincipal caller, string? aiClientId, string? systemTenantId)
    {
        aiClientId = aiClientId?.Trim();
        return !caller.IsUserPrincipal
               && !string.IsNullOrWhiteSpace(systemTenantId)
               && !string.IsNullOrEmpty(aiClientId)
               && string.Equals(caller.TenantId, systemTenantId, StringComparison.OrdinalIgnoreCase)
               && string.Equals(caller.ClientId, aiClientId, StringComparison.Ordinal);
    }
}
