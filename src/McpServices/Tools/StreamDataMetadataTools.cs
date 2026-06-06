using System.ComponentModel;
using Meshmakers.Octo.Backend.McpServices.Models.Aggregation;
using Meshmakers.Octo.Backend.McpServices.Services;
using Meshmakers.Octo.ConstructionKit.Contracts;
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

    /// <summary>Get bucket size + column specs for a rollup archive.</summary>
    [McpServerTool(Name = "get_rollup_query_metadata")]
    [Description(
        "Return the query-construction metadata for a rollup archive: its bucket size + the physical " +
        "column specs that the rollup aggregates. Use this to plan a downsampling or aggregation query " +
        "against a rollup. Returns Resolved=false if the rtId doesn't resolve to a rollup archive or stream " +
        "data is not enabled. Equivalent to GraphQL StreamData.rollupQueryMetadata but without the logical-" +
        "path back-resolution (column specs are physical for now).")]
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

            // For now: project the source-column paths from the rollup's aggregation specs (rather than
            // back-resolving them to CK-attribute paths via RollupLogicalPathResolver, which lives in
            // Runtime.Engine.CrateDb — not referenced by the MCP server). Cascade-rollups (rollup over
            // rollup) therefore surface the intermediate _sum/_count column names rather than the
            // ultimate CK path; full back-resolution is a follow-up.
            var paths = rollup.Aggregations.Select(a => a.SourcePath).Distinct().ToList();

            return new RollupQueryMetadataResponse
            {
                IsSuccess = true,
                TenantId = ctx.TenantId,
                RollupRtId = rollupRtId,
                BucketSizeMs = (long)rollup.BucketSize.TotalMilliseconds,
                LogicalSourcePaths = paths,
                Resolved = true,
                Message = $"Rollup '{rollupRtId}' resolved: {paths.Count} column(s), {rollup.BucketSize}."
            };
        }
        catch (Exception ex)
        {
            return new RollupQueryMetadataResponse { IsSuccess = false, ErrorMessage = ex.Message };
        }
    }
}
