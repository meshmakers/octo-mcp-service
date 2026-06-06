namespace Meshmakers.Octo.Backend.McpServices.Models;

/// <summary>
///     One row describing an attribute path reachable from a CK type, returned by
///     <c>get_available_archive_paths</c>. Mirrors the GraphQL <c>ArchivePathInfo</c> shape but with
///     plain string fields so MCP clients don't have to know CK contract enums.
/// </summary>
public sealed record ArchivePathInfo(
    string Path,
    string? PrimitiveType,
    bool IsRecord,
    bool IsArray,
    string? RecordTypeId);

/// <summary>Response of <c>get_available_archive_paths</c>.</summary>
public sealed class AvailableArchivePathsResponse : ErrorResponse
{
    /// <summary>CK type the introspection ran against.</summary>
    public string? CkTypeId { get; init; }

    /// <summary>Effective recursion depth cap that was applied.</summary>
    public int? MaxDepth { get; init; }

    /// <summary>All reachable attribute paths within the depth cap, in walk order.</summary>
    public List<ArchivePathInfo>? Paths { get; init; }

    /// <summary>Number of paths returned.</summary>
    public int? PathCount { get; init; }
}
