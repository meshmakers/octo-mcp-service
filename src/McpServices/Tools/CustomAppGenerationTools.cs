using System.ComponentModel;
using System.Text.RegularExpressions;
using Meshmakers.Octo.Backend.McpServices.Models;
using ModelContextProtocol.Server;

// ReSharper disable UnusedMember.Global

namespace Meshmakers.Octo.Backend.McpServices.Tools;

/// <summary>
///     Guidance tools for the OctoMesh Custom-App generation flow (#4135). All three tools
///     are pure-logic / informational — no SDK call, no DB call. They give the worker pod
///     agent the conventions and catalogs it needs to scaffold a Custom-App that matches
///     the canonical <c>meshmakers/template-repo</c> shape without guessing import paths,
///     naming conventions, or drawer-item structure. The actual file mutation happens via
///     the Claude CLI's built-in Read / Edit / Write / Bash tools against the materialised
///     workspace on the worker pod.
/// </summary>
[McpServerToolType]
public sealed class CustomAppGenerationTools
{
    /// <summary>
    ///     Returns the manifest of canonical files in the OctoMesh Custom-App template-repo
    ///     scaffold. The agent reads this to know which files exist as starting points and
    ///     which are hands-off (generated artifacts, lockfiles). Pure-logic — no I/O.
    /// </summary>
    [McpServerTool(Name = "get_custom_app_template_manifest")]
    [McpRisk(McpRiskLevel.Low)]
    [Description(
        "Manifest of canonical files in the OctoMesh Custom-App template-repo scaffold (#4135). " +
        "Read once at the start of a Custom-App-generation session to know which files are " +
        "in scope for editing and which are generated artifacts. Pure-logic — no I/O, no auth.")]
    public static Task<CustomAppTemplateManifestResponse> GetCustomAppTemplateManifest(
        McpServer server)
    {
        var files = TemplateManifestCatalog.GetFiles();
        return Task.FromResult(new CustomAppTemplateManifestResponse
        {
            IsSuccess = true,
            Message = $"Returning {files.Count} canonical template files.",
            TemplateRef = TemplateManifestCatalog.TemplateRef,
            Files = files,
        });
    }

    /// <summary>
    ///     Returns the catalog of Kendo Angular components the template-repo's package.json
    ///     declares. The agent uses this to avoid guessing import paths and avoid mixing
    ///     versions across categories. Pure-logic — no I/O.
    /// </summary>
    [McpServerTool(Name = "list_kendo_components")]
    [McpRisk(McpRiskLevel.Low)]
    [Description(
        "Catalog of Kendo Angular components the OctoMesh Custom-App template-repo pins, with " +
        "their npm packages, import symbols, and one-line usage hints. Read before importing " +
        "a Kendo component to avoid guessing the package name. Pure-logic — no I/O, no auth.")]
    public static Task<KendoComponentCatalogResponse> ListKendoComponents(
        McpServer server,
        [Description("Optional category filter (e.g. 'Layout', 'Grid', 'Inputs'). Case-insensitive. Empty returns everything.")]
        string? category = null)
    {
        var all = KendoComponentCatalog.GetAll();
        var filtered = string.IsNullOrWhiteSpace(category)
            ? all
            : all.Where(c => string.Equals(c.Category, category, StringComparison.OrdinalIgnoreCase)).ToList();

        return Task.FromResult(new KendoComponentCatalogResponse
        {
            IsSuccess = true,
            Message = string.IsNullOrWhiteSpace(category)
                ? $"Returning {filtered.Count} components across all categories."
                : $"Returning {filtered.Count} components in category '{category}'.",
            KendoVersion = KendoComponentCatalog.KendoVersion,
            Components = filtered,
        });
    }

    /// <summary>
    ///     Given an intended app slug + a list of drawer items the user wants, returns a
    ///     structured plan the agent then implements against the workspace. The plan is
    ///     advisory — the agent can adjust it before writing files. Pure-logic — no I/O.
    /// </summary>
    [McpServerTool(Name = "plan_custom_app_scaffold")]
    [McpRisk(McpRiskLevel.Low)]
    [Description(
        "Produce a structured scaffold plan for a new OctoMesh Custom-App: page components, " +
        "drawer items, routes (#4135). Pass the app slug and a list of drawer items the user " +
        "wants. The tool returns kebab/PascalCase derivations, target file paths, and the route " +
        "entries — the agent then writes the files via Edit/Write. Pure-logic — no I/O, no auth.")]
    public static Task<CustomAppScaffoldPlanResponse> PlanCustomAppScaffold(
        McpServer server,
        [Description("App slug — kebab-case (e.g. 'energy-portal'). Used for package.json + config.")]
        string appSlug,
        [Description("Drawer item labels — newline- or comma-separated (e.g. 'User List, Asset Table, Profile Editor'). Each becomes a page.")]
        string drawerItems)
    {
        if (string.IsNullOrWhiteSpace(appSlug))
        {
            return Task.FromResult(new CustomAppScaffoldPlanResponse
            {
                IsSuccess = false,
                ErrorMessage = "appSlug is required.",
            });
        }
        if (string.IsNullOrWhiteSpace(drawerItems))
        {
            return Task.FromResult(new CustomAppScaffoldPlanResponse
            {
                IsSuccess = false,
                ErrorMessage = "drawerItems is required — pass at least one label.",
            });
        }

        var slug = Kebab(appSlug.Trim());
        var items = drawerItems
            .Split(new[] { '\n', '\r', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToList();

        if (items.Count == 0)
        {
            return Task.FromResult(new CustomAppScaffoldPlanResponse
            {
                IsSuccess = false,
                ErrorMessage = "drawerItems must contain at least one non-empty label.",
            });
        }

        var pages = new List<ScaffoldedPageInfo>();
        var drawer = new List<DrawerItemPlan>();
        var routes = new List<RoutePlan>();

        foreach (var label in items)
        {
            var slugForItem = Kebab(label);
            var className = Pascal(label);
            pages.Add(new ScaffoldedPageInfo
            {
                RouteSlug = slugForItem,
                ClassName = className,
                TsPath = $"src/custom-app/src/app/pages/{slugForItem}/{slugForItem}.ts",
                HtmlPath = $"src/custom-app/src/app/pages/{slugForItem}/{slugForItem}.html",
                ScssPath = $"src/custom-app/src/app/pages/{slugForItem}/{slugForItem}.scss",
            });
            drawer.Add(new DrawerItemPlan
            {
                Id = slugForItem,
                Text = label,
                SvgIcon = SuggestIcon(label),
            });
            routes.Add(new RoutePlan
            {
                Path = slugForItem,
                ComponentPath = $"./pages/{slugForItem}/{slugForItem}",
                ComponentClassName = className,
                TitleKey = $"PAGES.{slugForItem.ToUpperInvariant().Replace('-', '_')}.TITLE",
            });
        }

        return Task.FromResult(new CustomAppScaffoldPlanResponse
        {
            IsSuccess = true,
            Message = $"Planned {pages.Count} page(s) for app '{slug}'.",
            AppSlug = slug,
            Pages = pages,
            DrawerItems = drawer,
            Routes = routes,
        });
    }

    private static string Kebab(string s)
    {
        // "User List" / "userList" / "user_list" -> "user-list"
        var step1 = Regex.Replace(s, @"([a-z0-9])([A-Z])", "$1-$2");
        var step2 = Regex.Replace(step1, @"[\s_]+", "-").ToLowerInvariant();
        return Regex.Replace(step2, @"[^a-z0-9\-]", "").Trim('-');
    }

    private static string Pascal(string s)
    {
        var parts = Regex.Split(s, @"[\s_\-]+").Where(p => p.Length > 0);
        return string.Concat(parts.Select(p => char.ToUpperInvariant(p[0]) + p[1..].ToLowerInvariant()));
    }

    private static string SuggestIcon(string label)
    {
        // Best-effort icon picker — falls back to `gridIcon` so the agent always gets a
        // valid import. The template-repo's @progress/kendo-svg-icons package exports
        // hundreds of these; the catalog below is a small curated subset.
        var l = label.ToLowerInvariant();
        if (l.Contains("user") || l.Contains("profile")) return "userIcon";
        if (l.Contains("asset") || l.Contains("device")) return "componentsIcon";
        if (l.Contains("dashboard") || l.Contains("home")) return "windowSettingsIcon";
        if (l.Contains("report") || l.Contains("stats") || l.Contains("analytics")) return "chartLineIcon";
        if (l.Contains("setting") || l.Contains("config") || l.Contains("admin")) return "gearIcon";
        if (l.Contains("file") || l.Contains("document")) return "fileIcon";
        if (l.Contains("calendar") || l.Contains("schedule")) return "calendarIcon";
        if (l.Contains("message") || l.Contains("chat") || l.Contains("notification")) return "envelopeIcon";
        return "gridIcon";
    }
}

/// <summary>
///     Static catalog of the canonical Custom-App template files. Updated when the
///     template-repo adds / removes a load-bearing file.
/// </summary>
internal static class TemplateManifestCatalog
{
    public const string TemplateRef = "main";

    public static List<CustomAppTemplateFile> GetFiles() => new()
    {
        new() { Path = "src/custom-app/package.json",                            Description = "Angular 21 + Kendo + Apollo + Tailwind dep set. Edit name/version/dependencies; do NOT touch lockfile entries." },
        new() { Path = "src/custom-app/angular.json",                            Description = "Angular CLI workspace config. Edit only when adding a new project or build configuration." },
        new() { Path = "src/custom-app/tsconfig.json",                           Description = "TypeScript compiler options (strict mode on). Avoid loosening; lift narrow exceptions via // @ts-expect-error instead." },
        new() { Path = "src/custom-app/codegen.yml",                             Description = "GraphQL codegen config. Update when adding a new schema source." },
        new() { Path = "src/custom-app/src/app/app.ts",                          Description = "Root standalone component. AppBar + Drawer layout entry point." },
        new() { Path = "src/custom-app/src/app/app.routes.ts",                   Description = "Routing table. Add new routes under the `:lang` children block — pattern: { path: 'slug', loadComponent: () => import('./pages/slug/slug').then(m => m.Slug) }." },
        new() { Path = "src/custom-app/src/app/app.config.ts",                   Description = "Application config — providers, HttpClient, OAuth, Apollo. Edit when adding a cross-cutting provider." },
        new() { Path = "src/custom-app/src/app/services/my-command-settings.service.ts", Description = "Drawer navigation items. Add a new item per page: { id, type: 'link', text, svgIcon, link: async () => 'slug' }." },
        new() { Path = "src/custom-app/src/assets/config.json",                  Description = "Runtime config (OctoMesh service URLs, supported languages). Overridden by env vars in the nginx Docker image at startup." },
        new() { Path = "src/custom-app/src/assets/i18n/en.json",                 Description = "English translation strings. Add a new top-level key per page (e.g. PAGES.USER_LIST.TITLE)." },
        new() { Path = "src/custom-app/src/assets/i18n/de.json",                 Description = "German translation strings. Keep keys in sync with en.json — the i18n loader does not key-fallback at runtime." },
        new() { Path = "src/custom-app/src/styles.scss",                         Description = "Global Tailwind + Kendo theme imports. Avoid component-specific styles here." },
        new() { Path = "src/custom-app/Dockerfile",                              Description = "Production nginx image. Edit only when adding a new build-time env or nginx route." },
        new() { Path = "src/custom-app/nginx.conf",                              Description = "nginx routing for the SPA fallback. Edit when adding a new top-level API proxy." },
        new() { Path = "src/custom-app/src/app/graphQL/",                        Description = "GraphQL .graphql query files + generated TS. Add a .graphql file then run `npm run codegen`.", Editable = false },
        new() { Path = "src/custom-app/src/app/pages/",                          Description = "Page components — one folder per route. Each folder has slug.ts / slug.html / slug.scss / slug.spec.ts." },
        new() { Path = "src/custom-app/src/app/services/",                       Description = "Application services. New domain services land here." },
        new() { Path = "src/custom-app/src/app/models/",                         Description = "TypeScript interfaces / DTOs not generated from GraphQL." },
        new() { Path = "src/custom-app/package-lock.json",                       Description = "npm lockfile. NEVER edit by hand; let `npm install` / `npm ci` manage it.", Editable = false },
        new() { Path = "src/charts/",                                            Description = "Helm charts for Kubernetes deployment of the Custom-App. Edit values.yaml when adapter URLs change." },
    };
}

/// <summary>
///     Static catalog of Kendo Angular components the template-repo pins. Single source of
///     truth — when template-repo's package.json bumps a category, mirror it here so the AI
///     never imports from a mismatched version.
/// </summary>
internal static class KendoComponentCatalog
{
    public const string KendoVersion = "23.2.0";

    public static List<KendoComponentInfo> GetAll() => new()
    {
        new() { Category = "Layout", Name = "AppBar",       NpmPackage = "@progress/kendo-angular-navigation", ImportSymbol = "AppBarComponent",       Hint = "Top app bar with brand + actions. Pair with Drawer." },
        new() { Category = "Layout", Name = "Drawer",       NpmPackage = "@progress/kendo-angular-layout",     ImportSymbol = "DrawerModule",          Hint = "Side-nav. Items come from CommandSettingsService — see template manifest." },
        new() { Category = "Layout", Name = "TabStrip",     NpmPackage = "@progress/kendo-angular-layout",     ImportSymbol = "TabStripModule",        Hint = "Tabbed content. Prefer Drawer for top-level navigation; TabStrip for in-page sub-sections." },
        new() { Category = "Layout", Name = "Card",         NpmPackage = "@progress/kendo-angular-layout",     ImportSymbol = "CardModule",            Hint = "Bordered content panel. Default container for page sections." },
        new() { Category = "Grid",   Name = "Grid",         NpmPackage = "@progress/kendo-angular-grid",       ImportSymbol = "GridModule",            Hint = "Data table. Bind via [data] + use [kendoGridBinding] directive when paging/sort/filter need built-in handling." },
        new() { Category = "Grid",   Name = "TreeList",     NpmPackage = "@progress/kendo-angular-treelist",   ImportSymbol = "TreeListModule",        Hint = "Hierarchical data grid. Use over Grid when rows have parent-child structure." },
        new() { Category = "Inputs", Name = "TextBox",      NpmPackage = "@progress/kendo-angular-inputs",     ImportSymbol = "TextBoxModule",         Hint = "Styled single-line text input. Replaces native input[type=text] in Kendo-themed pages." },
        new() { Category = "Inputs", Name = "NumericTextBox", NpmPackage = "@progress/kendo-angular-inputs",   ImportSymbol = "NumericTextBoxModule",  Hint = "Numeric input with up/down. Use over native input[type=number] for consistent locale handling." },
        new() { Category = "Inputs", Name = "DatePicker",   NpmPackage = "@progress/kendo-angular-dateinputs", ImportSymbol = "DateInputsModule",      Hint = "Calendar-popup date selector. Pair with i18n for localised formats." },
        new() { Category = "Inputs", Name = "Switch",       NpmPackage = "@progress/kendo-angular-inputs",     ImportSymbol = "SwitchModule",          Hint = "Boolean toggle. Use over Checkbox for explicit on/off semantics." },
        new() { Category = "Inputs", Name = "DropDownList", NpmPackage = "@progress/kendo-angular-dropdowns",  ImportSymbol = "DropDownListModule",    Hint = "Single-select dropdown. Use ComboBox when free-text entry is also allowed." },
        new() { Category = "Inputs", Name = "MultiSelect",  NpmPackage = "@progress/kendo-angular-dropdowns",  ImportSymbol = "MultiSelectModule",     Hint = "Multi-select chip-style dropdown." },
        new() { Category = "Buttons", Name = "Button",      NpmPackage = "@progress/kendo-angular-buttons",    ImportSymbol = "ButtonModule",          Hint = "Primary action button. Use the [themeColor] input for primary/error styling." },
        new() { Category = "Buttons", Name = "DropDownButton", NpmPackage = "@progress/kendo-angular-buttons", ImportSymbol = "DropDownButtonModule",  Hint = "Button + menu hybrid for row-level actions." },
        new() { Category = "Dialogs", Name = "Dialog",      NpmPackage = "@progress/kendo-angular-dialog",     ImportSymbol = "DialogModule",          Hint = "Modal dialog for confirmation flows / forms. Pair with the [actions] template for OK/Cancel." },
        new() { Category = "Dialogs", Name = "Notification", NpmPackage = "@progress/kendo-angular-notification", ImportSymbol = "NotificationModule", Hint = "Toast-style transient feedback. Inject NotificationService and call .show()." },
        new() { Category = "Indicators", Name = "Loader",   NpmPackage = "@progress/kendo-angular-indicators", ImportSymbol = "LoaderModule",          Hint = "Inline spinner. Pair with a signal-bound *ngIf to show during async work." },
        new() { Category = "Indicators", Name = "Badge",    NpmPackage = "@progress/kendo-angular-indicators", ImportSymbol = "BadgeModule",           Hint = "Small status pill — counts, online/offline, etc." },
        new() { Category = "Charts", Name = "Chart",        NpmPackage = "@progress/kendo-angular-charts",     ImportSymbol = "ChartsModule",          Hint = "Cartesian chart container. Use for time-series / asset metrics rendering." },
        new() { Category = "Icons",  Name = "SvgIcon",      NpmPackage = "@progress/kendo-angular-icons",      ImportSymbol = "SVGIconModule",         Hint = "Render an icon from @progress/kendo-svg-icons. Import the icon constant separately." },
    };
}
