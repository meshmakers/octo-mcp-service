using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;

namespace Meshmakers.Octo.Backend.McpServices.Models;

/// <summary>
/// Response for entity query operations
/// </summary>
public sealed class QueryEntitiesResponse
{
    /// <summary>
    /// Construction Kit Type ID that was queried
    /// </summary>
    public required string CkTypeId { get; init; }
    
    /// <summary>
    /// Total number of entities matching the query criteria (before pagination)
    /// </summary>
    public required long TotalCount { get; init; }
    
    /// <summary>
    /// Number of entities returned in this response (after pagination)
    /// </summary>
    public required int ReturnedCount { get; init; }
    
    /// <summary>
    /// Collection of entity data matching the query
    /// </summary>
    public required IList<RtEntityDto> Entities { get; init; }
}