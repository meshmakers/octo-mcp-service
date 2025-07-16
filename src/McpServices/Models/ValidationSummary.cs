namespace Meshmakers.Octo.Backend.McpServices.Models;

/// <summary>
/// Summary of parameter validation results
/// </summary>
public sealed class ValidationSummary
{
    /// <summary>
    /// Total number of parameters provided
    /// </summary>
    public required int TotalProvided { get; init; }
    
    /// <summary>
    /// Number of required parameters that are missing
    /// </summary>
    public required int RequiredMissing { get; init; }
    
    /// <summary>
    /// Number of unknown parameters that will be ignored
    /// </summary>
    public required int UnknownParams { get; init; }
    
    /// <summary>
    /// Recommendation based on validation results
    /// </summary>
    public required string Recommendation { get; init; }
}