using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;

namespace Meshmakers.Octo.Backend.McpServices.Models;

/// <summary>
///     Response for entity creation operations
/// </summary>
public sealed class CreateEntityResponse : ErrorResponse
{
    /// <summary>
    ///     Construction Kit Type ID of the created entity
    /// </summary>
    public required string CkTypeId { get; init; }

    /// <summary>
    ///     Runtime ID assigned to the newly created entity
    /// </summary>
    public string? RtId { get; init; }

    /// <summary>
    ///     Complete entity data after creation
    /// </summary>
    public RtEntityDto? Entity { get; init; }
}