namespace Meshmakers.Octo.Backend.McpServices.Models;

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