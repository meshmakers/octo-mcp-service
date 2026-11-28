using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;

namespace Meshmakers.Octo.Backend.McpServices.Models;

/// <summary>
///     Common envelope for identity-management tool responses.
/// </summary>
public class IdentityResponse
{
    /// <summary>True when the underlying service call succeeded.</summary>
    public bool IsSuccess { get; set; }

    /// <summary>Error message when <see cref="IsSuccess"/> is false.</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>Optional human-readable status message.</summary>
    public string? Message { get; set; }

    /// <summary>Tenant the operation was executed against.</summary>
    public string? TenantId { get; set; }
}

/// <summary>List of users response.</summary>
public class GetUsersResponse : IdentityResponse
{
    /// <summary>Users returned by the identity service.</summary>
    public List<UserDto> Users { get; set; } = [];

    /// <summary>Total number of users.</summary>
    public int TotalCount { get; set; }
}

/// <summary>Single-user response.</summary>
public class UserResponse : IdentityResponse
{
    /// <summary>User identifier (name or e-mail) that was operated on.</summary>
    public string? User { get; set; }
}

/// <summary>List of roles response.</summary>
public class GetRolesResponse : IdentityResponse
{
    /// <summary>Roles returned by the identity service.</summary>
    public List<RoleDto> Roles { get; set; } = [];

    /// <summary>Total number of roles.</summary>
    public int TotalCount { get; set; }
}

/// <summary>Single-role response.</summary>
public class RoleResponse : IdentityResponse
{
    /// <summary>Role name that was operated on.</summary>
    public string? RoleName { get; set; }
}

/// <summary>List of groups response.</summary>
public class GetGroupsResponse : IdentityResponse
{
    /// <summary>Groups returned by the identity service.</summary>
    public List<GroupDto> Groups { get; set; } = [];

    /// <summary>Total number of groups.</summary>
    public int TotalCount { get; set; }
}

/// <summary>Single-group response.</summary>
public class GroupResponse : IdentityResponse
{
    /// <summary>Group RtId that was operated on.</summary>
    public string? GroupId { get; set; }

    /// <summary>Full group projection, when applicable.</summary>
    public GroupDto? Group { get; set; }
}

/// <summary>List of clients response.</summary>
public class GetClientsResponse : IdentityResponse
{
    /// <summary>Clients returned by the identity service.</summary>
    public List<ClientDto> Clients { get; set; } = [];

    /// <summary>Total number of clients.</summary>
    public int TotalCount { get; set; }
}

/// <summary>Single-client response.</summary>
public class ClientResponse : IdentityResponse
{
    /// <summary>Client identifier that was operated on.</summary>
    public string? ClientId { get; set; }

    /// <summary>Full client projection, when applicable.</summary>
    public ClientDto? Client { get; set; }
}

/// <summary>Response carrying a client's directly-assigned role IDs.</summary>
public class ClientRolesResponse : IdentityResponse
{
    /// <summary>Client identifier that was operated on.</summary>
    public string? ClientId { get; set; }

    /// <summary>Directly-assigned role IDs (excluding group-inherited roles).</summary>
    public List<string> RoleIds { get; set; } = [];
}

/// <summary>Response for ProvisionClientInExistingTenants.</summary>
public class ClientMirrorBackfillResponse : IdentityResponse
{
    /// <summary>Client identifier that was provisioned.</summary>
    public string? ClientId { get; set; }

    /// <summary>Raw backfill response from the identity service.</summary>
    public ClientMirrorBackfillResponseDto? Backfill { get; set; }
}

/// <summary>Response for ProvisionClientInTenant.</summary>
public class ClientMirrorProvisionResponse : IdentityResponse
{
    /// <summary>Client identifier that was provisioned.</summary>
    public string? ClientId { get; set; }

    /// <summary>Target child tenant.</summary>
    public string? ChildTenantId { get; set; }

    /// <summary>Raw provision response.</summary>
    public ClientMirrorProvisionResponseDto? Provision { get; set; }
}

/// <summary>Response for GetClientMirrors.</summary>
public class GetClientMirrorsResponse : IdentityResponse
{
    /// <summary>Client identifier that was queried.</summary>
    public string? ClientId { get; set; }

    /// <summary>Sub-tenants the client is currently mirrored into.</summary>
    public List<ClientMirrorDto> Mirrors { get; set; } = [];

    /// <summary>Total number of mirrors.</summary>
    public int TotalCount { get; set; }
}

/// <summary>List of identity providers response.</summary>
public class GetIdentityProvidersResponse : IdentityResponse
{
    /// <summary>Identity providers returned by the identity service.</summary>
    public List<IdentityProviderDto> Providers { get; set; } = [];

    /// <summary>Total number of providers.</summary>
    public int TotalCount { get; set; }
}

/// <summary>Single identity-provider response.</summary>
public class IdentityProviderResponse : IdentityResponse
{
    /// <summary>Provider RtId that was operated on.</summary>
    public string? ProviderId { get; set; }
}

/// <summary>Response for apply_client_overlay (AB#4209 Step 4).</summary>
public class ApplyClientOverlayResponse : IdentityResponse
{
    /// <summary>Client the overlay was applied to.</summary>
    public string? ClientId { get; set; }

    /// <summary>Overlay name that became the <c>Source = "overlay:&lt;name&gt;"</c> suffix.</summary>
    public string? OverlayName { get; set; }

    /// <summary>Per-list added / skipped-duplicate counts from the identity service.</summary>
    public ApplyOverlayUrisResultDto? Result { get; set; }
}

/// <summary>Response for clean_client_overlays (AB#4209 Step 5 / deliverable 7).</summary>
public class CleanClientOverlaysResponse : IdentityResponse
{
    /// <summary>Overlay name filter that was applied, or null when every <c>overlay:*</c> source was targeted.</summary>
    public string? OverlayName { get; set; }

    /// <summary>Per-client breakdown plus the aggregate removal counts from the identity service.</summary>
    public CleanOverlayEntriesResultDto? Result { get; set; }
}

/// <summary>
///     Response for the <c>switch_tenant</c> tool (AB#4338). On success carries the target tenant and
///     the roles resolved in that tenant (read off the exchanged B token's JWT). On failure the
///     <see cref="IdentityResponse.ErrorMessage"/> recommends falling back to the <c>authenticate</c> tool.
/// </summary>
public class SwitchTenantResponse : IdentityResponse
{
    /// <summary>Roles the authenticated user holds in the target tenant, resolved in that tenant.</summary>
    public List<string> Roles { get; set; } = [];
}

/// <summary>One child tenant of a <c>set_client_mirror_roles</c> result (System.Identity 3.9.0).</summary>
public class ClientMirrorRoleTenantRow
{
    /// <summary>The child tenant of the mirror.</summary>
    public string? ChildTenantId { get; set; }

    /// <summary>Applied, Planned, InSync, Skipped or Failed (a failed mirror is retried by the periodic reconciliation).</summary>
    public string? Outcome { get; set; }

    /// <summary>
    ///     Markers of the change: WOULD-ADD / ADDED, WOULD-REMOVE / REMOVED, WOULD-ADOPT / ADOPTED, ALREADY-PRESENT,
    ///     HAND-SET, ROLE-MISSING, DECLINED, ROLE-REFUSED, FAILED, SKIPPED, NO-ROLES.
    /// </summary>
    public List<string> Status { get; set; } = [];

    /// <summary>Direct roles of the mirror before the change.</summary>
    public List<string> RolesBefore { get; set; } = [];

    /// <summary>Direct roles of the mirror after the change (a dry run: after an apply).</summary>
    public List<string> RolesAfter { get; set; } = [];

    /// <summary>Listed roles granted (or to be granted).</summary>
    public List<string> Added { get; set; } = [];

    /// <summary>Managed roles the list no longer names, removed (or to be removed).</summary>
    public List<string> Removed { get; set; } = [];

    /// <summary>Listed roles the mirror holds (adopted ones included).</summary>
    public List<string> AlreadyPresent { get; set; } = [];

    /// <summary>Listed roles the mirror held unmanaged; managed from now on.</summary>
    public List<string> Adopted { get; set; } = [];

    /// <summary>Roles of the mirror the list does not name and identity does not manage; never removed.</summary>
    public List<string> HandSet { get; set; } = [];

    /// <summary>Listed roles the child tenant has no role of.</summary>
    public List<string> RoleMissing { get; set; } = [];

    /// <summary>Listed roles a tenant administrator removed from this mirror; not assigned again.</summary>
    public List<string> Declined { get; set; } = [];

    /// <summary>Listed roles the reconciliation refuses in this child tenant.</summary>
    public List<string> RoleRefused { get; set; } = [];
}

/// <summary>Response of <c>set_client_mirror_roles</c>.</summary>
public class SetClientMirrorRolesResponse : IdentityResponse
{
    /// <summary>The original client.</summary>
    public string? ClientId { get; set; }

    /// <summary>True: nothing was written, the rows are the plan.</summary>
    public bool DryRun { get; set; }

    /// <summary>The complete role list the call set (or would set).</summary>
    public List<string> RoleNames { get; set; } = [];

    /// <summary>One row per tracked mirror (child tenant).</summary>
    public List<ClientMirrorRoleTenantRow> Tenants { get; set; } = [];

    /// <summary>Child tenants whose mirror failed.</summary>
    public List<string> FailedTenants { get; set; } = [];
}

/// <summary>Response of <c>get_client_mirror_roles</c>.</summary>
public class GetClientMirrorRolesResponse : IdentityResponse
{
    /// <summary>The original client.</summary>
    public string? ClientId { get; set; }

    /// <summary>The roles the client hands to its mirrors (<c>Client.MirrorRoleNames</c>), sorted; empty when none.</summary>
    public List<string> MirrorRoleNames { get; set; } = [];
}
