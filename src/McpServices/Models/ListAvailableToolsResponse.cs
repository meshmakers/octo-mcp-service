namespace Meshmakers.Octo.Backend.McpServices.Models;

/// <summary>
/// Response for listing available tools
/// </summary>
public sealed class ListAvailableToolsResponse
{
    /// <summary>
    /// Total number of available tools
    /// </summary>
    public required int TotalTools { get; init; }
    
    /// <summary>
    /// Breakdown of tools by category with counts
    /// </summary>
    public required Dictionary<string, int> Categories { get; init; }
    
    /// <summary>
    /// Category filter that was applied, if any
    /// </summary>
    public string? CategoryFilter { get; init; }
    
    /// <summary>
    /// List of available tools
    /// </summary>
    public required List<ToolInfo> Tools { get; init; }
}