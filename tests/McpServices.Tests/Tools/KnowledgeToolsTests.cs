using System.Text.Json;
using FluentAssertions;
using Meshmakers.Octo.Backend.McpServices.ContentGuard;
using Meshmakers.Octo.Backend.McpServices.Models;
using Meshmakers.Octo.Backend.McpServices.Resources;
using Meshmakers.Octo.Backend.McpServices.Services.Knowledge;
using Meshmakers.Octo.Backend.McpServices.Tools;
using Meshmakers.Octo.ConstructionKit.Contracts;
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
///     AB#8484: the knowledge tools against an in-memory repository that behaves like the engine for what they use
///     (entities by type and id, outbound associations, ApplyChanges with inserts, updates and association changes).
/// </summary>
public class KnowledgeToolsTests : TestBase
{
    private const string SourceRtId = "672000000000000000000101";

    // The generation JSON of the KnowledgeCapture prototype (prompt wiki-prompt-structured, v3), unchanged.
    private const string PrototypeGenerationJson = """
        {"title": "Rolling out a seed change",
         "sections": [
           {"heading": "Summary", "sectionContent": "A seed change reaches tenants through a blueprint version bump.", "sortOrder": 0},
           {"heading": "Issues and blockers", "sectionContent": "FixAll must not run on EnergyIQ tenants.", "sortOrder": 1}],
         "citations": [
           {"sectionRef": "Summary", "sourceQuote": "bump the blueprint version", "sourceAnchor": "", "sourceRtId": "672000000000000000000101"}]}
        """;

    private readonly Dictionary<OctoObjectId, RtEntity> _entities = new();
    private readonly List<(RtEntityId Origin, RtEntityId Target, string Role)> _associations = [];

    public KnowledgeToolsTests()
    {
        TestServiceProvider.RegisterService<IContentGuard>(new ContentGuard(NullLogger<ContentGuard>.Instance));

        CkTypeGraph? pageType = CkGraphTestBuilder.BuildType("System.Ai-4.7.0/KnowledgePage-1");
        MockCkCacheService.Setup(c => c.TryGetRtCkType(It.IsAny<string>(), It.IsAny<RtCkId<CkTypeId>>(), out pageType))
            .Returns(true);
        MockCkCacheService.Setup(c => c.GetCkTypes(It.IsAny<string>())).Returns([]);

        var source = new RtEntity
        {
            RtId = new OctoObjectId(SourceRtId),
            CkTypeId = new RtCkId<CkTypeId>(KnowledgePages.SourceArtifactType)
        };
        _entities[source.RtId] = source;

        MockTenantRepository
            .Setup(r => r.GetRtEntitiesByTypeAsync(It.IsAny<IOctoSession>(), It.IsAny<RtCkId<CkTypeId>>(),
                It.IsAny<RtEntityQueryOptions>(), It.IsAny<int?>(), It.IsAny<int?>()))
            .ReturnsAsync((IOctoSession _, RtCkId<CkTypeId> type, RtEntityQueryOptions _, int? _, int? _) =>
            {
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
                var items = _associations
                    .Where(a => a.Origin.RtId == origin.RtId && a.Role == o.RoleId!.ToString())
                    .Select(a => new RtAssociation { OriginRtId = a.Origin.RtId, TargetRtId = a.Target.RtId })
                    .ToList();
                return new ResultSet<RtAssociation>(items, items.Count, null, null);
            });
        MockTenantRepository
            .Setup(r => r.ApplyChangesAsync(It.IsAny<IOctoSession>(), It.IsAny<IReadOnlyList<IEntityUpdateInfo<RtEntity>>>(),
                It.IsAny<IReadOnlyList<AssociationUpdateInfo>>(), It.IsAny<OperationResult>()))
            .Returns((IOctoSession _, IReadOnlyList<IEntityUpdateInfo<RtEntity>> updates,
                IReadOnlyList<AssociationUpdateInfo> associations, OperationResult _) =>
            {
                foreach (var update in updates)
                {
                    var entity = update.RtEntity!;
                    if (update.ModOption == EntityModOptions.Insert || !_entities.TryGetValue(entity.RtId, out var stored))
                    {
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

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    private Task<KnowledgeWriteResponse> UpsertAsync(string slug, string title, string body, int? expectedVersion = null,
        List<string>? linksTo = null) =>
        KnowledgeTools.KnowledgeUpsertPage(MockServer.Object, slug, title,
            [new KnowledgeSectionDto { Heading = "Summary", SectionContent = body }], linksTo: linksTo,
            expectedVersion: expectedVersion, cancellationToken: Ct);

    private IEnumerable<RtEntity> Versions(string slug) => _entities.Values
        .Where(e => Equals(e.CkTypeId, KnowledgePages.VersionTypeId) && e.RtWellKnownName!.StartsWith(slug + "@"));

    [Fact]
    public async Task Upsert_CreatesThePageAndVersion1_WithAuthorAndTime()
    {
        var result = await UpsertAsync("rollout-seed-change", "Rolling out a seed change", "Bump, train, update.");

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        result.Created.Should().BeTrue();
        result.Page!.CurrentVersion.Should().Be(1);
        result.Page.Status.Should().Be("Draft");
        var version = Versions("rollout-seed-change").Should().ContainSingle().Subject;
        version.GetAttributeStringValueOrDefault("Author").Should().Be("user:test-subject");
        version.GetAttributeValueOrDefault("AuthoredAt").Should().BeOfType<DateTime>();
        _associations.Should().ContainSingle(a => a.Role == KnowledgePages.VersionOfRole &&
                                                   a.Target.RtId.ToString() == result.Page.RtId);
    }

    [Fact]
    public async Task EveryWrite_AppendsAVersion_AndRestoreMakesAnOldVersionCurrent()
    {
        await UpsertAsync("page-a", "Page A", "first text");
        await UpsertAsync("page-a", "Page A", "second text");
        Versions("page-a").Should().HaveCount(2);

        var restored = await KnowledgeTools.KnowledgeRestoreVersion(MockServer.Object, "page-a", 1, expectedVersion: 2,
            cancellationToken: Ct);

        restored.IsSuccess.Should().BeTrue(restored.ErrorMessage);
        restored.Page!.CurrentVersion.Should().Be(3);
        Versions("page-a").Should().HaveCount(3, "a restore appends, it never deletes");
        Versions("page-a").Single(v => v.RtWellKnownName == "page-a@3")
            .GetAttributeValueOrDefault("RestoredFromVersion").Should().Be(1);
        var current = await KnowledgeTools.KnowledgeGet(MockServer.Object, "page-a");
        current.Markdown.Should().Contain("first text").And.NotContain("second text");
        var old = await KnowledgeTools.KnowledgeGet(MockServer.Object, "page-a", version: 2);
        old.Markdown.Should().Contain("second text");
    }

    [Fact]
    public async Task StaleExpectedVersion_IsAConflict_AndWritesNothing()
    {
        await UpsertAsync("page-a", "Page A", "first text");

        var result = await UpsertAsync("page-a", "Page A", "other text", expectedVersion: 5);

        result.IsSuccess.Should().BeFalse();
        result.IsConflict.Should().BeTrue();
        result.ErrorCode.Should().Be("VersionConflict");
        Versions("page-a").Should().ContainSingle();
    }

    [Fact]
    public async Task CredentialLikeContent_IsRejected_WithContentRejected_AndNothingIsWritten()
    {
        var result = await UpsertAsync("leaky-page", "Keys", "Use sk-ant-api03-AbCdEf123456 for the calls.");

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ContentGuardResult.ErrorCode);
        result.ErrorMessage.Should().NotContain("AbCdEf123456");
        _entities.Values.Should().NotContain(e => Equals(e.CkTypeId, KnowledgePages.PageTypeId));
    }

    [Fact]
    public async Task TenantSecretValue_IsRejected()
    {
        TestServiceProvider.RegisterService(SecretProtector("mqtt-broker-pass-8484"));
        var secretType = CkGraphTestBuilder.BuildType("System.Communication-4.13.0/MqttConfiguration-1",
            ("Password", Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects.AttributeValueTypesDto.Secret, null));
        MockCkCacheService.Setup(c => c.GetCkTypes(It.IsAny<string>())).Returns([secretType]);
        var config = new RtEntity { RtId = OctoObjectId.GenerateNewId(), CkTypeId = secretType.CkTypeId.ToRtCkId() };
        config.SetAttributeRawValue("Password", RtSecretValue.Pending("ignored-by-the-mock"));
        _entities[config.RtId] = config;

        var result = await UpsertAsync("broker", "Broker", "Log in with mqtt-broker-pass-8484 as password.");

        result.ErrorCode.Should().Be(ContentGuardResult.ErrorCode);
        result.ErrorMessage.Should().Contain(nameof(ContentGuardDetector.TenantSecret));
    }

    [Fact]
    public async Task CredentialInTheChangeNote_IsRejected_AndNothingIsWritten()
    {
        var result = await KnowledgeTools.KnowledgeUpsertPage(MockServer.Object, "page-a", "Page A",
            [new KnowledgeSectionDto { Heading = "Summary", SectionContent = "harmless" }],
            changeNote: "copied from the run with sk-ant-api03-AbCdEf123456", cancellationToken: Ct);

        result.ErrorCode.Should().Be(ContentGuardResult.ErrorCode);
        result.ErrorMessage.Should().Contain("changeNote").And.NotContain("AbCdEf123456");
        _entities.Values.Should().NotContain(e => Equals(e.CkTypeId, KnowledgePages.PageTypeId));
    }

    [Fact]
    public async Task SetStatus_RunsTheContentGuard_OnTheChangeNote()
    {
        await UpsertAsync("page-a", "Page A", "text");

        var result = await KnowledgeTools.KnowledgeSetStatus(MockServer.Object, "page-a", "InReview",
            changeNote: "token sk-ant-api03-AbCdEf123456", cancellationToken: Ct);

        result.ErrorCode.Should().Be(ContentGuardResult.ErrorCode);
        Versions("page-a").Should().ContainSingle();
    }

    [Fact]
    public async Task SetStatus_Verified_IsRefusedWithoutAnyPrincipal()
    {
        GivenUnauthenticatedCaller();

        var result = await KnowledgeTools.KnowledgeSetStatus(MockServer.Object, "page-a", "Verified",
            cancellationToken: Ct);

        result.ErrorCode.Should().Be("VerifiedRequiresPerson");
    }

    [Fact]
    public async Task Upsert_AcceptsThePrototypeGenerationJson_AndLinksGeneratedFrom()
    {
        var generation = JsonDocument.Parse(PrototypeGenerationJson).RootElement;
        var sections = generation.GetProperty("sections").Deserialize<List<KnowledgeSectionDto>>()!;
        var citations = generation.GetProperty("citations").Deserialize<List<KnowledgeCitationDto>>()!;

        var result = await KnowledgeTools.KnowledgeUpsertPage(MockServer.Object, "rollout-seed-change",
            generation.GetProperty("title").GetString()!, sections, citations, origin: "Ingest",
            promptName: "wiki-prompt-structured", promptVersion: 3, cancellationToken: Ct);

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        result.LinkedSources.Should().Equal(SourceRtId);
        _associations.Should().ContainSingle(a => a.Role == KnowledgePages.GeneratedFromRole &&
                                                   a.Target.RtId.ToString() == SourceRtId);
        var page = await KnowledgeTools.KnowledgeGet(MockServer.Object, "rollout-seed-change");
        page.Sections.Select(s => s.Heading).Should().Equal("Summary", "Issues and blockers");
        page.Citations.Should().ContainSingle(c => c.SourceRtId == SourceRtId);
        page.GeneratedFrom.Should().Equal(SourceRtId);
        page.Markdown.Should().StartWith("# Rolling out a seed change");
        Versions("rollout-seed-change").Single().GetAttributeStringValueOrDefault("PromptName")
            .Should().Be("wiki-prompt-structured");
    }

    [Fact]
    public async Task SetStatus_MovesThroughTheStates_AndVerifiedIsRefusedForServiceAccounts()
    {
        await UpsertAsync("page-a", "Page A", "text");

        (await KnowledgeTools.KnowledgeSetStatus(MockServer.Object, "page-a", "InReview", cancellationToken: Ct)).Page!.Status
            .Should().Be("InReview");
        (await KnowledgeTools.KnowledgeSetStatus(MockServer.Object, "page-a", "Verified", cancellationToken: Ct)).Page!.Status
            .Should().Be("Verified");
        (await KnowledgeTools.KnowledgeSetStatus(MockServer.Object, "page-a", "Rejected", cancellationToken: Ct)).Page!.Status
            .Should().Be("Rejected");
        (await KnowledgeTools.KnowledgeSetStatus(MockServer.Object, "page-a", "Draft", cancellationToken: Ct)).Page!.Status
            .Should().Be("Draft");
        Versions("page-a").Should().HaveCount(5);

        GivenServicePrincipalCaller();
        var refused = await KnowledgeTools.KnowledgeSetStatus(MockServer.Object, "page-a", "Verified", cancellationToken: Ct);

        refused.IsSuccess.Should().BeFalse();
        refused.ErrorCode.Should().Be("VerifiedRequiresPerson");
        Versions("page-a").Should().HaveCount(5);
    }

    [Fact]
    public async Task ContentWrite_PutsAVerifiedPageBackToDraft()
    {
        await UpsertAsync("page-a", "Page A", "text");
        await KnowledgeTools.KnowledgeSetStatus(MockServer.Object, "page-a", "Verified", cancellationToken: Ct);

        var result = await UpsertAsync("page-a", "Page A", "changed text");

        result.Page!.Status.Should().Be("Draft");
    }

    [Fact]
    public async Task Search_RanksTitleMatchesFirst_AndReturnsTitleStatusAndSummary()
    {
        await KnowledgeTools.KnowledgeUpsertPage(MockServer.Object, "energyiq-fixall", "FixAll on EnergyIQ tenants",
            [new KnowledgeSectionDto { Heading = "Rule", SectionContent = "Never run it." }],
            summary: "FixAll breaks EnergyIQ.", cancellationToken: Ct);
        await UpsertAsync("rollout", "Rollout", "Mentions FixAll once.");
        await UpsertAsync("unrelated", "Unrelated", "Nothing here.");

        var result = await KnowledgeTools.KnowledgeSearch(MockServer.Object, "FixAll EnergyIQ");

        result.IsSuccess.Should().BeTrue();
        result.Pages.Select(p => p.Slug).Should().StartWith(["energyiq-fixall", "rollout"]);
        result.Pages[0].Status.Should().Be("Draft");
        result.Pages[0].Summary.Should().Be("FixAll breaks EnergyIQ.");
        result.Pages[0].Uri.Should().Be("knowledge-page://test-tenant/energyiq-fixall");
    }

    [Fact]
    public async Task Link_AddsAndRemovesLinksTo_WithoutAVersion()
    {
        await UpsertAsync("page-a", "Page A", "text");
        await UpsertAsync("page-b", "Page B", "text");

        (await KnowledgeTools.KnowledgeLink(MockServer.Object, "page-a", "page-b")).IsSuccess.Should().BeTrue();
        (await KnowledgeTools.KnowledgeGet(MockServer.Object, "page-a")).LinksTo.Should().Equal("page-b");
        (await KnowledgeTools.KnowledgeLink(MockServer.Object, "page-a", "page-b", remove: true)).IsSuccess
            .Should().BeTrue();
        (await KnowledgeTools.KnowledgeGet(MockServer.Object, "page-a")).LinksTo.Should().BeEmpty();
        Versions("page-a").Should().ContainSingle();
    }

    [Fact]
    public async Task Upsert_WithLinksTo_LinksTheCitedPages_AndRefusesUnknownOnes()
    {
        await UpsertAsync("page-b", "Page B", "text");

        (await UpsertAsync("answer", "Answer", "From page-b.", linksTo: ["page-b"])).IsSuccess.Should().BeTrue();
        (await KnowledgeTools.KnowledgeGet(MockServer.Object, "answer")).LinksTo.Should().Equal("page-b");
        var refused = await UpsertAsync("answer-2", "Answer", "x", linksTo: ["missing-page"]);
        refused.ErrorMessage.Should().Contain("missing-page");
    }

    [Fact]
    public async Task ResourceTemplate_RendersThePage()
    {
        await UpsertAsync("page-a", "Page A", "resource text");

        var markdown = await KnowledgePageResources.GetKnowledgePageAsync(MockServer.Object, "test-tenant", "page-a");

        markdown.Should().StartWith("# Page A").And.Contain("resource text");
    }

    [Theory]
    [InlineData("Bad Slug")]
    [InlineData("")]
    [InlineData("-x")]
    public async Task InvalidSlug_IsRefused_BeforeAnySession(string slug)
    {
        var result = await UpsertAsync(slug, "T", "x");

        result.ErrorCode.Should().Be("InvalidArgument");
        MockSecureSessionFactory.Verify(f => f.GetSessionAsync(It.IsAny<RtSecurityContext>()), Times.Never);
    }

    [Fact]
    public async Task Unauthenticated_IsRefused()
    {
        GivenUnauthenticatedCaller();

        var result = await KnowledgeTools.KnowledgeSearch(MockServer.Object);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Not authenticated");
    }

    [Fact]
    public async Task Writes_OpenTheSessionWithTheCallersContext()
    {
        await UpsertAsync("page-a", "Page A", "text");

        MockSecureSessionFactory.Verify(f => f.GetSessionAsync(It.Is<RtSecurityContext>(c => c != RtSecurityContext.System)),
            Times.AtLeastOnce);
    }

    [Fact]
    public async Task TenantWithoutKnowledgeTypes_GetsAClearError()
    {
        CkTypeGraph? none = null;
        MockCkCacheService.Setup(c => c.TryGetRtCkType(It.IsAny<string>(), It.IsAny<RtCkId<CkTypeId>>(), out none))
            .Returns(false);

        var result = await KnowledgeTools.KnowledgeSearch(MockServer.Object);

        result.ErrorMessage.Should().Contain("System.Ai 4.7.0");
    }

    private static Meshmakers.Octo.Runtime.Contracts.Secrets.ISecretAttributeProtector SecretProtector(string plaintext)
    {
        var protector = new Mock<Meshmakers.Octo.Runtime.Contracts.Secrets.ISecretAttributeProtector>();
        protector.SetupGet(p => p.IsConfigured).Returns(true);
        protector.Setup(p => p.RevealOrNull(It.IsAny<RtSecretValue?>(),
                It.IsAny<Meshmakers.Octo.Runtime.Contracts.Secrets.SecretAccessContext?>()))
            .Returns(plaintext);
        return protector.Object;
    }
}
