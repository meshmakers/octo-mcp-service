using Meshmakers.Octo.Sdk.ServiceClient.AssetRepositoryServices.StreamData;

namespace Meshmakers.Octo.Backend.McpServices.Models;

/// <summary>Common envelope for Stream Data / Time Series tool responses.</summary>
public class TimeSeriesResponse
{
    /// <summary>True when the underlying service call succeeded.</summary>
    public bool IsSuccess { get; set; }

    /// <summary>Error message when <see cref="IsSuccess"/> is false.</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>Optional human-readable status message.</summary>
    public string? Message { get; set; }

    /// <summary>Tenant the operation was executed against.</summary>
    public string? TenantId { get; set; }
}

/// <summary>Response for archive/rollup lifecycle actions that act on a single runtime id.</summary>
public class ArchiveActionResponse : TimeSeriesResponse
{
    /// <summary>Runtime id of the archive (or rollup) that was acted on.</summary>
    public string? RtId { get; set; }
}

/// <summary>Response for backfill_rollup_archive (AB#4269).</summary>
public class RollupBackfillResponse : TimeSeriesResponse
{
    /// <summary>Runtime id of the rollup archive that was backfilled.</summary>
    public string? RtId { get; set; }

    /// <summary>
    /// The recompute job started by the backfill, or null when the source archive held no data
    /// (no-op).
    /// </summary>
    public RollupRecomputeJobInfoDto? Job { get; set; }
}

/// <summary>Response for list_recompute_jobs (AB#5189).</summary>
public class ListRecomputeJobsResponse : TimeSeriesResponse
{
    /// <summary>Rollup archive runtime id whose jobs were listed.</summary>
    public string? ArchiveRtId { get; set; }

    /// <summary>
    /// The archive's recompute jobs, newest first. <c>LastProgressAt</c> on a non-terminal job is
    /// its heartbeat; a Running job whose heartbeat stops advancing is no longer alive.
    /// </summary>
    public List<RollupRecomputeJobInfoDto> Jobs { get; set; } = [];

    /// <summary>Total number of jobs returned.</summary>
    public int TotalCount { get; set; }
}

/// <summary>Response for list_rollups_for_archive.</summary>
public class ListRollupsResponse : TimeSeriesResponse
{
    /// <summary>Source archive runtime id whose rollups were listed.</summary>
    public string? SourceArchiveRtId { get; set; }

    /// <summary>Rollup archives attached to the source.</summary>
    public List<RollupArchiveInfoDto> Rollups { get; set; } = [];

    /// <summary>Total number of rollups.</summary>
    public int TotalCount { get; set; }
}

/// <summary>Response for validate_rollups (AB#6968).</summary>
public class ValidateRollupsResponse : TimeSeriesResponse
{
    /// <summary>True when every checked rollup is consistent.</summary>
    public bool IsConsistent { get; set; }

    /// <summary>True when at least one rollup could not be checked, informational entries included.</summary>
    public bool HasNotChecked { get; set; }

    /// <summary>Number of rollups that could not be checked in a way that makes the report not consistent; see <c>Report.Entries[].NotChecked</c>.</summary>
    public int NotCheckedCount { get; set; }

    /// <summary>Number of rollups listed as not checked for information only (never activated); they do not affect <c>IsConsistent</c>.</summary>
    public int NotCheckedInformationalCount { get; set; }

    /// <summary>The validator's report: one entry per checked rollup with its findings.</summary>
    public RollupConsistencyReportDto? Report { get; set; }
}

/// <summary>Response for repair_rollups (AB#6968).</summary>
public class RepairRollupsResponse : TimeSeriesResponse
{
    /// <summary>True when nothing was changed (dry run).</summary>
    public bool DryRun { get; set; }

    /// <summary>The planned steps of a dry run; <c>null</c> when a repair was requested.</summary>
    public RollupRepairReportDto? Plan { get; set; }

    /// <summary>
    /// The repair request of an applied repair (state Pending at first). The repair runs in the background on the
    /// server; follow it with get_rollup_repair_status until the state is Done or Failed.
    /// </summary>
    public RollupRepairRequestDto? Request { get; set; }
}

/// <summary>Response for get_rollup_repair_status (AB#6968).</summary>
public class RollupRepairStatusResponse : TimeSeriesResponse
{
    /// <summary>The repair request: state, planned steps and, once it has ended, the final report.</summary>
    public RollupRepairRequestDto? Request { get; set; }

    /// <summary>True when the request has ended (Done or Failed).</summary>
    public bool IsFinished { get; set; }
}
