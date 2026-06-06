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

    /// <summary>True when the rtId resolved to a rollup archive; false when not found / no stream data.</summary>
    public bool Resolved { get; set; }
}
