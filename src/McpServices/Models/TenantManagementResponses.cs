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
