using System.ComponentModel;
using Meshmakers.Octo.Backend.McpServices.Models;
using Meshmakers.Octo.Backend.McpServices.Services;
using Meshmakers.Octo.Sdk.ServiceClient.AssetRepositoryServices.Blueprints;
using ModelContextProtocol.Server;

// ReSharper disable UnusedMember.Global

namespace Meshmakers.Octo.Backend.McpServices.Tools;

/// <summary>
///     AB#9371: validate a blueprint draft without installing it. Backed by the asset repository's
///     <c>POST {tenantId}/v1/blueprints/validate</c> (SDK <c>IAssetServicesClient.ValidateBlueprintAsync</c>), which runs
///     the shared engine validator of <c>octo-bpm validate</c> and the install path.
/// </summary>
[McpServerToolType]
public sealed class BlueprintValidationTools
{
    /// <summary>At most this many findings are returned to the agent (the server bounds its list as well).</summary>
    internal const int MaxFindings = 100;

    /// <summary>
    ///     Validate a blueprint draft; nothing is installed or written. The draft is passed inline
    ///     (<paramref name="files" />, AB#9005 shape) or as an upload (<paramref name="transferId" />).
    /// </summary>
    [McpServerTool(Name = "validate_blueprint")]
    [McpRisk(McpRiskLevel.Low)]
    [Description(
        "Validate a blueprint draft against the target tenant WITHOUT installing it - the same checks as the local " +
        "octo-bpm validate and the blueprint install: blueprint.yaml (id, seedDataPath/seedDataPaths), every seed " +
        "file (YAML, duplicate entities, unreferenced files under seed-data/), the Secret seed lint, and every " +
        "ckModelDependencies entry including its version range against the CK model catalogs the tenant reads. Pass " +
        "the draft in exactly one way: inline as files (name/content pairs, names relative to the blueprint root, " +
        "e.g. blueprint.yaml, seed-data/entities.yaml), up to 1 MiB (1048576 bytes of UTF-8 content) and 2000 files; " +
        "or, for larger drafts, call prepare_file_upload with a .zip of the blueprint folder (or blueprint.yaml), PUT " +
        "it and pass the transferId. Returns isValid, blueprintId and findings, each with a stable code (149-167: " +
        "149 manifest missing, 150 manifest unreadable, 151 blueprint id missing, 152/153 seed file missing, 155 seed " +
        "unparsable, 157 duplicate entity across files, 160 Secret value in seed, 163 CK dependency range in no " +
        "catalog with modelId, 164 range without minimum, 167 draft unreadable), level, file, modelId and message. " +
        "Findings are bounded (totalFindings counts all). A broken draft is a result, not a failed call.")]
    public static async Task<BlueprintValidationResponse> ValidateBlueprint(
        McpServer server,
        [Description("Inline draft: the files of the blueprint folder as name/content pairs (blueprint.yaml and the " +
                     "seed files). At most 1 MiB of content and 2000 files. Omit when passing a transferId.")]
        List<CkDraftFileDto>? files = null,
        [Description("Transfer id from prepare_file_upload (file name ending .zip, .yaml or .yml). Omit when the " +
                     "draft is passed inline.")]
        string? transferId = null,
        [Description("Tenant to operate on. Falls back to URL route.")] string? tenantId = null)
    {
        var hasUpload = !string.IsNullOrWhiteSpace(transferId);
        var hasFiles = files is { Count: > 0 };
        if (hasUpload == hasFiles)
        {
            return new BlueprintValidationResponse
            {
                IsSuccess = false,
                ErrorMessage = hasUpload
                    ? "Pass the draft in exactly one way: transferId or files."
                    : "Pass the draft inline as files (blueprint.yaml and the seed files) or as a transferId."
            };
        }

        // Authentication first, so an unauthenticated call never makes the server stage a draft.
        var asset = await AssetClientContext.TryBuildAsync(server, tenantId);
        if (asset.Error != null)
        {
            return new BlueprintValidationResponse { IsSuccess = false, ErrorMessage = asset.Error };
        }

        string? inlinePath = null;
        try
        {
            string filePath;
            if (hasUpload)
            {
                var store = server.Services!.GetRequiredService<IFileTransferStore>();
                var upload = store.GetUpload(transferId!, McpSessionContext.GetFileTransferOwner(server));
                if (upload == null)
                {
                    return new BlueprintValidationResponse
                    {
                        IsSuccess = false,
                        ErrorMessage = $"Upload '{transferId}' not found or expired."
                    };
                }

                filePath = upload.FilePath;
            }
            else
            {
                var staged = CkDraftInlinePackager.Stage(files!, "validate_blueprint");
                if (staged.FilePath == null)
                {
                    return new BlueprintValidationResponse
                    {
                        IsSuccess = false, ErrorCode = staged.ErrorCode, ErrorMessage = staged.ErrorMessage
                    };
                }

                filePath = inlinePath = staged.FilePath;
            }

            var result = await asset.Client!.ValidateBlueprintAsync(asset.TenantId!, filePath);
            return Map(result, asset.TenantId!);
        }
        catch (Exception ex)
        {
            return new BlueprintValidationResponse { IsSuccess = false, ErrorMessage = ex.Message };
        }
        finally
        {
            // An inline draft lives only for this call; an upload is kept for the fix loop.
            CkDraftInlinePackager.TryDelete(inlinePath);
        }
    }

    /// <summary>Maps the endpoint result; bounded to <see cref="MaxFindings" />.</summary>
    internal static BlueprintValidationResponse Map(BlueprintDraftValidationResponseDto result, string tenantId)
    {
        var errors = result.Findings.Count(f => f.Level is "Error" or "FatalError");
        return new BlueprintValidationResponse
        {
            IsSuccess = true,
            TenantId = tenantId,
            IsValid = result.IsValid,
            BlueprintId = result.BlueprintId,
            TotalFindings = result.TotalFindings,
            Truncated = result.Truncated || result.Findings.Count > MaxFindings,
            Findings = result.Findings.Take(MaxFindings).ToList(),
            Message = result.IsValid
                ? $"Blueprint draft '{result.BlueprintId}' is valid for tenant '{tenantId}'" +
                  (result.TotalFindings > 0 ? $" ({result.TotalFindings} warning/info finding(s))." : ".")
                : $"Blueprint draft has {errors} error(s)."
        };
    }
}
