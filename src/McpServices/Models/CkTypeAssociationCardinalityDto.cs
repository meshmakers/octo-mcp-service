namespace Meshmakers.Octo.Backend.McpServices.Models;

/// <summary>
/// Association cardinality for Construction Kit associations
/// </summary>
public enum CkTypeAssociationCardinalityDto
{
    /// <summary>Multiplicity zero or one.</summary>
    ZeroOrOne,
    /// <summary>Multiplicity one.</summary>
    One,
    /// <summary>Multiplicity more than one.</summary>
    N,
}