using System.Text.Json.Serialization;

namespace Meshmakers.Octo.Backend.McpServices.Models.Aggregation;

/// <summary>
///     Aggregation function for runtime + stream-data queries.
///     Serialised lowercase (count/sum/avg/min/max) for AI ergonomics — strings are easier to construct
///     than enum integers, and lowercase mirrors SQL conventions.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AggregationFunctionDto
{
    /// <summary>Count of non-null values (attributePath optional, ignored if provided).</summary>
    count = 0,

    /// <summary>Sum of values (attributePath required).</summary>
    sum = 1,

    /// <summary>Arithmetic mean (attributePath required).</summary>
    avg = 2,

    /// <summary>Minimum value (attributePath required).</summary>
    min = 3,

    /// <summary>Maximum value (attributePath required).</summary>
    max = 4
}
