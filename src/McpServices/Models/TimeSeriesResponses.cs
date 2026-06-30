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
