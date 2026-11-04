namespace Meshmakers.Octo.Backend.McpServices.Models.Aggregation;

/// <summary>
///     One aggregation column for runtime + stream-data queries.
///     Mirrors the GraphQL <c>RtQueryColumnInput</c> / <c>StreamDataQueryColumnInput</c> shape but with
///     lowercase function strings + an optional alias for predictable response keys.
/// </summary>
public class AggregationColumnDto
{
    /// <summary>
    ///     Aggregation function to apply.
    /// </summary>
    public AggregationFunctionDto Function { get; set; }

    /// <summary>
    ///     Attribute path to aggregate. Required for sum/avg/min/max/integral. With a path, count counts the
    ///     non-null values of that attribute; runtime aggregations also accept count without a path (counts the
    ///     entities). Stream-data queries require a path for every function, count included (AB#5346).
    ///     Use dot-notation for nested fields (e.g. <c>Sensor.Power</c>).
    /// </summary>
    public string? AttributePath { get; set; }

    /// <summary>
    ///     Optional response column alias. Default is <c>"&lt;function&gt;_&lt;sanitised-path&gt;"</c>
    ///     (e.g. <c>avg_Power</c>) or <c>"count"</c> for a runtime count without a path.
    /// </summary>
    public string? Alias { get; set; }
}
