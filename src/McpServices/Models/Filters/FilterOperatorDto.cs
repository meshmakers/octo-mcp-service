namespace Meshmakers.Octo.Backend.McpServices.Models.Filters;

/// <summary>
/// Verfügbare Filter-Operatoren für Felder
/// </summary>
public enum FilterOperatorDto
{
    /// <summary>Gleichheit</summary>
    Equals,
    
    /// <summary>Ungleichheit</summary>
    NotEquals,
    
    /// <summary>Enthält Substring</summary>
    Contains,
    
    /// <summary>Beginnt mit</summary>
    StartsWith,
    
    /// <summary>Endet mit</summary>
    EndsWith,
    
    /// <summary>Größer als</summary>
    GreaterThan,
    
    /// <summary>Größer oder gleich</summary>
    GreaterThanOrEqual,
    
    /// <summary>Kleiner als</summary>
    LessThan,
    
    /// <summary>Kleiner oder gleich</summary>
    LessThanOrEqual,
    
    /// <summary>Zwischen zwei Werten</summary>
    Between,
    
    /// <summary>In Liste von Werten</summary>
    In,
    
    /// <summary>Nicht in Liste von Werten</summary>
    NotIn,
    
    /// <summary>Ist NULL</summary>
    IsNull,
    
    /// <summary>Ist nicht NULL</summary>
    IsNotNull,
    
    /// <summary>Regulärer Ausdruck</summary>
    Regex
}
