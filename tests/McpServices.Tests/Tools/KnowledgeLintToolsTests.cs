using System.Text.Json;
using FluentAssertions;
using Meshmakers.Octo.Backend.McpServices.ContentGuard;
using Meshmakers.Octo.Backend.McpServices.Models;
using Meshmakers.Octo.Backend.McpServices.Services;
using Meshmakers.Octo.Backend.McpServices.Services.Knowledge;
using Meshmakers.Octo.Backend.McpServices.Tools;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Contracts.Secrets;
using Meshmakers.Octo.Runtime.Engine.Repositories.Query;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace McpServices.Tests.Tools;

/// <summary>
///     AB#8488: <c>knowledge_write_lint_report</c> writes one unvalidated KnowledgeLintReport per call, runs the content
///     guard over every text, refuses invalid input before a session is opened and changes no knowledge page.
/// </summary>
public class KnowledgeLintToolsTests : TestBase
{
    private static readonly DateTime Started = new(2026, 11, 10, 2, 30, 0, DateTimeKind.Utc);

    private readonly List<IEntityUpdateInfo<RtEntity>> _writes = [];
    private readonly Dictionary<OctoObjectId, RtEntity> _entities = new();
    private bool _reportTypeHasValidatedAttribute = true;
    private bool _pageTypeHasDismissedAttribute = true;

    public KnowledgeLintToolsTests()
    {
        TestServiceProvider.RegisterService<IContentGuard>(new ContentGuard(NullLogger<ContentGuard>.Instance));
        MockCkCacheService.Setup(c => c.GetCkTypes(It.IsAny<string>())).Returns([]);
        MockCkCacheService
            .Setup(c => c.TryGetRtCkType(It.IsAny<string>(), It.IsAny<RtCkId<CkTypeId>>(),
                out It.Ref<CkTypeGraph?>.IsAny))
            .Returns(new TryGetType((string _, RtCkId<CkTypeId> id, out CkTypeGraph? graph) =>
            {
                graph = id.ToString() == KnowledgeLintTools.ReportType
                    ? ReportType(_reportTypeHasValidatedAttribute)
                    : _pageTypeHasDismissedAttribute
                        ? CkGraphTestBuilder.BuildType("System.Ai-4.10.0/KnowledgePage-1",
                            ("DismissedBlueprintVersion", AttributeValueTypesDto.Int, null))
                        : CkGraphTestBuilder.BuildType("System.Ai-4.8.0/KnowledgePage-1");
                return true;
            }));
        MockTenantRepository
            .Setup(r => r.GetRtEntitiesByTypeAsync(It.IsAny<IOctoSession>(), It.IsAny<RtCkId<CkTypeId>>(),
                It.IsAny<RtEntityQueryOptions>(), It.IsAny<int?>(), It.IsAny<int?>()))
            .ReturnsAsync((IOctoSession _, RtCkId<CkTypeId> type, RtEntityQueryOptions _, int? _, int? _) =>
            {
                var items = _entities.Values.Where(e => Equals(e.CkTypeId, type)).ToList();
                return new ResultSet<RtEntity>(items, items.Count, null, null);
            });
        MockTenantRepository
            .Setup(r => r.ApplyChangesAsync(It.IsAny<IOctoSession>(),
                It.IsAny<IReadOnlyList<IEntityUpdateInfo<RtEntity>>>(), It.IsAny<IReadOnlyList<AssociationUpdateInfo>>(),
                It.IsAny<OperationResult>()))
            .Returns((IOctoSession _, IReadOnlyList<IEntityUpdateInfo<RtEntity>> updates,
                IReadOnlyList<AssociationUpdateInfo> _, OperationResult _) =>
            {
                _writes.AddRange(updates);
                return Task.CompletedTask;
            });
    }

    private delegate bool TryGetType(string tenantId, RtCkId<CkTypeId> ckTypeId, out CkTypeGraph? graph);

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    private static CkTypeGraph ReportType(bool withValidated)
    {
        var attributes = new List<(string, AttributeValueTypesDto, CkId<CkRecordId>?)>
        {
            ("Name", AttributeValueTypesDto.String, null),
            ("StartedAt", AttributeValueTypesDto.DateTime, null),
            ("Findings", AttributeValueTypesDto.RecordArray, null)
        };
        if (withValidated)
        {
            attributes.Add(("IsValidated", AttributeValueTypesDto.Boolean, null));
        }

        return CkGraphTestBuilder.BuildType("System.Ai-4.8.0/KnowledgeLintReport-1", attributes.ToArray());
    }

    private static KnowledgeLintFindingDto Finding(string kind, string page = "page-a", string? related = null,
        string detail = "Something to check.") =>
        new() { Kind = kind, PageRef = page, RelatedPageRef = related, Detail = detail };

    private Task<KnowledgeLintReportResponse> WriteAsync(List<KnowledgeLintFindingDto>? findings = null,
        int pagesChecked = 3, DateTime? completedAt = null, string? name = null, int? inputTokens = null,
        int? outputTokens = null, double? cost = null, DateTime? verifiedThrough = null, string? runRef = null,
        DateTime? startedAt = null) =>
        KnowledgeLintTools.KnowledgeWriteLintReport(MockServer.Object, startedAt ?? Started, pagesChecked, findings,
            completedAt, name, inputTokens, outputTokens, cost, verifiedThrough, runRef, cancellationToken: Ct);

    private RtEntity WrittenReport() =>
        _writes.Should().ContainSingle().Subject.RtEntity!;

    private void VerifyNothingWritten() =>
        MockTenantRepository.Verify(r => r.ApplyChangesAsync(It.IsAny<IOctoSession>(),
            It.IsAny<IReadOnlyList<IEntityUpdateInfo<RtEntity>>>(), It.IsAny<IReadOnlyList<AssociationUpdateInfo>>(),
            It.IsAny<OperationResult>()), Times.Never);

    private void VerifyNoSessionOpened() =>
        MockSecureSessionFactory.Verify(f => f.GetSessionAsync(It.IsAny<RtSecurityContext>()), Times.Never);

    private static List<RtRecord> Findings(RtEntity report) =>
        ((IEnumerable<RtRecord>)report.GetAttributeValueOrDefault("Findings")!).ToList();

    // ---- happy path ----

    [Fact]
    public async Task Write_StoresOneReport_WithEveryFindingKind_AndAllRunData()
    {
        var findings = KnowledgeLintTools.FindingKindNames
            .Select((k, i) => Finding(k, $"page-{i}", k is "Contradiction" or "MissingLink" ? "page-x" : null,
                $"Finding of kind {k}."))
            .ToList();

        var result = await WriteAsync(findings, pagesChecked: 7, completedAt: Started.AddMinutes(3),
            inputTokens: 41000, outputTokens: 1800, cost: 0.15, verifiedThrough: Started.AddDays(-1),
            runRef: "knowledge-lint/2026-11-12");

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        result.FindingCount.Should().Be(7);
        result.IsValidated.Should().BeFalse();
        result.FindingsByKind.Should().HaveCount(7).And.ContainKey("CitationNotInSource")
            .And.ContainKey("PlatformUpdateAvailable");
        result.Name.Should().Be("knowledge-lint 2026-11-10T02:30Z");
        var report = WrittenReport();
        _writes[0].ModOption.Should().Be(EntityModOptions.Insert);
        report.CkTypeId!.ToString().Should().Be(KnowledgeLintTools.ReportType);
        report.RtId.ToString().Should().Be(result.RtId);
        report.GetAttributeValueOrDefault("IsValidated").Should().Be(false);
        report.GetAttributeValueOrDefault("PagesChecked").Should().Be(7);
        report.GetAttributeValueOrDefault("StartedAt").Should().Be(Started);
        report.GetAttributeValueOrDefault("CompletedAt").Should().Be(Started.AddMinutes(3));
        report.GetAttributeValueOrDefault("InputTokens").Should().Be(41000);
        report.GetAttributeValueOrDefault("OutputTokens").Should().Be(1800);
        report.GetAttributeValueOrDefault("Cost").Should().Be(0.15);
        report.GetAttributeValueOrDefault("VerifiedThrough").Should().Be(Started.AddDays(-1));
        report.GetAttributeStringValueOrDefault("RunRef").Should().Be("knowledge-lint/2026-11-12");

        var records = Findings(report);
        records.Select(r => r.Attributes["Kind"]).Should().Equal(0, 1, 2, 3, 4, 5, 6);
        records.Should().OnlyContain(r => r.CkRecordId.ToString() == KnowledgeLintTools.FindingRecord);
        records[0].Attributes["RelatedPageRef"].Should().Be("page-x");
        records[1].Attributes.Should().NotContainKey("RelatedPageRef");
        records[5].Attributes["Detail"].Should().Be("Finding of kind CitationNotInSource.");
    }

    [Fact]
    public async Task Write_CleanRun_StoresAReportWithoutFindings_AndNoRunData()
    {
        var result = await WriteAsync(pagesChecked: 0, name: "nightly");

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        result.FindingCount.Should().Be(0);
        var report = WrittenReport();
        report.GetAttributeStringValueOrDefault("Name").Should().Be("nightly");
        Findings(report).Should().BeEmpty();
        report.Attributes.Keys.Should().NotContain(["InputTokens", "OutputTokens", "Cost", "VerifiedThrough",
            "RunRef", "CompletedAt"]);
    }

    [Fact]
    public async Task Write_KindsAreCaseInsensitive_AndAcceptTheEnumKey()
    {
        var result = await WriteAsync([Finding("orphan"), Finding("5")]);

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        Findings(WrittenReport()).Select(r => r.Attributes["Kind"]).Should().Equal(1, 5);
    }

    [Fact]
    public async Task PlatformUpdateAvailable_IsStoredAsKey6()
    {
        var result = await WriteAsync([Finding("PlatformUpdateAvailable", "app-build-sequence",
            detail: "Edited page at version 3; Blueprint version 4 (System.Ai.Builder-1.1.0) is newer.")]);

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        Findings(WrittenReport()).Single().Attributes["Kind"].Should().Be(6);
    }

    [Fact]
    public async Task PlatformUpdateAvailable_OnATenantBeforeSystemAi4100_IsRefused_AndNothingIsWritten()
    {
        _pageTypeHasDismissedAttribute = false;

        var refused = await WriteAsync([Finding("PlatformUpdateAvailable")]);
        var other = await WriteAsync([Finding("Stale")]);

        refused.IsSuccess.Should().BeFalse();
        refused.ErrorMessage.Should().Contain("System.Ai").And.Contain("4.10.0").And.Contain("AB#8780");
        other.IsSuccess.Should().BeTrue("the other kinds still work on System.Ai 4.8/4.9");
        _writes.Should().ContainSingle();
    }

    [Fact]
    public async Task Write_TouchesNoKnowledgePage_AndNeverSetsVerified()
    {
        await WriteAsync([Finding("Stale")]);

        _writes.Should().OnlyContain(w => w.RtEntity!.CkTypeId!.ToString() == KnowledgeLintTools.ReportType);
        _writes.SelectMany(w => w.RtEntity!.Attributes.Keys).Should().NotContain("VerificationStatus");
    }

    [Fact]
    public async Task Write_TwoCalls_AreTwoReports()
    {
        await WriteAsync([Finding("Orphan")]);
        await WriteAsync([Finding("Orphan")]);

        _writes.Should().HaveCount(2);
        _writes.Select(w => w.RtEntity!.RtId).Distinct().Should().HaveCount(2);
        _writes.Should().OnlyContain(w => w.ModOption == EntityModOptions.Insert);
    }

    // ---- content guard ----

    [Theory]
    [InlineData("detail")]
    [InlineData("name")]
    [InlineData("runRef")]
    public async Task CredentialInTheReport_IsRejected_AndNothingIsWritten(string field)
    {
        const string leak = "sk-ant-api03-AbCdEf123456";
        var result = await WriteAsync(
            [Finding("UnsupportedClaim", detail: field == "detail" ? $"The page says to call with {leak}." : "ok")],
            name: field == "name" ? $"lint {leak}" : null, runRef: field == "runRef" ? leak : null);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ContentGuardResult.ErrorCode);
        result.ErrorMessage.Should().NotContain("AbCdEf123456");
        VerifyNothingWritten();
    }

    [Fact]
    public async Task TenantSecretValueInAFinding_IsRejected()
    {
        TestServiceProvider.RegisterService(SecretProtector("mqtt-broker-pass-8488"));
        var secretType = CkGraphTestBuilder.BuildType("System.Communication-4.13.0/MqttConfiguration-1",
            ("Password", AttributeValueTypesDto.Secret, null));
        MockCkCacheService.Setup(c => c.GetCkTypes(It.IsAny<string>())).Returns([secretType]);
        var config = new RtEntity { RtId = OctoObjectId.GenerateNewId(), CkTypeId = secretType.CkTypeId.ToRtCkId() };
        config.SetAttributeRawValue("Password", RtSecretValue.Pending("ignored-by-the-mock"));
        _entities[config.RtId] = config;

        var result = await WriteAsync([Finding("CitationNotInSource",
            detail: "Quote 'log in with mqtt-broker-pass-8488' is not in the source.")]);

        result.ErrorCode.Should().Be(ContentGuardResult.ErrorCode);
        result.ErrorMessage.Should().Contain(nameof(ContentGuardDetector.TenantSecret)).And.NotContain("mqtt-broker-pass");
        VerifyNothingWritten();
    }

    [Fact]
    public async Task WithoutAContentGuard_TheWriteIsRefused()
    {
        TestServiceProvider.RegisterService<IContentGuard>(null!);

        var result = await WriteAsync([Finding("Orphan")]);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ContentGuardResult.ErrorCode);
        VerifyNothingWritten();
    }

    // ---- refusals before any session ----

    [Fact]
    public async Task Unauthenticated_IsRefused()
    {
        GivenUnauthenticatedCaller();

        var result = await WriteAsync([Finding("Orphan")]);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Not authenticated");
        VerifyNothingWritten();
    }

    public static TheoryData<string> InvalidVariants =>
    [
        "kind", "page-ref", "related-ref", "detail-empty", "detail-long", "too-many", "pages-negative",
        "completed-before-started", "tokens-negative", "cost-nan", "name-long", "run-ref-long", "started-missing",
        "finding-null", "cursor-after-completed", "cursor-in-future", "completed-in-future", "cursor-just-after-now"
    ];

    [Theory]
    [MemberData(nameof(InvalidVariants))]
    public async Task InvalidInput_IsRefused_BeforeAnySession(string variant)
    {
        var result = variant switch
        {
            "kind" => await WriteAsync([Finding("Typo")]),
            "page-ref" => await WriteAsync([Finding("Orphan", page: "Not A Slug")]),
            "related-ref" => await WriteAsync([Finding("Contradiction", related: "two words")]),
            "detail-empty" => await WriteAsync([Finding("Orphan", detail: " ")]),
            "detail-long" => await WriteAsync([Finding("Orphan", detail: new string('x', 2001))]),
            "too-many" => await WriteAsync(Enumerable.Range(0, 501).Select(_ => Finding("Orphan")).ToList()),
            "pages-negative" => await WriteAsync(pagesChecked: -1),
            "completed-before-started" => await WriteAsync(completedAt: Started.AddSeconds(-1)),
            "tokens-negative" => await WriteAsync(inputTokens: -5),
            "cost-nan" => await WriteAsync(cost: double.NaN),
            "name-long" => await WriteAsync(name: new string('n', 201)),
            "run-ref-long" => await WriteAsync(runRef: new string('r', 201)),
            "started-missing" => await WriteAsync(startedAt: default(DateTime)),
            "finding-null" => await WriteAsync([null!]),
            "cursor-after-completed" => await WriteAsync(completedAt: Started.AddMinutes(1),
                verifiedThrough: Started.AddMinutes(2)),
            "cursor-in-future" => await WriteAsync(verifiedThrough: DateTime.UtcNow.AddDays(1)),
            "cursor-just-after-now" => await WriteAsync(verifiedThrough: DateTime.UtcNow.AddMinutes(1)),
            "completed-in-future" => await WriteAsync(completedAt: DateTime.UtcNow.AddDays(1),
                verifiedThrough: DateTime.UtcNow.AddDays(1)),
            _ => throw new ArgumentOutOfRangeException(nameof(variant))
        };

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("InvalidArgument", result.ErrorMessage);
        VerifyNoSessionOpened();
        VerifyNothingWritten();
    }

    [Fact]
    public async Task UnknownKind_NamesTheAllowedKinds()
    {
        var result = await WriteAsync([Finding("Duplicate")]);

        result.ErrorMessage.Should().Contain("findings[0].kind").And.Contain("CitationNotInSource");
    }

    [Fact]
    public async Task ExactlyTheLimits_AreAccepted()
    {
        var result = await WriteAsync(
            Enumerable.Range(0, 500).Select(_ => Finding("Orphan", detail: new string('x', 2000))).ToList(),
            name: new string('n', 200), completedAt: Started.AddMinutes(1), verifiedThrough: Started.AddMinutes(1));

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        result.FindingCount.Should().Be(500);
    }

    [Fact]
    public async Task TenantOnAnOlderSystemAi_IsToldWhatItNeeds_AndNothingIsWritten()
    {
        _reportTypeHasValidatedAttribute = false;

        var result = await WriteAsync([Finding("Orphan")]);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("IsValidated").And.Contain("AB#8488");
        VerifyNothingWritten();
    }

    [Fact]
    public async Task FailedApply_IsReported_AndAborted()
    {
        MockTenantRepository
            .Setup(r => r.ApplyChangesAsync(It.IsAny<IOctoSession>(),
                It.IsAny<IReadOnlyList<IEntityUpdateInfo<RtEntity>>>(), It.IsAny<IReadOnlyList<AssociationUpdateInfo>>(),
                It.IsAny<OperationResult>()))
            .ThrowsAsync(new InvalidOperationException("engine down"));

        var result = await WriteAsync([Finding("Orphan")]);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("engine down");
        MockSession.Verify(s => s.AbortTransactionAsync(), Times.Once);
        MockSession.Verify(s => s.CommitTransactionAsync(), Times.Never);
    }

    [Fact]
    public void Tool_IsMediumRisk_LikeTheOtherKnowledgeWrites()
    {
        new ToolRiskRegistry().GetRiskLevel("knowledge_write_lint_report").Should().Be(McpRiskLevel.Medium);
    }

    private static ISecretAttributeProtector SecretProtector(string plaintext)
    {
        var protector = new Mock<ISecretAttributeProtector>();
        protector.SetupGet(p => p.IsConfigured).Returns(true);
        protector.Setup(p => p.RevealOrNull(It.IsAny<RtSecretValue?>(), It.IsAny<SecretAccessContext?>()))
            .Returns(plaintext);
        return protector.Object;
    }
}

/// <summary>AB#8488: the generic entity tools refuse KnowledgeLintReport and point at knowledge_write_lint_report.</summary>
public class KnowledgeLintReportGenericWriteRefusalTests : TestBase
{
    private const string ReportFullId = "System.Ai-4.8.0/KnowledgeLintReport-1";

    private static AttributeUpdateItem Item(string path, string json) =>
        new() { AttributePath = path, Value = JsonDocument.Parse(json).RootElement.Clone() };

    public KnowledgeLintReportGenericWriteRefusalTests()
    {
        var type = CkGraphTestBuilder.BuildTypeWithAccess(ReportFullId, [], [], [],
            ("Name", AttributeValueTypesDto.String, null, null));
        CkGraphTestBuilder.SetupTryGetRtCkType(MockCkCacheService, type);
        MockCkCacheService.Setup(c => c.GetRtCkType(It.IsAny<string>(), It.IsAny<RtCkId<CkTypeId>>())).Returns(type);
    }

    private void VerifyNothingWritten()
    {
        MockTenantRepository.Verify(r => r.InsertOneRtEntityAsync(It.IsAny<IOctoSession>(),
            It.IsAny<RtCkId<CkTypeId>>(), It.IsAny<RtEntity>()), Times.Never);
        MockTenantRepository.Verify(r => r.UpdateOneRtEntityByIdAsync(It.IsAny<IOctoSession>(),
            It.IsAny<RtCkId<CkTypeId>>(), It.IsAny<OctoObjectId>(), It.IsAny<RtEntity>()), Times.Never);
        MockTenantRepository.Verify(r => r.DeleteOneRtEntityByRtIdAsync(It.IsAny<IOctoSession>(),
            It.IsAny<RtCkId<CkTypeId>>(), It.IsAny<OctoObjectId>(), It.IsAny<DeleteOptions>()), Times.Never);
    }

    [Fact]
    public async Task CreateEntity_IsRefused_WithTheLintTool()
    {
        var result = await RuntimeEntityCrudTools.CreateEntity(MockServer.Object, ReportFullId,
            [Item("Name", "\"forged report\"")]);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(KnowledgeWriteRefusal.Code);
        result.ErrorParameters!["ckTypeId"].Should().Be(KnowledgeLintTools.ReportType);
        result.ErrorParameters["tool"].Should().Be("knowledge_write_lint_report");
        result.ErrorMessage.Should().Contain("knowledge_write_lint_report").And.NotContain("forged report");
        VerifyNothingWritten();
    }

    [Fact]
    public async Task UpdateEntity_IsRefused_WithTheLintTool()
    {
        var result = await RuntimeEntityCrudTools.UpdateEntity(MockServer.Object, OctoObjectId.GenerateNewId().ToString(),
            ReportFullId, [Item("Name", "\"changed\"")]);

        result.ErrorCode.Should().Be(KnowledgeWriteRefusal.Code);
        result.ErrorParameters!["tool"].Should().Be("knowledge_write_lint_report");
        VerifyNothingWritten();
    }

    [Fact]
    public async Task DeleteEntity_IsRefused()
    {
        var result = await RuntimeEntityCrudTools.DeleteEntity(MockServer.Object, ReportFullId,
            OctoObjectId.GenerateNewId().ToString());

        result.ErrorCode.Should().Be(KnowledgeWriteRefusal.Code);
        result.ErrorParameters!["tool"].Should().Be("none");
        result.ErrorMessage.Should().Contain("lint reports");
        VerifyNothingWritten();
    }

    [Fact]
    public void ImportMessage_PointsAtTheLintTool()
    {
        KnowledgeWriteRefusal.ImportMessage(KnowledgeLintTools.ReportType)
            .Should().Contain("knowledge_write_lint_report").And.Contain("Nothing was imported");
    }

    [Fact]
    public async Task TypeDerivedFromTheLintReport_IsRefused_WithoutPointingAtAToolThatCannotWriteIt()
    {
        var derived = CkGraphTestBuilder.BuildTypeWithAccess("Custom-1.0.0/SpecialLintReport-1", [], [ReportFullId], [],
            ("Name", AttributeValueTypesDto.String, null, null));
        CkGraphTestBuilder.SetupTryGetRtCkType(MockCkCacheService, derived);
        MockCkCacheService.Setup(c => c.GetRtCkType(It.IsAny<string>(), It.IsAny<RtCkId<CkTypeId>>())).Returns(derived);

        var result = await RuntimeEntityCrudTools.CreateEntity(MockServer.Object, "Custom-1.0.0/SpecialLintReport-1",
            [Item("Name", "\"forged\"")]);

        result.ErrorCode.Should().Be(KnowledgeWriteRefusal.Code);
        result.ErrorParameters!["ckTypeId"].Should().Be("Custom/SpecialLintReport");
        result.ErrorParameters["tool"].Should().Be("none", "the lint tool writes only the base type");
        result.ErrorMessage.Should().Contain("Derived lint reports are not written or imported over MCP");
        KnowledgeWriteRefusal.ImportMessage(MockCkCacheService.Object, DefaultTestTenantId,
                new RtCkId<CkTypeId>("Custom/SpecialLintReport"))
            .Should().Contain("Derived lint reports").And.Contain("Nothing was imported").And.NotContain("knowledge_upsert_page");
        VerifyNothingWritten();
    }

    [Fact]
    public void PagesKeepTheirOwnRefusal()
    {
        KnowledgeWriteRefusal.ImportMessage(KnowledgePages.PageType).Should().Contain("knowledge_upsert_page");
    }
}
