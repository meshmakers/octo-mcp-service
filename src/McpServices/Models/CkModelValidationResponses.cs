using Meshmakers.Octo.Sdk.ServiceClient.AssetRepositoryServices.CkModelCatalog;

namespace Meshmakers.Octo.Backend.McpServices.Models;

/// <summary>
///     Response of <c>validate_ck_model</c> (AB#8492). <see cref="FileTransferResponse.IsSuccess" /> says whether the
///     validation ran; <see cref="IsValid" /> whether the draft is valid.
/// </summary>
public class CkModelValidationResponse : FileTransferResponse
{
    /// <summary>True when the draft has no error (set only when the validation ran).</summary>
    public bool IsValid { get; set; }

    /// <summary>Id of the model (<c>Name-Version</c>) when the draft could be read that far.</summary>
    public string? ModelId { get; set; }

    /// <summary><c>Source</c> (zipped <c>ConstructionKit/</c> folder, compiled on the server) or <c>Compiled</c>.</summary>
    public string? SourceKind { get; set; }

    /// <summary>The errors with message number, file of the draft and CK element path; empty when valid.</summary>
    public List<CkModelValidationMessageDto> Errors { get; set; } = [];

    /// <summary>Compiler warnings; they do not make the draft invalid.</summary>
    public List<CkModelValidationMessageDto> Warnings { get; set; } = [];

    /// <summary>True when importing the draft would break an installed model (the import then refuses unless forced).</summary>
    public bool IsBlocked { get; set; }

    /// <summary>The installed models the import would break.</summary>
    public List<CkUpgradeBlockerDto> Blockers { get; set; } = [];
}
