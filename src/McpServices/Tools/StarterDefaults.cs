using Meshmakers.Octo.Backend.McpServices.Services;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using ModelContextProtocol.Server;

namespace Meshmakers.Octo.Backend.McpServices.Tools;

/// <summary>One deployment site or workload of a starter tenant, as the tenant repository stores it.</summary>
/// <param name="RtId">Runtime id.</param>
/// <param name="Name">Name attribute (empty when it has none).</param>
/// <param name="DeploymentState"><c>System.Communication/DeploymentState</c> as its number; null when unreadable.</param>
internal sealed record StarterDefaultsEntity(string RtId, string Name, int? DeploymentState)
{
    /// <summary><c>System.Communication/DeploymentState</c> Undeployed.</summary>
    internal const int Undeployed = 0;

    private static readonly string[] StateNames = ["Undeployed", "Pending", "Deployed", "Error", "Disabled"];

    internal bool IsUndeployed => DeploymentState == Undeployed;

    /// <summary>The state as its CK enum name, "unknown" when it cannot be read.</summary>
    internal string StateName => DeploymentState is { } s && s >= 0 && s < StateNames.Length ? StateNames[s] : "unknown";
}

/// <summary>
///     The deployment sites and workloads of a tenant, read the way the communication controller reads them
///     (<c>StarterDeploymentService</c>, AB#8452), so <c>create_tenant starter=true</c> applies the same rule.
/// </summary>
/// <param name="SiteCount">Number of deployment sites of the tenant (0 when it has no System.Communication model).</param>
/// <param name="Site">The only site, null unless <paramref name="SiteCount" /> is 1.</param>
/// <param name="SiteIsCloud">Whether the only site has Environment Cloud.</param>
/// <param name="WorkloadCount">Number of workloads the only site hosts (role Hosts), 0 without exactly one site.</param>
/// <param name="Adapter">The only workload when it is an Adapter, else null.</param>
internal sealed record StarterDefaultsState(
    int SiteCount,
    StarterDefaultsEntity? Site,
    bool SiteIsCloud,
    int WorkloadCount,
    StarterDefaultsEntity? Adapter)
{
    /// <summary>No deployment site at all: Communication was never enabled for the tenant.</summary>
    internal static StarterDefaultsState None { get; } = new(0, null, false, 0, null);

    /// <summary>
    ///     The controller's rule for a fresh tenant: exactly one Cloud site, exactly one workload on it, and that workload
    ///     is an adapter.
    /// </summary>
    internal bool IsFreshShape => SiteCount == 1 && Site != null && SiteIsCloud && WorkloadCount == 1 && Adapter != null;

    /// <summary>Why the tenant does not have the fresh shape, for the step details.</summary>
    internal string ShapeText => SiteCount != 1
        ? $"{SiteCount} deployment site(s), expected exactly one Cloud site"
        : !SiteIsCloud
            ? $"site '{Site?.Name}' is not a Cloud site"
            : WorkloadCount != 1 || Adapter == null
                ? $"site '{Site?.Name}' hosts {WorkloadCount} workload(s), expected exactly one adapter"
                : "one Cloud site with one adapter";
}

/// <summary>Reads <see cref="StarterDefaultsState" /> from the tenant repository with the caller's rights.</summary>
internal static class StarterDefaults
{
    internal const string SiteType = "System.Communication/DeploymentSite";
    internal const string WorkloadType = "System.Communication/DeployableWorkload";
    internal const string AdapterType = "System.Communication/Adapter";
    internal const string HostsRole = "System.Communication/Hosts";

    /// <summary><c>System.Communication/Environment</c> Cloud.</summary>
    internal const int EnvironmentCloud = 1;

    /// <summary>
    ///     Opens the tenant with the caller's rights and reads its sites and the workloads of the only site. A tenant
    ///     without the System.Communication model has no sites (<see cref="StarterDefaultsState.None" />). Throws when the
    ///     tenant cannot be opened, so callers fail closed.
    /// </summary>
    internal static async Task<StarterDefaultsState> ReadAsync(McpServer server, string tenantId,
        CancellationToken cancellationToken)
    {
        var tenantResolution = server.Services!.GetRequiredService<ITenantResolutionService>();
        var security = await RuntimeSecurityContextResolver.ResolveAsync(server, tenantResolution, tenantId,
            cancellationToken);
        if (security.Error != null)
        {
            throw new InvalidOperationException(security.Error);
        }

        var repository = await tenantResolution.GetTenantRepositoryAsync(tenantId);
        var ckCache = server.Services!.GetRequiredService<ICkCacheService>();
        await repository.LoadCacheForTenantAsync(ckCache);
        if (!ckCache.TryGetRtCkType(repository.TenantId, new RtCkId<CkTypeId>(SiteType), out _))
        {
            return StarterDefaultsState.None;
        }

        using var session = await repository.GetSessionAsync(security.SecurityContext!);
        return await ReadAsync(repository, session);
    }

    /// <summary>Reads the sites and the workloads of the only site; see <see cref="StarterDefaultsState" />.</summary>
    internal static async Task<StarterDefaultsState> ReadAsync(ITenantRepository repository, IOctoSession session)
    {
        var siteTypeId = new RtCkId<CkTypeId>(SiteType);
        var sites = await repository.GetRtEntitiesByTypeAsync(session, siteTypeId, RtEntityQueryOptions.Create(), 0, 3);
        var siteCount = sites.Items.Count();
        if (siteCount != 1)
        {
            return new StarterDefaultsState(siteCount, null, false, 0, null);
        }

        var site = sites.Items.First();
        var siteIsCloud = EnumValue(site.GetAttributeValueOrDefault("Environment")) == EnvironmentCloud;

        // Workloads point to their site with the role Hosts, so from the site the association is inbound; the target
        // type is abstract and the repository returns the concrete adapters and applications (as in the controller).
        var workloads = (await repository.GetRtAssociationTargetsAsync(session, site.RtId, siteTypeId,
            new RtCkId<CkAssociationRoleId>(HostsRole), new RtCkId<CkTypeId>(WorkloadType), GraphDirections.Inbound,
            null, RtEntityQueryOptions.Create(), 0, 3)).Items.ToList();
        var adapter = workloads.Count == 1 && Equals(workloads[0].CkTypeId, new RtCkId<CkTypeId>(AdapterType))
            ? ToEntity(workloads[0])
            : null;

        return new StarterDefaultsState(1, ToEntity(site), siteIsCloud, workloads.Count, adapter);
    }

    private static StarterDefaultsEntity ToEntity(RtEntity entity) => new(entity.RtId.ToString(),
        entity.GetAttributeStringValueOrDefault("Name") ?? string.Empty,
        EnumValue(entity.GetAttributeValueOrDefault("DeploymentState")));

    /// <summary>An enum attribute as its number; null when it is missing or not a number.</summary>
    internal static int? EnumValue(object? value) => value switch
    {
        int i => i,
        long l => (int)l,
        Enum e => Convert.ToInt32(e, System.Globalization.CultureInfo.InvariantCulture),
        string s when int.TryParse(s, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var n) => n,
        _ => null
    };
}
