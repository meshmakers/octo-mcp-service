namespace Meshmakers.Octo.Backend.McpServices.Models;

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