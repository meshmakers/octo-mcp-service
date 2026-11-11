using FluentAssertions;
using Meshmakers.Octo.Backend.McpServices.ContentGuard;
using Meshmakers.Octo.Backend.McpServices.Models;
using Meshmakers.Octo.Backend.McpServices.Services.Knowledge;
using Meshmakers.Octo.Backend.McpServices.Tools;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Engine.Repositories.Query;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace McpServices.Tests.Tools;

/// <summary>
///     AB#8780: platform updates of Blueprint knowledge pages. Fixtures of the story: unedited (switched), edited (kept,
///     PlatformUpdateAvailable), deleted (nothing), link change (LinksTo follows the declared links), already current
///     (nothing, idempotent); plus adopt by restore, dismiss, the run reference and the Verified rule of AB#8565. The
///     repository is in memory and behaves like the engine for what the tools use; a blueprint update is simulated by
///     inserting seeded version entities (author <c>blueprint:…</c>, <c>rtBlueprintSource</c> stamp) the way the engine
///     inserts them (AB#8778: new versions arrive, the page is not refreshed).
/// </summary>
public class KnowledgePlatformUpdateToolsTests : TestBase
{
    private const string RunRef = "knowledge-platform-updates/2026-11-12T02:15:00Z";

    private readonly Dictionary<OctoObjectId, RtEntity> _entities = new();
    private readonly List<(RtEntityId Origin, RtEntityId Target, string Role)> _associations = [];
    private readonly List<IEntityUpdateInfo<RtEntity>> _writes = [];
    private DateTime _clock = new(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);
    private bool _modelHasPlatformUpdates = true;

    public KnowledgePlatformUpdateToolsTests()
    {
        TestServiceProvider.RegisterService<IContentGuard>(new ContentGuard(NullLogger<ContentGuard>.Instance));
        MockCkCacheService.Setup(c => c.GetCkTypes(It.IsAny<string>())).Returns([]);
        MockCkCacheService
            .Setup(c => c.TryGetRtCkType(It.IsAny<string>(), It.IsAny<RtCkId<CkTypeId>>(), out It.Ref<CkTypeGraph?>.IsAny))
            .Returns(new TryGetType((string _, RtCkId<CkTypeId> _, out CkTypeGraph? graph) =>
            {
                graph = _modelHasPlatformUpdates
                    ? CkGraphTestBuilder.BuildType("System.Ai-4.10.0/KnowledgePage-1",
                        ("DismissedBlueprintVersion", AttributeValueTypesDto.Int, null))
                    : CkGraphTestBuilder.BuildType("System.Ai-4.9.0/KnowledgePage-1");
                return true;
            }));
        MockTenantRepository
            .Setup(r => r.GetRtEntitiesByTypeAsync(It.IsAny<IOctoSession>(), It.IsAny<RtCkId<CkTypeId>>(),
                It.IsAny<RtEntityQueryOptions>(), It.IsAny<int?>(), It.IsAny<int?>()))
            .ReturnsAsync((IOctoSession _, RtCkId<CkTypeId> type, RtEntityQueryOptions _, int? _, int? _) =>
            {
                // Field filters are not applied here on purpose: the tools filter the result again themselves.
                var items = _entities.Values.Where(e => Equals(e.CkTypeId, type)).ToList();
                return new ResultSet<RtEntity>(items, items.Count, null, null);
            });
        MockTenantRepository
            .Setup(r => r.GetRtEntityByRtIdAsync(It.IsAny<IOctoSession>(), It.IsAny<RtEntityId>()))
            .ReturnsAsync((IOctoSession _, RtEntityId id) =>
                _entities.TryGetValue(id.RtId, out var e) && Equals(e.CkTypeId, id.CkTypeId) ? e : null);
        MockTenantRepository
            .Setup(r => r.GetRtAssociationsAsync(It.IsAny<IOctoSession>(), It.IsAny<RtEntityId>(),
                It.IsAny<RtAssociationExtendedQueryOptions>()))
            .ReturnsAsync((IOctoSession _, RtEntityId origin, RtAssociationExtendedQueryOptions o) =>
            {
                var items = _associations.Where(a => a.Origin.RtId == origin.RtId && a.Role == o.RoleId!.ToString())
                    .Select(a => new RtAssociation { OriginRtId = a.Origin.RtId, TargetRtId = a.Target.RtId }).ToList();
                return new ResultSet<RtAssociation>(items, items.Count, null, null);
            });
        MockTenantRepository
            .Setup(r => r.ApplyChangesAsync(It.IsAny<IOctoSession>(), It.IsAny<IReadOnlyList<IEntityUpdateInfo<RtEntity>>>(),
                It.IsAny<IReadOnlyList<AssociationUpdateInfo>>(), It.IsAny<OperationResult>()))
            .Returns((IOctoSession _, IReadOnlyList<IEntityUpdateInfo<RtEntity>> updates,
                IReadOnlyList<AssociationUpdateInfo> associations, OperationResult _) =>
            {
                _writes.AddRange(updates);
                foreach (var update in updates)
                {
                    var entity = update.RtEntity!;
                    if (update.ModOption == EntityModOptions.Insert || !_entities.TryGetValue(entity.RtId, out var stored))
                    {
                        entity.RtCreationDateTime ??= Tick();
                        _entities[entity.RtId] = entity;
                        continue;
                    }

                    foreach (var attribute in entity.Attributes)
                    {
                        stored.SetAttributeRawValue(attribute.Key, attribute.Value);
                    }
                }

                foreach (var a in associations)
                {
                    if (a.ModOption == AssociationModOptionsDto.Delete)
                    {
                        _associations.RemoveAll(x => x.Origin.RtId == a.Origin.RtId && x.Target.RtId == a.Target.RtId &&
                                                     x.Role == a.RoleId.ToString());
                    }
                    else
                    {
                        _associations.Add((a.Origin, a.Target, a.RoleId.ToString()));
                    }
                }

                return Task.CompletedTask;
            });
    }

    private delegate bool TryGetType(string tenantId, RtCkId<CkTypeId> ckTypeId, out CkTypeGraph? graph);

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    private DateTime Tick() => _clock = _clock.AddMinutes(1);

    // ---- fixtures ----

    private static string Blueprint(int n) => $"System.Ai.Builder-1.{n}.0";

    private static KnowledgeContent Content(string title, int status = KnowledgePages.StatusVerified) =>
        new(title, 4, $"Summary of {title}.", [new KnowledgeSectionDto { Heading = "Steps", SectionContent = $"Text of {title}." }],
            [], [], status);

    /// <summary>A version as a blueprint seeds it (or, with <paramref name="blueprint" /> false, as the tenant wrote it).</summary>
    private RtEntity SeedVersion(RtEntity page, int number, string title, bool blueprint = true,
        int status = KnowledgePages.StatusVerified, string[]? declaredLinks = null, bool stamped = true)
    {
        var slug = page.RtWellKnownName!;
        var version = new RtEntity
        {
            RtId = OctoObjectId.GenerateNewId(),
            CkTypeId = KnowledgePages.VersionTypeId,
            RtWellKnownName = KnowledgePages.VersionName(slug, number),
            RtCreationDateTime = Tick()
        };
        KnowledgePages.SetContent(version, Content(title, status));
        version.SetAttributeValue("VersionNumber", AttributeValueTypesDto.Int, number);
        version.SetAttributeValue("Author", AttributeValueTypesDto.String, blueprint ? "blueprint:" + Blueprint(number) : "user:alice");
        version.SetAttributeValue("AuthoredAt", AttributeValueTypesDto.DateTime, _clock.AddDays(-3));
        if (blueprint && stamped)
        {
            version.SetAttributeValue("RtBlueprintSource", AttributeValueTypesDto.String, Blueprint(number));
        }

        if (declaredLinks != null)
        {
            version.SetAttributeValue("DeclaredLinks", AttributeValueTypesDto.StringArray, declaredLinks.ToList());
        }

        _entities[version.RtId] = version;
        _associations.Add((new RtEntityId(KnowledgePages.VersionTypeId, version.RtId),
            new RtEntityId(KnowledgePages.PageTypeId, page.RtId), KnowledgePages.VersionOfRole));
        return version;
    }

    /// <summary>A Blueprint page at version 1 as the first install seeds it (page + version 1).</summary>
    private RtEntity SeedPage(string slug, string title = "Build sequence", string[]? links = null)
    {
        var page = new RtEntity { RtId = OctoObjectId.GenerateNewId(), CkTypeId = KnowledgePages.PageTypeId, RtWellKnownName = slug };
        KnowledgePages.SetContent(page, Content(title));
        page.SetAttributeValue("Origin", AttributeValueTypesDto.Enum, 3);
        page.SetAttributeValue("CurrentVersion", AttributeValueTypesDto.Int, 1);
        page.SetAttributeValue("BlueprintVersion", AttributeValueTypesDto.String, Blueprint(1));
        page.RtCreationDateTime = Tick();
        _entities[page.RtId] = page;
        SeedVersion(page, 1, title);
        foreach (var target in links ?? [])
        {
            Link(page, Page(target));
        }

        return page;
    }

    private RtEntity Page(string slug) => _entities.Values.Single(e =>
        Equals(e.CkTypeId, KnowledgePages.PageTypeId) && e.RtWellKnownName == slug);

    private void Link(RtEntity from, RtEntity to) => _associations.Add((new RtEntityId(KnowledgePages.PageTypeId, from.RtId),
        new RtEntityId(KnowledgePages.PageTypeId, to.RtId), KnowledgePages.LinksToRole));

    private IEnumerable<string> LinksOf(RtEntity page) => _associations
        .Where(a => a.Origin.RtId == page.RtId && a.Role == KnowledgePages.LinksToRole)
        .Select(a => _entities[a.Target.RtId].RtWellKnownName!).OrderBy(s => s, StringComparer.Ordinal);

    /// <summary>A tenant edit of the page through the knowledge tools (writes a new, non-Blueprint version).</summary>
    private async Task EditAsync(string slug, string text = "Our own steps.")
    {
        var result = await KnowledgeTools.KnowledgeUpsertPage(MockServer.Object, slug, "Build sequence (ours)",
            [new KnowledgeSectionDto { Heading = "Steps", SectionContent = text }], cancellationToken: Ct);
        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
    }

    private Task<KnowledgeAdoptPlatformUpdatesResponse> AdoptAsync(string runRef = RunRef) =>
        KnowledgePlatformUpdateTools.KnowledgeAdoptPlatformUpdates(MockServer.Object, runRef, cancellationToken: Ct);

    private Task<KnowledgePlatformUpdatesResponse> ListAsync() =>
        KnowledgePlatformUpdateTools.KnowledgeListPlatformUpdates(MockServer.Object);

    private static int Current(RtEntity page) => KnowledgePages.CurrentVersion(page);

    // ---- AC: unedited page switched ----

    [Fact]
    public async Task Unedited_IsSwitchedToTheNewestBlueprintVersion_WithItsFields_AndNoNewVersion()
    {
        var page = SeedPage("app-build-sequence");
        SeedVersion(page, 2, "Build sequence v2");
        SeedVersion(page, 3, "Build sequence v3");
        var versionsBefore = _entities.Values.Count(e => Equals(e.CkTypeId, KnowledgePages.VersionTypeId));

        var result = await AdoptAsync();

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        result.Adopted.Should().ContainSingle(a => a.Slug == "app-build-sequence" && a.FromVersion == 1 && a.ToVersion == 3 &&
                                                   a.Blueprint == Blueprint(3));
        Current(page).Should().Be(3);
        var shown = KnowledgePages.ReadContent(page);
        var v3 = KnowledgePages.ReadContent(_entities.Values.Single(e => e.RtWellKnownName == "app-build-sequence@3"));
        shown.Title.Should().Be(v3.Title);
        shown.Summary.Should().Be(v3.Summary);
        shown.Sections.Select(s => s.SectionContent).Should().Equal(v3.Sections.Select(s => s.SectionContent));
        shown.Citations.Should().BeEquivalentTo(v3.Citations);
        page.GetAttributeStringValueOrDefault("BlueprintVersion").Should().Be(Blueprint(3));
        _entities.Values.Count(e => Equals(e.CkTypeId, KnowledgePages.VersionTypeId)).Should().Be(versionsBefore,
            "an adopt points the page at the seeded version; it writes no copy");
    }

    [Fact]
    public async Task Unedited_KeepsTheVerifiedStatusTheBlueprintVersionBrings_AndADraftOneStaysDraft()
    {
        var verified = SeedPage("verified-page");
        SeedVersion(verified, 2, "Verified v2");
        var draft = SeedPage("draft-page");
        SeedVersion(draft, 2, "Draft v2", status: KnowledgePages.StatusDraft);

        (await AdoptAsync()).Adopted.Should().HaveCount(2);

        KnowledgePages.ReadContent(verified).Status.Should().Be(KnowledgePages.StatusVerified);
        KnowledgePages.ReadContent(draft).Status.Should().Be(KnowledgePages.StatusDraft,
            "the status comes from the version, the tool chooses none");
    }

    // ---- AC: edited page kept + PlatformUpdateAvailable ----

    [Fact]
    public async Task Edited_KeepsItsVersionAndContent_AndIsListedAsAvailable()
    {
        SeedPage("app-build-sequence");
        await EditAsync("app-build-sequence");
        var page = Page("app-build-sequence");
        var before = KnowledgePages.ReadContent(page);
        SeedVersion(page, 3, "Build sequence v3");

        var adopt = await AdoptAsync();
        var list = await ListAsync();

        adopt.Adopted.Should().BeEmpty();
        Current(page).Should().Be(2);
        KnowledgePages.ReadContent(page).Should().BeEquivalentTo(before);
        KnowledgePages.ReadContent(page).Status.Should().Be(KnowledgePages.StatusDraft, "never Verified on an edited page");
        var available = list.Updates.Should().ContainSingle().Subject;
        available.Should().BeEquivalentTo(new KnowledgePlatformUpdateDto
        {
            Slug = "app-build-sequence", State = "Available", CurrentVersion = 2, NewerVersion = 3, Blueprint = Blueprint(3)
        });
        adopt.Available.Should().ContainSingle(a => a.Slug == "app-build-sequence");
    }

    [Fact]
    public async Task Edited_ABlueprintVersionThatArrivedAfterTheEdit_IsAvailable_EvenWithALowerNumber()
    {
        // Version 2 of the blueprint arrives after the tenant already wrote its own versions 2 and 3.
        SeedPage("page-a");
        await EditAsync("page-a", "edit one");
        await EditAsync("page-a", "edit two");
        var page = Page("page-a");
        Current(page).Should().Be(3);
        SeedVersion(page, 4, "v4 from the blueprint");

        (await ListAsync()).Updates.Should().ContainSingle(u => u.State == "Available" && u.NewerVersion == 4);
    }

    [Fact]
    public async Task Edited_AfterTheUpdateArrived_WithAHigherNumber_IsNotListed()
    {
        var page = SeedPage("page-a");
        SeedVersion(page, 2, "v2 from the blueprint");
        await EditAsync("page-a");

        Current(page).Should().Be(3, "the edit takes the next free number, not the number of the seeded version 2");
        (await ListAsync()).Updates.Should().BeEmpty("the edit was written after version 2 arrived (it is in the history)");
    }

    [Fact]
    public async Task ATenantVersionWithTheNumberOfABlueprintVersion_CountsAsEdited_AndIsNeverSwitched()
    {
        var page = SeedPage("page-a");
        SeedVersion(page, 2, "tenant text", blueprint: false, status: KnowledgePages.StatusDraft);
        page.SetAttributeValue("CurrentVersion", AttributeValueTypesDto.Int, 2);
        SeedVersion(page, 2, "seeded text with the same number");
        SeedVersion(page, 3, "v3");

        var result = await AdoptAsync();

        result.Adopted.Should().BeEmpty();
        Current(page).Should().Be(2);
        result.Available.Should().ContainSingle(a => a.NewerVersion == 3);
    }

    [Fact]
    public async Task AVersionWithABlueprintAuthorButWithoutTheEngineStamp_IsNoBlueprintVersion()
    {
        var page = SeedPage("page-a");
        SeedVersion(page, 2, "forged", stamped: false);

        (await AdoptAsync()).Adopted.Should().BeEmpty();
        Current(page).Should().Be(1);
    }

    // ---- AC: adopt (restore) and dismiss ----

    [Fact]
    public async Task Adopt_RestoringTheNewerVersion_RemovesTheFinding()
    {
        SeedPage("page-a");
        await EditAsync("page-a");
        var page = Page("page-a");
        SeedVersion(page, 3, "v3");
        (await ListAsync()).Updates.Should().ContainSingle();

        var restore = await KnowledgeTools.KnowledgeRestoreVersion(MockServer.Object, "page-a", 3, cancellationToken: Ct);

        restore.IsSuccess.Should().BeTrue(restore.ErrorMessage);
        Current(page).Should().Be(4, "the restore is a new version with the next free number");
        KnowledgePages.ReadContent(page).Title.Should().Be("v3");
        KnowledgePages.ReadContent(page).Status.Should().Be(KnowledgePages.StatusDraft, "a person verifies outside MCP");
        (await ListAsync()).Updates.Should().BeEmpty();
    }

    [Fact]
    public async Task Dismiss_StoresTheVersion_AndSuppressesTheFinding_UntilANewerBlueprintVersionArrives()
    {
        SeedPage("page-a");
        await EditAsync("page-a");
        var page = Page("page-a");
        SeedVersion(page, 3, "v3");

        var dismiss = await KnowledgePlatformUpdateTools.KnowledgeDismissPlatformUpdate(MockServer.Object, "page-a", 3);

        dismiss.IsSuccess.Should().BeTrue(dismiss.ErrorMessage);
        page.GetAttributeValueOrDefault("DismissedBlueprintVersion").Should().Be(3);
        Current(page).Should().Be(2);
        (await ListAsync()).Updates.Should().BeEmpty();

        SeedVersion(page, 4, "v4");
        var again = (await ListAsync()).Updates.Should().ContainSingle().Subject;
        again.NewerVersion.Should().Be(4);
        again.DismissedVersion.Should().Be(3);
    }

    [Fact]
    public async Task Dismiss_WritesNoVersionAndNoStatus_AndIsIdempotent()
    {
        SeedPage("page-a");
        await EditAsync("page-a");
        var page = Page("page-a");
        SeedVersion(page, 3, "v3");
        _writes.Clear();

        await KnowledgePlatformUpdateTools.KnowledgeDismissPlatformUpdate(MockServer.Object, "page-a", 3);
        var second = await KnowledgePlatformUpdateTools.KnowledgeDismissPlatformUpdate(MockServer.Object, "page-a", 3);

        second.IsSuccess.Should().BeTrue();
        second.Message.Should().Contain("already dismissed");
        _writes.Should().ContainSingle();
        _writes[0].RtEntity!.Attributes.Keys.Should().Equal("DismissedBlueprintVersion");
    }

    [Theory]
    [InlineData(2, "no Blueprint version 2")]
    [InlineData(9, "no Blueprint version 9")]
    public async Task Dismiss_OfAVersionThatIsNoBlueprintVersion_IsRefused(int version, string message)
    {
        SeedPage("page-a");
        await EditAsync("page-a"); // version 2 is the tenant's own

        var result = await KnowledgePlatformUpdateTools.KnowledgeDismissPlatformUpdate(MockServer.Object, "page-a", version);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain(message);
    }

    // ---- AC: deleted page ----

    [Fact]
    public async Task Deleted_StaysDeleted_NoSwitch_NoFinding()
    {
        var page = SeedPage("retired-page");
        SeedVersion(page, 2, "v2");
        _entities.Remove(page.RtId); // a person deleted the page; its versions stay, the update brought v2

        var adopt = await AdoptAsync();
        var list = await ListAsync();

        adopt.Adopted.Should().BeEmpty();
        adopt.Available.Should().BeEmpty();
        list.Updates.Should().BeEmpty();
        _entities.Values.Should().NotContain(e => Equals(e.CkTypeId, KnowledgePages.PageTypeId));
        _writes.Should().BeEmpty();
    }

    // ---- AC: links ----

    [Fact]
    public async Task LinkChange_LinksToFollowsTheDeclaredLinksOfTheAdoptedVersion_AddedAndRemoved()
    {
        SeedPage("page-b", "B");
        SeedPage("page-c", "C");
        SeedPage("page-d", "D");
        var page = SeedPage("page-a", "A", links: ["page-b", "page-c"]);
        SeedVersion(page, 2, "A v2", declaredLinks: ["page-c", "page-d", "page-missing"]);

        var result = await AdoptAsync();

        var adopted = result.Adopted.Should().ContainSingle(a => a.Slug == "page-a").Subject;
        LinksOf(page).Should().Equal("page-c", "page-d");
        adopted.LinksAdded.Should().Be(1);
        adopted.LinksRemoved.Should().Be(1);
        adopted.UnresolvedLinks.Should().Equal("page-missing");
    }

    [Fact]
    public async Task AVersionWithoutDeclaredLinks_LeavesTheLinksUnchanged()
    {
        SeedPage("page-b", "B");
        var page = SeedPage("page-a", "A", links: ["page-b"]);
        SeedVersion(page, 2, "A v2");

        (await AdoptAsync()).Adopted.Should().ContainSingle();
        LinksOf(page).Should().Equal("page-b");
    }

    [Fact]
    public async Task AnEmptyDeclaredLinksList_RemovesEveryLink()
    {
        SeedPage("page-b", "B");
        var page = SeedPage("page-a", "A", links: ["page-b"]);
        SeedVersion(page, 2, "A v2", declaredLinks: []);

        (await AdoptAsync()).Adopted.Should().ContainSingle();
        LinksOf(page).Should().BeEmpty();
    }

    // ---- AC: audit ----

    [Fact]
    public async Task EverySwitch_IsAPageWriteOfTheKnowledgeTools_WithTheRunReference()
    {
        var page = SeedPage("page-a");
        SeedVersion(page, 2, "v2");

        await AdoptAsync();

        page.GetAttributeStringValueOrDefault("AdoptedByRun").Should().Be(RunRef);
        _writes.Should().ContainSingle(w => w.ModOption == EntityModOptions.Update &&
                                            Equals(w.RtEntity!.CkTypeId, KnowledgePages.PageTypeId));
        MockSecureSessionFactory.Verify(f => f.GetSessionAsync(It.Is<RtSecurityContext>(c =>
            c != RtSecurityContext.System && c.WriteChannel == KnowledgePages.WriteChannel)), Times.AtLeastOnce);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public async Task Adopt_WithoutARunReference_IsRefused_BeforeAnySession(string runRef)
    {
        var result = await AdoptAsync(runRef);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("InvalidArgument");
        MockSecureSessionFactory.Verify(f => f.GetSessionAsync(It.IsAny<RtSecurityContext>()), Times.Never);
    }

    [Fact]
    public async Task Adopt_RunsTheContentGuard_AndSkipsARejectedPage()
    {
        var page = SeedPage("page-a");
        var v2 = SeedVersion(page, 2, "v2");
        KnowledgePages.SetContent(v2, Content("v2") with
        {
            Sections = [new KnowledgeSectionDto { Heading = "Steps", SectionContent = "password=Sup3rS3cretValue!" }]
        });

        var result = await AdoptAsync();

        result.Adopted.Should().BeEmpty();
        result.Skipped.Should().ContainSingle(s => s.Slug == "page-a" && s.Reason!.Contains(ContentGuardResult.ErrorCode));
        Current(page).Should().Be(1);
    }

    // ---- AC: idempotent / already current ----

    [Fact]
    public async Task SecondRun_WithoutANewBlueprintVersion_ChangesNothing()
    {
        var page = SeedPage("page-a");
        SeedVersion(page, 2, "v2");
        (await AdoptAsync()).Adopted.Should().ContainSingle();
        _writes.Clear();

        var second = await AdoptAsync(RunRef + "-2");

        second.IsSuccess.Should().BeTrue();
        second.Adopted.Should().BeEmpty();
        second.Skipped.Should().BeEmpty();
        _writes.Should().BeEmpty();
        page.GetAttributeStringValueOrDefault("AdoptedByRun").Should().Be(RunRef);
    }

    [Fact]
    public async Task AlreadyCurrent_NothingIsAdoptedOrListed()
    {
        SeedPage("page-a");

        (await AdoptAsync()).Adopted.Should().BeEmpty();
        (await ListAsync()).Updates.Should().BeEmpty();
        _writes.Should().BeEmpty();
    }

    // ---- model, authentication, arguments ----

    [Fact]
    public async Task OnATenantBeforeSystemAi4100_AdoptAndDismissAreRefused_ListStillWorks()
    {
        var page = SeedPage("page-a");
        SeedVersion(page, 2, "v2");
        _modelHasPlatformUpdates = false;

        var adopt = await AdoptAsync();
        var dismiss = await KnowledgePlatformUpdateTools.KnowledgeDismissPlatformUpdate(MockServer.Object, "page-a", 2);
        var list = await ListAsync();

        adopt.ErrorMessage.Should().Contain("System.Ai 4.10.0");
        dismiss.ErrorMessage.Should().Contain("System.Ai 4.10.0");
        list.IsSuccess.Should().BeTrue();
        _writes.Should().BeEmpty();
    }

    [Fact]
    public async Task Unauthenticated_IsRefused_ByEveryTool()
    {
        GivenUnauthenticatedCaller();

        (await AdoptAsync()).ErrorMessage.Should().Contain("Not authenticated");
        (await ListAsync()).ErrorMessage.Should().Contain("Not authenticated");
        (await KnowledgePlatformUpdateTools.KnowledgeDismissPlatformUpdate(MockServer.Object, "page-a", 2)).ErrorMessage
            .Should().Contain("Not authenticated");
        _writes.Should().BeEmpty();
    }

    [Theory]
    [InlineData("Bad Slug", 2)]
    [InlineData("page-a", 0)]
    public async Task Dismiss_WithInvalidArguments_IsRefused_BeforeAnySession(string slug, int version)
    {
        var result = await KnowledgePlatformUpdateTools.KnowledgeDismissPlatformUpdate(MockServer.Object, slug, version);

        result.ErrorCode.Should().Be("InvalidArgument");
        MockSecureSessionFactory.Verify(f => f.GetSessionAsync(It.IsAny<RtSecurityContext>()), Times.Never);
    }

    [Fact]
    public void Risk_WritesAreMedium()
    {
        var registry = new Meshmakers.Octo.Backend.McpServices.Services.ToolRiskRegistry();
        registry.GetRiskLevel("knowledge_adopt_platform_updates").Should().Be(McpRiskLevel.Medium);
        registry.GetRiskLevel("knowledge_dismiss_platform_update").Should().Be(McpRiskLevel.Medium);
    }
}
