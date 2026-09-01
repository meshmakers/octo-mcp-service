using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using ModelContextProtocol.Server;

namespace Meshmakers.Octo.Backend.McpServices.Services;

/// <summary>
///     The authenticated caller of the current MCP request, read off the principal the JWT bearer handler
///     produced <b>after</b> it verified the token's signature, issuer and lifetime.
///     <para>
///     This is the single definition of "who is calling" for the whole service. It was extracted from
///     <see cref="RuntimeSecurityContextResolver" /> (AB#5030) when the session token store had to be
///     bound to the caller as well (AB#5036) — two copies of the claim reading would have been free to
///     drift, and the two features must agree on what a "service token" is or the client-credentials
///     exemption would apply on one path and not the other.
///     </para>
///     <para>
///     Claim reading mirrors <c>AssetRepositoryServices/GraphQL/Helpers.GetSecurityContext</c> one-for-one:
///     <c>ConfigureJwtBearerOptions</c> leaves <c>MapInboundClaims</c> at its ASP.NET default of
///     <c>true</c>, so a JWT <c>sub</c> reaches the principal as <see cref="ClaimTypes.NameIdentifier" />
///     and <c>role</c> as <see cref="ClaimTypes.Role" />, while <c>tenant_id</c> and <c>client_id</c> keep
///     their JWT names. Probing only the JWT short names would misclassify every user token as a service
///     token and hand it the tenant-gate exemption.
///     </para>
/// </summary>
/// <param name="UserSubjectId">The end-user subject, or null for a client-credentials service token.</param>
/// <param name="ClientId">The OAuth client id, when the token carries one.</param>
/// <param name="TenantId">The <c>tenant_id</c> claim, i.e. the tenant the caller is homed in.</param>
/// <param name="Roles">Role claim values, de-duplicated across both claim spellings.</param>
internal sealed record McpCallerPrincipal(
    string? UserSubjectId,
    string? ClientId,
    string? TenantId,
    string[] Roles)
{
    /// <summary>
    ///     Whether this is an end-user principal. A client-credentials (service) token carries no subject
    ///     at all — only a client id — and that absence is the marker both the tenant gate (AB#5030) and
    ///     the session-token binding (AB#5036) key their exemption off.
    /// </summary>
    public bool IsUserPrincipal => UserSubjectId != null;

    /// <summary>
    ///     Identity a runtime session acts as: the end user when there is one, else the calling client.
    /// </summary>
    public string? SubjectId => UserSubjectId ?? ClientId;

    /// <summary>
    ///     Caller-scoped prefix for every <see cref="IMcpSessionTokenStore" /> key (AB#5036).
    ///     <para>
    ///     Before AB#5036 the store key was the client-supplied <c>Mcp-Session-Id</c> header alone, with a
    ///     shared constant fallback when the header was absent — and because the Streamable HTTP transport
    ///     defaults to <c>HttpServerSessionMode.Stateless</c> it never mints that header, so in practice
    ///     every caller on the pod shared one process-wide slot. Deriving the key from the validated
    ///     principal makes the slot unreachable for anyone else: a caller can still name any session id
    ///     they like, but it only ever partitions <i>within</i> their own namespace.
    ///     </para>
    ///     Null when the principal carries neither a subject nor a client id — such a caller gets no store
    ///     access at all rather than a shared one.
    /// </summary>
    public string? StoreKeyPrefix => IsUserPrincipal
        ? $"u:{TenantId ?? "-"}:{UserSubjectId}"
        : ClientId != null
            ? $"c:{ClientId}"
            : null;

    /// <summary>
    ///     Reads the caller off the request principal reachable through the server's
    ///     <see cref="IHttpContextAccessor" />. Returns null when the caller is not authenticated, or when
    ///     the host never registered an accessor — resolved with <c>GetService</c> so such a host degrades
    ///     into a denial rather than an exception thrown out of a tool.
    /// </summary>
    /// <param name="server">MCP server instance — provides DI access to the request context.</param>
    public static McpCallerPrincipal? FromServer(McpServer server)
    {
        return FromHttpContext(server.Services?.GetService<IHttpContextAccessor>()?.HttpContext);
    }

    /// <summary>
    ///     Reads the caller off <paramref name="httpContext" />'s principal, or null when there is no
    ///     authenticated principal.
    /// </summary>
    /// <param name="httpContext">The current request, may be null.</param>
    public static McpCallerPrincipal? FromHttpContext(HttpContext? httpContext)
    {
        var user = httpContext?.User;
        if (user?.Identity?.IsAuthenticated != true)
        {
            return null;
        }

        var userSubjectId = user.FindFirst("sub")?.Value
                            ?? user.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        var clientId = user.FindFirst("client_id")?.Value;
        var tenantId = user.FindFirst("tenant_id")?.Value;

        // Role claims may arrive under the identity's RoleClaimType (inbound mapping on) or under the
        // JWT short name "role" (mapping off) — read both, like the subject above.
        var roles = user.Identities
            .SelectMany(i => i.FindAll(i.RoleClaimType).Concat(i.FindAll("role")))
            .Select(c => c.Value)
            .Distinct()
            .ToArray();

        return new McpCallerPrincipal(userSubjectId, clientId, tenantId, roles);
    }
}
