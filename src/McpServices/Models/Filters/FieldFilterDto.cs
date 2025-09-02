using System.Text.Json.Serialization;

namespace Meshmakers.Octo.Backend.McpServices.Models.Filters;

/// <summary>
///     Field filter for a specific attribute with operator and value
/// </summary>
public class FieldFilterDto
{
    /// <summary>
    ///     Path to the attribute (can be dot-separated for nested fields)
    /// </summary>
    public string AttributePath { get; set; } = string.Empty;

    /// <summary>
    ///     Operator for the filter
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public FilterOperatorDto Operator { get; set; }

    /// <summary>
    ///     Value for the filter
    /// </summary>
    public object? Value { get; set; }

    /// <summary>
    ///     Secondary value for between operators
    /// </summary>
    public object? SecondValue { get; set; }
}