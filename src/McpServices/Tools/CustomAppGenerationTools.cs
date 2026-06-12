using System.ComponentModel;
using System.Text.RegularExpressions;
using Meshmakers.Octo.Backend.McpServices.Models;
using Meshmakers.Octo.Backend.McpServices.Services;
using Microsoft.Extensions.DependencyInjection;
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

    /// <summary>
    ///     Expand a <see cref="CustomAppScaffoldPlanResponse" /> + per-page CK type bindings
    ///     into the structured operation list the agent applies via its built-in
    ///     <c>Write</c>/<c>Edit</c> tools. The MCP server cannot write to the agent's
    ///     workspace directly — this tool centralises design while keeping the agent in
    ///     control of the apply. Pure-logic — no I/O, no auth.
    ///     <para>
    ///         The 7 per-page templates (page TS/HTML/SCSS, service, service spec, DTO,
    ///         GraphQL query) and the 2 cross-cutting edits (app.routes.ts,
    ///         my-command-settings.service.ts) are anchored to
    ///         <c>meshmakers/template-repo</c>'s conventions; an edit whose anchor pattern
    ///         doesn't match the workspace's file shape is omitted with a clear
    ///         <c>NextSteps</c> hint so the agent can fall back to a manual edit.
    ///         custom-svg-icons.ts is handled via <c>NextSteps</c> only — its append-only
    ///         shape (the last icon varies per project) makes a stable anchor pattern
    ///         brittle.
    ///     </para>
    /// </summary>
    [McpServerTool(Name = "apply_custom_app_scaffold")]
    [McpRisk(McpRiskLevel.Medium)]
    [Description(
        "Expand a plan_custom_app_scaffold output into the canonical Custom-App file " +
        "contents + route/drawer edits (#4135 / M3 B-2c-2). Returns WriteOps the agent " +
        "applies via Write, EditOps it applies via Edit, and NextSteps for things the " +
        "tool can't pre-fill (GraphQL leaves for unbound pages, grid columns, icon " +
        "append). Pass the plan plus per-page CK type bindings keyed by RouteSlug — the " +
        "tool pre-fills DTO + GraphQL leaves from each binding's attribute list. Pure-" +
        "logic — no I/O, no auth.")]
    public static Task<ApplyCustomAppScaffoldResponse> ApplyCustomAppScaffold(
        McpServer server,
        [Description("Output of plan_custom_app_scaffold — must have IsSuccess=true and at least one page.")]
        CustomAppScaffoldPlanResponse plan,
        [Description("Per-page CK type bindings, keyed by RouteSlug. Pages without an entry get DTO + GraphQL stubs with TODO markers.")]
        Dictionary<string, ApplyScaffoldTypeBinding>? typeBindings = null,
        [Description("Workspace-relative path to app.routes.ts. Defaults to 'src/custom-app/src/app/app.routes.ts'.")]
        string? appRoutesPath = null,
        [Description("Workspace-relative path to my-command-settings.service.ts. Defaults to 'src/custom-app/src/app/services/my-command-settings.service.ts'.")]
        string? commandSettingsPath = null,
        [Description("Workspace-relative path to custom-svg-icons.ts. Defaults to 'src/custom-app/src/app/custom-svg-icons.ts'.")]
        string? customSvgIconsPath = null)
    {
        if (plan == null)
        {
            return Task.FromResult(new ApplyCustomAppScaffoldResponse
            {
                IsSuccess = false,
                ErrorMessage = "plan is required.",
            });
        }
        if (!plan.IsSuccess)
        {
            return Task.FromResult(new ApplyCustomAppScaffoldResponse
            {
                IsSuccess = false,
                ErrorMessage = "plan.IsSuccess must be true — pass a successful plan_custom_app_scaffold output.",
            });
        }
        if (plan.Pages.Count == 0)
        {
            return Task.FromResult(new ApplyCustomAppScaffoldResponse
            {
                IsSuccess = false,
                ErrorMessage = "plan must contain at least one Page.",
            });
        }

        var bindings = typeBindings ?? new Dictionary<string, ApplyScaffoldTypeBinding>(StringComparer.Ordinal);
        var routesPath = string.IsNullOrWhiteSpace(appRoutesPath)
            ? "src/custom-app/src/app/app.routes.ts" : appRoutesPath;
        var settingsPath = string.IsNullOrWhiteSpace(commandSettingsPath)
            ? "src/custom-app/src/app/services/my-command-settings.service.ts" : commandSettingsPath;
        var iconsPath = string.IsNullOrWhiteSpace(customSvgIconsPath)
            ? "src/custom-app/src/app/custom-svg-icons.ts" : customSvgIconsPath;

        var writeOps = new List<WriteOp>();
        var routeEntries = new List<string>();
        var drawerEntries = new List<string>();
        var iconImports = new List<string>();
        var iconNextSteps = new List<string>();
        var nextSteps = new List<string>();

        for (var i = 0; i < plan.Pages.Count; i++)
        {
            var page = plan.Pages[i];
            bindings.TryGetValue(page.RouteSlug, out var binding);
            var values = CustomAppTemplateRenderer.BuildValues(page, binding);

            var pageFolder = $"src/custom-app/src/app/pages/{page.RouteSlug}";
            writeOps.Add(new WriteOp
            {
                Path = $"{pageFolder}/{page.RouteSlug}.ts",
                Content = CustomAppTemplateRenderer.Render(CustomAppTemplates.PageComponentTs, values),
                Purpose = "page-component",
            });
            writeOps.Add(new WriteOp
            {
                Path = $"{pageFolder}/{page.RouteSlug}.html",
                Content = CustomAppTemplateRenderer.Render(CustomAppTemplates.PageTemplateHtml, values),
                Purpose = "page-template",
            });
            writeOps.Add(new WriteOp
            {
                Path = $"{pageFolder}/{page.RouteSlug}.scss",
                Content = CustomAppTemplates.PageStylesScss,
                Purpose = "page-styles",
            });
            writeOps.Add(new WriteOp
            {
                Path = $"src/custom-app/src/app/services/{page.RouteSlug}.service.ts",
                Content = CustomAppTemplateRenderer.Render(CustomAppTemplates.ServiceTs, values),
                Purpose = "service",
            });
            writeOps.Add(new WriteOp
            {
                Path = $"src/custom-app/src/app/services/{page.RouteSlug}.service.spec.ts",
                Content = CustomAppTemplateRenderer.Render(CustomAppTemplates.ServiceSpecTs, values),
                Purpose = "service-spec",
            });
            writeOps.Add(new WriteOp
            {
                Path = $"src/custom-app/src/app/models/{page.RouteSlug}-entry.ts",
                Content = CustomAppTemplateRenderer.Render(CustomAppTemplates.ModelTs, values),
                Purpose = "dto",
            });
            writeOps.Add(new WriteOp
            {
                Path = $"src/custom-app/src/app/graphQL/get{page.ClassName}.graphql",
                Content = CustomAppTemplateRenderer.Render(CustomAppTemplates.QueryGraphql, values),
                Purpose = "graphql-query",
            });

            // Build the per-page snippets that aggregate into single EditOps below.
            var route = plan.Routes.FirstOrDefault(r => r.Path == page.RouteSlug);
            if (route != null)
            {
                routeEntries.Add(BuildRouteEntry(route, page));
            }
            var drawer = plan.DrawerItems.FirstOrDefault(d => d.Id == page.RouteSlug);
            if (drawer != null)
            {
                drawerEntries.Add(BuildDrawerEntry(drawer));
                iconImports.Add(drawer.SvgIcon);
                iconNextSteps.Add(
                    $"Add an icon export for `{drawer.SvgIcon}` to {iconsPath} (one ${nameof(SuggestIcon)}-style SVGIcon constant). " +
                    "Append at the end of the file — its append-only shape makes a stable Edit anchor brittle.");
            }

            if (binding == null)
            {
                nextSteps.Add(
                    $"Page '{page.RouteSlug}' has no type binding — DTO + GraphQL query carry TODO stubs. " +
                    $"Fill in the DTO fields in models/{page.RouteSlug}-entry.ts and the leaf list in graphQL/get{page.ClassName}.graphql before running `npm run codegen`.");
            }
            else
            {
                nextSteps.Add(
                    $"Page '{page.RouteSlug}' bound to {binding.TypeId}. Pick the grid columns in pages/{page.RouteSlug}/{page.RouteSlug}.html — the DTO + GraphQL stub already enumerate the attributes.");
            }
        }

        var editOps = new List<EditOp>();

        if (routeEntries.Count > 0)
        {
            // Anchor: the close of the :lang children block + the wildcard ** route start.
            // This 4-line sequence is unique in the template-repo's app.routes.ts.
            const string routesOld = "    ],\n  },\n  {\n    path: '**',";
            var routesNew = string.Join("", routeEntries) + routesOld;
            editOps.Add(new EditOp
            {
                Path = routesPath,
                OldString = routesOld,
                NewString = routesNew,
                Purpose = "route-registration",
            });
        }

        if (drawerEntries.Count > 0)
        {
            // Anchor: the close of the commandItems array + the next override.
            const string settingsOld = "    ];\n  }\n\n  override get navigateRelativeToRoute(): ActivatedRoute {";
            var settingsNew = string.Join("", drawerEntries) + settingsOld;
            editOps.Add(new EditOp
            {
                Path = settingsPath,
                OldString = settingsOld,
                NewString = settingsNew,
                Purpose = "drawer-item",
            });
        }

        if (iconImports.Count > 0)
        {
            var imports = string.Join(", ", iconImports.Distinct(StringComparer.Ordinal));
            nextSteps.Add(
                $"Import {{ {imports} }} from `{iconsPath.Replace("/custom-svg-icons.ts", "/custom-svg-icons")}` in {settingsPath} so the drawer entries can reference the icon constants.");
            nextSteps.AddRange(iconNextSteps);
        }

        nextSteps.Add(
            "After applying the WriteOps + EditOps, run `npm run codegen` from `src/custom-app/` " +
            "to emit each `getXxx.generated.ts` from the new `getXxx.graphql` and refresh `globalTypes.ts`. " +
            "Then run `npm run lint && npm run test:ci && npm run build:prod` before pushing.");

        return Task.FromResult(new ApplyCustomAppScaffoldResponse
        {
            IsSuccess = true,
            Message = $"Expanded plan into {writeOps.Count} write op(s) + {editOps.Count} edit op(s) for {plan.Pages.Count} page(s).",
            WriteOps = writeOps,
            EditOps = editOps,
            NextSteps = nextSteps,
        });
    }

    /// <summary>
    ///     Format one route entry for <c>app.routes.ts</c>'s <c>:lang</c> children block.
    ///     Mirrors the lazy-load shape used in <c>meshmakers/ai-sandbox-app</c> PR #8.
    /// </summary>
    private static string BuildRouteEntry(RoutePlan route, ScaffoldedPageInfo page)
    {
        // Breadcrumb label: humanise the kebab slug ("audit-log" → "Audit Log").
        var label = string.Join(" ", route.Path.Split('-')
            .Where(p => p.Length > 0)
            .Select(p => char.ToUpperInvariant(p[0]) + p[1..].ToLowerInvariant()));
        return
            "      {\n" +
            $"        path: '{route.Path}',\n" +
            "        loadComponent: () =>\n" +
            $"          import('{route.ComponentPath}').then((m) => m.{route.ComponentClassName}Component),\n" +
            "        data: {\n" +
            "          breadcrumb: [\n" +
            "            { label: 'Home', url: '' },\n" +
            $"            {{ label: '{label}' }},\n" +
            "          ],\n" +
            "        },\n" +
            "      },\n";
    }

    /// <summary>
    ///     Format one drawer command-settings entry. Mirrors the shape in template-repo's
    ///     <c>my-command-settings.service.ts</c>.
    /// </summary>
    private static string BuildDrawerEntry(DrawerItemPlan drawer)
    {
        return
            "      {\n" +
            $"        id: '{drawer.Id}',\n" +
            "        type: 'link',\n" +
            $"        text: '{drawer.Text}',\n" +
            $"        svgIcon: {drawer.SvgIcon},\n" +
            $"        link: async (): Promise<string> => '{drawer.Id}',\n" +
            "      },\n";
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

    /// <summary>
    ///     Creates a new GitHub repo under the PAT-owner's account or an explicit org (#4146).
    ///     Used as the first step of a Custom-App-generation session so the workspace's
    ///     <c>git remote add origin</c> + first push has a destination. Credentials flow:
    ///     the worker pod's materialiser writes the tenant's PAT into the <c>GH_TOKEN</c>
    ///     env var; the agent reads it via Bash and passes it on this tool's
    ///     <c>accessToken</c> parameter. The MCP service never persists the PAT.
    /// </summary>
    [McpServerTool(Name = "create_tenant_app_repo")]
    [McpRisk(McpRiskLevel.Medium)]
    [Description(
        "Create a new GitHub repo for a tenant's Custom-App (#4146). Pass the PAT from " +
        "the worker pod's $GH_TOKEN env var on `accessToken`. Returns clone URLs + " +
        "default branch + repo id. On a name collision the response carries " +
        "IsConflict=true with the existing repo's URL so the agent can reuse rather than " +
        "loop on retries. The tool never writes the PAT to logs or persisted state.")]
    public static async Task<CreateTenantAppRepoResponse> CreateTenantAppRepo(
        McpServer server,
        [Description(
            "The new repo's name. Validated against GitHub's pattern (alphanumerics, dashes, " +
            "underscores, dots; no slashes). Required.")]
        string name,
        [Description(
            "GitHub PAT with `repo` scope. Read the worker pod's $GH_TOKEN env var via the " +
            "CLI's Bash tool and pass the value here. Required — the tool refuses to call " +
            "GitHub with an empty token rather than risk a 401 cascade.")]
        string accessToken,
        [Description("Optional short description shown on the GitHub repo page. Default null (no description).")]
        string? description = null,
        [Description(
            "Create as a private repo. Default true — tenant code shouldn't accidentally " +
            "land in a public repo. Override only when the operator explicitly wants public.")]
        bool isPrivate = true,
        [Description(
            "Optional GitHub org slug. When set, creates under POST /orgs/{org}/repos; the " +
            "PAT must have admin access on the org. When null, creates under the PAT-owner's " +
            "user account.")]
        string? org = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return new CreateTenantAppRepoResponse
            {
                IsSuccess = false,
                ErrorMessage = "Required: name."
            };
        }
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            return new CreateTenantAppRepoResponse
            {
                IsSuccess = false,
                ErrorMessage =
                    "Required: accessToken. Read $GH_TOKEN from the worker pod env via Bash; the materialiser " +
                    "writes it on session start when an AiCredentialBinding(Kind=GitHubPat) is registered for the tenant."
            };
        }
        // Pre-validate the obvious cases at the tool layer so GitHub doesn't have to. Saves
        // a round-trip and the resulting 422-disambiguation logic in the client.
        if (name.Contains('/') || name.Contains(' '))
        {
            return new CreateTenantAppRepoResponse
            {
                IsSuccess = false,
                ErrorMessage = $"Invalid repo name '{name}'. Repo names cannot contain '/' or spaces."
            };
        }

        try
        {
            var client = server.Services!.GetRequiredService<IGitHubRepoApiClient>();
            var result = await client.CreateAsync(accessToken, name, description, isPrivate, org, cancellationToken);
            return result.Outcome switch
            {
                GitHubRepoCreateOutcome.Created => new CreateTenantAppRepoResponse
                {
                    IsSuccess = true,
                    Message = $"Created repo {result.Repo!.FullName}.",
                    Owner = result.Repo.Owner,
                    FullName = result.Repo.FullName,
                    CloneUrl = result.Repo.CloneUrl,
                    SshUrl = result.Repo.SshUrl,
                    DefaultBranch = result.Repo.DefaultBranch,
                    RepoId = result.Repo.RepoId,
                },
                GitHubRepoCreateOutcome.Conflict => new CreateTenantAppRepoResponse
                {
                    IsSuccess = false,
                    IsConflict = true,
                    ErrorMessage = result.ErrorMessage,
                    Owner = result.Repo?.Owner,
                    FullName = result.Repo?.FullName,
                    CloneUrl = result.Repo?.CloneUrl,
                    SshUrl = result.Repo?.SshUrl,
                    DefaultBranch = result.Repo?.DefaultBranch,
                    RepoId = result.Repo?.RepoId,
                },
                GitHubRepoCreateOutcome.Unauthorised => new CreateTenantAppRepoResponse
                {
                    IsSuccess = false,
                    ErrorMessage = result.ErrorMessage,
                },
                _ => new CreateTenantAppRepoResponse
                {
                    IsSuccess = false,
                    ErrorMessage = result.ErrorMessage,
                },
            };
        }
        catch (Exception ex)
        {
            return new CreateTenantAppRepoResponse
            {
                IsSuccess = false,
                ErrorMessage = ex.Message,
            };
        }
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
