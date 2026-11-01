using System.Collections;
using Meshmakers.Common.Shared;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v2;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Engine.StreamData;

namespace Meshmakers.Octo.Backend.McpServices.Services;

/// <summary>
///     AB#8043: the CK attribute access rules (CK v2 <c>access</c>, AB#5668) and the identity-owned User fields (AB#8042)
///     for the generic CRUD, query and aggregation tools. These tools talk to the tenant repository directly, and the
///     repository does not know <c>access</c>, so the MCP server enforces the same rules as the asset-repo GraphQL guards
///     (<c>AccessQueryGuard</c>, <c>IdentityOwnedWriteGuard</c>, <c>StoredQueryWriteGuard</c>):
///     <list type="bullet">
///         <item>a <c>Hidden</c> attribute is never used in a filter, aggregation, group-by or stored query
///             (<see cref="NotQueryableCode" />); its value never leaves the server (<see cref="AccessAwareRtEntityToDtoMapper" />);</item>
///         <item>a <c>Hidden</c> or <c>MethodOnly</c> attribute is never written, a <c>ReadOnly</c> one only on create
///             (<see cref="AttributeAccess.IsGenericallyWritable" />), and the identity-owned User fields of
///             <see cref="IdentityOwnedWrites" /> never (<see cref="NotWritableCode" />).</item>
///     </list>
///     The rules come from the engine helpers (<see cref="AttributeAccess" />, <see cref="IdentityOwnedWrites" />,
///     <see cref="ArchiveHiddenColumnGuard.FindHiddenAttribute" />); this class keeps no list of its own. Messages name the
///     attribute path and the type, never a value.
/// </summary>
internal static class AttributeAccessGuard
{
    /// <summary>Error code of a query refusal (identical to the GraphQL error code).</summary>
    public const string NotQueryableCode = "ATTRIBUTE_NOT_QUERYABLE";

    /// <summary>Error code of a write refusal (identical to the GraphQL error code).</summary>
    public const string NotWritableCode = "ATTRIBUTE_NOT_WRITABLE";

    internal const string StoredFilterOperation = "a stored query filter";
    internal const string StoredSortOperation = "a stored query sort order";
    internal const string StoredColumnOperation = "a stored query column";
    internal const string StoredGroupByOperation = "a stored query group-by column";
    internal const string StoredAttributeSearchOperation = "a stored query attribute search";

    /// <summary>
    ///     The refusal message for the first usage whose path reaches a <c>Hidden</c> attribute, or <c>null</c>.
    ///     A path reaches Hidden when one of its segments is Hidden or it ends at a record that (transitively) holds a
    ///     Hidden sub-attribute (<see cref="ArchiveHiddenColumnGuard.FindHiddenAttribute" />). The path is checked against
    ///     the queried type and every type derived from it (a query returns derived entities too). A type the CK cache
    ///     does not know is checked by attribute name against every Hidden assignment of the tenant (fail closed).
    /// </summary>
    /// <param name="ckCache">CK cache (loaded for the tenant)</param>
    /// <param name="tenantId">Tenant id</param>
    /// <param name="ckTypeId">The queried CK type (runtime or full id); <c>null</c> = unknown, checked by name</param>
    /// <param name="usages">Attribute paths and how they are used (e.g. <c>filter operator 'Equals'</c>)</param>
    public static string? FindHiddenUsage(ICkCacheService ckCache, string tenantId, string? ckTypeId,
        IEnumerable<(string? Path, string Operation)> usages)
    {
        var list = usages.Where(u => !string.IsNullOrWhiteSpace(u.Path)).ToList();
        if (list.Count == 0)
        {
            return null;
        }

        var roots = ResolveRoots(ckCache, tenantId, ckTypeId);
        HashSet<string>? hiddenNames = null;
        foreach (var (path, operation) in list)
        {
            var normalized = NormalizePath(path!);
            var hidden = roots.Count == 0
                ? IsHiddenByName(normalized, hiddenNames ??= HiddenAttributeNames(ckCache, tenantId))
                : roots.Any(r => ArchiveHiddenColumnGuard.FindHiddenAttribute(ckCache, tenantId, r, normalized) != null);
            if (hidden)
            {
                return $"{NotQueryableCode}: Attribute '{path}' of '{ckTypeId ?? "<unknown>"}' is hidden and cannot be " +
                       $"used for {operation}.";
            }
        }

        return null;
    }

    /// <summary>
    ///     The refusal message for the first attribute path a generic write must not set, or <c>null</c>. Every segment
    ///     counts (a protected record-valued attribute also protects <c>record.field</c>): <c>Hidden</c> and
    ///     <c>MethodOnly</c> never, <c>ReadOnly</c> only when <paramref name="isCreate" />
    ///     (<see cref="AttributeAccess.IsGenericallyWritable" />). The identity-owned fields of
    ///     <c>System.Identity/User</c> (<see cref="IdentityOwnedWrites" />) are refused on create and update. Unknown
    ///     attributes pass (the engine reports them when the write runs).
    /// </summary>
    /// <param name="ckCache">CK cache (loaded for the tenant)</param>
    /// <param name="tenantId">Tenant id</param>
    /// <param name="ckTypeId">The written CK type (runtime id <c>Model/Type</c>)</param>
    /// <param name="paths">The attribute paths of the write (dot notation through records, indexes allowed)</param>
    /// <param name="isCreate"><c>true</c> for a create, <c>false</c> for an update</param>
    public static string? FindNotWritable(ICkCacheService ckCache, string tenantId, string ckTypeId,
        IEnumerable<string?> paths, bool isCreate)
    {
        var rtCkTypeId = TryParse(ckTypeId);
        CkTypeGraph? root = null;
        if (rtCkTypeId != null && ckCache.TryGetRtCkType(tenantId, rtCkTypeId, out var graph))
        {
            root = graph;
        }

        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            var segments = Segments(NormalizePath(path)).ToList();
            if (segments.Count == 0)
            {
                continue;
            }

            if (root != null)
            {
                var protectedAttribute = ResolveSegments(ckCache, tenantId, root, segments)
                    .FirstOrDefault(a => !AttributeAccess.IsGenericallyWritable(a.Access, isCreate));
                if (protectedAttribute != null)
                {
                    return $"{NotWritableCode}: Attribute '{path}' of '{ckTypeId}' is not writable via generic " +
                           $"mutations (access: {protectedAttribute.Access}).";
                }
            }

            if (IdentityOwnedWrites.IsIdentityOwnedAttribute(rtCkTypeId, segments[0]))
            {
                return $"{NotWritableCode}: '{path}' of '{ckTypeId}' is written only by the identity service " +
                       $"(identity REST API or identity methods), not via generic mutations (reason: " +
                       $"{IdentityOwnedWrites.RefusalReason}).";
            }
        }

        return null;
    }

    /// <summary>
    ///     AB#8049 for the MCP writers and runners: the refusal message when <paramref name="entity" /> is a stored query
    ///     (<c>System/PersistentQuery</c> or a type derived from it) whose filters, sort orders, columns, group-by columns
    ///     or attribute-search paths reach a <c>Hidden</c> attribute of its <c>QueryCkTypeId</c>, otherwise <c>null</c>.
    ///     The repository applies a stored filter as it is, so such a query would answer "does the hidden value match my
    ///     guess" wherever it runs.
    /// </summary>
    public static string? FindHiddenStoredQueryPath(ICkCacheService ckCache, string tenantId, RtEntity entity)
    {
        if (entity.CkTypeId == null || !IsPersistentQuery(ckCache, tenantId, entity.CkTypeId))
        {
            return null;
        }

        var paths = CollectStoredQueryPaths(name => entity.Attributes.TryGetValue(name, out var v) ? v : null)
            .Select(p => ((string?)p.Path, p.Operation))
            .ToList();
        if (paths.Count == 0)
        {
            return null;
        }

        var queryCkTypeId = entity.Attributes.TryGetValue("QueryCkTypeId", out var q) ? q as string : null;
        return FindHiddenUsage(ckCache, tenantId, string.IsNullOrWhiteSpace(queryCkTypeId) ? null : queryCkTypeId,
            paths);
    }

    /// <summary>Every path a stored query carries, with what it is used for (mirrors the asset-repo guard).</summary>
    internal static IEnumerable<(string Path, string Operation)> CollectStoredQueryPaths(Func<string, object?> attribute)
    {
        foreach (var path in RecordMembers(attribute("FieldFilter"), "AttributePath"))
        {
            yield return (path, StoredFilterOperation);
        }

        foreach (var path in RecordMembers(attribute("Sorting"), "AttributePath"))
        {
            yield return (path, StoredSortOperation);
        }

        // Columns: a string array (simple queries) or aggregation column records (AttributePath).
        foreach (var path in Strings(attribute("Columns")).Concat(RecordMembers(attribute("Columns"), "AttributePath")))
        {
            yield return (path, StoredColumnOperation);
        }

        foreach (var path in Strings(attribute("GroupingColumns")))
        {
            yield return (path, StoredGroupByOperation);
        }

        if (attribute("AttributeSearchFilter") is RtRecord search)
        {
            foreach (var path in Strings(Member(search, "AttributePaths")))
            {
                yield return (path, StoredAttributeSearchOperation);
            }
        }
    }

    private static bool IsPersistentQuery(ICkCacheService ckCache, string tenantId, RtCkId<CkTypeId> ckTypeId) =>
        ckCache.TryGetRtCkType(tenantId, ckTypeId, out var graph) && graph != null &&
        graph.GetBaseTypes(true).Any(t => t.ToRtCkId() == SystemCkIds.RtCkPersistentQueryTypeId);

    private static List<RtCkId<CkTypeId>> ResolveRoots(ICkCacheService ckCache, string tenantId, string? ckTypeId)
    {
        var roots = new List<RtCkId<CkTypeId>>();
        var rtCkTypeId = ckTypeId == null ? null : TryParse(ckTypeId);
        if (rtCkTypeId == null || !ckCache.TryGetRtCkType(tenantId, rtCkTypeId, out var graph) || graph == null)
        {
            return roots; // unknown: checked by name
        }

        roots.Add(graph.CkTypeId.ToRtCkId());
        roots.AddRange(graph.GetAllDerivedTypes(false).Select(d => d.ToRtCkId()));
        return roots;
    }

    private static RtCkId<CkTypeId>? TryParse(string ckTypeId)
    {
        try
        {
            return new RtCkId<CkTypeId>(ckTypeId);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool IsHiddenByName(string path, IReadOnlySet<string> hiddenNames) =>
        hiddenNames.Count > 0 && Segments(path).Any(s => hiddenNames.Contains(s.ToPascalCase()));

    /// <summary>All Hidden attribute names (PascalCase) of the tenant's types and records.</summary>
    private static HashSet<string> HiddenAttributeNames(ICkCacheService ckCache, string tenantId) =>
        ckCache.GetCkTypes(tenantId).SelectMany(t => t.AllAttributes.Values)
            .Concat(ckCache.GetCkRecords(tenantId).SelectMany(r => r.AllAttributes.Values))
            .Where(a => !AttributeAccess.IsReadableOutsideMethod(a.Access))
            .Select(a => a.AttributeName)
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>Removes array indexes and projections (<c>Endpoints[0].Token</c> becomes <c>Endpoints.Token</c>).</summary>
    private static string NormalizePath(string path) =>
        string.Join('.', Segments(path));

    private static IEnumerable<string> Segments(string path) =>
        path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => s.IndexOf('[') is var bracket and >= 0 ? s[..bracket] : s)
            .Where(s => s.Length > 0);

    private static IEnumerable<CkTypeAttributeGraph> ResolveSegments(ICkCacheService ckCache, string tenantId,
        CkTypeWithAttributesGraph root, IReadOnlyList<string> segments)
    {
        CkTypeWithAttributesGraph current = root;
        foreach (var segment in segments)
        {
            var attribute = current.AllAttributesByName.TryGetValue(segment, out var exact)
                ? exact
                : current.AllAttributesByName
                    .FirstOrDefault(kv => string.Equals(kv.Key, segment, StringComparison.OrdinalIgnoreCase)).Value;
            if (attribute == null)
            {
                yield break;
            }

            yield return attribute;

            if (attribute.ValueType is not (AttributeValueTypesDto.Record or AttributeValueTypesDto.RecordArray) ||
                attribute.ValueCkRecordId == null ||
                !ckCache.TryGetCkRecord(tenantId, attribute.ValueCkRecordId, out var record) || record == null)
            {
                yield break;
            }

            current = record;
        }
    }

    private static IEnumerable<string> Strings(object? value) => value switch
    {
        null => [],
        string single => [single],
        IEnumerable items => items.OfType<string>().Where(s => !string.IsNullOrWhiteSpace(s)),
        _ => []
    };

    private static IEnumerable<string> RecordMembers(object? value, string member) => value switch
    {
        RtRecord record => Strings(Member(record, member)),
        IEnumerable items and not string => items.OfType<RtRecord>().SelectMany(r => Strings(Member(r, member))),
        _ => []
    };

    private static object? Member(RtRecord record, string member) =>
        record.Attributes.TryGetValue(member, out var value)
            ? value
            : record.Attributes.FirstOrDefault(kv => string.Equals(kv.Key, member, StringComparison.OrdinalIgnoreCase))
                .Value;
}
