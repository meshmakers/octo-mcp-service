using System.Text.Json;
using FluentAssertions;
using Meshmakers.Octo.Backend.McpServices.Models;
using Meshmakers.Octo.Backend.McpServices.Models.Filters;
using Meshmakers.Octo.Backend.McpServices.Tools;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Engine.Repositories.Query;
using Moq;
using Xunit;

namespace McpServices.Tests.Tools;

/// <summary>
///     AB#7553: the generic CRUD and query tools accept the full versioned ckTypeId that <c>get_type_schema</c>
///     returns (<c>System.Communication-4.13.0/MqttConfiguration-1</c>) as well as the runtime id
///     (<c>System.Communication/MqttConfiguration</c>). The cache and the repository behave like the real ones: the
///     cache getters throw for an id they do not hold, <c>CreateTransientRtEntityAsync</c> only knows the full id
///     (a runtime id would parse to model version 1.0.0) and the entity calls only know the runtime id.
/// </summary>
public class RuntimeEntityCrudToolsCkTypeIdTests : TestBase
{
    private const string FullId = "System.Communication-4.13.0/MqttConfiguration-1";
    private const string RuntimeId = "System.Communication/MqttConfiguration";
    private const string FakePassword = "fake-mqtt-password-7553";

    private readonly RtEntity _stored;

    public RuntimeEntityCrudToolsCkTypeIdTests()
    {
        var type = CkGraphTestBuilder.BuildType(FullId,
            ("Name", AttributeValueTypesDto.String, null),
            ("Password", AttributeValueTypesDto.Secret, null));

        // The real cache: exact getters throw for anything that is not installed.
        MockCkCacheService.Setup(c => c.GetCkType(It.IsAny<string>(), It.IsAny<CkId<CkTypeId>>()))
            .Throws(new KeyNotFoundException("CkTypeId not found in CkCache"));
        MockCkCacheService.Setup(c => c.GetRtCkType(It.IsAny<string>(), It.IsAny<RtCkId<CkTypeId>>()))
            .Throws(new KeyNotFoundException("RtCkTypeId not found in CkCache"));
        MockCkCacheService.Setup(c => c.GetCkType(It.IsAny<string>(), type.CkTypeId)).Returns(type);
        MockCkCacheService.Setup(c => c.GetRtCkType(It.IsAny<string>(), type.CkTypeId.ToRtCkId())).Returns(type);
        MockCkCacheService.Setup(c => c.GetCkTypes(It.IsAny<string>())).Returns([type]);
        CkGraphTestBuilder.SetupTryGetRtCkType(MockCkCacheService, type);

        _stored = new RtEntity
        {
            RtId = OctoObjectId.GenerateNewId(),
            CkTypeId = type.CkTypeId.ToRtCkId(),
            RtVersion = 1
        };

        // The real repository: the entity calls are keyed by the runtime id ...
        MockTenantRepository
            .Setup(r => r.GetRtEntityByRtIdAsync(It.IsAny<IOctoSession>(), It.IsAny<RtEntityId>()))
            .ReturnsAsync((IOctoSession _, RtEntityId id) =>
                id.CkTypeId.Equals(type.CkTypeId.ToRtCkId()) ? _stored : null);
        MockTenantRepository
            .Setup(r => r.GetCkTypeGraphAsync(It.IsAny<RtCkId<CkTypeId>>()))
            .ReturnsAsync((RtCkId<CkTypeId> id) => id.Equals(type.CkTypeId.ToRtCkId())
                ? type
                : throw new KeyNotFoundException($"'{id}' not found"));
        MockTenantRepository
            .Setup(r => r.GetRtEntitiesByTypeAsync(It.IsAny<IOctoSession>(), It.IsAny<RtCkId<CkTypeId>>(),
                It.IsAny<RtEntityQueryOptions>(), It.IsAny<int?>(), It.IsAny<int?>()))
            .ReturnsAsync((IOctoSession _, RtCkId<CkTypeId> id, RtEntityQueryOptions _, int? _, int? _) =>
                id.Equals(type.CkTypeId.ToRtCkId())
                    ? new ResultSet<RtEntity>([_stored], 1, null, null)
                    : throw new KeyNotFoundException($"'{id}' not found"));
        // ... and the transient entity is keyed by the full id (the runtime id would resolve to model 1.0.0).
        MockTenantRepository
            .Setup(r => r.CreateTransientRtEntityAsync(It.IsAny<CkId<CkTypeId>>()))
            .ReturnsAsync((CkId<CkTypeId> id) => id.Equals(type.CkTypeId)
                ? new RtEntity { RtId = OctoObjectId.GenerateNewId(), CkTypeId = id.ToRtCkId() }
                : throw new KeyNotFoundException($"CkTypeId '{id}' not found in CkCache"));
    }

    private static AttributeUpdateItem Item(string path, string json) =>
        new() { AttributePath = path, Value = JsonDocument.Parse(json).RootElement.Clone() };

    private static AttributeUpdateItem Password() => Item("Password", $"\"{FakePassword}\"");

    public static TheoryData<string> Spellings => new() { FullId, RuntimeId };

    [Theory]
    [MemberData(nameof(Spellings))]
    public async Task CreateEntityWithSecrets_AcceptsBothSpellings_AndNeverReturnsThePassword(string ckTypeId)
    {
        var result = await RuntimeEntityCrudTools.CreateEntityWithSecrets(MockServer.Object, ckTypeId,
            [Item("Name", "\"broker\"")], [Password()]);

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        MockTenantRepository.Verify(r => r.InsertOneRtEntityAsync(It.IsAny<IOctoSession>(),
            It.Is<RtCkId<CkTypeId>>(id => id.Equals(new CkId<CkTypeId>(FullId).ToRtCkId())),
            It.Is<RtEntity>(e => e.Attributes["Password"] is RtSecretValue)), Times.Once);
        JsonSerializer.Serialize(result).Should().NotContain(FakePassword);
    }

    [Theory]
    [MemberData(nameof(Spellings))]
    public async Task SetEntitySecrets_AcceptsBothSpellings_AndNeverReturnsThePassword(string ckTypeId)
    {
        var result = await RuntimeEntityCrudTools.SetEntitySecrets(MockServer.Object, _stored.RtId.ToString(),
            ckTypeId, [Password()]);

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        result.Entity!.Attributes!.Single(a =>
                string.Equals(a.AttributeName, "password", StringComparison.OrdinalIgnoreCase))
            .SecretIsSet.Should().BeTrue();
        JsonSerializer.Serialize(result).Should().NotContain(FakePassword);
    }

    [Theory]
    [MemberData(nameof(Spellings))]
    public async Task CreateEntity_AcceptsBothSpellings(string ckTypeId)
    {
        var result = await RuntimeEntityCrudTools.CreateEntity(MockServer.Object, ckTypeId,
            [Item("Name", "\"broker\"")]);

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
    }

    [Theory]
    [MemberData(nameof(Spellings))]
    public async Task CreateEntity_PasswordInEntityData_IsRefusedForBothSpellings_WithoutEchoingIt(string ckTypeId)
    {
        var result = await RuntimeEntityCrudTools.CreateEntity(MockServer.Object, ckTypeId, [Password()]);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("set_entity_secrets");
        JsonSerializer.Serialize(result).Should().NotContain(FakePassword);
    }

    [Theory]
    [MemberData(nameof(Spellings))]
    public async Task UpdateEntity_AcceptsBothSpellings(string ckTypeId)
    {
        var result = await RuntimeEntityCrudTools.UpdateEntity(MockServer.Object, _stored.RtId.ToString(),
            ckTypeId, [Item("Name", "\"renamed\"")]);

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
    }

    [Theory]
    [MemberData(nameof(Spellings))]
    public async Task GetEntityById_AcceptsBothSpellings(string ckTypeId)
    {
        var result = await RuntimeEntityCrudTools.GetEntityById(MockServer.Object, ckTypeId,
            _stored.RtId.ToString());

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
    }

    [Theory]
    [MemberData(nameof(Spellings))]
    public async Task DeleteEntity_AcceptsBothSpellings(string ckTypeId)
    {
        var result = await RuntimeEntityCrudTools.DeleteEntity(MockServer.Object, ckTypeId,
            _stored.RtId.ToString());

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
    }

    [Theory]
    [MemberData(nameof(Spellings))]
    public async Task QueryEntities_AcceptsBothSpellings(string ckTypeId)
    {
        var result = await RuntimeEntityCrudTools.QueryEntities(MockServer.Object, ckTypeId);

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        result.TotalCount.Should().Be(1);
    }

    [Theory]
    [MemberData(nameof(Spellings))]
    public async Task QueryEntitiesSimple_AcceptsBothSpellings(string ckTypeId)
    {
        var result = await RuntimeEntityCrudTools.QueryEntitiesSimple(MockServer.Object, ckTypeId);

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        result.TotalCount.Should().Be(1);
    }

    [Theory]
    [MemberData(nameof(Spellings))]
    public async Task QueryEntities_SecretFilter_IsRefusedForBothSpellings(string ckTypeId)
    {
        var result = await RuntimeEntityCrudTools.QueryEntitiesSimple(MockServer.Object, ckTypeId,
            [new SimpleFilterDto { AttributePath = "Password", Value = FakePassword }]);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Secret attribute");
    }

    [Fact]
    public async Task CreateEntityWithSecrets_NonSecretAttributeInSecrets_StillSaysNotASecretAttribute()
    {
        var result = await RuntimeEntityCrudTools.CreateEntityWithSecrets(MockServer.Object, FullId,
            [], [Item("Name", "\"x\"")]);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("is not a Secret attribute");
    }

    [Fact]
    public async Task UnknownType_IsReportedAsNotFound_NamingTheId_NotAsNotASecretAttribute()
    {
        const string unknown = "System.Communication-4.13.0/MqttConfig-1";

        var create = await RuntimeEntityCrudTools.CreateEntityWithSecrets(MockServer.Object, unknown,
            [], [Password()]);
        var set = await RuntimeEntityCrudTools.SetEntitySecrets(MockServer.Object, _stored.RtId.ToString(),
            unknown, [Password()]);
        var update = await RuntimeEntityCrudTools.UpdateEntity(MockServer.Object, _stored.RtId.ToString(),
            unknown, [Item("Name", "\"x\"")]);
        var delete = await RuntimeEntityCrudTools.DeleteEntity(MockServer.Object, unknown, _stored.RtId.ToString());
        var get = await RuntimeEntityCrudTools.GetEntityById(MockServer.Object, unknown, _stored.RtId.ToString());
        var query = await RuntimeEntityCrudTools.QueryEntities(MockServer.Object, unknown);

        foreach (var message in new[]
                 {
                     create.ErrorMessage, set.ErrorMessage, update.ErrorMessage, delete.ErrorMessage,
                     get.ErrorMessage, query.ErrorMessage
                 })
        {
            message.Should().Contain(unknown).And.Contain("not found").And.Contain(FullId)
                .And.NotContain("not a Secret attribute").And.NotContain(FakePassword);
        }

        MockTenantRepository.Verify(r => r.InsertOneRtEntityAsync(It.IsAny<IOctoSession>(),
            It.IsAny<RtCkId<CkTypeId>>(), It.IsAny<RtEntity>()), Times.Never);
        MockTenantRepository.Verify(r => r.DeleteOneRtEntityByRtIdAsync(It.IsAny<IOctoSession>(),
            It.IsAny<RtCkId<CkTypeId>>(), It.IsAny<OctoObjectId>(), It.IsAny<DeleteOptions>()), Times.Never);
    }
}
