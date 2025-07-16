namespace Meshmakers.Octo.Backend.McpServices.Models;

/// <summary>
/// Response for type search
/// </summary>
public sealed class SearchTypesResponse
{
    /// <summary>
    /// The search term that was used
    /// </summary>
    public required string SearchTerm { get; init; }
    
    /// <summary>
    /// Number of types that matched the search criteria
    /// </summary>
    public required int MatchCount { get; init; }
    
    /// <summary>
    /// Whether abstract types were included in the search
    /// </summary>
    public required bool IncludeAbstract { get; init; }
    
    /// <summary>
    /// List of types that matched the search criteria
    /// </summary>
    public required List<CkTypeMetadata> Matches { get; init; }
}