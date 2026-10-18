using FluentAssertions;
using Meshmakers.Octo.Backend.McpServices.Models;
using Meshmakers.Octo.Backend.McpServices.Models.Aggregation;
using Meshmakers.Octo.Backend.McpServices.Services;
using Meshmakers.Octo.Backend.McpServices.Tools;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Engine.Repositories.Query;
using Moq;
using Xunit;

namespace McpServices.Tests.Tools;

/// <summary>
///     AB#6998: the ckTypeId that <c>search_types</c> / <c>get_available_types</c> return is accepted by the type
///     tools (<c>get_type_schema</c>, <c>get_available_archive_paths</c>, the aggregation tools), also for a tenant
///     that holds a minor version of the model (2.6.0). Short spellings resolve when they are unique; unknown or
///     ambiguous ids name the full candidate ids.
/// </summary>
public class CkTypeIdRoundTripTests : TestBase
{
    private const string FullMachine = "Industry.Basic-2.6.0/Machine-1";

    private readonly CkTypeGraph _machine = CkGraphTestBuilder.BuildType(FullMachine);
    private readonly CkTypeGraph _sensor = CkGraphTestBuilder.BuildType("Industry.Basic-2.6.0/Sensor-1");

    private void GivenInstalled(params CkTypeGraph[] types)
    {
        // Like the real cache: the exact getters throw for an id that is not installed.
        MockCkCacheService
            .Setup(c => c.GetCkType(It.IsAny<string>(), It.IsAny<CkId<CkTypeId>>()))
            .Throws(new KeyNotFoundException("CkTypeId not found in CkCache"));
        MockCkCacheService
            .Setup(c => c.GetRtCkType(It.IsAny<string>(), It.IsAny<RtCkId<CkTypeId>>()))
            .Throws(new KeyNotFoundException("RtCkTypeId not found in CkCache"));
        foreach (var type in types)
        {
            var graph = type;
            MockCkCacheService
                .Setup(c => c.GetCkType(It.IsAny<string>(), graph.CkTypeId)).Returns(graph);
            MockCkCacheService
                .Setup(c => c.GetRtCkType(It.IsAny<string>(), graph.CkTypeId.ToRtCkId())).Returns(graph);
        }

        MockCkCacheService.Setup(c => c.GetCkTypes(It.IsAny<string>())).Returns(types);
        MockCkCacheService
            .Setup(c => c.GetCkTypeQueryColumnPaths(It.IsAny<string>(), It.IsAny<CkId<CkTypeId>>(),
                It.IsAny<CkTypeQueryColumnOptions>()))
            .Returns([]);
    }

    [Fact]
    public async Task SearchTypes_ReturnsFullId_AndRtId_ThatTheTypeToolsAccept()
    {
        GivenInstalled(_machine, _sensor);

        var search = await SchemaDiscoveryTools.SearchTypes(MockServer.Object, "machine");

        search.IsSuccess.Should().BeTrue(search.ErrorMessage ?? "");
        var hit = search.Matches!.Single();
        hit.CkTypeId.Should().Be(FullMachine);
        hit.RtCkTypeId.Should().Be("Industry.Basic/Machine");

        var schema = await SchemaDiscoveryTools.GetTypeSchema(MockServer.Object, hit.CkTypeId);
        schema.IsSuccess.Should().BeTrue(schema.ErrorMessage ?? "");
        schema.CkTypeId.Should().Be(hit.CkTypeId, "get_type_schema returns the same id format as the search");

        var viaRtId = await SchemaDiscoveryTools.GetTypeSchema(MockServer.Object, hit.RtCkTypeId!);
        viaRtId.IsSuccess.Should().BeTrue(viaRtId.ErrorMessage ?? "");

        var paths = await SchemaDiscoveryTools.GetAvailableArchivePaths(MockServer.Object, hit.CkTypeId);
        paths.IsSuccess.Should().BeTrue(paths.ErrorMessage ?? "");
    }

    [Theory]
    [InlineData("Industry.Basic-2/Machine")] // what search_types used to return (major only, no element version)
    [InlineData("Industry.Basic-2.6/Machine-1")]
    [InlineData("Industry.Basic-2.6.0/Machine")]
    [InlineData("Industry.Basic/Machine")]
    public async Task GetTypeSchema_ShortSpelling_ResolvesToTheInstalledType(string shortId)
    {
        GivenInstalled(_machine, _sensor);

        var schema = await SchemaDiscoveryTools.GetTypeSchema(MockServer.Object, shortId);

        schema.IsSuccess.Should().BeTrue(schema.ErrorMessage ?? "");
        schema.CkTypeId.Should().Be(FullMachine);
    }

    [Fact]
    public async Task GetTypeSchema_UnknownType_NamesTheFullIdOfTheCandidate()
    {
        GivenInstalled(_machine, _sensor);

        var schema = await SchemaDiscoveryTools.GetTypeSchema(MockServer.Object, "Industry.Basic-2.6.0/Machin-1");

        schema.IsSuccess.Should().BeFalse();
        schema.ErrorMessage.Should().Contain("not found").And.Contain("Did you mean").And.Contain(FullMachine);
    }

    [Fact]
    public async Task GetTypeSchema_WrongModelPrefix_NamesTheFullIdOfTheTypeInAnotherModel()
    {
        GivenInstalled(_machine, _sensor);

        var schema = await SchemaDiscoveryTools.GetTypeSchema(MockServer.Object, "Basic-2.5.0/Machine-1");

        schema.IsSuccess.Should().BeFalse();
        schema.ErrorMessage.Should().Contain(FullMachine);
    }

    [Fact]
    public async Task GetTypeSchema_TwoInstalledVersions_AmbiguousShortIdListsBoth()
    {
        var older = CkGraphTestBuilder.BuildType("Industry.Basic-2.5.0/Machine-1");
        GivenInstalled(_machine, older);

        var schema = await SchemaDiscoveryTools.GetTypeSchema(MockServer.Object, "Industry.Basic-2/Machine");

        schema.IsSuccess.Should().BeFalse();
        schema.ErrorMessage.Should().Contain("ambiguous")
            .And.Contain("Industry.Basic-2.5.0/Machine-1").And.Contain(FullMachine);

        var exact = await SchemaDiscoveryTools.GetTypeSchema(MockServer.Object, FullMachine);
        exact.IsSuccess.Should().BeTrue(exact.ErrorMessage ?? "");
    }

    [Fact]
    public async Task AggregationTool_FullIdFromSearch_IsPassedToTheEngineAsRuntimeId()
    {
        GivenInstalled(_machine, _sensor);
        var result = new ResultSet<RtEntity>([], 0, new AggregationResult(7, [], [], [], [], []), null);
        RtCkId<CkTypeId>? seen = null;
        MockTenantRepository
            .Setup(r => r.GetRtEntitiesByTypeAsync(
                It.IsAny<IOctoSession>(), It.IsAny<RtCkId<CkTypeId>>(), It.IsAny<RtEntityQueryOptions>(),
                It.IsAny<int?>(), It.IsAny<int?>()))
            .Callback<IOctoSession, RtCkId<CkTypeId>, RtEntityQueryOptions, int?, int?>((_, id, _, _, _) => seen = id)
            .ReturnsAsync(result);

        var response = await RuntimeAggregationTools.QueryEntitiesAggregation(
            MockServer.Object, FullMachine,
            aggregations: [new() { Function = AggregationFunctionDto.count }]);

        response.IsSuccess.Should().BeTrue(response.ErrorMessage ?? "");
        seen.Should().NotBeNull();
        seen!.ToString().Should().Be("Industry.Basic/Machine");
    }

    [Fact]
    public async Task AggregationTool_UnknownType_NamesCandidates()
    {
        GivenInstalled(_machine, _sensor);

        var response = await RuntimeAggregationTools.QueryEntitiesAggregation(
            MockServer.Object, "Industry.Basic-2.6.0/Machin-1",
            aggregations: [new() { Function = AggregationFunctionDto.count }]);

        response.IsSuccess.Should().BeFalse();
        response.ErrorMessage.Should().Contain(FullMachine);
    }

    [Fact]
    public async Task AssociationTree_FullIdFromSearch_ResolvesAndUnknownIdNamesCandidates()
    {
        GivenInstalled(_machine, _sensor);
        var result = new ResultSet<RtEntity>([], 0, null, null);
        RtCkId<CkTypeId>? seen = null;
        MockTenantRepository
            .Setup(r => r.GetRtEntitiesByTypeAsync(
                It.IsAny<IOctoSession>(), It.IsAny<RtCkId<CkTypeId>>(), It.IsAny<RtEntityQueryOptions>(),
                It.IsAny<int?>(), It.IsAny<int?>()))
            .Callback<IOctoSession, RtCkId<CkTypeId>, RtEntityQueryOptions, int?, int?>((_, id, _, _, _) => seen = id)
            .ReturnsAsync(result);

        var ok = await RuntimeEntityCrudTools.GetAssociationTree(
            MockServer.Object, FullMachine, "System/ParentChild", CkTypeAssociationDirectionDto.Inbound);
        ok.IsSuccess.Should().BeTrue(ok.ErrorMessage ?? "");
        seen!.ToString().Should().Be("Industry.Basic/Machine");

        var unknown = await RuntimeEntityCrudTools.GetAssociationTree(
            MockServer.Object, "Industry.Basic-2.6.0/Machin-1", "System/ParentChild",
            CkTypeAssociationDirectionDto.Inbound);
        unknown.IsSuccess.Should().BeFalse();
        unknown.ErrorMessage.Should().Contain(FullMachine);
    }

    [Fact]
    public void Resolver_RejectsIdWithoutModelSeparator()
    {
        GivenInstalled(_machine);

        var resolution = CkTypeIdResolver.Resolve(MockCkCacheService.Object, "test-tenant", "Machine");

        resolution.Type.Should().BeNull();
        resolution.Error.Should().Contain("Model-<version>/Type-<n>");
    }
}
