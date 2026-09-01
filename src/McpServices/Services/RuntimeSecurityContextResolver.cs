using System.Security.Claims;
using Meshmakers.Octo.Runtime.Contracts;
using Microsoft.AspNetCore.Http;
using ModelContextProtocol.Server;

namespace Meshmakers.Octo.Backend.McpServices.Services;

/// <summary>
///     Outcome of <see cref="RuntimeSecurityContextResolver.ResolveAsync" />: either a caller security
///     context plus the tenant it is valid for, or an <see cref="Error" /> the tool surfaces verbatim in
///     its response envelope. Exactly one of <see cref="SecurityContext" /> / <see cref="Error" /> is set.
/// </summary>
/// <param name="SecurityContext">Identity the runtime session must act for; null when <paramref name="Error" /> is set.</param>
/// <param name="ResolvedTenantId">The tenant the call was resolved to; null when tenant resolution itself failed.</param>
/// <param name="Error">Actionable error message, or null on success.</param>
internal sealed record RuntimeSecurityContextResult(
    RtSecurityContext? SecurityContext,
    string? ResolvedTenantId,
    string? Error);

/// <summary>
///     Resolves the <see cref="RtSecurityContext" /> the direct-engine tools (families 2 and 3) must open
///     their runtime sessions with, and gates the tool's <c>tenantId</c> parameter against the caller's
///     validated request principal (AB#5030).
///     <para>
///     Before AB#5030 these tools opened parameterless system sessions
///     (<c>tenantRepository.GetSessionAsync()</c>), which bypassed data-permission enforcement (AB#4969)
///     entirely and let any authenticated caller read or write any tenant named in the tool parameter.
///     </para>
///     <para>
///     <b>Identity source.</b> The caller identity comes from <c>HttpContext.User</c> — the principal the
///     JWT bearer handler produced after it verified the token's signature, issuer and lifetime. It does
///     NOT come from the per-session token store: that store is keyed by the client-supplied
///     <c>Mcp-Session-Id</c> header (with a shared <c>"default-session"</c> fallback), so a caller could
///     otherwise inherit another caller's identity simply by naming their session id. The store is
///     consulted only on the cross-tenant path, and there the token it hands out is verified to belong to
///     the request principal before it is used.
///     </para>
///     <para>
///     The claim reading mirrors <c>AssetRepositoryServices/GraphQL/Helpers.GetSecurityContext</c>
///     one-for-one, so the same user sees the same data through MCP and through GraphQL.
///     </para>
///     <para>
///     The resolver is fail-closed and never throws — every failure comes back as
///     <see cref="RuntimeSecurityContextResult.Error" /> so the tool contract ("never throw out of a
///     tool") is preserved.
///     </para>
/// </summary>
internal static class RuntimeSecurityContextResolver
{
    /// <summary>
    ///     Resolves the target tenant, verifies the caller may act on it, and builds the caller's
    ///     <see cref="RtSecurityContext" />.
    /// </summary>
    /// <param name="server">MCP server instance — provides DI access and the request principal.</param>
    /// <param name="tenantResolution">Tenant resolution service (tool parameter / route parameter).</param>
    /// <param name="toolTenantId">The tool's <c>tenantId</c> parameter, may be null.</param>
    /// <param name="cancellationToken">Cancellation token forwarded to the cross-tenant token exchange.</param>
    public static async ValueTask<RuntimeSecurityContextResult> ResolveAsync(
        McpServer server,
        ITenantResolutionService tenantResolution,
        string? toolTenantId,
        CancellationToken cancellationToken = default)
    {
        string resolvedTenantId;
        try
        {
            resolvedTenantId = tenantResolution.ResolveTenantId(toolTenantId);
        }
        catch (Exception ex)
        {
            // No tenant on the tool parameter and none on the route — surface the resolver's own
            // message, it already names both affordances. Catching Exception (not just
            // InvalidOperationException) keeps the "never throws" contract independent of what a
            // future ITenantResolutionService implementation decides to throw.
            return new RuntimeSecurityContextResult(null, null, ex.Message);
        }

        // ── Validated request principal ────────────────────────────────────────────────────────
        // IHttpContextAccessor is registered by AddOctoServiceInfrastructure. GetService (not
        // GetRequiredService) so a host that never wired it degrades into a denial rather than an
        // exception thrown out of a tool.
        var httpContextAccessor = server.Services?.GetService<IHttpContextAccessor>();
        var user = httpContextAccessor?.HttpContext?.User;
        if (user?.Identity?.IsAuthenticated != true)
        {
            return new RuntimeSecurityContextResult(null, resolvedTenantId, Constants.NotAuthenticatedError);
        }

        // ConfigureJwtBearerOptions leaves MapInboundClaims at its ASP.NET default of true, so the
        // JWT's `sub` arrives on the principal as ClaimTypes.NameIdentifier. Both names must be
        // probed or nothing matches. `client_id` is the client-credentials fallback.
        var userSubjectId = user.FindFirst("sub")?.Value
                            ?? user.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        var clientId = user.FindFirst("client_id")?.Value;
        var subjectId = userSubjectId ?? clientId;

        // A client-credentials (service) token carries no subject at all — only a client id. That is
        // the marker the tenant-gate exemption below keys off.
        var isUserPrincipal = userSubjectId != null;

        // Role claims may arrive under the identity's RoleClaimType (inbound mapping on) or under the
        // JWT short name "role" (mapping off) — read both, like the subject above.
        var roles = user.Identities
            .SelectMany(i => i.FindAll(i.RoleClaimType).Concat(i.FindAll("role")))
            .Select(c => c.Value)
            .Distinct()
            .ToArray();

        // `tenant_id` is not part of the inbound claim map, so it keeps its JWT name on the principal.
        var principalTenantId = user.FindFirst("tenant_id")?.Value;

        if (!isUserPrincipal)
        {
            // Client-credentials SERVICE tokens are deliberately exempt from the tenant match, mirroring
            // the backend TenantAuthorizationMiddleware which skips the same class of token. That is how
            // the AI Adapter worker (token via IMcpTokenIssuer) and the mesh-adapter AnthropicAiQueryNode
            // (token via ServiceAccountConfiguration) reach every tenant they serve.
            //
            // Blast radius: because ConfigureJwtBearerOptions sets ValidateAudience = false, ANY
            // client-credentials client of this authority passes the transport gate and is then exempt
            // here — not just the two components above. Tightening this is tracked as AB#5032; until it
            // lands the exemption must stay or the AI worker loses access to every tenant.
            return new RuntimeSecurityContextResult(
                RtSecurityContext.ForUser(subjectId, roles), resolvedTenantId, null);
        }

        // ── Tenant gate for user principals ───────────────────────────────────────────────────
        if (string.IsNullOrWhiteSpace(principalTenantId))
        {
            // TenantAuthorizationMiddleware answers 403 for a user token without a tenant claim; the
            // per-tool tenant parameter must not be more permissive than the route gate.
            return new RuntimeSecurityContextResult(null, resolvedTenantId,
                $"Access to tenant '{resolvedTenantId}' denied: the caller's token carries no 'tenant_id' claim.");
        }

        if (string.Equals(principalTenantId, resolvedTenantId, StringComparison.OrdinalIgnoreCase))
        {
            // Normal case — the caller acts on their own tenant. The identity is taken straight from the
            // validated principal: no session-store lookup, no unvalidated JWT parsing.
            //
            // NEVER RtSecurityContext.System here — the whole point of AB#5030 is that these tools act
            // as the caller.
            return new RuntimeSecurityContextResult(
                RtSecurityContext.ForUser(subjectId, roles), resolvedTenantId, null);
        }

        return await ResolveCrossTenantAsync(
            server, resolvedTenantId, userSubjectId!, principalTenantId, cancellationToken);
    }

    /// <summary>
    ///     Cross-tenant path (AB#4338): a user homed in tenant A legitimately reaching tenant B through
    ///     the RFC 8693 token exchange. The identity in B is the B-shadow user — a different subject id
    ///     with roles resolved in B — so the A principal must NOT be reused as the security context.
    /// </summary>
    private static async ValueTask<RuntimeSecurityContextResult> ResolveCrossTenantAsync(
        McpServer server,
        string resolvedTenantId,
        string principalSubjectId,
        string principalTenantId,
        CancellationToken cancellationToken)
    {
        // The exchange needs the caller's home token as the subject_token; only the session store has it.
        string? homeToken;
        try
        {
            homeToken = await McpSessionContext.TryGetAccessTokenAsync(server, cancellationToken);
        }
        catch (Exception ex)
        {
            // Fail closed: an unreachable identity service or a misconfigured host must never degrade
            // into a system session, and must never throw out of a tool.
            return new RuntimeSecurityContextResult(null, resolvedTenantId,
                $"Could not resolve the caller's session token: {ex.Message}");
        }

        if (homeToken == null)
        {
            return new RuntimeSecurityContextResult(null, resolvedTenantId, Constants.NotAuthenticatedError);
        }

        // Bind the store entry to THIS request. The store key derives from the client-supplied
        // Mcp-Session-Id header and falls back to a shared slot, so without this check a caller could
        // borrow whatever token another caller happened to leave in that slot.
        var homeSubjectId = JwtClaimReader.TryReadSubjectId(homeToken);
        var homeTenantId = JwtClaimReader.TryReadTenantId(homeToken);
        if (homeSubjectId == null
            || homeTenantId == null
            || !string.Equals(homeSubjectId, principalSubjectId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(homeTenantId, principalTenantId, StringComparison.OrdinalIgnoreCase))
        {
            return new RuntimeSecurityContextResult(null, resolvedTenantId,
                $"Access to tenant '{resolvedTenantId}' denied: the stored session token does not belong to the "
                + "authenticated caller. Re-run the 'authenticate' tool for this MCP session before making "
                + "cross-tenant calls.");
        }

        string? exchangedToken;
        try
        {
            exchangedToken = await McpSessionContext.TryGetAccessTokenAsync(
                server, resolvedTenantId, cancellationToken);
        }
        catch (Exception ex)
        {
            return new RuntimeSecurityContextResult(null, resolvedTenantId,
                $"Cross-tenant token exchange for tenant '{resolvedTenantId}' failed: {ex.Message}");
        }

        if (exchangedToken == null)
        {
            // Deliberately NOT Constants.NotAuthenticatedError: the caller IS authenticated, they just
            // may not act on this tenant. Reporting "not authenticated" would send AI clients into a
            // pointless device-flow re-login.
            return new RuntimeSecurityContextResult(null, resolvedTenantId,
                $"Access to tenant '{resolvedTenantId}' denied: the cross-tenant token exchange failed. The "
                + $"caller is homed in tenant '{principalTenantId}' and has no permission for "
                + $"'{resolvedTenantId}', or the identity service rejected the exchange.");
        }

        // Reading claims out of the exchanged token without signature validation is sound here: it was
        // just returned to us by the identity server over TLS as the response to our own token-exchange
        // request, so it never passed through the caller.
        var exchangedTenantId = JwtClaimReader.TryReadTenantId(exchangedToken);
        if (exchangedTenantId == null
            || !string.Equals(exchangedTenantId, resolvedTenantId, StringComparison.OrdinalIgnoreCase))
        {
            return new RuntimeSecurityContextResult(null, resolvedTenantId,
                $"Access to tenant '{resolvedTenantId}' denied: the session token is issued for tenant "
                + $"'{exchangedTenantId ?? "<unreadable>"}'.");
        }

        var shadowSubjectId = JwtClaimReader.TryReadSubjectId(exchangedToken)
                              ?? JwtClaimReader.TryReadClientId(exchangedToken);
        var shadowRoles = JwtClaimReader.ReadRoles(exchangedToken);

        return new RuntimeSecurityContextResult(
            RtSecurityContext.ForUser(shadowSubjectId, shadowRoles), resolvedTenantId, null);
    }

    /// <summary>
    ///     Tenant gate for call sites that never open a runtime session (schema discovery, stream-data
    ///     metadata, schema resources, <c>Echo</c>). Callers only look at
    ///     <see cref="RuntimeSecurityContextResult.Error" />.
    ///     <para>
    ///     This deliberately delegates to <see cref="ResolveAsync" /> instead of reimplementing a
    ///     "cheaper" gate-only variant: the deny decisions and the context construction are the same
    ///     computation, and the only work the gate-only caller does not need is a single
    ///     <see cref="RtSecurityContext.ForUser" /> allocation. A second code path would be free to drift
    ///     away from the one that actually guards the data.
    ///     </para>
    /// </summary>
    /// <param name="server">MCP server instance — provides DI access and the request principal.</param>
    /// <param name="tenantResolution">Tenant resolution service (tool parameter / route parameter).</param>
    /// <param name="toolTenantId">The tool's <c>tenantId</c> parameter, may be null.</param>
    /// <param name="cancellationToken">Cancellation token forwarded to the cross-tenant token exchange.</param>
    public static ValueTask<RuntimeSecurityContextResult> ResolveTenantAccessAsync(
        McpServer server,
        ITenantResolutionService tenantResolution,
        string? toolTenantId,
        CancellationToken cancellationToken = default)
    {
        return ResolveAsync(server, tenantResolution, toolTenantId, cancellationToken);
    }
}
