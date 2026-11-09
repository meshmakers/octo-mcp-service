using System.Text.Json;
using FluentAssertions;
using Meshmakers.Octo.Backend.McpServices.ContentGuard;
using Meshmakers.Octo.Backend.McpServices.Models;
using Meshmakers.Octo.Backend.McpServices.Resources;
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

    /// <summary>The text search of every page list query (null = no text search), in call order.</summary>
    private readonly List<string?> _textSearches = [];

    /// <summary>
    ///     AB#8566: when set, the repository returns every page for a text search, like the engine returned
    ///     knowledge-schema (score 0) for "Zebrafisch" in the hand test; the tool alone must drop such pages.
    /// </summary>
    private bool _textIndexReturnsEveryPage;
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
            .ReturnsAsync((IOctoSession _, RtCkId<CkTypeId> type, RtEntityQueryOptions options, int? _, int? _) =>
            {
                var textSearch = options.TextSearchFilter?.SearchTerm?.ToString();
                _textSearches.Add(textSearch);
                var items = _entities.Values.Where(e => Equals(e.CkTypeId, type))
                    .Where(e => textSearch == null || _textIndexReturnsEveryPage || MatchesTextIndex(e, textSearch))
                    .ToList();
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
    public async Task PageMovedOnBeforeTheTransaction_IsAConflict_AndWritesNothing()
    {
        await UpsertAsync("page-a", "Page A", "text");
        var page = _entities.Values.Single(e => Equals(e.CkTypeId, KnowledgePages.PageTypeId));
        // Another writer commits version 2 after this call read the page and before its transaction starts.
        MockSession.Setup(s => s.StartTransaction()).Callback(() =>
            page.SetAttributeValue("CurrentVersion",
                Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects.AttributeValueTypesDto.Int, 2));

        var result = await UpsertAsync("page-a", "Page A", "my change");

        result.IsSuccess.Should().BeFalse();
        result.IsConflict.Should().BeTrue();
        result.ErrorCode.Should().Be("VersionConflict");
        Versions("page-a").Should().ContainSingle();
        MockSession.Verify(s => s.AbortTransactionAsync(), Times.Once);
    }

    [Fact]
    public async Task WriteConflictOfTheEngine_IsAVersionConflict()
    {
        await UpsertAsync("page-a", "Page A", "text");
        MockTenantRepository
            .Setup(r => r.ApplyChangesAsync(It.IsAny<IOctoSession>(), It.IsAny<IReadOnlyList<IEntityUpdateInfo<RtEntity>>>(),
                It.IsAny<IReadOnlyList<AssociationUpdateInfo>>(), It.IsAny<OperationResult>()))
            .ThrowsAsync(new InvalidOperationException("Command update failed: WriteConflict error: this operation " +
                                                       "conflicted with another operation."));

        var result = await KnowledgeTools.KnowledgeSetStatus(MockServer.Object, "page-a", "InReview",
            cancellationToken: Ct);

        result.IsConflict.Should().BeTrue();
        result.ErrorCode.Should().Be("VersionConflict");
        Versions("page-a").Should().ContainSingle();
    }

    [Fact]
    public async Task WriteConflictReportedInTheOperationResult_IsAVersionConflict()
    {
        await UpsertAsync("page-a", "Page A", "text");
        MockTenantRepository
            .Setup(r => r.ApplyChangesAsync(It.IsAny<IOctoSession>(), It.IsAny<IReadOnlyList<IEntityUpdateInfo<RtEntity>>>(),
                It.IsAny<IReadOnlyList<AssociationUpdateInfo>>(), It.IsAny<OperationResult>()))
            .Callback((IOctoSession _, IReadOnlyList<IEntityUpdateInfo<RtEntity>> _,
                IReadOnlyList<AssociationUpdateInfo> _, OperationResult result) =>
                result.AddMessage(new Meshmakers.Octo.ConstructionKit.Contracts.Messages.OperationMessage(
                    Meshmakers.Octo.ConstructionKit.Contracts.Messages.MessageLevel.Error, null, 0,
                    "WriteConflict error: this operation conflicted with another operation.")))
            .Returns(Task.CompletedTask);

        var result = await KnowledgeTools.KnowledgeSetStatus(MockServer.Object, "page-a", "InReview",
            cancellationToken: Ct);

        result.ErrorCode.Should().Be("VersionConflict");
        MockSession.Verify(s => s.CommitTransactionAsync(), Times.Once); // only the setup upsert committed
    }

    [Fact]
    public async Task WriteConflictOnCommit_IsAVersionConflict_AndAFailingAbortDoesNotHideIt()
    {
        await UpsertAsync("page-a", "Page A", "text");
        MockSession.Setup(s => s.CommitTransactionAsync())
            .ThrowsAsync(new InvalidOperationException("Commit failed: WriteConflict"));
        MockSession.Setup(s => s.AbortTransactionAsync())
            .ThrowsAsync(new InvalidOperationException("Cannot call abortTransaction after calling commitTransaction."));

        var result = await KnowledgeTools.KnowledgeSetStatus(MockServer.Object, "page-a", "InReview",
            cancellationToken: Ct);

        result.IsConflict.Should().BeTrue();
        result.ErrorCode.Should().Be("VersionConflict");
        MockSession.Verify(s => s.AbortTransactionAsync(), Times.Once);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CredentialInACitationReference_IsRejected(bool inSectionRef)
    {
        const string key = "sk-ant-api03-AbCdEf123456";
        var citation = new KnowledgeCitationDto
        {
            SectionRef = inSectionRef ? key : "Summary",
            SourceQuote = "quote",
            SourceRtId = inSectionRef ? null : key
        };

        var result = await KnowledgeTools.KnowledgeUpsertPage(MockServer.Object, "page-a", "Page A",
            [new KnowledgeSectionDto { Heading = "Summary", SectionContent = "harmless" }], [citation],
            cancellationToken: Ct);

        result.ErrorCode.Should().Be(ContentGuardResult.ErrorCode);
        result.ErrorMessage.Should().Contain("citations[0]").And.NotContain("AbCdEf123456");
        _entities.Values.Should().NotContain(e => Equals(e.CkTypeId, KnowledgePages.PageTypeId));
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

    // AB#8657: run data of the ingest pipeline (tokens, cost, reviewer rounds, run reference) on the new version.
    private static readonly string[] RunDataAttributes =
        ["PromptTokens", "CompletionTokens", "CostUsd", "ReviewerIterations", "RunRef"];

    private Task<KnowledgeWriteResponse> UpsertWithRunDataAsync(string slug, int? inputTokens = null,
        int? outputTokens = null, double? cost = null, int? reviewerRounds = null, string? runRef = null) =>
        KnowledgeTools.KnowledgeUpsertPage(MockServer.Object, slug, "Page " + slug,
            [new KnowledgeSectionDto { Heading = "Summary", SectionContent = "text" }],
            inputTokens: inputTokens, outputTokens: outputTokens, cost: cost, reviewerRounds: reviewerRounds,
            runRef: runRef, cancellationToken: Ct);

    [Fact]
    public async Task Upsert_WithAllRunData_StoresEachValueOnTheNewVersion()
    {
        var result = await KnowledgeTools.KnowledgeUpsertPage(MockServer.Object, "page-a", "Page A",
            [new KnowledgeSectionDto { Heading = "Summary", SectionContent = "text" }], origin: "Ingest",
            promptName: "wiki-prompt-agentic", promptVersion: 3, modelId: "claude-sonnet-4-6", inputTokens: 12345,
            outputTokens: 2100, cost: 0.0532, reviewerRounds: 2, runRef: "exec-6731a2f0", cancellationToken: Ct);

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        var version = Versions("page-a").Single();
        version.GetAttributeValueOrDefault("PromptTokens").Should().Be(12345);
        version.GetAttributeValueOrDefault("CompletionTokens").Should().Be(2100);
        version.GetAttributeValueOrDefault("CostUsd").Should().Be(0.0532);
        version.GetAttributeValueOrDefault("ReviewerIterations").Should().Be(2);
        version.GetAttributeStringValueOrDefault("RunRef").Should().Be("exec-6731a2f0");
        version.GetAttributeStringValueOrDefault("PromptName").Should().Be("wiki-prompt-agentic");
        version.GetAttributeValueOrDefault("PromptVersion").Should().Be(3);
        version.GetAttributeStringValueOrDefault("ModelId").Should().Be("claude-sonnet-4-6");
        Page("page-a").Attributes.Keys.Should().NotContain(RunDataAttributes,
            "run data belongs to the version, not to the page");
    }

    [Fact]
    public async Task Upsert_WithZeroRunData_StoresTheZeros()
    {
        var result = await UpsertWithRunDataAsync("page-a", 0, 0, 0d, 0);

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        var version = Versions("page-a").Single();
        version.GetAttributeValueOrDefault("PromptTokens").Should().Be(0);
        version.GetAttributeValueOrDefault("CostUsd").Should().Be(0d);
        version.GetAttributeValueOrDefault("ReviewerIterations").Should().Be(0);
    }

    [Fact]
    public async Task Upsert_WithPromptMetadataOnly_BehavesAsBefore_AndStoresNoRunData()
    {
        var result = await KnowledgeTools.KnowledgeUpsertPage(MockServer.Object, "page-a", "Page A",
            [new KnowledgeSectionDto { Heading = "Summary", SectionContent = "text" }],
            promptName: "wiki-prompt-structured", promptVersion: 3, modelId: "claude-sonnet-4-6", cancellationToken: Ct);

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        result.Message.Should().Be("Created 'page-a' as version 1 (Draft).");
        var version = Versions("page-a").Single();
        version.GetAttributeStringValueOrDefault("PromptName").Should().Be("wiki-prompt-structured");
        version.GetAttributeValueOrDefault("PromptVersion").Should().Be(3);
        version.GetAttributeStringValueOrDefault("ModelId").Should().Be("claude-sonnet-4-6");
        version.Attributes.Keys.Should().NotContain(RunDataAttributes);
    }

    [Fact]
    public async Task Upsert_UpdateWithoutRunData_WritesAVersionWithoutRunData_AndKeepsTheEarlierVersion()
    {
        await UpsertWithRunDataAsync("page-a", 100, 50, 0.01, 1, "exec-1");

        var result = await UpsertAsync("page-a", "Page A", "edited by hand");

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        var versions = Versions("page-a").ToDictionary(v => v.RtWellKnownName!);
        versions["page-a@2"].Attributes.Keys.Should().NotContain(RunDataAttributes);
        versions["page-a@1"].GetAttributeValueOrDefault("PromptTokens").Should().Be(100);
        versions["page-a@1"].GetAttributeStringValueOrDefault("RunRef").Should().Be("exec-1");
    }

    [Theory]
    [InlineData(-1, null, null, null, "inputTokens (-1)")]
    [InlineData(null, -5, null, null, "outputTokens (-5)")]
    [InlineData(null, null, -0.01, null, "cost (-0.01)")]
    [InlineData(null, null, null, -1, "reviewerRounds (-1)")]
    public async Task Upsert_WithNegativeRunData_IsRejected_AndNothingIsWritten(int? inputTokens, int? outputTokens,
        double? cost, int? reviewerRounds, string expected)
    {
        var result = await UpsertWithRunDataAsync("page-a", inputTokens, outputTokens, cost, reviewerRounds);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("InvalidArgument");
        result.ErrorMessage.Should().Be($"Run data must not be negative: {expected}.");
        _entities.Values.Should().NotContain(e => Equals(e.CkTypeId, KnowledgePages.PageTypeId));
        MockTenantRepository.Verify(r => r.ApplyChangesAsync(It.IsAny<IOctoSession>(),
            It.IsAny<IReadOnlyList<IEntityUpdateInfo<RtEntity>>>(), It.IsAny<IReadOnlyList<AssociationUpdateInfo>>(),
            It.IsAny<OperationResult>()), Times.Never);
    }

    [Fact]
    public async Task Upsert_WithSeveralNegativeValues_NamesEachOfThem()
    {
        var result = await UpsertWithRunDataAsync("page-a", -1, -2, -3d, -4);

        result.ErrorMessage.Should().Contain("inputTokens (-1)").And.Contain("outputTokens (-2)")
            .And.Contain("cost (-3)").And.Contain("reviewerRounds (-4)");
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public async Task Upsert_WithANonFiniteCost_IsRejected(double cost)
    {
        var result = await UpsertWithRunDataAsync("page-a", cost: cost);

        result.ErrorCode.Should().Be("InvalidArgument");
        result.ErrorMessage.Should().Be("cost must be a finite number of USD.");
        _entities.Values.Should().NotContain(e => Equals(e.CkTypeId, KnowledgePages.PageTypeId));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null, 201)]
    public async Task Upsert_WithAnInvalidRunRef_IsRejected(string? runRef, int length = 0)
    {
        var result = await UpsertWithRunDataAsync("page-a", runRef: runRef ?? new string('x', length));

        result.ErrorCode.Should().Be("InvalidArgument");
        result.ErrorMessage.Should().Contain("runRef");
        _entities.Values.Should().NotContain(e => Equals(e.CkTypeId, KnowledgePages.PageTypeId));
    }

    [Theory]
    [InlineData("3f2b8c1e-9d4a-4e7b-a6c5-0f1e2d3c4b5a")] // pipeline execution id: Guid ("D"), as the mesh adapter leases it
    [InlineData("3F2B8C1E9D4A4E7BA6C50F1E2D3C4B5A")] // Guid without dashes, upper case
    [InlineData("672000000000000000000abc")] // 24-hex rtId of a future agent Run (AB#8453)
    public async Task Upsert_WithARealExecutionIdAsRunRef_PassesTheContentGuard_AndIsStored(string runRef)
    {
        var result = await UpsertWithRunDataAsync("page-a", runRef: runRef);

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        Versions("page-a").Single().GetAttributeStringValueOrDefault("RunRef").Should().Be(runRef);
    }

    [Fact]
    public async Task Upsert_WithACredentialInTheRunRef_IsRejected_ByTheContentGuard()
    {
        var result = await UpsertWithRunDataAsync("page-a", runRef: "sk-ant-api03-AbCdEf123456");

        result.ErrorCode.Should().Be(ContentGuardResult.ErrorCode);
        result.ErrorMessage.Should().Contain("runRef").And.NotContain("AbCdEf123456");
        _entities.Values.Should().NotContain(e => Equals(e.CkTypeId, KnowledgePages.PageTypeId));
    }

    [Fact]
    public void Upsert_DescriptionsList_TheRunDataParameters()
    {
        var method = typeof(KnowledgeTools).GetMethod(nameof(KnowledgeTools.KnowledgeUpsertPage))!;
        var description = method.GetCustomAttributes(typeof(System.ComponentModel.DescriptionAttribute), false)
            .Cast<System.ComponentModel.DescriptionAttribute>().Single().Description;
        description.Should().Contain("inputTokens, outputTokens, cost (USD), reviewerRounds and runRef");

        string[] parameters = ["inputTokens", "outputTokens", "cost", "reviewerRounds", "runRef"];
        foreach (var name in parameters)
        {
            var parameter = method.GetParameters().Single(p => p.Name == name);
            parameter.IsOptional.Should().BeTrue();
            parameter.GetCustomAttributes(typeof(System.ComponentModel.DescriptionAttribute), false)
                .Should().ContainSingle(name);
        }
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

    public enum Caller
    {
        User,
        ServiceAccount,
        NoPrincipal
    }

    private void GivenCaller(Caller caller)
    {
        switch (caller)
        {
            case Caller.ServiceAccount:
                GivenServicePrincipalCaller();
                break;
            case Caller.NoPrincipal:
                GivenUnauthenticatedCaller();
                break;
        }
    }

    private RtEntity Page(string slug) => _entities.Values.Single(e =>
        Equals(e.CkTypeId, KnowledgePages.PageTypeId) && e.RtWellKnownName == slug);

    /// <summary>Seeds a page directly as Verified (as a person's octo-cli write would leave it), version 2.</summary>
    private async Task SeedVerifiedPageAsync(string slug)
    {
        await UpsertAsync(slug, "Page A", "text");
        var page = Page(slug);
        page.SetAttributeValue("VerificationStatus", AttributeValueTypesDto.Enum, KnowledgePages.StatusVerified);
        page.SetAttributeValue("CurrentVersion", AttributeValueTypesDto.Int, 2);
    }

    [Theory]
    [InlineData(Caller.User, "Verified")]
    [InlineData(Caller.ServiceAccount, "Verified")]
    [InlineData(Caller.NoPrincipal, "Verified")]
    [InlineData(Caller.User, "verified")]
    [InlineData(Caller.User, "2")]
    public async Task SetStatus_Verified_IsRefusedOverMcpForEveryCaller(Caller caller, string status)
    {
        await UpsertAsync("page-a", "Page A", "text");
        GivenCaller(caller);
        MockTenantResolution.Invocations.Clear();

        var result = await KnowledgeTools.KnowledgeSetStatus(MockServer.Object, "page-a", status, cancellationToken: Ct);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("VerifiedNotAllowedOverMcp");
        result.ErrorMessage.Should().Contain("SetKnowledgePageStatus").And.Contain("GraphQL");
        Versions("page-a").Should().ContainSingle("no version is written");
        Page("page-a").GetAttributeValueOrDefault("VerificationStatus").Should().Be(KnowledgePages.StatusDraft);
        // The check runs before the tenant is opened: no repository is resolved, nothing can be written.
        MockTenantResolution.Verify(t => t.GetTenantRepositoryAsync(It.IsAny<string?>()), Times.Never);
    }

    [Theory]
    [InlineData(Caller.User)]
    [InlineData(Caller.ServiceAccount)]
    public async Task SetStatus_DraftInReviewRejected_StillWork_AndWriteAVersion(Caller caller)
    {
        await UpsertAsync("page-a", "Page A", "text");
        GivenCaller(caller);

        foreach (var status in new[] { "InReview", "Rejected", "Draft" })
        {
            var result = await KnowledgeTools.KnowledgeSetStatus(MockServer.Object, "page-a", status, cancellationToken: Ct);
            result.IsSuccess.Should().BeTrue(result.ErrorMessage);
            result.Page!.Status.Should().Be(status);
        }

        Versions("page-a").Should().HaveCount(4);
    }

    [Fact]
    public async Task SetStatus_OnAVerifiedPage_CanMoveItBackToInReview()
    {
        await SeedVerifiedPageAsync("page-a");

        var result = await KnowledgeTools.KnowledgeSetStatus(MockServer.Object, "page-a", "InReview", cancellationToken: Ct);

        result.Page!.Status.Should().Be("InReview");
        result.Page.CurrentVersion.Should().Be(3);
    }

    [Fact]
    public void SetStatus_Description_ListsOnlyTheMcpStates_AndNamesTheHumanPath()
    {
        var method = typeof(KnowledgeTools).GetMethod(nameof(KnowledgeTools.KnowledgeSetStatus))!;
        var description = method.GetCustomAttributes(typeof(System.ComponentModel.DescriptionAttribute), false)
            .Cast<System.ComponentModel.DescriptionAttribute>().Single().Description;
        description.Should().Contain("Draft, InReview or Rejected").And.Contain("SetKnowledgePageStatus")
            .And.Contain("VerifiedNotAllowedOverMcp");
        var statusParameter = method.GetParameters().Single(p => p.Name == "status")
            .GetCustomAttributes(typeof(System.ComponentModel.DescriptionAttribute), false)
            .Cast<System.ComponentModel.DescriptionAttribute>().Single().Description;
        statusParameter.Should().StartWith("Draft, InReview or Rejected").And.Contain("SetKnowledgePageStatus");
    }

    [Fact]
    public async Task ContentWrite_PutsAVerifiedPageBackToDraft()
    {
        await SeedVerifiedPageAsync("page-a");

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

    /// <summary>
    ///     Like the MongoDB text index the page query uses: a page matches when its title, summary or a section contains a
    ///     query word; stop words and words shorter than 3 characters match nothing (MongoDB drops them), so a short-word
    ///     query sent to the index returns no page.
    /// </summary>
    private static bool MatchesTextIndex(RtEntity page, string textSearch)
    {
        var content = KnowledgePages.ReadContent(page);
        var text = string.Join(" ", new[] { content.Title, content.Summary ?? string.Empty }
            .Concat(content.Sections.Select(x => $"{x.Heading} {x.SectionContent}")));
        return textSearch.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length >= 3)
            .Any(w => text.Contains(w, StringComparison.OrdinalIgnoreCase));
    }

    // AB#8566: a page about the schema and one that contains "Zebrafisch". The repository applies the text index
    // (MatchesTextIndex) unless a test sets _textIndexReturnsEveryPage to replay the engine of the hand test.
    private async Task GivenSchemaAndZebrafischPagesAsync()
    {
        await UpsertAsync("knowledge-schema", "Knowledge base conventions", "How pages are written.");
        await UpsertAsync("zebrafisch-pipeline", "Pipeline schedule", "Die Zebrafisch-Pipeline läuft nachts.");
    }

    [Fact]
    public async Task Search_WithAQuery_ReturnsOnlyMatchingPages()
    {
        await GivenSchemaAndZebrafischPagesAsync();

        var result = await KnowledgeTools.KnowledgeSearch(MockServer.Object, "Zebrafisch");

        result.IsSuccess.Should().BeTrue();
        result.Pages.Select(p => p.Slug).Should().Equal("zebrafisch-pipeline");
        result.Pages.Should().OnlyContain(p => p.Score > 0);
    }

    [Fact]
    public async Task Search_DropsAPageTheTextIndexPreselected_WithoutAnyQueryWord()
    {
        await GivenSchemaAndZebrafischPagesAsync();
        _textIndexReturnsEveryPage = true;

        var result = await KnowledgeTools.KnowledgeSearch(MockServer.Object, "Zebrafisch");

        // The repository returned both pages; knowledge-schema has no query word (score 0) and is dropped by the tool.
        _textSearches.Should().EndWith("Zebrafisch");
        result.IsSuccess.Should().BeTrue();
        result.Pages.Select(p => p.Slug).Should().Equal("zebrafisch-pipeline");
        result.Pages.Should().OnlyContain(p => p.Score > 0);
    }

    [Fact]
    public async Task Search_WhenTheTextIndexPreselectsOnlyPagesWithoutAQueryWord_IsEmpty_WithTheNoMatchMessage()
    {
        await GivenSchemaAndZebrafischPagesAsync();
        _textIndexReturnsEveryPage = true;

        var result = await KnowledgeTools.KnowledgeSearch(MockServer.Object, "Okapi");

        result.IsSuccess.Should().BeTrue();
        result.Pages.Should().BeEmpty();
        result.Message.Should().StartWith("No knowledge page matches");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Verified")]
    [InlineData("Draft")]
    public async Task Search_WithAQueryThatMatchesNothing_IsEmpty_WithTheNoMatchMessage(string? status)
    {
        await GivenSchemaAndZebrafischPagesAsync();
        var page = _entities.Values.Single(e => e.RtWellKnownName == "knowledge-schema");
        page.SetAttributeValue("VerificationStatus", AttributeValueTypesDto.Enum, KnowledgePages.StatusVerified);

        var result = await KnowledgeTools.KnowledgeSearch(MockServer.Object, "Okapi", status: status);

        result.IsSuccess.Should().BeTrue();
        result.Pages.Should().BeEmpty();
        result.Message.Should().StartWith("No knowledge page matches");
    }

    [Fact]
    public async Task Search_ForZebrafischWithStatusVerified_IsEmpty_WhenOnlyTheSchemaPageIsVerified()
    {
        // The hand-test case: knowledge-schema (Verified) used to come back with score 0.
        await GivenSchemaAndZebrafischPagesAsync();
        _textIndexReturnsEveryPage = true;
        var page = _entities.Values.Single(e => e.RtWellKnownName == "knowledge-schema");
        page.SetAttributeValue("VerificationStatus", AttributeValueTypesDto.Enum, KnowledgePages.StatusVerified);

        var result = await KnowledgeTools.KnowledgeSearch(MockServer.Object, "Zebrafisch", status: "Verified");

        result.Pages.Should().BeEmpty();
        result.Message.Should().StartWith("No knowledge page matches");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("a of")]
    public async Task Search_WithoutAWordOfThreeCharacters_ReturnsTheIndexOfAllPages(string? query)
    {
        await GivenSchemaAndZebrafischPagesAsync();
        _textSearches.Clear();

        var result = await KnowledgeTools.KnowledgeSearch(MockServer.Object, query);

        result.IsSuccess.Should().BeTrue();
        result.Pages.Select(p => p.Slug).Should().Equal("knowledge-schema", "zebrafisch-pipeline");
        // The page list is read without a text search: sent to the text index, "a of" would return no page.
        _textSearches.Should().Equal([null]);
        if (!string.IsNullOrWhiteSpace(query))
        {
            result.Message.Should().Contain("index of all pages, not a match");
        }
    }

    // AB#8567: omitted optional content parameters keep the current value on update; explicit empty values clear it.
    private Task<KnowledgeWriteResponse> CreateFullPageAsync() =>
        KnowledgeTools.KnowledgeUpsertPage(MockServer.Object, "zebrafisch-pipeline", "Zebrafisch pipeline",
            [new KnowledgeSectionDto { Heading = "When", SectionContent = "Nightly at 02:00." }],
            citations: [new KnowledgeCitationDto { SectionRef = "When", SourceQuote = "runs nightly", SourceRtId = SourceRtId }],
            hallucinationFlags: [new KnowledgeHallucinationFlagDto { Claim = "02:00", Reason = "not in the source" }],
            summary: "Wann die Zebrafisch-Pipeline läuft.", kind: "HowTo", cancellationToken: Ct);

    private RtEntity VersionEntity(string slug, int version) =>
        Versions(slug).Single(v => v.RtWellKnownName == $"{slug}@{version}");

    [Fact]
    public async Task Update_WithoutOptionalContent_KeepsKindSummaryCitationsAndFlags()
    {
        (await CreateFullPageAsync()).IsSuccess.Should().BeTrue();

        var result = await KnowledgeTools.KnowledgeUpsertPage(MockServer.Object, "zebrafisch-pipeline",
            "Zebrafisch pipeline", [new KnowledgeSectionDto { Heading = "When", SectionContent = "Nightly at 03:00." }],
            expectedVersion: 1, cancellationToken: Ct);

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        result.Page!.CurrentVersion.Should().Be(2);
        result.Page.Summary.Should().Be("Wann die Zebrafisch-Pipeline läuft.");
        result.Page.Kind.Should().Be("HowTo");
        var page = await KnowledgeTools.KnowledgeGet(MockServer.Object, "zebrafisch-pipeline");
        page.Sections.Single().SectionContent.Should().Be("Nightly at 03:00.", "sections are replaced");
        page.Citations.Should().ContainSingle(c => c.SourceQuote == "runs nightly");
        page.HallucinationFlags.Should().ContainSingle(f => f.Claim == "02:00");
        var version2 = KnowledgePages.ReadContent(VersionEntity("zebrafisch-pipeline", 2));
        version2.Summary.Should().Be("Wann die Zebrafisch-Pipeline läuft.", "the version carries the kept summary too");
        version2.Citations.Should().HaveCount(1);
        version2.HallucinationFlags.Should().HaveCount(1);
    }

    [Fact]
    public async Task Update_WithExplicitEmptyValues_ClearsSummaryCitationsAndFlags()
    {
        await CreateFullPageAsync();

        var result = await KnowledgeTools.KnowledgeUpsertPage(MockServer.Object, "zebrafisch-pipeline",
            "Zebrafisch pipeline", [new KnowledgeSectionDto { Heading = "When", SectionContent = "Nightly." }],
            citations: [], hallucinationFlags: [], summary: "", kind: "Concept", expectedVersion: 1,
            cancellationToken: Ct);

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        result.Page!.Summary.Should().BeEmpty();
        result.Page.Kind.Should().Be("Concept", "an explicit kind replaces the current one");
        var page = await KnowledgeTools.KnowledgeGet(MockServer.Object, "zebrafisch-pipeline");
        page.Citations.Should().BeEmpty();
        page.HallucinationFlags.Should().BeEmpty();
    }

    [Fact]
    public async Task Create_WithoutOptionalContent_HasDefaults()
    {
        var result = await UpsertAsync("plain", "Plain", "text");

        result.Page!.Kind.Should().Be("Concept");
        result.Page.Summary.Should().BeEmpty();
        (await KnowledgeTools.KnowledgeGet(MockServer.Object, "plain")).Citations.Should().BeEmpty();
    }

    [Fact]
    public void Upsert_Description_StatesTheOmittedMeansUnchangedRule()
    {
        var description = typeof(KnowledgeTools).GetMethod(nameof(KnowledgeTools.KnowledgeUpsertPage))!
            .GetCustomAttributes(typeof(System.ComponentModel.DescriptionAttribute), false)
            .Cast<System.ComponentModel.DescriptionAttribute>().Single().Description;
        description.Should().Contain("omitted kind, summary, citations or hallucinationFlags keeps the current value")
            .And.Contain("origin applies to new pages only").And.Contain("only add");
    }

    [Fact]
    public async Task Search_WithAWordOfThreeCharacters_SendsTheQueryToTheTextIndex()
    {
        await GivenSchemaAndZebrafischPagesAsync();
        _textSearches.Clear();

        var result = await KnowledgeTools.KnowledgeSearch(MockServer.Object, "a Zebrafisch");

        result.Pages.Select(p => p.Slug).Should().Equal("zebrafisch-pipeline");
        _textSearches.Should().Equal("a Zebrafisch");
    }

    // AB#8566 review: the page text index stems English words, so it returns a page for an inflected query word. The
    // repository replays that with _textIndexReturnsEveryPage; the tool must keep the stem match and drop the page
    // without any query word.
    [Theory]
    [InlineData("pipelines")] // plural of "Pipeline" in the title and the section
    [InlineData("scheduled")] // inflected form of "schedule" in the title
    [InlineData("Scheduling pipelines")]
    public async Task Search_KeepsAPageThatMatchesOnlyAnInflectedForm_AndDropsThePageWithoutAMatch(string query)
    {
        await GivenSchemaAndZebrafischPagesAsync();
        _textIndexReturnsEveryPage = true;

        var result = await KnowledgeTools.KnowledgeSearch(MockServer.Object, query);

        result.IsSuccess.Should().BeTrue();
        result.Pages.Select(p => p.Slug).Should().Equal("zebrafisch-pipeline");
        result.Pages.Should().OnlyContain(p => p.Score > 0);
    }

    [Fact]
    public void Score_CountsStemMatchesWithTheFieldWeights_AndIsZeroWithoutAnyMatch()
    {
        var content = new KnowledgeContent("Pipeline schedule", 0, "Pipelines run nightly.",
            [new KnowledgeSectionDto { Heading = "Runs", SectionContent = "The pipeline runs at 02:00." }], [], [], 0);

        // "pipelines": literal in the summary (2), stem in the title (3) and the section (1).
        KnowledgeTools.Score(content, ["pipelines"]).Should().Be(6);
        // "running": no literal hit; stem "run" in the summary (2) and twice in the section (heading + text, 2).
        KnowledgeTools.Score(content, ["running"]).Should().Be(4);
        KnowledgeTools.Score(content, ["okapi", "zebrafisch"]).Should().Be(0);
    }

    [Theory]
    [InlineData("zebrafisch-pipelines", new[] { "zebrafisch", "pipelines" })]
    [InlineData("(Pipelines) page's run", new[] { "pipelines", "page", "run" })]
    [InlineData("a b? ok", new string[0])]
    public void Terms_SplitTheQueryLikeThePageText_AndKeepWordsOf3PlusCharacters(string query, string[] expected)
    {
        KnowledgeTools.Terms(query).Should().Equal(expected);
    }

    [Fact]
    public void Score_AHyphenatedOrBracketedQueryWord_MatchesThePageWord()
    {
        var content = new KnowledgeContent("Pipeline schedule", 0, null, [], [], [], 0);

        KnowledgeTools.Score(content, KnowledgeTools.Terms("zebrafisch-pipelines")).Should().Be(3);
        KnowledgeTools.Score(content, KnowledgeTools.Terms("(pipelines)")).Should().Be(3);
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

    public static TheoryData<string> AllTools =>
        ["search", "get", "upsert", "link", "restore", "set_status"];

    private Task<KnowledgeResponse> CallAsync(string tool) => tool switch
    {
        "search" => Cast(KnowledgeTools.KnowledgeSearch(MockServer.Object)),
        "get" => Cast(KnowledgeTools.KnowledgeGet(MockServer.Object, "page-a")),
        "upsert" => Cast(UpsertAsync("page-a", "Page A", "text")),
        "link" => Cast(KnowledgeTools.KnowledgeLink(MockServer.Object, "page-a", "page-b")),
        "restore" => Cast(KnowledgeTools.KnowledgeRestoreVersion(MockServer.Object, "page-a", 1, cancellationToken: Ct)),
        "set_status" => Cast(KnowledgeTools.KnowledgeSetStatus(MockServer.Object, "page-a", "InReview",
            cancellationToken: Ct)),
        _ => throw new ArgumentOutOfRangeException(nameof(tool))
    };

    private static async Task<KnowledgeResponse> Cast<T>(Task<T> task) where T : KnowledgeResponse => await task;

    [Theory]
    [MemberData(nameof(AllTools))]
    public async Task Unauthenticated_IsRefused_ByEveryTool(string tool)
    {
        GivenUnauthenticatedCaller();

        var result = await CallAsync(tool);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Not authenticated");
        MockTenantRepository.Verify(r => r.ApplyChangesAsync(It.IsAny<IOctoSession>(),
            It.IsAny<IReadOnlyList<IEntityUpdateInfo<RtEntity>>>(), It.IsAny<IReadOnlyList<AssociationUpdateInfo>>(),
            It.IsAny<OperationResult>()), Times.Never);
    }

    public static TheoryData<string> MissingArguments =>
        ["search-limit", "get-slug", "upsert-title", "upsert-sections", "link-same", "restore-version", "status-unknown"];

    [Theory]
    [MemberData(nameof(MissingArguments))]
    public async Task MissingOrInvalidArguments_AreRefused_BeforeAnySession(string variant)
    {
        KnowledgeResponse result = variant switch
        {
            "search-limit" => await KnowledgeTools.KnowledgeSearch(MockServer.Object, limit: 0),
            "get-slug" => await KnowledgeTools.KnowledgeGet(MockServer.Object, ""),
            "upsert-title" => await UpsertAsync("page-a", " ", "text"),
            "upsert-sections" => await KnowledgeTools.KnowledgeUpsertPage(MockServer.Object, "page-a", "Page A", [],
                cancellationToken: Ct),
            "link-same" => await KnowledgeTools.KnowledgeLink(MockServer.Object, "page-a", "page-a"),
            "restore-version" => await KnowledgeTools.KnowledgeRestoreVersion(MockServer.Object, "page-a", 0,
                cancellationToken: Ct),
            "status-unknown" => await KnowledgeTools.KnowledgeSetStatus(MockServer.Object, "page-a", "Approved",
                cancellationToken: Ct),
            _ => throw new ArgumentOutOfRangeException(nameof(variant))
        };

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().NotBeNullOrEmpty();
        MockSecureSessionFactory.Verify(f => f.GetSessionAsync(It.IsAny<RtSecurityContext>()), Times.Never);
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
