using System.ComponentModel;
using Meshmakers.Octo.Backend.McpServices.Models;
using Meshmakers.Octo.Backend.McpServices.Services;
using Meshmakers.Octo.Sdk.ServiceClient.AssetRepositoryServices.Blueprints;
using ModelContextProtocol.Server;

// ReSharper disable UnusedMember.Global

namespace Meshmakers.Octo.Backend.McpServices.Tools;

/// <summary>
///     Tenant blueprint management tools. Mirrors octo-cli Blueprint commands (Phase 1 install + Phase 2a/3
///     update/uninstall lifecycle).
/// </summary>
[McpServerToolType]
public sealed class BlueprintTools
{
    /// <summary>List blueprints available across configured catalogs.</summary>
    [McpServerTool(Name = "list_blueprints")]
    [Description(
        "List blueprints available across configured catalogs (paginated). Equivalent to octo-cli ListBlueprints.")]
    public static async Task<ListBlueprintsResponse> ListBlueprints(
        McpServer server,
        [Description("Skip offset for pagination.")] int skip = 0,
        [Description("Page size (default 100).")] int take = 100,
        [Description("Tenant to operate on. Falls back to URL route.")] string? tenantId = null)
    {
        var ctx = await AssetClientContext.TryBuildAsync(server, tenantId);
        if (ctx.Error != null)
        {
            return new ListBlueprintsResponse { IsSuccess = false, ErrorMessage = ctx.Error };
        }

        try
        {
            var result = await ctx.Client!.ListBlueprintsAsync(skip, take);
            return new ListBlueprintsResponse
            {
                IsSuccess = true,
                TenantId = ctx.TenantId,
                Catalog = result,
                Message = $"Returned {result.Items.Count} of {result.TotalCount} blueprint(s)."
            };
        }
        catch (Exception ex)
        {
            return new ListBlueprintsResponse { IsSuccess = false, ErrorMessage = ex.Message };
        }
    }

    /// <summary>Apply a blueprint to the tenant.</summary>
    [McpServerTool(Name = "install_blueprint")]
    [McpRisk(McpRiskLevel.High)]
    [Description(
        "Apply a blueprint to the tenant for the first time. With force=true, re-applies seed data via upsert " +
        "(recovery path). Equivalent to octo-cli InstallBlueprint.")]
    public static async Task<InstallBlueprintResponse> InstallBlueprint(
        McpServer server,
        [Description("Blueprint ID (e.g. 'MyBlueprint-1.0.0').")] string blueprintId,
        [Description("Re-apply seed data via upsert. Effectively a recovery action.")] bool force = false,
        [Description("Tenant to operate on. Falls back to URL route.")] string? tenantId = null)
    {
        if (string.IsNullOrWhiteSpace(blueprintId))
        {
            return new InstallBlueprintResponse { IsSuccess = false, ErrorMessage = "blueprintId is required." };
        }

        var ctx = await AssetClientContext.TryBuildAsync(server, tenantId);
        if (ctx.Error != null)
        {
            return new InstallBlueprintResponse { IsSuccess = false, ErrorMessage = ctx.Error };
        }

        try
        {
            var result = await ctx.Client!.ApplyBlueprintAsync(ctx.TenantId!, blueprintId, force);
            return new InstallBlueprintResponse
            {
                IsSuccess = true,
                TenantId = ctx.TenantId,
                Result = result,
                Message = result.Success
                    ? $"Blueprint '{blueprintId}' applied (mode '{result.ApplicationMode}', " +
                      $"{result.SeedDataFilesApplied} seed file(s), {result.LoadedCkModels.Count} CK model(s))."
                    : $"Blueprint '{blueprintId}' application reported Success=false."
            };
        }
        catch (Exception ex)
        {
            return new InstallBlueprintResponse { IsSuccess = false, ErrorMessage = ex.Message };
        }
    }

    /// <summary>Return the application history of blueprints on this tenant.</summary>
    [McpServerTool(Name = "get_blueprint_history")]
    [Description(
        "Return the blueprint application history for the tenant in chronological order. Equivalent to octo-cli " +
        "GetBlueprintHistory.")]
    public static async Task<BlueprintHistoryResponse> GetBlueprintHistory(
        McpServer server,
        [Description("Tenant to operate on. Falls back to URL route.")] string? tenantId = null)
    {
        var ctx = await AssetClientContext.TryBuildAsync(server, tenantId);
        if (ctx.Error != null)
        {
            return new BlueprintHistoryResponse { IsSuccess = false, ErrorMessage = ctx.Error };
        }

        try
        {
            var history = (await ctx.Client!.GetBlueprintHistoryAsync(ctx.TenantId!)).ToList();
            return new BlueprintHistoryResponse
            {
                IsSuccess = true,
                TenantId = ctx.TenantId,
                History = history,
                TotalCount = history.Count,
                Message = history.Count == 0
                    ? "No blueprint history yet."
                    : $"{history.Count} history item(s)."
            };
        }
        catch (Exception ex)
        {
            return new BlueprintHistoryResponse { IsSuccess = false, ErrorMessage = ex.Message };
        }
    }

    /// <summary>Get available update info for the tenant's current blueprint.</summary>
    [McpServerTool(Name = "get_blueprint_update_info")]
    [Description(
        "Return update info for the tenant's current blueprint: available versions, recommended target, and " +
        "whether an update exists. No direct CLI equivalent (used by PreviewBlueprintUpdate/UpdateBlueprint).")]
    public static async Task<BlueprintUpdateInfoResponse> GetBlueprintUpdateInfo(
        McpServer server,
        [Description("Tenant to operate on. Falls back to URL route.")] string? tenantId = null)
    {
        var ctx = await AssetClientContext.TryBuildAsync(server, tenantId);
        if (ctx.Error != null)
        {
            return new BlueprintUpdateInfoResponse { IsSuccess = false, ErrorMessage = ctx.Error };
        }

        try
        {
            var info = await ctx.Client!.GetBlueprintUpdateInfoAsync(ctx.TenantId!);
            return new BlueprintUpdateInfoResponse
            {
                IsSuccess = true,
                TenantId = ctx.TenantId,
                UpdateInfo = info,
                Message = info.HasUpdate
                    ? $"Update available: {info.CurrentVersion} → {info.RecommendedVersion}."
                    : "No updates available."
            };
        }
        catch (Exception ex)
        {
            return new BlueprintUpdateInfoResponse { IsSuccess = false, ErrorMessage = ex.Message };
        }
    }

    /// <summary>Preview the changes a blueprint update would apply without applying them.</summary>
    [McpServerTool(Name = "preview_blueprint_update")]
    [Description(
        "Preview the changes a blueprint update would make without applying them. Equivalent to octo-cli " +
        "PreviewBlueprintUpdate.")]
    public static async Task<BlueprintUpdatePreviewResponse> PreviewBlueprintUpdate(
        McpServer server,
        [Description("Target blueprint version (e.g. 'MyBlueprint-2.0.0').")] string targetVersion,
        [Description("Update mode: 'Merge' (default), 'Safe' or 'Full'.")] string updateMode = "Merge",
        [Description("Tenant to operate on. Falls back to URL route.")] string? tenantId = null)
    {
        if (string.IsNullOrWhiteSpace(targetVersion))
        {
            return new BlueprintUpdatePreviewResponse { IsSuccess = false, ErrorMessage = "targetVersion is required." };
        }

        var ctx = await AssetClientContext.TryBuildAsync(server, tenantId);
        if (ctx.Error != null)
        {
            return new BlueprintUpdatePreviewResponse { IsSuccess = false, ErrorMessage = ctx.Error };
        }

        try
        {
            var preview = await ctx.Client!.PreviewBlueprintUpdateAsync(ctx.TenantId!, new BlueprintUpdateRequestDto
            {
                TargetVersion = targetVersion,
                UpdateMode = updateMode,
                DryRun = true
            });

            return new BlueprintUpdatePreviewResponse
            {
                IsSuccess = true,
                TenantId = ctx.TenantId,
                Preview = preview,
                Message =
                    $"Preview to {targetVersion} ({updateMode}): " +
                    $"+{preview.EntitiesToAdd} / ~{preview.EntitiesToUpdate} / -{preview.EntitiesToDelete}, " +
                    $"{preview.Conflicts.Count} conflict(s), {preview.Warnings.Count} warning(s)."
            };
        }
        catch (Exception ex)
        {
            return new BlueprintUpdatePreviewResponse { IsSuccess = false, ErrorMessage = ex.Message };
        }
    }

    /// <summary>Apply a blueprint update. Destructive unless dryRun=true; otherwise requires confirm=true.</summary>
    [McpServerTool(Name = "update_blueprint")]
    [McpRisk(McpRiskLevel.Medium)]
    [Description(
        "Apply a blueprint update. DESTRUCTIVE — requires confirm=true unless dryRun is also true. Equivalent to " +
        "octo-cli UpdateBlueprint.")]
    public static async Task<UpdateBlueprintResponse> UpdateBlueprint(
        McpServer server,
        [Description("Target blueprint version.")] string targetVersion,
        [Description("Update mode: 'Merge' (default), 'Safe' or 'Full'.")] string updateMode = "Merge",
        [Description("Dry run: no persistent changes. Useful for validation.")] bool dryRun = false,
        [Description("Required to actually apply changes (no-op for dryRun=true).")] bool confirm = false,
        [Description("Tenant to operate on. Falls back to URL route.")] string? tenantId = null)
    {
        if (string.IsNullOrWhiteSpace(targetVersion))
        {
            return new UpdateBlueprintResponse { IsSuccess = false, ErrorMessage = "targetVersion is required." };
        }

        if (!dryRun && !confirm)
        {
            return new UpdateBlueprintResponse
            {
                IsSuccess = false,
                ErrorMessage = $"Refusing to apply blueprint update to '{targetVersion}' without confirm=true."
            };
        }

        var ctx = await AssetClientContext.TryBuildAsync(server, tenantId);
        if (ctx.Error != null)
        {
            return new UpdateBlueprintResponse { IsSuccess = false, ErrorMessage = ctx.Error };
        }

        try
        {
            await ctx.Client!.ApplyBlueprintUpdateAsync(ctx.TenantId!, new BlueprintUpdateRequestDto
            {
                TargetVersion = targetVersion,
                UpdateMode = updateMode,
                DryRun = dryRun
            });

            return new UpdateBlueprintResponse
            {
                IsSuccess = true,
                TenantId = ctx.TenantId,
                TargetVersion = targetVersion,
                DryRun = dryRun,
                Message = dryRun
                    ? $"Dry run completed for target '{targetVersion}' (mode '{updateMode}')."
                    : $"Blueprint updated to '{targetVersion}' (mode '{updateMode}')."
            };
        }
        catch (Exception ex)
        {
            return new UpdateBlueprintResponse { IsSuccess = false, ErrorMessage = ex.Message };
        }
    }

    /// <summary>List blueprints currently installed on the tenant.</summary>
    [McpServerTool(Name = "list_blueprint_installations")]
    [Description(
        "List blueprints currently installed on the tenant (distinct from the catalog listing). Equivalent to " +
        "octo-cli ListBlueprintInstallations.")]
    public static async Task<ListBlueprintInstallationsResponse> ListBlueprintInstallations(
        McpServer server,
        [Description("Tenant to operate on. Falls back to URL route.")] string? tenantId = null)
    {
        var ctx = await AssetClientContext.TryBuildAsync(server, tenantId);
        if (ctx.Error != null)
        {
            return new ListBlueprintInstallationsResponse { IsSuccess = false, ErrorMessage = ctx.Error };
        }

        try
        {
            var installs = (await ctx.Client!.ListBlueprintInstallationsAsync(ctx.TenantId!)).ToList();
            return new ListBlueprintInstallationsResponse
            {
                IsSuccess = true,
                TenantId = ctx.TenantId,
                Installations = installs,
                TotalCount = installs.Count,
                Message = installs.Count == 0 ? "No blueprints installed." : $"{installs.Count} installation(s)."
            };
        }
        catch (Exception ex)
        {
            return new ListBlueprintInstallationsResponse { IsSuccess = false, ErrorMessage = ex.Message };
        }
    }

    /// <summary>Uninstall a blueprint from the tenant. Destructive: requires confirm.</summary>
    [McpServerTool(Name = "uninstall_blueprint")]
    [McpRisk(McpRiskLevel.High)]
    [Description(
        "Remove a blueprint from the tenant. DESTRUCTIVE — locked owned entities are erased. With cascade=true, " +
        "dependents are uninstalled first and orphaned dependencies are auto-cleaned. Requires confirm=true. " +
        "Equivalent to octo-cli UninstallBlueprint.")]
    public static async Task<UninstallBlueprintResponse> UninstallBlueprint(
        McpServer server,
        [Description("Blueprint name to uninstall.")] string blueprintName,
        [Description("Cascade: also remove dependents and orphan deps.")] bool cascade = false,
        [Description("Must be true to actually uninstall.")] bool confirm = false,
        [Description("Tenant to operate on. Falls back to URL route.")] string? tenantId = null)
    {
        if (string.IsNullOrWhiteSpace(blueprintName))
        {
            return new UninstallBlueprintResponse { IsSuccess = false, ErrorMessage = "blueprintName is required." };
        }

        if (!confirm)
        {
            return new UninstallBlueprintResponse
            {
                IsSuccess = false,
                ErrorMessage = $"Refusing to uninstall blueprint '{blueprintName}' without confirm=true."
            };
        }

        var ctx = await AssetClientContext.TryBuildAsync(server, tenantId);
        if (ctx.Error != null)
        {
            return new UninstallBlueprintResponse { IsSuccess = false, ErrorMessage = ctx.Error };
        }

        try
        {
            var result = await ctx.Client!.UninstallBlueprintAsync(ctx.TenantId!, blueprintName, cascade);
            return new UninstallBlueprintResponse
            {
                IsSuccess = true,
                TenantId = ctx.TenantId,
                Result = result,
                Message = result.Success
                    ? $"Blueprint '{blueprintName}' uninstalled ({result.EntitiesDeleted} entities deleted, " +
                      $"{result.CascadedDependencies.Count} cascaded)."
                    : result.BlockingDependents.Count > 0
                        ? $"Uninstall blocked by dependents: {string.Join(", ", result.BlockingDependents)}. " +
                          "Use cascade=true to remove them first."
                        : $"Uninstall of '{blueprintName}' reported Success=false."
            };
        }
        catch (Exception ex)
        {
            return new UninstallBlueprintResponse { IsSuccess = false, ErrorMessage = ex.Message };
        }
    }
}
