using Meshmakers.Octo.Backend.McpServices.Models.Aggregation;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;

namespace Meshmakers.Octo.Backend.McpServices.Services;

/// <summary>
///     Maps the MCP-side aggregation DTOs (lowercase enum strings, optional alias) to the engine-side
///     <see cref="AggregationFunction"/> + <see cref="AggregationColumn"/> shapes, and pre-validates the
///     input before the SDK call.
/// </summary>
internal static class AggregationMapper
{
    /// <summary>Maps the lowercase MCP enum to the engine's <see cref="AggregationFunction"/>.</summary>
    public static AggregationFunction ToEngineFunction(AggregationFunctionDto dto) => dto switch
    {
        AggregationFunctionDto.count => AggregationFunction.Count,
        AggregationFunctionDto.sum => AggregationFunction.Sum,
        AggregationFunctionDto.avg => AggregationFunction.Average,
        AggregationFunctionDto.min => AggregationFunction.Minimum,
        AggregationFunctionDto.max => AggregationFunction.Maximum,
        AggregationFunctionDto.integral => AggregationFunction.Integral,
        _ => throw new ArgumentOutOfRangeException(nameof(dto), dto, "Unknown aggregation function")
    };

    /// <summary>Derives the response-column alias when the user didn't specify one.</summary>
    public static string DeriveAlias(AggregationColumnDto column)
    {
        if (!string.IsNullOrWhiteSpace(column.Alias))
        {
            return column.Alias!;
        }

        if (column.Function == AggregationFunctionDto.count &&
            string.IsNullOrWhiteSpace(column.AttributePath))
        {
            return "count";
        }

        // Default: "<function>_<path>" with dots replaced for valid JSON keys.
        var fn = column.Function.ToString();
        var pathPart = (column.AttributePath ?? "").Replace('.', '_');
        return string.IsNullOrEmpty(pathPart) ? fn : $"{fn}_{pathPart}";
    }

    /// <summary>
    ///     Validates the aggregation column list. Returns null on success or a human-readable error message.
    /// </summary>
    /// <param name="aggregations">The requested aggregation columns.</param>
    /// <param name="allowIntegral">
    ///     True only for the stream-data aggregation and grouping queries, the one place the engine serves
    ///     <see cref="AggregationFunctionDto.integral"/> (AB#7919). Runtime aggregations and downsampling refuse it.
    /// </param>
    public static string? Validate(IReadOnlyList<AggregationColumnDto>? aggregations, bool allowIntegral = false)
    {
        if (aggregations == null || aggregations.Count == 0)
        {
            return "At least one aggregation column is required.";
        }

        var seenAliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < aggregations.Count; i++)
        {
            var col = aggregations[i];

            if (col.Function != AggregationFunctionDto.count &&
                string.IsNullOrWhiteSpace(col.AttributePath))
            {
                return
                    $"aggregations[{i}].attributePath is required for function '{col.Function}'. " +
                    "Only 'count' may omit the path.";
            }

            if (col.Function == AggregationFunctionDto.integral && !allowIntegral)
            {
                return
                    $"aggregations[{i}]: 'integral' is only supported by query_stream_data_aggregation and " +
                    "query_stream_data_grouping (and persisted stream-data aggregation/grouping queries); " +
                    "it is not available for runtime aggregations or downsampling.";
            }

            var alias = DeriveAlias(col);
            if (!seenAliases.Add(alias))
            {
                return
                    $"Duplicate alias '{alias}' in aggregations[{i}]. " +
                    "Set the 'alias' property explicitly to disambiguate.";
            }
        }

        return null;
    }

    /// <summary>
    ///     The integral is computed over the query window, so a request with an <c>integral</c> column needs both
    ///     <c>from</c> and <c>to</c> (AB#7919). Returns null on success or a human-readable error message.
    /// </summary>
    public static string? ValidateIntegralWindow(IReadOnlyList<AggregationColumnDto> aggregations,
        DateTime? from, DateTime? to)
    {
        if (!aggregations.Any(c => c.Function == AggregationFunctionDto.integral))
        {
            return null;
        }

        if (from is null || to is null)
        {
            return "'integral' requires both from and to: it integrates over the query window.";
        }

        return from < to
            ? null
            : $"Invalid time window: from ({from:O}) must be strictly less than to ({to:O}).";
    }

    /// <summary>
    ///     The engine's short function token in stream-data result keys (<c>{column}_{token}</c>, e.g.
    ///     <c>power_avg</c>, <c>power_integral</c>), see <c>CrateDbStreamDataRepository</c>.
    /// </summary>
    public static string EngineResultToken(AggregationFunctionDto dto) => dto switch
    {
        AggregationFunctionDto.count => "count",
        AggregationFunctionDto.sum => "sum",
        AggregationFunctionDto.avg => "avg",
        AggregationFunctionDto.min => "min",
        AggregationFunctionDto.max => "max",
        AggregationFunctionDto.integral => "integral",
        _ => throw new ArgumentOutOfRangeException(nameof(dto), dto, "Unknown aggregation function")
    };

    /// <summary>Validates group-by attribute paths.</summary>
    public static string? ValidateGroupBy(IReadOnlyList<string>? groupByPaths)
    {
        if (groupByPaths == null || groupByPaths.Count == 0)
        {
            return "groupByAttributePaths must contain at least one path.";
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in groupByPaths)
        {
            if (string.IsNullOrWhiteSpace(p))
            {
                return "groupByAttributePaths contains an empty entry.";
            }

            if (!seen.Add(p))
            {
                return $"Duplicate group-by path '{p}'.";
            }
        }

        return null;
    }

    /// <summary>
    ///     Applies a list of aggregation columns to an engine <see cref="AggregationInput"/>. The aliases
    ///     are not consumed by the engine — they are used only when shaping the response.
    /// </summary>
    public static void ApplyToAggregationInput(AggregationInput input,
        IReadOnlyList<AggregationColumnDto> columns)
    {
        foreach (var col in columns)
        {
            switch (col.Function)
            {
                case AggregationFunctionDto.count:
                    // Count without path → engine counts row existence. With path → counts non-null values.
                    if (string.IsNullOrWhiteSpace(col.AttributePath))
                    {
                        input.CountAttributePaths();
                    }
                    else
                    {
                        input.CountAttributePaths(col.AttributePath!);
                    }
                    break;
                case AggregationFunctionDto.sum:
                    input.SumAttributePaths(col.AttributePath!);
                    break;
                case AggregationFunctionDto.avg:
                    input.AvgAttributePaths(col.AttributePath!);
                    break;
                case AggregationFunctionDto.min:
                    input.MinAttributePaths(col.AttributePath!);
                    break;
                case AggregationFunctionDto.max:
                    input.MaxAttributePaths(col.AttributePath!);
                    break;
                case AggregationFunctionDto.integral:
                    // A persisted runtime aggregation query may carry the CK enum value Integral (System 2.8.0);
                    // runtime entities have no time axis to integrate over.
                    throw new ArgumentException(
                        $"'integral' on '{col.AttributePath}' is only supported by stream-data queries, not by runtime aggregations.");
            }
        }
    }

    /// <summary>
    ///     Maps to the engine <see cref="AggregationColumn"/> records used by stream-data queries.
    /// </summary>
    public static IReadOnlyList<AggregationColumn> ToEngineColumns(
        IReadOnlyList<AggregationColumnDto> columns) =>
        columns
            .Select(c => new AggregationColumn(
                c.AttributePath ?? "*",
                ToEngineFunction(c.Function)))
            .ToList();

    /// <summary>
    ///     Maps the CK <c>AggregationTypes</c> enum name (Count / Minimum / Maximum / Average / Sum / Integral, plus the
    ///     legacy short forms Min/Max/Avg) to the MCP-side <see cref="AggregationFunctionDto"/>.
    ///     The persisted query columns carry the enum value as a CK enum — we read the name and map.
    /// </summary>
    public static AggregationFunctionDto MapCkAggregationName(string ckEnumName) => ckEnumName switch
    {
        "Count" => AggregationFunctionDto.count,
        "Sum" => AggregationFunctionDto.sum,
        "Average" => AggregationFunctionDto.avg,
        "Avg" => AggregationFunctionDto.avg,
        "Minimum" => AggregationFunctionDto.min,
        "Min" => AggregationFunctionDto.min,
        "Maximum" => AggregationFunctionDto.max,
        "Max" => AggregationFunctionDto.max,
        "Integral" => AggregationFunctionDto.integral,
        _ => throw new ArgumentOutOfRangeException(nameof(ckEnumName), ckEnumName,
            $"Unknown CK aggregation type: {ckEnumName}")
    };
}
