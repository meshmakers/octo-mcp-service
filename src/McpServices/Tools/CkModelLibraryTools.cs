using System.ComponentModel;
using Meshmakers.Octo.Backend.McpServices.Models;
using Meshmakers.Octo.Backend.McpServices.Services;
using Meshmakers.Octo.Sdk.ServiceClient.AssetRepositoryServices.CkModelCatalog;
using ModelContextProtocol.Server;

// ReSharper disable UnusedMember.Global

namespace Meshmakers.Octo.Backend.McpServices.Tools;

/// <summary>
///     CK model library and catalog management tools. Mirrors octo-cli CkModelLibraries commands.
///     Import operations enqueue background jobs and return their IDs — poll separately for completion.
/// </summary>
[McpServerToolType]
public sealed class CkModelLibraryTools
{
    /// <summary>List the catalog sources configured for the asset service.</summary>
    [McpServerTool(Name = "list_ck_catalogs")]
    [Description("List CK model catalog sources configured for the asset service. Equivalent to octo-cli ListCatalogs.")]
    public static async Task<ListCkCatalogsResponse> ListCatalogs(
        McpServer server,
        [Description("Tenant to operate on. Falls back to URL route.")] string? tenantId = null)
    {
        var ctx = await AssetClientContext.TryBuildAsync(server, tenantId);
        if (ctx.Error != null)
        {
            return new ListCkCatalogsResponse { IsSuccess = false, ErrorMessage = ctx.Error };
        }

        try
        {
            var catalogs = await ctx.Client!.GetCkModelCatalogsAsync();
            return new ListCkCatalogsResponse
            {
                IsSuccess = true,
                TenantId = ctx.TenantId,
                Catalogs = catalogs,
                TotalCount = catalogs.Count,
                Message = catalogs.Count == 0 ? "No catalogs configured." : $"{catalogs.Count} catalog(s)."
            };
        }
        catch (Exception ex)
        {
            return new ListCkCatalogsResponse { IsSuccess = false, ErrorMessage = ex.Message };
        }
    }

    /// <summary>List models available in the catalog(s), with optional filter.</summary>
    [McpServerTool(Name = "list_ck_catalog_models")]
    [Description(
        "List models available in the catalog(s), optionally filtered by catalog name or search term. Paginated. " +
        "Equivalent to octo-cli ListCatalogModels.")]
    public static async Task<ListCkCatalogModelsResponse> ListCatalogModels(
        McpServer server,
        [Description("Optional catalog name filter.")] string? catalogName = null,
        [Description("Optional search term.")] string? searchTerm = null,
        [Description("Skip offset for pagination.")] int skip = 0,
        [Description("Page size (default 100).")] int take = 100,
        [Description("Tenant to operate on. Falls back to URL route.")] string? tenantId = null)
    {
        var ctx = await AssetClientContext.TryBuildAsync(server, tenantId);
        if (ctx.Error != null)
        {
            return new ListCkCatalogModelsResponse { IsSuccess = false, ErrorMessage = ctx.Error };
        }

        try
        {
            var result = await ctx.Client!.ListCkModelCatalogModelsAsync(catalogName, searchTerm, skip, take);
            return new ListCkCatalogModelsResponse
            {
                IsSuccess = true,
                TenantId = ctx.TenantId,
                Models = result,
                Message = $"Returned {result.Items.Count} of {result.TotalCount} model(s)."
            };
        }
        catch (Exception ex)
        {
            return new ListCkCatalogModelsResponse { IsSuccess = false, ErrorMessage = ex.Message };
        }
    }

    /// <summary>Refresh catalog caches (specific catalog or all).</summary>
    [McpServerTool(Name = "refresh_ck_catalogs")]
    [McpRisk(McpRiskLevel.Medium)]
    [Description("Refresh CK model catalog caches. Equivalent to octo-cli RefreshCatalogs.")]
    public static async Task<RefreshCkCatalogsResponse> RefreshCatalogs(
        McpServer server,
        [Description("Optional catalog name. If omitted, all catalogs are refreshed.")] string? catalogName = null,
        [Description("Tenant to operate on. Falls back to URL route.")] string? tenantId = null)
    {
        var ctx = await AssetClientContext.TryBuildAsync(server, tenantId);
        if (ctx.Error != null)
        {
            return new RefreshCkCatalogsResponse { IsSuccess = false, ErrorMessage = ctx.Error };
        }

        try
        {
            await ctx.Client!.RefreshCkModelCatalogsAsync(catalogName);
            return new RefreshCkCatalogsResponse
            {
                IsSuccess = true,
                TenantId = ctx.TenantId,
                CatalogName = catalogName,
                Message = catalogName == null
                    ? "All catalogs refreshed."
                    : $"Catalog '{catalogName}' refreshed."
            };
        }
        catch (Exception ex)
        {
            return new RefreshCkCatalogsResponse { IsSuccess = false, ErrorMessage = ex.Message };
        }
    }

    /// <summary>Get the merged installed-vs-catalog library status for the tenant.</summary>
    [McpServerTool(Name = "get_ck_library_status")]
    [Description(
        "Get the merged library status for the tenant: installed CK models with their catalog availability and " +
        "update flags. Equivalent to octo-cli LibraryStatus.")]
    public static async Task<CkLibraryStatusResponse> GetLibraryStatus(
        McpServer server,
        [Description("Tenant to operate on. Falls back to URL route.")] string? tenantId = null)
    {
        var ctx = await AssetClientContext.TryBuildAsync(server, tenantId);
        if (ctx.Error != null)
        {
            return new CkLibraryStatusResponse { IsSuccess = false, ErrorMessage = ctx.Error };
        }

        try
        {
            var status = await ctx.Client!.GetLibraryStatusAsync(ctx.TenantId!);
            return new CkLibraryStatusResponse
            {
                IsSuccess = true,
                TenantId = ctx.TenantId,
                Status = status,
                Message =
                    $"{status.Items.Count} model(s), {status.ModelsNeedingActionCount} need action."
            };
        }
        catch (Exception ex)
        {
            return new CkLibraryStatusResponse { IsSuccess = false, ErrorMessage = ex.Message };
        }
    }

    /// <summary>Resolve dependencies for one catalog model.</summary>
    [McpServerTool(Name = "check_ck_dependencies")]
    [Description(
        "Resolve dependencies for a catalog model. Returns the ordered import list and dependency trees. " +
        "Equivalent to octo-cli CheckDependencies.")]
    public static async Task<CkDependenciesResponse> CheckDependencies(
        McpServer server,
        [Description("Catalog name.")] string catalogName,
        [Description("Model ID (e.g. 'Industry.Energy-2.0.0').")] string modelId,
        [Description("Tenant to operate on. Falls back to URL route.")] string? tenantId = null)
    {
        if (string.IsNullOrWhiteSpace(catalogName) || string.IsNullOrWhiteSpace(modelId))
        {
            return new CkDependenciesResponse
            {
                IsSuccess = false,
                ErrorMessage = "catalogName and modelId are required."
            };
        }

        var ctx = await AssetClientContext.TryBuildAsync(server, tenantId);
        if (ctx.Error != null)
        {
            return new CkDependenciesResponse { IsSuccess = false, ErrorMessage = ctx.Error };
        }

        try
        {
            var result = await ctx.Client!.ResolveDependenciesBatchAsync(ctx.TenantId!,
                [new ImportFromCatalogRequestDto { CatalogName = catalogName, ModelId = modelId }]);

            return new CkDependenciesResponse
            {
                IsSuccess = true,
                TenantId = ctx.TenantId,
                Resolution = result,
                Message =
                    $"Dependency resolution for '{modelId}': {result.ModelsToImport.Count} model(s) would be imported."
            };
        }
        catch (Exception ex)
        {
            return new CkDependenciesResponse { IsSuccess = false, ErrorMessage = ex.Message };
        }
    }

    /// <summary>Pre-flight upgrade/migration check for a catalog model.</summary>
    [McpServerTool(Name = "check_ck_upgrade")]
    [Description(
        "Pre-flight check for upgrading/migrating an installed CK model from a catalog version. Reports whether " +
        "an upgrade is needed, whether a migration path exists, and whether breaking changes are involved. " +
        "Equivalent to octo-cli CheckUpgrade.")]
    public static async Task<CkUpgradeCheckResponse> CheckUpgrade(
        McpServer server,
        [Description("Catalog name.")] string catalogName,
        [Description("Model ID (target version).")] string modelId,
        [Description("Tenant to operate on. Falls back to URL route.")] string? tenantId = null)
    {
        if (string.IsNullOrWhiteSpace(catalogName) || string.IsNullOrWhiteSpace(modelId))
        {
            return new CkUpgradeCheckResponse
            {
                IsSuccess = false,
                ErrorMessage = "catalogName and modelId are required."
            };
        }

        var ctx = await AssetClientContext.TryBuildAsync(server, tenantId);
        if (ctx.Error != null)
        {
            return new CkUpgradeCheckResponse { IsSuccess = false, ErrorMessage = ctx.Error };
        }

        try
        {
            var check = await ctx.Client!.CheckUpgradeAsync(ctx.TenantId!,
                new ImportFromCatalogRequestDto { CatalogName = catalogName, ModelId = modelId });

            return new CkUpgradeCheckResponse
            {
                IsSuccess = true,
                TenantId = ctx.TenantId,
                Check = check,
                Message = check.UpgradeNeeded
                    ? $"Upgrade needed: {check.InstalledVersion ?? "(none)"} → {check.TargetVersion}" +
                      (check.HasBreakingChanges ? " (BREAKING CHANGES)" : string.Empty)
                    : "No upgrade required."
            };
        }
        catch (Exception ex)
        {
            return new CkUpgradeCheckResponse { IsSuccess = false, ErrorMessage = ex.Message };
        }
    }

    /// <summary>Import a CK model with its dependencies from a catalog. Returns job IDs (no waiting).</summary>
    [McpServerTool(Name = "import_ck_from_catalog")]
    [McpRisk(McpRiskLevel.Medium)]
    [Description(
        "Import a CK model from a catalog including all dependencies (in dependency order). Returns the job IDs " +
        "enqueued — poll asset jobs separately to track completion. Equivalent to octo-cli ImportFromCatalog " +
        "without the -w wait flag.")]
    public static async Task<CkImportResponse> ImportFromCatalog(
        McpServer server,
        [Description("Catalog name.")] string catalogName,
        [Description("Model ID (e.g. 'Industry.Energy-2.0.0').")] string modelId,
        [Description("Tenant to operate on. Falls back to URL route.")] string? tenantId = null)
    {
        if (string.IsNullOrWhiteSpace(catalogName) || string.IsNullOrWhiteSpace(modelId))
        {
            return new CkImportResponse { IsSuccess = false, ErrorMessage = "catalogName and modelId are required." };
        }

        var ctx = await AssetClientContext.TryBuildAsync(server, tenantId);
        if (ctx.Error != null)
        {
            return new CkImportResponse { IsSuccess = false, ErrorMessage = ctx.Error };
        }

        try
        {
            var depResult = await ctx.Client!.ResolveDependenciesBatchAsync(ctx.TenantId!,
                [new ImportFromCatalogRequestDto { CatalogName = catalogName, ModelId = modelId }]);

            if (depResult.ModelsToImport.Count == 0)
            {
                return new CkImportResponse
                {
                    IsSuccess = true,
                    TenantId = ctx.TenantId,
                    Message = $"Nothing to import — '{modelId}' (with deps) is already up to date."
                };
            }

            var importResult = await ctx.Client.ImportFromCatalogBatchAsync(ctx.TenantId!,
                new ImportFromCatalogBatchRequestDto
                {
                    CatalogName = catalogName,
                    ModelIds = depResult.ModelsToImport
                });

            return new CkImportResponse
            {
                IsSuccess = true,
                TenantId = ctx.TenantId,
                ModelsToImport = depResult.ModelsToImport,
                JobIds = importResult.JobIds,
                Message =
                    $"Enqueued {importResult.JobIds.Count} import job(s) for {depResult.ModelsToImport.Count} model(s)."
            };
        }
        catch (Exception ex)
        {
            return new CkImportResponse { IsSuccess = false, ErrorMessage = ex.Message };
        }
    }

    /// <summary>Import every CK model that needs update or fix. Destructive: requires confirm.</summary>
    [McpServerTool(Name = "fix_all_ck_models")]
    [McpRisk(McpRiskLevel.High)]
    [Description(
        "Find every installed CK model that needs an update or fix, resolve dependencies, and enqueue imports. " +
        "DESTRUCTIVE — modifies the installed model set. Requires confirm=true. Equivalent to octo-cli FixAll " +
        "without the -w wait flag.")]
    public static async Task<CkImportResponse> FixAllModels(
        McpServer server,
        [Description("Must be true to actually enqueue imports.")] bool confirm = false,
        [Description("Tenant to operate on. Falls back to URL route.")] string? tenantId = null)
    {
        var ctx = await AssetClientContext.TryBuildAsync(server, tenantId);
        if (ctx.Error != null)
        {
            return new CkImportResponse { IsSuccess = false, ErrorMessage = ctx.Error };
        }

        try
        {
            var status = await ctx.Client!.GetLibraryStatusAsync(ctx.TenantId!);
            var actionModels = status.Items
                .Where(i => i.NeedsAction && !i.IsServiceManaged && i.IsCompatible &&
                            i.CatalogName != null && i.FullModelId != null)
                .ToList();

            if (actionModels.Count == 0)
            {
                return new CkImportResponse
                {
                    IsSuccess = true,
                    TenantId = ctx.TenantId,
                    Message = "All models are up to date. Nothing to do."
                };
            }

            var requests = actionModels
                .Select(m => new ImportFromCatalogRequestDto
                {
                    CatalogName = m.CatalogName!,
                    ModelId = m.FullModelId!
                })
                .ToList();

            var depResult = await ctx.Client.ResolveDependenciesBatchAsync(ctx.TenantId!, requests);

            if (depResult.ModelsToImport.Count == 0)
            {
                return new CkImportResponse
                {
                    IsSuccess = true,
                    TenantId = ctx.TenantId,
                    Message = "No models to import after dependency resolution."
                };
            }

            if (!confirm)
            {
                return new CkImportResponse
                {
                    IsSuccess = false,
                    TenantId = ctx.TenantId,
                    ModelsToImport = depResult.ModelsToImport,
                    ErrorMessage =
                        $"Refusing to import {depResult.ModelsToImport.Count} model(s) without confirm=true. " +
                        $"Models that would be imported: {string.Join(", ", depResult.ModelsToImport)}"
                };
            }

            var catalogName = requests[0].CatalogName;
            var importResult = await ctx.Client.ImportFromCatalogBatchAsync(ctx.TenantId!,
                new ImportFromCatalogBatchRequestDto
                {
                    CatalogName = catalogName,
                    ModelIds = depResult.ModelsToImport
                });

            return new CkImportResponse
            {
                IsSuccess = true,
                TenantId = ctx.TenantId,
                ModelsToImport = depResult.ModelsToImport,
                JobIds = importResult.JobIds,
                Message =
                    $"Enqueued {importResult.JobIds.Count} fix-up import job(s) for " +
                    $"{depResult.ModelsToImport.Count} model(s) ({actionModels.Count} needed action)."
            };
        }
        catch (Exception ex)
        {
            return new CkImportResponse { IsSuccess = false, ErrorMessage = ex.Message };
        }
    }
}
