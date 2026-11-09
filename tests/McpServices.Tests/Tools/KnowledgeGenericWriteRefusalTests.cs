using System.ComponentModel;
using System.IO.Compression;
using System.Text.Json;
using FluentAssertions;
using Meshmakers.Octo.Backend.McpServices.Models;
using Meshmakers.Octo.Backend.McpServices.Services;
using Meshmakers.Octo.Backend.McpServices.Tools;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Engine.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Contracts.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace McpServices.Tests.Tools;

/// <summary>
///     AB#8568: the generic entity tools refuse the System.Ai knowledge types (KnowledgePage, KnowledgePageVersion and
///     types derived from them) with GENERIC_WRITE_REPLACED_BY_KNOWLEDGE_TOOL and write nothing: create, create with
///     secrets, update, set secrets and delete, on the tenant-less and the tenant-routed call, by the requested type
///     and by the actual type of the stored entity. Reads keep working.
/// </summary>
public class KnowledgeGenericWriteRefusalTests : TestBase
{
    private const string Code = "GENERIC_WRITE_REPLACED_BY_KNOWLEDGE_TOOL";
    private const string PageFullId = "System.Ai-4.7.0/KnowledgePage-1";
    private const string VersionFullId = "System.Ai-4.7.0/KnowledgePageVersion-1";
    private const string DerivedFullId = "Custom-1.0.0/SpecialPage-1";
    private const string BaseFullId = "System-2.0.0/Entity-1";
    private const string Canary = "bypass-probe-8568";

    private static AttributeUpdateItem Item(string path, string json) =>
        new() { AttributePath = path, Value = JsonDocument.Parse(json).RootElement.Clone() };

    private CkTypeGraph GivenType(string fullId, params string[] baseTypeIds)
    {
        var type = CkGraphTestBuilder.BuildTypeWithAccess(fullId, [], baseTypeIds, [],
            ("Summary", AttributeValueTypesDto.String, null, null),
            ("ApiKey", AttributeValueTypesDto.Secret, null, null));
        CkGraphTestBuilder.SetupTryGetRtCkType(MockCkCacheService, type);
        MockCkCacheService
            .Setup(c => c.GetRtCkType(It.IsAny<string>(), It.IsAny<RtCkId<CkTypeId>>()))
            .Returns(type);
        return type;
    }

    private RtEntity GivenStoredEntity(string runtimeTypeId)
    {
        var entity = new RtEntity
        {
            RtId = OctoObjectId.GenerateNewId(),
            CkTypeId = new RtCkId<CkTypeId>(runtimeTypeId),
            RtWellKnownName = "m1-handtest-b",
            RtVersion = 3
        };
        entity.SetAttributeValue("Summary", AttributeValueTypesDto.String, "original summary");
        MockTenantRepository
            .Setup(r => r.GetRtEntityByRtIdAsync(It.IsAny<IOctoSession>(), It.IsAny<RtEntityId>()))
            .ReturnsAsync(entity);
        MockTenantRepository
            .Setup(r => r.GetRtEntitiesByTypeAsync(It.IsAny<IOctoSession>(), It.IsAny<RtCkId<CkTypeId>>(),
                It.IsAny<RtEntityQueryOptions>(), It.IsAny<int?>(), It.IsAny<int?>()))
            .ReturnsAsync(new ResultSet<RtEntity>([entity], 1, null, null));
        return entity;
    }

    private void VerifyNothingWritten()
    {
        MockTenantRepository.Verify(r => r.InsertOneRtEntityAsync(It.IsAny<IOctoSession>(),
            It.IsAny<RtCkId<CkTypeId>>(), It.IsAny<RtEntity>()), Times.Never);
        MockTenantRepository.Verify(r => r.CreateTransientRtEntityAsync(It.IsAny<CkId<CkTypeId>>()), Times.Never);
        MockTenantRepository.Verify(r => r.UpdateOneRtEntityByIdAsync(It.IsAny<IOctoSession>(),
            It.IsAny<RtCkId<CkTypeId>>(), It.IsAny<OctoObjectId>(), It.IsAny<RtEntity>()), Times.Never);
        MockTenantRepository.Verify(r => r.ApplyChangesAsync(It.IsAny<IOctoSession>(),
            It.IsAny<IReadOnlyList<IEntityUpdateInfo<RtEntity>>>(), It.IsAny<OperationResult>()), Times.Never);
        MockTenantRepository.Verify(r => r.DeleteOneRtEntityByRtIdAsync(It.IsAny<IOctoSession>(),
            It.IsAny<RtCkId<CkTypeId>>(), It.IsAny<OctoObjectId>(), It.IsAny<DeleteOptions>()), Times.Never);
    }

    private void VerifyNoSessionOpened() =>
        MockSecureSessionFactory.Verify(f => f.GetSessionAsync(It.IsAny<RtSecurityContext>()), Times.Never);

    private static void AssertRefusal(bool isSuccess, string? errorCode, IReadOnlyDictionary<string, string>? parameters,
        string? message, string expectedType, string expectedTool)
    {
        isSuccess.Should().BeFalse();
        errorCode.Should().Be(Code);
        parameters.Should().NotBeNull();
        parameters!["ckTypeId"].Should().Be(expectedType);
        parameters["tool"].Should().Be(expectedTool);
        message.Should().NotContain(Canary, "the refusal carries ids only, never entity data");
    }

    public static TheoryData<string, string, string?> TypesAndTenants => new()
    {
        { PageFullId, "System.Ai/KnowledgePage", null },
        { PageFullId, "System.Ai/KnowledgePage", DefaultTestTenantId },
        { VersionFullId, "System.Ai/KnowledgePageVersion", null },
        { VersionFullId, "System.Ai/KnowledgePageVersion", DefaultTestTenantId },
        { "System.Ai/KnowledgePage", "System.Ai/KnowledgePage", null }
    };

    // ── create ──────────────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(TypesAndTenants))]
    public async Task CreateEntity_KnowledgeType_IsRefused(string requested, string runtimeId, string? tenantId)
    {
        GivenType(requested.Contains('-') ? requested : PageFullId);

        var result = await RuntimeEntityCrudTools.CreateEntity(MockServer.Object, requested,
            [Item("Summary", $"\"{Canary}\"")], tenantId);

        AssertRefusal(result.IsSuccess, result.ErrorCode, result.ErrorParameters, result.ErrorMessage, runtimeId,
            "knowledge_upsert_page,knowledge_set_status,knowledge_restore_version,knowledge_link");
        result.ErrorMessage.Should().Contain("knowledge_upsert_page");
        result.RtId.Should().BeNull();
        VerifyNothingWritten();
        VerifyNoSessionOpened();
    }

    [Theory]
    [MemberData(nameof(TypesAndTenants))]
    public async Task CreateEntityWithSecrets_KnowledgeType_IsRefused(string requested, string runtimeId,
        string? tenantId)
    {
        GivenType(requested.Contains('-') ? requested : PageFullId);

        var result = await RuntimeEntityCrudTools.CreateEntityWithSecrets(MockServer.Object, requested,
            [Item("Summary", $"\"{Canary}\"")], [Item("ApiKey", "\"sk-fake-8568\"")], tenantId);

        AssertRefusal(result.IsSuccess, result.ErrorCode, result.ErrorParameters, result.ErrorMessage, runtimeId,
            "knowledge_upsert_page,knowledge_set_status,knowledge_restore_version,knowledge_link");
        JsonSerializer.Serialize(result).Should().NotContain("sk-fake-8568");
        VerifyNothingWritten();
        VerifyNoSessionOpened();
    }

    [Fact]
    public async Task CreateEntity_TypeDerivedFromKnowledgePage_IsRefused()
    {
        GivenType(DerivedFullId, PageFullId);

        var result = await RuntimeEntityCrudTools.CreateEntity(MockServer.Object, DerivedFullId,
            [Item("Summary", $"\"{Canary}\"")]);

        result.ErrorCode.Should().Be(Code);
        result.ErrorParameters!["ckTypeId"].Should().Be("Custom/SpecialPage");
        VerifyNothingWritten();
    }

    // ── update / set secrets ────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(TypesAndTenants))]
    public async Task UpdateEntity_KnowledgeType_IsRefusedBeforeASessionIsOpened(string requested, string runtimeId,
        string? tenantId)
    {
        GivenType(requested.Contains('-') ? requested : PageFullId);
        var stored = GivenStoredEntity(runtimeId);

        var result = await RuntimeEntityCrudTools.UpdateEntity(MockServer.Object, stored.RtId.ToString(), requested,
            [Item("Summary", $"\"{Canary}\"")], tenantId: tenantId);

        AssertRefusal(result.IsSuccess, result.ErrorCode, result.ErrorParameters, result.ErrorMessage, runtimeId,
            "knowledge_upsert_page,knowledge_set_status,knowledge_restore_version,knowledge_link");
        result.Entity.Should().BeNull();
        stored.GetAttributeStringValueOrDefault("Summary").Should().Be("original summary");
        VerifyNothingWritten();
        VerifyNoSessionOpened();
    }

    [Theory]
    [MemberData(nameof(TypesAndTenants))]
    public async Task SetEntitySecrets_KnowledgeType_IsRefused(string requested, string runtimeId, string? tenantId)
    {
        GivenType(requested.Contains('-') ? requested : PageFullId);
        var stored = GivenStoredEntity(runtimeId);

        var result = await RuntimeEntityCrudTools.SetEntitySecrets(MockServer.Object, stored.RtId.ToString(),
            requested, [Item("ApiKey", "\"sk-fake-8568\"")], tenantId: tenantId);

        AssertRefusal(result.IsSuccess, result.ErrorCode, result.ErrorParameters, result.ErrorMessage, runtimeId,
            "knowledge_upsert_page,knowledge_set_status,knowledge_restore_version,knowledge_link");
        VerifyNothingWritten();
        VerifyNoSessionOpened();
    }

    [Fact]
    public async Task UpdateEntity_ThroughABaseTypeOnAStoredPage_IsRefusedByTheActualType()
    {
        // The caller names a base type; the stored entity is a KnowledgePage. The second check (actual type) refuses
        // inside the transaction and aborts it.
        GivenType(BaseFullId);
        var stored = GivenStoredEntity("System.Ai/KnowledgePage");

        var result = await RuntimeEntityCrudTools.UpdateEntity(MockServer.Object, stored.RtId.ToString(), BaseFullId,
            [Item("Summary", $"\"{Canary}\"")]);

        result.ErrorCode.Should().Be(Code);
        result.ErrorParameters!["ckTypeId"].Should().Be("System.Ai/KnowledgePage");
        MockSession.Verify(s => s.AbortTransactionAsync(), Times.Once);
        VerifyNothingWritten();
    }

    // ── delete ──────────────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(TypesAndTenants))]
    public async Task DeleteEntity_KnowledgeType_IsRefusedAndNamesRejected(string requested, string runtimeId,
        string? tenantId)
    {
        GivenType(requested.Contains('-') ? requested : PageFullId);
        var stored = GivenStoredEntity(runtimeId);

        var result = await RuntimeEntityCrudTools.DeleteEntity(MockServer.Object, requested, stored.RtId.ToString(),
            tenantId: tenantId);

        AssertRefusal(result.IsSuccess, result.ErrorCode, result.ErrorParameters, result.ErrorMessage, runtimeId,
            "none");
        result.ErrorMessage.Should().Contain("Rejected").And.Contain("knowledge_set_status")
            .And.Contain("outside MCP");
        VerifyNothingWritten();
        VerifyNoSessionOpened();
    }

    [Fact]
    public async Task DeleteEntity_ThroughABaseTypeOnAStoredVersion_IsRefusedByTheActualType()
    {
        GivenType(BaseFullId);
        var stored = GivenStoredEntity("System.Ai/KnowledgePageVersion");

        var result = await RuntimeEntityCrudTools.DeleteEntity(MockServer.Object, BaseFullId, stored.RtId.ToString());

        result.ErrorCode.Should().Be(Code);
        MockSession.Verify(s => s.AbortTransactionAsync(), Times.Once);
        VerifyNothingWritten();
    }

    // ── other types and reads are unchanged ─────────────────────────────────

    [Fact]
    public async Task DeleteEntity_OtherType_StillDeletes()
    {
        GivenType("System.Ai-4.7.0/SourceArtifact-1");
        var stored = GivenStoredEntity("System.Ai/SourceArtifact");

        var result = await RuntimeEntityCrudTools.DeleteEntity(MockServer.Object, "System.Ai-4.7.0/SourceArtifact-1",
            stored.RtId.ToString());

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        result.ErrorCode.Should().BeNull();
        MockTenantRepository.Verify(r => r.DeleteOneRtEntityByRtIdAsync(It.IsAny<IOctoSession>(),
            It.IsAny<RtCkId<CkTypeId>>(), It.IsAny<OctoObjectId>(), It.IsAny<DeleteOptions>()), Times.Once);
    }

    [Fact]
    public async Task GetEntityById_KnowledgePage_StillReads()
    {
        GivenType(PageFullId);
        var stored = GivenStoredEntity("System.Ai/KnowledgePage");

        var result = await RuntimeEntityCrudTools.GetEntityById(MockServer.Object, PageFullId, stored.RtId.ToString());

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        result.Entity.Should().NotBeNull();
    }

    [Fact]
    public async Task QueryEntities_KnowledgePage_StillReads()
    {
        GivenType(PageFullId);
        GivenStoredEntity("System.Ai/KnowledgePage");

        var result = await RuntimeEntityCrudTools.QueryEntities(MockServer.Object, PageFullId);

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
    }

    [Fact]
    public void ToolDescriptions_NameTheRefusal()
    {
        foreach (var name in new[]
                 {
                     nameof(RuntimeEntityCrudTools.CreateEntity), nameof(RuntimeEntityCrudTools.CreateEntityWithSecrets),
                     nameof(RuntimeEntityCrudTools.UpdateEntity), nameof(RuntimeEntityCrudTools.SetEntitySecrets),
                     nameof(RuntimeEntityCrudTools.DeleteEntity)
                 })
        {
            Description(typeof(RuntimeEntityCrudTools), name).Should().Contain(Code).And.Contain("KnowledgePage");
        }

        Description(typeof(RuntimeEntityCrudTools), nameof(RuntimeEntityCrudTools.DeleteEntity))
            .Should().Contain("Rejected").And.Contain("outside MCP");
        Description(typeof(CkModelFileTools), nameof(CkModelFileTools.ImportRuntimeModel)).Should().Contain(Code);
    }

    private static string Description(Type type, string method) =>
        type.GetMethod(method)!.GetCustomAttributes(typeof(DescriptionAttribute), false)
            .Cast<DescriptionAttribute>().Single().Description;
}

/// <summary>
///     AB#8568: <c>import_runtime_model</c> (the bulk write path) refuses files that write knowledge pages, versions or
///     their links. The check reads the decoded ids (importer's JSON reader, YAML parser events), so escapes, quoting,
///     aliases and casing do not hide a reference; derived types are found through the CK type hierarchy; a file that
///     cannot be checked is refused with a generic message.
/// </summary>
public class KnowledgeRuntimeImportRefusalTests : ToolTestBase
{
    private const string Code = "GENERIC_WRITE_REPLACED_BY_KNOWLEDGE_TOOL";
    private const string PageFullId = "System.Ai-4.7.0/KnowledgePage-1";
    private const string DerivedFullId = "Custom-1.0.0/SpecialPage-1";
    private const string AssetFullId = "Basic-2.0.0/Asset-1";

    private readonly Dictionary<RtCkId<CkTypeId>, CkTypeGraph> _knownTypes = new();

    private delegate bool TryGetTypeCallback(string tenantId, RtCkId<CkTypeId> ckTypeId, out CkTypeGraph? typeGraph);

    public KnowledgeRuntimeImportRefusalTests()
    {
        GivenAuthenticated();
        MockCkCacheService
            .Setup(c => c.TryGetRtCkType(It.IsAny<string>(), It.IsAny<RtCkId<CkTypeId>>(),
                out It.Ref<CkTypeGraph?>.IsAny))
            .Returns(new TryGetTypeCallback((string _, RtCkId<CkTypeId> id, out CkTypeGraph? graph) =>
                _knownTypes.TryGetValue(id, out graph)));
    }

    /// <summary>The tenant has System.Ai (KnowledgePage), Custom/SpecialPage derived from it and Basic/Asset.</summary>
    private void GivenKnowledgeTenant()
    {
        GivenType(PageFullId);
        GivenType(DerivedFullId, PageFullId);
        GivenType(AssetFullId);
    }

    private void GivenType(string fullId, params string[] baseTypeIds)
    {
        var type = CkGraphTestBuilder.BuildTypeWithAccess(fullId, [], baseTypeIds, [],
            ("Summary", AttributeValueTypesDto.String, null, null));
        _knownTypes[new CkId<CkTypeId>(fullId).ToRtCkId()] = type;
    }

    private string StageUpload(string content, string fileName = "model.yaml")
    {
        var (id, path) = FileTransferStore.ReserveUpload(CallerSession, fileName);
        File.WriteAllText(path, content);
        FileTransferStore.CompleteUpload(id, content.Length);
        return id;
    }

    private string StageZipUpload(string entryName, string entryContent)
    {
        var (id, path) = FileTransferStore.ReserveUpload(CallerSession, "model.zip");
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            using var writer = new StreamWriter(archive.CreateEntry(entryName).Open());
            writer.Write(entryContent);
        }

        FileTransferStore.CompleteUpload(id, new FileInfo(path).Length);
        return id;
    }

    private void GivenImportSucceeds()
    {
        MockAssetClient.Setup(c => c.ImportRtModelAsync(DefaultTenantId,
                Meshmakers.Octo.Communication.Contracts.DataTransferObjects.ImportStrategyDto.InsertOnly,
                It.IsAny<string>()))
            .ReturnsAsync("job-ok");
        MockBotClient.Setup(c => c.GetImportJobStatus("job-ok"))
            .ReturnsAsync(new Meshmakers.Octo.Communication.Contracts.DataTransferObjects.JobDto
            {
                Id = "job-ok", Status = "Succeeded"
            });
    }

    private void VerifyNothingImported() =>
        MockAssetClient.Verify(c => c.ImportRtModelAsync(It.IsAny<string>(),
            It.IsAny<Meshmakers.Octo.Communication.Contracts.DataTransferObjects.ImportStrategyDto>(),
            It.IsAny<string>()), Times.Never);

    private const string PageModel = """
                                     $schema: https://schemas.meshmakers.cloud/runtime-model.schema.json
                                     entities:
                                       - rtId: 67a1000000000000000a1c99
                                         ckTypeId: System.Ai/KnowledgePage
                                         rtWellKnownName: bypass
                                     """;

    private const string LinkOnlyModel = """
                                         $schema: https://schemas.meshmakers.cloud/runtime-model.schema.json
                                         entities:
                                           - rtId: 67a1000000000000000a1c98
                                             ckTypeId: Basic/Asset
                                             associations:
                                               - roleId: System.Ai-4.7.0/GeneratedFrom
                                                 targetRtId: 67a1000000000000000a1c97
                                                 targetCkTypeId: Basic/Asset
                                         """;

    private static string Json(string ckTypeIdLiteral, string key = "ckTypeId") =>
        "{\"$schema\":\"https://schemas.meshmakers.cloud/runtime-model.schema.json\",\"dependencies\":[]," +
        "\"entities\":[{\"rtId\":\"67a1000000000000000a1c96\",\"" + key + "\":\"" + ckTypeIdLiteral + "\"}]}";

    public static TheoryData<string, string, string> EncodedKnowledgeReferences => new()
    {
        // YAML: plain, link only, double-quoted escapes, single-quoted, folded scalar, other casing, version suffix.
        { "model.yaml", PageModel, "System.Ai/KnowledgePage" },
        { "model.yaml", LinkOnlyModel, "System.Ai/GeneratedFrom" },
        { "model.yaml", "entities:\n  - ckTypeId: \"System.Ai\\x2FKnowledgePage\"\n", "System.Ai/KnowledgePage" },
        { "model.yaml", "entities:\n  - ckTypeId: \"System.Ai\\u002FKnowledgePageVersion\"\n", "System.Ai/KnowledgePageVersion" },
        { "model.yaml", "entities:\n  - ckTypeId: 'System.Ai/KnowledgePage'\n", "System.Ai/KnowledgePage" },
        { "model.yaml", "entities:\n  - ckTypeId: >-\n      System.Ai/KnowledgePage\n", "System.Ai/KnowledgePage" },
        { "model.yaml", "entities:\n  - ckTypeId: system.ai/knowledgepage\n", "System.Ai/KnowledgePage" },
        { "model.yaml", "entities:\n  - ckTypeId: System.Ai-4.7.0-beta/KnowledgePage-1\n", "System.Ai/KnowledgePage" },
        { "model.yaml", "entities:\n  - \"ckType\\x49d\": System.Ai/KnowledgePage\n", "System.Ai/KnowledgePage" },
        // JSON in a YAML file is read by the YAML reader, as the importer does.
        { "model.yaml", Json("System.Ai-4.7.0/KnowledgePageVersion-1"), "System.Ai/KnowledgePageVersion" },
        // JSON: escaped slash, unicode escape, other casing of the value and of the key, full id.
        { "model.json", Json("System.Ai\\/KnowledgePage"), "System.Ai/KnowledgePage" },
        { "model.json", Json("System.Ai\\u002FKnowledgePage"), "System.Ai/KnowledgePage" },
        { "model.json", Json("SYSTEM.AI/KNOWLEDGEPAGE"), "System.Ai/KnowledgePage" },
        { "model.json", Json("System.Ai/KnowledgePage", key: "CkTypeId"), "System.Ai/KnowledgePage" },
        { "model.json", Json("System.Ai-4.7.0/KnowledgePageVersion-1"), "System.Ai/KnowledgePageVersion" }
    };

    [Theory]
    [MemberData(nameof(EncodedKnowledgeReferences))]
    public async Task ImportRuntimeModel_WithAnEncodedKnowledgeReference_IsRefusedBeforeTheService(string fileName,
        string content, string expectedReference)
    {
        var transferId = StageUpload(content, fileName);

        var result = await CkModelFileTools.ImportRuntimeModel(MockServer.Object, transferId);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(Code);
        result.ErrorMessage.Should().Contain(expectedReference).And.Contain("Nothing was imported");
        VerifyNothingImported();
    }

    [Theory]
    [InlineData("model.yaml", "entities:\n  - ckTypeId: Custom/SpecialPage\n")]
    [InlineData("model.json", "{\"$schema\":\"https://schemas.meshmakers.cloud/runtime-model.schema.json\",\"entities\":[{\"ckTypeId\":\"Custom\\/SpecialPage\"}]}")]
    [InlineData("model.yaml", "entities:\n  - ckTypeId: Basic/Asset\n    associations:\n      - roleId: Basic/RelatedTo\n        targetCkTypeId: Custom-1.0.0/SpecialPage-1\n")]
    // The key is written with an escape ("ckTypeId" once decoded).
    [InlineData("model.yaml", "entities:\n  - \"ckType\\x49d\": Custom/SpecialPage\n")]
    // The alias resolves to the derived type; its anchor sits on an unrelated key.
    [InlineData("model.yaml", "note: &t Custom/SpecialPage\nentities:\n  - ckTypeId: *t\n")]
    public async Task ImportRuntimeModel_WithATypeDerivedFromKnowledgePage_IsRefused(string fileName, string content)
    {
        GivenKnowledgeTenant();
        var transferId = StageUpload(content, fileName);

        var result = await CkModelFileTools.ImportRuntimeModel(MockServer.Object, transferId);

        result.ErrorCode.Should().Be(Code);
        result.ErrorMessage.Should().Contain("Custom/SpecialPage").And.Contain("Nothing was imported");
        VerifyNothingImported();
    }

    [Fact]
    public async Task ImportRuntimeModel_UnknownTypeInAKnowledgeTenant_IsRefused()
    {
        GivenKnowledgeTenant();
        var transferId = StageUpload("entities:\n  - ckTypeId: custom/specialpage\n");

        var result = await CkModelFileTools.ImportRuntimeModel(MockServer.Object, transferId);

        result.ErrorCode.Should().Be(Code);
        result.ErrorMessage.Should().Contain("does not know").And.Contain("Nothing was imported");
        VerifyNothingImported();
    }

    [Theory]
    [InlineData("rt-model.yaml", PageModel)]
    [InlineData("nested/rt-model.JSON", "{\"$schema\":\"https://schemas.meshmakers.cloud/runtime-model.schema.json\",\"entities\":[{\"ckTypeId\":\"System.Ai\\u002FKnowledgePage\"}]}")]
    public async Task ImportRuntimeModel_ZipWithKnowledgePage_IsRefused(string entryName, string entryContent)
    {
        var transferId = StageZipUpload(entryName, entryContent);

        var result = await CkModelFileTools.ImportRuntimeModel(MockServer.Object, transferId);

        result.ErrorCode.Should().Be(Code);
        result.ErrorMessage.Should().Contain("System.Ai/KnowledgePage");
        VerifyNothingImported();
    }

    [Fact]
    public async Task ImportRuntimeModel_CorruptZip_IsRefusedWithAGenericMessage_AndDoesNotThrow()
    {
        var (id, path) = FileTransferStore.ReserveUpload(CallerSession, "model.zip");
        File.WriteAllBytes(path, [(byte)'P', (byte)'K', 3, 4, 0, 1, 2, 3, 4, 5, 6, 7]);
        FileTransferStore.CompleteUpload(id, 12);

        var result = await CkModelFileTools.ImportRuntimeModel(MockServer.Object, id);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(Code);
        result.ErrorMessage.Should().Be(KnowledgeWriteRefusal.ImportCheckFailedMessage);
        result.ErrorMessage.Should().NotContain(Path.GetDirectoryName(path)!).And.NotContain("Exception");
        VerifyNothingImported();
    }

    [Theory]
    [InlineData("model.json", "{\"entities\":[{\"ckTypeId\":")]
    [InlineData("model.yaml", "entities:\n  - ckTypeId: [unclosed\n")]
    // The importer's JSON reader requires the leading $schema; without it the import would fail too.
    [InlineData("model.json", "{\"entities\":[{\"ckTypeId\":\"Basic/Asset\"}]}")]
    public async Task ImportRuntimeModel_UnparsableFile_IsRefused(string fileName, string content)
    {
        var transferId = StageUpload(content, fileName);

        var result = await CkModelFileTools.ImportRuntimeModel(MockServer.Object, transferId);

        result.ErrorCode.Should().Be(Code);
        result.ErrorMessage.Should().Be(KnowledgeWriteRefusal.ImportCheckFailedMessage);
        VerifyNothingImported();
    }

    [Fact]
    public async Task ImportRuntimeModel_UploadFileMissing_IsRefused_NotImportedUnchecked()
    {
        var (id, path) = FileTransferStore.ReserveUpload(CallerSession, "model.yaml");
        File.WriteAllText(path, PageModel);
        FileTransferStore.CompleteUpload(id, PageModel.Length);
        File.Delete(path);

        var result = await CkModelFileTools.ImportRuntimeModel(MockServer.Object, id);

        result.ErrorCode.Should().Be(Code);
        result.ErrorMessage.Should().Be(KnowledgeWriteRefusal.ImportCheckFailedMessage);
        VerifyNothingImported();
    }

    [Fact]
    public async Task Check_RefusesAZipEntryAboveTheCap()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ab8568-{Guid.NewGuid():N}.zip");
        try
        {
            using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                using var writer = new StreamWriter(archive.CreateEntry("rt.yaml").Open());
                writer.Write("entities:\n" + string.Concat(Enumerable.Repeat("  - ckTypeId: Basic/Asset\n", 1000)));
            }

            var check = new KnowledgeImportCheck(
                    new ServiceCollection().AddRuntimeEngine().Services.BuildServiceProvider()
                        .GetRequiredService<IRtJsonSerializer>(), MockCkCacheService.Object, DefaultTenantId)
                .WithEntryBytesLimit(1024);

            var act = () => check.FindRefusalAsync(path);

            await act.Should().ThrowAsync<InvalidDataException>();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ImportRuntimeModel_LargeJson_IsReadInBulks_AndAPageAtTheEndIsFound()
    {
        var entities = string.Join(",", Enumerable.Range(0, 12_000)
            .Select(i => $"{{\"rtId\":\"67a1000000000000{i:x8}\",\"ckTypeId\":\"Basic/Asset\"}}"));
        var transferId = StageUpload(
            "{\"$schema\":\"https://schemas.meshmakers.cloud/runtime-model.schema.json\",\"entities\":[" + entities + ",{\"ckTypeId\":\"System.Ai/KnowledgePage\"}]}", "model.json");

        var result = await CkModelFileTools.ImportRuntimeModel(MockServer.Object, transferId);

        result.ErrorCode.Should().Be(Code);
        result.ErrorMessage.Should().Contain("System.Ai/KnowledgePage");
    }

    [Theory]
    // A tenant without knowledge types: an unknown type is left to the importer.
    [InlineData(false, "model.yaml", "entities:\n  - ckTypeId: System.Ai/SourceArtifact\n  - ckTypeId: Basic/KnowledgePageLike\n")]
    // Free text that mentions a knowledge id is not a write of one.
    [InlineData(true, "model.yaml", "entities:\n  - ckTypeId: Basic/Asset\n    attributes:\n      - id: summary\n        value: see System.Ai/KnowledgePage for the page model\n")]
    [InlineData(true, "model.json", "{\"$schema\":\"https://schemas.meshmakers.cloud/runtime-model.schema.json\",\"entities\":[{\"rtId\":\"67a1000000000000000a1c95\",\"ckTypeId\":\"Basic/Asset\"}]}")]
    public async Task ImportRuntimeModel_WithoutKnowledgeReference_IsImported(bool knowledgeTenant, string fileName,
        string content)
    {
        if (knowledgeTenant)
        {
            GivenKnowledgeTenant();
        }

        var transferId = StageUpload(content, fileName);
        GivenImportSucceeds();

        var result = await CkModelFileTools.ImportRuntimeModel(MockServer.Object, transferId);

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
    }

    [Theory]
    [InlineData("System.Ai/KnowledgePage", true)]
    [InlineData(" system.ai-4.7.0/KNOWLEDGEPAGE-1 ", true)]
    [InlineData("System.Ai-4.7.0-beta/KnowledgePage", true)]
    [InlineData("System.Ai/KnowledgePageLike", false)]
    [InlineData("Basic/KnowledgePage", false)]
    [InlineData("System.Ai/", false)]
    public void IsProtectedName_NormalisesVersionsAndCasing(string id, bool expected)
    {
        KnowledgeImportCheck.IsProtectedName(id, ["KnowledgePage", "KnowledgePageVersion"], out _).Should().Be(expected);
    }
}
