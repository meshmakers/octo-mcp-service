namespace Meshmakers.Octo.Backend.McpServices.Models;

/// <summary>
///     Detailed information about a specific tool
/// </summary>
public sealed class ToolDetailsResponse
{
    /// <summary>
    ///     Name of the tool
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    ///     Category the tool belongs to
    /// </summary>
    public required string Category { get; init; }

    /// <summary>
    ///     Detailed description of the tool's functionality
    /// </summary>
    public required string Description { get; init; }

    /// <summary>
    ///     .NET class name implementing the tool
    /// </summary>
    public required string ClassName { get; init; }

    /// <summary>
    ///     .NET method name implementing the tool
    /// </summary>
    public required string MethodName { get; init; }

    /// <summary>
    ///     Return type of the tool method
    /// </summary>
    public required string ReturnType { get; init; }

    /// <summary>
    ///     All parameters accepted by this tool
    /// </summary>
    public required List<ToolParameterInfo> Parameters { get; init; }

    /// <summary>
    ///     Parameters that must be provided (no default values)
    /// </summary>
    public required List<ToolParameterInfo> RequiredParameters { get; init; }

    /// <summary>
    ///     Parameters that have default values and are optional
    /// </summary>
    public required List<ToolParameterInfo> OptionalParameters { get; init; }

    /// <summary>
    ///     Example usage scenarios for this tool
    /// </summary>
    public required List<ToolUsageExample> UsageExamples { get; init; }

    /// <summary>
    ///     Additional notes and tips for using this tool
    /// </summary>
    public required List<string> Notes { get; init; }

    /// <summary>
    ///     Return type description, if applicable
    /// </summary>
    public string? ReturnDescription { get; init; }
}