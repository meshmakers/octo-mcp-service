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
    ///     Machine-readable error code, set for refusals with a contract. <c>GENERIC_CREATE_REPLACED_BY_METHOD</c>:
    ///     the type's generic create is replaced by a CK method; create the entity through that method.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? ErrorCode { get; init; }

    /// <summary>
    ///     Language-neutral parameters of <see cref="ErrorCode" /> (for <c>GENERIC_CREATE_REPLACED_BY_METHOD</c>:
    ///     <c>ckTypeId</c> and <c>methodId</c>).
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, string>? ErrorParameters { get; init; }

    /// <summary>
    ///     Runtime ID assigned to the newly created entity
    /// </summary>
    public string? RtId { get; init; }

    /// <summary>
    ///     Complete entity data after creation
    /// </summary>
    public RtEntityDto? Entity { get; init; }
}