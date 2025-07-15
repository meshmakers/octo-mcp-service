using System.Text.Json.Serialization;

namespace Meshmakers.Octo.Backend.McpServices.Models.Filters;

/// <summary>
/// Filter für ein einzelnes Feld
/// </summary>
public class FieldFilterDto
{
    /// <summary>
    /// Pfad zum Feld (kann dot-separated sein für verschachtelte Felder)
    /// </summary>
    public string FieldPath { get; set; } = string.Empty;
    
    /// <summary>
    /// Filter-Operator
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public FilterOperatorDto Operator { get; set; }
    
    /// <summary>
    /// Primärer Wert für den Filter
    /// </summary>
    public object? Value { get; set; }
    
    /// <summary>
    /// Sekundärer Wert (z.B. für Between-Operationen)
    /// </summary>
    public object? SecondValue { get; set; }
}
