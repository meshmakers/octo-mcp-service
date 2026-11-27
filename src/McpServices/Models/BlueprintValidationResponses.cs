using Meshmakers.Octo.Sdk.ServiceClient.AssetRepositoryServices.Blueprints;

namespace Meshmakers.Octo.Backend.McpServices.Models;

/// <summary>
///     Result of <c>validate_blueprint</c> (AB#9371): a blueprint draft validated without installing it.
/// </summary>
public class BlueprintValidationResponse : FileTransferResponse
{
    /// <summary>True when no finding is an error.</summary>
    public bool IsValid { get; set; }

    /// <summary>Id of the blueprint (<c>Name-Version</c>) when the manifest could be read.</summary>
    public string? BlueprintId { get; set; }

    /// <summary>Number of all findings; <see cref="Findings" /> is bounded.</summary>
    public int TotalFindings { get; set; }

    /// <summary>True when <see cref="Findings" /> was cut.</summary>
    public bool Truncated { get; set; }

    /// <summary>The findings with their stable codes (149-167), errors first.</summary>
    public List<BlueprintDraftFindingDto> Findings { get; set; } = [];
}
