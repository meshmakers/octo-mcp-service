using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;

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

    /// <summary>
    ///     <c>true</c> when the caller passed <c>expected_version</c> on the request and the
    ///     stored <c>RtVersion</c> did not match — the delete was refused. See
    ///     <see cref="UpdateEntityResponse.IsConflict" /> for the contract.
    /// </summary>
    public bool IsConflict { get; init; }

    /// <summary>
    ///     Server-side <c>RtVersion</c> at the moment of the refused delete. Useful so the
    ///     caller can decide between retrying the delete and aborting (someone else likely
    ///     wrote to the entity since the caller last read it).
    /// </summary>
    public ulong? CurrentRtVersion { get; init; }

    /// <summary>
    ///     Current server-side payload of the entity when a conflict refused the delete.
    ///     Lets the caller diff "what I expected to delete" against "what's actually there"
    ///     before re-issuing.
    /// </summary>
    public RtEntityDto? Entity { get; init; }
}