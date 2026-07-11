using System.Collections.Concurrent;
using Microsoft.AspNetCore.Http;
using ModelContextProtocol.Server;

namespace Meshmakers.Octo.Backend.McpServices.Services;

/// <summary>
///     Helpers for resolving the MCP session id and the access token stored for it.
///     Centralised so individual tool classes do not duplicate the header lookup + token-store dance.
/// </summary>
internal static class McpSessionContext
{
    // Per-session refresh lock — guards against N concurrent tool calls firing N refresh
    // requests at the identity server when a session token has just expired. Keyed by session
    // id; entry stays alive for the lifetime of the session (cleared when tokens are removed).
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> RefreshLocks = new();

    // Per-(session, tenant) exchange lock — guards against N concurrent tool calls firing N
    // cross-tenant token-exchange requests at the identity server when the B token is absent/expired.
    // Mirrors RefreshLocks. Keyed by (sessionId, tenantId). AB#4338.
    private static readonly ConcurrentDictionary<(string SessionId, string TenantId), SemaphoreSlim>
        ExchangeLocks = new();

    /// <summary>
    ///     Returns the MCP session id from the <c>Mcp-Session-Id</c> request header, with a
    ///     deterministic fallback so out-of-band tool calls (tests, headless scripts) still
    ///     land on a stable key.
    /// </summary>
    public static string GetSessionId(McpServer server)
    {
        var httpContextAccessor = server.Services?.GetService<IHttpContextAccessor>();
        var sessionId = httpContextAccessor?.HttpContext?.Request.Headers["Mcp-Session-Id"].FirstOrDefault();
        return sessionId ?? server.ServerOptions?.ServerInfo?.Name ?? "default-session";
    }

    /// <summary>
    ///     Returns the access token for the current session, or null if the caller is not
    ///     authenticated. Transparently performs an OAuth2 <c>refresh_token</c> grant when the
    ///     stored access token is expired but a refresh token is available — the same pattern
    ///     octo-cli's <c>AuthenticationService.EnsureAuthenticatedAsync</c> uses before every
    ///     service call.
    /// </summary>
    /// <remarks>
    ///     Three sources, in this order:
    ///     <list type="number">
    ///         <item>The per-session token store populated by the device-flow <c>authenticate</c>
    ///               tool — the interactive path for human developers running <c>claude</c> locally.
    ///               If the stored token is fresh, it is returned directly.</item>
    ///         <item>If the stored token is expired but carries a refresh token, an OAuth2
    ///               refresh-token grant is attempted via <see cref="ISessionTokenRefresher" />.
    ///               On success the new tokens are written back to the store and the new access
    ///               token is returned. On failure (refresh token revoked, identity unreachable,
    ///               etc.) the stored tokens are removed and the method falls through to step 3.</item>
    ///         <item>The HTTP <c>Authorization: Bearer</c> header. The OIDC middleware has already
    ///               validated this token before the request reached this method, so forwarding it
    ///               to downstream Octo API clients reuses the same authentication that gated the
    ///               MCP request. This is the path the OctoMesh AI worker pod takes — its
    ///               <c>.mcp.json</c> carries an adapter-minted Bearer token in the <c>headers</c>
    ///               block, the worker never calls <c>authenticate</c>, and without this fallback
    ///               every <see cref="AssetClientContext.TryBuildAsync" />-based tool would refuse
    ///               with <see cref="Constants.NotAuthenticatedError"/> even though the caller
    ///               IS authenticated.</item>
    ///     </list>
    ///     A per-session <see cref="SemaphoreSlim" /> serialises refresh attempts so concurrent
    ///     tool calls don't burn N refresh tokens at the identity server. After acquiring the
    ///     lock the method re-reads from the store before refreshing, in case another caller
    ///     already did it.
    /// </remarks>
    public static async ValueTask<string?> TryGetAccessTokenAsync(
        McpServer server,
        CancellationToken cancellationToken = default)
    {
        var sessionId = GetSessionId(server);
        var tokenStore = server.Services!.GetRequiredService<IMcpSessionTokenStore>();

        var tokens = tokenStore.GetTokens(sessionId);
        if (tokens != null && !tokens.IsExpired)
        {
            return tokens.AccessToken;
        }

        if (tokens?.RefreshToken != null)
        {
            var refreshed = await TryRefreshAsync(server, sessionId, tokens.RefreshToken, cancellationToken);
            if (refreshed != null)
            {
                return refreshed;
            }
        }

        return TryGetBearerHeader(server);
    }

    /// <summary>
    ///     Tenant-aware token resolution (AB#4338). Returns the access token to use for a call scoped to
    ///     <paramref name="tenantId" />:
    ///     <list type="bullet">
    ///         <item>When <paramref name="tenantId" /> is null/empty, equals the home token's own
    ///               <c>tenant_id</c> claim, or the home <c>tenant_id</c> cannot be read (opaque bearer)
    ///               → the home token from
    ///               <see cref="TryGetAccessTokenAsync(McpServer, CancellationToken)" /> (existing path).</item>
    ///         <item>Otherwise → the per-<c>(session, tenant)</c> cached cross-tenant (B) token if present
    ///               and not expired; else a fresh RFC 8693 exchange from the home token via
    ///               <see cref="ITenantTokenExchanger" />, cached and returned (transparent acquisition —
    ///               tools work against tenant B even without an explicit <c>switch_tenant</c>).</item>
    ///     </list>
    ///     Returns null when the caller is not authenticated (no home token) or when the exchange fails
    ///     (target tenant not accessible, identity unreachable) — the caller surfaces an actionable error.
    ///     A per-<c>(session, tenant)</c> <see cref="SemaphoreSlim" /> serialises concurrent exchanges for
    ///     the same key, re-reading the cache after acquiring the lock (double-checked-locking, same
    ///     pattern as the refresh path).
    /// </summary>
    public static async ValueTask<string?> TryGetAccessTokenAsync(
        McpServer server,
        string? tenantId,
        CancellationToken cancellationToken = default)
    {
        // Home token first — it is the proof-of-identity for any exchange, and the source of the
        // home tenant_id we compare against.
        var homeToken = await TryGetAccessTokenAsync(server, cancellationToken);
        if (homeToken == null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(tenantId))
        {
            return homeToken;
        }

        // Only exchange when we can positively determine the home token belongs to a DIFFERENT tenant.
        // If the home tenant matches, or cannot be read (opaque / non-JWT bearer, e.g. an
        // adapter-minted token), the home token is the correct, safe default — blindly exchanging on
        // an unreadable tenant would break every existing single-token flow.
        var homeTenantId = JwtClaimReader.TryReadTenantId(homeToken);
        if (homeTenantId == null || string.Equals(homeTenantId, tenantId, StringComparison.OrdinalIgnoreCase))
        {
            return homeToken;
        }

        var sessionId = GetSessionId(server);
        var tokenStore = server.Services!.GetRequiredService<IMcpSessionTokenStore>();

        var cached = tokenStore.GetTenantTokens(sessionId, tenantId);
        if (cached != null && !cached.IsExpired)
        {
            return cached.AccessToken;
        }

        return await TryExchangeAsync(server, sessionId, tenantId, homeToken, cancellationToken);
    }

    private static async Task<string?> TryExchangeAsync(
        McpServer server,
        string sessionId,
        string tenantId,
        string homeToken,
        CancellationToken cancellationToken)
    {
        var semaphore = ExchangeLocks.GetOrAdd((sessionId, tenantId), _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync(cancellationToken);
        try
        {
            var tokenStore = server.Services!.GetRequiredService<IMcpSessionTokenStore>();

            // Re-read after acquiring the lock — a concurrent caller may have already exchanged
            // while we were waiting. Standard double-checked-locking pattern.
            var current = tokenStore.GetTenantTokens(sessionId, tenantId);
            if (current != null && !current.IsExpired)
            {
                return current.AccessToken;
            }

            var exchanger = server.Services!.GetRequiredService<ITenantTokenExchanger>();
            var exchanged = await exchanger.ExchangeForTenantAsync(homeToken, tenantId, cancellationToken);
            if (exchanged == null)
            {
                // Exchange failed (e.g. user may not access the target tenant). Drop any stale entry
                // and let the caller surface an actionable error.
                tokenStore.RemoveTenantTokens(sessionId, tenantId);
                ExchangeLocks.TryRemove((sessionId, tenantId), out _);
                return null;
            }

            tokenStore.SetTenantTokens(sessionId, tenantId, exchanged);
            return exchanged.AccessToken;
        }
        finally
        {
            semaphore.Release();
        }
    }

    private static async Task<string?> TryRefreshAsync(
        McpServer server,
        string sessionId,
        string refreshToken,
        CancellationToken cancellationToken)
    {
        var semaphore = RefreshLocks.GetOrAdd(sessionId, _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync(cancellationToken);
        try
        {
            var tokenStore = server.Services!.GetRequiredService<IMcpSessionTokenStore>();

            // Re-read after acquiring the lock — a concurrent caller may have already refreshed
            // while we were waiting. Standard double-checked-locking pattern.
            var current = tokenStore.GetTokens(sessionId);
            if (current != null && !current.IsExpired)
            {
                return current.AccessToken;
            }

            // If another caller refreshed and got a rotated refresh token, use the latest
            // one rather than the one passed in.
            var effectiveRefreshToken = current?.RefreshToken ?? refreshToken;

            var refresher = server.Services!.GetRequiredService<ISessionTokenRefresher>();
            var fresh = await refresher.RefreshAsync(effectiveRefreshToken, cancellationToken);
            if (fresh == null)
            {
                // Refresh failed — drop the stale tokens so subsequent calls fall through to
                // the Authorization-header path (or surface "Not authenticated"). Also remove
                // the lock so the leak is bounded.
                tokenStore.RemoveTokens(sessionId);
                RefreshLocks.TryRemove(sessionId, out _);
                return null;
            }

            tokenStore.SetTokens(sessionId, fresh);
            return fresh.AccessToken;
        }
        finally
        {
            semaphore.Release();
        }
    }

    private static string? TryGetBearerHeader(McpServer server)
    {
        var httpContextAccessor = server.Services!.GetService<IHttpContextAccessor>();
        var authHeader = httpContextAccessor?.HttpContext?.Request.Headers.Authorization.FirstOrDefault();
        if (!string.IsNullOrEmpty(authHeader)
            && authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            var bearer = authHeader["Bearer ".Length..].Trim();
            if (!string.IsNullOrEmpty(bearer))
            {
                return bearer;
            }
        }

        return null;
    }
}
