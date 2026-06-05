using Meshmakers.Octo.Sdk.ServiceClient.AssetRepositoryServices.Blueprints;
using Meshmakers.Octo.Sdk.ServiceClient.AssetRepositoryServices.CkModelCatalog;

namespace Meshmakers.Octo.Backend.McpServices.Models;

/// <summary>Common envelope for asset-management tool responses.</summary>
public class AssetResponse
{
    /// <summary>True when the underlying service call succeeded.</summary>
    public bool IsSuccess { get; set; }

    /// <summary>Error message when <see cref="IsSuccess"/> is false.</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>Optional human-readable status message.</summary>
    public string? Message { get; set; }

    /// <summary>Tenant the operation was executed against.</summary>
    public string? TenantId { get; set; }
}

/// <summary>Response of list_blueprints.</summary>
public class ListBlueprintsResponse : AssetResponse
{
    /// <summary>Catalog list page returned by the asset service.</summary>
    public BlueprintCatalogListResponseDto? Catalog { get; set; }
}

/// <summary>Response of install_blueprint.</summary>
public class InstallBlueprintResponse : AssetResponse
{
    /// <summary>Apply result returned by the asset service.</summary>
    public BlueprintApplyResultDto? Result { get; set; }
}

/// <summary>Response of get_blueprint_history.</summary>
public class BlueprintHistoryResponse : AssetResponse
{
    /// <summary>Chronological history items.</summary>
    public List<BlueprintHistoryItemDto> History { get; set; } = [];

    /// <summary>Total number of history items.</summary>
    public int TotalCount { get; set; }
}

/// <summary>Response of get_blueprint_update_info.</summary>
public class BlueprintUpdateInfoResponse : AssetResponse
{
    /// <summary>Update info payload.</summary>
    public BlueprintUpdateInfoDto? UpdateInfo { get; set; }
}

/// <summary>Response of preview_blueprint_update.</summary>
public class BlueprintUpdatePreviewResponse : AssetResponse
{
    /// <summary>Preview payload.</summary>
    public BlueprintUpdatePreviewDto? Preview { get; set; }
}

/// <summary>Response of update_blueprint.</summary>
public class UpdateBlueprintResponse : AssetResponse
{
    /// <summary>Target version that was applied.</summary>
    public string? TargetVersion { get; set; }

    /// <summary>Whether the call was a dry run.</summary>
    public bool DryRun { get; set; }
}

/// <summary>Response of list_blueprint_backups.</summary>
public class ListBlueprintBackupsResponse : AssetResponse
{
    /// <summary>Backups available for the tenant.</summary>
    public List<BlueprintBackupDto> Backups { get; set; } = [];

    /// <summary>Total number of backups.</summary>
    public int TotalCount { get; set; }
}

/// <summary>Response of rollback_blueprint.</summary>
public class RollbackBlueprintResponse : AssetResponse
{
    /// <summary>Restore result returned by the asset service.</summary>
    public BlueprintRestoreResultDto? Result { get; set; }
}

/// <summary>Response of list_blueprint_installations.</summary>
public class ListBlueprintInstallationsResponse : AssetResponse
{
    /// <summary>Blueprints currently installed on the tenant.</summary>
    public List<BlueprintInstallationDto> Installations { get; set; } = [];

    /// <summary>Total number of installations.</summary>
    public int TotalCount { get; set; }
}

/// <summary>Response of uninstall_blueprint.</summary>
public class UninstallBlueprintResponse : AssetResponse
{
    /// <summary>Uninstall result.</summary>
    public BlueprintUninstallResultDto? Result { get; set; }
}

/// <summary>Response of list_ck_catalogs.</summary>
public class ListCkCatalogsResponse : AssetResponse
{
    /// <summary>Catalog sources configured for the asset service.</summary>
    public List<CkModelCatalogDto> Catalogs { get; set; } = [];

    /// <summary>Total number of catalogs.</summary>
    public int TotalCount { get; set; }
}

/// <summary>Response of list_ck_catalog_models.</summary>
public class ListCkCatalogModelsResponse : AssetResponse
{
    /// <summary>Catalog model list page returned by the asset service.</summary>
    public CkModelCatalogListResponseDto? Models { get; set; }
}

/// <summary>Response of get_ck_library_status.</summary>
public class CkLibraryStatusResponse : AssetResponse
{
    /// <summary>Merged installed-vs-catalog status payload.</summary>
    public CkModelLibraryStatusResponseDto? Status { get; set; }
}

/// <summary>Response of check_ck_dependencies.</summary>
public class CkDependenciesResponse : AssetResponse
{
    /// <summary>Resolved dependency tree.</summary>
    public BatchDependencyResolutionResponseDto? Resolution { get; set; }
}

/// <summary>Response of check_ck_upgrade.</summary>
public class CkUpgradeCheckResponse : AssetResponse
{
    /// <summary>Pre-flight check payload.</summary>
    public UpgradeCheckResponseDto? Check { get; set; }
}

/// <summary>Response of import_ck_from_catalog and fix_all_ck_models.</summary>
public class CkImportResponse : AssetResponse
{
    /// <summary>Models that will be imported (resolved including dependencies).</summary>
    public List<string> ModelsToImport { get; set; } = [];

    /// <summary>Asset-service job IDs for the import operations. Poll separately to track completion.</summary>
    public List<string> JobIds { get; set; } = [];
}

/// <summary>Response of refresh_ck_catalogs.</summary>
public class RefreshCkCatalogsResponse : AssetResponse
{
    /// <summary>Name of the catalog that was refreshed, or null for "all".</summary>
    public string? CatalogName { get; set; }
}
