// ReSharper disable UnusedAutoPropertyAccessor.Global
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

/// <summary>
/// Response for getting a single entity by ID
/// </summary>
public sealed class GetEntityResponse
{
    /// <summary>
    /// Construction Kit Type ID of the entity
    /// </summary>
    public required string TypeId { get; init; }
    
    /// <summary>
    /// Entity data, or null if not found
    /// </summary>
    public object? Entity { get; init; }
}

/// <summary>
/// Response for entity creation operations
/// </summary>
public sealed class CreateEntityResponse
{
    /// <summary>
    /// Indicates if the entity was successfully created
    /// </summary>
    public required bool Success { get; init; }
    
    /// <summary>
    /// Construction Kit Type ID of the created entity
    /// </summary>
    public required string CkTypeId { get; init; }
    
    /// <summary>
    /// Runtime ID assigned to the newly created entity
    /// </summary>
    public required string RtId { get; init; }
    
    /// <summary>
    /// Complete entity data after creation
    /// </summary>
    public object? Entity { get; init; }
}

/// <summary>
/// Response for entity update operations
/// </summary>
public sealed class UpdateEntityResponse
{
    /// <summary>
    /// Indicates if the entity was successfully updated
    /// </summary>
    public required bool Success { get; init; }
    
    /// <summary>
    /// Construction Kit Type ID of the updated entity
    /// </summary>
    public required string TypeId { get; init; }
    
    /// <summary>
    /// Runtime ID of the updated entity
    /// </summary>
    public required string RtId { get; init; }
    
    /// <summary>
    /// Complete entity data after update
    /// </summary>
    public object? Entity { get; init; }
}

/// <summary>
/// Response for entity deletion operations
/// </summary>
public sealed class DeleteEntityResponse
{
    /// <summary>
    /// Indicates if the entity was successfully deleted
    /// </summary>
    public required bool Success { get; init; }
    
    /// <summary>
    /// Confirmation message about the deletion
    /// </summary>
    public required string Message { get; init; }
    
    /// <summary>
    /// Construction Kit Type ID of the deleted entity
    /// </summary>
    public required string CkTypeId { get; init; }
    
    /// <summary>
    /// Runtime ID of the deleted entity
    /// </summary>
    public required string RtId { get; init; }
}

/// <summary>
/// Response for entity not found scenarios
/// </summary>
public sealed class EntityNotFoundResponse
{
    /// <summary>
    /// Error message indicating the entity was not found
    /// </summary>
    public required string Error { get; init; }
    
    /// <summary>
    /// Runtime ID of the entity that was not found
    /// </summary>
    public required string RtId { get; init; }
    
    /// <summary>
    /// Construction Kit Type ID that was searched
    /// </summary>
    public required string CkTypeId { get; init; }
}

/// <summary>
/// Error response with entity operation context
/// </summary>
public sealed class EntityOperationError
{
    /// <summary>
    /// Short error description
    /// </summary>
    public required string Error { get; init; }
    
    /// <summary>
    /// Detailed error message with technical information
    /// </summary>
    public required string Message { get; init; }
    
    /// <summary>
    /// Runtime ID related to the failed operation, if applicable
    /// </summary>
    public string? RtId { get; init; }
    
    /// <summary>
    /// Construction Kit Type ID related to the failed operation, if applicable
    /// </summary>
    public string? CkTypeId { get; init; }
    
    /// <summary>
    /// Alternative entity ID field for legacy compatibility
    /// </summary>
    public string? EntityId { get; init; }
}
