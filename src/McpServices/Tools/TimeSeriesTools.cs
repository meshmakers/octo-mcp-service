using System.ComponentModel;
using Meshmakers.Octo.Backend.McpServices.Models;
using Meshmakers.Octo.Backend.McpServices.Services;
using ModelContextProtocol.Server;

// ReSharper disable UnusedMember.Global

namespace Meshmakers.Octo.Backend.McpServices.Tools;

/// <summary>
///     Time Series + Archive + Rollup lifecycle tools (Stream Data service). Mirrors the octo-cli TimeSeries
///     commands.
/// </summary>
[McpServerToolType]
public sealed class TimeSeriesTools
{
    // ── Stream Data lifecycle ───────────────────────────────────────────────

    /// <summary>Enable Stream Data ingestion for the tenant.</summary>
    [McpServerTool(Name = "enable_stream_data")]
    [McpRisk(McpRiskLevel.High)]
    [Description("Enable Stream Data ingestion for the resolved tenant. Equivalent to octo-cli EnableStreamData.")]
    public static async Task<TimeSeriesResponse> EnableStreamData(
        McpServer server,
        [Description("Tenant to operate on. Falls back to URL route.")] string? tenantId = null)
    {
        var ctx = await StreamDataClientContext.TryBuildAsync(server, tenantId);
        if (ctx.Error != null)
        {
            return new TimeSeriesResponse { IsSuccess = false, ErrorMessage = ctx.Error };
        }

        try
        {
            await ctx.Client!.EnableAsync(ctx.TenantId!);
            return new TimeSeriesResponse
            {
                IsSuccess = true,
                TenantId = ctx.TenantId,
                Message = $"Stream Data enabled for tenant '{ctx.TenantId}'."
            };
        }
        catch (Exception ex)
        {
            return new TimeSeriesResponse { IsSuccess = false, ErrorMessage = ex.Message };
        }
    }

    /// <summary>Disable Stream Data ingestion for the tenant. Destructive: requires confirm.</summary>
    [McpServerTool(Name = "disable_stream_data")]
    [McpRisk(McpRiskLevel.High)]
    [Description(
        "Disable Stream Data ingestion for the resolved tenant. DESTRUCTIVE — ingestion stops until re-enabled. " +
        "Requires confirm=true. Equivalent to octo-cli DisableStreamData.")]
    public static async Task<TimeSeriesResponse> DisableStreamData(
        McpServer server,
        [Description("Must be true to actually disable.")] bool confirm = false,
        [Description("Tenant to operate on. Falls back to URL route.")] string? tenantId = null)
    {
        if (!confirm)
        {
            return new TimeSeriesResponse
            {
                IsSuccess = false,
                ErrorMessage = "Refusing to disable Stream Data without confirm=true."
            };
        }

        var ctx = await StreamDataClientContext.TryBuildAsync(server, tenantId);
        if (ctx.Error != null)
        {
            return new TimeSeriesResponse { IsSuccess = false, ErrorMessage = ctx.Error };
        }

        try
        {
            await ctx.Client!.DisableAsync(ctx.TenantId!);
            return new TimeSeriesResponse
            {
                IsSuccess = true,
                TenantId = ctx.TenantId,
                Message = $"Stream Data disabled for tenant '{ctx.TenantId}'."
            };
        }
        catch (Exception ex)
        {
            return new TimeSeriesResponse { IsSuccess = false, ErrorMessage = ex.Message };
        }
    }

    // ── Archive lifecycle ───────────────────────────────────────────────────

    /// <summary>Activate a CkArchive: provisions the per-archive CrateDB table.</summary>
    [McpServerTool(Name = "activate_archive")]
    [McpRisk(McpRiskLevel.High)]
    [Description(
        "Activate a CkArchive: provisions the per-archive CrateDB table and transitions the archive to " +
        "'Activated'. Allowed from 'Created', 'Disabled', or 'Failed'; idempotent on 'Activated'. Equivalent " +
        "to octo-cli ActivateArchive.")]
    public static Task<ArchiveActionResponse> ActivateArchive(
        McpServer server,
        [Description("Archive runtime ID.")] string archiveRtId,
        [Description("Tenant to operate on. Falls back to URL route.")] string? tenantId = null)
        => ArchiveAction(server, tenantId, archiveRtId,
            requiredConfirm: false, confirm: true,
            (c, tid, id) => c.ActivateArchiveAsync(tid, id),
            successMessage: id => $"Archive '{id}' activated.");

    /// <summary>Disable an archive: data preserved, ingest stops. Allowed only from Activated.</summary>
    [McpServerTool(Name = "disable_archive")]
    [McpRisk(McpRiskLevel.High)]
    [Description(
        "Disable a CkArchive: transitions to 'Disabled' (data preserved). Allowed only from 'Activated'. " +
        "Equivalent to octo-cli DisableArchive.")]
    public static Task<ArchiveActionResponse> DisableArchive(
        McpServer server,
        [Description("Archive runtime ID.")] string archiveRtId,
        [Description("Tenant to operate on. Falls back to URL route.")] string? tenantId = null)
        => ArchiveAction(server, tenantId, archiveRtId,
            requiredConfirm: false, confirm: true,
            (c, tid, id) => c.DisableArchiveAsync(tid, id),
            successMessage: id => $"Archive '{id}' disabled (data preserved).");

    /// <summary>Re-enable a previously disabled archive.</summary>
    [McpServerTool(Name = "enable_archive")]
    [McpRisk(McpRiskLevel.High)]
    [Description(
        "Re-enable a previously disabled archive (Disabled → Activated). Re-validates column paths against the " +
        "current CK model; no DDL. Equivalent to octo-cli EnableArchive.")]
    public static Task<ArchiveActionResponse> EnableArchive(
        McpServer server,
        [Description("Archive runtime ID.")] string archiveRtId,
        [Description("Tenant to operate on. Falls back to URL route.")] string? tenantId = null)
        => ArchiveAction(server, tenantId, archiveRtId,
            requiredConfirm: false, confirm: true,
            (c, tid, id) => c.EnableArchiveAsync(tid, id),
            successMessage: id => $"Archive '{id}' re-enabled.");

    /// <summary>Retry archive activation after a DDL failure.</summary>
    [McpServerTool(Name = "retry_archive_activation")]
    [McpRisk(McpRiskLevel.High)]
    [Description(
        "Retry activation of an archive after a previous DDL failure. Allowed only from 'Failed'. " +
        "Equivalent to octo-cli RetryArchiveActivation.")]
    public static Task<ArchiveActionResponse> RetryArchiveActivation(
        McpServer server,
        [Description("Archive runtime ID.")] string archiveRtId,
        [Description("Tenant to operate on. Falls back to URL route.")] string? tenantId = null)
        => ArchiveAction(server, tenantId, archiveRtId,
            requiredConfirm: false, confirm: true,
            (c, tid, id) => c.RetryArchiveActivationAsync(tid, id),
            successMessage: id => $"Archive '{id}' activation retried.");

    /// <summary>Delete a CkArchive. Destructive: drops the table and loses data.</summary>
    [McpServerTool(Name = "delete_archive")]
    [McpRisk(McpRiskLevel.Medium)]
    [Description(
        "Drop the per-archive CrateDB table (idempotent) and soft-delete the CkArchive entity. DESTRUCTIVE — " +
        "historical data is lost. Allowed from any status. Requires confirm=true. Equivalent to octo-cli " +
        "DeleteArchive.")]
    public static Task<ArchiveActionResponse> DeleteArchive(
        McpServer server,
        [Description("Archive runtime ID.")] string archiveRtId,
        [Description("Must be true to actually delete.")] bool confirm = false,
        [Description("Tenant to operate on. Falls back to URL route.")] string? tenantId = null)
        => ArchiveAction(server, tenantId, archiveRtId,
            requiredConfirm: true, confirm,
            (c, tid, id) => c.DeleteArchiveAsync(tid, id),
            successMessage: id => $"Archive '{id}' deleted — table dropped, data lost.");

    // ── Rollup archives (concept §9) ────────────────────────────────────────

    /// <summary>List rollup archives attached to a source archive.</summary>
    [McpServerTool(Name = "list_rollups_for_archive")]
    [Description(
        "List every non-soft-deleted rollup archive attached to the given source CkArchive. Equivalent to " +
        "octo-cli ListRollupsForArchive.")]
    public static async Task<ListRollupsResponse> ListRollupsForArchive(
        McpServer server,
        [Description("Source archive runtime ID.")] string sourceArchiveRtId,
        [Description("Tenant to operate on. Falls back to URL route.")] string? tenantId = null)
    {
        if (string.IsNullOrWhiteSpace(sourceArchiveRtId))
        {
            return new ListRollupsResponse { IsSuccess = false, ErrorMessage = "sourceArchiveRtId is required." };
        }

        var ctx = await StreamDataClientContext.TryBuildAsync(server, tenantId);
        if (ctx.Error != null)
        {
            return new ListRollupsResponse { IsSuccess = false, ErrorMessage = ctx.Error };
        }

        try
        {
            var rollups = (await ctx.Client!.ListRollupsForArchiveAsync(ctx.TenantId!, sourceArchiveRtId)).ToList();
            return new ListRollupsResponse
            {
                IsSuccess = true,
                TenantId = ctx.TenantId,
                SourceArchiveRtId = sourceArchiveRtId,
                Rollups = rollups,
                TotalCount = rollups.Count,
                Message = rollups.Count == 0
                    ? $"No rollups attached to archive '{sourceArchiveRtId}'."
                    : $"{rollups.Count} rollup(s) attached to archive '{sourceArchiveRtId}'."
            };
        }
        catch (Exception ex)
        {
            return new ListRollupsResponse { IsSuccess = false, ErrorMessage = ex.Message };
        }
    }

    /// <summary>Freeze a rollup archive until a target timestamp.</summary>
    [McpServerTool(Name = "freeze_rollup_archive")]
    [McpRisk(McpRiskLevel.High)]
    [Description(
        "Freeze a rollup archive at the given timestamp. Monotonic — rejected if the new value is earlier than " +
        "the current FrozenUntil. Equivalent to octo-cli FreezeRollupArchive.")]
    public static async Task<ArchiveActionResponse> FreezeRollupArchive(
        McpServer server,
        [Description("Rollup archive runtime ID.")] string rollupRtId,
        [Description("Upper bound of the frozen range (UTC timestamp).")] DateTime until,
        [Description("Tenant to operate on. Falls back to URL route.")] string? tenantId = null)
    {
        if (string.IsNullOrWhiteSpace(rollupRtId))
        {
            return new ArchiveActionResponse { IsSuccess = false, ErrorMessage = "rollupRtId is required." };
        }

        var ctx = await StreamDataClientContext.TryBuildAsync(server, tenantId);
        if (ctx.Error != null)
        {
            return new ArchiveActionResponse { IsSuccess = false, ErrorMessage = ctx.Error };
        }

        try
        {
            await ctx.Client!.FreezeRollupArchiveAsync(ctx.TenantId!, rollupRtId, until);
            return new ArchiveActionResponse
            {
                IsSuccess = true,
                TenantId = ctx.TenantId,
                RtId = rollupRtId,
                Message = $"Rollup '{rollupRtId}' frozen until {until:O}."
            };
        }
        catch (Exception ex)
        {
            return new ArchiveActionResponse { IsSuccess = false, ErrorMessage = ex.Message };
        }
    }

    /// <summary>Clear FrozenUntil on a rollup archive. Idempotent.</summary>
    [McpServerTool(Name = "unfreeze_rollup_archive")]
    [McpRisk(McpRiskLevel.High)]
    [Description(
        "Clear FrozenUntil on a rollup archive. Idempotent. Set acceptGaps=true when source data inside the " +
        "previously-frozen range has been truncated and the operator knowingly accepts the resulting gaps. " +
        "Equivalent to octo-cli UnfreezeRollupArchive.")]
    public static async Task<ArchiveActionResponse> UnfreezeRollupArchive(
        McpServer server,
        [Description("Rollup archive runtime ID.")] string rollupRtId,
        [Description("Accept gaps if source data inside the frozen range has been truncated.")] bool acceptGaps = false,
        [Description("Tenant to operate on. Falls back to URL route.")] string? tenantId = null)
    {
        if (string.IsNullOrWhiteSpace(rollupRtId))
        {
            return new ArchiveActionResponse { IsSuccess = false, ErrorMessage = "rollupRtId is required." };
        }

        var ctx = await StreamDataClientContext.TryBuildAsync(server, tenantId);
        if (ctx.Error != null)
        {
            return new ArchiveActionResponse { IsSuccess = false, ErrorMessage = ctx.Error };
        }

        try
        {
            await ctx.Client!.UnfreezeRollupArchiveAsync(ctx.TenantId!, rollupRtId, acceptGaps);
            return new ArchiveActionResponse
            {
                IsSuccess = true,
                TenantId = ctx.TenantId,
                RtId = rollupRtId,
                Message = acceptGaps
                    ? $"Rollup '{rollupRtId}' unfrozen (gaps accepted)."
                    : $"Rollup '{rollupRtId}' unfrozen."
            };
        }
        catch (Exception ex)
        {
            return new ArchiveActionResponse { IsSuccess = false, ErrorMessage = ex.Message };
        }
    }

    /// <summary>Rewind a rollup's watermark. Destructive: rows in the rewound range are temporarily out of sync.</summary>
    [McpServerTool(Name = "rewind_rollup_watermark")]
    [McpRisk(McpRiskLevel.High)]
    [Description(
        "Reset a rollup's watermark (truncated to bucket boundary) so subsequent orchestrator ticks re-aggregate " +
        "the rewound range. DESTRUCTIVE — rows in that range are temporarily out of sync until the orchestrator " +
        "catches up. Requires confirm=true. Equivalent to octo-cli RewindRollupWatermark.")]
    public static async Task<ArchiveActionResponse> RewindRollupWatermark(
        McpServer server,
        [Description("Rollup archive runtime ID.")] string rollupRtId,
        [Description("Bucket end timestamp to rewind to (UTC).")] DateTime toBucketEnd,
        [Description("Must be true to actually rewind.")] bool confirm = false,
        [Description("Tenant to operate on. Falls back to URL route.")] string? tenantId = null)
    {
        if (string.IsNullOrWhiteSpace(rollupRtId))
        {
            return new ArchiveActionResponse { IsSuccess = false, ErrorMessage = "rollupRtId is required." };
        }

        if (!confirm)
        {
            return new ArchiveActionResponse
            {
                IsSuccess = false,
                ErrorMessage = $"Refusing to rewind rollup watermark on '{rollupRtId}' without confirm=true."
            };
        }

        var ctx = await StreamDataClientContext.TryBuildAsync(server, tenantId);
        if (ctx.Error != null)
        {
            return new ArchiveActionResponse { IsSuccess = false, ErrorMessage = ctx.Error };
        }

        try
        {
            await ctx.Client!.RewindRollupWatermarkAsync(ctx.TenantId!, rollupRtId, toBucketEnd);
            return new ArchiveActionResponse
            {
                IsSuccess = true,
                TenantId = ctx.TenantId,
                RtId = rollupRtId,
                Message = $"Rollup '{rollupRtId}' watermark rewound to {toBucketEnd:O}."
            };
        }
        catch (Exception ex)
        {
            return new ArchiveActionResponse { IsSuccess = false, ErrorMessage = ex.Message };
        }
    }

    /// <summary>Populate / reset a rollup over the entire history of its source archive (AB#4269).</summary>
    [McpServerTool(Name = "backfill_rollup_archive")]
    [McpRisk(McpRiskLevel.High)]
    [Description(
        "Populate or reset a rollup over the ENTIRE history of its source archive without supplying a timestamp " +
        "(AB#4269). Resolves the source archive's earliest timestamp and recomputes [sourceMin, now) over the same " +
        "reader-safe optimistic recompute path as recomputeArchive (atomic generation-swap, RecomputeJob " +
        "observability). Re-running resets an already-populated rollup. Requires confirm=true. A no-op when the " +
        "source archive holds no data. Equivalent to octo-cli BackfillRollup.")]
    public static async Task<RollupBackfillResponse> BackfillRollupArchive(
        McpServer server,
        [Description("Rollup archive runtime ID to backfill from its source.")] string rollupRtId,
        [Description("Must be true to actually run the backfill (it can reset a populated rollup).")] bool confirm = false,
        [Description("Tenant to operate on. Falls back to URL route.")] string? tenantId = null)
    {
        if (string.IsNullOrWhiteSpace(rollupRtId))
        {
            return new RollupBackfillResponse { IsSuccess = false, ErrorMessage = "rollupRtId is required." };
        }

        if (!confirm)
        {
            return new RollupBackfillResponse
            {
                IsSuccess = false,
                ErrorMessage = $"Refusing to backfill rollup '{rollupRtId}' without confirm=true."
            };
        }

        var ctx = await StreamDataClientContext.TryBuildAsync(server, tenantId);
        if (ctx.Error != null)
        {
            return new RollupBackfillResponse { IsSuccess = false, ErrorMessage = ctx.Error };
        }

        try
        {
            var job = await ctx.Client!.BackfillRollupFromSourceAsync(ctx.TenantId!, rollupRtId);
            return new RollupBackfillResponse
            {
                IsSuccess = true,
                TenantId = ctx.TenantId,
                RtId = rollupRtId,
                Job = job,
                Message = job is null
                    ? $"Backfill of rollup '{rollupRtId}' was a no-op: the source archive holds no data."
                    : $"Backfill of rollup '{rollupRtId}' started: job {job.RtId}, state {job.State}."
            };
        }
        catch (Exception ex)
        {
            return new RollupBackfillResponse { IsSuccess = false, ErrorMessage = ex.Message };
        }
    }

    private static async Task<ArchiveActionResponse> ArchiveAction(
        McpServer server,
        string? tenantId,
        string rtId,
        bool requiredConfirm,
        bool confirm,
        Func<Sdk.ServiceClient.AssetRepositoryServices.StreamData.IStreamDataServicesClient, string, string, Task> action,
        Func<string, string> successMessage)
    {
        if (string.IsNullOrWhiteSpace(rtId))
        {
            return new ArchiveActionResponse { IsSuccess = false, ErrorMessage = "rtId is required." };
        }

        if (requiredConfirm && !confirm)
        {
            return new ArchiveActionResponse
            {
                IsSuccess = false,
                ErrorMessage = $"Refusing to act on archive '{rtId}' without confirm=true."
            };
        }

        var ctx = await StreamDataClientContext.TryBuildAsync(server, tenantId);
        if (ctx.Error != null)
        {
            return new ArchiveActionResponse { IsSuccess = false, ErrorMessage = ctx.Error };
        }

        try
        {
            await action(ctx.Client!, ctx.TenantId!, rtId);
            return new ArchiveActionResponse
            {
                IsSuccess = true,
                TenantId = ctx.TenantId,
                RtId = rtId,
                Message = successMessage(rtId)
            };
        }
        catch (Exception ex)
        {
            return new ArchiveActionResponse { IsSuccess = false, ErrorMessage = ex.Message };
        }
    }
}
