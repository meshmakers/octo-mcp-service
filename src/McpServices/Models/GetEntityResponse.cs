using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;

namespace Meshmakers.Octo.Backend.McpServices.Models;

/// <summary>
///     Response for getting a single entity by ID
/// </summary>
public sealed class GetEntityResponse : ErrorResponse
{
    /// <summary>
    ///     Construction Kit Type ID of the entity
    /// </summary>
    public required string TypeId { get; init; }

    /// <summary>
    ///     Entity data, or null if not found
    /// </summary>
    public RtEntityDto? Entity { get; init; }
}