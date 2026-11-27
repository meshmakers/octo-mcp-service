namespace Meshmakers.Octo.Backend.McpServices;

internal static class Constants
{
    public const string McpServiceIdentityDataVersionKey = "McpIdentityData";
    // 4 (AB#9729): the agent-exchange client is registered with the identity data.
    public const int McpServiceIdentityDataVersionValue = 4;

    public const string McpServiceEnabledKey = "McpServiceEnabled";

    /// <summary>
    ///     Client ID for the MCP Swagger UI client (Authorization Code Flow).
    /// </summary>
    public const string McpServicesSwaggerClientId = "octo-mcpServices-swagger";

    /// <summary>
    ///     Client ID for the MCP Device Authorization Flow client (for CLI/AI tools).
    /// </summary>
    public const string McpServicesDeviceClientId = "octo-mcpServices-device";

    /// <summary>
    ///     API scope of an agent's service-account client (AB#9729, System.Identity.Bootstrap 1.12.0). A token with it
    ///     opens no platform API; MCP accepts it only from an agent of the token's tenant and exchanges it for an
    ///     <c>octo_api</c> token before any downstream call.
    /// </summary>
    public const string AgentScope = "octo_agent";

    /// <summary>
    ///     Confidential client of the MCP service that alone may exchange an agent token (AB#9729). Registered in every
    ///     tenant with <see cref="Options.McpServiceOptions.AgentExchangeClientSecret" />; identity refuses the exchange
    ///     for any other presenter.
    /// </summary>
    public const string AgentExchangeClientId = "octo-mcpServices-agent-exchange";

    /// <summary>
    ///     Error of a downstream call of an agent whose token could not be exchanged (AB#9729): no exchange secret is
    ///     configured, identity refused the exchange (agent client disabled, client not registered) or was unreachable. The
    ///     agent token itself is never sent to a platform API.
    /// </summary>
    public const string AgentExchangeFailedError =
        "Access denied: the agent token could not be exchanged for a platform token (agent exchange not configured, " +
        "agent client disabled or identity unreachable). An agent token is never sent to a platform API.";

    /// <summary>Error of an agent that asks for another tenant than its own (AB#9729).</summary>
    public static string AgentOtherTenantError(string tenantId) =>
        $"Access denied: an agent acts only in its own tenant, not in '{tenantId}'.";

    /// <summary>
    ///     Uniform error for tools whose token resolution (McpSessionContext.TryGetAccessTokenAsync)
    ///     found neither a device-flow session token nor an inbound Authorization: Bearer header.
    ///     Names both login paths — interactive MCP clients authenticate via their own OAuth flow
    ///     and never call the 'authenticate' tool. Tests pin the "Not authenticated" prefix.
    /// </summary>
    public const string NotAuthenticatedError =
        "Not authenticated. Log in via your MCP client's OAuth flow (e.g. /mcp in Claude Code), or call the 'authenticate' tool for the device-code flow.";

    /// <summary>
    ///     Error for a failed RFC 8693 token exchange towards <paramref name="tenantId" /> (AB#7282): the
    ///     caller is authenticated but the identity server refused a token for that tenant (no access) or was
    ///     unreachable. Tests pin the "token exchange for tenant" phrase.
    /// </summary>
    /// <param name="tenantId">Tenant the exchange was attempted for.</param>
    public static string TenantExchangeFailedError(string tenantId) =>
        $"Access denied: token exchange for tenant '{tenantId}' failed. The caller may not access this tenant, " +
        "or the identity service is unreachable. The home-tenant token is never used for another tenant.";

    /// <summary>
    ///     Uniform error for a session-store token that does not belong to the authenticated caller
    ///     (AB#5036). Deliberately NOT <see cref="NotAuthenticatedError" />: the caller IS authenticated —
    ///     the stored session simply carries a different identity, and reporting "not authenticated" would
    ///     send AI clients into a pointless device-flow re-login. Both
    ///     <c>McpSessionContext.VerifyBinding</c> (family 1) and <c>RuntimeSecurityContextResolver</c>
    ///     (families 2/3) surface this same text so the two paths cannot drift; tests pin the
    ///     "does not belong to the authenticated caller" phrase.
    /// </summary>
    public const string SessionTokenNotBoundError =
        "Access denied: the stored session token does not belong to the authenticated caller. Re-run the "
        + "'authenticate' tool for this MCP session, or remove the 'Mcp-Session-Id' request header so the "
        + "request's own bearer token is used.";

    /// <summary>
    ///     Policy for system api authorization
    /// </summary>
    public const string SystemApiPolicy = "SystemApiPolicy";

    /// <summary>
    ///     Policy for tenant api read only authorization
    /// </summary>
    public const string TenantApiReadOnlyPolicy = "TenantApiReadOnlyPolicy";

    /// <summary>
    ///     Policy for tenant api read write authorization
    /// </summary>
    public const string TenantApiReadWritePolicy = "TenantApiReadWritePolicy";

    internal static readonly DateTime StartTime = DateTime.UtcNow;
}