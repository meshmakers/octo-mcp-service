namespace Meshmakers.Octo.Backend.McpServices.Models;

/// <summary>
/// Schema response for a specific association
/// </summary>
public sealed class AssociationSchemaResponse
{
    /// <summary>
    /// Full association identifier
    /// </summary>
    public required string AssociationRoleId { get; init; }

    /// <summary>
    /// Type of the associated entity
    /// </summary>
    public required string TargetCkTypeId { get; init; }

    /// <summary>
    /// Direction of the association (inbound or outbound)
    /// </summary>
    public required CkTypeAssociationDirectionDto Direction { get; init; }

    /// <summary>
    /// Cardinality of the association (single or multiple)
    /// </summary>
    public required CkTypeAssociationCardinalityDto Cardinality { get; init; }
}