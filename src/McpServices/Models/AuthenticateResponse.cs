namespace Meshmakers.Octo.Backend.McpServices.Models;

/// <summary>
///     Response from the authenticate tool.
/// </summary>
public class AuthenticateResponse
{
    /// <summary>Whether the operation succeeded.</summary>
    public bool IsSuccess { get; set; }

    /// <summary>Whether the user was already authenticated.</summary>
    public bool IsAlreadyAuthenticated { get; set; }

    /// <summary>The user code to display and enter in the browser.</summary>
    public string? UserCode { get; set; }

    /// <summary>The verification URI to open in a browser.</summary>
    public string? VerificationUri { get; set; }

    /// <summary>The complete verification URI with user code embedded.</summary>
    public string? VerificationUriComplete { get; set; }

    /// <summary>Seconds until the authorization request expires.</summary>
    public int? ExpiresInSeconds { get; set; }

    /// <summary>Human-readable message.</summary>
    public string? Message { get; set; }

    /// <summary>Error message if the operation failed.</summary>
    public string? ErrorMessage { get; set; }
}

/// <summary>
///     Response from the check_auth_status tool.
/// </summary>
public class CheckAuthStatusResponse
{
    /// <summary>Whether the operation succeeded.</summary>
    public bool IsSuccess { get; set; }

    /// <summary>Whether the user is now authenticated.</summary>
    public bool IsAuthenticated { get; set; }

    /// <summary>Whether authentication is still pending (user hasn't completed browser flow yet).</summary>
    public bool IsPending { get; set; }

    /// <summary>Seconds to wait before retrying (when pending).</summary>
    public int? RetryAfterSeconds { get; set; }

    /// <summary>Human-readable message.</summary>
    public string? Message { get; set; }

    /// <summary>Error message if the operation failed.</summary>
    public string? ErrorMessage { get; set; }
}
