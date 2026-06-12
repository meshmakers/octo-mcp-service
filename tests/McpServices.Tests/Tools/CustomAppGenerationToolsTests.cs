using FluentAssertions;
using Meshmakers.Octo.Backend.McpServices.Models;
using Meshmakers.Octo.Backend.McpServices.Services;
using Meshmakers.Octo.Backend.McpServices.Tools;
using Xunit;

namespace McpServices.Tests.Tools;

/// <summary>
///     Specs for the #4135 Custom-App generation guidance tools. All three tools are
///     pure-logic (no SDK call, no DB call, no auth) so the tests drive them with the bare
///     <see cref="TestBase" /> MockServer — no token plumbing, no client mocks.
/// </summary>
public class CustomAppGenerationToolsTests : TestBase
{
    // ----- get_custom_app_template_manifest -----

    [Fact]
    public async Task GetCustomAppTemplateManifest_ReturnsCanonicalFiles()
    {
        var result = await CustomAppGenerationTools.GetCustomAppTemplateManifest(MockServer.Object);

        result.IsSuccess.Should().BeTrue();
        result.Files.Should().NotBeEmpty();
        result.TemplateRef.Should().NotBeNullOrWhiteSpace();
        result.Files.Should().Contain(f => f.Path == "src/custom-app/src/app/app.routes.ts");
        result.Files.Should().Contain(f => f.Path == "src/custom-app/src/app/services/my-command-settings.service.ts");
    }

    [Fact]
    public async Task GetCustomAppTemplateManifest_MarksLockfileNonEditable()
    {
        // package-lock.json must not be hand-edited by the agent — the manifest is the
        // signal that prevents that.
        var result = await CustomAppGenerationTools.GetCustomAppTemplateManifest(MockServer.Object);

        var lockfile = result.Files.FirstOrDefault(f => f.Path == "src/custom-app/package-lock.json");
        lockfile.Should().NotBeNull();
        lockfile!.Editable.Should().BeFalse();
    }

    // ----- list_kendo_components -----

    [Fact]
    public async Task ListKendoComponents_NoCategory_ReturnsFullCatalog()
    {
        var result = await CustomAppGenerationTools.ListKendoComponents(MockServer.Object);

        result.IsSuccess.Should().BeTrue();
        result.Components.Should().NotBeEmpty();
        result.KendoVersion.Should().NotBeNullOrWhiteSpace();
        // Sanity-check a couple of mandatory entries.
        result.Components.Should().Contain(c => c.Name == "Grid" && c.NpmPackage == "@progress/kendo-angular-grid");
        result.Components.Should().Contain(c => c.Name == "AppBar");
    }

    [Fact]
    public async Task ListKendoComponents_WithCategory_FiltersCaseInsensitively()
    {
        var result = await CustomAppGenerationTools.ListKendoComponents(MockServer.Object, category: "LAYOUT");

        result.IsSuccess.Should().BeTrue();
        result.Components.Should().NotBeEmpty();
        result.Components.Should().OnlyContain(c => string.Equals(c.Category, "Layout", System.StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ListKendoComponents_UnknownCategory_ReturnsEmptyButSuccess()
    {
        // Unknown category isn't an error — it just yields zero results. The AI can
        // re-issue with a different filter without seeing IsSuccess=false.
        var result = await CustomAppGenerationTools.ListKendoComponents(MockServer.Object, category: "DoesNotExist");

        result.IsSuccess.Should().BeTrue();
        result.Components.Should().BeEmpty();
    }

    // ----- plan_custom_app_scaffold -----

    [Fact]
    public async Task PlanCustomAppScaffold_HappyPath_DerivesSlugClassAndPaths()
    {
        var result = await CustomAppGenerationTools.PlanCustomAppScaffold(
            MockServer.Object,
            appSlug: "Energy Portal",
            drawerItems: "User List, Asset Table");

        result.IsSuccess.Should().BeTrue();
        result.AppSlug.Should().Be("energy-portal");
        result.Pages.Should().HaveCount(2);
        result.Pages[0].RouteSlug.Should().Be("user-list");
        result.Pages[0].ClassName.Should().Be("UserList");
        result.Pages[0].TsPath.Should().Be("src/custom-app/src/app/pages/user-list/user-list.ts");
        result.Pages[1].RouteSlug.Should().Be("asset-table");
        result.Pages[1].ClassName.Should().Be("AssetTable");

        result.DrawerItems.Should().HaveCount(2);
        result.DrawerItems[0].Id.Should().Be("user-list");
        // The icon picker uses keyword matching — verify the "user" branch fires.
        result.DrawerItems[0].SvgIcon.Should().Be("userIcon");

        result.Routes.Should().HaveCount(2);
        result.Routes[0].Path.Should().Be("user-list");
        result.Routes[0].TitleKey.Should().Be("PAGES.USER_LIST.TITLE");
    }

    [Fact]
    public async Task PlanCustomAppScaffold_SplitsOnNewlinesAndCommas()
    {
        // Bastion CLI / Refinery Studio may pass either delimiter; the parser supports
        // both so the agent doesn't have to normalise.
        var result = await CustomAppGenerationTools.PlanCustomAppScaffold(
            MockServer.Object,
            appSlug: "demo",
            drawerItems: "Dashboard\nUser List\nReports");

        result.IsSuccess.Should().BeTrue();
        result.Pages.Should().HaveCount(3);
        result.Pages.Select(p => p.RouteSlug).Should().Equal("dashboard", "user-list", "reports");
    }

    [Fact]
    public async Task PlanCustomAppScaffold_MissingAppSlug_ReturnsError()
    {
        var result = await CustomAppGenerationTools.PlanCustomAppScaffold(
            MockServer.Object,
            appSlug: "",
            drawerItems: "Page");

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("appSlug");
    }

    [Fact]
    public async Task PlanCustomAppScaffold_MissingDrawerItems_ReturnsError()
    {
        var result = await CustomAppGenerationTools.PlanCustomAppScaffold(
            MockServer.Object,
            appSlug: "demo",
            drawerItems: "");

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("drawerItems");
    }

    [Fact]
    public async Task PlanCustomAppScaffold_EmptyAfterSplit_ReturnsError()
    {
        // Commas + whitespace only — no real label survives the split. Must surface as
        // an actionable error rather than producing an empty plan.
        var result = await CustomAppGenerationTools.PlanCustomAppScaffold(
            MockServer.Object,
            appSlug: "demo",
            drawerItems: ",,, ");

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("at least one");
    }

    // ===== create_tenant_app_repo (#4146) =========================================
    // The tool delegates to IGitHubRepoApiClient. Tests inject a stub client into the
    // server's IServiceProvider and assert the tool maps each Outcome onto the right
    // response shape. The HTTP plumbing in GitHubRepoApiClient itself is covered by
    // a dedicated test in GitHubRepoApiClientTests.

    [Fact]
    public async Task CreateTenantAppRepo_HappyPath_ReturnsUrlsAndMetadata()
    {
        var stub = new StubGitHubRepoApiClient
        {
            Result = new GitHubRepoCreateResult
            {
                Outcome = GitHubRepoCreateOutcome.Created,
                Repo = new GitHubRepoInfo
                {
                    Owner = "meshmakers",
                    FullName = "meshmakers/customer-list",
                    CloneUrl = "https://github.com/meshmakers/customer-list.git",
                    SshUrl = "git@github.com:meshmakers/customer-list.git",
                    DefaultBranch = "main",
                    RepoId = 1234567,
                },
            }
        };
        TestServiceProvider.RegisterService<IGitHubRepoApiClient>(stub);

        var result = await CustomAppGenerationTools.CreateTenantAppRepo(
            MockServer.Object,
            name: "customer-list",
            accessToken: "ghp_fake",
            description: "A list view",
            isPrivate: true,
            org: "meshmakers");

        result.IsSuccess.Should().BeTrue();
        result.IsConflict.Should().BeFalse();
        result.Owner.Should().Be("meshmakers");
        result.FullName.Should().Be("meshmakers/customer-list");
        result.CloneUrl.Should().Be("https://github.com/meshmakers/customer-list.git");
        result.SshUrl.Should().Be("git@github.com:meshmakers/customer-list.git");
        result.DefaultBranch.Should().Be("main");
        result.RepoId.Should().Be(1234567);
        stub.LastCall.Should().NotBeNull();
        stub.LastCall!.Name.Should().Be("customer-list");
        stub.LastCall.Org.Should().Be("meshmakers");
        stub.LastCall.IsPrivate.Should().BeTrue();
        // PAT must reach the client unchanged — no manipulation in the tool layer.
        stub.LastCall.AccessToken.Should().Be("ghp_fake");
    }

    [Fact]
    public async Task CreateTenantAppRepo_NoAccessToken_RefusesBeforeApiCall()
    {
        // The tool refuses an empty PAT pre-call. Reaching the API with an empty bearer
        // would surface as a 401 — friendlier to return the actionable error here.
        var stub = new StubGitHubRepoApiClient();
        TestServiceProvider.RegisterService<IGitHubRepoApiClient>(stub);

        var result = await CustomAppGenerationTools.CreateTenantAppRepo(
            MockServer.Object,
            name: "customer-list",
            accessToken: "");

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("accessToken");
        result.ErrorMessage.Should().Contain("GH_TOKEN", "the error must point the agent at the env var convention");
        stub.LastCall.Should().BeNull("the client must not be called when validation fails");
    }

    [Fact]
    public async Task CreateTenantAppRepo_EmptyName_RefusesBeforeApiCall()
    {
        var stub = new StubGitHubRepoApiClient();
        TestServiceProvider.RegisterService<IGitHubRepoApiClient>(stub);

        var result = await CustomAppGenerationTools.CreateTenantAppRepo(
            MockServer.Object,
            name: " ",
            accessToken: "ghp_fake");

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("name");
        stub.LastCall.Should().BeNull();
    }

    [Theory]
    [InlineData("foo/bar")]
    [InlineData("with space")]
    public async Task CreateTenantAppRepo_NameWithInvalidChars_RefusesBeforeApiCall(string badName)
    {
        var stub = new StubGitHubRepoApiClient();
        TestServiceProvider.RegisterService<IGitHubRepoApiClient>(stub);

        var result = await CustomAppGenerationTools.CreateTenantAppRepo(
            MockServer.Object,
            name: badName,
            accessToken: "ghp_fake");

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Invalid repo name");
        stub.LastCall.Should().BeNull("pre-validation must short-circuit the API call");
    }

    [Fact]
    public async Task CreateTenantAppRepo_NameCollision_ReturnsIsConflictWithExistingUrls()
    {
        var stub = new StubGitHubRepoApiClient
        {
            Result = new GitHubRepoCreateResult
            {
                Outcome = GitHubRepoCreateOutcome.Conflict,
                ErrorMessage = "Repo meshmakers/customer-list already exists. Reuse it or pick a different name.",
                Repo = new GitHubRepoInfo
                {
                    Owner = "meshmakers",
                    FullName = "meshmakers/customer-list",
                    CloneUrl = "https://github.com/meshmakers/customer-list.git",
                    SshUrl = "git@github.com:meshmakers/customer-list.git",
                    DefaultBranch = "main",
                    RepoId = 999,
                },
            }
        };
        TestServiceProvider.RegisterService<IGitHubRepoApiClient>(stub);

        var result = await CustomAppGenerationTools.CreateTenantAppRepo(
            MockServer.Object,
            name: "customer-list",
            accessToken: "ghp_fake",
            org: "meshmakers");

        result.IsSuccess.Should().BeFalse();
        result.IsConflict.Should().BeTrue("the conflict shape lets the agent reuse without a second round-trip");
        result.CloneUrl.Should().Be("https://github.com/meshmakers/customer-list.git",
            "the existing repo's URL is the actionable bit");
        result.FullName.Should().Be("meshmakers/customer-list");
        result.ErrorMessage.Should().Contain("already exists");
    }

    [Fact]
    public async Task CreateTenantAppRepo_PatExpired_ReturnsUnauthorisedErrorWithoutIsConflict()
    {
        var stub = new StubGitHubRepoApiClient
        {
            Result = new GitHubRepoCreateResult
            {
                Outcome = GitHubRepoCreateOutcome.Unauthorised,
                ErrorMessage = "GitHub rejected the PAT (401). Rotate the tenant binding.",
            }
        };
        TestServiceProvider.RegisterService<IGitHubRepoApiClient>(stub);

        var result = await CustomAppGenerationTools.CreateTenantAppRepo(
            MockServer.Object,
            name: "customer-list",
            accessToken: "ghp_expired");

        result.IsSuccess.Should().BeFalse();
        result.IsConflict.Should().BeFalse("a 401 is not a name collision; mixing them would hide the rotation signal");
        result.ErrorMessage.Should().Contain("PAT");
    }

    [Fact]
    public async Task CreateTenantAppRepo_NoOrg_CallsClientWithNullOrg()
    {
        var stub = new StubGitHubRepoApiClient
        {
            Result = new GitHubRepoCreateResult
            {
                Outcome = GitHubRepoCreateOutcome.Created,
                Repo = new GitHubRepoInfo
                {
                    Owner = "gerald",
                    FullName = "gerald/personal-app",
                    CloneUrl = "https://github.com/gerald/personal-app.git",
                    SshUrl = "git@github.com:gerald/personal-app.git",
                    DefaultBranch = "main",
                    RepoId = 42,
                },
            }
        };
        TestServiceProvider.RegisterService<IGitHubRepoApiClient>(stub);

        await CustomAppGenerationTools.CreateTenantAppRepo(
            MockServer.Object,
            name: "personal-app",
            accessToken: "ghp_fake");

        stub.LastCall!.Org.Should().BeNull("omitting org routes to POST /user/repos under the PAT-owner's account");
        stub.LastCall.IsPrivate.Should().BeTrue("private is the default — tenant code is sensitive by default");
    }

    // ===== apply_custom_app_scaffold (M3 B-2c-2) ===================================
    // Pure-logic expansion of a plan into WriteOps + EditOps + NextSteps. The MCP
    // server can't write to the agent's workspace directly; these tests pin that the
    // returned ops contain the canonical content shape the agent then applies via its
    // built-in Write/Edit tools.

    [Fact]
    public async Task ApplyCustomAppScaffold_HappyPath_EmitsSevenWriteOpsPerPage()
    {
        var plan = await CustomAppGenerationTools.PlanCustomAppScaffold(
            MockServer.Object, appSlug: "demo", drawerItems: "Audit Log");

        var binding = new ApplyScaffoldTypeBinding
        {
            TypeId = "System.Ai-3/AiAuditEvent",
            GraphqlOperationName = "systemAiAuditEvent",
            Attributes = new List<ApplyScaffoldAttribute>
            {
                new() { Name = "at", TsType = "string", IsOptional = false },
                new() { Name = "eventType", TsType = "string", IsOptional = false },
                new() { Name = "detail", TsType = "string", IsOptional = true },
            },
        };

        var result = await CustomAppGenerationTools.ApplyCustomAppScaffold(
            MockServer.Object,
            plan,
            new Dictionary<string, ApplyScaffoldTypeBinding> { ["audit-log"] = binding });

        result.IsSuccess.Should().BeTrue();
        result.WriteOps.Should().HaveCount(7, "one page → page.ts + page.html + page.scss + service + spec + dto + graphql");
        result.WriteOps.Select(w => w.Purpose).Should().BeEquivalentTo(
            "page-component", "page-template", "page-styles", "service",
            "service-spec", "dto", "graphql-query");
        result.EditOps.Should().HaveCount(2, "one route registration + one drawer item");
        result.EditOps.Select(e => e.Purpose).Should().Contain(new[] { "route-registration", "drawer-item" });
    }

    [Fact]
    public async Task ApplyCustomAppScaffold_BindingAttributes_LandInDtoAndGraphqlAndMapper()
    {
        var plan = await CustomAppGenerationTools.PlanCustomAppScaffold(
            MockServer.Object, appSlug: "demo", drawerItems: "Audit Log");

        var binding = new ApplyScaffoldTypeBinding
        {
            TypeId = "System.Ai-3/AiAuditEvent",
            GraphqlOperationName = "systemAiAuditEvent",
            Attributes = new List<ApplyScaffoldAttribute>
            {
                new() { Name = "at", TsType = "string", IsOptional = false },
                new() { Name = "detail", TsType = "string", IsOptional = true },
            },
        };

        var result = await CustomAppGenerationTools.ApplyCustomAppScaffold(
            MockServer.Object,
            plan,
            new Dictionary<string, ApplyScaffoldTypeBinding> { ["audit-log"] = binding });

        var dto = result.WriteOps.Single(w => w.Purpose == "dto");
        dto.Content.Should().Contain("at: string;");
        dto.Content.Should().Contain("detail?: string | null;",
            "optional attributes carry the | null tail");

        var gql = result.WriteOps.Single(w => w.Purpose == "graphql-query");
        gql.Content.Should().Contain("systemAiAuditEvent(first: $first)");
        gql.Content.Should().Contain("at");
        gql.Content.Should().Contain("detail");

        var svc = result.WriteOps.Single(w => w.Purpose == "service");
        svc.Content.Should().Contain("at: node.at,");
        svc.Content.Should().Contain("detail: node.detail ?? null,",
            "optional attributes get the ?? null coalesce in the mapper");
    }

    [Fact]
    public async Task ApplyCustomAppScaffold_NoTypeBinding_EmitsTodoStubs()
    {
        var plan = await CustomAppGenerationTools.PlanCustomAppScaffold(
            MockServer.Object, appSlug: "demo", drawerItems: "Mystery Page");

        var result = await CustomAppGenerationTools.ApplyCustomAppScaffold(
            MockServer.Object, plan);

        result.IsSuccess.Should().BeTrue();
        var dto = result.WriteOps.Single(w => w.Purpose == "dto");
        dto.Content.Should().Contain("// TODO: list the fields");
        var gql = result.WriteOps.Single(w => w.Purpose == "graphql-query");
        gql.Content.Should().Contain("# TODO: list the attributes");

        result.NextSteps.Should().Contain(s => s.Contains("mystery-page") && s.Contains("no type binding"));
    }

    [Fact]
    public async Task ApplyCustomAppScaffold_MultiplePages_BundlesRouteAndDrawerEdits()
    {
        // Three pages → three route entries land in ONE EditOp (bundled before the
        // wildcard anchor). Same for drawer entries. The agent applies two single
        // EditOps rather than 3 + 3 separate ones; that's the whole point of bundling.
        var plan = await CustomAppGenerationTools.PlanCustomAppScaffold(
            MockServer.Object, appSlug: "demo",
            drawerItems: "Audit Log, Asset Table, Settings");

        var result = await CustomAppGenerationTools.ApplyCustomAppScaffold(
            MockServer.Object, plan);

        result.IsSuccess.Should().BeTrue();
        result.WriteOps.Should().HaveCount(21, "7 per page × 3 pages");
        result.EditOps.Should().HaveCount(2);

        var route = result.EditOps.Single(e => e.Purpose == "route-registration");
        route.NewString.Should().Contain("'audit-log'");
        route.NewString.Should().Contain("'asset-table'");
        route.NewString.Should().Contain("'settings'");

        var drawer = result.EditOps.Single(e => e.Purpose == "drawer-item");
        drawer.NewString.Should().Contain("id: 'audit-log'");
        drawer.NewString.Should().Contain("id: 'asset-table'");
        drawer.NewString.Should().Contain("id: 'settings'");
    }

    [Fact]
    public async Task ApplyCustomAppScaffold_RouteEditAnchor_PreservesWildcardSuffix()
    {
        // The route EditOp's NewString MUST end with the same anchor it replaces — the
        // OldString of '    ],\n  },\n  {\n    path: \'**\',' must be present at the end
        // of NewString or the Edit becomes a delete-and-truncate.
        var plan = await CustomAppGenerationTools.PlanCustomAppScaffold(
            MockServer.Object, appSlug: "demo", drawerItems: "Audit Log");

        var result = await CustomAppGenerationTools.ApplyCustomAppScaffold(
            MockServer.Object, plan);

        var route = result.EditOps.Single(e => e.Purpose == "route-registration");
        route.OldString.Should().Be("    ],\n  },\n  {\n    path: '**',");
        route.NewString.Should().EndWith(route.OldString);
    }

    [Fact]
    public async Task ApplyCustomAppScaffold_NullPlan_ReturnsValidationError()
    {
        var result = await CustomAppGenerationTools.ApplyCustomAppScaffold(
            MockServer.Object, plan: null!);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("plan");
    }

    [Fact]
    public async Task ApplyCustomAppScaffold_PlanWithIsSuccessFalse_Refuses()
    {
        var badPlan = new CustomAppScaffoldPlanResponse { IsSuccess = false, ErrorMessage = "x" };

        var result = await CustomAppGenerationTools.ApplyCustomAppScaffold(
            MockServer.Object, badPlan);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("IsSuccess");
    }

    [Fact]
    public async Task ApplyCustomAppScaffold_EmptyPagesList_Refuses()
    {
        var emptyPlan = new CustomAppScaffoldPlanResponse { IsSuccess = true, AppSlug = "demo" };

        var result = await CustomAppGenerationTools.ApplyCustomAppScaffold(
            MockServer.Object, emptyPlan);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Page");
    }

    [Fact]
    public async Task ApplyCustomAppScaffold_CustomPaths_PropagateToEditOps()
    {
        // Operators with a non-default workspace layout pass custom file paths; the
        // EditOps must carry those paths verbatim so the agent's Edit lands on the
        // right file.
        var plan = await CustomAppGenerationTools.PlanCustomAppScaffold(
            MockServer.Object, appSlug: "demo", drawerItems: "Audit Log");

        var result = await CustomAppGenerationTools.ApplyCustomAppScaffold(
            MockServer.Object, plan, typeBindings: null,
            appRoutesPath: "custom/path/app.routes.ts",
            commandSettingsPath: "custom/path/cmd.service.ts");

        result.IsSuccess.Should().BeTrue();
        var route = result.EditOps.Single(e => e.Purpose == "route-registration");
        route.Path.Should().Be("custom/path/app.routes.ts");
        var drawer = result.EditOps.Single(e => e.Purpose == "drawer-item");
        drawer.Path.Should().Be("custom/path/cmd.service.ts");
    }

    [Fact]
    public async Task ApplyCustomAppScaffold_NextSteps_PromptsCodegenAndBuild()
    {
        var plan = await CustomAppGenerationTools.PlanCustomAppScaffold(
            MockServer.Object, appSlug: "demo", drawerItems: "Audit Log");

        var result = await CustomAppGenerationTools.ApplyCustomAppScaffold(
            MockServer.Object, plan);

        result.NextSteps.Should().Contain(s => s.Contains("npm run codegen"));
        result.NextSteps.Should().Contain(s => s.Contains("npm run build:prod"));
    }

    private sealed class StubGitHubRepoApiClient : IGitHubRepoApiClient
    {
        public GitHubRepoCreateResult Result { get; set; } = new()
        {
            Outcome = GitHubRepoCreateOutcome.UnexpectedError,
            ErrorMessage = "Test stub was not configured."
        };

        public CallRecord? LastCall { get; private set; }

        public Task<GitHubRepoCreateResult> CreateAsync(
            string accessToken, string name, string? description, bool isPrivate, string? org,
            CancellationToken cancellationToken = default)
        {
            LastCall = new CallRecord(accessToken, name, description, isPrivate, org);
            return Task.FromResult(Result);
        }

        public sealed record CallRecord(string AccessToken, string Name, string? Description, bool IsPrivate, string? Org);
    }
}
