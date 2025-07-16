namespace Meshmakers.Octo.Backend.McpServices.Models;

/// <summary>
/// Performance information for a specific tool
/// </summary>
public sealed class PerformanceToolInfo
{
    /// <summary>
    /// Name of the tool
    /// </summary>
    public required string Name { get; init; }
    
    /// <summary>
    /// Average response time
    /// </summary>
    public required string AvgTime { get; init; }
}