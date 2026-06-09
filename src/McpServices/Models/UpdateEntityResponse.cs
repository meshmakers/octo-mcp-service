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
    ///     Complete entity data after update. On a conflict (see
    ///     <see cref="IsConflict" />) this carries the current server-side payload so the
    ///     caller can rebase its write on the latest state instead of issuing a fresh
    ///     <c>get_entity_by_id</c> round-trip.
    /// </summary>
    public RtEntityDto? Entity { get; init; }

    /// <summary>
    ///     <c>true</c> when the caller passed <c>expected_version</c> on the request and the
    ///     stored <c>RtVersion</c> did not match — the write was refused. The pair (
    ///     <see cref="IsConflict" />, <see cref="CurrentRtVersion" />, <see cref="Entity" />)
    ///     gives the caller everything needed to retry: detect, re-read, merge, re-send with
    ///     the new <c>expected_version</c>. Unset (defaults to <c>false</c>) on every other
    ///     failure mode so callers can discriminate "stale" from "broken".
    /// </summary>
    public bool IsConflict { get; init; }

    /// <summary>
    ///     The server's current <c>RtVersion</c> for the entity. Always set on a successful
    ///     update (the post-bump value, so a caller that immediately re-updates passes this
    ///     value as the next <c>expected_version</c>). Also set on a conflict so the caller
    ///     can resync. <c>null</c> when the entity could not be loaded at all (404-style
    ///     error path).
    /// </summary>
    public ulong? CurrentRtVersion { get; init; }
}