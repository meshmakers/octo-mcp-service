using System.ComponentModel;
using System.IO.Compression;
using System.Text.Json;
using FluentAssertions;
using Meshmakers.Octo.Backend.McpServices.Models;
using Meshmakers.Octo.Backend.McpServices.Tools;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Engine.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
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

/// <summary>AB#8568: <c>import_runtime_model</c> (the bulk write path) refuses files that reference knowledge types.</summary>
public class KnowledgeRuntimeImportRefusalTests : ToolTestBase
{
    private const string Code = "GENERIC_WRITE_REPLACED_BY_KNOWLEDGE_TOOL";

    public KnowledgeRuntimeImportRefusalTests()
    {
        GivenAuthenticated();
    }

    private string StageUpload(string content, string fileName = "model.yaml")
    {
        var (id, path) = FileTransferStore.ReserveUpload(CallerSession, fileName);
        File.WriteAllText(path, content);
        FileTransferStore.CompleteUpload(id, content.Length);
        return id;
    }

    private string StageZipUpload(string entryContent)
    {
        var (id, path) = FileTransferStore.ReserveUpload(CallerSession, "model.zip");
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            using var writer = new StreamWriter(archive.CreateEntry("rt-model.yaml").Open());
            writer.Write(entryContent);
        }

        FileTransferStore.CompleteUpload(id, new FileInfo(path).Length);
        return id;
    }

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
                                         """;

    [Theory]
    [InlineData(PageModel, "System.Ai/KnowledgePage")]
    [InlineData(LinkOnlyModel, "System.Ai/GeneratedFrom")]
    [InlineData("{\"entities\":[{\"ckTypeId\":\"System.Ai-4.7.0/KnowledgePageVersion-1\"}]}", "System.Ai/KnowledgePageVersion")]
    public async Task ImportRuntimeModel_WithKnowledgeReference_IsRefusedBeforeTheService(string content,
        string expectedReference)
    {
        var transferId = StageUpload(content);

        var result = await CkModelFileTools.ImportRuntimeModel(MockServer.Object, transferId);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(Code);
        result.ErrorMessage.Should().Contain(expectedReference).And.Contain("Nothing was imported");
        MockAssetClient.Verify(c => c.ImportRtModelAsync(It.IsAny<string>(),
            It.IsAny<Meshmakers.Octo.Communication.Contracts.DataTransferObjects.ImportStrategyDto>(),
            It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ImportRuntimeModel_ZipWithKnowledgePage_IsRefused()
    {
        var transferId = StageZipUpload(PageModel);

        var result = await CkModelFileTools.ImportRuntimeModel(MockServer.Object, transferId);

        result.ErrorCode.Should().Be(Code);
        MockAssetClient.Verify(c => c.ImportRtModelAsync(It.IsAny<string>(),
            It.IsAny<Meshmakers.Octo.Communication.Contracts.DataTransferObjects.ImportStrategyDto>(),
            It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ImportRuntimeModel_WithoutKnowledgeReference_IsImported()
    {
        var transferId = StageUpload("""
                                     entities:
                                       - ckTypeId: System.Ai/SourceArtifact
                                       - ckTypeId: Basic/KnowledgePageLike
                                     """);
        MockAssetClient.Setup(c => c.ImportRtModelAsync(DefaultTenantId,
                Meshmakers.Octo.Communication.Contracts.DataTransferObjects.ImportStrategyDto.InsertOnly,
                It.IsAny<string>()))
            .ReturnsAsync("job-ok");
        MockBotClient.Setup(c => c.GetImportJobStatus("job-ok"))
            .ReturnsAsync(new Meshmakers.Octo.Communication.Contracts.DataTransferObjects.JobDto
            {
                Id = "job-ok", Status = "Succeeded"
            });

        var result = await CkModelFileTools.ImportRuntimeModel(MockServer.Object, transferId);

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
    }
}
