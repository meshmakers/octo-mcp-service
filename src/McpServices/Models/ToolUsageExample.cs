namespace Meshmakers.Octo.Backend.McpServices.Models;

/// <summary>
///     Usage example for a tool
/// </summary>
public sealed class ToolUsageExample
{
    /// <summary>
    ///     Description of what this example demonstrates
    /// </summary>
    public required string Description { get; init; }

    /// <summary>
    ///     Example parameters to use with the tool
    /// </summary>
    public required object Parameters { get; init; }
}