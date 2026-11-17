using System.ComponentModel;
using System.Net;
using Meshmakers.Octo.Backend.McpServices.Models;
using Meshmakers.Octo.Backend.McpServices.Services;
using Meshmakers.Octo.Sdk.ServiceClient;
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
    /// <summary>Error code of refresh_blueprint_catalogs for an unknown catalog name (HTTP 404 of the service).</summary>
    public const string CatalogNotFoundCode = "CatalogNotFound";

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
        "(recovery path). The result lists the archives of the blueprint seed (activated, skipped with a reason, " +
        "failed, backfill job of new rollups) and the warnings. Equivalent to octo-cli InstallBlueprint. " +
        "Requires the tenant role DataModelManagement (AB#8529); without it the call fails with ErrorCode " +
        "DataModelManagementRequired.")]
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
                      $"{result.SeedDataFilesApplied} seed file(s), {result.LoadedCkModels.Count} CK model(s))." +
                      DescribeOutcome(result.ArchiveActivations, result.Warnings)
                    : $"Blueprint '{blueprintId}' application reported Success=false."
            };
        }
        catch (Exception ex)
        {
            var problem = ServiceProblemMessage.From(ex);
            return new InstallBlueprintResponse { IsSuccess = false, ErrorMessage = problem.Message, ErrorCode = problem.Code };
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
        "Apply a blueprint update. DESTRUCTIVE — requires confirm=true unless dryRun is also true. The result lists " +
        "the archives the update activated or skipped (with backfill job ids of new rollups) and the warnings. " +
        "Equivalent to octo-cli UpdateBlueprint. " +
        "Requires the tenant role DataModelManagement (AB#8529); without it the call fails with ErrorCode " +
        "DataModelManagementRequired.")]
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
            var result = await ctx.Client!.ApplyBlueprintUpdateAsync(ctx.TenantId!, new BlueprintUpdateRequestDto
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
                Result = result,
                Message = dryRun
                    ? $"Dry run completed for target '{targetVersion}' (mode '{updateMode}')."
                    : $"Blueprint updated to '{targetVersion}' (mode '{updateMode}')." +
                      DescribeOutcome(result?.ArchiveActivations, result?.Warnings)
            };
        }
        catch (Exception ex)
        {
            var problem = ServiceProblemMessage.From(ex);
            return new UpdateBlueprintResponse { IsSuccess = false, ErrorMessage = problem.Message, ErrorCode = problem.Code };
        }
    }

    /// <summary>
    ///     AB#7991: one-sentence summary of what an install or update did with the archives of the seed and how many
    ///     warnings it carries; empty when there is nothing to report. The full per-archive list is in <c>Result</c>.
    /// </summary>
    internal static string DescribeOutcome(
        IReadOnlyCollection<BlueprintArchiveActivationDto>? activations, IReadOnlyCollection<string>? warnings)
    {
        var parts = new List<string>();
        if (activations is { Count: > 0 })
        {
            var byOutcome = activations
                .GroupBy(a => a.Outcome)
                .Select(g => $"{g.Count()} {g.Key}")
                .ToList();
            var queued = activations
                .Where(a => a.Backfill == "Queued")
                .Select(a => $"{a.WellKnownName ?? a.ArchiveRtId}: job {a.BackfillJobRtId}")
                .ToList();
            var text = $"Archives: {string.Join(", ", byOutcome)}";
            if (queued.Count > 0)
            {
                text += $"; {queued.Count} rollup backfill(s) queued ({string.Join(", ", queued)})";
            }

            parts.Add(text + ".");
        }

        if (warnings is { Count: > 0 })
        {
            parts.Add($"{warnings.Count} warning(s), see Result.Warnings.");
        }

        return parts.Count == 0 ? string.Empty : " " + string.Join(" ", parts);
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

    /// <summary>Refresh the blueprint catalog caches of the asset repository (one catalog or all), AB#9182.</summary>
    [McpServerTool(Name = "refresh_blueprint_catalogs")]
    [McpRisk(McpRiskLevel.Medium)]
    [Description(
        "Refresh the blueprint catalog caches of the asset repository (always forced, no service restart), so newly " +
        "published blueprint versions become visible. Equivalent to octo-cli RefreshBlueprintCatalogs. Returns the " +
        "result per catalog (Refreshed, Skipped, Failed); one failed catalog does not stop the others but sets " +
        "IsSuccess=false. Unknown catalogName: ErrorCode CatalogNotFound. Limits: with several asset-repository " +
        "replicas only the answering one is refreshed, so repeat if the version stays missing (bug AB#9169); a " +
        "GitHub Pages catalog can serve its old index for up to 10 minutes after a deploy (CDN cache). The planned " +
        "per-tenant catalog (AB#9176) will need no refresh. Requires the tenant role DataModelManagement; without " +
        "it ErrorCode DataModelManagementRequired.")]
    public static async Task<RefreshBlueprintCatalogsResponse> RefreshBlueprintCatalogs(
        McpServer server,
        [Description("Optional blueprint catalog name (case-insensitive). If omitted, all catalogs are refreshed.")]
        string? catalogName = null,
        [Description("Tenant to operate on. Falls back to URL route.")] string? tenantId = null)
    {
        // Same convention as the SDK and octo-cli: a blank name means "all catalogs".
        if (string.IsNullOrWhiteSpace(catalogName))
        {
            catalogName = null;
        }
        else if (!IsCatalogNameSegment(catalogName))
        {
            // The SDK puts the name into the request path unescaped; a name with '/', '?', '%' or a dot segment
            // could address another route (e.g. the all-catalogs refresh). Catalog names are type-like names.
            return new RefreshBlueprintCatalogsResponse
            {
                IsSuccess = false,
                CatalogName = catalogName,
                ErrorMessage = "catalogName may contain only ASCII letters, digits, '.', '-' and '_' (e.g. " +
                               "'PrivateGitHubBlueprintCatalog'). Omit it to refresh all catalogs."
            };
        }

        var ctx = await AssetClientContext.TryBuildAsync(server, tenantId);
        if (ctx.Error != null)
        {
            return new RefreshBlueprintCatalogsResponse { IsSuccess = false, ErrorMessage = ctx.Error };
        }

        try
        {
            var result = await ctx.Client!.RefreshBlueprintCatalogsAsync(catalogName);
            var results = result.Results;
            var refreshed = results.Count(r => r.Status == "Refreshed");
            var skipped = results.Count(r => r.Status == "Skipped");
            var failed = results.Count(r => r.Status == "Failed");
            var summary = $"{refreshed} refreshed, {skipped} skipped, {failed} failed.";

            return new RefreshBlueprintCatalogsResponse
            {
                IsSuccess = failed == 0,
                TenantId = ctx.TenantId,
                CatalogName = catalogName,
                Results = results,
                RefreshedCount = refreshed,
                SkippedCount = skipped,
                FailedCount = failed,
                ErrorMessage = failed == 0
                    ? null
                    : $"{failed} of {results.Count} blueprint catalog(s) failed to refresh: " +
                      string.Join("; ", results.Where(r => r.Status == "Failed")
                          .Select(r => $"'{r.CatalogName}': {r.Message}")),
                Message = catalogName == null
                    ? $"Blueprint catalog refresh finished: {summary}"
                    : $"Blueprint catalog '{catalogName}' refresh finished: {summary}"
            };
        }
        catch (ServiceClientResultException ex) when (ex.HttpStatusCode == HttpStatusCode.NotFound && catalogName != null)
        {
            return new RefreshBlueprintCatalogsResponse
            {
                IsSuccess = false,
                CatalogName = catalogName,
                ErrorCode = CatalogNotFoundCode,
                ErrorMessage = $"Blueprint catalog '{catalogName}' not found. Omit catalogName to refresh all " +
                               "catalogs; the result lists their names."
            };
        }
        catch (Exception ex)
        {
            var problem = ServiceProblemMessage.From(ex);
            return new RefreshBlueprintCatalogsResponse
            {
                IsSuccess = false, CatalogName = catalogName, ErrorMessage = problem.Message, ErrorCode = problem.Code
            };
        }
    }

    private static bool IsCatalogNameSegment(string name) =>
        name is not "." and not ".." &&
        name.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_');

    /// <summary>Uninstall a blueprint from the tenant. Destructive: requires confirm.</summary>
    [McpServerTool(Name = "uninstall_blueprint")]
    [McpRisk(McpRiskLevel.High)]
    [Description(
        "Remove a blueprint from the tenant. DESTRUCTIVE — locked owned entities are erased. With cascade=true, " +
        "dependents are uninstalled first and orphaned dependencies are auto-cleaned. Requires confirm=true. " +
        "Equivalent to octo-cli UninstallBlueprint. " +
        "Requires the tenant role DataModelManagement (AB#8529); without it the call fails with ErrorCode " +
        "DataModelManagementRequired.")]
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
            var problem = ServiceProblemMessage.From(ex);
            return new UninstallBlueprintResponse { IsSuccess = false, ErrorMessage = problem.Message, ErrorCode = problem.Code };
        }
    }
}
