namespace Meshmakers.Octo.Backend.McpServices.Models;

/// <summary>
///     Association direction for Construction Kit associations
/// </summary>
public enum CkTypeAssociationDirectionDto
{
    /// <summary>
    ///     All inbound directions (e. g. parent to child)
    /// </summary>
    Inbound = 1,

    /// <summary>
    ///     All outbound directions (e. g. child to parent)
    /// </summary>
    Outbound = 2
}