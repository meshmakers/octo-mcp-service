namespace Meshmakers.Octo.Backend.McpServices.Models;

/// <summary>
///     Performance metrics for tools
/// </summary>
public sealed class PerformanceMetrics
{
    /// <summary>
    ///     Tool with the best average response time
    /// </summary>
    public required PerformanceToolInfo FastestTool { get; init; }

    /// <summary>
    ///     Tool with the worst average response time
    /// </summary>
    public required PerformanceToolInfo SlowestTool { get; init; }

    /// <summary>
    ///     Tool with the highest success rate
    /// </summary>
    public required ReliabilityToolInfo MostReliable { get; init; }

    /// <summary>
    ///     Tool with the lowest success rate
    /// </summary>
    public required ReliabilityToolInfo LeastReliable { get; init; }
}