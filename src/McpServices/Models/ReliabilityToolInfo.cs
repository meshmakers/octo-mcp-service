namespace Meshmakers.Octo.Backend.McpServices.Models;

/// <summary>
///     Reliability information for a specific tool
/// </summary>
public sealed class ReliabilityToolInfo
{
    /// <summary>
    ///     Name of the tool
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    ///     Success rate as a percentage
    /// </summary>
    public required double SuccessRate { get; init; }
}