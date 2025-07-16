namespace Meshmakers.Octo.Backend.McpServices.Models;

/// <summary>
/// Parameter validation result for a single parameter
/// </summary>
public sealed class ParameterValidationResult
{
    /// <summary>
    /// Name of the parameter that was validated
    /// </summary>
    public required string Parameter { get; init; }
    
    /// <summary>
    /// Validation status (e.g., 'valid', 'invalid', 'missing')
    /// </summary>
    public required string Status { get; init; }
    
    /// <summary>
    /// Expected type for this parameter
    /// </summary>
    public required string Type { get; init; }
    
    /// <summary>
    /// Value that was provided for validation
    /// </summary>
    public string? ProvidedValue { get; init; }
}