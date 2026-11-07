using System.ComponentModel;
using Meshmakers.Octo.Backend.McpServices.Models;
using Meshmakers.Octo.Backend.McpServices.Services;
using ModelContextProtocol.Server;

// ReSharper disable UnusedMember.Global

namespace Meshmakers.Octo.Backend.McpServices.Tools;

/// <summary>
///     AB#8492: validate a CK model draft against the tenant without importing it. Backed by the asset repository's
///     <c>POST {tenantId}/v1/Models/ValidateCk</c> (SDK <c>IAssetServicesClient.ValidateCkModelAsync</c>).
/// </summary>
[McpServerToolType]
public sealed class CkModelValidationTools
{
    /// <summary>Validate an uploaded CK model draft against the tenant's installed models; nothing is imported.</summary>
    [McpServerTool(Name = "validate_ck_model")]
    [McpRisk(McpRiskLevel.Low)]
    [Description(
        "Validate a Construction Kit model draft against the target tenant WITHOUT importing it. Call " +
        "prepare_file_upload first, PUT the file to the returned URL, then call this tool with the transferId. " +
        "Accepted: a zip of the source ConstructionKit/ folder (ckModel.yaml + types/ attributes/ enums/ records/ " +
        "associations/ ...; compiled on the server, dependencies resolved from the server's catalogs), or a compiled " +
        "model (yaml, json or a zip of one). The compiled model is checked with the import's own checks against the " +
        "models installed in the tenant (CK language, engine version, system model references, dependencies) and, " +
        "without errors, with the tenant upgrade guard. Returns isValid, or errors with messageNumber, file (relative " +
        "to ConstructionKit/), elementPath and message; a missing dependency is an error, not a failed call. " +
        "isBlocked/blockers name installed models the import would break. Nothing is written; the upload is kept so " +
        "the same transferId can be fixed and validated again or imported.")]
    public static async Task<CkModelValidationResponse> ValidateCkModel(
        McpServer server,
        [Description("Transfer id from prepare_file_upload (file name ending .zip, .yaml or .json).")] string transferId,
        [Description("Tenant to operate on. Falls back to URL route.")] string? tenantId = null)
    {
        if (string.IsNullOrWhiteSpace(transferId))
        {
            return new CkModelValidationResponse { IsSuccess = false, ErrorMessage = "transferId is required." };
        }

        var store = server.Services!.GetRequiredService<IFileTransferStore>();
        var upload = store.GetUpload(transferId, McpSessionContext.GetCallerLabel(server));
        if (upload == null)
        {
            return new CkModelValidationResponse
            {
                IsSuccess = false,
                ErrorMessage = $"Upload '{transferId}' not found or expired."
            };
        }

        var asset = await AssetClientContext.TryBuildAsync(server, tenantId);
        if (asset.Error != null)
        {
            return new CkModelValidationResponse { IsSuccess = false, ErrorMessage = asset.Error };
        }

        try
        {
            var result = await asset.Client!.ValidateCkModelAsync(asset.TenantId!, upload.FilePath);
            return new CkModelValidationResponse
            {
                IsSuccess = true,
                TenantId = asset.TenantId,
                IsValid = result.IsValid,
                ModelId = result.ModelId,
                SourceKind = result.SourceKind,
                Errors = result.Errors,
                Warnings = result.Warnings,
                IsBlocked = result.IsBlocked,
                Blockers = result.Blockers,
                Message = result.IsValid
                    ? $"CK model draft '{result.ModelId}' is valid for tenant '{asset.TenantId}'" +
                      (result.IsBlocked ? $"; importing it would break {result.Blockers.Count} installed model(s)." : ".")
                    : $"CK model draft has {result.Errors.Count} error(s)."
            };
        }
        catch (Exception ex)
        {
            return new CkModelValidationResponse { IsSuccess = false, ErrorMessage = ex.Message };
        }
    }
}
