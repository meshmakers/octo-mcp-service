namespace Meshmakers.Octo.Backend.McpServices.Models;

/// <summary>
///     Basic information about an available tool
/// </summary>
public sealed class ToolInfo
{
    /// <summary>
    ///     Name of the tool as used in MCP calls
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    ///     Category this tool belongs to (e.g., 'CRUD Operations', 'Schema Discovery')
    /// </summary>
    public required string Category { get; init; }

    /// <summary>
    ///     Human-readable description of what the tool does
    /// </summary>
    public required string Description { get; init; }

    /// <summary>
    ///     .NET class name containing the tool implementation
    /// </summary>
    public required string ClassName { get; init; }

    /// <summary>
    ///     .NET method name implementing the tool
    /// </summary>
    public required string MethodName { get; init; }

    /// <summary>
    ///     List of parameters this tool accepts
    /// </summary>
    public required List<ToolParameterInfo> Parameters { get; init; }

    /// <summary>
    ///     Total number of parameters
    /// </summary>
    public required int ParameterCount { get; init; }

    /// <summary>
    ///     Indicates if this tool has any optional parameters
    /// </summary>
    public required bool HasOptionalParams { get; init; }
}