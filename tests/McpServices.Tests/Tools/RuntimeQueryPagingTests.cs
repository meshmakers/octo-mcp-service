using FluentAssertions;
using Meshmakers.Octo.Backend.McpServices.Models;
using Meshmakers.Octo.Backend.McpServices.Options;
using Meshmakers.Octo.Backend.McpServices.Services;
using Meshmakers.Octo.Backend.McpServices.Tools;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v2;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Engine.Repositories.Query;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace McpServices.Tests.Tools;

/// <summary>
///     AB#8904: the entity query tools read the engine directly, so the asset-repo API guard (AB#8898) never sees
///     them. Each of them pages: without a limit/take the engine gets take = 100, a limit/take of 1000 is accepted,
///     1001 is refused with an error that names the limit and no engine query runs, and skip/offset is forwarded.
///     The result's paging block says whether more items exist and which skip fetches the next page.
/// </summary>
public class RuntimeQueryPagingTests : TestBase
{
    private const string FullId = "Industry.Basic-2.6.0/Machine-1";
    private const string RuntimeId = "Industry.Basic/Machine";
    private const string QueryRtId = "507f1f77bcf86cd799439011";
    private const string OriginRtId = "507f1f77bcf86cd799439012";

    private int? _seenSkip;
    private int? _seenTake;
    private int _engineCalls;
    private const long TotalCount = 250;
    private int _returnedCount = 100;

    public RuntimeQueryPagingTests()
    {
        var type = CkGraphTestBuilder.BuildType(FullId, ("Name", AttributeValueTypesDto.String, null));
        MockCkCacheService.Setup(c => c.GetCkType(It.IsAny<string>(), It.IsAny<CkId<CkTypeId>>()))
            .Throws(new KeyNotFoundException("CkTypeId not found in CkCache"));
        MockCkCacheService.Setup(c => c.GetRtCkType(It.IsAny<string>(), It.IsAny<RtCkId<CkTypeId>>()))
            .Throws(new KeyNotFoundException("RtCkTypeId not found in CkCache"));
        MockCkCacheService.Setup(c => c.GetCkType(It.IsAny<string>(), type.CkTypeId)).Returns(type);
        MockCkCacheService.Setup(c => c.GetRtCkType(It.IsAny<string>(), type.CkTypeId.ToRtCkId())).Returns(type);
        MockCkCacheService.Setup(c => c.GetCkTypes(It.IsAny<string>())).Returns([type]);
        CkGraphTestBuilder.SetupTryGetRtCkType(MockCkCacheService, type);

        MockTenantRepository
            .Setup(r => r.GetCkTypeGraphAsync(It.IsAny<RtCkId<CkTypeId>>()))
            .ReturnsAsync(type);
        MockTenantRepository
            .Setup(r => r.GetRtEntitiesByTypeAsync(It.IsAny<IOctoSession>(), It.IsAny<RtCkId<CkTypeId>>(),
                It.IsAny<RtEntityQueryOptions>(), It.IsAny<int?>(), It.IsAny<int?>()))
            .ReturnsAsync((IOctoSession _, RtCkId<CkTypeId> _, RtEntityQueryOptions _, int? skip, int? take) =>
                Page(skip, take));
        MockTenantRepository
            .Setup(r => r.GetRtEntityByRtIdAsync(It.IsAny<IOctoSession>(), It.IsAny<RtEntityId>()))
            .ReturnsAsync(new RtEntity { RtId = new OctoObjectId(OriginRtId), CkTypeId = type.CkTypeId.ToRtCkId() });
        MockTenantRepository
            .Setup(r => r.GetRtAssociationTargetsAsync(It.IsAny<IOctoSession>(), It.IsAny<OctoObjectId>(),
                It.IsAny<RtCkId<CkTypeId>>(), It.IsAny<RtCkId<CkAssociationRoleId>>(), It.IsAny<RtCkId<CkTypeId>>(),
                It.IsAny<GraphDirections>(), It.IsAny<IReadOnlyList<OctoObjectId>?>(),
                It.IsAny<RtEntityQueryOptions>(), It.IsAny<int?>(), It.IsAny<int?>()))
            .ReturnsAsync((IOctoSession _, OctoObjectId _, RtCkId<CkTypeId> _, RtCkId<CkAssociationRoleId> _,
                    RtCkId<CkTypeId> _, GraphDirections _, IReadOnlyList<OctoObjectId>? _, RtEntityQueryOptions _,
                    int? skip, int? take) => Page(skip, take));
        MockTenantRepository
            .Setup(r => r.GetRtEntityByRtIdAsync<RtPersistentQuery>(It.IsAny<IOctoSession>(),
                It.IsAny<OctoObjectId>()))
            .ReturnsAsync(new RtSimpleRtQuery { QueryCkTypeId = RuntimeId });
    }

    private ResultSet<RtEntity> Page(int? skip, int? take)
    {
        _engineCalls++;
        _seenSkip = skip;
        _seenTake = take;
        var items = Enumerable.Range(0, _returnedCount)
            .Select(_ => new RtEntity { RtId = OctoObjectId.GenerateNewId(), CkTypeId = new RtCkId<CkTypeId>(RuntimeId) })
            .ToList();
        return new ResultSet<RtEntity>(items, TotalCount, null, null);
    }

    /// <summary>One call of a query tool with (take, skip), mapped to its success flag, error and paging block.</summary>
    public delegate Task<(bool IsSuccess, string? Error, QueryPagingInfo? Paging)> PagedCall(
        RuntimeQueryPagingTests test, int? take, int? skip);

    public static TheoryData<string> Tools => new()
    {
        "query_entities", "query_entities_simple", "navigate_associations", "get_association_tree",
        "execute_runtime_query"
    };

    private static readonly Dictionary<string, (string TakeArgument, string SkipArgument, PagedCall Call)> Calls =
        new()
        {
            ["query_entities"] = ("limit", "offset", async (t, take, skip) =>
            {
                var r = await RuntimeEntityCrudTools.QueryEntities(t.MockServer.Object, FullId,
                    limit: take, offset: skip);
                return (r.IsSuccess, r.ErrorMessage, r.Paging);
            }),
            ["query_entities_simple"] = ("limit", "offset", async (t, take, skip) =>
            {
                var r = await RuntimeEntityCrudTools.QueryEntitiesSimple(t.MockServer.Object, FullId,
                    limit: take, offset: skip);
                return (r.IsSuccess, r.ErrorMessage, r.Paging);
            }),
            ["navigate_associations"] = ("take", "skip", async (t, take, skip) =>
            {
                var r = await RuntimeEntityCrudTools.NavigateAssociations(t.MockServer.Object, RuntimeId,
                    OriginRtId, "System/ParentChild", CkTypeAssociationDirectionDto.Inbound, RuntimeId,
                    take: take, skip: skip);
                return (r.IsSuccess, r.ErrorMessage, r.Paging);
            }),
            ["get_association_tree"] = ("rootTake", "rootSkip", async (t, take, skip) =>
            {
                var r = await RuntimeEntityCrudTools.GetAssociationTree(t.MockServer.Object, FullId,
                    "System/ParentChild", CkTypeAssociationDirectionDto.Inbound, maxDepth: 0,
                    rootTake: take, rootSkip: skip);
                return (r.IsSuccess, r.ErrorMessage, r.Paging);
            }),
            ["execute_runtime_query"] = ("take", "skip", async (t, take, skip) =>
            {
                var r = await RuntimeAggregationTools.ExecuteRuntimeQuery(t.MockServer.Object, QueryRtId,
                    skip: skip, take: take);
                return (r.IsSuccess, r.ErrorMessage, r.Paging);
            })
        };

    [Theory]
    [MemberData(nameof(Tools))]
    public async Task NoLimit_PassesTheDefaultPageSizeOf100_AndSaysHowToFetchTheNextPage(string tool)
    {
        var (isSuccess, error, paging) = await Calls[tool].Call(this, null, null);

        isSuccess.Should().BeTrue(error);
        _seenTake.Should().Be(100);
        _seenSkip.Should().Be(0);
        paging.Should().NotBeNull();
        paging!.Take.Should().Be(100);
        paging.DefaultTakeApplied.Should().BeTrue();
        paging.MaxTake.Should().Be(1000);
        paging.HasMore.Should().BeTrue();
        paging.NextSkip.Should().Be(100);
        paging.Hint.Should().Contain($"{Calls[tool].SkipArgument}=100");
    }

    [Theory]
    [MemberData(nameof(Tools))]
    public async Task Limit1000_IsAccepted(string tool)
    {
        var (isSuccess, error, paging) = await Calls[tool].Call(this, 1000, null);

        isSuccess.Should().BeTrue(error);
        _seenTake.Should().Be(1000);
        paging!.DefaultTakeApplied.Should().BeFalse();
    }

    [Theory]
    [MemberData(nameof(Tools))]
    public async Task Limit1001_IsRefusedWithTheNamedLimit_AndNoEngineQueryRuns(string tool)
    {
        var (isSuccess, error, _) = await Calls[tool].Call(this, 1001, null);

        isSuccess.Should().BeFalse();
        var (takeArgument, skipArgument, _) = Calls[tool];
        error.Should().StartWith($"{takeArgument} 1001 exceeds the maximum of 1000; page with {skipArgument}");
        _engineCalls.Should().Be(0);
        MockSecureSessionFactory.Verify(f => f.GetSessionAsync(It.IsAny<RtSecurityContext>()), Times.Never);
    }

    [Theory]
    [MemberData(nameof(Tools))]
    public async Task Skip_IsForwardedToTheEngine_AndTheLastPageHasNoNextSkip(string tool)
    {
        _returnedCount = 50;

        var (isSuccess, error, paging) = await Calls[tool].Call(this, 200, 200);

        isSuccess.Should().BeTrue(error);
        _seenSkip.Should().Be(200);
        _seenTake.Should().Be(200);
        paging!.Skip.Should().Be(200);
        paging.ReturnedCount.Should().Be(50);
        paging.HasMore.Should().BeFalse("200 skipped + 50 returned = the total of 250");
        paging.NextSkip.Should().BeNull();
        paging.Hint.Should().BeNull();
    }

    [Theory]
    [MemberData(nameof(Tools))]
    public async Task NegativeSkip_OrZeroLimit_IsRefused_AndNoEngineQueryRuns(string tool)
    {
        var negativeSkip = await Calls[tool].Call(this, null, -1);
        var zeroTake = await Calls[tool].Call(this, 0, null);

        negativeSkip.IsSuccess.Should().BeFalse();
        negativeSkip.Error.Should().Contain($"{Calls[tool].SkipArgument} -1 is negative");
        zeroTake.IsSuccess.Should().BeFalse();
        zeroTake.Error.Should().Contain($"{Calls[tool].TakeArgument} 0 is invalid");
        _engineCalls.Should().Be(0);
    }

    [Fact]
    public async Task ConfiguredMaximumBelow1000_IsEnforced()
    {
        TestServiceProvider.RegisterService(Options.Create(new DynamicToolOptions
        {
            MaxQueryResultLimit = 500, DefaultQueryLimit = 50
        }));

        var refused = await RuntimeEntityCrudTools.QueryEntities(MockServer.Object, FullId, limit: 501);
        var defaulted = await RuntimeEntityCrudTools.QueryEntities(MockServer.Object, FullId);

        refused.IsSuccess.Should().BeFalse();
        refused.ErrorMessage.Should().StartWith("limit 501 exceeds the maximum of 500");
        defaulted.IsSuccess.Should().BeTrue(defaulted.ErrorMessage);
        _seenTake.Should().Be(50);
        defaulted.Paging!.MaxTake.Should().Be(500);
    }

    [Fact]
    public async Task ConfiguredMaximumAbove1000_IsIgnored_TheToolsStillRefuseAbove1000()
    {
        TestServiceProvider.RegisterService(Options.Create(new DynamicToolOptions
        {
            MaxQueryResultLimit = 5000, DefaultQueryLimit = 2000
        }));

        var refused = await RuntimeEntityCrudTools.QueryEntities(MockServer.Object, FullId, limit: 5000);
        var defaulted = await RuntimeEntityCrudTools.QueryEntities(MockServer.Object, FullId);

        refused.IsSuccess.Should().BeFalse();
        refused.ErrorMessage.Should().StartWith("limit 5000 exceeds the maximum of 1000; page with offset");
        defaulted.IsSuccess.Should().BeTrue(defaulted.ErrorMessage);
        _seenTake.Should().Be(1000, "the default is lowered to the effective maximum");
    }

    [Fact]
    public async Task ExecuteRuntimeQuery_TakeAbove1000_IsRefusedBeforeThePersistedQueryIsLoaded()
    {
        var result = await RuntimeAggregationTools.ExecuteRuntimeQuery(MockServer.Object, QueryRtId, take: 5000);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().StartWith("take 5000 exceeds the maximum of 1000; page with skip");
        MockTenantRepository.Verify(r => r.GetRtEntityByRtIdAsync<RtPersistentQuery>(It.IsAny<IOctoSession>(),
            It.IsAny<OctoObjectId>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteRuntimeQuery_AggregationSubtype_HasNoPagingBlock()
    {
        var aggregation = new RtAggregationRtQuery { QueryCkTypeId = RuntimeId };
        aggregation.Columns.Add(new RtAggregationQueryColumnRecord
        {
            AttributePath = "Name", AggregationType = RtAggregationTypesEnum.Count
        });
        MockTenantRepository
            .Setup(r => r.GetRtEntityByRtIdAsync<RtPersistentQuery>(It.IsAny<IOctoSession>(),
                It.IsAny<OctoObjectId>()))
            .ReturnsAsync(aggregation);

        var result = await RuntimeAggregationTools.ExecuteRuntimeQuery(MockServer.Object, QueryRtId);

        result.Paging.Should().BeNull();
    }

    [Fact]
    public void EffectiveLimits_AreClampedToThePlatformMaximum()
    {
        new DynamicToolOptions().GetEffectiveMaxQueryResultLimit().Should().Be(1000);
        new DynamicToolOptions().GetEffectiveDefaultQueryLimit().Should().Be(100);
        new DynamicToolOptions { MaxQueryResultLimit = 5000 }.GetEffectiveMaxQueryResultLimit().Should().Be(1000);
        new DynamicToolOptions { MaxQueryResultLimit = 0 }.GetEffectiveMaxQueryResultLimit().Should().Be(1000);
        new DynamicToolOptions { DefaultQueryLimit = 0 }.GetEffectiveDefaultQueryLimit().Should().Be(100);
        new DynamicToolOptions { MaxQueryResultLimit = 20 }.GetEffectiveDefaultQueryLimit().Should().Be(20);
    }

    [Fact]
    public async Task HealthCheck_ReportsAMaximumAbove1000_AsDegraded()
    {
        var toolService = new Mock<IDynamicToolService>();
        toolService.Setup(t => t.GetDomainOptions()).Returns(new DomainToolOptions());
        toolService.Setup(t => t.ValidateQueryParameters(It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<DateTime?>(),
            It.IsAny<DateTime?>())).Returns((true, null));
        var healthCheck = new DynamicToolHealthCheck(
            toolService.Object,
            Options.Create(new DynamicToolOptions { MaxQueryResultLimit = 5000 }),
            NullLogger<DynamicToolHealthCheck>.Instance);

        var result = await healthCheck.CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken);

        result.Status.Should().Be(HealthStatus.Degraded);
        result.Description.Should().Contain("MaxQueryResultLimit 5000 exceeds the platform maximum of 1000");
        result.Data["max_query_limit"].Should().Be(1000);
    }
}
