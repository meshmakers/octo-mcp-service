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

    /// <summary>Disable Stream Data for the tenant: a guarded, reversible flag flip. Requires confirm.</summary>
    [McpServerTool(Name = "disable_stream_data")]
    [McpRisk(McpRiskLevel.High)]
    [Description(
        "Disable Stream Data for the resolved tenant. Refused with a Conflict error while archives are still " +
        "activated - the error names them; disable them first with disable_archive (data kept) or remove them " +
        "with delete_archive (confirm=true, rollups before their source). Switches the tenant flag off; the " +
        "System.StreamData model, archive definitions and stored data are kept. Required before delete_tenant / " +
        "detach_tenant (AB#4255). Reversible with enable_stream_data. Requires confirm=true. Equivalent to " +
        "octo-cli DisableStreamData.")]
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
        "List every non-soft-deleted rollup archive attached to the given source CkArchive. A rollup can " +
        "be attached to SEVERAL source archives (AB#5157), each authoritative for a half-open " +
        "[validFrom, validTo) span — read each row's `sources` list for the full picture. The row-level " +
        "`sourceArchiveRtId` is the deprecated derived field: the server fills it only for a rollup with " +
        "exactly one unbounded source and leaves it null otherwise. The response-level " +
        "`sourceArchiveRtId` is just the echo of the archive that was asked for. Equivalent to octo-cli " +
        "ListRollupsForArchive.")]
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

    /// <summary>List the recompute job history of a rollup archive.</summary>
    [McpServerTool(Name = "list_recompute_jobs")]
    [Description(
        "List the most recent recompute jobs of a rollup archive, newest first (capped at 50 by the server). Each " +
        "job carries state (Pending / Running / Swapping / Completed / Failed / Coalesced), row and window counts, " +
        "timings and the failure reason. `lastProgressAt` is the heartbeat of a non-terminal job (AB#5189): it is " +
        "stamped when the job is created or starts computing and after every committed chunk, so a Running job " +
        "whose heartbeat stops advancing is no longer alive — after the server's stale-job timeout it is failed " +
        "with the reason 'Presumed dead'. Use this to watch a backfill_rollup_archive or recompute job run to " +
        "completion. Equivalent to octo-cli ListRecomputeJobs.")]
    public static async Task<ListRecomputeJobsResponse> ListRecomputeJobs(
        McpServer server,
        [Description("Rollup archive runtime ID whose recompute jobs to list.")] string archiveRtId,
        [Description("Tenant to operate on. Falls back to URL route.")] string? tenantId = null)
    {
        if (string.IsNullOrWhiteSpace(archiveRtId))
        {
            return new ListRecomputeJobsResponse { IsSuccess = false, ErrorMessage = "archiveRtId is required." };
        }

        var ctx = await StreamDataClientContext.TryBuildAsync(server, tenantId);
        if (ctx.Error != null)
        {
            return new ListRecomputeJobsResponse { IsSuccess = false, ErrorMessage = ctx.Error };
        }

        try
        {
            var jobs = (await ctx.Client!.ListRecomputeJobsForArchiveAsync(ctx.TenantId!, archiveRtId)).ToList();
            return new ListRecomputeJobsResponse
            {
                IsSuccess = true,
                TenantId = ctx.TenantId,
                ArchiveRtId = archiveRtId,
                Jobs = jobs,
                TotalCount = jobs.Count,
                Message = jobs.Count == 0
                    ? $"No recompute jobs recorded for archive '{archiveRtId}'."
                    : $"{jobs.Count} recompute job(s) for archive '{archiveRtId}', newest first."
            };
        }
        catch (Exception ex)
        {
            return new ListRecomputeJobsResponse { IsSuccess = false, ErrorMessage = ex.Message };
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
        "Queue a durable, background backfill that populates or resets a rollup over the ENTIRE history of its " +
        "source archive without supplying a timestamp (AB#4269 / AB#4286). Resolves the source archive's earliest " +
        "timestamp and enqueues a recompute of [sourceMin, now); the heavy work runs in the background (never bound " +
        "to this request, so a client timeout cannot cancel it) and the queued work survives an asset-repo restart. " +
        "Returns the Pending RecomputeJob id immediately — poll list_recompute_jobs to watch Pending -> Running -> " +
        "Completed (its `lastProgressAt` heartbeat shows whether a Running job is still alive). Re-running resets an " +
        "already-populated rollup. Requires confirm=true. A no-op when the source archive holds no data. Equivalent " +
        "to octo-cli BackfillRollup.")]
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
                    : $"Backfill of rollup '{rollupRtId}' queued (durable background): job {job.RtId}, state {job.State}. "
                      + $"Poll list_recompute_jobs for archive '{rollupRtId}' to watch it run to completion."
            };
        }
        catch (Exception ex)
        {
            return new RollupBackfillResponse { IsSuccess = false, ErrorMessage = ex.Message };
        }
    }

    /// <summary>Check the rollup archives of the tenant for restore inconsistencies (AB#6968).</summary>
    [McpServerTool(Name = "validate_rollups")]
    [McpRisk(McpRiskLevel.Low)]
    [Description(
        "Read-only consistency check of the rollup archives of a tenant (AB#6968): a pointer without rows, rows " +
        "above the pointer, a watermark ahead of the rows, a missing generation map. Run it after a CrateDB " +
        "restore (DR runbook). The server uses counts and MAX/MIN statements only; nothing is changed and no row is " +
        "exported. An inconsistent rollup is reported in `report.entries[].findings`, not as an error. If " +
        "`isConsistent` is false, call repair_rollups (dry run first). Equivalent to octo-cli ValidateRollups.")]
    public static async Task<ValidateRollupsResponse> ValidateRollups(
        McpServer server,
        [Description("Rollup archive runtime IDs to check. Omit to check every rollup of the tenant.")]
        List<string>? rollupRtIds = null,
        [Description("Tenant to operate on. Falls back to URL route.")] string? tenantId = null)
    {
        var ctx = await StreamDataClientContext.TryBuildAsync(server, tenantId);
        if (ctx.Error != null)
        {
            return new ValidateRollupsResponse { IsSuccess = false, ErrorMessage = ctx.Error };
        }

        try
        {
            var report = await ctx.Client!.ValidateRollupsAsync(ctx.TenantId!, rollupRtIds);
            var inconsistent = report.Entries.Count(e => !e.IsOk);
            return new ValidateRollupsResponse
            {
                IsSuccess = true,
                TenantId = ctx.TenantId,
                IsConsistent = report.IsOk,
                Report = report,
                Message = report.IsOk
                    ? $"{report.Entries.Count} rollup(s) checked, all consistent."
                    : $"{report.Entries.Count} rollup(s) checked, {inconsistent} inconsistent. "
                      + "Call repair_rollups (dry run first) to see the planned repairs."
            };
        }
        catch (Exception ex)
        {
            return new ValidateRollupsResponse { IsSuccess = false, ErrorMessage = ex.Message };
        }
    }

    /// <summary>Repair the rollup inconsistencies validate_rollups finds (AB#6968).</summary>
    [McpServerTool(Name = "repair_rollups")]
    [McpRisk(McpRiskLevel.High)]
    [Description(
        "Repair what validate_rollups finds (AB#6968): delete rows above the generation the pointer selects, rewind " +
        "a watermark, create a missing generation map, recompute a pointer range. Idempotent. DRY RUN BY DEFAULT: " +
        "with dryRun=true (the default) it lists the planned steps per rollup and changes nothing. With " +
        "dryRun=false (requires confirm=true) it REQUESTS the repair: the server executes it in the background " +
        "(nothing runs inside this call) and returns `request` with a `requestId`. Poll get_rollup_repair_status " +
        "until the state is Done or Failed; the final report then lists every step and the validation after the " +
        "repair. A second request while one is active returns the active request. Equivalent to octo-cli RepairRollups.")]
    public static async Task<RepairRollupsResponse> RepairRollups(
        McpServer server,
        [Description("Rollup archive runtime IDs to repair. Omit to repair every rollup of the tenant.")]
        List<string>? rollupRtIds = null,
        [Description("True (default) lists the planned repairs and changes nothing; false requests the repair.")]
        bool dryRun = true,
        [Description("Must be true when dryRun is false (the repair deletes rows and recomputes ranges).")]
        bool confirm = false,
        [Description("Tenant to operate on. Falls back to URL route.")] string? tenantId = null)
    {
        if (!dryRun && !confirm)
        {
            return new RepairRollupsResponse
            {
                IsSuccess = false,
                ErrorMessage = "Refusing to repair rollups without confirm=true. Use dryRun=true to see the plan."
            };
        }

        var ctx = await StreamDataClientContext.TryBuildAsync(server, tenantId);
        if (ctx.Error != null)
        {
            return new RepairRollupsResponse { IsSuccess = false, ErrorMessage = ctx.Error };
        }

        try
        {
            if (dryRun)
            {
                var plan = await ctx.Client!.PlanRollupRepairAsync(ctx.TenantId!, rollupRtIds);
                return new RepairRollupsResponse
                {
                    IsSuccess = true,
                    TenantId = ctx.TenantId,
                    DryRun = true,
                    Plan = plan,
                    Message = $"Dry run: {plan.Actions.Count} planned step(s); nothing was changed."
                };
            }

            var request = await ctx.Client!.EnqueueRollupRepairAsync(ctx.TenantId!, rollupRtIds);
            return new RepairRollupsResponse
            {
                IsSuccess = true,
                TenantId = ctx.TenantId,
                Request = request,
                Message = $"Repair requested: {request.RequestId} is {request.State}. It runs in the background; "
                          + "poll get_rollup_repair_status until it is Done or Failed."
            };
        }
        catch (Exception ex)
        {
            return new RepairRollupsResponse { IsSuccess = false, ErrorMessage = ex.Message };
        }
    }

    /// <summary>Read the state of a rollup repair request (AB#6968).</summary>
    [McpServerTool(Name = "get_rollup_repair_status")]
    [McpRisk(McpRiskLevel.Low)]
    [Description(
        "Read-only: the state of a rollup repair request started by repair_rollups (AB#6968): Pending, Running, " +
        "Done or Failed, the planned steps and, once the request has ended, the final report (every step, the " +
        "validation after the repair and, on failure, the failed step). `isFinished` is true for Done and Failed. " +
        "Equivalent to octo-cli GetRollupRepairStatus.")]
    public static async Task<RollupRepairStatusResponse> GetRollupRepairStatus(
        McpServer server,
        [Description("The requestId returned by repair_rollups.")] string requestId,
        [Description("Tenant to operate on. Falls back to URL route.")] string? tenantId = null)
    {
        if (string.IsNullOrWhiteSpace(requestId))
        {
            return new RollupRepairStatusResponse { IsSuccess = false, ErrorMessage = "requestId is required." };
        }

        var ctx = await StreamDataClientContext.TryBuildAsync(server, tenantId);
        if (ctx.Error != null)
        {
            return new RollupRepairStatusResponse { IsSuccess = false, ErrorMessage = ctx.Error };
        }

        try
        {
            var request = await ctx.Client!.GetRollupRepairRequestAsync(ctx.TenantId!, requestId);
            return new RollupRepairStatusResponse
            {
                IsSuccess = true,
                TenantId = ctx.TenantId,
                Request = request,
                IsFinished = request.IsFinished,
                Message = request.IsFinished
                    ? $"Repair request {request.RequestId} ended: {request.State}."
                    : $"Repair request {request.RequestId} is {request.State}; poll again."
            };
        }
        catch (Exception ex)
        {
            return new RollupRepairStatusResponse { IsSuccess = false, ErrorMessage = ex.Message };
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
