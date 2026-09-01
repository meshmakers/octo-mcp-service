using System.Collections.Concurrent;
using Microsoft.AspNetCore.Http;
using ModelContextProtocol.Server;

namespace Meshmakers.Octo.Backend.McpServices.Services;

/// <summary>
///     Outcome of <see cref="McpSessionContext.ResolveAccessTokenAsync(McpServer, CancellationToken)" />:
///     the bearer to present to the backend services, or an <see cref="Error" /> the tool surfaces
///     verbatim. Both null means "no token, no specific reason" — the caller turns that into
///     <see cref="Constants.NotAuthenticatedError" />.
/// </summary>
/// <param name="AccessToken">Token to use for outbound calls; null when none could be resolved.</param>
/// <param name="Error">Actionable error message, or null.</param>
internal readonly record struct SessionAccessToken(string? AccessToken, string? Error);

/// <summary>
///     Helpers for resolving the MCP session token-store key and the access token stored for it.
///     Centralised so individual tool classes do not duplicate the header lookup + token-store dance.
///     <para>
///     <b>The store is bound to the authenticated caller (AB#5036).</b> Both halves of that binding live
///     here:
///     </para>
///     <list type="number">
///         <item>
///             <b>Structural</b> — the store key is derived from the validated request principal
///             (<see cref="McpCallerPrincipal.StoreKeyPrefix" />); the client-supplied
///             <c>Mcp-Session-Id</c> header may only partition <i>within</i> that namespace. There is no
///             constant fallback slot any more, so two callers can never land on the same entry, and a
///             caller cannot reach a foreign entry by naming someone else's session id.
///         </item>
///         <item>
///             <b>Verification</b> — before a stored token is handed to a backend service its <c>sub</c>
///             and <c>tenant_id</c> must match the request principal's, mirroring what
///             <see cref="RuntimeSecurityContextResolver" /> already did for the cross-tenant path.
///             Client-credentials (service) principals are exempt, see <see cref="VerifyBinding" />.
///         </item>
///     </list>
/// </summary>
internal static class McpSessionContext
{
    // Per-session refresh lock — guards against N concurrent tool calls firing N refresh
    // requests at the identity server when a session token has just expired. Keyed by the
    // caller-bound store key; entry stays alive for the lifetime of the session (cleared when tokens
    // are removed).
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> RefreshLocks = new();

    // Per-(session, tenant) exchange lock — guards against N concurrent tool calls firing N
    // cross-tenant token-exchange requests at the identity server when the B token is absent/expired.
    // Mirrors RefreshLocks. Keyed by (storeKey, tenantId). AB#4338.
    private static readonly ConcurrentDictionary<(string SessionId, string TenantId), SemaphoreSlim>
        ExchangeLocks = new();

    /// <summary>
    ///     Returns the key this request's tokens live under in <see cref="IMcpSessionTokenStore" />, or
    ///     null when no authenticated caller can be identified — in which case the request simply has no
    ///     store token and falls back to its own <c>Authorization: Bearer</c> header, which is exactly the
    ///     right identity.
    ///     <para>
    ///     The key is <c>{caller}</c>, or <c>{caller}|{Mcp-Session-Id}</c> when the client sent that
    ///     header. The header is therefore no longer an identity — it only lets one caller keep several
    ///     concurrent MCP sessions apart. Note that the Streamable HTTP transport defaults to
    ///     <c>HttpServerSessionMode.Stateless</c> and never mints the header itself, so the common case is
    ///     the caller-only key; the device flow (<c>authenticate</c> → <c>check_auth_status</c>) relies on
    ///     it being stable across the two calls.
    ///     </para>
    /// </summary>
    /// <param name="server">MCP server instance — provides DI access to the request context.</param>
    public static string? TryGetSessionKey(McpServer server)
    {
        var callerKey = McpCallerPrincipal.FromServer(server)?.StoreKeyPrefix;
        if (callerKey == null)
        {
            return null;
        }

        var httpContextAccessor = server.Services?.GetService<IHttpContextAccessor>();
        var headerSessionId = httpContextAccessor?.HttpContext?.Request.Headers["Mcp-Session-Id"]
            .FirstOrDefault();

        return string.IsNullOrWhiteSpace(headerSessionId) ? callerKey : $"{callerKey}|{headerSessionId}";
    }

    /// <summary>
    ///     Non-null caller label for the file-transfer store's ownership tag. That tag is informational
    ///     (the transfer endpoints authorise on the opaque 128-bit transfer id, not on the session), so an
    ///     unidentifiable caller gets a constant here — unlike <see cref="TryGetSessionKey" />, where a
    ///     shared value would be a security hole.
    /// </summary>
    /// <param name="server">MCP server instance — provides DI access to the request context.</param>
    public static string GetCallerLabel(McpServer server)
    {
        return TryGetSessionKey(server) ?? "anonymous";
    }

    /// <summary>
    ///     Returns the access token for the current session, or null if the caller is not
    ///     authenticated. Transparently performs an OAuth2 <c>refresh_token</c> grant when the
    ///     stored access token is expired (or within the refresh margin) but a refresh token is
    ///     available — an expiry-driven decision, like octo-cli's <c>AuthenticationService</c>
    ///     (both refresh on the stored expiry timestamp, not on a live <c>/userinfo</c> probe).
    /// </summary>
    /// <remarks>
    ///     Three sources, in this order:
    ///     <list type="number">
    ///         <item>The per-session token store populated by the device-flow <c>authenticate</c>
    ///               tool — the interactive path for human developers running <c>claude</c> locally.
    ///               If the stored token is fresh AND bound to the request principal, it is returned
    ///               directly.</item>
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
    ///     <para>
    ///     Prefer <see cref="ResolveAccessTokenAsync(McpServer, CancellationToken)" /> at call sites that
    ///     can surface an error message — this overload collapses "no token" and "the stored token is not
    ///     yours" into the same null.
    ///     </para>
    /// </remarks>
    public static async ValueTask<string?> TryGetAccessTokenAsync(
        McpServer server,
        CancellationToken cancellationToken = default)
    {
        return (await ResolveAccessTokenAsync(server, cancellationToken)).AccessToken;
    }

    /// <summary>
    ///     Tenant-aware convenience wrapper over
    ///     <see cref="ResolveAccessTokenAsync(McpServer, string?, CancellationToken)" /> that drops the
    ///     error message. See that overload for the semantics.
    /// </summary>
    public static async ValueTask<string?> TryGetAccessTokenAsync(
        McpServer server,
        string? tenantId,
        CancellationToken cancellationToken = default)
    {
        return (await ResolveAccessTokenAsync(server, tenantId, cancellationToken)).AccessToken;
    }

    /// <summary>
    ///     Resolves the bearer for the current request: the caller-bound session-store token when there is
    ///     one, else the request's own <c>Authorization: Bearer</c> header. See
    ///     <see cref="TryGetAccessTokenAsync(McpServer, CancellationToken)" /> for the source order and the
    ///     refresh behaviour.
    ///     <para>
    ///     Returns <see cref="SessionAccessToken.Error" /> — and no token — when a stored token exists but
    ///     does not belong to the authenticated caller. That case is deliberately NOT degraded into the
    ///     header fallback: the caller has a session in the wrong identity and needs to be told, otherwise
    ///     the tool would silently act as somebody (or some tenant) else.
    ///     </para>
    /// </summary>
    /// <param name="server">MCP server instance — provides DI access and the request principal.</param>
    /// <param name="cancellationToken">Cancellation token forwarded to the refresh grant.</param>
    public static async ValueTask<SessionAccessToken> ResolveAccessTokenAsync(
        McpServer server,
        CancellationToken cancellationToken = default)
    {
        var caller = McpCallerPrincipal.FromServer(server);
        var sessionKey = caller?.StoreKeyPrefix == null ? null : TryGetSessionKey(server);

        if (sessionKey != null)
        {
            var tokenStore = server.Services!.GetRequiredService<IMcpSessionTokenStore>();
            var tokens = tokenStore.GetTokens(sessionKey);

            if (tokens != null && !tokens.IsExpired)
            {
                var error = VerifyBinding(caller!, tokens.AccessToken);
                return error != null
                    ? new SessionAccessToken(null, error)
                    : new SessionAccessToken(tokens.AccessToken, null);
            }

            if (tokens?.RefreshToken != null)
            {
                var refreshed = await TryRefreshAsync(server, sessionKey, tokens.RefreshToken, cancellationToken);
                if (refreshed != null)
                {
                    var error = VerifyBinding(caller!, refreshed);
                    return error != null
                        ? new SessionAccessToken(null, error)
                        : new SessionAccessToken(refreshed, null);
                }
            }
        }

        return new SessionAccessToken(TryGetBearerHeader(server), null);
    }

    /// <summary>
    ///     Tenant-aware token resolution (AB#4338). Returns the access token to use for a call scoped to
    ///     <paramref name="tenantId" />:
    ///     <list type="bullet">
    ///         <item>When <paramref name="tenantId" /> is null/empty, equals the home token's own
    ///               <c>tenant_id</c> claim, or the home <c>tenant_id</c> cannot be read (opaque bearer)
    ///               → the home token from
    ///               <see cref="ResolveAccessTokenAsync(McpServer, CancellationToken)" /> (existing path).</item>
    ///         <item>Otherwise → the per-<c>(session, tenant)</c> cached cross-tenant (B) token if present
    ///               and not expired; else a fresh RFC 8693 exchange from the home token via
    ///               <see cref="ITenantTokenExchanger" />, cached and returned (transparent acquisition —
    ///               tools work against tenant B even without an explicit <c>switch_tenant</c>).</item>
    ///     </list>
    ///     Returns no token when the caller is not authenticated (no home token) or when the exchange fails
    ///     (target tenant not accessible, identity unreachable) — the caller surfaces an actionable error.
    ///     A per-<c>(session, tenant)</c> <see cref="SemaphoreSlim" /> serialises concurrent exchanges for
    ///     the same key, re-reading the cache after acquiring the lock (double-checked-locking, same
    ///     pattern as the refresh path).
    ///     <para>
    ///     The per-tenant cache is keyed by the same caller-bound store key as the home token (AB#5036), so
    ///     an exchanged B token is reachable only by the principal that obtained it.
    ///     </para>
    /// </summary>
    /// <param name="server">MCP server instance — provides DI access and the request principal.</param>
    /// <param name="tenantId">Tenant the outbound call is scoped to; null/empty means "home tenant".</param>
    /// <param name="cancellationToken">Cancellation token forwarded to the exchange.</param>
    public static async ValueTask<SessionAccessToken> ResolveAccessTokenAsync(
        McpServer server,
        string? tenantId,
        CancellationToken cancellationToken = default)
    {
        // Home token first — it is the proof-of-identity for any exchange, and the source of the
        // home tenant_id we compare against.
        var home = await ResolveAccessTokenAsync(server, cancellationToken);
        if (home.Error != null || home.AccessToken == null)
        {
            return home;
        }

        if (string.IsNullOrWhiteSpace(tenantId))
        {
            return home;
        }

        // Only exchange when we can positively determine the home token belongs to a DIFFERENT tenant.
        // If the home tenant matches, or cannot be read (opaque / non-JWT bearer, e.g. an
        // adapter-minted token), the home token is the correct, safe default — blindly exchanging on
        // an unreadable tenant would break every existing single-token flow.
        var homeTenantId = JwtClaimReader.TryReadTenantId(home.AccessToken);
        if (homeTenantId == null || string.Equals(homeTenantId, tenantId, StringComparison.OrdinalIgnoreCase))
        {
            return home;
        }

        var sessionKey = TryGetSessionKey(server);
        if (sessionKey == null)
        {
            // No identifiable caller ⇒ nowhere to cache the exchanged token. The home token came from the
            // request's own bearer header, so a one-shot uncached exchange is still correct.
            return new SessionAccessToken(
                await ExchangeAsync(server, tenantId, home.AccessToken, cancellationToken), null);
        }

        var tokenStore = server.Services!.GetRequiredService<IMcpSessionTokenStore>();
        var cached = tokenStore.GetTenantTokens(sessionKey, tenantId);
        if (cached != null && !cached.IsExpired)
        {
            return new SessionAccessToken(cached.AccessToken, null);
        }

        return new SessionAccessToken(
            await TryExchangeAsync(server, sessionKey, tenantId, home.AccessToken, cancellationToken), null);
    }

    /// <summary>
    ///     Verifies that a token taken out of the session store belongs to <paramref name="caller" />.
    ///     Returns null when it does, else the message the tool surfaces.
    ///     <para>
    ///     Same rule as <see cref="RuntimeSecurityContextResolver" />'s cross-tenant binding check: the
    ///     token's <c>sub</c> AND <c>tenant_id</c> must match the validated principal's, and an opaque
    ///     (unparseable) token fails because it cannot be bound at all.
    ///     </para>
    ///     <para>
    ///     <b>Client-credentials principals are exempt.</b> A service token carries no <c>sub</c>, so a
    ///     subject match is not a question that can be asked of it — the AI Adapter worker
    ///     (<c>IMcpTokenIssuer</c>) and the mesh-adapter <c>AnthropicAiQueryNode</c>
    ///     (<c>ServiceAccountConfiguration</c>) present their token in the <c>Authorization</c> header and
    ///     never populate the store, and requiring a subject here would lock both out of every tenant.
    ///     Their store namespace is their <c>client_id</c>, which is as narrow as their identity gets;
    ///     tightening the client-credentials exemption as a whole is tracked as AB#5032.
    ///     </para>
    /// </summary>
    /// <param name="caller">The validated request principal.</param>
    /// <param name="storedAccessToken">The access token found in (or refreshed into) the store.</param>
    private static string? VerifyBinding(McpCallerPrincipal caller, string storedAccessToken)
    {
        if (!caller.IsUserPrincipal)
        {
            return null;
        }

        var tokenSubjectId = JwtClaimReader.TryReadSubjectId(storedAccessToken);
        var tokenTenantId = JwtClaimReader.TryReadTenantId(storedAccessToken);

        if (tokenSubjectId != null
            && tokenTenantId != null
            && string.Equals(tokenSubjectId, caller.UserSubjectId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(tokenTenantId, caller.TenantId, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return Constants.SessionTokenNotBoundError;
    }

    private static async Task<string?> TryExchangeAsync(
        McpServer server,
        string sessionKey,
        string tenantId,
        string homeToken,
        CancellationToken cancellationToken)
    {
        var semaphore = ExchangeLocks.GetOrAdd((sessionKey, tenantId), _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync(cancellationToken);
        try
        {
            var tokenStore = server.Services!.GetRequiredService<IMcpSessionTokenStore>();

            // Re-read after acquiring the lock — a concurrent caller may have already exchanged
            // while we were waiting. Standard double-checked-locking pattern.
            var current = tokenStore.GetTenantTokens(sessionKey, tenantId);
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
                tokenStore.RemoveTenantTokens(sessionKey, tenantId);
                ExchangeLocks.TryRemove((sessionKey, tenantId), out _);
                return null;
            }

            tokenStore.SetTenantTokens(sessionKey, tenantId, exchanged);
            return exchanged.AccessToken;
        }
        finally
        {
            semaphore.Release();
        }
    }

    private static async Task<string?> ExchangeAsync(
        McpServer server,
        string tenantId,
        string homeToken,
        CancellationToken cancellationToken)
    {
        var exchanger = server.Services!.GetRequiredService<ITenantTokenExchanger>();
        var exchanged = await exchanger.ExchangeForTenantAsync(homeToken, tenantId, cancellationToken);
        return exchanged?.AccessToken;
    }

    private static async Task<string?> TryRefreshAsync(
        McpServer server,
        string sessionKey,
        string refreshToken,
        CancellationToken cancellationToken)
    {
        var semaphore = RefreshLocks.GetOrAdd(sessionKey, _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync(cancellationToken);
        try
        {
            var tokenStore = server.Services!.GetRequiredService<IMcpSessionTokenStore>();

            // Re-read after acquiring the lock — a concurrent caller may have already refreshed
            // while we were waiting. Standard double-checked-locking pattern.
            var current = tokenStore.GetTokens(sessionKey);
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
                tokenStore.RemoveTokens(sessionKey);
                RefreshLocks.TryRemove(sessionKey, out _);
                return null;
            }

            tokenStore.SetTokens(sessionKey, fresh);
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
