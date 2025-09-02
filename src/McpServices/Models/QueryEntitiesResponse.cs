using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;

namespace Meshmakers.Octo.Backend.McpServices.Models;

/// <summary>
///     Response for entity query operations
/// </summary>
public sealed class QueryEntitiesResponse : ErrorResponse
{
    /// <summary>
    ///     Construction Kit Type ID that was queried
    /// </summary>
    public required string CkTypeId { get; init; }

    /// <summary>
    ///     Total number of entities matching the query criteria (before pagination)
    /// </summary>
    public long? TotalCount { get; init; }

    /// <summary>
    ///     Number of entities returned in this response (after pagination)
    /// </summary>
    public int? ReturnedCount { get; init; }

    /// <summary>
    ///     Collection of entity data matching the query
    /// </summary>
    public IList<RtEntityDto>? Entities { get; init; }
}