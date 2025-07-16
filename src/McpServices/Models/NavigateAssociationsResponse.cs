using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;

namespace Meshmakers.Octo.Backend.McpServices.Models;

/// <summary>
/// Response for association navigation operations
/// </summary>
public sealed class NavigateAssociationsResponse
{
    /// <summary>
    /// Origin Construction Kit Type ID
    /// </summary>
    public required string OriginCkTypeId { get; init; }
    
    /// <summary>
    /// Origin Runtime entity ID
    /// </summary>
    public required string OriginRtId { get; init; }
    
    /// <summary>
    /// Construction Kit Role ID used for the navigation
    /// </summary>
    public required string CkRoleId { get; init; }
    
    /// <summary>
    /// Target type filter that was applied, if any
    /// </summary>
    public string? TargetTypeId { get; init; }
    
    /// <summary>
    /// Number of entities found before pagination
    /// </summary>
    public required long TotalCount { get; init; }
    
    /// <summary>
    /// Collection of related entities
    /// </summary>
    public required IList<RtEntityDto> Entities { get; init; }
}