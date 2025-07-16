namespace Meshmakers.Octo.Backend.McpServices.Models;

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