using System.Text.Json.Serialization;

namespace Meshmakers.Octo.Backend.McpServices.Models.Filters;

/// <summary>
///     Typed filter definition for entity queries
/// </summary>
public class FieldFilterCriteriaDto
{
    /// <summary>
    ///     List of field filters
    /// </summary>
    public List<FieldFilterDto> Fields { get; set; } = [];

    /// <summary>
    ///     Logical operator for combining the fields
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public LogicalOperatorDto Operator { get; set; } = LogicalOperatorDto.And;

    /// <summary>
    ///     Nested filters for complex logic
    /// </summary>
    public List<FieldFilterCriteriaDto>? NestedFilters { get; set; }
}