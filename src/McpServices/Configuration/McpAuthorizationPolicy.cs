using System.Security.Claims;
using Meshmakers.Octo.Backend.McpServices.Options;
using Meshmakers.Octo.Services.Infrastructure;
using Microsoft.AspNetCore.Authorization;

namespace Meshmakers.Octo.Backend.McpServices.Configuration;

/// <summary>
///     Scope policy for the MCP transport endpoints (AB#5032).
///     <para>
///     Before this, both <c>MapMcp</c> endpoints carried a bare <c>RequireAuthorization()</c>: <b>any</b>
///     token the authority issued got in, including one carrying no Octo API scope at all (a pure
///     <c>openid profile</c> front-end token, for instance). The backend services all gate on the
///     <c>scope</c> claim — read endpoints accept <c>octo_api</c> or <c>octo_api.read_only</c>, write
///     endpoints accept <c>octo_api</c> — and the MCP surface must not be the one door that does not.
///     </para>
///     <para>
///     <b>Why one uniform requirement and not a read/write split.</b> MCP multiplexes every tool over a
///     single JSON-RPC <c>POST</c> to <c>/mcp</c> (or <c>/{tenantId}/mcp</c>); which tool is being called
///     lives in the request <i>body</i>, and ASP.NET authorization runs on the endpoint before the body
///     is read. There is no second endpoint to attach a stricter policy to, and
///     <c>WithHttpTransport()</c>'s session mode is explicitly out of scope here. So the endpoint can
///     only ask one question, and the honest answer for a surface that contains
///     <c>delete_tenant</c> and <c>uninstall_blueprint</c> next to <c>get_entity_by_id</c> is the
///     <b>write</b> requirement: <c>octo_api</c>. Accepting <c>octo_api.read_only</c> here would hand a
///     read-only token the whole write surface, which is strictly worse than refusing it.
///     </para>
///     <para>
///     This is not a breaking change for any provisioned consumer: the two seeded MCP clients
///     (<c>octo-mcpServices-swagger</c> / <c>octo-mcpServices-device</c>, System.Identity.Bootstrap
///     entities 660…33 / 660…34) allow <c>octo_api</c> and not <c>octo_api.read_only</c>; the device
///     flow in <c>AuthenticationTools</c> and the RFC 8693 exchange in <c>TenantTokenExchanger</c> both
///     request <c>octo_api</c>; and the mesh-adapter service account requests
///     <c>ApiScopes.OctoApiFullAccess</c>. The set is nevertheless configurable via
///     <see cref="McpServiceOptions.RequiredApiScopes" /> so an operator can widen it for a service
///     account provisioned differently, without a code change.
///     </para>
///     <para>
///     A per-tool read/write distinction remains possible <i>in band</i> (at tool dispatch, where the
///     tool name is known) and is the right place for it; that is a separate change from the endpoint
///     policy this work item asks for.
///     </para>
/// </summary>
internal static class McpAuthorizationPolicy
{
    /// <summary>
    ///     Name of the authorization policy both MCP endpoints require.
    /// </summary>
    public const string PolicyName = "McpTransportPolicy";

    /// <summary>
    ///     Registers <see cref="PolicyName" /> against the configured required scopes.
    /// </summary>
    /// <param name="options">Authorization options being built.</param>
    /// <param name="requiredScopes">Scopes of which the caller must carry at least one.</param>
    public static void AddMcpTransportPolicy(
        AuthorizationOptions options, IReadOnlyCollection<string> requiredScopes)
    {
        options.AddPolicy(PolicyName, policy =>
        {
            policy.RequireAuthenticatedUser();
            if (requiredScopes.Count > 0)
            {
                policy.RequireAssertion(ctx => HasRequiredScope(ctx.User, requiredScopes));
            }
        });
    }

    /// <summary>
    ///     True when <paramref name="principal" /> carries at least one of
    ///     <paramref name="requiredScopes" /> in its <c>scope</c> claims.
    ///     <para>
    ///     Both wire encodings are accepted: one claim per scope (what Duende's JWTs produce, and what
    ///     the backend services' <c>RequireClaim</c> policies rely on) and a single space-delimited
    ///     claim value (the raw OAuth form some handlers leave intact). Accepting both is never more
    ///     permissive than the backend rule — it only stops a correctly-scoped token from being refused
    ///     because of how the handler split the claim.
    ///     </para>
    /// </summary>
    /// <param name="principal">The validated request principal.</param>
    /// <param name="requiredScopes">Scopes of which one must be present.</param>
    public static bool HasRequiredScope(
        ClaimsPrincipal? principal, IReadOnlyCollection<string> requiredScopes)
    {
        if (principal == null)
        {
            return false;
        }

        foreach (var claim in principal.FindAll(InfrastructureCommon.ClaimScope))
        {
            foreach (var value in claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (requiredScopes.Contains(value, StringComparer.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }
}
