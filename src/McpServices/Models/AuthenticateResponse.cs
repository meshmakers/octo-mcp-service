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

/// <summary>
///     Response from the auth_status tool — current authentication status of the MCP session,
///     plus identity claims extracted from the JWT when the token is available. Equivalent to
///     octo-cli's <c>AuthStatus</c> command. The act of querying triggers a lazy refresh, so
///     a successful response also confirms that the refresh-token chain is still valid.
/// </summary>
public class AuthStatusResponse
{
    /// <summary>Whether the tool ran without throwing. Independent of authentication state.</summary>
    public bool IsSuccess { get; set; }

    /// <summary>Whether the session currently has a usable access token (after a possible refresh).</summary>
    public bool IsAuthenticated { get; set; }

    /// <summary>
    ///     True when the stored access token was expired and a successful refresh-token grant
    ///     produced a new one in the course of answering this call.
    /// </summary>
    public bool WasRefreshed { get; set; }

    /// <summary>
    ///     UTC expiry of the currently-stored access token, when one is known. Null when the
    ///     token comes from the HTTP <c>Authorization</c> header (worker-pod path) — that token's
    ///     expiry is owned by the upstream OIDC middleware, not the session store.
    /// </summary>
    public DateTime? ExpiresAtUtc { get; set; }

    /// <summary>JWT <c>sub</c> claim — the user's stable identity id. Null for opaque tokens.</summary>
    public string? SubjectId { get; set; }

    /// <summary>JWT <c>name</c> claim — friendly display name. Null when not present.</summary>
    public string? UserName { get; set; }

    /// <summary>
    ///     JWT <c>tenant</c> / <c>tenant_id</c> claim — the tenant the token is scoped to. Null
    ///     when the issuer didn't emit one (cross-tenant adapter-minted tokens).
    /// </summary>
    public string? TenantId { get; set; }

    /// <summary>Human-readable message.</summary>
    public string? Message { get; set; }

    /// <summary>Error message if the operation failed.</summary>
    public string? ErrorMessage { get; set; }
}
