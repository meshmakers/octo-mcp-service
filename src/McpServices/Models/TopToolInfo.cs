namespace Meshmakers.Octo.Backend.McpServices.Models;

/// <summary>
/// Information about a frequently used tool
/// </summary>
public sealed class TopToolInfo
{
    /// <summary>
    /// Name of the tool
    /// </summary>
    public required string Name { get; init; }
    
    /// <summary>
    /// Number of times this tool was called
    /// </summary>
    public required int Invocations { get; init; }
    
    /// <summary>
    /// Average response time for this tool
    /// </summary>
    public required string AvgResponseTime { get; init; }
}