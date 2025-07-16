namespace Meshmakers.Octo.Backend.McpServices.Models;

/// <summary>
/// Response for parameter validation
/// </summary>
public sealed class ValidateParametersResponse
{
    /// <summary>
    /// Indicates if all provided parameters are valid
    /// </summary>
    public required bool IsValid { get; init; }
    
    /// <summary>
    /// Name of the tool being validated
    /// </summary>
    public required string ToolName { get; init; }
    
    /// <summary>
    /// List of parameter names that were provided
    /// </summary>
    public required List<string> ProvidedParameters { get; init; }
    
    /// <summary>
    /// Detailed validation results for each parameter
    /// </summary>
    public required List<ParameterValidationResult> ValidationResults { get; init; }
    
    /// <summary>
    /// Warning messages about the provided parameters
    /// </summary>
    public required List<string> Warnings { get; init; }
    
    /// <summary>
    /// Error messages about invalid or missing parameters
    /// </summary>
    public required List<string> Errors { get; init; }
    
    /// <summary>
    /// Summary of the validation results
    /// </summary>
    public required ValidationSummary Summary { get; init; }
}