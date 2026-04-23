using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;

namespace Meshmakers.Octo.Backend.McpServices.Models;

/// <summary>
///     Response for recursive association tree traversal
/// </summary>
public sealed class AssociationTreeResponse : ErrorResponse
{
    /// <summary>
    ///     The CK Role ID used for traversal
    /// </summary>
    public required string CkRoleId { get; init; }

    /// <summary>
    ///     The direction of traversal
    /// </summary>
    public required string Direction { get; init; }

    /// <summary>
    ///     Maximum depth of traversal
    /// </summary>
    public int MaxDepth { get; init; }

    /// <summary>
    ///     The tree nodes with their children
    /// </summary>
    public IList<AssociationTreeNode>? Nodes { get; init; }
}

/// <summary>
///     A node in the association tree with optional children
/// </summary>
public sealed class AssociationTreeNode
{
    /// <summary>
    ///     Runtime ID of this entity
    /// </summary>
    public required string RtId { get; init; }

    /// <summary>
    ///     CK Type ID of this entity
    /// </summary>
    public required string CkTypeId { get; init; }

    /// <summary>
    ///     Entity attributes (filtered by attributePaths if specified)
    /// </summary>
    public IList<RtEntityAttributeDto>? Attributes { get; init; }

    /// <summary>
    ///     Child nodes at the next depth level
    /// </summary>
    public IList<AssociationTreeNode>? Children { get; init; }
}
