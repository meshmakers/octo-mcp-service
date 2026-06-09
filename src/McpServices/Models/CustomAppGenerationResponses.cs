namespace Meshmakers.Octo.Backend.McpServices.Models;

/// <summary>
///     Response of <c>get_custom_app_template_manifest</c>. Lists the canonical files that
///     come from the OctoMesh Custom-App template-repo scaffold (#4135). The agent reads this
///     to know which files exist as starting points; the actual file content lives in the
///     materialised workspace on the worker pod and is read via the Claude CLI's built-in
///     Read tool, not via MCP.
/// </summary>
public sealed class CustomAppTemplateManifestResponse
{
    /// <summary>Whether the call succeeded.</summary>
    public bool IsSuccess { get; set; }

    /// <summary>Set when <see cref="IsSuccess" /> is false.</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>Free-text message for the AI client (success summary).</summary>
    public string? Message { get; set; }

    /// <summary>Always null — this tool is workspace-/tenant-agnostic.</summary>
    public string? TenantId { get; set; }

    /// <summary>The git-tag / branch of the template-repo the manifest describes.</summary>
    public string? TemplateRef { get; set; }

    /// <summary>Files that ship in the canonical scaffold.</summary>
    public List<CustomAppTemplateFile> Files { get; set; } = new();
}

/// <summary>One canonical file in the Custom-App template scaffold.</summary>
public sealed class CustomAppTemplateFile
{
    /// <summary>Path relative to the workspace root (e.g. <c>src/custom-app/src/app/app.routes.ts</c>).</summary>
    public required string Path { get; init; }

    /// <summary>What the file is for and when the agent should edit vs leave it alone.</summary>
    public required string Description { get; init; }

    /// <summary>
    ///     Whether the agent should expect to edit this file when adding features. Files marked
    ///     <c>false</c> are typically generated (npm dirs, Angular CLI output) or hands-off
    ///     (license stubs, lockfiles).
    /// </summary>
    public bool Editable { get; init; } = true;
}

/// <summary>
///     Response of <c>list_kendo_components</c>. Catalog of Kendo Angular components the
///     template-repo's package.json declares — names, npm packages, basic usage hints
///     (#4135). The agent uses this to avoid guessing the import path and avoid mixing
///     versions across categories.
/// </summary>
public sealed class KendoComponentCatalogResponse
{
    /// <summary>Whether the call succeeded.</summary>
    public bool IsSuccess { get; set; }

    /// <summary>Set when <see cref="IsSuccess" /> is false.</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>Free-text message for the AI client (success summary).</summary>
    public string? Message { get; set; }

    /// <summary>Always null — this tool is workspace-/tenant-agnostic.</summary>
    public string? TenantId { get; set; }

    /// <summary>Pinned Kendo Angular major version the catalog targets.</summary>
    public string? KendoVersion { get; set; }

    /// <summary>One row per Kendo Angular component the template-repo lists.</summary>
    public List<KendoComponentInfo> Components { get; set; } = new();
}

/// <summary>One Kendo Angular component entry.</summary>
public sealed class KendoComponentInfo
{
    /// <summary>Logical category for grouping in the UI / docs (e.g. <c>Layout</c>, <c>Grid</c>).</summary>
    public required string Category { get; init; }

    /// <summary>Friendly name (e.g. <c>Grid</c>, <c>DropDownList</c>, <c>AppBar</c>).</summary>
    public required string Name { get; init; }

    /// <summary>npm package providing the component (e.g. <c>@progress/kendo-angular-grid</c>).</summary>
    public required string NpmPackage { get; init; }

    /// <summary>Angular module / standalone-component class name typically imported.</summary>
    public required string ImportSymbol { get; init; }

    /// <summary>One-line note on when to use this component, written for the AI to read.</summary>
    public required string Hint { get; init; }
}

/// <summary>
///     Response of <c>plan_custom_app_scaffold</c>. Given an intended app name + a list of
///     drawer items the user wants, returns a structured plan the agent then implements
///     against the materialised workspace (#4135). The plan is advisory — the agent can
///     adjust it before writing files.
/// </summary>
public sealed class CustomAppScaffoldPlanResponse
{
    /// <summary>Whether the call succeeded.</summary>
    public bool IsSuccess { get; set; }

    /// <summary>Set when <see cref="IsSuccess" /> is false.</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>Free-text message for the AI client (success summary).</summary>
    public string? Message { get; set; }

    /// <summary>Always null — this tool is workspace-/tenant-agnostic.</summary>
    public string? TenantId { get; set; }

    /// <summary>Slug derived from the app name (used in package.json / config).</summary>
    public string? AppSlug { get; set; }

    /// <summary>Suggested page-component scaffolds — one per drawer item that needs a new page.</summary>
    public List<ScaffoldedPageInfo> Pages { get; set; } = new();

    /// <summary>
    ///     Drawer command-settings entries the agent should add to
    ///     <c>src/app/services/my-command-settings.service.ts</c>. Order mirrors the input.
    /// </summary>
    public List<DrawerItemPlan> DrawerItems { get; set; } = new();

    /// <summary>
    ///     Route entries the agent should add to <c>src/app/app.routes.ts</c> under the
    ///     <c>:lang</c> children block.
    /// </summary>
    public List<RoutePlan> Routes { get; set; } = new();
}

/// <summary>One planned page-component scaffold.</summary>
public sealed class ScaffoldedPageInfo
{
    /// <summary>kebab-case route slug — e.g. <c>user-list</c>.</summary>
    public required string RouteSlug { get; init; }

    /// <summary>PascalCase class name without suffix — e.g. <c>UserList</c>.</summary>
    public required string ClassName { get; init; }

    /// <summary>Workspace-relative file path of the new page's TS file.</summary>
    public required string TsPath { get; init; }

    /// <summary>Workspace-relative file path of the new page's template.</summary>
    public required string HtmlPath { get; init; }

    /// <summary>Workspace-relative file path of the new page's styles.</summary>
    public required string ScssPath { get; init; }
}

/// <summary>Planned drawer-navigation entry.</summary>
public sealed class DrawerItemPlan
{
    /// <summary>Matching <see cref="ScaffoldedPageInfo.RouteSlug" /> the item links to.</summary>
    public required string Id { get; init; }

    /// <summary>Human-readable label shown in the drawer.</summary>
    public required string Text { get; init; }

    /// <summary>Suggested Kendo SVG icon import (e.g. <c>userIcon</c>).</summary>
    public required string SvgIcon { get; init; }
}

/// <summary>Planned route entry under <c>app.routes.ts</c>'s <c>:lang</c> children.</summary>
public sealed class RoutePlan
{
    /// <summary>Route path segment (e.g. <c>user-list</c>).</summary>
    public required string Path { get; init; }

    /// <summary>Lazy-loaded component import path (e.g. <c>./pages/user-list/user-list</c>).</summary>
    public required string ComponentPath { get; init; }

    /// <summary>PascalCase class name the import resolves to (matches <see cref="ScaffoldedPageInfo.ClassName" />).</summary>
    public required string ComponentClassName { get; init; }

    /// <summary>Translation key for the breadcrumb / page title.</summary>
    public required string TitleKey { get; init; }
}

/// <summary>
///     Response of <c>create_tenant_app_repo</c> (#4146). Returns the URLs the agent needs
///     to wire <c>git remote add origin</c> and the metadata an App Catalog can later read.
///     On a name-collision conflict (HTTP 422 from GitHub), <see cref="IsConflict" /> is true
///     and the existing repo's URL is in <see cref="CloneUrl" /> so the agent can decide to
///     reuse the repo or surface to the operator that a name change is needed.
/// </summary>
public sealed class CreateTenantAppRepoResponse
{
    /// <summary>Whether the call succeeded.</summary>
    public bool IsSuccess { get; set; }

    /// <summary>Set when <see cref="IsSuccess" /> is false.</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>Free-text message for the AI client (success summary).</summary>
    public string? Message { get; set; }

    /// <summary>Owner login (the PAT-owner's user or the explicit org).</summary>
    public string? Owner { get; set; }

    /// <summary>Full repo name (<c>owner/name</c>).</summary>
    public string? FullName { get; set; }

    /// <summary>
    ///     Set to <c>true</c> when GitHub refused with HTTP 422 because a repo of that name
    ///     already exists. <see cref="CloneUrl" /> + <see cref="FullName" /> carry the
    ///     existing repo's URLs so the agent can reuse without a second round-trip.
    /// </summary>
    public bool IsConflict { get; set; }

    /// <summary>HTTPS clone URL — what the agent passes to <c>git remote add origin</c>.</summary>
    public string? CloneUrl { get; set; }

    /// <summary>SSH clone URL — alternative for the dev-bridge case.</summary>
    public string? SshUrl { get; set; }

    /// <summary>Default branch name (typically <c>main</c>).</summary>
    public string? DefaultBranch { get; set; }

    /// <summary>GitHub's numeric repository id, useful for cross-referencing with the API.</summary>
    public long? RepoId { get; set; }
}
