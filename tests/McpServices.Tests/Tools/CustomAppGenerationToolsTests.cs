using FluentAssertions;
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
}
