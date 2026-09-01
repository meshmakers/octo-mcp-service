namespace Meshmakers.Octo.Backend.McpServices;

internal static class Constants
{
    public const string McpServiceIdentityDataVersionKey = "McpIdentityData";
    public const int McpServiceIdentityDataVersionValue = 3;

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
    ///     Uniform error for tools whose token resolution (McpSessionContext.TryGetAccessTokenAsync)
    ///     found neither a device-flow session token nor an inbound Authorization: Bearer header.
    ///     Names both login paths — interactive MCP clients authenticate via their own OAuth flow
    ///     and never call the 'authenticate' tool. Tests pin the "Not authenticated" prefix.
    /// </summary>
    public const string NotAuthenticatedError =
        "Not authenticated. Log in via your MCP client's OAuth flow (e.g. /mcp in Claude Code), or call the 'authenticate' tool for the device-code flow.";

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