namespace Meshmakers.Octo.Backend.McpServices.Models;

/// <summary>
///     Breakdown of tool usage by category
/// </summary>
public sealed class CategoryBreakdownInfo
{
    /// <summary>
    ///     Percentage of CRUD operation calls
    /// </summary>
    public required double Crud { get; init; }

    /// <summary>
    ///     Percentage of analytics calls
    /// </summary>
    public required double Analytics { get; init; }

    /// <summary>
    ///     Percentage of discovery calls
    /// </summary>
    public required double Discovery { get; init; }

    /// <summary>
    ///     Percentage of maintenance calls
    /// </summary>
    public required double Maintenance { get; init; }

    /// <summary>
    ///     Percentage of management calls
    /// </summary>
    public required double Management { get; init; }
}