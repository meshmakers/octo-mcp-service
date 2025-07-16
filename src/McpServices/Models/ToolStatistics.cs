namespace Meshmakers.Octo.Backend.McpServices.Models;

/// <summary>
/// Statistics about tool performance
/// </summary>
public sealed class ToolStatistics
{
    /// <summary>
    /// Time range these statistics cover
    /// </summary>
    public required string TimeRange { get; init; }
    
    /// <summary>
    /// When these statistics were generated
    /// </summary>
    public required DateTime GeneratedAt { get; init; }
    
    /// <summary>
    /// Total number of tool invocations in the time period
    /// </summary>
    public required int TotalInvocations { get; init; }
    
    /// <summary>
    /// Number of unique tools that were used
    /// </summary>
    public required int UniqueTools { get; init; }
    
    /// <summary>
    /// Average response time across all tools
    /// </summary>
    public required string AverageResponseTime { get; init; }
    
    /// <summary>
    /// Overall success rate as a percentage
    /// </summary>
    public required double SuccessRate { get; init; }
    
    /// <summary>
    /// Most frequently used tools
    /// </summary>
    public required List<TopToolInfo> TopTools { get; init; }
    
    /// <summary>
    /// Breakdown of tool usage by category
    /// </summary>
    public required CategoryBreakdownInfo CategoryBreakdown { get; init; }
    
    /// <summary>
    /// Error statistics and common issues
    /// </summary>
    public required ErrorStatistics ErrorStats { get; init; }
    
    /// <summary>
    /// Performance metrics for tools
    /// </summary>
    public required PerformanceMetrics Performance { get; init; }
}