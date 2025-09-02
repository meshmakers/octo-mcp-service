namespace Meshmakers.Octo.Backend.McpServices.Models;

/// <summary>
///     Response for available types query
/// </summary>
public sealed class AvailableTypesResponse : ErrorResponse
{
    /// <summary>
    ///     Total number of types returned
    /// </summary>
    public int? TotalTypes { get; init; }

    /// <summary>
    ///     Whether abstract types were included in the query
    /// </summary>
    public bool? IncludeAbstract { get; init; }

    /// <summary>
    ///     Model ID filter that was applied, if any
    /// </summary>
    public string? ModelIdFilter { get; init; }

    /// <summary>
    ///     List of type metadata matching the query criteria
    /// </summary>
    public List<CkTypeInfo>? Types { get; init; }
}