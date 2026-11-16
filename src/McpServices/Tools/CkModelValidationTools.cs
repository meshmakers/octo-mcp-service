using System.ComponentModel;
using Meshmakers.Octo.Backend.McpServices.Models;
using Meshmakers.Octo.Backend.McpServices.Services;
using Meshmakers.Octo.Sdk.ServiceClient.AssetRepositoryServices.CkModelCatalog;
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
    /// <summary>
    ///     Validate a CK model draft against the tenant's installed models; nothing is imported. The draft is an upload
    ///     (<paramref name="transferId" />) or, for MCP-only clients, inline (<paramref name="files" />, AB#9005).
    /// </summary>
    [McpServerTool(Name = "validate_ck_model")]
    [McpRisk(McpRiskLevel.Low)]
    [Description(
        "Validate a Construction Kit model draft against the target tenant WITHOUT importing it. Pass the draft in " +
        "exactly one way: inline as files (name/content pairs, names relative to ConstructionKit/, e.g. ckModel.yaml, " +
        "types/pump.yaml), up to 1 MiB (1048576 bytes of UTF-8 content) and 2000 files; or, for larger drafts, call " +
        "prepare_file_upload, PUT the file to the returned URL and pass the transferId. An inline draft over the " +
        "limit is refused with errorCode InlineDraftTooLarge (message 9005). " +
        "Accepted: the source ConstructionKit/ folder (ckModel.yaml + types/ attributes/ enums/ records/ " +
        "associations/ ...; compiled on the server, dependencies resolved from the server's catalogs), or a compiled " +
        "model (yaml, json or a zip of one). The compiled model is checked with the import's own checks against the " +
        "models installed in the tenant (CK language, engine version, system model references, dependencies) and, " +
        "without errors, with the tenant upgrade guard. Returns isValid, or errors with messageNumber, file (relative " +
        "to ConstructionKit/), elementPath and message; a missing dependency is an error, not a failed call. " +
        "isBlocked/blockers name installed models the import would break. Nothing is written; an upload is kept so " +
        "the same transferId can be fixed and validated again or imported.")]
    public static async Task<CkModelValidationResponse> ValidateCkModel(
        McpServer server,
        [Description("Transfer id from prepare_file_upload (file name ending .zip, .yaml or .json). Omit when the " +
                     "draft is passed inline.")]
        string? transferId = null,
        [Description("Inline draft: the files of the ConstructionKit/ folder as name/content pairs (or one compiled " +
                     "model file). At most 1 MiB of content and 2000 files.")]
        List<CkDraftFileDto>? files = null,
        [Description("Tenant to operate on. Falls back to URL route.")] string? tenantId = null)
    {
        var hasUpload = !string.IsNullOrWhiteSpace(transferId);
        var hasFiles = files is { Count: > 0 };
        if (hasUpload == hasFiles)
        {
            return new CkModelValidationResponse
            {
                IsSuccess = false,
                ErrorMessage = hasUpload
                    ? "Pass the draft in exactly one way: transferId or files."
                    : "transferId is required, or pass the draft inline as files."
            };
        }

        // Authentication first, so an unauthenticated call never makes the server stage a draft.
        var asset = await AssetClientContext.TryBuildAsync(server, tenantId);
        if (asset.Error != null)
        {
            return new CkModelValidationResponse { IsSuccess = false, ErrorMessage = asset.Error };
        }

        string? inlinePath = null;
        try
        {
            string filePath;
            if (hasUpload)
            {
                var store = server.Services!.GetRequiredService<IFileTransferStore>();
                var upload = store.GetUpload(transferId!, McpSessionContext.GetCallerLabel(server));
                if (upload == null)
                {
                    return new CkModelValidationResponse
                    {
                        IsSuccess = false,
                        ErrorMessage = $"Upload '{transferId}' not found or expired."
                    };
                }

                filePath = upload.FilePath;
            }
            else
            {
                CkDraftInlinePackager.Result packaged;
                try
                {
                    packaged = CkDraftInlinePackager.Package(files!, Path.GetTempPath());
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // The exception text names the server's temp path: not for the client.
                    return new CkModelValidationResponse
                    {
                        IsSuccess = false,
                        ErrorMessage = "The inline draft could not be staged on the MCP server (temporary storage)."
                    };
                }

                if (packaged.TooLarge != null)
                {
                    return new CkModelValidationResponse
                    {
                        IsSuccess = false,
                        ErrorCode = CkDraftInlinePackager.InlineDraftTooLargeCode,
                        ErrorMessage = packaged.TooLarge,
                        Errors =
                        [
                            new CkModelValidationMessageDto
                            {
                                MessageNumber = CkDraftInlinePackager.InlineDraftTooLargeNumber,
                                Level = "Error",
                                Message = packaged.TooLarge
                            }
                        ]
                    };
                }

                if (packaged.Error != null)
                {
                    return new CkModelValidationResponse { IsSuccess = false, ErrorMessage = packaged.Error };
                }

                filePath = inlinePath = packaged.FilePath!;
            }

            var result = await asset.Client!.ValidateCkModelAsync(asset.TenantId!, filePath);
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
        finally
        {
            // An inline draft lives only for this call (AB#9005/AB#9015); an upload is kept for the fix loop.
            CkDraftInlinePackager.TryDelete(inlinePath);
        }
    }
}
