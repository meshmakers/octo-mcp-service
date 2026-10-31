using System.Text.Json.Serialization;

namespace Meshmakers.Octo.Backend.McpServices.Models.Aggregation;

/// <summary>
///     Aggregation function for runtime + stream-data queries.
///     Serialised lowercase (count/sum/avg/min/max/integral) for AI ergonomics — strings are easier to construct
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
    max = 4,

    /// <summary>
    ///     Step integral of a numeric column over the query window, in value unit x hours (W -> Wh, kW -> kWh;
    ///     divide W·h by 1000 for kWh). Stream-data aggregation and grouping queries only; from and to are
    ///     required. Last value holds until the next row (LOCF), the last row before <c>from</c> carries in,
    ///     several rtIds are summed. AB#7918 / AB#7919.
    /// </summary>
    integral = 5
}
