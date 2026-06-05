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
