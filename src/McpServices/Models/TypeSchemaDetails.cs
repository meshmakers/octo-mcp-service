namespace Meshmakers.Octo.Backend.McpServices.Models;

/// <summary>
/// Schema details for type creation and validation
/// </summary>
public sealed class TypeSchemaDetails
{
    /// <summary>
    /// Indicates if entities of this type can be created (not abstract)
    /// </summary>
    public required bool CanCreate { get; init; }
    
    /// <summary>
    /// Attributes that must be provided when creating entities
    /// </summary>
    public object? RequiredAttributes { get; init; }
    
    /// <summary>
    /// Attributes that are optional when creating entities
    /// </summary>
    public object? OptionalAttributes { get; init; }
}