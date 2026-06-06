using System.Text.Json.Serialization;

namespace Meshmakers.Octo.Backend.McpServices.Models.Aggregation;

/// <summary>Sort direction.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SortDirectionDto
{
    /// <summary>Ascending (default).</summary>
    asc = 0,

    /// <summary>Descending.</summary>
    desc = 1
}

/// <summary>
///     Sort specification — attribute path + direction. Used by aggregation + stream-data tools.
/// </summary>
public class SortColumnDto
{
    /// <summary>Attribute path to sort by.</summary>
    public string AttributePath { get; set; } = string.Empty;

    /// <summary>Sort direction (asc / desc). Default ascending.</summary>
    public SortDirectionDto Direction { get; set; } = SortDirectionDto.asc;
}
