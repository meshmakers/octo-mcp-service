namespace Meshmakers.Octo.Backend.McpServices.Models;

/// <summary>
///     Response from the whoami tool.
/// </summary>
public class WhoAmIResponse
{
    /// <summary>Whether the operation succeeded.</summary>
    public bool IsSuccess { get; set; }

    /// <summary>Whether the user is authenticated.</summary>
    public bool IsAuthenticated { get; set; }

    /// <summary>The user's subject identifier.</summary>
    public string? UserId { get; set; }

    /// <summary>The user's display name or preferred username.</summary>
    public string? UserName { get; set; }

    /// <summary>The user's email address.</summary>
    public string? Email { get; set; }

    /// <summary>The tenant ID from the JWT token.</summary>
    public string? TenantId { get; set; }

    /// <summary>List of tenant IDs the user has access to.</summary>
    public List<string> AllowedTenants { get; set; } = [];

    /// <summary>List of roles assigned to the user.</summary>
    public List<string> Roles { get; set; } = [];

    /// <summary>List of scopes in the access token.</summary>
    public List<string> Scopes { get; set; } = [];

    /// <summary>UTC time when the access token expires.</summary>
    public DateTime? TokenExpiresAtUtc { get; set; }

    /// <summary>Error message if the operation failed.</summary>
    public string? ErrorMessage { get; set; }
}

/// <summary>
///     Response from the list_tenants tool.
/// </summary>
public class ListTenantsResponse
{
    /// <summary>Whether the operation succeeded.</summary>
    public bool IsSuccess { get; set; }

    /// <summary>The current tenant ID from the JWT token.</summary>
    public string? CurrentTenantId { get; set; }

    /// <summary>List of tenant IDs the user has access to.</summary>
    public List<string> AllowedTenants { get; set; } = [];

    /// <summary>Total number of allowed tenants.</summary>
    public int TotalCount { get; set; }

    /// <summary>Human-readable message.</summary>
    public string? Message { get; set; }

    /// <summary>Error message if the operation failed.</summary>
    public string? ErrorMessage { get; set; }
}
