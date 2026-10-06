using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;

namespace Meshmakers.Octo.Backend.McpServices.Services;

/// <summary>
///     Resolves attribute paths against the CK type graph to decide whether they address a
///     <c>Secret</c> attribute (AB#5528/AB#5543). The generic CRUD tools use it to route secret writes to
///     the high-risk <c>set_entity_secrets</c> tool and to map <c>clearSecretAttributes</c> names
///     (camelCase or PascalCase) to the PascalCase attribute names the engine expects.
/// </summary>
/// <remarks>
///     Paths use dot notation through records (<c>Endpoints.Token</c>); array indexes
///     (<c>Endpoints[0].Token</c>) are ignored for the type lookup. An unknown CK type or attribute is
///     reported as "not a secret" — the engine raises the authoritative error when the write runs.
/// </remarks>
internal static class SecretAttributePaths
{
    /// <summary>
    ///     Returns true when <paramref name="path" /> resolves to an attribute whose value type is
    ///     <see cref="AttributeValueTypesDto.Secret" />.
    /// </summary>
    public static bool IsSecretPath(ICkCacheService ckCache, string tenantId, string ckTypeId,
        string path)
    {
        return TryResolve(ckCache, tenantId, ckTypeId, path, out var attribute, out _) &&
               attribute!.ValueType == AttributeValueTypesDto.Secret;
    }

    /// <summary>
    ///     Resolves a top-level attribute name case-insensitively and returns its PascalCase name when it
    ///     exists on the type, otherwise the input unchanged (the engine reports unknown names).
    /// </summary>
    public static string NormaliseTopLevelName(ICkCacheService ckCache, string tenantId,
        string ckTypeId, string name)
    {
        return TryResolve(ckCache, tenantId, ckTypeId, name, out _, out var resolvedPath)
            ? resolvedPath!
            : name;
    }

    /// <summary>
    ///     Pre-validates query usages (filters, aggregations, group-by) before the engine runs: the first usage
    ///     that addresses a Secret attribute yields a <c>SecretAttributeNotQueryable</c> message, otherwise
    ///     <c>null</c>. The engine enforces the same rule; checking up front gives the AI client the error before
    ///     any database round-trip.
    /// </summary>
    /// <param name="ckCache">CK cache</param>
    /// <param name="tenantId">Tenant id</param>
    /// <param name="ckTypeId">The queried CK type</param>
    /// <param name="usages">Attribute paths and a description of how they are used (e.g. "aggregation 'sum'")</param>
    public static string? FindNotQueryableUsage(ICkCacheService ckCache, string tenantId, string ckTypeId,
        IEnumerable<(string? Path, string Operation)> usages)
    {
        foreach (var (path, operation) in usages)
        {
            if (string.IsNullOrWhiteSpace(path) || !IsSecretPath(ckCache, tenantId, ckTypeId, path))
            {
                continue;
            }

            return $"{SecretErrors.NotQueryableCode}: Attribute '{path}' of '{ckTypeId}' is a Secret attribute and " +
                   $"cannot be used for {operation}. Secret attributes only support the filter operators IsNull and " +
                   "IsNotNull; they cannot be sorted, searched, aggregated, grouped or used as query columns.";
        }

        return null;
    }

    private static bool TryResolve(ICkCacheService ckCache, string tenantId, string ckTypeId,
        string path, out CkTypeAttributeGraph? attribute, out string? resolvedPath)
    {
        attribute = null;
        resolvedPath = null;
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        CkTypeWithAttributesGraph? current;
        try
        {
            current = ckCache.GetRtCkType(tenantId, new RtCkId<CkTypeId>(ckTypeId));
        }
        catch
        {
            return false;
        }

        var resolvedSegments = new List<string>();
        var segments = path.Split('.', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < segments.Length; i++)
        {
            if (current == null)
            {
                return false;
            }

            var segment = StripIndex(segments[i]);
            var match = current.AllAttributesByName
                .FirstOrDefault(kv => string.Equals(kv.Key, segment, StringComparison.OrdinalIgnoreCase));
            if (match.Value == null)
            {
                return false;
            }

            resolvedSegments.Add(match.Key);
            if (i == segments.Length - 1)
            {
                attribute = match.Value;
                resolvedPath = string.Join('.', resolvedSegments);
                return true;
            }

            if (match.Value.ValueType is not (AttributeValueTypesDto.Record or AttributeValueTypesDto.RecordArray) ||
                match.Value.ValueCkRecordId == null ||
                !ckCache.TryGetCkRecord(tenantId, match.Value.ValueCkRecordId, out CkRecordGraph? record))
            {
                return false;
            }

            current = record;
        }

        return false;
    }

    private static string StripIndex(string segment)
    {
        var bracket = segment.IndexOf('[');
        return bracket >= 0 ? segment[..bracket] : segment;
    }
}
