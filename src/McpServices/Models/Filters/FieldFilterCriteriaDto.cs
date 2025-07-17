using System.Text.Json.Serialization;

namespace Meshmakers.Octo.Backend.McpServices.Models.Filters;

/// <summary>
/// Typisierte Filter-Definition für Entity-Queries
/// </summary>
public class FieldFilterCriteriaDto
{
    /// <summary>
    /// Liste von Feld-Filtern
    /// </summary>
    public List<FieldFilterDto> Fields { get; set; } = [];
    
    /// <summary>
    /// Logischer Operator für die Verknüpfung der Fields
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public LogicalOperatorDto Operator { get; set; } = LogicalOperatorDto.And;
    
    /// <summary>
    /// Verschachtelte Filter für komplexe Logik
    /// </summary>
    public List<FieldFilterCriteriaDto>? NestedFilters { get; set; }
}
