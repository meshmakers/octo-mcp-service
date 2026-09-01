using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.DataPermissions;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using ModelContextProtocol.Server;

namespace Meshmakers.Octo.Backend.McpServices.Services;

/// <summary>
///     Stream-data type gate (AB#5038) — the MCP mirror of the asset-repo
///     <c>GraphQL/Utils/DataPermissionStreamGuard</c> (AB#4973, decision F4).
///     <para>
///     A stream-data read on a protected CK type is rejected before any CrateDB SQL is built when the
///     caller has no <b>full</b> Read grant. Stream rows carry no creator, so an owned-only grant cannot
///     be honored per row and denies stream reads (conservative — accepted limitation F4 of AB#4969).
///     Tenants without policies pass through untouched.
///     </para>
///     <para>
///     <b>Why this gate is the barrier.</b> The engine-side stream stores
///     (<c>ITenantContext.GetStreamDataRepository()</c> / <c>GetArchiveRuntimeStore()</c> /
///     <c>GetRollupArchiveRuntimeStore()</c>) open their own <i>system</i> sessions internally, so the
///     caller's <see cref="RtSecurityContext" /> never reaches the archive read and row-level filtering
///     does not happen on this path. Threading the security context into those stores is the larger fix
///     and is deliberately out of scope here; until it lands, the type gate plus the AB#5030 tenant gate
///     are the whole barrier.
///     </para>
///     <para>
///     Decision logic and message text are kept identical to the GraphQL guard on purpose: the same
///     caller must get the same answer through MCP and through GraphQL. The one deliberate difference is
///     the shape of the refusal — MCP tools never throw, so the guard <i>returns</i> the message and the
///     caller puts it into its response envelope's <c>ErrorMessage</c>.
///     </para>
/// </summary>
internal static class DataPermissionStreamGuard
{
    /// <summary>
    ///     Surface name embedded in the refusal, matching the GraphQL guard's
    ///     <c>EnsureStreamReadAllowedAsync</c> ("stream data").
    /// </summary>
    private const string StreamDataSurface = "stream data";

    /// <summary>
    ///     True when the guard would actually classify something for this caller and tenant — i.e. the
    ///     caller is not a system context, the data-permission services are wired, and the tenant has at
    ///     least one policy rule.
    ///     <para>
    ///     Call sites that would have to fetch extra state <i>just</i> to know the CK type (e.g. the
    ///     archive snapshots behind <c>get_archive_storage_stats</c>) use this to skip that work on the
    ///     overwhelmingly common unprotected-tenant path. The policy table is TTL-cached per tenant in
    ///     <see cref="IDataPermissionResolver" />, so the follow-up
    ///     <see cref="EnsureStreamReadAllowedAsync" /> does not pay for the lookup twice.
    ///     </para>
    /// </summary>
    /// <param name="server">MCP server instance — provides DI access to the resolver and the CK cache.</param>
    /// <param name="tenantRepository">Repository of the tenant whose policy table is resolved.</param>
    /// <param name="securityContext">The caller, as resolved by <see cref="RuntimeSecurityContextResolver" />.</param>
    public static async ValueTask<bool> IsEnforcingAsync(
        McpServer server,
        ITenantRepository tenantRepository,
        RtSecurityContext? securityContext)
    {
        if (securityContext is null || securityContext.IsSystem)
        {
            return false;
        }

        var resolver = server.Services?.GetService<IDataPermissionResolver>();
        if (resolver == null || server.Services?.GetService<ICkCacheService>() == null)
        {
            return false;
        }

        var policyTable = await resolver.GetPolicyTableAsync(tenantRepository).ConfigureAwait(false);
        return policyTable.HasRules;
    }

    /// <summary>
    ///     Checks whether <paramref name="securityContext" /> holds a full Read grant on
    ///     <paramref name="ckTypeId" /> in <paramref name="tenantId" />.
    /// </summary>
    /// <param name="server">MCP server instance — provides DI access to the resolver and the CK cache.</param>
    /// <param name="tenantRepository">Repository of the tenant whose policy table is resolved.</param>
    /// <param name="tenantId">Tenant the CK type is resolved in.</param>
    /// <param name="ckTypeId">Target CK type of the archive / persisted query being read.</param>
    /// <param name="securityContext">The caller, as resolved by <see cref="RuntimeSecurityContextResolver" />.</param>
    /// <returns>
    ///     <c>null</c> when the read may proceed (unprotected type, full grant, or the guard is dormant
    ///     because the data-permission services are not wired); otherwise the refusal message.
    /// </returns>
    public static async ValueTask<string?> EnsureStreamReadAllowedAsync(
        McpServer server,
        ITenantRepository tenantRepository,
        string tenantId,
        RtCkId<CkTypeId> ckTypeId,
        RtSecurityContext? securityContext)
    {
        // A system context bypasses data permissions by definition. AB#5030 means the direct-engine
        // tools never build one, but the check keeps the two guards literally identical.
        if (securityContext is null || securityContext.IsSystem)
        {
            return null;
        }

        // Resolved with GetService, not GetRequiredService: the guard is dormant when the host did not
        // wire the data-permission services, exactly like the GraphQL one. AddRuntimeEngine() registers
        // both in production.
        var resolver = server.Services?.GetService<IDataPermissionResolver>();
        var ckCacheService = server.Services?.GetService<ICkCacheService>();
        if (resolver == null || ckCacheService == null)
        {
            return null;
        }

        var policyTable = await resolver.GetPolicyTableAsync(tenantRepository).ConfigureAwait(false);
        if (!policyTable.HasRules)
        {
            return null;
        }

        // Base types matter: a policy targeting a base / collection-root type protects the derived type
        // too, and GetSelfAndBaseFullNames can only walk the hierarchy when the tenant's CK model is in
        // the cache. Loading it here (and only on the protected path, so the dormant case stays free)
        // avoids under-blocking on a cold cache — the GraphQL side is always hot because the schema was
        // built from the same cache.
        await tenantRepository.LoadCacheForTenantAsync(ckCacheService).ConfigureAwait(false);

        var selfAndBase =
            RtDataPermissionCkTypeHelper.GetSelfAndBaseFullNames(ckCacheService, tenantId, ckTypeId);
        var level = RtDataAccessEvaluator.Classify(policyTable, selfAndBase, RtDataAction.Read,
            securityContext, includeAuditOnlyPolicies: false);

        return level is RtDataAccessLevel.Denied or RtDataAccessLevel.OwnedOnly
            ? $"Access denied: missing data permission 'Read' on '{ckTypeId.SemanticVersionedFullName}' "
              + $"for {StreamDataSurface}."
            : null;
    }
}
