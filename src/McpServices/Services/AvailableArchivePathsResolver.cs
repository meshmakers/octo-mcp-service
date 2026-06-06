using Meshmakers.Octo.Backend.McpServices.Models;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;

namespace Meshmakers.Octo.Backend.McpServices.Services;

/// <summary>
///     Walks the CK type/record graph from a starting <c>ckTypeId</c> and emits one
///     <see cref="ArchivePathInfo"/> per reachable attribute path. Backs the
///     <c>get_available_archive_paths</c> MCP tool — the AI-side equivalent of the asset-repo
///     studio's <c>availableArchivePaths</c> GraphQL query (concept §16).
/// </summary>
/// <remarks>
///     <para>
///         Bounded by <c>maxDepth</c> so recursive record structures terminate predictably. The
///         default cap (5) matches the GraphQL resolver and the studio picker. Visited-record
///         tracking prevents infinite recursion on self-referential records (e.g. a tree-shaped
///         record whose child slot points back at the parent record type).
///     </para>
///     <para>
///         For each attribute the resolver emits a single row; for record-typed attributes the row
///         is followed by the record's own attributes prefixed with the parent path. Array
///         attributes (StringArray / IntegerArray / RecordArray) flag <c>IsArray=true</c>, and the
///         flag propagates into the record's children so a downstream consumer can tell apart "this
///         path is a column" from "this path is an element of an array column".
///     </para>
/// </remarks>
internal static class AvailableArchivePathsResolver
{
    /// <summary>Default recursion cap — matches the GraphQL counterpart.</summary>
    public const int DefaultMaxDepth = 5;

    public static List<ArchivePathInfo> Resolve(
        ICkCacheService ckCache, string tenantId, RtCkId<CkTypeId> ckTypeId, int maxDepth)
    {
        if (maxDepth < 1)
        {
            maxDepth = 1;
        }

        var ckType = ckCache.GetRtCkType(tenantId, ckTypeId);

        var results = new List<ArchivePathInfo>();
        var visitedRecords = new HashSet<CkId<CkRecordId>>();

        foreach (var (name, attribute) in ckType.AllAttributesByName)
        {
            Walk(ckCache, tenantId, name, attribute,
                isInsideArray: false, depth: 1, maxDepth, visitedRecords, results);
        }

        return results;
    }

    private static void Walk(
        ICkCacheService ckCache,
        string tenantId,
        string path,
        CkTypeAttributeGraph attribute,
        bool isInsideArray,
        int depth,
        int maxDepth,
        HashSet<CkId<CkRecordId>> visitedRecords,
        List<ArchivePathInfo> sink)
    {
        var isArray = isInsideArray
                      || attribute.ValueType is AttributeValueTypesDto.RecordArray
                                             or AttributeValueTypesDto.StringArray
                                             or AttributeValueTypesDto.IntegerArray;

        if (attribute.ValueType is AttributeValueTypesDto.Record
                                or AttributeValueTypesDto.RecordArray)
        {
            var recordId = attribute.ValueCkRecordId;
            sink.Add(new ArchivePathInfo(
                Path: path,
                PrimitiveType: null,
                IsRecord: true,
                IsArray: isArray,
                RecordTypeId: recordId?.ToString()));

            if (recordId == null || depth >= maxDepth || !visitedRecords.Add(recordId))
            {
                return;
            }

            if (!ckCache.TryGetCkRecord(tenantId, recordId, out var recordGraph) || recordGraph == null)
            {
                visitedRecords.Remove(recordId);
                return;
            }

            foreach (var (childName, childAttribute) in recordGraph.AllAttributesByName)
            {
                Walk(ckCache, tenantId, $"{path}.{childName}", childAttribute,
                    isInsideArray: isArray, depth + 1, maxDepth, visitedRecords, sink);
            }

            visitedRecords.Remove(recordId);
            return;
        }

        sink.Add(new ArchivePathInfo(
            Path: path,
            PrimitiveType: attribute.ValueType.ToString(),
            IsRecord: false,
            IsArray: isArray,
            RecordTypeId: null));
    }
}
