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