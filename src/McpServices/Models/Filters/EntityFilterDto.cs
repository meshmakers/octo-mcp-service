using System.Text.Json.Serialization;

namespace Meshmakers.Octo.Backend.McpServices.Models.Filters;

/// <summary>
/// Typisierte Filter-Definition für Entity-Queries
/// </summary>
public class EntityFilterDto
{
    /// <summary>
    /// Liste von Feld-Filtern
    /// </summary>
    public List<FieldFilterDto> Fields { get; set; } = new();
    
    /// <summary>
    /// Logischer Operator für die Verknüpfung der Fields
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public LogicalOperatorDto Operator { get; set; } = LogicalOperatorDto.And;
    
    /// <summary>
    /// Verschachtelte Filter für komplexe Logik
    /// </summary>
    public List<EntityFilterDto>? NestedFilters { get; set; }
}
