using System.ComponentModel;
using Meshmakers.Octo.Backend.McpServices.Models.Aggregation;
using Meshmakers.Octo.Backend.McpServices.Services;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Runtime.Engine.CrateDb;
using Meshmakers.Octo.Runtime.Engine.StreamData;
using ModelContextProtocol.Server;

// ReSharper disable UnusedMember.Global

namespace Meshmakers.Octo.Backend.McpServices.Tools;

/// <summary>
///     Read-only metadata tools for archives + rollups: storage stats and rollup query metadata.
///     These are the read complements to the lifecycle tools in <see cref="TimeSeriesTools" />.
/// </summary>
[McpServerToolType]
public sealed class StreamDataMetadataTools
{
    /// <summary>Bulk-fetch storage stats for one or more archives.</summary>
    [McpServerTool(Name = "get_archive_storage_stats")]
    [Description(
        "Bulk-fetch per-archive storage stats (row count, on-disk size, health). One round-trip per call. " +
        "Archives whose backing table doesn't exist yet (not activated, post-delete) come back with " +
        "tableExists=false and zero counters — the caller doesn't have to pre-filter. Equivalent to GraphQL " +
        "StreamData.archivesStorageStats.")]
    public static async Task<ArchiveStorageStatsResponse> GetArchiveStorageStats(
        McpServer server,
        [Description("Archive runtime ids to fetch stats for.")] List<string> archiveRtIds,
        [Description("Tenant id. Falls back to URL route.")] string? tenantId = null)
    {
        if (archiveRtIds == null || archiveRtIds.Count == 0)
        {
            return new ArchiveStorageStatsResponse
            {
                IsSuccess = true,
                Stats = [],
                Message = "archiveRtIds was empty; no stats to fetch."
            };
        }

        try
        {
            var tenantResolution = server.Services!.GetRequiredService<ITenantResolutionService>();
            var ctx = await tenantResolution.GetTenantContextAsync(tenantId);

            var repo = ctx.GetStreamDataRepository();
            if (repo == null)
            {
                // Match the GraphQL behaviour: surface placeholder rows so the AI client can still
                // render the list uniformly without a special "not enabled" code path.
                return new ArchiveStorageStatsResponse
                {
                    IsSuccess = true,
                    TenantId = ctx.TenantId,
                    Stats = archiveRtIds.Select(id => new ArchiveStorageStatsItem
                    {
                        ArchiveRtId = id,
                        TableExists = false,
                        RecordCount = 0,
                        SizeBytes = 0,
                        Health = "Unknown"
                    }).ToList(),
                    Message = "Stream data is not enabled for this tenant; stats returned as placeholders."
                };
            }

            var rtIds = archiveRtIds.Select(s => new OctoObjectId(s)).ToList();
            var stats = await repo.GetArchiveStatsAsync(rtIds);

            // Preserve input order so callers can zip with their existing list.
            var items = archiveRtIds.Select(id =>
            {
                var rtId = new OctoObjectId(id);
                if (stats.TryGetValue(rtId, out var s))
                {
                    return new ArchiveStorageStatsItem
                    {
                        ArchiveRtId = id,
                        TableExists = s.TableExists,
                        RecordCount = s.RecordCount,
                        SizeBytes = s.SizeBytes,
                        Health = s.Health.ToString()
                    };
                }

                return new ArchiveStorageStatsItem
                {
                    ArchiveRtId = id,
                    TableExists = false,
                    RecordCount = 0,
                    SizeBytes = 0,
                    Health = "Unknown"
                };
            }).ToList();

            return new ArchiveStorageStatsResponse
            {
                IsSuccess = true,
                TenantId = ctx.TenantId,
                Stats = items,
                Message = $"Stats fetched for {items.Count} archive(s)."
            };
        }
        catch (Exception ex)
        {
            return new ArchiveStorageStatsResponse { IsSuccess = false, ErrorMessage = ex.Message };
        }
    }

    /// <summary>Get bucket size + logical CK-attribute paths for a rollup archive.</summary>
    [McpServerTool(Name = "get_rollup_query_metadata")]
    [Description(
        "Return the query-construction metadata for a rollup archive: its bucket size + the logical " +
        "CK-attribute paths the rollup ultimately aggregates over. For cascade rollups (rollup over rollup) " +
        "the physical _sum/_count storage columns on the intermediate rollup are walked back to the " +
        "original CK attribute paths via RollupLogicalPathResolver. Returns Resolved=false if the rtId " +
        "doesn't resolve to a rollup archive or stream data is not enabled. Equivalent to GraphQL " +
        "StreamData.rollupQueryMetadata.")]
    public static async Task<RollupQueryMetadataResponse> GetRollupQueryMetadata(
        McpServer server,
        [Description("Rollup archive runtime id.")] string rollupRtId,
        [Description("Tenant id. Falls back to URL route.")] string? tenantId = null)
    {
        if (string.IsNullOrWhiteSpace(rollupRtId))
        {
            return new RollupQueryMetadataResponse { IsSuccess = false, ErrorMessage = "rollupRtId is required." };
        }

        try
        {
            var tenantResolution = server.Services!.GetRequiredService<ITenantResolutionService>();
            var ctx = await tenantResolution.GetTenantContextAsync(tenantId);

            var rollupStore = ctx.GetRollupArchiveRuntimeStore();
            if (rollupStore == null)
            {
                return new RollupQueryMetadataResponse
                {
                    IsSuccess = true,
                    TenantId = ctx.TenantId,
                    RollupRtId = rollupRtId,
                    Resolved = false,
                    Message = "Stream data is not enabled for this tenant."
                };
            }

            var rollup = await rollupStore.GetAsync(new OctoObjectId(rollupRtId));
            if (rollup == null)
            {
                return new RollupQueryMetadataResponse
                {
                    IsSuccess = true,
                    TenantId = ctx.TenantId,
                    RollupRtId = rollupRtId,
                    Resolved = false,
                    Message = $"No rollup archive with rtId '{rollupRtId}' found."
                };
            }

            // Walk the source-archive chain to recover the *logical* CK-attribute paths the rollup
            // aggregates over. For a single-step rollup (raw → rollup) the spec's SourcePath is already
            // the CK attribute path and ResolveAsync returns it as-is. For cascade rollups (rollup →
            // rollup), the spec's SourcePath is a physical _sum/_count column on the parent — the
            // resolver reverse-maps it through the parent's aggregation specs until it hits a raw /
            // time-range archive. Specs whose chain is broken are silently dropped (see resolver docs).
            var archiveStore = ctx.GetArchiveRuntimeStore();
            var paths = await RollupLogicalPathResolver.ResolveAsync(
                rollup,
                id => archiveStore.GetAsync(id),
                id => rollupStore.GetAsync(id));

            return new RollupQueryMetadataResponse
            {
                IsSuccess = true,
                TenantId = ctx.TenantId,
                RollupRtId = rollupRtId,
                BucketSizeMs = (long)rollup.BucketSize.TotalMilliseconds,
                LogicalSourcePaths = paths.ToList(),
                Resolved = true,
                Message = $"Rollup '{rollupRtId}' resolved: {paths.Count} logical path(s), {rollup.BucketSize}."
            };
        }
        catch (Exception ex)
        {
            return new RollupQueryMetadataResponse { IsSuccess = false, ErrorMessage = ex.Message };
        }
    }

    /// <summary>Resolution-aware series routing: pick the archive/rollup to query for a target point count.</summary>
    [McpServerTool(Name = "resolve_series_query")]
    [Description(
        "Resolution-aware series routing (AB#4290): given a base archive family, a time window, a target " +
        "point count and the required aggregation function, pick the archive/rollup to query at the best " +
        "resolution — without knowing which physical archive holds the data at a usable grain. Then run " +
        "query_stream_data_downsampling against the returned archiveRtId with limit = points and the " +
        "returned reducingFunction. Signals: Ok (reduced or raw-fits), NoSuitableRollup (no matching SUM " +
        "rollup — returns raw, does not mis-reduce), ResolutionLimited (coarser than requested — fewer " +
        "points), UnknownBaseGrain, EmptyLadder. Equivalent to GraphQL StreamData.resolveSeriesQuery.")]
    public static async Task<SeriesResolutionResponse> ResolveSeriesQuery(
        McpServer server,
        [Description("Base (raw / time-range) archive runtime id of the series' resolution family.")] string baseArchiveRtId,
        [Description("Inclusive start of the query window (UTC, ISO-8601).")] DateTime from,
        [Description("Exclusive end of the query window (UTC, ISO-8601).")] DateTime to,
        [Description("Logical CK attribute path of the measured column, e.g. 'Amount.Value'.")] string sourcePath,
        [Description("Required aggregation: sum (energy), max (demand), avg, min, count. Never guessed.")] string requiredAggregation,
        [Description("Desired number of output points (pixel-driven; ~600 typical).")] int targetPoints = 600,
        [Description("Optional source-entity rtId scope (e.g. the EnergyMeasurement entities of a MeteringPoint).")] List<string>? rtIds = null,
        [Description("Optional OBIS-code filter narrowing the series.")] string? obisFilter = null,
        [Description("Tenant id. Falls back to URL route.")] string? tenantId = null)
    {
        if (string.IsNullOrWhiteSpace(baseArchiveRtId))
        {
            return new SeriesResolutionResponse { IsSuccess = false, ErrorMessage = "baseArchiveRtId is required." };
        }

        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            return new SeriesResolutionResponse { IsSuccess = false, ErrorMessage = "sourcePath is required." };
        }

        if (from >= to)
        {
            return new SeriesResolutionResponse { IsSuccess = false, ErrorMessage = "from must be earlier than to." };
        }

        if (targetPoints <= 0)
        {
            return new SeriesResolutionResponse { IsSuccess = false, ErrorMessage = "targetPoints must be positive." };
        }

        if (!TryParseRollupFunction(requiredAggregation, out var function))
        {
            return new SeriesResolutionResponse
            {
                IsSuccess = false,
                ErrorMessage = "requiredAggregation must be one of sum / avg / min / max / count."
            };
        }

        try
        {
            var tenantResolution = server.Services!.GetRequiredService<ITenantResolutionService>();
            var ctx = await tenantResolution.GetTenantContextAsync(tenantId);

            var rollupStore = ctx.GetRollupArchiveRuntimeStore();
            if (rollupStore == null)
            {
                return new SeriesResolutionResponse
                {
                    IsSuccess = true,
                    TenantId = ctx.TenantId,
                    Resolved = false,
                    Message = "Stream data is not enabled for this tenant."
                };
            }

            var archiveStore = ctx.GetArchiveRuntimeStore();
            var service = new SeriesResolutionService(archiveStore, new RollupDependencyGraph(rollupStore));

            var request = new SeriesResolutionRequest(
                new OctoObjectId(baseArchiveRtId),
                TargetCkTypeId: null,
                from,
                to,
                targetPoints,
                function,
                sourcePath)
            {
                RtIds = rtIds?.Select(id => new OctoObjectId(id)).ToList(),
                ObisFilter = obisFilter
            };

            var result = await service.ResolveAsync(request);

            return new SeriesResolutionResponse
            {
                IsSuccess = true,
                TenantId = ctx.TenantId,
                Resolved = true,
                ArchiveRtId = result.ArchiveRtId.ToString(),
                EffectiveBucketMs = result.EffectiveBucketMs,
                Points = result.Points,
                ReducingFunction = result.ReducingFunction.ToString(),
                Signal = result.Signal.ToString(),
                ActualPoints = result.ActualPoints,
                Diagnostic = result.Diagnostic,
                Message = result.Diagnostic ?? $"Resolved to {result.ArchiveRtId} ({result.Signal})."
            };
        }
        catch (Exception ex)
        {
            return new SeriesResolutionResponse { IsSuccess = false, ErrorMessage = ex.Message };
        }
    }

    /// <summary>
    /// Parses a lowercase aggregation name (family-3 convention) to a <see cref="CkRollupFunction"/>.
    /// </summary>
    private static bool TryParseRollupFunction(string? value, out CkRollupFunction function)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "sum":
                function = CkRollupFunction.Sum;
                return true;
            case "avg":
            case "average":
                function = CkRollupFunction.Avg;
                return true;
            case "min":
            case "minimum":
                function = CkRollupFunction.Min;
                return true;
            case "max":
            case "maximum":
                function = CkRollupFunction.Max;
                return true;
            case "count":
                function = CkRollupFunction.Count;
                return true;
            default:
                function = CkRollupFunction.Sum;
                return false;
        }
    }
}
