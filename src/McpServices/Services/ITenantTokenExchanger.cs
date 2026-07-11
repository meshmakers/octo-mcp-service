namespace Meshmakers.Octo.Backend.McpServices.Services;

/// <summary>
///     Performs RFC 8693 cross-tenant token exchange against the identity service (AB#4338). Given the
///     user's home-tenant (A) access token, obtains a target-tenant (B) access token with B-resolved
///     roles — no browser / credential prompt. Extracted from <see cref="McpSessionContext" /> so the
///     exchange path is mockable in unit tests, mirroring <see cref="ISessionTokenRefresher" />.
/// </summary>
public interface ITenantTokenExchanger
{
    /// <summary>
    ///     Exchanges the caller's home-tenant access token for a target-tenant access token. Returns
    ///     <c>null</c> when the exchange fails (target tenant not accessible, subject token invalid /
    ///     expired, identity service unreachable, malformed response) — callers should surface an
    ///     actionable error (e.g. recommend the <c>authenticate</c> tool for the target tenant).
    /// </summary>
    /// <param name="homeAccessToken">The user's current home-tenant (A) access token — proof of identity.</param>
    /// <param name="targetTenantId">The target tenant (B) to obtain a token for.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<McpSessionTokens?> ExchangeForTenantAsync(
        string homeAccessToken,
        string targetTenantId,
        CancellationToken cancellationToken = default);
}
