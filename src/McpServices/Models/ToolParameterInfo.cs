namespace Meshmakers.Octo.Backend.McpServices.Models;

/// <summary>
///     Information about a single tool parameter
/// </summary>
public sealed class ToolParameterInfo
{
    /// <summary>
    ///     Name of the parameter
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    ///     Data type of the parameter (e.g., 'string', 'integer', 'boolean')
    /// </summary>
    public required string Type { get; init; }

    /// <summary>
    ///     Indicates if this parameter is optional
    /// </summary>
    public required bool IsOptional { get; init; }

    /// <summary>
    ///     Default value if the parameter is optional
    /// </summary>
    public string? DefaultValue { get; init; }

    /// <summary>
    ///     Description of what this parameter does
    /// </summary>
    public required string Description { get; init; }
}