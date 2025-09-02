using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;

namespace Meshmakers.Octo.Backend.McpServices.Models;

/// <summary>
///     Response for entity update operations
/// </summary>
public sealed class UpdateEntityResponse : ErrorResponse
{
    /// <summary>
    ///     Construction Kit Type ID of the updated entity
    /// </summary>
    public required string TypeId { get; init; }

    /// <summary>
    ///     Runtime ID of the updated entity
    /// </summary>
    public string? RtId { get; init; }

    /// <summary>
    ///     Complete entity data after update
    /// </summary>
    public RtEntityDto? Entity { get; init; }
}