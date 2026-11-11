using System.Globalization;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;

namespace Meshmakers.Octo.Backend.McpServices.Services.Knowledge;

/// <summary>What the knowledge maintenance does with a page that has a newer Blueprint version (AB#8780).</summary>
internal enum PlatformUpdateState
{
    /// <summary>Unedited page (its current version is a Blueprint version): switch it to the newer Blueprint version.</summary>
    Adoptable,

    /// <summary>Edited page (its current version was written in the tenant): report PlatformUpdateAvailable.</summary>
    Available
}

/// <summary>One version of a knowledge page, as far as the platform-update rule needs it.</summary>
internal sealed record KnowledgeVersionInfo(RtEntity Entity, int Number, bool IsBlueprint, DateTime? ArrivedAt)
{
    /// <summary>The blueprint id and version of a Blueprint version (<c>blueprint:System.Ai.Builder-1.1.0</c> → <c>System.Ai.Builder-1.1.0</c>).</summary>
    public string? Blueprint => IsBlueprint
        ? Entity.GetAttributeStringValueOrDefault("Author")?[KnowledgePlatformUpdates.BlueprintAuthorPrefix.Length..]
        : null;
}

/// <summary>A page with a newer Blueprint version and what to do with it.</summary>
internal sealed record PlatformUpdate(
    RtEntity Page,
    string Slug,
    int CurrentVersion,
    PlatformUpdateState State,
    KnowledgeVersionInfo Target,
    int? DismissedVersion);

/// <summary>
///     AB#8780: completes the blueprint update rule of AB#8778 for knowledge pages. The engine does not refresh an existing
///     page (its content is <c>TenantOwned</c>, <c>CurrentVersion</c> is <c>RuntimeState</c>, pages with <c>LinksTo</c>
///     are skipped); only the new <c>KnowledgePageVersion</c> entities of a blueprint update arrive. This class finds the
///     pages they belong to and decides, from the data alone:
///     <list type="bullet">
///         <item>
///             <b>Unedited</b> — the current version is a Blueprint version (the tenant never wrote one): the newest
///             Blueprint version with a higher number is <see cref="PlatformUpdateState.Adoptable" />.
///         </item>
///         <item>
///             <b>Edited</b> — the current version was written in the tenant (or cannot be found): the newest Blueprint
///             version is <see cref="PlatformUpdateState.Available" /> when it is newer than the current one (a higher
///             number, or it arrived in the tenant after the current version was written) and its number is above the
///             version a person dismissed.
///         </item>
///     </list>
///     A Blueprint version is one a blueprint seeded: its author is <c>blueprint:&lt;id&gt;-&lt;version&gt;</c> and it
///     carries the engine's <c>rtBlueprintSource</c> stamp. The knowledge tools never write such an author (they write
///     <c>user:</c> or <c>client:</c>), and client-credentials callers cannot write versions directly (writeAccess,
///     AB#8575). Deleted pages are not looked at: their versions stay, the page is not recreated.
/// </summary>
internal static class KnowledgePlatformUpdates
{
    /// <summary>Author prefix of a version seeded by a blueprint (System.Ai.Default, System.Ai.Builder).</summary>
    public const string BlueprintAuthorPrefix = "blueprint:";

    /// <summary>The page attribute of System.Ai 4.10.0 that only tenants with the AB#8780 model have.</summary>
    public const string DismissedAttribute = "DismissedBlueprintVersion";

    /// <summary>The page attribute (System.Ai attribute <c>RunRef</c>) naming the run that last adopted a Blueprint version.</summary>
    public const string AdoptedByRunAttribute = "AdoptedByRun";

    /// <summary>The version attribute with the LinksTo slugs a Blueprint version declares.</summary>
    public const string DeclaredLinksAttribute = "DeclaredLinks";

    /// <summary>At most this many pages are looked at in one call (like the lint).</summary>
    public const int MaxPages = 1000;

    /// <summary>At most this many Blueprint versions are read in one call.</summary>
    public const int MaxBlueprintVersions = 5000;

    /// <summary>True for a version a blueprint seeded (author prefix and the engine's blueprint stamp).</summary>
    public static bool IsBlueprintVersion(RtEntity version) =>
        version.GetAttributeStringValueOrDefault("Author") is { } author &&
        author.StartsWith(BlueprintAuthorPrefix, StringComparison.Ordinal) &&
        author.Length > BlueprintAuthorPrefix.Length &&
        !string.IsNullOrEmpty(version.GetAttributeStringValueOrDefault("RtBlueprintSource"));

    /// <summary>Every version of a page (well-known name <c>&lt;slug&gt;@&lt;n&gt;</c>).</summary>
    public static async Task<IReadOnlyList<KnowledgeVersionInfo>> ListVersionsAsync(ITenantRepository repository,
        IOctoSession session, string slug)
    {
        var options = RtEntityQueryOptions.Create();
        options.FieldStartsWith("rtWellKnownName", slug + "@");
        var result = await repository.GetRtEntitiesByTypeAsync(session, KnowledgePages.VersionTypeId, options, 0,
            MaxBlueprintVersions);
        return result.Items.Select(v => Info(v, slug)).Where(v => v != null).Select(v => v!).ToList();
    }

    /// <summary>The highest version number of a page; 0 when it has none.</summary>
    public static async Task<int> HighestVersionAsync(ITenantRepository repository, IOctoSession session, string slug)
    {
        var versions = await ListVersionsAsync(repository, session, slug);
        return versions.Count == 0 ? 0 : versions.Max(v => v.Number);
    }

    /// <summary>
    ///     The pages that have a newer Blueprint version, in slug order. One query finds the Blueprint versions; only the
    ///     pages they belong to are read in full.
    /// </summary>
    public static async Task<IReadOnlyList<PlatformUpdate>> FindAsync(ITenantRepository repository,
        IOctoSession session)
    {
        var options = RtEntityQueryOptions.Create();
        options.FieldStartsWith("Author", BlueprintAuthorPrefix);
        var blueprintVersions = (await repository.GetRtEntitiesByTypeAsync(session, KnowledgePages.VersionTypeId,
                options, 0, MaxBlueprintVersions)).Items
            .Where(IsBlueprintVersion)
            .Select(v => (Slug: SlugOf(v.RtWellKnownName), Version: v))
            .Where(x => x.Slug != null)
            .GroupBy(x => x.Slug!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Version).ToList(), StringComparer.Ordinal);
        if (blueprintVersions.Count == 0)
        {
            return [];
        }

        var pages = await repository.GetRtEntitiesByTypeAsync(session, KnowledgePages.PageTypeId,
            RtEntityQueryOptions.Create(), 0, MaxPages);
        var updates = new List<PlatformUpdate>();
        foreach (var page in pages.Items.Where(p => p.RtWellKnownName != null && blueprintVersions.ContainsKey(p.RtWellKnownName))
                     .OrderBy(p => p.RtWellKnownName, StringComparer.Ordinal))
        {
            // Only pages with a Blueprint version get here; their full version list decides (current version, arrival).
            var slug = page.RtWellKnownName!;
            var versions = await ListVersionsAsync(repository, session, slug);
            var update = Decide(page, slug, KnowledgePages.CurrentVersion(page), versions, Dismissed(page));
            if (update != null)
            {
                updates.Add(update);
            }
        }

        return updates;
    }

    /// <summary>
    ///     The rule for one page (see the class remarks); <c>null</c> when there is nothing to adopt or report.
    /// </summary>
    public static PlatformUpdate? Decide(RtEntity page, string slug, int current,
        IReadOnlyList<KnowledgeVersionInfo> versions, int? dismissed)
    {
        var blueprint = versions.Where(v => v.IsBlueprint).ToList();
        if (blueprint.Count == 0)
        {
            return null;
        }

        // The current version: every version with the current number. When a version written in the tenant shares the
        // number with a Blueprint version (a seed that arrived with a number the tenant had already used), the page
        // counts as edited - the safe direction: nothing is switched, a person decides.
        var currentVersions = versions.Where(v => v.Number == current).ToList();
        var unedited = currentVersions.Count > 0 && currentVersions.All(v => v.IsBlueprint);
        if (unedited)
        {
            var target = blueprint.Where(v => v.Number > current).MaxBy(v => v.Number);
            return target == null
                ? null
                : new PlatformUpdate(page, slug, current, PlatformUpdateState.Adoptable, target, dismissed);
        }

        var newest = blueprint.MaxBy(v => v.Number)!;
        if (dismissed != null && newest.Number <= dismissed.Value)
        {
            return null;
        }

        var currentArrived = currentVersions.Select(v => v.ArrivedAt).Where(t => t != null).DefaultIfEmpty(null).Max() ??
                             CompiledAt(page);
        var newer = newest.Number > current ||
                    currentArrived == null ||
                    (newest.ArrivedAt != null && newest.ArrivedAt > currentArrived);
        return newer ? new PlatformUpdate(page, slug, current, PlatformUpdateState.Available, newest, dismissed) : null;
    }

    /// <summary>The dismissed Blueprint version of a page, if any.</summary>
    public static int? Dismissed(RtEntity page) =>
        page.GetAttributeValueOrDefault(DismissedAttribute) switch
        {
            null => null,
            int i => i,
            long l => (int)l,
            var v => int.TryParse(v.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null
        };

    /// <summary>The LinksTo slugs a version declares; <c>null</c> when it declares none (links stay unchanged).</summary>
    public static IReadOnlyList<string>? DeclaredLinks(RtEntity version) =>
        version.GetAttributeValueOrDefault(DeclaredLinksAttribute) switch
        {
            null => null,
            string s => [s],
            IEnumerable<object> list => list.Where(x => x != null).Select(x => x.ToString()!)
                .Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).Distinct(StringComparer.Ordinal).ToList(),
            _ => null
        };

    /// <summary>The slug of a version name <c>&lt;slug&gt;@&lt;n&gt;</c>; <c>null</c> for another name.</summary>
    public static string? SlugOf(string? versionName)
    {
        if (string.IsNullOrEmpty(versionName))
        {
            return null;
        }

        var at = versionName.LastIndexOf('@');
        return at > 0 && int.TryParse(versionName[(at + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out _)
            ? versionName[..at]
            : null;
    }

    private static KnowledgeVersionInfo? Info(RtEntity version, string slug)
    {
        if (SlugOf(version.RtWellKnownName) != slug)
        {
            return null;
        }

        var number = version.GetAttributeValueOrDefault("VersionNumber") switch
        {
            int i => i,
            long l => (int)l,
            _ => int.Parse(version.RtWellKnownName![(version.RtWellKnownName!.LastIndexOf('@') + 1)..],
                CultureInfo.InvariantCulture)
        };
        var arrived = version.RtCreationDateTime ?? version.GetAttributeValueOrDefault("AuthoredAt") as DateTime?;
        return new KnowledgeVersionInfo(version, number, IsBlueprintVersion(version), arrived?.ToUniversalTime());
    }

    private static DateTime? CompiledAt(RtEntity page) =>
        page.GetAttributeValueOrDefault("CompiledAt") is DateTime t ? t.ToUniversalTime() : null;
}
