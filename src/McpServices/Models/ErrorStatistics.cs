namespace Meshmakers.Octo.Backend.McpServices.Models;

/// <summary>
/// Error statistics for tool usage
/// </summary>
public sealed class ErrorStatistics
{
    /// <summary>
    /// Total number of errors in the time period
    /// </summary>
    public required int TotalErrors { get; init; }
    
    /// <summary>
    /// Most common error types
    /// </summary>
    public required List<CommonErrorInfo> CommonErrors { get; init; }
}