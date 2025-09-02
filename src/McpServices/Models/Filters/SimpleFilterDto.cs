namespace Meshmakers.Octo.Backend.McpServices.Models.Filters;

/// <summary>
///     Defines a simple filter with an attribute path and a value
/// </summary>
public class SimpleFilterDto
{
    /// <summary>
    ///     The attribute path to filter on
    /// </summary>
    public required string AttributePath { get; set; }

    /// <summary>
    ///     The value to filter by
    /// </summary>
    public object? Value { get; set; }
}