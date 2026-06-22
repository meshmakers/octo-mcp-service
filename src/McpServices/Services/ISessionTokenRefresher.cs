namespace Meshmakers.Octo.Backend.McpServices.Services;

/// <summary>
///     Performs OAuth2 <c>refresh_token</c> grants against the identity service. Extracted from
///     <see cref="McpSessionContext" /> so the refresh path is mockable in unit tests.
/// </summary>
public interface ISessionTokenRefresher
{
    /// <summary>
    ///     Exchanges a refresh token for a fresh <see cref="McpSessionTokens" />. Returns
    ///     <c>null</c> when the refresh fails (refresh token revoked / expired, identity service
    ///     unreachable, malformed response) — callers should treat that as "session no longer
    ///     authenticated" and drop the stored tokens.
    /// </summary>
    Task<McpSessionTokens?> RefreshAsync(string refreshToken, CancellationToken cancellationToken = default);
}
