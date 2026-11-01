using System.Text.Json;
using FluentAssertions;
using Meshmakers.Octo.Backend.McpServices.Models;
using RtRecordDto = Meshmakers.Octo.Communication.Contracts.DataTransferObjects.RtRecordDto;
using Meshmakers.Octo.Backend.McpServices.Models.Aggregation;
using Meshmakers.Octo.Backend.McpServices.Models.Filters;
using Meshmakers.Octo.Backend.McpServices.Services;
using Meshmakers.Octo.Backend.McpServices.Tools;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v2;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Engine.Repositories.Query;
using Moq;
using Xunit;

namespace McpServices.Tests.Tools;

/// <summary>
///     AB#8043: the generic CRUD, query and aggregation tools read and write the tenant repository directly, so they enforce
///     the CK attribute access rules themselves: Hidden values never leave the server, Hidden attributes are never queried,
///     Hidden / MethodOnly (and, on update, ReadOnly) attributes and the identity-owned User fields are never written, and a
///     new System.Identity/User is refused (User.CreateUser-1 replaces the generic create). The model mirrors System.Identity
///     User; every value is a fixed dummy string.
/// </summary>
public class RuntimeEntityAccessGuardTests : TestBase
{
    private const string UserFullId = "System.Identity-2.30.0/User-1";
    private const string UserRtId = "System.Identity/User";
    private const string DeviceFullId = "TestModule-1.0.0/Device-1";
    private const string BaseFullId = "TestModule-1.0.0/Principal-1";

    private const string DummyPasswordHash = "dummy-password-hash-value";
    private const string DummySecurityStamp = "dummy-security-stamp-value";
    private const string DummyTokenValue = "dummy-authenticator-token-value";
    private const string DummyRecoveryCode = "dummy-recovery-code-value";
    private const string DummyProviderKey = "dummy-provider-key-value";

    private static readonly CkId<CkRecordId> UserTokenRecordId = new("System.Identity-2.30.0/UserToken-1");
    private static readonly CkId<CkRecordId> ProfileRecordId = new("System.Identity-2.30.0/Profile-1");
    private static readonly CkId<CkRecordId> LoginRecordId = new("System.Identity-2.30.0/Login-1");

    private readonly List<CkTypeGraph> _types = [];
    private readonly List<CkRecordGraph> _records = [];

    public RuntimeEntityAccessGuardTests()
    {
        _records.Add(CkGraphTestBuilder.BuildRecordWithAccess(UserTokenRecordId,
            ("Name", AttributeValueTypesDto.String, null, null),
            ("Value", AttributeValueTypesDto.String, null, null)));
        _records.Add(CkGraphTestBuilder.BuildRecordWithAccess(ProfileRecordId,
            ("Nickname", AttributeValueTypesDto.String, null, null),
            ("RecoveryCode", AttributeValueTypesDto.String, null, CkAttributeAccessDto.Hidden)));
        _records.Add(CkGraphTestBuilder.BuildRecordWithAccess(LoginRecordId,
            ("Provider", AttributeValueTypesDto.String, null, null),
            ("ProviderKey", AttributeValueTypesDto.String, null, CkAttributeAccessDto.Hidden)));

        _types.Add(BuildUserType([]));
        _types.Add(CkGraphTestBuilder.BuildTypeWithAccess(DeviceFullId, [], [], [],
            ("Name", AttributeValueTypesDto.String, null, null),
            ("SerialNumber", AttributeValueTypesDto.String, null, CkAttributeAccessDto.ReadOnly),
            ("Firmware", AttributeValueTypesDto.String, null, CkAttributeAccessDto.MethodOnly),
            ("PairingKey", AttributeValueTypesDto.String, null, CkAttributeAccessDto.Hidden),
            ("Passcode", AttributeValueTypesDto.Secret, null, CkAttributeAccessDto.Hidden),
            ("SecurityStamp", AttributeValueTypesDto.String, null, null)));
        _types.Add(CkGraphTestBuilder.BuildTypeWithAccess(BaseFullId, [], [], [UserFullId],
            ("Name", AttributeValueTypesDto.String, null, null)));

        MockCkCacheService.Setup(c => c.GetCkTypes(It.IsAny<string>())).Returns(() => _types);
        MockCkCacheService.Setup(c => c.GetCkRecords(It.IsAny<string>())).Returns(() => _records);
        MockCkCacheService.Setup(c => c.GetRtCkType(It.IsAny<string>(), It.IsAny<RtCkId<CkTypeId>>()))
            .Returns((string _, RtCkId<CkTypeId> id) =>
                FindType(id) ?? throw new InvalidOperationException($"unknown type {id}"));
        MockCkCacheService.Setup(c => c.GetCkType(It.IsAny<string>(), It.IsAny<CkId<CkTypeId>>()))
            .Returns((string _, CkId<CkTypeId> id) =>
                FindType(id.ToRtCkId()) ?? throw new InvalidOperationException($"unknown type {id}"));
        MockCkCacheService
            .Setup(c => c.TryGetRtCkType(It.IsAny<string>(), It.IsAny<RtCkId<CkTypeId>>(),
                out It.Ref<CkTypeGraph?>.IsAny))
            .Returns(new TryGetRtCkTypeCallback((string _, RtCkId<CkTypeId> id, out CkTypeGraph? graph) =>
            {
                graph = FindType(id);
                return graph != null;
            }));
        MockCkCacheService
            .Setup(c => c.TryGetCkType(It.IsAny<string>(), It.IsAny<CkId<CkTypeId>>(), out It.Ref<CkTypeGraph?>.IsAny))
            .Returns(new TryGetCkTypeCallback((string _, CkId<CkTypeId> id, out CkTypeGraph? graph) =>
            {
                graph = FindType(id.ToRtCkId());
                return graph != null;
            }));
        MockCkCacheService.Setup(c => c.GetRtCkRecord(It.IsAny<string>(), It.IsAny<RtCkId<CkRecordId>>()))
            .Returns((string _, RtCkId<CkRecordId> id) =>
                _records.Single(r => r.CkRecordId.ToRtCkId().ToString() == id.ToString()));
        MockCkCacheService
            .Setup(c => c.TryGetCkRecord(It.IsAny<string>(), It.IsAny<CkId<CkRecordId>>(),
                out It.Ref<CkRecordGraph?>.IsAny))
            .Returns(new TryGetCkRecordCallback((string _, CkId<CkRecordId> id, out CkRecordGraph? graph) =>
            {
                graph = _records.FirstOrDefault(r => r.CkRecordId.ToRtCkId().ToString() == id.ToRtCkId().ToString());
                return graph != null;
            }));
    }

    private delegate bool TryGetRtCkTypeCallback(string tenantId, RtCkId<CkTypeId> id, out CkTypeGraph? graph);

    private delegate bool TryGetCkTypeCallback(string tenantId, CkId<CkTypeId> id, out CkTypeGraph? graph);

    private delegate bool TryGetCkRecordCallback(string tenantId, CkId<CkRecordId> id, out CkRecordGraph? graph);

    // Like the real cache (CkCache.TryGetRtCkType: a dictionary keyed by the version-less runtime id): an RtCkId that
    // spells a model version does not match.
    private CkTypeGraph? FindType(RtCkId<CkTypeId> id) =>
        _types.FirstOrDefault(t => t.CkTypeId.ToRtCkId().ToString() == id.ToString());

    private static CkTypeGraph BuildUserType(IReadOnlyCollection<(string, CkMethodDto)> methods) =>
        CkGraphTestBuilder.BuildTypeWithAccess(UserFullId, methods, [BaseFullId], [],
            ("Name", AttributeValueTypesDto.String, null, null),
            ("UserName", AttributeValueTypesDto.String, null, CkAttributeAccessDto.MethodOnly),
            ("Email", AttributeValueTypesDto.String, null, CkAttributeAccessDto.MethodOnly),
            ("PasswordHash", AttributeValueTypesDto.String, null, CkAttributeAccessDto.Hidden),
            ("SecurityStamp", AttributeValueTypesDto.String, null, CkAttributeAccessDto.Hidden),
            ("UserTokens", AttributeValueTypesDto.RecordArray, UserTokenRecordId, CkAttributeAccessDto.Hidden),
            ("LockoutEnd", AttributeValueTypesDto.DateTime, null, CkAttributeAccessDto.ReadOnly),
            ("TwoFactorEnabled", AttributeValueTypesDto.Boolean, null, null),
            ("Profile", AttributeValueTypesDto.Record, ProfileRecordId, null),
            ("Logins", AttributeValueTypesDto.RecordArray, LoginRecordId, null));

    private RtEntity GivenStoredUser()
    {
        var user = new RtEntity
        {
            RtId = OctoObjectId.GenerateNewId(),
            CkTypeId = new RtCkId<CkTypeId>(UserRtId),
            RtVersion = 4
        };
        user.SetAttributeValue("Name", AttributeValueTypesDto.String, "Test User");
        user.SetAttributeValue("Email", AttributeValueTypesDto.String, "user@example.test");
        user.SetAttributeValue("PasswordHash", AttributeValueTypesDto.String, DummyPasswordHash);
        user.SetAttributeValue("SecurityStamp", AttributeValueTypesDto.String, DummySecurityStamp);
        user.SetAttributeValue("UserTokens", AttributeValueTypesDto.RecordArray, new List<object>
        {
            NewRecord(UserTokenRecordId, ("Name", "authenticator"), ("Value", DummyTokenValue))
        });
        user.SetAttributeValue("Profile", AttributeValueTypesDto.Record, NewRecord(ProfileRecordId, ("Nickname", "tester"), ("RecoveryCode", DummyRecoveryCode)));
        user.SetAttributeValue("Logins", AttributeValueTypesDto.RecordArray, new List<object>
        {
            NewRecord(LoginRecordId, ("Provider", "dummy-idp"), ("ProviderKey", DummyProviderKey))
        });

        MockTenantRepository
            .Setup(r => r.GetRtEntityByRtIdAsync(It.IsAny<IOctoSession>(), It.IsAny<RtEntityId>()))
            .ReturnsAsync(user);
        MockTenantRepository
            .Setup(r => r.GetRtEntitiesByTypeAsync(It.IsAny<IOctoSession>(), It.IsAny<RtCkId<CkTypeId>>(),
                It.IsAny<RtEntityQueryOptions>(), It.IsAny<int?>(), It.IsAny<int?>()))
            .ReturnsAsync(new ResultSet<RtEntity>([user], 1, null, null));
        return user;
    }

    private static RtRecord NewRecord(CkId<CkRecordId> recordId, params (string Name, object Value)[] values)
    {
        var record = new RtRecord { CkRecordId = recordId.ToRtCkId() };
        foreach (var (name, value) in values)
        {
            record.SetAttributeValue(name, AttributeValueTypesDto.String, value);
        }

        return record;
    }

    private static string Json(object response) => JsonSerializer.Serialize(response);

    private static void ShouldNotLeakHiddenValues(object response)
    {
        var json = Json(response);
        json.Should().NotContain(DummyPasswordHash).And.NotContain(DummySecurityStamp)
            .And.NotContain(DummyTokenValue).And.NotContain(DummyRecoveryCode).And.NotContain(DummyProviderKey);
        json.Should().NotContainEquivalentOf("passwordHash").And.NotContainEquivalentOf("securityStamp")
            .And.NotContainEquivalentOf("userTokens").And.NotContainEquivalentOf("recoveryCode")
            .And.NotContainEquivalentOf("providerKey");
    }

    private void VerifyNoSessionOpened() =>
        MockSecureSessionFactory.Verify(f => f.GetSessionAsync(It.IsAny<RtSecurityContext>()), Times.Never);

    private void VerifyNoWrite()
    {
        MockTenantRepository.Verify(r => r.UpdateOneRtEntityByIdAsync(It.IsAny<IOctoSession>(),
            It.IsAny<RtCkId<CkTypeId>>(), It.IsAny<OctoObjectId>(), It.IsAny<RtEntity>()), Times.Never);
        MockTenantRepository.Verify(r => r.ApplyChangesAsync(It.IsAny<IOctoSession>(),
            It.IsAny<IReadOnlyList<IEntityUpdateInfo<RtEntity>>>(), It.IsAny<OperationResult>()), Times.Never);
        MockTenantRepository.Verify(r => r.InsertOneRtEntityAsync(It.IsAny<IOctoSession>(),
            It.IsAny<RtCkId<CkTypeId>>(), It.IsAny<RtEntity>()), Times.Never);
    }

    private static AttributeUpdateItem Item(string path, string json) =>
        new() { AttributePath = path, Value = JsonDocument.Parse(json).RootElement.Clone() };

    private static FieldFilterCriteriaDto Filter(string path, FilterOperatorDto op, object? value = null) =>
        new() { Fields = [new FieldFilterDto { AttributePath = path, Operator = op, Value = value }] };

    // ── reads: Hidden values never leave the server ─────────────────────────────────────────

    [Fact]
    public async Task GetEntityById_User_ReturnsVisibleAttributesButNoHiddenValue()
    {
        var user = GivenStoredUser();

        var result = await RuntimeEntityCrudTools.GetEntityById(MockServer.Object, UserFullId, user.RtId.ToString());

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        result.Entity!.Attributes!.Select(a => a.AttributeName).Should()
            .BeEquivalentTo("name", "email", "profile", "logins");
        var profile = (RtRecordDto)result.Entity.Attributes!.Single(a => a.AttributeName == "profile").Value!;
        profile.Attributes!.Select(a => a.AttributeName).Should().BeEquivalentTo("nickname");
        ShouldNotLeakHiddenValues(result);
    }

    [Fact]
    public async Task GetEntityById_VisibleRecordArrayWithAHiddenChild_ReturnsTheElementsWithoutTheChild()
    {
        var user = GivenStoredUser();

        var result = await RuntimeEntityCrudTools.GetEntityById(MockServer.Object, UserFullId, user.RtId.ToString());

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        var logins = ((IEnumerable<object>)result.Entity!.Attributes!.Single(a => a.AttributeName == "logins").Value!)
            .Cast<RtRecordDto>().ToList();
        logins.Should().HaveCount(1);
        logins[0].Attributes!.Select(a => a.AttributeName).Should().BeEquivalentTo("provider");
        Json(result).Should().NotContain(DummyProviderKey).And.Contain("dummy-idp");
    }

    [Fact]
    public async Task QueryEntities_User_ReturnsNoHiddenValue()
    {
        GivenStoredUser();

        var result = await RuntimeEntityCrudTools.QueryEntities(MockServer.Object, UserFullId);

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        result.Entities.Should().HaveCount(1);
        ShouldNotLeakHiddenValues(result);
    }

    [Fact]
    public async Task QueryEntitiesSimple_RequestingAHiddenAttributePath_ReturnsNoValue()
    {
        GivenStoredUser();

        var result = await RuntimeEntityCrudTools.QueryEntitiesSimple(MockServer.Object, UserFullId,
            attributePaths: ["passwordHash", "userTokens.value", "name"]);

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        result.Entities!.Single().Attributes!.Select(a => a.AttributeName).Should().BeEquivalentTo("name");
        ShouldNotLeakHiddenValues(result);
    }

    [Fact]
    public async Task UpdateEntity_StaleVersionConflictPayload_ReturnsNoHiddenValue()
    {
        var user = GivenStoredUser();

        var result = await RuntimeEntityCrudTools.UpdateEntity(MockServer.Object, user.RtId.ToString(), UserFullId,
            [Item("Name", "\"renamed\"")], expectedVersion: 1);

        result.IsConflict.Should().BeTrue();
        result.Entity.Should().NotBeNull();
        ShouldNotLeakHiddenValues(result);
    }

    [Fact]
    public async Task ExecuteRuntimeQuery_SimpleQueryOverUsers_ReturnsNoHiddenValue()
    {
        GivenStoredUser();
        var query = new RtSimpleRtQuery { QueryCkTypeId = UserRtId };
        MockTenantRepository
            .Setup(r => r.GetRtEntityByRtIdAsync<RtPersistentQuery>(It.IsAny<IOctoSession>(), It.IsAny<OctoObjectId>()))
            .ReturnsAsync(query);

        var result = await RuntimeAggregationTools.ExecuteRuntimeQuery(MockServer.Object, "507f1f77bcf86cd799439011");

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        result.Entities.Should().HaveCount(1);
        ShouldNotLeakHiddenValues(result);
    }

    // ── queries: Hidden attributes are never filtered, aggregated or grouped ────────────────

    [Theory]
    [InlineData("passwordHash", FilterOperatorDto.Equals)]
    [InlineData("PasswordHash", FilterOperatorDto.StartsWith)]
    [InlineData("securityStamp", FilterOperatorDto.IsNull)]
    [InlineData("userTokens.value", FilterOperatorDto.Like)]
    [InlineData("userTokens[0].value", FilterOperatorDto.Equals)]
    [InlineData("profile.recoveryCode", FilterOperatorDto.Equals)]
    [InlineData("profile", FilterOperatorDto.IsNotNull)]
    public async Task QueryEntities_FilterOnAHiddenPath_IsRefusedBeforeAnySession(string path, FilterOperatorDto op)
    {
        var result = await RuntimeEntityCrudTools.QueryEntities(MockServer.Object, UserFullId,
            Filter(path, op, DummyPasswordHash));

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().StartWith(AttributeAccessGuard.NotQueryableCode).And.Contain(path)
            .And.NotContain(DummyPasswordHash);
        VerifyNoSessionOpened();
    }

    [Fact]
    public async Task QueryEntities_NestedFilterOnAHiddenAttribute_IsRefused()
    {
        var filters = new FieldFilterCriteriaDto
        {
            Fields = [new FieldFilterDto { AttributePath = "name", Operator = FilterOperatorDto.Equals, Value = "x" }],
            NestedFilters = [Filter("passwordHash", FilterOperatorDto.Equals, DummyPasswordHash)]
        };

        var result = await RuntimeEntityCrudTools.QueryEntities(MockServer.Object, UserFullId, filters);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().StartWith(AttributeAccessGuard.NotQueryableCode);
        VerifyNoSessionOpened();
    }

    [Fact]
    public async Task QueryEntities_OnABaseType_FilterOnAHiddenAttributeOfADerivedType_IsRefused()
    {
        var result = await RuntimeEntityCrudTools.QueryEntities(MockServer.Object, BaseFullId,
            Filter("passwordHash", FilterOperatorDto.Equals, DummyPasswordHash));

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().StartWith(AttributeAccessGuard.NotQueryableCode);
        VerifyNoSessionOpened();
    }

    [Fact]
    public async Task QueryEntities_FilterOnAVisibleAttribute_IsNotRefused()
    {
        GivenStoredUser();

        var result = await RuntimeEntityCrudTools.QueryEntities(MockServer.Object, UserFullId,
            Filter("profile.nickname", FilterOperatorDto.Equals, "tester"));

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
    }

    [Fact]
    public async Task QueryEntitiesSimple_FilterOnAHiddenAttribute_IsRefused()
    {
        var result = await RuntimeEntityCrudTools.QueryEntitiesSimple(MockServer.Object, UserFullId,
            [new SimpleFilterDto { AttributePath = "securityStamp", Value = DummySecurityStamp }]);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().StartWith(AttributeAccessGuard.NotQueryableCode).And.NotContain(DummySecurityStamp);
        VerifyNoSessionOpened();
    }

    [Fact]
    public async Task NavigateAssociations_FilterOnAHiddenAttributeOfTheTarget_IsRefused()
    {
        var result = await RuntimeEntityCrudTools.NavigateAssociations(MockServer.Object, "System.Identity/Group",
            "507f1f77bcf86cd799439011", "System.Identity/GroupMember", CkTypeAssociationDirectionDto.Outbound,
            UserRtId, [new SimpleFilterDto { AttributePath = "passwordHash", Value = DummyPasswordHash }]);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().StartWith(AttributeAccessGuard.NotQueryableCode);
        VerifyNoSessionOpened();
    }

    [Fact]
    public async Task GetAssociationTree_RootFilterOnAHiddenAttribute_IsRefused()
    {
        var result = await RuntimeEntityCrudTools.GetAssociationTree(MockServer.Object, UserFullId,
            "System/ParentChild", CkTypeAssociationDirectionDto.Inbound,
            rootFilters: [new SimpleFilterDto { AttributePath = "passwordHash", Value = DummyPasswordHash }]);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().StartWith(AttributeAccessGuard.NotQueryableCode);
        VerifyNoSessionOpened();
    }

    [Theory]
    [InlineData(AggregationFunctionDto.min, "passwordHash")]
    [InlineData(AggregationFunctionDto.max, "securityStamp")]
    [InlineData(AggregationFunctionDto.count, "userTokens.value")]
    public async Task QueryEntitiesAggregation_OnAHiddenAttribute_IsRefused(AggregationFunctionDto function, string path)
    {
        var result = await RuntimeAggregationTools.QueryEntitiesAggregation(MockServer.Object, UserFullId,
            [new AggregationColumnDto { Function = function, AttributePath = path }]);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().StartWith(AttributeAccessGuard.NotQueryableCode).And.Contain("aggregation");
        VerifyNoSessionOpened();
    }

    [Fact]
    public async Task QueryEntitiesGrouping_GroupByAHiddenAttribute_IsRefused()
    {
        var result = await RuntimeAggregationTools.QueryEntitiesGrouping(MockServer.Object, UserFullId,
            ["passwordHash"], [new AggregationColumnDto { Function = AggregationFunctionDto.count }]);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().StartWith(AttributeAccessGuard.NotQueryableCode).And.Contain("group-by");
        VerifyNoSessionOpened();
    }

    [Fact]
    public async Task QueryEntitiesAggregation_FilterOnAHiddenAttribute_IsRefused()
    {
        var result = await RuntimeAggregationTools.QueryEntitiesAggregation(MockServer.Object, UserFullId,
            [new AggregationColumnDto { Function = AggregationFunctionDto.count }],
            Filter("passwordHash", FilterOperatorDto.StartsWith, "dummy"));

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().StartWith(AttributeAccessGuard.NotQueryableCode);
        VerifyNoSessionOpened();
    }

    [Fact]
    public async Task ExecuteRuntimeQuery_StoredFilterOnAHiddenAttribute_IsRefusedAndNotRun()
    {
        var query = new RtSimpleRtQuery { QueryCkTypeId = UserRtId };
        query.FieldFilter = new AttributeRecordValueList<RtFieldFilterRecord>
        {
            new RtFieldFilterRecord { AttributePath = "PasswordHash", ComparisonValue = DummyPasswordHash }
        };
        GivenQueryType(query, "SimpleRtQuery");
        MockTenantRepository
            .Setup(r => r.GetRtEntityByRtIdAsync<RtPersistentQuery>(It.IsAny<IOctoSession>(), It.IsAny<OctoObjectId>()))
            .ReturnsAsync(query);

        var result = await RuntimeAggregationTools.ExecuteRuntimeQuery(MockServer.Object, "507f1f77bcf86cd799439011");

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().StartWith(AttributeAccessGuard.NotQueryableCode)
            .And.Contain(AttributeAccessGuard.StoredFilterOperation).And.NotContain(DummyPasswordHash);
        MockTenantRepository.Verify(r => r.GetRtEntitiesByTypeAsync(It.IsAny<IOctoSession>(),
            It.IsAny<RtCkId<CkTypeId>>(), It.IsAny<RtEntityQueryOptions>(), It.IsAny<int?>(), It.IsAny<int?>()),
            Times.Never);
    }

    [Fact]
    public async Task ExecuteRuntimeQuery_ExtraFilterOnAHiddenAttribute_IsRefused()
    {
        var query = new RtSimpleRtQuery { QueryCkTypeId = UserRtId };
        GivenQueryType(query, "SimpleRtQuery");
        MockTenantRepository
            .Setup(r => r.GetRtEntityByRtIdAsync<RtPersistentQuery>(It.IsAny<IOctoSession>(), It.IsAny<OctoObjectId>()))
            .ReturnsAsync(query);

        var result = await RuntimeAggregationTools.ExecuteRuntimeQuery(MockServer.Object, "507f1f77bcf86cd799439011",
            Filter("securityStamp", FilterOperatorDto.Equals, DummySecurityStamp));

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().StartWith(AttributeAccessGuard.NotQueryableCode);
    }

    [Fact]
    public async Task CreateEntity_StoredQueryWithAHiddenGroupingColumn_IsRefusedAndNotInserted()
    {
        var created = new RtEntity();
        var queryTypeId = GivenQueryType(created, "GroupingAggregationRtQuery");
        created.SetAttributeValue("QueryCkTypeId", AttributeValueTypesDto.String, UserRtId);
        created.SetAttributeValue("GroupingColumns", AttributeValueTypesDto.StringArray, new List<object> { "SecurityStamp" });
        MockTenantRepository.Setup(r => r.CreateTransientRtEntityAsync(It.IsAny<CkId<CkTypeId>>()))
            .ReturnsAsync(created);

        var result = await RuntimeEntityCrudTools.CreateEntity(MockServer.Object, queryTypeId.ToString(),
            [Item("Name", "\"oracle\"")]);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(AttributeAccessGuard.NotQueryableCode);
        result.ErrorMessage.Should().Contain(AttributeAccessGuard.StoredGroupByOperation);
        VerifyNoWrite();
    }

    [Fact]
    public async Task UpdateEntity_StoredQueryWithAHiddenFilterPath_IsRefusedAndNotWritten()
    {
        var stored = new RtEntity { RtId = OctoObjectId.GenerateNewId(), RtVersion = 2 };
        GivenQueryType(stored, "SimpleRtQuery");
        stored.SetAttributeValue("QueryCkTypeId", AttributeValueTypesDto.String, UserRtId);
        stored.SetAttributeValue("FieldFilter", AttributeValueTypesDto.RecordArray, new List<object>
        {
            NewRecord(new CkId<CkRecordId>("System-2.0.0/FieldFilter-1"), ("AttributePath", "PasswordHash"))
        });
        MockTenantRepository
            .Setup(r => r.GetRtEntityByRtIdAsync(It.IsAny<IOctoSession>(), It.IsAny<RtEntityId>()))
            .ReturnsAsync(stored);

        var result = await RuntimeEntityCrudTools.UpdateEntity(MockServer.Object, stored.RtId.ToString(),
            stored.CkTypeId!.ToString(), [Item("Name", "\"renamed\"")]);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().StartWith(AttributeAccessGuard.NotQueryableCode)
            .And.Contain(AttributeAccessGuard.StoredFilterOperation);
        VerifyNoWrite();
    }

    [Fact]
    public async Task CreateEntity_StoredQueryOnAnUnknownType_GroupingByARecordWithAHiddenChild_IsRefused()
    {
        var created = new RtEntity();
        var queryTypeId = GivenQueryType(created, "GroupingAggregationRtQuery");
        created.SetAttributeValue("QueryCkTypeId", AttributeValueTypesDto.String, "TestModule/Missing");
        created.SetAttributeValue("GroupingColumns", AttributeValueTypesDto.StringArray, new List<object> { "profile" });
        MockTenantRepository.Setup(r => r.CreateTransientRtEntityAsync(It.IsAny<CkId<CkTypeId>>()))
            .ReturnsAsync(created);

        var result = await RuntimeEntityCrudTools.CreateEntity(MockServer.Object, queryTypeId.ToString(),
            [Item("Name", "\"oracle\"")]);

        result.IsSuccess.Should().BeFalse("an unknown query type is checked by name, fail closed");
        result.ErrorCode.Should().Be(AttributeAccessGuard.NotQueryableCode);
        VerifyNoWrite();
    }

    [Fact]
    public async Task QueryEntities_FilterOnAnAttributeThatIsHiddenAndSecret_ReportsTheHiddenCode()
    {
        var result = await RuntimeEntityCrudTools.QueryEntities(MockServer.Object, DeviceFullId,
            Filter("passcode", FilterOperatorDto.Equals, "dummy"));

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().StartWith(AttributeAccessGuard.NotQueryableCode);
    }

    [Fact]
    public async Task NavigateAssociations_TargetGivenByFullId_IsCheckedAgainstThatTypeOnly()
    {
        // Device declares a visible SecurityStamp; it is Hidden only on User. With the full id the target resolves, so
        // the filter is allowed (a by-name fallback would refuse it).
        var result = await RuntimeEntityCrudTools.NavigateAssociations(MockServer.Object, UserRtId,
            "507f1f77bcf86cd799439011", "System/ParentChild", CkTypeAssociationDirectionDto.Outbound,
            DeviceFullId, [new SimpleFilterDto { AttributePath = "securityStamp", Value = "x" }]);

        (result.ErrorMessage ?? string.Empty).Should().NotStartWith(AttributeAccessGuard.NotQueryableCode);
    }

    [Fact]
    public async Task UpdateEntity_ViaABaseTypeId_OfADerivedUser_RefusesTheDerivedHiddenAttribute()
    {
        var user = GivenStoredUser();

        var result = await RuntimeEntityCrudTools.UpdateEntity(MockServer.Object, user.RtId.ToString(), BaseFullId,
            [Item("passwordHash", "\"dummy-new-value\"")]);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().StartWith(AttributeAccessGuard.NotWritableCode).And.Contain("access: Hidden");
        VerifyNoWrite();
    }

    [Fact]
    public async Task UpdateEntity_ViaABaseTypeId_OfADerivedUser_RefusesAnIdentityOwnedField()
    {
        var user = GivenStoredUser();

        var result = await RuntimeEntityCrudTools.UpdateEntity(MockServer.Object, user.RtId.ToString(), BaseFullId,
            [Item("twoFactorEnabled", "false")]);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().StartWith(AttributeAccessGuard.NotWritableCode)
            .And.Contain(IdentityOwnedWrites.RefusalReason);
        VerifyNoWrite();
    }

    // Registers the CK type of the given stored-query entity as a type derived from System/PersistentQuery and sets the
    // entity's CkTypeId as the repository does when it loads it.
    private CkId<CkTypeId> GivenQueryType(RtEntity query, string elementName)
    {
        var fullId = new CkId<CkTypeId>($"System-2.0.0/{elementName}-1");
        query.CkTypeId = fullId.ToRtCkId();
        if (FindType(fullId.ToRtCkId()) == null)
        {
            _types.Add(CkGraphTestBuilder.BuildTypeWithAccess(fullId.ToString(), [],
                [$"System-2.0.0/{SystemCkIds.RtCkPersistentQueryTypeId.ElementId.FullName}"], [],
                ("Name", AttributeValueTypesDto.String, null, null)));
        }

        return fullId;
    }

    // ── writes: Hidden / MethodOnly / ReadOnly-on-update / identity-owned are refused ──────

    [Theory]
    [InlineData("passwordHash", "Hidden")]
    [InlineData("SecurityStamp", "Hidden")]
    [InlineData("userTokens[0].value", "Hidden")]
    [InlineData("email", "MethodOnly")]
    [InlineData("lockoutEnd", "ReadOnly")]
    public async Task UpdateEntity_ProtectedAttribute_IsRefusedWithoutWrite(string path, string access)
    {
        var user = GivenStoredUser();

        var result = await RuntimeEntityCrudTools.UpdateEntity(MockServer.Object, user.RtId.ToString(), UserFullId,
            [Item(path, "\"dummy-new-value\"")]);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().StartWith(AttributeAccessGuard.NotWritableCode).And.Contain(path)
            .And.Contain($"access: {access}").And.NotContain("dummy-new-value");
        VerifyNoSessionOpened();
        VerifyNoWrite();
    }

    [Fact]
    public async Task UpdateEntity_HiddenSubAttributeOfAVisibleRecord_IsRefused()
    {
        var user = GivenStoredUser();

        var result = await RuntimeEntityCrudTools.UpdateEntity(MockServer.Object, user.RtId.ToString(), UserFullId,
            [Item("profile.recoveryCode", "\"dummy-new-value\"")]);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().StartWith(AttributeAccessGuard.NotWritableCode).And.Contain("access: Hidden");
        VerifyNoWrite();
    }

    [Theory]
    [InlineData("twoFactorEnabled", "false")]
    [InlineData("NormalizedUserName", "\"dummy\"")]
    [InlineData("EmailConfirmed", "true")]
    public async Task UpdateEntity_IdentityOwnedUserField_IsRefused(string path, string json)
    {
        var user = GivenStoredUser();

        var result = await RuntimeEntityCrudTools.UpdateEntity(MockServer.Object, user.RtId.ToString(), UserFullId,
            [Item(path, json)]);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().StartWith(AttributeAccessGuard.NotWritableCode)
            .And.Contain(IdentityOwnedWrites.RefusalReason);
        VerifyNoWrite();
    }

    [Fact]
    public async Task UpdateEntity_ClearingAHiddenAttribute_IsRefused()
    {
        var user = GivenStoredUser();

        var result = await RuntimeEntityCrudTools.UpdateEntity(MockServer.Object, user.RtId.ToString(), UserFullId,
            [], clearSecretAttributes: ["passwordHash"]);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().StartWith(AttributeAccessGuard.NotWritableCode);
        VerifyNoWrite();
    }

    [Fact]
    public async Task UpdateEntity_VisibleAttributeOfAUser_IsWrittenAndHiddenValuesStayUntouched()
    {
        var user = GivenStoredUser();

        var result = await RuntimeEntityCrudTools.UpdateEntity(MockServer.Object, user.RtId.ToString(), UserFullId,
            [Item("Name", "\"renamed\"")]);

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        MockTenantRepository.Verify(r => r.UpdateOneRtEntityByIdAsync(It.IsAny<IOctoSession>(),
            It.IsAny<RtCkId<CkTypeId>>(), It.IsAny<OctoObjectId>(),
            It.Is<RtEntity>(e => (string?)e.Attributes["Name"] == "renamed" &&
                                 (string?)e.Attributes["PasswordHash"] == DummyPasswordHash)), Times.Once);
        ShouldNotLeakHiddenValues(result);
    }

    [Fact]
    public async Task CreateEntity_ReadOnlyAttribute_IsAllowedOnCreate()
    {
        MockTenantRepository.Setup(r => r.CreateTransientRtEntityAsync(It.IsAny<CkId<CkTypeId>>()))
            .ReturnsAsync(new RtEntity { CkTypeId = new RtCkId<CkTypeId>("TestModule/Device") });

        var result = await RuntimeEntityCrudTools.CreateEntity(MockServer.Object, DeviceFullId,
            [Item("Name", "\"sensor\""), Item("SerialNumber", "\"SN-0001\"")]);

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        MockTenantRepository.Verify(r => r.InsertOneRtEntityAsync(It.IsAny<IOctoSession>(),
            It.IsAny<RtCkId<CkTypeId>>(), It.Is<RtEntity>(e => (string?)e.Attributes["SerialNumber"] == "SN-0001")),
            Times.Once);
    }

    [Theory]
    [InlineData("Firmware", "MethodOnly")]
    [InlineData("pairingKey", "Hidden")]
    public async Task CreateEntity_MethodOnlyOrHiddenAttribute_IsRefused(string path, string access)
    {
        var result = await RuntimeEntityCrudTools.CreateEntity(MockServer.Object, DeviceFullId,
            [Item(path, "\"dummy-new-value\"")]);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(AttributeAccessGuard.NotWritableCode);
        result.ErrorMessage.Should().Contain($"access: {access}").And.NotContain("dummy-new-value");
        VerifyNoSessionOpened();
        VerifyNoWrite();
    }

    [Fact]
    public async Task CreateEntityWithSecrets_HiddenAttributeInSecrets_IsRefused()
    {
        var result = await RuntimeEntityCrudTools.CreateEntityWithSecrets(MockServer.Object, DeviceFullId,
            [Item("Name", "\"sensor\"")], [Item("pairingKey", "\"dummy-new-value\"")]);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(AttributeAccessGuard.NotWritableCode);
        VerifyNoWrite();
    }

    [Fact]
    public async Task CreateEntity_NewSystemIdentityUser_IsRefusedPointingToCreateUser()
    {
        // System.Identity 2.29+ declares the static User.CreateUser-1; the engine treats it as the replacement of the
        // generic create of User (runtime rule until the next System.Identity major sets the model flag).
        _types.RemoveAll(t => t.CkTypeId.ToString() == UserFullId);
        _types.Add(BuildUserType([(UserFullId, new CkMethodDto
        {
            MethodId = GenericCreateReplacement.IdentityUserCreateMethodId,
            Kind = CkMethodKindDto.Static
        })]));

        var result = await RuntimeEntityCrudTools.CreateEntity(MockServer.Object, UserFullId,
            [Item("Name", "\"new user\"")]);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(GenericCreateRefusal.Code);
        result.ErrorParameters!["methodId"].Should().Contain("CreateUser-1");
        VerifyNoSessionOpened();
        VerifyNoWrite();
    }
}
