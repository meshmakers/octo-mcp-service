using System.ComponentModel;
using Meshmakers.Octo.Backend.McpServices.Models.Aggregation;
using Meshmakers.Octo.Backend.McpServices.Models.Filters;
using Meshmakers.Octo.Backend.McpServices.Services;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using ModelContextProtocol.Server;

// ReSharper disable UnusedMember.Global

namespace Meshmakers.Octo.Backend.McpServices.Tools;

/// <summary>
///     Time-series stream-data query tools. Wraps <see cref="IStreamDataRepository"/> against a
///     CkArchive runtime id; the target CK type is resolved from the archive snapshot so callers
///     don't have to repeat it. Mirrors the four GraphQL StreamData.TransientStreamDataQuery
///     sub-queries (Simple, Aggregation, GroupingAggregation, Downsampling).
/// </summary>
[McpServerToolType]
public sealed class StreamDataAggregationTools
{
    /// <summary>Raw time-series values from an archive — column projection + optional filters.</summary>
    [McpServerTool(Name = "query_stream_data_simple")]
    [Description(
        "Read raw time-series rows from an archive. Returns the requested column paths plus per-row " +
        "metadata (rtId, timestamp, etc.) for each point in [from, to). Use this for individual sensor " +
        "readings; use _aggregation / _downsampling for summary statistics. Equivalent to GraphQL " +
        "StreamData.TransientStreamDataQuery.Simple.")]
    public static async Task<StreamDataResultResponse> QuerySimple(
        McpServer server,
        [Description("Archive runtime id.")] string archiveRtId,
        [Description("Attribute paths (columns) to project from the archive.")]
        List<string> columnPaths,
        [Description("Optional start of time range (UTC).")] DateTime? from = null,
        [Description("Optional end of time range (UTC).")] DateTime? to = null,
        [Description("Optional cap on number of rows.")] int? limit = null,
        [Description("Optional sort columns.")] List<SortColumnDto>? sort = null,
        [Description("Optional filter (And/Or with nested support).")] FieldFilterCriteriaDto? filters = null,
        [Description("Optional list of source-entity rtIds to restrict to.")] List<string>? sourceRtIds = null,
        [Description("Tenant id. Falls back to URL route.")] string? tenantId = null)
    {
        if (string.IsNullOrWhiteSpace(archiveRtId))
        {
            return new StreamDataResultResponse { IsSuccess = false, ErrorMessage = "archiveRtId is required." };
        }

        if (columnPaths == null || columnPaths.Count == 0)
        {
            return new StreamDataResultResponse
            {
                IsSuccess = false,
                ErrorMessage = "columnPaths must contain at least one path."
            };
        }

        var ctx = await StreamDataContext.TryResolveAsync(server, archiveRtId, tenantId);
        if (ctx.Error != null)
        {
            return new StreamDataResultResponse { IsSuccess = false, ErrorMessage = ctx.Error };
        }

        try
        {
            var options = StreamDataQueryOptions.Create()
                .WithCkTypeId(ctx.CkTypeId!)
                .WithColumns(columnPaths)
                .WithRtIds(MapRtIds(sourceRtIds))
                .WithTimeRange(from, to)
                .WithLimit(limit)
                .WithSortOrders(MapSortOrders(sort))
                .WithFieldFilters(MapFieldFilters(filters));

            var result = await ctx.Repo!.ExecuteQueryAsync(new OctoObjectId(archiveRtId), options);

            return new StreamDataResultResponse
            {
                IsSuccess = true,
                TenantId = ctx.TenantId,
                ArchiveRtId = archiveRtId,
                Rows = result.Rows.Select(MapRow).ToList(),
                TotalCount = result.TotalCount
            };
        }
        catch (Exception ex)
        {
            return new StreamDataResultResponse { IsSuccess = false, ErrorMessage = ex.Message };
        }
    }

    /// <summary>Aggregate over a time-series archive without grouping.</summary>
    [McpServerTool(Name = "query_stream_data_aggregation")]
    [Description(
        "Compute scalar aggregations over the rows in [from, to). Returns one row with the requested " +
        "aliases. Equivalent to GraphQL StreamData.TransientStreamDataQuery.Aggregation.")]
    public static async Task<AggregationResultResponse> QueryAggregation(
        McpServer server,
        string archiveRtId,
        List<AggregationColumnDto> aggregations,
        [Description("Optional start of time range (UTC).")] DateTime? from = null,
        [Description("Optional end of time range (UTC).")] DateTime? to = null,
        FieldFilterCriteriaDto? filters = null,
        List<string>? sourceRtIds = null,
        string? tenantId = null)
    {
        var validation = ValidateAggregationCall(archiveRtId, aggregations, groupByPaths: null);
        if (validation != null)
        {
            return new AggregationResultResponse { IsSuccess = false, ErrorMessage = validation };
        }

        var ctx = await StreamDataContext.TryResolveAsync(server, archiveRtId, tenantId);
        if (ctx.Error != null)
        {
            return new AggregationResultResponse { IsSuccess = false, ErrorMessage = ctx.Error };
        }

        try
        {
            var options = StreamDataAggregationQueryOptions.Create()
                .WithCkTypeId(ctx.CkTypeId!)
                .WithAggregationColumns(AggregationMapper.ToEngineColumns(aggregations))
                .WithRtIds(MapRtIds(sourceRtIds))
                .WithTimeRange(from, to)
                .WithFieldFilters(MapFieldFilters(filters));

            var result = await ctx.Repo!.ExecuteAggregationQueryAsync(
                new OctoObjectId(archiveRtId), options);

            var rows = ProjectStreamAggregationRows(result, aggregations, groupByPaths: null);

            return new AggregationResultResponse
            {
                IsSuccess = true,
                TenantId = ctx.TenantId,
                Rows = rows,
                RowCount = rows.Count
            };
        }
        catch (Exception ex)
        {
            return new AggregationResultResponse { IsSuccess = false, ErrorMessage = ex.Message };
        }
    }

    /// <summary>Aggregate over a time-series archive, grouped by attribute paths.</summary>
    [McpServerTool(Name = "query_stream_data_grouping")]
    [Description(
        "Compute aggregations grouped by attribute paths. Returns one row per distinct group with the " +
        "group-key columns plus the aggregation aliases. Equivalent to GraphQL " +
        "StreamData.TransientStreamDataQuery.GroupingAggregation.")]
    public static async Task<AggregationResultResponse> QueryGrouping(
        McpServer server,
        string archiveRtId,
        List<string> groupByAttributePaths,
        List<AggregationColumnDto> aggregations,
        DateTime? from = null,
        DateTime? to = null,
        FieldFilterCriteriaDto? filters = null,
        List<string>? sourceRtIds = null,
        string? tenantId = null)
    {
        var validation = ValidateAggregationCall(archiveRtId, aggregations, groupByAttributePaths);
        if (validation != null)
        {
            return new AggregationResultResponse { IsSuccess = false, ErrorMessage = validation };
        }

        var ctx = await StreamDataContext.TryResolveAsync(server, archiveRtId, tenantId);
        if (ctx.Error != null)
        {
            return new AggregationResultResponse { IsSuccess = false, ErrorMessage = ctx.Error };
        }

        try
        {
            var options = StreamDataGroupedAggregationQueryOptions.Create()
                .WithCkTypeId(ctx.CkTypeId!)
                .WithGroupByColumns(groupByAttributePaths)
                .WithAggregationColumns(AggregationMapper.ToEngineColumns(aggregations))
                .WithRtIds(MapRtIds(sourceRtIds))
                .WithTimeRange(from, to)
                .WithFieldFilters(MapFieldFilters(filters));

            var result = await ctx.Repo!.ExecuteGroupedAggregationQueryAsync(
                new OctoObjectId(archiveRtId), options);

            var rows = ProjectStreamAggregationRows(result, aggregations, groupByAttributePaths);

            return new AggregationResultResponse
            {
                IsSuccess = true,
                TenantId = ctx.TenantId,
                Rows = rows,
                RowCount = rows.Count
            };
        }
        catch (Exception ex)
        {
            return new AggregationResultResponse { IsSuccess = false, ErrorMessage = ex.Message };
        }
    }

    /// <summary>Time-bucketed aggregation over a time-series archive.</summary>
    [McpServerTool(Name = "query_stream_data_downsampling")]
    [Description(
        "Aggregate the [from, to) range into <= limit equally-spaced time buckets. Each output row " +
        "carries the aggregation aliases for that bucket — the server picks the bucket size. " +
        "Equivalent to GraphQL StreamData.TransientStreamDataQuery.Downsampling.")]
    public static async Task<DownsamplingResultResponse> QueryDownsampling(
        McpServer server,
        string archiveRtId,
        List<AggregationColumnDto> aggregations,
        [Description("Start of time range (UTC), required.")] DateTime from,
        [Description("End of time range (UTC), required.")] DateTime to,
        [Description("Maximum number of output buckets; server picks bin size from this.")] int limit = 100,
        FieldFilterCriteriaDto? filters = null,
        List<string>? sourceRtIds = null,
        string? tenantId = null)
    {
        if (string.IsNullOrWhiteSpace(archiveRtId))
        {
            return new DownsamplingResultResponse { IsSuccess = false, ErrorMessage = "archiveRtId is required." };
        }

        var aggError = AggregationMapper.Validate(aggregations);
        if (aggError != null)
        {
            return new DownsamplingResultResponse { IsSuccess = false, ErrorMessage = aggError };
        }

        if (from >= to)
        {
            return new DownsamplingResultResponse
            {
                IsSuccess = false,
                ErrorMessage = $"Invalid time window: from ({from:O}) must be strictly less than to ({to:O})."
            };
        }

        if (limit <= 0)
        {
            return new DownsamplingResultResponse
            {
                IsSuccess = false,
                ErrorMessage = "limit must be > 0."
            };
        }

        var ctx = await StreamDataContext.TryResolveAsync(server, archiveRtId, tenantId);
        if (ctx.Error != null)
        {
            return new DownsamplingResultResponse { IsSuccess = false, ErrorMessage = ctx.Error };
        }

        try
        {
            var options = StreamDataDownsamplingQueryOptions.Create()
                .WithCkTypeId(ctx.CkTypeId!)
                .WithAggregationColumns(AggregationMapper.ToEngineColumns(aggregations))
                .WithTimeRange(from, to)
                .WithLimit(limit)
                .WithRtIds(MapRtIds(sourceRtIds))
                .WithFieldFilters(MapFieldFilters(filters));

            var result = await ctx.Repo!.ExecuteDownsamplingQueryAsync(
                new OctoObjectId(archiveRtId), options);

            var rows = result.Rows.Select(r => BuildBucketRow(r, aggregations)).ToList();

            return new DownsamplingResultResponse
            {
                IsSuccess = true,
                TenantId = ctx.TenantId,
                ArchiveRtId = archiveRtId,
                Rows = rows,
                RowCount = rows.Count
            };
        }
        catch (Exception ex)
        {
            return new DownsamplingResultResponse { IsSuccess = false, ErrorMessage = ex.Message };
        }
    }

    // ── Shared validation + projection helpers ──────────────────────────────

    private static string? ValidateAggregationCall(string? archiveRtId,
        IReadOnlyList<AggregationColumnDto>? aggregations,
        IReadOnlyList<string>? groupByPaths)
    {
        if (string.IsNullOrWhiteSpace(archiveRtId))
        {
            return "archiveRtId is required.";
        }

        var aggError = AggregationMapper.Validate(aggregations);
        if (aggError != null)
        {
            return aggError;
        }

        if (groupByPaths != null)
        {
            return AggregationMapper.ValidateGroupBy(groupByPaths);
        }

        return null;
    }

    private static List<Dictionary<string, object?>> ProjectStreamAggregationRows(
        StreamDataQueryResult result,
        IReadOnlyList<AggregationColumnDto> aggregations,
        IReadOnlyList<string>? groupByPaths)
    {
        // For stream-data aggregations the engine returns one StreamDataRow per group (or one row
        // total for non-grouped). The values dictionary is keyed by the engine's column name
        // (`{Function}({path})` — see AggregationColumn.ToString). Look up by that key, write under
        // the MCP alias. Group keys are projected into the row from groupByPaths.

        return result.Rows.Select(row =>
        {
            var dict = new Dictionary<string, object?>();

            if (groupByPaths != null)
            {
                foreach (var path in groupByPaths)
                {
                    dict[path] = row.Values.TryGetValue(path, out var v) ? v : null;
                }
            }

            foreach (var col in aggregations)
            {
                var alias = AggregationMapper.DeriveAlias(col);
                var key = EngineColumnKey(col);
                dict[alias] = row.Values.TryGetValue(key, out var v) ? v : null;
            }

            return dict;
        }).ToList();
    }

    private static Dictionary<string, object?> BuildBucketRow(StreamDataRow row,
        IReadOnlyList<AggregationColumnDto> aggregations)
    {
        var dict = new Dictionary<string, object?>
        {
            ["bucketStart"] = row.Timestamp
        };

        foreach (var col in aggregations)
        {
            var alias = AggregationMapper.DeriveAlias(col);
            var key = EngineColumnKey(col);
            dict[alias] = row.Values.TryGetValue(key, out var v) ? v : null;
        }

        return dict;
    }

    /// <summary>
    ///     Engine column naming convention: <c>{Function}({attributePath})</c> from
    ///     <see cref="AggregationColumn.ToString" />. Substitute placeholder <c>*</c> for count without path.
    /// </summary>
    private static string EngineColumnKey(AggregationColumnDto col)
    {
        var fn = AggregationMapper.ToEngineFunction(col.Function);
        var path = string.IsNullOrWhiteSpace(col.AttributePath) ? "*" : col.AttributePath;
        return $"{fn}({path})";
    }

    private static StreamDataRowResponse MapRow(StreamDataRow row) => new()
    {
        RtId = row.RtId?.ToString(),
        CkTypeId = row.CkTypeId?.FullName,
        Timestamp = row.Timestamp,
        RtWellKnownName = row.RtWellKnownName,
        Values = new Dictionary<string, object?>(row.Values)
    };

    private static IReadOnlyList<OctoObjectId>? MapRtIds(IReadOnlyList<string>? ids) =>
        ids == null || ids.Count == 0
            ? null
            : ids.Select(s => new OctoObjectId(s)).ToList();

    private static IReadOnlyList<SortOrderItem>? MapSortOrders(IReadOnlyList<SortColumnDto>? sort) =>
        sort == null || sort.Count == 0
            ? null
            : sort.Select(s => new SortOrderItem(
                    s.AttributePath,
                    s.Direction == SortDirectionDto.desc ? SortOrders.Descending : SortOrders.Ascending))
                .ToList();

    private static IReadOnlyList<FieldFilter>? MapFieldFilters(FieldFilterCriteriaDto? criteria)
    {
        if (criteria == null || criteria.Fields == null || criteria.Fields.Count == 0)
        {
            return null;
        }

        return criteria.Fields.Select(f => new FieldFilter(
                f.AttributePath,
                MapFilterOperator(f.Operator),
                f.Value,
                f.SecondValue))
            .ToList();
    }

    private static FieldFilterOperator MapFilterOperator(FilterOperatorDto op) => op switch
    {
        FilterOperatorDto.Equals => FieldFilterOperator.Equals,
        FilterOperatorDto.NotEquals => FieldFilterOperator.NotEquals,
        FilterOperatorDto.GreaterThan => FieldFilterOperator.GreaterThan,
        FilterOperatorDto.GreaterThanOrEqual => FieldFilterOperator.GreaterEqualThan,
        FilterOperatorDto.LessThan => FieldFilterOperator.LessThan,
        FilterOperatorDto.LessThanOrEqual => FieldFilterOperator.LessEqualThan,
        FilterOperatorDto.In => FieldFilterOperator.In,
        FilterOperatorDto.NotIn => FieldFilterOperator.NotIn,
        FilterOperatorDto.Between => FieldFilterOperator.Between,
        _ => FieldFilterOperator.Equals
    };
}

/// <summary>
///     Resolves the <see cref="IStreamDataRepository" />, archive snapshot, and target CK type id for a
///     given archive runtime id and MCP session. Encapsulates the cascade of nullable accessors
///     (StreamData not enabled, archive doesn't exist, …) into a single result.
/// </summary>
internal sealed record StreamDataContext(
    IStreamDataRepository? Repo,
    RtCkId<CkTypeId>? CkTypeId,
    string? TenantId,
    string? Error)
{
    public static async Task<StreamDataContext> TryResolveAsync(
        McpServer server, string archiveRtId, string? tenantIdParam)
    {
        try
        {
            var tenantResolution = server.Services!.GetRequiredService<ITenantResolutionService>();
            var ctx = await tenantResolution.GetTenantContextAsync(tenantIdParam);

            var streamRepo = ctx.GetStreamDataRepository();
            if (streamRepo == null)
            {
                return new StreamDataContext(null, null, null,
                    "Stream data is not enabled for this tenant. Use enable_stream_data first.");
            }

            var archiveStore = ctx.GetArchiveRuntimeStore();
            var snapshot = await archiveStore.GetAsync(new OctoObjectId(archiveRtId));
            if (snapshot == null)
            {
                return new StreamDataContext(null, null, null,
                    $"Archive '{archiveRtId}' not found.");
            }

            return new StreamDataContext(streamRepo, snapshot.TargetCkTypeId, ctx.TenantId, null);
        }
        catch (Exception ex)
        {
            return new StreamDataContext(null, null, null, ex.Message);
        }
    }
}
