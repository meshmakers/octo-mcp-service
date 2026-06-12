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

/// <summary>
///     Response of <c>apply_custom_app_scaffold</c> (#4135 / M3 B-2c-2). Expands a
///     <see cref="CustomAppScaffoldPlanResponse" /> plus per-page CK type bindings into a
///     structured operation list the AI agent then applies via its built-in Write/Edit
///     tools. The MCP server cannot write to the agent's workspace directly — this tool
///     centralises design while keeping the agent in control of the apply.
/// </summary>
public sealed class ApplyCustomAppScaffoldResponse
{
    /// <summary>Whether the call succeeded.</summary>
    public bool IsSuccess { get; set; }

    /// <summary>Set when <see cref="IsSuccess" /> is false.</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>Free-text message for the AI client (success summary).</summary>
    public string? Message { get; set; }

    /// <summary>
    ///     File-creation operations the agent applies via its built-in <c>Write</c> tool.
    ///     Each carries the workspace-relative path, the canonical content, and a
    ///     semantic <see cref="WriteOp.Purpose" /> tag.
    /// </summary>
    public List<WriteOp> WriteOps { get; set; } = new();

    /// <summary>
    ///     In-place edits to existing files the agent applies via its built-in <c>Edit</c>
    ///     tool. Each carries the path and a precise <c>(OldString, NewString)</c> pair the
    ///     agent passes verbatim. An edit whose anchor pattern doesn't match the workspace's
    ///     actual file is omitted; the operator-facing fallback is recorded in
    ///     <see cref="NextSteps" />.
    /// </summary>
    public List<EditOp> EditOps { get; set; } = new();

    /// <summary>
    ///     Plain-English next steps the agent should perform AFTER applying the ops —
    ///     things this tool can't pre-fill (GraphQL query body from CK attributes, grid
    ///     columns from the operator's chosen attribute subset, manual route edits when
    ///     the auto-anchor didn't match).
    /// </summary>
    public List<string> NextSteps { get; set; } = new();
}

/// <summary>One file the agent should create via <c>Write</c>.</summary>
public sealed class WriteOp
{
    /// <summary>Workspace-relative path of the file to create.</summary>
    public required string Path { get; init; }

    /// <summary>Canonical content the agent writes verbatim.</summary>
    public required string Content { get; init; }

    /// <summary>
    ///     Semantic tag describing what kind of file this is — used for the agent's
    ///     trace + the tool's <see cref="ApplyCustomAppScaffoldResponse.Message" />.
    ///     Values: <c>page-component</c>, <c>page-template</c>, <c>page-styles</c>,
    ///     <c>service</c>, <c>service-spec</c>, <c>dto</c>, <c>graphql-query</c>.
    /// </summary>
    public required string Purpose { get; init; }
}

/// <summary>One in-place edit the agent applies via <c>Edit</c>.</summary>
public sealed class EditOp
{
    /// <summary>Workspace-relative path of the file to edit.</summary>
    public required string Path { get; init; }

    /// <summary>Existing substring the agent passes to <c>Edit</c>'s <c>old_string</c>.</summary>
    public required string OldString { get; init; }

    /// <summary>Replacement substring the agent passes to <c>Edit</c>'s <c>new_string</c>.</summary>
    public required string NewString { get; init; }

    /// <summary>
    ///     Semantic tag for the trace. Values: <c>route-registration</c>,
    ///     <c>drawer-item</c>, <c>icon-import</c>.
    /// </summary>
    public required string Purpose { get; init; }
}

/// <summary>
///     Per-page CK type binding the agent passes to <c>apply_custom_app_scaffold</c>.
///     The tool pre-fills the DTO interface fields + the GraphQL query leaves from
///     <see cref="Attributes" />. Pages without a binding entry get empty DTO/GraphQL
///     stubs and a <c>NextSteps</c> hint to fill them in manually.
/// </summary>
public sealed class ApplyScaffoldTypeBinding
{
    /// <summary>
    ///     The CK type's stable id (e.g. <c>System.Ai-3/AiAuditEvent</c>). Used for the
    ///     Apollo service comment + the model file's header so a reviewer can trace
    ///     the page back to the source type.
    /// </summary>
    public required string TypeId { get; init; }

    /// <summary>
    ///     The GraphQL operation name under <c>runtime.</c> — e.g. <c>systemAiAuditEvent</c>.
    ///     Derived from the type id by camel-casing the leaf segment; pass it explicitly
    ///     so the tool doesn't have to re-derive the convention.
    /// </summary>
    public required string GraphqlOperationName { get; init; }

    /// <summary>
    ///     The attributes to project into the DTO interface + the GraphQL query leaves.
    ///     Order is preserved in both outputs. Empty list → empty DTO + TODO-only query.
    /// </summary>
    public List<ApplyScaffoldAttribute> Attributes { get; set; } = new();
}

/// <summary>One CK attribute the binding projects into the page's DTO + GraphQL query.</summary>
public sealed class ApplyScaffoldAttribute
{
    /// <summary>
    ///     Attribute name as the GraphQL schema exposes it (camelCase — e.g.
    ///     <c>eventType</c>, <c>actorRef</c>). Used verbatim as both the DTO field name
    ///     and the GraphQL leaf name.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    ///     TypeScript type for the DTO field — <c>string</c>, <c>number</c>, <c>Date</c>,
    ///     <c>boolean</c>. The agent maps the CK <c>AttributeValueTypes</c> to a TS type.
    ///     Free-form so a future custom type doesn't need a tool change.
    /// </summary>
    public required string TsType { get; init; }

    /// <summary>
    ///     Whether the attribute is nullable in the CK schema. Optional fields surface as
    ///     <c>name?: type | null</c> in the DTO and get coalesced to <c>?? null</c> in the
    ///     mapping function.
    /// </summary>
    public bool IsOptional { get; init; }
}
