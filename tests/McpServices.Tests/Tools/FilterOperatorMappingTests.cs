using FluentAssertions;
using Meshmakers.Octo.Backend.McpServices.Models.Aggregation;
using Meshmakers.Octo.Backend.McpServices.Models.Filters;
using Meshmakers.Octo.Backend.McpServices.Tools;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Engine.Repositories.Query;
using Moq;
using Xunit;

namespace McpServices.Tests.Tools;

/// <summary>
///     Verifies that every <see cref="FilterOperatorDto"/> value maps to the expected engine operator and
///     that the runtime-side filter builder no longer silently drops operators it doesn't recognize. Locks
///     down the v1.5.1 operator-coverage extension (ROADMAP #2).
/// </summary>
public class FilterOperatorMappingTests : TestBase
{
    private const string CkTypeId = "EnergyCommunity-1/Sensor-1";

    // ── Stream-data side: MapFilterOperator is the single point of translation ──────────────

    [Theory]
    [InlineData(FilterOperatorDto.Equals, FieldFilterOperator.Equals)]
    [InlineData(FilterOperatorDto.NotEquals, FieldFilterOperator.NotEquals)]
    [InlineData(FilterOperatorDto.GreaterThan, FieldFilterOperator.GreaterThan)]
    [InlineData(FilterOperatorDto.GreaterThanOrEqual, FieldFilterOperator.GreaterEqualThan)]
    [InlineData(FilterOperatorDto.LessThan, FieldFilterOperator.LessThan)]
    [InlineData(FilterOperatorDto.LessThanOrEqual, FieldFilterOperator.LessEqualThan)]
    [InlineData(FilterOperatorDto.In, FieldFilterOperator.In)]
    [InlineData(FilterOperatorDto.NotIn, FieldFilterOperator.NotIn)]
    [InlineData(FilterOperatorDto.Between, FieldFilterOperator.Between)]
    [InlineData(FilterOperatorDto.Contains, FieldFilterOperator.Contains)]
    [InlineData(FilterOperatorDto.StartsWith, FieldFilterOperator.StartsWith)]
    [InlineData(FilterOperatorDto.EndsWith, FieldFilterOperator.EndsWith)]
    [InlineData(FilterOperatorDto.IsNull, FieldFilterOperator.IsNull)]
    [InlineData(FilterOperatorDto.IsNotNull, FieldFilterOperator.IsNotNull)]
    [InlineData(FilterOperatorDto.Regex, FieldFilterOperator.MatchRegEx)]
    [InlineData(FilterOperatorDto.Like, FieldFilterOperator.Like)]
    [InlineData(FilterOperatorDto.AnyEq, FieldFilterOperator.AnyEq)]
    [InlineData(FilterOperatorDto.AnyLike, FieldFilterOperator.AnyLike)]
    public void StreamData_MapFilterOperator_CoversEveryDtoValue(
        FilterOperatorDto dto, FieldFilterOperator expected)
    {
        StreamDataAggregationTools.MapFilterOperator(dto).Should().Be(expected);
    }

    [Fact]
    public void StreamData_MapFilterOperator_UnknownValueThrows()
    {
        // Reserved sentinel — an unknown integer cast to the enum must throw rather than degrade to Equals
        // (the old fallback, which masked typos).
        var unknown = (FilterOperatorDto)999;
        Action act = () => StreamDataAggregationTools.MapFilterOperator(unknown);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    // ── Runtime side: BuildTypedFilters routes each operator into FieldFilterCriteria ────────

    [Theory]
    [InlineData(FilterOperatorDto.IsNull)]
    [InlineData(FilterOperatorDto.IsNotNull)]
    [InlineData(FilterOperatorDto.Regex)]
    [InlineData(FilterOperatorDto.Like)]
    [InlineData(FilterOperatorDto.AnyEq)]
    [InlineData(FilterOperatorDto.AnyLike)]
    public async Task Runtime_BuildTypedFilters_NewOperatorRunsWithoutThrowing(
        FilterOperatorDto op)
    {
        // The runtime side previously silently dropped these operators (the case wasn't in the switch).
        // After ROADMAP #2 they each route to the matching FieldFilterCriteria extension and the engine
        // call succeeds. Empty result-set is fine; we only assert the dispatch doesn't blow up.
        MockTenantRepository
            .Setup(r => r.GetRtEntitiesByTypeAsync(
                It.IsAny<IOctoSession>(),
                It.IsAny<RtCkId<CkTypeId>>(),
                It.IsAny<RtEntityQueryOptions>(),
                It.IsAny<int?>(),
                It.IsAny<int?>()))
            .ReturnsAsync(new ResultSet<RtEntity>([], 0,
                new AggregationResult(0, [], [], [], [], []),
                null));

        var filters = new FieldFilterCriteriaDto
        {
            Operator = LogicalOperatorDto.And,
            Fields =
            [
                new FieldFilterDto
                {
                    AttributePath = "Name",
                    Operator = op,
                    Value = op is FilterOperatorDto.Like or FilterOperatorDto.Regex
                                    or FilterOperatorDto.AnyEq or FilterOperatorDto.AnyLike
                        ? "%inverter%"
                        : null
                }
            ]
        };

        var result = await RuntimeAggregationTools.QueryEntitiesAggregation(
            MockServer.Object,
            CkTypeId,
            aggregations: [new AggregationColumnDto { Function = AggregationFunctionDto.count }],
            filters: filters);

        result.IsSuccess.Should().BeTrue(result.ErrorMessage ?? "");
    }
}
