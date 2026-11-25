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

    /// <summary>
    ///     With <c>starter=true</c> (AB#9570): every step of the starter sequence in order, with its outcome. Empty
    ///     without the starter.
    /// </summary>
    public List<StarterStepDto> StarterSteps { get; set; } = [];

    /// <summary>With <c>starter=true</c>: the step that failed, null when every step is done or skipped.</summary>
    public string? FailedStep { get; set; }
}

/// <summary>
///     One step of the <c>create_tenant starter=true</c> sequence (AB#9570).
/// </summary>
public class StarterStepDto
{
    /// <summary>
    ///     Step name: <c>create_tenant</c>, <c>provision_admin</c>, <c>enable_communication</c>, <c>enable_ai</c>,
    ///     <c>install_blueprint</c> or <c>verify_ready</c>.
    /// </summary>
    public string Step { get; set; } = string.Empty;

    /// <summary>
    ///     <c>done</c> (the step ran now), <c>skipped</c> (it was already done, e.g. by an earlier call), <c>failed</c>
    ///     or <c>notRun</c> (an earlier step failed).
    /// </summary>
    public string Outcome { get; set; } = string.Empty;

    /// <summary>What the step did, why it was skipped, or why it failed.</summary>
    public string? Detail { get; set; }
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
