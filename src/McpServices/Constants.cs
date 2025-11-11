namespace Meshmakers.Octo.Backend.McpServices;

internal static class Constants
{
    public const string McpServiceIdentityDataVersionKey = "McpIdentityData";
    public const int McpServiceIdentityDataVersionValue = 1;

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