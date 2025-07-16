// ReSharper disable UnusedAutoPropertyAccessor.Global
namespace Meshmakers.Octo.Backend.McpServices.Models;

/// <summary>
/// Error response for tool management operations
/// </summary>
// ReSharper disable once ClassNeverInstantiated.Global
public sealed class ToolManagementError
{
    /// <summary>
    /// Short error description
    /// </summary>
    public required string Error { get; init; }
    
    /// <summary>
    /// Detailed error message
    /// </summary>
    public required string Message { get; init; }
    
    /// <summary>
    /// Tool name related to the error, if applicable
    /// </summary>
    public string? ToolName { get; init; }
    
    /// <summary>
    /// Suggestion for resolving the error
    /// </summary>
    public string? Suggestion { get; init; }
}
