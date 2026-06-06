using System.ComponentModel;
using Meshmakers.Octo.Backend.McpServices.Models.Aggregation;
using Meshmakers.Octo.Backend.McpServices.Models.Filters;
using Meshmakers.Octo.Backend.McpServices.Services;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v2;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
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
    /// <summary>Execute a persisted stream-data query (RtStreamDataQuery) by RtId.</summary>
    [McpServerTool(Name = "execute_stream_data_query")]
    [Description(
        "Execute a persisted stream-data query by RtId. Loads the RtStreamDataQuery entity, reads its " +
        "ArchiveRtId, and dispatches on the CK subtype: RtSimpleSdQuery returns raw time-series rows, " +
        "RtAggregationSdQuery / RtGroupingAggregationSdQuery return scalar / grouped aggregations, and " +
        "RtDownsamplingSdQuery returns time-bucketed rows. Optional from/to/limit/sourceRtIds and " +
        "extraFilters override the persisted defaults; extraFilters AND-combine. Mirrors GraphQL " +
        "StreamData.StreamDataQuery.")]
    public static async Task<PersistedStreamDataQueryResponse> ExecuteStreamDataQuery(
        McpServer server,
        [Description("Runtime id of the persisted RtStreamDataQuery entity to execute.")] string queryRtId,
        [Description("Optional override of start time (UTC).")] DateTime? fromOverride = null,
        [Description("Optional override of end time (UTC).")] DateTime? toOverride = null,
        [Description("Optional override of row / bucket limit.")] int? limitOverride = null,
        [Description("Optional override of source rtIds.")] List<string>? sourceRtIdsOverride = null,
        [Description("Optional additional field filters AND-combined with the persisted filters.")]
        FieldFilterCriteriaDto? extraFilters = null,
        [Description("Tenant id. Falls back to URL route.")] string? tenantId = null)
    {
        if (string.IsNullOrWhiteSpace(queryRtId))
        {
            return new PersistedStreamDataQueryResponse
            {
                IsSuccess = false,
                ErrorMessage = "queryRtId is required."
            };
        }

        var tenantResolution = server.Services!.GetRequiredService<ITenantResolutionService>();
        var tenantRepository = await tenantResolution.GetTenantRepositoryAsync(tenantId);
        var tenantContext = await tenantResolution.GetTenantContextAsync(tenantId);
        var resolvedTenantId = tenantRepository.TenantId;

        var streamRepo = tenantContext.GetStreamDataRepository();
        if (streamRepo == null)
        {
            return new PersistedStreamDataQueryResponse
            {
                IsSuccess = false,
                ErrorMessage = "Stream data is not enabled for this tenant. Use enable_stream_data first.",
                QueryRtId = queryRtId,
                TenantId = resolvedTenantId
            };
        }

        using var session = await tenantRepository.GetSessionAsync();
        session.StartTransaction();

        try
        {
            var queryId = new OctoObjectId(queryRtId);
            var loaded = await tenantRepository.GetRtEntityByRtIdAsync<RtStreamDataQuery>(session, queryId);
            if (loaded == null)
            {
                return new PersistedStreamDataQueryResponse
                {
                    IsSuccess = false,
                    ErrorMessage = $"Persisted stream-data query '{queryRtId}' not found.",
                    QueryRtId = queryRtId,
                    TenantId = resolvedTenantId
                };
            }

            if (string.IsNullOrWhiteSpace(loaded.ArchiveRtId))
            {
                return new PersistedStreamDataQueryResponse
                {
                    IsSuccess = false,
                    ErrorMessage = $"Persisted stream-data query '{queryRtId}' is missing ArchiveRtId.",
                    QueryRtId = queryRtId,
                    QuerySubtype = loaded.GetType().Name,
                    TenantId = resolvedTenantId
                };
            }

            var archiveRtId = new OctoObjectId(loaded.ArchiveRtId);
            var ckTypeId = loaded.QueryCkTypeId;
            var extraMappedFilters = MapFieldFilters(extraFilters);
            var sourceRtIdOverrideList = MapRtIds(sourceRtIdsOverride);

            return loaded switch
            {
                RtSimpleSdQuery simple => await ExecutePersistedSimpleAsync(
                    simple, streamRepo, archiveRtId, ckTypeId, fromOverride, toOverride, limitOverride,
                    sourceRtIdOverrideList, extraMappedFilters, queryRtId, resolvedTenantId),
                RtAggregationSdQuery aggregation => await ExecutePersistedAggregationAsync(
                    aggregation, streamRepo, archiveRtId, ckTypeId, fromOverride, toOverride,
                    sourceRtIdOverrideList, extraMappedFilters, queryRtId, resolvedTenantId),
                RtGroupingAggregationSdQuery grouping => await ExecutePersistedGroupingAsync(
                    grouping, streamRepo, archiveRtId, ckTypeId, fromOverride, toOverride,
                    sourceRtIdOverrideList, extraMappedFilters, queryRtId, resolvedTenantId),
                RtDownsamplingSdQuery downsampling => await ExecutePersistedDownsamplingAsync(
                    downsampling, streamRepo, archiveRtId, ckTypeId, fromOverride, toOverride,
                    limitOverride, sourceRtIdOverrideList, extraMappedFilters, queryRtId, resolvedTenantId),
                _ => new PersistedStreamDataQueryResponse
                {
                    IsSuccess = false,
                    ErrorMessage = $"Unknown persisted stream-data query subtype: {loaded.GetType().Name}",
                    QueryRtId = queryRtId,
                    QuerySubtype = loaded.GetType().Name,
                    ArchiveRtId = loaded.ArchiveRtId,
                    TenantId = resolvedTenantId
                }
            };
        }
        catch (Exception ex)
        {
            return new PersistedStreamDataQueryResponse
            {
                IsSuccess = false,
                ErrorMessage = ex.Message,
                QueryRtId = queryRtId,
                TenantId = resolvedTenantId
            };
        }
    }

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

    // ── Persisted stream-data executors ──────────────────────────────────────────────────────

    private static async Task<PersistedStreamDataQueryResponse> ExecutePersistedSimpleAsync(
        RtSimpleSdQuery query,
        IStreamDataRepository streamRepo,
        OctoObjectId archiveRtId,
        RtCkId<CkTypeId> ckTypeId,
        DateTime? fromOverride,
        DateTime? toOverride,
        int? limitOverride,
        IReadOnlyList<OctoObjectId>? sourceRtIdsOverride,
        IReadOnlyList<FieldFilter>? extraFilters,
        string queryRtId,
        string resolvedTenantId)
    {
        var columnPaths = query.Columns?.ToList() ?? [];
        var rtIds = sourceRtIdsOverride
                   ?? query.RtIds?.Select(id => new OctoObjectId(id)).ToList();
        var sortOrders = query.Sorting?
            .Select(s => new SortOrderItem(s.AttributePath, (SortOrders)(int)s.SortOrder))
            .ToList();

        var options = StreamDataQueryOptions.Create()
            .WithCkTypeId(ckTypeId)
            .WithColumns(columnPaths)
            .WithRtIds(rtIds)
            .WithTimeRange(fromOverride ?? query.From, toOverride ?? query.To)
            .WithLimit(limitOverride ?? (query.Limit.HasValue ? (int)query.Limit.Value : null))
            .WithSortOrders(sortOrders)
            .WithFieldFilters(MergePersistedAndExtraFilters(query.FieldFilter, extraFilters));

        var result = await streamRepo.ExecuteQueryAsync(archiveRtId, options);

        var streamRows = result.Rows.Select(MapRow).ToList();
        return new PersistedStreamDataQueryResponse
        {
            IsSuccess = true,
            TenantId = resolvedTenantId,
            QueryRtId = queryRtId,
            QuerySubtype = nameof(RtSimpleSdQuery),
            ArchiveRtId = archiveRtId.ToString(),
            CkTypeId = ckTypeId.ToString(),
            StreamRows = streamRows,
            RowCount = streamRows.Count,
            TotalCount = result.TotalCount
        };
    }

    private static async Task<PersistedStreamDataQueryResponse> ExecutePersistedAggregationAsync(
        RtAggregationSdQuery query,
        IStreamDataRepository streamRepo,
        OctoObjectId archiveRtId,
        RtCkId<CkTypeId> ckTypeId,
        DateTime? fromOverride,
        DateTime? toOverride,
        IReadOnlyList<OctoObjectId>? sourceRtIdsOverride,
        IReadOnlyList<FieldFilter>? extraFilters,
        string queryRtId,
        string resolvedTenantId)
    {
        var columns = RuntimeAggregationTools.MapPersistedAggregationColumns(query.Columns);
        var rtIds = sourceRtIdsOverride
                   ?? query.RtIds?.Select(id => new OctoObjectId(id)).ToList();

        var options = StreamDataAggregationQueryOptions.Create()
            .WithCkTypeId(ckTypeId)
            .WithAggregationColumns(AggregationMapper.ToEngineColumns(columns))
            .WithRtIds(rtIds)
            .WithTimeRange(fromOverride ?? query.From, toOverride ?? query.To)
            .WithFieldFilters(MergePersistedAndExtraFilters(query.FieldFilter, extraFilters));

        var result = await streamRepo.ExecuteAggregationQueryAsync(archiveRtId, options);
        var rows = ProjectStreamAggregationRows(result, columns, groupByPaths: null);

        return new PersistedStreamDataQueryResponse
        {
            IsSuccess = true,
            TenantId = resolvedTenantId,
            QueryRtId = queryRtId,
            QuerySubtype = nameof(RtAggregationSdQuery),
            ArchiveRtId = archiveRtId.ToString(),
            CkTypeId = ckTypeId.ToString(),
            Rows = rows,
            RowCount = rows.Count
        };
    }

    private static async Task<PersistedStreamDataQueryResponse> ExecutePersistedGroupingAsync(
        RtGroupingAggregationSdQuery query,
        IStreamDataRepository streamRepo,
        OctoObjectId archiveRtId,
        RtCkId<CkTypeId> ckTypeId,
        DateTime? fromOverride,
        DateTime? toOverride,
        IReadOnlyList<OctoObjectId>? sourceRtIdsOverride,
        IReadOnlyList<FieldFilter>? extraFilters,
        string queryRtId,
        string resolvedTenantId)
    {
        var groupBy = query.GroupingColumns?.ToList() ?? [];
        if (groupBy.Count == 0)
        {
            return new PersistedStreamDataQueryResponse
            {
                IsSuccess = false,
                ErrorMessage = "Persisted grouping aggregation query has no GroupingColumns.",
                QueryRtId = queryRtId,
                QuerySubtype = nameof(RtGroupingAggregationSdQuery),
                ArchiveRtId = archiveRtId.ToString(),
                CkTypeId = ckTypeId.ToString(),
                TenantId = resolvedTenantId
            };
        }

        var columns = RuntimeAggregationTools.MapPersistedAggregationColumns(query.Columns);
        var rtIds = sourceRtIdsOverride
                   ?? query.RtIds?.Select(id => new OctoObjectId(id)).ToList();

        var options = StreamDataGroupedAggregationQueryOptions.Create()
            .WithCkTypeId(ckTypeId)
            .WithGroupByColumns(groupBy)
            .WithAggregationColumns(AggregationMapper.ToEngineColumns(columns))
            .WithRtIds(rtIds)
            .WithTimeRange(fromOverride ?? query.From, toOverride ?? query.To)
            .WithFieldFilters(MergePersistedAndExtraFilters(query.FieldFilter, extraFilters));

        var result = await streamRepo.ExecuteGroupedAggregationQueryAsync(archiveRtId, options);
        var rows = ProjectStreamAggregationRows(result, columns, groupBy);

        return new PersistedStreamDataQueryResponse
        {
            IsSuccess = true,
            TenantId = resolvedTenantId,
            QueryRtId = queryRtId,
            QuerySubtype = nameof(RtGroupingAggregationSdQuery),
            ArchiveRtId = archiveRtId.ToString(),
            CkTypeId = ckTypeId.ToString(),
            Rows = rows,
            RowCount = rows.Count
        };
    }

    private static async Task<PersistedStreamDataQueryResponse> ExecutePersistedDownsamplingAsync(
        RtDownsamplingSdQuery query,
        IStreamDataRepository streamRepo,
        OctoObjectId archiveRtId,
        RtCkId<CkTypeId> ckTypeId,
        DateTime? fromOverride,
        DateTime? toOverride,
        int? limitOverride,
        IReadOnlyList<OctoObjectId>? sourceRtIdsOverride,
        IReadOnlyList<FieldFilter>? extraFilters,
        string queryRtId,
        string resolvedTenantId)
    {
        var effectiveFrom = fromOverride ?? query.From;
        var effectiveTo = toOverride ?? query.To;
        var effectiveLimit = limitOverride
                              ?? (query.Limit.HasValue ? (int)query.Limit.Value : (int?)null);

        // Downsampling has strict input invariants — the engine enforces from < to and limit > 0.
        // Surface them upfront so the caller gets an actionable error rather than an engine exception.
        if (!effectiveFrom.HasValue || !effectiveTo.HasValue || effectiveFrom >= effectiveTo)
        {
            return new PersistedStreamDataQueryResponse
            {
                IsSuccess = false,
                ErrorMessage = "Downsampling requires from < to (either persisted or overridden).",
                QueryRtId = queryRtId,
                QuerySubtype = nameof(RtDownsamplingSdQuery),
                ArchiveRtId = archiveRtId.ToString(),
                CkTypeId = ckTypeId.ToString(),
                TenantId = resolvedTenantId
            };
        }

        if (!effectiveLimit.HasValue || effectiveLimit.Value <= 0)
        {
            return new PersistedStreamDataQueryResponse
            {
                IsSuccess = false,
                ErrorMessage = "Downsampling requires a positive bucket limit (either persisted or overridden).",
                QueryRtId = queryRtId,
                QuerySubtype = nameof(RtDownsamplingSdQuery),
                ArchiveRtId = archiveRtId.ToString(),
                CkTypeId = ckTypeId.ToString(),
                TenantId = resolvedTenantId
            };
        }

        var columns = RuntimeAggregationTools.MapPersistedAggregationColumns(query.Columns);
        var rtIds = sourceRtIdsOverride
                   ?? query.RtIds?.Select(id => new OctoObjectId(id)).ToList();

        var options = StreamDataDownsamplingQueryOptions.Create()
            .WithCkTypeId(ckTypeId)
            .WithAggregationColumns(AggregationMapper.ToEngineColumns(columns))
            .WithTimeRange(effectiveFrom, effectiveTo)
            .WithLimit(effectiveLimit.Value)
            .WithRtIds(rtIds)
            .WithFieldFilters(MergePersistedAndExtraFilters(query.FieldFilter, extraFilters));

        var result = await streamRepo.ExecuteDownsamplingQueryAsync(archiveRtId, options);
        var rows = result.Rows.Select(r => BuildBucketRow(r, columns)).ToList();

        return new PersistedStreamDataQueryResponse
        {
            IsSuccess = true,
            TenantId = resolvedTenantId,
            QueryRtId = queryRtId,
            QuerySubtype = nameof(RtDownsamplingSdQuery),
            ArchiveRtId = archiveRtId.ToString(),
            CkTypeId = ckTypeId.ToString(),
            Rows = rows,
            RowCount = rows.Count
        };
    }

    /// <summary>
    ///     Maps persisted CK field-filter records to engine <see cref="FieldFilter"/> records and concats
    ///     them with the runtime override filters (AND-combined per the persisted-query spec).
    /// </summary>
    private static IReadOnlyList<FieldFilter>? MergePersistedAndExtraFilters(
        IEnumerable<RtFieldFilterRecord>? persistedFilters,
        IReadOnlyList<FieldFilter>? extraFilters)
    {
        var mapped = persistedFilters?
            .Select(f => new FieldFilter(
                f.AttributePath,
                (FieldFilterOperator)(int)f.Operator,
                f.ComparisonValue,
                null))
            .ToList();

        if (mapped is null || mapped.Count == 0)
        {
            return extraFilters;
        }

        if (extraFilters is null || extraFilters.Count == 0)
        {
            return mapped;
        }

        return mapped.Concat(extraFilters).ToList();
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
