using System.Text.Json;
using FluentAssertions;
using Meshmakers.Octo.Backend.McpServices.Models;
using Meshmakers.Octo.Backend.McpServices.Models.Aggregation;
using Meshmakers.Octo.Backend.McpServices.Models.Filters;
using Meshmakers.Octo.Backend.McpServices.Tools;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Contracts.Secrets;
using Moq;
using Xunit;

namespace McpServices.Tests.Tools;

/// <summary>
///     AB#5543: Secret attributes in the generic CRUD, query and aggregation tools. Secret values are never set
///     by the medium-risk tools (only by the high-risk set_entity_secrets), "unchanged" inputs are dropped,
///     clearing is explicit, and queries on secrets are refused with SecretAttributeNotQueryable.
/// </summary>
public class RuntimeEntityCrudToolsSecretTests : TestBase
{
    private const string TestCkTypeId = "TestModule-1.0.0/Credential-1";
    private const string FakeSecret = "fake-test-secret-value";
    private static readonly CkId<CkRecordId> EndpointRecordId = new("TestModule-1/Endpoint-1");

    public RuntimeEntityCrudToolsSecretTests()
    {
        var type = CkGraphTestBuilder.BuildType("TestModule-1/Credential-1",
            ("Name", AttributeValueTypesDto.String, null),
            ("Password", AttributeValueTypesDto.Secret, null),
            ("ApiKey", AttributeValueTypesDto.Secret, null),
            ("Endpoints", AttributeValueTypesDto.RecordArray, EndpointRecordId));
        var record = CkGraphTestBuilder.BuildRecord(EndpointRecordId,
            ("Key", AttributeValueTypesDto.String, null),
            ("Token", AttributeValueTypesDto.Secret, null));

        MockCkCacheService
            .Setup(c => c.GetRtCkType(It.IsAny<string>(), It.IsAny<RtCkId<CkTypeId>>()))
            .Returns(type);
        CkGraphTestBuilder.SetupTryGetCkRecord(MockCkCacheService, EndpointRecordId, record);
        CkGraphTestBuilder.SetupTryGetRtCkType(MockCkCacheService, type);
    }

    private static AttributeUpdateItem Item(string path, string json) =>
        new() { AttributePath = path, Value = JsonDocument.Parse(json).RootElement.Clone() };

    private RtEntity GivenStoredEntity(ulong version = 3)
    {
        var entity = new RtEntity
        {
            RtId = OctoObjectId.GenerateNewId(),
            CkTypeId = new RtCkId<CkTypeId>(TestCkTypeId),
            RtVersion = version
        };
        MockTenantRepository
            .Setup(r => r.GetRtEntityByRtIdAsync(It.IsAny<IOctoSession>(), It.IsAny<RtEntityId>()))
            .ReturnsAsync(entity);
        return entity;
    }

    private void VerifyNoWrite()
    {
        MockTenantRepository.Verify(r => r.UpdateOneRtEntityByIdAsync(It.IsAny<IOctoSession>(),
            It.IsAny<RtCkId<CkTypeId>>(), It.IsAny<OctoObjectId>(), It.IsAny<RtEntity>()), Times.Never);
        MockTenantRepository.Verify(r => r.ApplyChangesAsync(It.IsAny<IOctoSession>(),
            It.IsAny<IReadOnlyList<IEntityUpdateInfo<RtEntity>>>(), It.IsAny<OperationResult>()), Times.Never);
    }

    // ── update_entity ──────────────────────────────────────────────────────

    [Fact]
    public async Task UpdateEntity_SecretValue_IsRefusedWithPointerToSetEntitySecrets()
    {
        var entity = GivenStoredEntity();

        var result = await RuntimeEntityCrudTools.UpdateEntity(MockServer.Object, entity.RtId.ToString(),
            TestCkTypeId, [Item("password", $"\"{FakeSecret}\"")]);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("set_entity_secrets").And.NotContain(FakeSecret);
        MockSecureSessionFactory.Verify(f => f.GetSessionAsync(It.IsAny<RtSecurityContext>()), Times.Never);
        VerifyNoWrite();
    }

    [Fact]
    public async Task UpdateEntity_SecretInsideRecord_IsRefused()
    {
        var entity = GivenStoredEntity();

        var result = await RuntimeEntityCrudTools.UpdateEntity(MockServer.Object, entity.RtId.ToString(),
            TestCkTypeId, [Item("Endpoints[0].token", $"\"{FakeSecret}\"")]);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Secret attribute");
        VerifyNoWrite();
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"\"")]
    [InlineData("{\"isSet\":true}")]
    public async Task UpdateEntity_UnchangedSecretInput_IsDroppedAndOtherAttributesAreWritten(string json)
    {
        var entity = GivenStoredEntity();

        var result = await RuntimeEntityCrudTools.UpdateEntity(MockServer.Object, entity.RtId.ToString(),
            TestCkTypeId, [Item("Password", json), Item("Name", "\"renamed\"")]);

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        MockTenantRepository.Verify(r => r.UpdateOneRtEntityByIdAsync(It.IsAny<IOctoSession>(),
            It.IsAny<RtCkId<CkTypeId>>(), It.IsAny<OctoObjectId>(),
            It.Is<RtEntity>(e => !e.Attributes.ContainsKey("Password") &&
                                 (string?)e.Attributes["Name"] == "renamed")), Times.Once);
    }

    [Fact]
    public async Task UpdateEntity_ClearSecretAttributes_GoesThroughApplyChangesWithPascalCaseNames()
    {
        var entity = GivenStoredEntity();

        var result = await RuntimeEntityCrudTools.UpdateEntity(MockServer.Object, entity.RtId.ToString(),
            TestCkTypeId, [], clearSecretAttributes: ["password"]);

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        MockTenantRepository.Verify(r => r.ApplyChangesAsync(It.IsAny<IOctoSession>(),
            It.Is<IReadOnlyList<IEntityUpdateInfo<RtEntity>>>(l =>
                l.Count == 1 && l[0].ModOption == EntityModOptions.Update &&
                l[0].ClearSecretAttributes != null && l[0].ClearSecretAttributes!.SequenceEqual(new[] { "Password" })),
            It.IsAny<OperationResult>()), Times.Once);
        MockTenantRepository.Verify(r => r.UpdateOneRtEntityByIdAsync(It.IsAny<IOctoSession>(),
            It.IsAny<RtCkId<CkTypeId>>(), It.IsAny<OctoObjectId>(), It.IsAny<RtEntity>()), Times.Never);
    }

    [Fact]
    public async Task UpdateEntity_ClearRejectedByEngine_SurfacesMessageAndAborts()
    {
        var entity = GivenStoredEntity();
        MockTenantRepository
            .Setup(r => r.ApplyChangesAsync(It.IsAny<IOctoSession>(),
                It.IsAny<IReadOnlyList<IEntityUpdateInfo<RtEntity>>>(), It.IsAny<OperationResult>()))
            .ThrowsAsync(new InvalidOperationException("22: Secret attribute 'Password' is required and cannot be cleared."));

        var result = await RuntimeEntityCrudTools.UpdateEntity(MockServer.Object, entity.RtId.ToString(),
            TestCkTypeId, [], clearSecretAttributes: ["Password"]);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("22:");
        MockSession.Verify(s => s.AbortTransactionAsync(), Times.Once);
    }

    [Fact]
    public async Task UpdateEntity_ResponseNeverContainsSecretValue_OnlySecretIsSet()
    {
        var entity = GivenStoredEntity();
        // A value the engine would hold after a write — the mapper must project "is set" only.
        entity.SetAttributeValue("Password", AttributeValueTypesDto.Secret, FakeSecret);

        var result = await RuntimeEntityCrudTools.UpdateEntity(MockServer.Object, entity.RtId.ToString(),
            TestCkTypeId, [Item("Name", "\"n\"")]);

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        var password = result.Entity!.Attributes!.Single(a =>
            string.Equals(a.AttributeName, "password", StringComparison.OrdinalIgnoreCase));
        password.Value.Should().BeNull();
        password.SecretIsSet.Should().BeTrue();
        JsonSerializer.Serialize(result).Should().NotContain(FakeSecret);
    }

    // ── create_entity ──────────────────────────────────────────────────────

    [Fact]
    public async Task CreateEntity_SecretValue_IsRefused()
    {
        var result = await RuntimeEntityCrudTools.CreateEntity(MockServer.Object, TestCkTypeId,
            [Item("Name", "\"n\""), Item("Password", $"\"{FakeSecret}\"")]);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("set_entity_secrets").And.NotContain(FakeSecret);
        MockTenantRepository.Verify(r => r.InsertOneRtEntityAsync(It.IsAny<IOctoSession>(),
            It.IsAny<RtCkId<CkTypeId>>(), It.IsAny<RtEntity>()), Times.Never);
    }

    [Fact]
    public async Task CreateEntity_PlaceholderLikeSecret_IsAnOrdinaryValueAndRefusedLikeAnyOther()
    {
        var result = await RuntimeEntityCrudTools.CreateEntity(MockServer.Object, TestCkTypeId,
            [Item("Password", "\"TODO_SET_PASSWORD\"")]);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("set_entity_secrets").And.NotContain("placeholder");
    }

    // ── create_entity_with_secrets ─────────────────────────────────────────

    private void GivenTransientEntity()
    {
        MockTenantRepository
            .Setup(r => r.CreateTransientRtEntityAsync(It.IsAny<CkId<CkTypeId>>()))
            .ReturnsAsync(() => new RtEntity
            {
                RtId = OctoObjectId.GenerateNewId(),
                CkTypeId = new RtCkId<CkTypeId>(TestCkTypeId)
            });
    }

    [Fact]
    public async Task CreateEntityWithSecrets_HappyPath_InsertsOnceWithSecretAndReturnsIsSetOnly()
    {
        GivenTransientEntity();

        var result = await RuntimeEntityCrudTools.CreateEntityWithSecrets(MockServer.Object, TestCkTypeId,
            [Item("Name", "\"smtp\"")], [Item("password", $"\"{FakeSecret}\"")]);

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        MockTenantRepository.Verify(r => r.InsertOneRtEntityAsync(It.IsAny<IOctoSession>(),
            It.IsAny<RtCkId<CkTypeId>>(),
            It.Is<RtEntity>(e => (string?)e.Attributes["Name"] == "smtp" && e.Attributes["Password"] is RtSecretValue)),
            Times.Once);
        JsonSerializer.Serialize(result).Should().NotContain(FakeSecret);
        result.Entity!.Attributes!.Single(a =>
                string.Equals(a.AttributeName, "password", StringComparison.OrdinalIgnoreCase))
            .SecretIsSet.Should().BeTrue();
    }

    [Fact]
    public async Task CreateEntityWithSecrets_NoSecrets_PointsToCreateEntity()
    {
        var result = await RuntimeEntityCrudTools.CreateEntityWithSecrets(MockServer.Object, TestCkTypeId,
            [Item("Name", "\"n\"")]);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("create_entity");
        MockSecureSessionFactory.Verify(f => f.GetSessionAsync(It.IsAny<RtSecurityContext>()), Times.Never);
    }

    [Fact]
    public async Task CreateEntityWithSecrets_SecretValueInEntityData_IsRefused()
    {
        GivenTransientEntity();

        var result = await RuntimeEntityCrudTools.CreateEntityWithSecrets(MockServer.Object, TestCkTypeId,
            [Item("ApiKey", $"\"{FakeSecret}\"")], [Item("Password", "\"other-fake\"")]);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().NotContain(FakeSecret);
        MockTenantRepository.Verify(r => r.InsertOneRtEntityAsync(It.IsAny<IOctoSession>(),
            It.IsAny<RtCkId<CkTypeId>>(), It.IsAny<RtEntity>()), Times.Never);
    }

    [Theory]
    [InlineData("Name", "\"n\"")]
    [InlineData("Password", "\"\"")]
    [InlineData("Password", "null")]
    [InlineData("Password", "42")]
    public async Task CreateEntityWithSecrets_InvalidSecretEntry_IsRefused(string path, string json)
    {
        GivenTransientEntity();

        var result = await RuntimeEntityCrudTools.CreateEntityWithSecrets(MockServer.Object, TestCkTypeId,
            [], [Item(path, json)]);

        result.IsSuccess.Should().BeFalse();
        MockTenantRepository.Verify(r => r.InsertOneRtEntityAsync(It.IsAny<IOctoSession>(),
            It.IsAny<RtCkId<CkTypeId>>(), It.IsAny<RtEntity>()), Times.Never);
    }

    [Theory]
    [InlineData("TODO_SET_PASSWORD")]
    [InlineData("<set me>")]
    public async Task CreateEntityWithSecrets_PlaceholderLikeValue_IsWrittenAsOrdinarySecret(string value)
    {
        GivenTransientEntity();

        var result = await RuntimeEntityCrudTools.CreateEntityWithSecrets(MockServer.Object, TestCkTypeId,
            [], [Item("Password", $"\"{value}\"")]);

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        MockTenantRepository.Verify(r => r.InsertOneRtEntityAsync(It.IsAny<IOctoSession>(),
            It.IsAny<RtCkId<CkTypeId>>(), It.Is<RtEntity>(e => e.Attributes["Password"] is RtSecretValue)),
            Times.Once);
    }

    [Fact]
    public async Task CreateEntityWithSecrets_EngineRefusal_AbortsWithoutValue()
    {
        GivenTransientEntity();
        MockTenantRepository
            .Setup(r => r.InsertOneRtEntityAsync(It.IsAny<IOctoSession>(), It.IsAny<RtCkId<CkTypeId>>(),
                It.IsAny<RtEntity>()))
            .ThrowsAsync(new InvalidOperationException("2: Required attribute 'ApiKey' is missing."));

        var result = await RuntimeEntityCrudTools.CreateEntityWithSecrets(MockServer.Object, TestCkTypeId,
            [], [Item("Password", $"\"{FakeSecret}\"")]);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("2:").And.NotContain(FakeSecret);
        MockSession.Verify(s => s.AbortTransactionAsync(), Times.Once);
    }

    [Fact]
    public async Task CreateEntityWithSecrets_Unauthenticated_ReturnsNotAuthenticated()
    {
        GivenUnauthenticatedCaller();

        var result = await RuntimeEntityCrudTools.CreateEntityWithSecrets(MockServer.Object, TestCkTypeId,
            [], [Item("Password", $"\"{FakeSecret}\"")]);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().StartWith("Not authenticated");
        MockTenantRepository.Verify(r => r.InsertOneRtEntityAsync(It.IsAny<IOctoSession>(),
            It.IsAny<RtCkId<CkTypeId>>(), It.IsAny<RtEntity>()), Times.Never);
    }

    // ── set_entity_secrets ─────────────────────────────────────────────────

    [Fact]
    public async Task SetEntitySecrets_HappyPath_WritesSecretAndReturnsIsSetOnly()
    {
        var entity = GivenStoredEntity(version: 7);

        var result = await RuntimeEntityCrudTools.SetEntitySecrets(MockServer.Object, entity.RtId.ToString(),
            TestCkTypeId, [Item("password", $"\"{FakeSecret}\"")], expectedVersion: 7);

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        MockTenantRepository.Verify(r => r.UpdateOneRtEntityByIdAsync(It.IsAny<IOctoSession>(),
            It.IsAny<RtCkId<CkTypeId>>(), It.IsAny<OctoObjectId>(),
            It.Is<RtEntity>(e => e.RtVersion == 8 && e.Attributes["Password"] is RtSecretValue)), Times.Once);
        JsonSerializer.Serialize(result).Should().NotContain(FakeSecret);
        result.Entity!.Attributes!.Single(a =>
                string.Equals(a.AttributeName, "password", StringComparison.OrdinalIgnoreCase))
            .SecretIsSet.Should().BeTrue();
    }

    [Fact]
    public async Task SetEntitySecrets_SetAndClear_UsesApplyChanges()
    {
        var entity = GivenStoredEntity();

        var result = await RuntimeEntityCrudTools.SetEntitySecrets(MockServer.Object, entity.RtId.ToString(),
            TestCkTypeId, [Item("Password", $"\"{FakeSecret}\"")], clearSecretAttributes: ["apiKey"]);

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        MockTenantRepository.Verify(r => r.ApplyChangesAsync(It.IsAny<IOctoSession>(),
            It.Is<IReadOnlyList<IEntityUpdateInfo<RtEntity>>>(l =>
                l[0].ClearSecretAttributes!.Contains("ApiKey")),
            It.IsAny<OperationResult>()), Times.Once);
    }

    [Fact]
    public async Task SetEntitySecrets_NonSecretAttribute_IsRefused()
    {
        var entity = GivenStoredEntity();

        var result = await RuntimeEntityCrudTools.SetEntitySecrets(MockServer.Object, entity.RtId.ToString(),
            TestCkTypeId, [Item("Name", "\"n\"")]);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("not a Secret attribute").And.Contain("update_entity");
        VerifyNoWrite();
    }

    [Theory]
    [InlineData("\"\"")]
    [InlineData("null")]
    [InlineData("42")]
    public async Task SetEntitySecrets_EmptyOrNonString_IsRefused(string json)
    {
        var entity = GivenStoredEntity();

        var result = await RuntimeEntityCrudTools.SetEntitySecrets(MockServer.Object, entity.RtId.ToString(),
            TestCkTypeId, [Item("Password", json)]);

        result.IsSuccess.Should().BeFalse();
        VerifyNoWrite();
    }

    [Theory]
    [InlineData("TODO_SET_PASSWORD")]
    [InlineData("<set me>")]
    public async Task SetEntitySecrets_PlaceholderLikeValue_IsWrittenAsOrdinarySecret(string value)
    {
        var entity = GivenStoredEntity();

        var result = await RuntimeEntityCrudTools.SetEntitySecrets(MockServer.Object, entity.RtId.ToString(),
            TestCkTypeId, [Item("Password", $"\"{value}\"")]);

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        MockTenantRepository.Verify(r => r.UpdateOneRtEntityByIdAsync(It.IsAny<IOctoSession>(),
            It.IsAny<RtCkId<CkTypeId>>(), It.IsAny<OctoObjectId>(),
            It.Is<RtEntity>(e => e.Attributes["Password"] is RtSecretValue)), Times.Once);
    }

    [Fact]
    public async Task SetEntitySecrets_NoSecretsAndNoClear_ReturnsValidationError()
    {
        var result = await RuntimeEntityCrudTools.SetEntitySecrets(MockServer.Object,
            "507f1f77bcf86cd799439011", TestCkTypeId);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("at least one");
        MockSecureSessionFactory.Verify(f => f.GetSessionAsync(It.IsAny<RtSecurityContext>()), Times.Never);
    }

    [Fact]
    public async Task SetEntitySecrets_Unauthenticated_ReturnsNotAuthenticated()
    {
        GivenUnauthenticatedCaller();

        var result = await RuntimeEntityCrudTools.SetEntitySecrets(MockServer.Object,
            "507f1f77bcf86cd799439011", TestCkTypeId, [Item("Password", $"\"{FakeSecret}\"")]);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().StartWith("Not authenticated");
        VerifyNoWrite();
    }

    [Fact]
    public async Task SetEntitySecrets_StaleExpectedVersion_ReturnsConflict()
    {
        var entity = GivenStoredEntity(version: 9);

        var result = await RuntimeEntityCrudTools.SetEntitySecrets(MockServer.Object, entity.RtId.ToString(),
            TestCkTypeId, [Item("Password", $"\"{FakeSecret}\"")], expectedVersion: 2);

        result.IsConflict.Should().BeTrue();
        VerifyNoWrite();
    }

    // ── queries / aggregations ─────────────────────────────────────────────

    [Fact]
    public async Task QueryEntities_EqualsFilterOnSecret_IsRefusedBeforeTheEngine()
    {
        var filters = new FieldFilterCriteriaDto
        {
            Fields = [new FieldFilterDto { AttributePath = "password", Operator = FilterOperatorDto.Equals, Value = "x" }]
        };

        var result = await RuntimeEntityCrudTools.QueryEntities(MockServer.Object, TestCkTypeId, filters);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().StartWith("SecretAttributeNotQueryable");
        MockSecureSessionFactory.Verify(f => f.GetSessionAsync(It.IsAny<RtSecurityContext>()), Times.Never);
    }

    [Fact]
    public async Task QueryEntities_IsNotNullFilterOnSecret_IsAllowed()
    {
        MockTenantRepository
            .Setup(r => r.GetRtEntitiesByTypeAsync(It.IsAny<IOctoSession>(), It.IsAny<RtCkId<CkTypeId>>(),
                It.IsAny<RtEntityQueryOptions>(), null, null))
            .ReturnsAsync(new Meshmakers.Octo.Runtime.Engine.Repositories.Query.ResultSet<RtEntity>(
                new List<RtEntity>(), 0, null, null));
        var filters = new FieldFilterCriteriaDto
        {
            Fields = [new FieldFilterDto { AttributePath = "Password", Operator = FilterOperatorDto.IsNotNull }]
        };

        var result = await RuntimeEntityCrudTools.QueryEntities(MockServer.Object, TestCkTypeId, filters);

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
    }

    [Fact]
    public async Task QueryEntitiesSimple_FilterOnSecret_IsRefused()
    {
        var result = await RuntimeEntityCrudTools.QueryEntitiesSimple(MockServer.Object, TestCkTypeId,
            [new SimpleFilterDto { AttributePath = "Endpoints.Token", Value = "x" }]);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().StartWith("SecretAttributeNotQueryable");
    }

    [Fact]
    public async Task QueryEntities_EngineRefusal_IsSurfacedWithErrorCode()
    {
        MockTenantRepository
            .Setup(r => r.GetRtEntitiesByTypeAsync(It.IsAny<IOctoSession>(), It.IsAny<RtCkId<CkTypeId>>(),
                It.IsAny<RtEntityQueryOptions>(), null, null))
            .ThrowsAsync(new SecretAttributeNotQueryableException("Password", "sort", "Credential"));

        var result = await RuntimeEntityCrudTools.QueryEntities(MockServer.Object, TestCkTypeId);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().StartWith("SecretAttributeNotQueryable").And.Contain("IsNull");
    }

    [Fact]
    public async Task QueryEntitiesAggregation_OnSecret_IsRefused()
    {
        var result = await RuntimeAggregationTools.QueryEntitiesAggregation(MockServer.Object, TestCkTypeId,
            [new AggregationColumnDto { Function = AggregationFunctionDto.min, AttributePath = "Password" }]);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().StartWith("SecretAttributeNotQueryable").And.Contain("aggregation");
        MockSecureSessionFactory.Verify(f => f.GetSessionAsync(It.IsAny<RtSecurityContext>()), Times.Never);
    }

    [Fact]
    public async Task QueryEntitiesGrouping_GroupByOnSecret_IsRefused()
    {
        var result = await RuntimeAggregationTools.QueryEntitiesGrouping(MockServer.Object, TestCkTypeId,
            ["password"], [new AggregationColumnDto { Function = AggregationFunctionDto.count }]);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("group-by");
    }
}
