using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;

namespace Meshmakers.Octo.Backend.McpServices.Models;

/// <summary>
///     Common envelope for tenant-management tool responses.
/// </summary>
public class TenantManagementResponse
{
    /// <summary>True when the underlying service call succeeded.</summary>
    public bool IsSuccess { get; set; }

    /// <summary>Error message when <see cref="IsSuccess"/> is false.</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>Optional human-readable status message.</summary>
    public string? Message { get; set; }
}

/// <summary>
///     Minimal tenant projection returned by the MCP tenant tools.
/// </summary>
public class TenantInfoDto
{
    /// <summary>Tenant identifier.</summary>
    public string? TenantId { get; set; }

    /// <summary>Backing database name.</summary>
    public string? Database { get; set; }
}

/// <summary>
///     Response of the <c>get_tenants</c> tool.
/// </summary>
public class GetTenantsResponse : TenantManagementResponse
{
    /// <summary>Child tenants returned by the Asset Repository service.</summary>
    public List<TenantInfoDto> Tenants { get; set; } = [];

    /// <summary>Total number of child tenants.</summary>
    public int TotalCount { get; set; }

    /// <summary>Parent tenant whose children were enumerated.</summary>
    public string? ParentTenantId { get; set; }
}

/// <summary>
///     Response of the <c>create_tenant</c> tool.
/// </summary>
public class CreateTenantResponse : TenantManagementResponse
{
    /// <summary>Identifier of the newly-created child tenant.</summary>
    public string? CreatedTenantId { get; set; }

    /// <summary>Backing database name of the new tenant.</summary>
    public string? Database { get; set; }

    /// <summary>Parent tenant under which the child was created.</summary>
    public string? ParentTenantId { get; set; }
}

/// <summary>
///     Response of the <c>delete_tenant</c> tool.
/// </summary>
public class DeleteTenantResponse : TenantManagementResponse
{
    /// <summary>Identifier of the deleted child tenant.</summary>
    public string? DeletedTenantId { get; set; }

    /// <summary>Parent tenant from which the child was removed.</summary>
    public string? ParentTenantId { get; set; }

    /// <summary>
    ///     Capabilities <c>disableFeatures</c> switched off before the delete (AB#7192), by display name
    ///     (AI Services, Communication, Reporting, Stream Data). Empty when the flag was not set or nothing was enabled.
    /// </summary>
    public List<string> DisabledFeatures { get; set; } = [];

    /// <summary>
    ///     Capabilities that stay enabled and why (AB#7192): for Stream Data the activated archives, which are reported and
    ///     never deleted. The tenant is not deleted while this is not empty.
    /// </summary>
    public List<string> FeaturesStillEnabled { get; set; } = [];
}

/// <summary>
///     Response of <c>enable_ai</c> / <c>disable_ai</c> (AB#7192).
/// </summary>
public class AiLifecycleResponse : TenantManagementResponse
{
    /// <summary>Tenant whose AI Services capability was toggled.</summary>
    public string? TenantId { get; set; }
}

/// <summary>
///     Response of the simple single-tenant operations (clean, attach, detach, clear-cache, update-system-ck-model).
/// </summary>
public class TenantOperationResponse : TenantManagementResponse
{
    /// <summary>Child tenant the operation targeted.</summary>
    public string? ChildTenantId { get; set; }

    /// <summary>Parent tenant context.</summary>
    public string? ParentTenantId { get; set; }
}

/// <summary>
///     Response of the <c>get_tenant_features</c> tool (AB#4884).
/// </summary>
public class TenantFeaturesResponse : TenantManagementResponse
{
    /// <summary>Tenant whose feature status was read.</summary>
    public string? TenantId { get; set; }

    /// <summary>Aggregate enabled-state of the four capabilities the delete/detach guard evaluates.</summary>
    public TenantFeaturesStatusDto? Features { get; set; }
}
