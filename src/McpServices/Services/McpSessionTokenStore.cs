using System.Collections.Concurrent;

namespace Meshmakers.Octo.Backend.McpServices.Services;

/// <summary>
///     Represents the OAuth2 tokens for an authenticated MCP session.
/// </summary>
public record McpSessionTokens
{
    /// <summary>
    ///     The access token issued by the identity server.
    /// </summary>
    public required string AccessToken { get; init; }

    /// <summary>
    ///     The refresh token for obtaining new access tokens.
    /// </summary>
    public string? RefreshToken { get; init; }

    /// <summary>
    ///     The UTC time when the access token expires.
    /// </summary>
    public required DateTime ExpiresAtUtc { get; init; }

    /// <summary>
    ///     The device code used during the device authorization flow (before authentication completes).
    /// </summary>
    public string? DeviceCode { get; init; }

    /// <summary>
    ///     The interval in seconds to wait between polling attempts during device authorization flow.
    /// </summary>
    public int? PollIntervalSeconds { get; init; }

    /// <summary>
    ///     Whether the access token has expired.
    /// </summary>
    public bool IsExpired => DateTime.UtcNow >= ExpiresAtUtc;
}

/// <summary>
///     Represents a pending device authorization request.
/// </summary>
public record DeviceAuthorizationState
{
    /// <summary>
    ///     The device code for token polling.
    /// </summary>
    public required string DeviceCode { get; init; }

    /// <summary>
    ///     The user code to display.
    /// </summary>
    public required string UserCode { get; init; }

    /// <summary>
    ///     The verification URI where the user should go.
    /// </summary>
    public required string VerificationUri { get; init; }

    /// <summary>
    ///     The complete verification URI with user code embedded.
    /// </summary>
    public string? VerificationUriComplete { get; init; }

    /// <summary>
    ///     The UTC time when this authorization request expires.
    /// </summary>
    public required DateTime ExpiresAtUtc { get; init; }

    /// <summary>
    ///     The interval in seconds between polling attempts.
    /// </summary>
    public required int IntervalSeconds { get; init; }
}

/// <summary>
///     In-memory token store for MCP sessions. Stores OAuth2 tokens and pending device authorizations
///     keyed by the MCP session ID.
/// </summary>
public interface IMcpSessionTokenStore
{
    /// <summary>
    ///     Stores tokens for the given session.
    /// </summary>
    void SetTokens(string sessionId, McpSessionTokens tokens);

    /// <summary>
    ///     Gets the tokens for the given session, or null if not found.
    /// </summary>
    McpSessionTokens? GetTokens(string sessionId);

    /// <summary>
    ///     Removes the tokens for the given session.
    /// </summary>
    void RemoveTokens(string sessionId);

    /// <summary>
    ///     Stores the cross-tenant (B) access token exchanged for the given session + target tenant
    ///     (AB#4338). The single-entry <see cref="SetTokens" /> slot stays as the home / root (A) token;
    ///     this cache is keyed by <c>(sessionId, tenantId)</c> so a session can hold a distinct token per
    ///     tenant it has switched into.
    /// </summary>
    void SetTenantTokens(string sessionId, string tenantId, McpSessionTokens tokens);

    /// <summary>
    ///     Gets the cross-tenant (B) access token cached for the given session + target tenant, or null
    ///     if none has been exchanged yet.
    /// </summary>
    McpSessionTokens? GetTenantTokens(string sessionId, string tenantId);

    /// <summary>
    ///     Removes the cross-tenant (B) access token cached for the given session + target tenant.
    /// </summary>
    void RemoveTenantTokens(string sessionId, string tenantId);

    /// <summary>
    ///     Stores a pending device authorization for the given session.
    /// </summary>
    void SetDeviceAuthorization(string sessionId, DeviceAuthorizationState state);

    /// <summary>
    ///     Gets the pending device authorization for the given session, or null if not found.
    /// </summary>
    DeviceAuthorizationState? GetDeviceAuthorization(string sessionId);

    /// <summary>
    ///     Removes the pending device authorization for the given session.
    /// </summary>
    void RemoveDeviceAuthorization(string sessionId);
}

/// <summary>
///     Thread-safe in-memory implementation of <see cref="IMcpSessionTokenStore" />.
/// </summary>
internal class McpSessionTokenStore : IMcpSessionTokenStore
{
    private readonly ConcurrentDictionary<string, DeviceAuthorizationState> _deviceAuthorizations = new();
    private readonly ConcurrentDictionary<string, McpSessionTokens> _tokens = new();

    // Per-tenant (cross-tenant exchange) token cache keyed by (sessionId, tenantId). AB#4338.
    private readonly ConcurrentDictionary<(string SessionId, string TenantId), McpSessionTokens> _tenantTokens =
        new();

    public void SetTokens(string sessionId, McpSessionTokens tokens)
    {
        _tokens[sessionId] = tokens;
    }

    public McpSessionTokens? GetTokens(string sessionId)
    {
        return _tokens.GetValueOrDefault(sessionId);
    }

    public void RemoveTokens(string sessionId)
    {
        _tokens.TryRemove(sessionId, out _);
    }

    public void SetTenantTokens(string sessionId, string tenantId, McpSessionTokens tokens)
    {
        _tenantTokens[(sessionId, tenantId)] = tokens;
    }

    public McpSessionTokens? GetTenantTokens(string sessionId, string tenantId)
    {
        return _tenantTokens.GetValueOrDefault((sessionId, tenantId));
    }

    public void RemoveTenantTokens(string sessionId, string tenantId)
    {
        _tenantTokens.TryRemove((sessionId, tenantId), out _);
    }

    public void SetDeviceAuthorization(string sessionId, DeviceAuthorizationState state)
    {
        _deviceAuthorizations[sessionId] = state;
    }

    public DeviceAuthorizationState? GetDeviceAuthorization(string sessionId)
    {
        return _deviceAuthorizations.GetValueOrDefault(sessionId);
    }

    public void RemoveDeviceAuthorization(string sessionId)
    {
        _deviceAuthorizations.TryRemove(sessionId, out _);
    }
}
