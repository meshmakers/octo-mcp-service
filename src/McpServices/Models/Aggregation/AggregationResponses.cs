using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Runtime.Contracts.StreamData;

namespace Meshmakers.Octo.Backend.McpServices.Models.Aggregation;

/// <summary>Common envelope for aggregation tool responses.</summary>
public class AggregationResponse
{
    /// <summary>True when the underlying engine call succeeded.</summary>
    public bool IsSuccess { get; set; }

    /// <summary>Error message when <see cref="IsSuccess"/> is false.</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>Optional human-readable status message.</summary>
    public string? Message { get; set; }

    /// <summary>Tenant the operation was executed against.</summary>
    public string? TenantId { get; set; }
}

/// <summary>Response of runtime aggregation tools (with or without grouping).</summary>
public class AggregationResultResponse : AggregationResponse
{
    /// <summary>
    ///     Result rows. For non-grouped aggregation there's exactly one row with the aliases of the
    ///     requested aggregations. For grouped aggregation there's one row per distinct group, with the
    ///     group-by attribute values + the aggregation aliases.
    /// </summary>
    public List<Dictionary<string, object?>> Rows { get; set; } = [];

    /// <summary>Total number of rows.</summary>
    public int RowCount { get; set; }
}

/// <summary>Response of the simple stream-data query tool.</summary>
public class StreamDataResultResponse : AggregationResponse
{
    /// <summary>Archive runtime id the query ran against.</summary>
    public string? ArchiveRtId { get; set; }

    /// <summary>Result rows from the time-series store.</summary>
    public List<StreamDataRowResponse> Rows { get; set; } = [];

    /// <summary>Total row count reported by the engine.</summary>
    public long TotalCount { get; set; }
}

/// <summary>Per-row projection from a stream-data result. Mirrors <see cref="StreamDataRow"/>.</summary>
public class StreamDataRowResponse
{
    /// <summary>Source entity runtime id (when the row is tied to one).</summary>
    public string? RtId { get; set; }

    /// <summary>CK type id of the source entity.</summary>
    public string? CkTypeId { get; set; }

    /// <summary>Observation timestamp.</summary>
    public DateTime? Timestamp { get; set; }

    /// <summary>Well-known name of the source entity (when available).</summary>
    public string? RtWellKnownName { get; set; }

    /// <summary>Per-column values (path → value).</summary>
    public Dictionary<string, object?> Values { get; set; } = [];
}

/// <summary>Response of the downsampling stream-data tool.</summary>
public class DownsamplingResultResponse : AggregationResponse
{
    /// <summary>Archive runtime id the query ran against.</summary>
    public string? ArchiveRtId { get; set; }

    /// <summary>One row per time bucket — each row carries the aggregation aliases for that bucket.</summary>
    public List<Dictionary<string, object?>> Rows { get; set; } = [];

    /// <summary>Number of returned buckets.</summary>
    public int RowCount { get; set; }
}

/// <summary>Response of <c>get_archive_storage_stats</c>.</summary>
public class ArchiveStorageStatsResponse : AggregationResponse
{
    /// <summary>Stats per archive runtime id. Order matches the input list.</summary>
    public List<ArchiveStorageStatsItem> Stats { get; set; } = [];
}

/// <summary>One archive's storage stats projection.</summary>
public class ArchiveStorageStatsItem
{
    /// <summary>Archive runtime id.</summary>
    public string? ArchiveRtId { get; set; }

    /// <summary>True when the backing table exists (archive activated). False = stats are placeholders.</summary>
    public bool TableExists { get; set; }

    /// <summary>Row count on the backing table.</summary>
    public long RecordCount { get; set; }

    /// <summary>On-disk size in bytes.</summary>
    public long SizeBytes { get; set; }

    /// <summary>Health classification — Unknown / Good / Warning / Critical.</summary>
    public string Health { get; set; } = "Unknown";
}

/// <summary>
///     Response of <c>execute_runtime_query</c>. Carries either entity DTOs (RtSimpleRtQuery) or scalar /
///     grouped aggregation rows (RtAggregationRtQuery / RtGroupingAggregationRtQuery), discriminated by
///     <see cref="QuerySubtype"/>.
/// </summary>
public class PersistedRuntimeQueryResponse : AggregationResponse
{
    /// <summary>Runtime id of the persisted query that was executed.</summary>
    public string? QueryRtId { get; set; }

    /// <summary>Concrete CK subtype name of the persisted query (e.g. <c>RtSimpleRtQuery</c>).</summary>
    public string? QuerySubtype { get; set; }

    /// <summary>CK type id the persisted query targets.</summary>
    public string? CkTypeId { get; set; }

    /// <summary>
    ///     Entity DTOs returned by simple persisted queries. Null for aggregation subtypes.
    /// </summary>
    public IList<RtEntityDto>? Entities { get; set; }

    /// <summary>
    ///     Aggregation rows. For non-grouped aggregation there's exactly one row with the configured column
    ///     aliases; for grouped aggregation there's one row per distinct group. Null for simple queries.
    /// </summary>
    public List<Dictionary<string, object?>>? Rows { get; set; }

    /// <summary>Total number of result rows / entities returned in this response.</summary>
    public int? RowCount { get; set; }

    /// <summary>Total entity count reported by the engine (simple queries only).</summary>
    public long? TotalCount { get; set; }
}

/// <summary>
///     Response of <c>execute_stream_data_query</c>. Discriminates between simple, aggregation, grouped
///     aggregation, and downsampling subtypes via <see cref="QuerySubtype"/>.
/// </summary>
public class PersistedStreamDataQueryResponse : AggregationResponse
{
    /// <summary>Runtime id of the persisted stream-data query that was executed.</summary>
    public string? QueryRtId { get; set; }

    /// <summary>Concrete CK subtype name (e.g. <c>RtSimpleSdQuery</c>).</summary>
    public string? QuerySubtype { get; set; }

    /// <summary>Archive runtime id read from the persisted query.</summary>
    public string? ArchiveRtId { get; set; }

    /// <summary>CK type id the archive holds time-series rows for.</summary>
    public string? CkTypeId { get; set; }

    /// <summary>Per-row stream-data values (simple subtype only). Null for aggregation subtypes.</summary>
    public List<StreamDataRowResponse>? StreamRows { get; set; }

    /// <summary>Aggregation / downsampling rows. Null for simple subtype.</summary>
    public List<Dictionary<string, object?>>? Rows { get; set; }

    /// <summary>Number of returned rows.</summary>
    public int? RowCount { get; set; }

    /// <summary>Total stream-data row count reported by the engine (simple subtype only).</summary>
    public long? TotalCount { get; set; }
}

/// <summary>Response of <c>get_rollup_query_metadata</c>.</summary>
public class RollupQueryMetadataResponse : AggregationResponse
{
    /// <summary>Rollup runtime id.</summary>
    public string? RollupRtId { get; set; }

    /// <summary>Bucket size in milliseconds.</summary>
    public long BucketSizeMs { get; set; }

    /// <summary>Logical CK-attribute paths the rollup aggregates over.</summary>
    public List<string> LogicalSourcePaths { get; set; } = [];

    /// <summary>
    ///     The rollup's source archives with their validity spans (AB#5157). A rollup can be fed by
    ///     several sources, each authoritative for a half-open span; a rollup migrated from the
    ///     deprecated single-source form reports exactly one unbounded entry.
    /// </summary>
    public List<RollupSourceItem> Sources { get; set; } = [];

    /// <summary>True when the rtId resolved to a rollup archive; false when not found / no stream data.</summary>
    public bool Resolved { get; set; }
}

/// <summary>
///     One source archive of a rollup with its validity span (AB#5157). Mirrors
///     <see cref="RollupSourceReference" />.
/// </summary>
public class RollupSourceItem
{
    /// <summary>Runtime id of the source archive (raw, time-range or another rollup).</summary>
    public string? SourceArchiveRtId { get; set; }

    /// <summary>Inclusive start of the span this source is authoritative for; null = open start.</summary>
    public DateTime? ValidFrom { get; set; }

    /// <summary>Exclusive end of the span this source is authoritative for; null = open end.</summary>
    public DateTime? ValidTo { get; set; }
}

/// <summary>
/// Response of <c>resolve_series_query</c> (AB#4290): the archive-selection decision for a
/// resolution-aware series query. Equivalent to GraphQL StreamData.resolveSeriesQuery.
/// </summary>
public class SeriesResolutionResponse : AggregationResponse
{
    /// <summary>The archive to query — a rollup, or the base archive on the refuse/raw paths.</summary>
    public string? ArchiveRtId { get; set; }

    /// <summary>Width in milliseconds of one output bucket; 0 when no bucketing applies / grain unknown.</summary>
    public long EffectiveBucketMs { get; set; }

    /// <summary>Number of points the caller can expect from the downsampling query.</summary>
    public int Points { get; set; }

    /// <summary>Aggregation function the downsampling query must use (Avg/Min/Max/Sum/Count).</summary>
    public string? ReducingFunction { get; set; }

    /// <summary>
    ///     Outcome signal: Ok / NoSuitableRollup / ResolutionLimited / UnknownBaseGrain / EmptyLadder /
    ///     CoverageLimited.
    /// </summary>
    public string? Signal { get; set; }

    /// <summary>Deliverable point count when below the requested target, or the native raw count on the refuse path. Null when the target was met.</summary>
    public int? ActualPoints { get; set; }

    /// <summary>Human-readable explanation of the chosen route / signal.</summary>
    public string? Diagnostic { get; set; }

    /// <summary>
    ///     Available-from of the finer rung the measured coverage filter excluded (AB#5157) — the
    ///     timestamp from which the request could have been served at that finer resolution. Non-null
    ///     only together with Signal <c>CoverageLimited</c>, and null even then when the excluded rung
    ///     reports no coverage at all.
    /// </summary>
    public DateTime? FinerRungAvailableFrom { get; set; }

    /// <summary>True when a routing decision was produced; false when stream data is not enabled.</summary>
    public bool Resolved { get; set; }
}

/// <summary>
///     Response of <c>get_archive_coverage</c> (AB#5157): the measured availability of every rung of
///     one archive family — the queried archive plus every rollup transitively derived from it.
///     Equivalent to GraphQL StreamData.coverageFor.
/// </summary>
public class ArchiveCoverageResponse : AggregationResponse
{
    /// <summary>
    ///     True when a family walk was performed; false when stream data is not enabled for the tenant.
    ///     <see cref="Items" /> is empty in both the disabled and the unknown-archive case.
    /// </summary>
    public bool Resolved { get; set; }

    /// <summary>
    ///     One entry per rung, the queried archive first, then its dependent rollups in breadth-first
    ///     order. Empty when the archive is unknown or stream data is not enabled.
    /// </summary>
    public List<ArchiveCoverageItem> Items { get; set; } = [];
}

/// <summary>One rung of an archive family with its measured coverage. Mirrors <see cref="ArchiveCoverageRung" />.</summary>
public class ArchiveCoverageItem
{
    /// <summary>Runtime id of the archive this rung describes.</summary>
    public string? ArchiveRtId { get; set; }

    /// <summary>Human-readable name of the rung; null falls back to the rtId.</summary>
    public string? RtWellKnownName { get; set; }

    /// <summary>True when the rung is the family's base (raw / time-range) archive, i.e. not a rollup.</summary>
    public bool IsBase { get; set; }

    /// <summary>Archive lifecycle status (e.g. Activated, Disabled) — tells a disabled rung from an empty one.</summary>
    public string? Status { get; set; }

    /// <summary>
    ///     Native bucket width in milliseconds — a rollup's bucket size, a time-range base archive's
    ///     period, and null for a raw base archive (no declared grain).
    /// </summary>
    public long? BucketSizeMs { get; set; }

    /// <summary>Bucket-boundary alignment (FixedSize / CalendarDay / IsoWeek / CalendarMonth / CalendarQuarter / CalendarYear).</summary>
    public string? BucketAlignment { get; set; }

    /// <summary>Aggregation functions this rung declares, as PascalCase names. Empty for a base archive.</summary>
    public List<string> StoredFunctions { get; set; } = [];

    /// <summary>Measured earliest timestamp with data; null when the rung holds no data.</summary>
    public DateTime? AvailableFrom { get; set; }

    /// <summary>Measured latest timestamp with data; null when the rung holds no data.</summary>
    public DateTime? AvailableTo { get; set; }
}
