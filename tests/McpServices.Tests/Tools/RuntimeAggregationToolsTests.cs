using FluentAssertions;
using Meshmakers.Octo.Backend.McpServices.Models.Aggregation;
using Meshmakers.Octo.Backend.McpServices.Tools;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Engine.Repositories.Query;
using Moq;
using Xunit;

namespace McpServices.Tests.Tools;

public class RuntimeAggregationToolsTests : TestBase
{
    // CkTypeId format is Name-VersionUint, not SemVer.
    private const string CkTypeId = "EnergyCommunity-1/Sensor-1";

    private static AggregationResult MakeScalarResult(long count = 0,
        IEnumerable<StatisticsResult>? counts = null,
        IEnumerable<StatisticsResult>? sums = null,
        IEnumerable<StatisticsResult>? avgs = null,
        IEnumerable<StatisticsResult>? mins = null,
        IEnumerable<StatisticsResult>? maxs = null) =>
        new(count,
            counts ?? [], mins ?? [], maxs ?? [], avgs ?? [], sums ?? []);

    private static StatisticsResult Stat(string path, object? value) =>
        new() { AttributePath = path, Value = value };

    private void SetupRepoReturning(AggregationResult? scalar,
        IEnumerable<FieldAggregationResult>? grouped = null)
    {
        var result = new ResultSet<RtEntity>([], 0, scalar, grouped);
        MockTenantRepository
            .Setup(r => r.GetRtEntitiesByTypeAsync(
                It.IsAny<IOctoSession>(),
                It.IsAny<RtCkId<CkTypeId>>(),
                It.IsAny<RtEntityQueryOptions>(),
                It.IsAny<int?>(),
                It.IsAny<int?>()))
            .ReturnsAsync(result);
    }

    // ── query_entities_aggregation ──────────────────────────────────────────

    [Fact]
    public async Task QueryEntitiesAggregation_HappyPath_ReturnsScalarRow()
    {
        SetupRepoReturning(scalar: MakeScalarResult(
            count: 42,
            sums: [Stat("Power", 1000.0)],
            avgs: [Stat("Temperature", 22.5)]));

        var result = await RuntimeAggregationTools.QueryEntitiesAggregation(
            MockServer.Object,
            CkTypeId,
            aggregations:
            [
                new() { Function = AggregationFunctionDto.count, Alias = "n" },
                new() { Function = AggregationFunctionDto.sum, AttributePath = "Power" },
                new() { Function = AggregationFunctionDto.avg, AttributePath = "Temperature", Alias = "tempAvg" }
            ]);

        result.IsSuccess.Should().BeTrue(result.ErrorMessage ?? "");
        result.RowCount.Should().Be(1);
        var row = result.Rows.Single();
        row["n"].Should().Be(42L);
        row["sum_Power"].Should().Be(1000.0);
        row["tempAvg"].Should().Be(22.5);
    }

    [Fact]
    public async Task QueryEntitiesAggregation_NoEngineResult_ReturnsEmpty()
    {
        SetupRepoReturning(scalar: null);

        var result = await RuntimeAggregationTools.QueryEntitiesAggregation(
            MockServer.Object,
            CkTypeId,
            aggregations: [new() { Function = AggregationFunctionDto.count }]);

        result.IsSuccess.Should().BeTrue();
        result.Rows.Should().BeEmpty();
    }

    [Fact]
    public async Task QueryEntitiesAggregation_MissingCkTypeId_ReturnsValidationError()
    {
        var result = await RuntimeAggregationTools.QueryEntitiesAggregation(
            MockServer.Object,
            ckTypeId: "",
            aggregations: [new() { Function = AggregationFunctionDto.count }]);

        result.IsSuccess.Should().BeFalse();
        MockTenantRepository.Verify(r => r.GetRtEntitiesByTypeAsync(
            It.IsAny<IOctoSession>(), It.IsAny<RtCkId<CkTypeId>>(),
            It.IsAny<RtEntityQueryOptions>(), It.IsAny<int?>(), It.IsAny<int?>()),
            Times.Never);
    }

    [Fact]
    public async Task QueryEntitiesAggregation_EmptyAggregations_ReturnsValidationError()
    {
        var result = await RuntimeAggregationTools.QueryEntitiesAggregation(
            MockServer.Object,
            CkTypeId,
            aggregations: []);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("At least one");
    }

    [Fact]
    public async Task QueryEntitiesAggregation_SumWithoutPath_ReturnsValidationError()
    {
        var result = await RuntimeAggregationTools.QueryEntitiesAggregation(
            MockServer.Object,
            CkTypeId,
            aggregations: [new() { Function = AggregationFunctionDto.sum, AttributePath = null }]);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("attributePath is required");
    }

    // ── query_entities_grouping ─────────────────────────────────────────────

    [Fact]
    public async Task QueryEntitiesGrouping_HappyPath_ReturnsOneRowPerGroup()
    {
        var group1 = new FieldAggregationResult(
            groupByAttributePaths: ["FacilityId"],
            keys: ["F1"],
            count: 10,
            countStatistics: [],
            minStatistics: [],
            maxStatistics: [],
            avgStatistics: [Stat("Power", 5.0)],
            sumStatistics: []);

        var group2 = new FieldAggregationResult(
            groupByAttributePaths: ["FacilityId"],
            keys: ["F2"],
            count: 15,
            countStatistics: [],
            minStatistics: [],
            maxStatistics: [],
            avgStatistics: [Stat("Power", 7.0)],
            sumStatistics: []);

        SetupRepoReturning(scalar: null, grouped: [group1, group2]);

        var result = await RuntimeAggregationTools.QueryEntitiesGrouping(
            MockServer.Object,
            CkTypeId,
            groupByAttributePaths: ["FacilityId"],
            aggregations:
            [
                new() { Function = AggregationFunctionDto.count },
                new() { Function = AggregationFunctionDto.avg, AttributePath = "Power" }
            ]);

        result.IsSuccess.Should().BeTrue();
        result.RowCount.Should().Be(2);

        result.Rows[0]["FacilityId"].Should().Be("F1");
        result.Rows[0]["count"].Should().Be(10L);
        result.Rows[0]["avg_Power"].Should().Be(5.0);

        result.Rows[1]["FacilityId"].Should().Be("F2");
        result.Rows[1]["count"].Should().Be(15L);
        result.Rows[1]["avg_Power"].Should().Be(7.0);
    }

    [Fact]
    public async Task QueryEntitiesGrouping_EmptyGroupByList_ReturnsValidationError()
    {
        var result = await RuntimeAggregationTools.QueryEntitiesGrouping(
            MockServer.Object,
            CkTypeId,
            groupByAttributePaths: [],
            aggregations: [new() { Function = AggregationFunctionDto.count }]);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("at least one");
    }

    [Fact]
    public async Task QueryEntitiesGrouping_DuplicateGroupByPath_ReturnsValidationError()
    {
        var result = await RuntimeAggregationTools.QueryEntitiesGrouping(
            MockServer.Object,
            CkTypeId,
            groupByAttributePaths: ["FacilityId", "FacilityId"],
            aggregations: [new() { Function = AggregationFunctionDto.count }]);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Duplicate");
    }

    [Fact]
    public async Task QueryEntitiesGrouping_NoEngineResult_ReturnsEmpty()
    {
        SetupRepoReturning(scalar: null, grouped: null);

        var result = await RuntimeAggregationTools.QueryEntitiesGrouping(
            MockServer.Object,
            CkTypeId,
            groupByAttributePaths: ["FacilityId"],
            aggregations: [new() { Function = AggregationFunctionDto.count }]);

        result.IsSuccess.Should().BeTrue();
        result.Rows.Should().BeEmpty();
    }
}
