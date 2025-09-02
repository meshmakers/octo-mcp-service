namespace Meshmakers.Octo.Backend.McpServices.Models;

/// <summary>
///     Response for entity deletion operations
/// </summary>
public sealed class DeleteEntityResponse : ErrorResponse
{
    /// <summary>
    ///     Construction Kit Type ID of the deleted entity
    /// </summary>
    public required string CkTypeId { get; init; }

    /// <summary>
    ///     Runtime ID of the deleted entity
    /// </summary>
    public required string RtId { get; init; }
}