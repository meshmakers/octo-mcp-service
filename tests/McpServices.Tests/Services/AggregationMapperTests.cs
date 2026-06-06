using FluentAssertions;
using Meshmakers.Octo.Backend.McpServices.Models.Aggregation;
using Meshmakers.Octo.Backend.McpServices.Services;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Xunit;

namespace McpServices.Tests.Services;

public class AggregationMapperTests
{
    // ── ToEngineFunction ─────────────────────────────────────────────────────

    [Theory]
    [InlineData(AggregationFunctionDto.count, AggregationFunction.Count)]
    [InlineData(AggregationFunctionDto.sum, AggregationFunction.Sum)]
    [InlineData(AggregationFunctionDto.avg, AggregationFunction.Average)]
    [InlineData(AggregationFunctionDto.min, AggregationFunction.Minimum)]
    [InlineData(AggregationFunctionDto.max, AggregationFunction.Maximum)]
    public void ToEngineFunction_MapsAllValues(AggregationFunctionDto dto, AggregationFunction engine)
    {
        AggregationMapper.ToEngineFunction(dto).Should().Be(engine);
    }

    // ── DeriveAlias ──────────────────────────────────────────────────────────

    [Fact]
    public void DeriveAlias_ExplicitAlias_IsUsed()
    {
        var c = new AggregationColumnDto { Alias = "myAlias", Function = AggregationFunctionDto.sum, AttributePath = "Power" };
        AggregationMapper.DeriveAlias(c).Should().Be("myAlias");
    }

    [Fact]
    public void DeriveAlias_Count_NoPath_Defaults_To_count()
    {
        var c = new AggregationColumnDto { Function = AggregationFunctionDto.count };
        AggregationMapper.DeriveAlias(c).Should().Be("count");
    }

    [Fact]
    public void DeriveAlias_SumWithPath_Defaults_To_FunctionUnderscorePath()
    {
        var c = new AggregationColumnDto { Function = AggregationFunctionDto.sum, AttributePath = "Power" };
        AggregationMapper.DeriveAlias(c).Should().Be("sum_Power");
    }

    [Fact]
    public void DeriveAlias_DotsInPath_AreReplaced_WithUnderscore()
    {
        var c = new AggregationColumnDto { Function = AggregationFunctionDto.avg, AttributePath = "Sensor.Reading.Value" };
        AggregationMapper.DeriveAlias(c).Should().Be("avg_Sensor_Reading_Value");
    }

    // ── Validate (aggregations) ──────────────────────────────────────────────

    [Fact]
    public void Validate_NullList_ReturnsError()
    {
        AggregationMapper.Validate(null).Should().Contain("At least one");
    }

    [Fact]
    public void Validate_EmptyList_ReturnsError()
    {
        AggregationMapper.Validate([]).Should().Contain("At least one");
    }

    [Fact]
    public void Validate_SumWithoutPath_ReturnsError()
    {
        var result = AggregationMapper.Validate([
            new() { Function = AggregationFunctionDto.sum, AttributePath = null }
        ]);
        result.Should().Contain("attributePath is required").And.Contain("sum");
    }

    [Fact]
    public void Validate_CountWithoutPath_IsValid()
    {
        AggregationMapper.Validate([
            new() { Function = AggregationFunctionDto.count }
        ]).Should().BeNull();
    }

    [Fact]
    public void Validate_DuplicateAliases_ReturnsError()
    {
        var result = AggregationMapper.Validate([
            new() { Function = AggregationFunctionDto.sum, AttributePath = "Power", Alias = "total" },
            new() { Function = AggregationFunctionDto.avg, AttributePath = "Power", Alias = "total" }
        ]);
        result.Should().Contain("Duplicate alias 'total'");
    }

    [Fact]
    public void Validate_DuplicateDerivedAliases_ReturnsError()
    {
        // Two count() columns without aliases both derive to "count" → conflict.
        var result = AggregationMapper.Validate([
            new() { Function = AggregationFunctionDto.count },
            new() { Function = AggregationFunctionDto.count }
        ]);
        result.Should().Contain("Duplicate alias 'count'");
    }

    [Fact]
    public void Validate_ValidMixedList_ReturnsNull()
    {
        AggregationMapper.Validate([
            new() { Function = AggregationFunctionDto.count },
            new() { Function = AggregationFunctionDto.sum, AttributePath = "Power" },
            new() { Function = AggregationFunctionDto.avg, AttributePath = "Temperature", Alias = "tempAvg" }
        ]).Should().BeNull();
    }

    // ── ValidateGroupBy ──────────────────────────────────────────────────────

    [Fact]
    public void ValidateGroupBy_NullList_ReturnsError()
    {
        AggregationMapper.ValidateGroupBy(null).Should().Contain("at least one");
    }

    [Fact]
    public void ValidateGroupBy_EmptyEntry_ReturnsError()
    {
        AggregationMapper.ValidateGroupBy(["valid", "  "]).Should().Contain("empty");
    }

    [Fact]
    public void ValidateGroupBy_DuplicateEntry_ReturnsError()
    {
        AggregationMapper.ValidateGroupBy(["FacilityId", "FacilityId"]).Should().Contain("Duplicate");
    }

    [Fact]
    public void ValidateGroupBy_DistinctPaths_ReturnsNull()
    {
        AggregationMapper.ValidateGroupBy(["FacilityId", "Region"]).Should().BeNull();
    }

    // ── ApplyToAggregationInput ──────────────────────────────────────────────

    [Fact]
    public void ApplyToAggregationInput_AllFunctions_FillRespectiveLists()
    {
        var input = new AggregationInput();
        AggregationMapper.ApplyToAggregationInput(input, [
            new() { Function = AggregationFunctionDto.count, AttributePath = "Id" },
            new() { Function = AggregationFunctionDto.sum, AttributePath = "Power" },
            new() { Function = AggregationFunctionDto.avg, AttributePath = "Temp" },
            new() { Function = AggregationFunctionDto.min, AttributePath = "MinV" },
            new() { Function = AggregationFunctionDto.max, AttributePath = "MaxV" }
        ]);

        input.CountAttributePathList.Should().ContainSingle().Which.Should().Be("Id");
        input.SumAttributePathList.Should().ContainSingle().Which.Should().Be("Power");
        input.AvgAttributePathList.Should().ContainSingle().Which.Should().Be("Temp");
        input.MinValueAttributePathList.Should().ContainSingle().Which.Should().Be("MinV");
        input.MaxValueAttributePathList.Should().ContainSingle().Which.Should().Be("MaxV");
    }

    // ── ToEngineColumns ──────────────────────────────────────────────────────

    [Fact]
    public void ToEngineColumns_MapsFunctionAndPath()
    {
        var result = AggregationMapper.ToEngineColumns([
            new() { Function = AggregationFunctionDto.sum, AttributePath = "Power" },
            new() { Function = AggregationFunctionDto.count }
        ]);

        result.Should().HaveCount(2);
        result[0].AttributePath.Should().Be("Power");
        result[0].Function.Should().Be(AggregationFunction.Sum);
        result[1].Function.Should().Be(AggregationFunction.Count);
        // Count without path: we substitute "*" as a placeholder for the engine's required string.
        result[1].AttributePath.Should().Be("*");
    }
}
