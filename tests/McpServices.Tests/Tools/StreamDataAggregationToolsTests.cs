using FluentAssertions;
using Meshmakers.Octo.Backend.McpServices.Models.Aggregation;
using Meshmakers.Octo.Backend.McpServices.Services;
using Meshmakers.Octo.Backend.McpServices.Tools;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Moq;
using Xunit;

namespace McpServices.Tests.Tools;

public class StreamDataAggregationToolsTests : TestBase
{
    private const string ArchiveRtId = "507f1f77bcf86cd799439011";
    // CkTypeId format is Name-VersionUint, not SemVer.
    private static readonly RtCkId<CkTypeId> SensorCkType =
        new("EnergyCommunity-1/Sensor-1");

    private readonly Mock<ITenantContext> _mockTenantContext = new();
    private readonly Mock<IStreamDataRepository> _mockStreamRepo = new();
    private readonly Mock<IArchiveRuntimeStore> _mockArchiveStore = new();

    public StreamDataAggregationToolsTests()
    {
        MockTenantResolution
            .Setup(t => t.GetTenantContextAsync(It.IsAny<string?>()))
            .ReturnsAsync(_mockTenantContext.Object);
        _mockTenantContext.Setup(c => c.TenantId).Returns("test-tenant");
        _mockTenantContext.Setup(c => c.GetStreamDataRepository()).Returns(_mockStreamRepo.Object);
        _mockTenantContext.Setup(c => c.GetArchiveRuntimeStore()).Returns(_mockArchiveStore.Object);

        var snapshot = new ArchiveSnapshot(
            RtId: new OctoObjectId(ArchiveRtId),
            TargetCkTypeId: SensorCkType,
            Status: CkArchiveStatus.Activated,
            RtWellKnownName: "TestArchive",
            Columns: []);
        _mockArchiveStore
            .Setup(s => s.GetAsync(It.IsAny<OctoObjectId>()))
            .ReturnsAsync(snapshot);
    }

    private static StreamDataQueryResult Result(params StreamDataRow[] rows) =>
        new() { Rows = rows, TotalCount = rows.Length };

    private static StreamDataRow Row(DateTime? ts = null,
        params (string Key, object? Value)[] values) =>
        new()
        {
            Timestamp = ts,
            Values = values.ToDictionary(v => v.Key, v => v.Value)
        };

    // ── Common context-resolution failures ──────────────────────────────────

    [Fact]
    public async Task Simple_StreamDataNotEnabled_ReturnsError()
    {
        _mockTenantContext.Setup(c => c.GetStreamDataRepository())
            .Returns((IStreamDataRepository?)null);

        var result = await StreamDataAggregationTools.QuerySimple(
            MockServer.Object, ArchiveRtId, ["Power"]);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("not enabled");
    }

    [Fact]
    public async Task Simple_ArchiveNotFound_ReturnsError()
    {
        _mockArchiveStore
            .Setup(s => s.GetAsync(It.IsAny<OctoObjectId>()))
            .ReturnsAsync((ArchiveSnapshot?)null);

        var result = await StreamDataAggregationTools.QuerySimple(
            MockServer.Object, ArchiveRtId, ["Power"]);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("not found");
    }

    [Fact]
    public async Task Simple_MissingArchiveRtId_ReturnsValidationError()
    {
        var result = await StreamDataAggregationTools.QuerySimple(
            MockServer.Object, archiveRtId: "", columnPaths: ["x"]);

        result.IsSuccess.Should().BeFalse();
        _mockStreamRepo.Verify(r => r.ExecuteQueryAsync(
            It.IsAny<OctoObjectId>(), It.IsAny<StreamDataQueryOptions>()), Times.Never);
    }

    [Fact]
    public async Task Simple_EmptyColumnPaths_ReturnsValidationError()
    {
        var result = await StreamDataAggregationTools.QuerySimple(
            MockServer.Object, ArchiveRtId, columnPaths: []);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("at least one");
    }

    // ── Simple ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Simple_HappyPath_ProjectsRows()
    {
        var ts = new DateTime(2026, 6, 6, 12, 0, 0, DateTimeKind.Utc);
        _mockStreamRepo
            .Setup(r => r.ExecuteQueryAsync(It.IsAny<OctoObjectId>(),
                It.IsAny<StreamDataQueryOptions>()))
            .ReturnsAsync(Result(Row(ts, ("Power", 100.0), ("Temperature", 22.0))));

        var result = await StreamDataAggregationTools.QuerySimple(
            MockServer.Object, ArchiveRtId, ["Power", "Temperature"]);

        result.IsSuccess.Should().BeTrue(result.ErrorMessage ?? "");
        result.TotalCount.Should().Be(1);
        result.Rows.Single().Timestamp.Should().Be(ts);
        result.Rows.Single().Values["Power"].Should().Be(100.0);
    }

    // ── Aggregation (scalar) ────────────────────────────────────────────────

    [Fact]
    public async Task Aggregation_HappyPath_MapsEngineColumnKeyToAlias()
    {
        // The CrateDB repository keys aggregation results "{column}_{token}" (AB#7919).
        var row = Row(values:
        [
            ("power_sum", 1000.0),
            ("temperature_avg", 22.5)
        ]);
        _mockStreamRepo
            .Setup(r => r.ExecuteAggregationQueryAsync(It.IsAny<OctoObjectId>(),
                It.IsAny<StreamDataAggregationQueryOptions>()))
            .ReturnsAsync(Result(row));

        var result = await StreamDataAggregationTools.QueryAggregation(
            MockServer.Object, ArchiveRtId,
            aggregations:
            [
                new() { Function = AggregationFunctionDto.sum, AttributePath = "Power" },
                new() { Function = AggregationFunctionDto.avg, AttributePath = "Temperature", Alias = "tempAvg" }
            ]);

        result.IsSuccess.Should().BeTrue(result.ErrorMessage ?? "");
        result.Rows.Single()["sum_Power"].Should().Be(1000.0);
        result.Rows.Single()["tempAvg"].Should().Be(22.5);
    }

    [Fact]
    public async Task Aggregation_EmptyAggregations_ReturnsValidationError()
    {
        var result = await StreamDataAggregationTools.QueryAggregation(
            MockServer.Object, ArchiveRtId, aggregations: []);

        result.IsSuccess.Should().BeFalse();
    }

    // ── Grouping ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Grouping_HappyPath_ProjectsGroupKeysAndAliases()
    {
        var row1 = Row(values:
        [
            ("facilityid", "F1"),
            ("power_sum", 500.0)
        ]);
        var row2 = Row(values:
        [
            ("facilityid", "F2"),
            ("power_sum", 750.0)
        ]);
        _mockStreamRepo
            .Setup(r => r.ExecuteGroupedAggregationQueryAsync(It.IsAny<OctoObjectId>(),
                It.IsAny<StreamDataGroupedAggregationQueryOptions>()))
            .ReturnsAsync(Result(row1, row2));

        var result = await StreamDataAggregationTools.QueryGrouping(
            MockServer.Object, ArchiveRtId,
            groupByAttributePaths: ["FacilityId"],
            aggregations: [new() { Function = AggregationFunctionDto.sum, AttributePath = "Power" }]);

        result.IsSuccess.Should().BeTrue(result.ErrorMessage ?? "");
        result.Rows.Should().HaveCount(2);
        result.Rows[0]["FacilityId"].Should().Be("F1");
        result.Rows[0]["sum_Power"].Should().Be(500.0);
        result.Rows[1]["FacilityId"].Should().Be("F2");
        result.Rows[1]["sum_Power"].Should().Be(750.0);
    }

    [Fact]
    public async Task Grouping_EmptyGroupByList_ReturnsValidationError()
    {
        var result = await StreamDataAggregationTools.QueryGrouping(
            MockServer.Object, ArchiveRtId,
            groupByAttributePaths: [],
            aggregations: [new() { Function = AggregationFunctionDto.count }]);

        result.IsSuccess.Should().BeFalse();
    }

    // ── Downsampling ────────────────────────────────────────────────────────

    [Fact]
    public async Task Downsampling_HappyPath_ReturnsBuckets()
    {
        var b1 = new DateTime(2026, 6, 6, 0, 0, 0, DateTimeKind.Utc);
        var b2 = b1.AddHours(1);

        _mockStreamRepo
            .Setup(r => r.ExecuteDownsamplingQueryAsync(It.IsAny<OctoObjectId>(),
                It.IsAny<StreamDataDownsamplingQueryOptions>()))
            .ReturnsAsync(Result(
                Row(b1, ("power_avg", 1.0)),
                Row(b2, ("power_avg", 2.0))));

        var result = await StreamDataAggregationTools.QueryDownsampling(
            MockServer.Object, ArchiveRtId,
            aggregations: [new() { Function = AggregationFunctionDto.avg, AttributePath = "Power" }],
            from: b1, to: b1.AddDays(1), limit: 24);

        result.IsSuccess.Should().BeTrue(result.ErrorMessage ?? "");
        result.RowCount.Should().Be(2);
        result.Rows[0]["bucketStart"].Should().Be(b1);
        result.Rows[0]["avg_Power"].Should().Be(1.0);
        result.Rows[1]["bucketStart"].Should().Be(b2);
        result.Rows[1]["avg_Power"].Should().Be(2.0);
    }

    [Fact]
    public async Task Downsampling_FromAfterTo_ReturnsValidationError()
    {
        var ts = DateTime.UtcNow;
        var result = await StreamDataAggregationTools.QueryDownsampling(
            MockServer.Object, ArchiveRtId,
            aggregations: [new() { Function = AggregationFunctionDto.avg, AttributePath = "Power" }],
            from: ts.AddDays(1), to: ts, limit: 10);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("from").And.Contain("less than");
    }

    [Fact]
    public async Task Downsampling_ZeroLimit_ReturnsValidationError()
    {
        var ts = DateTime.UtcNow;
        var result = await StreamDataAggregationTools.QueryDownsampling(
            MockServer.Object, ArchiveRtId,
            aggregations: [new() { Function = AggregationFunctionDto.avg, AttributePath = "Power" }],
            from: ts, to: ts.AddDays(1), limit: 0);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("limit");
    }

    // ── Integral (AB#7919) ──────────────────────────────────────────────────

    private static readonly DateTime WindowFrom = new(2026, 6, 1, 10, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime WindowTo = new(2026, 6, 1, 11, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Aggregation_Integral_PassesIntegralAndWindowToTheEngine_AndMapsTheResult()
    {
        StreamDataAggregationQueryOptions? captured = null;
        _mockStreamRepo
            .Setup(r => r.ExecuteAggregationQueryAsync(It.IsAny<OctoObjectId>(),
                It.IsAny<StreamDataAggregationQueryOptions>()))
            .Callback<OctoObjectId, StreamDataAggregationQueryOptions>((_, o) => captured = o)
            .ReturnsAsync(Result(Row(values: [("activepower_integral", 1650.0)])));

        var result = await StreamDataAggregationTools.QueryAggregation(
            MockServer.Object, ArchiveRtId,
            aggregations: [new() { Function = AggregationFunctionDto.integral, AttributePath = "ActivePower" }],
            from: WindowFrom, to: WindowTo);

        result.IsSuccess.Should().BeTrue(result.ErrorMessage ?? "");
        result.Rows.Single()["integral_ActivePower"].Should().Be(1650.0);
        captured.Should().NotBeNull();
        captured!.AggregationColumns.Should().ContainSingle()
            .Which.Function.Should().Be(AggregationFunction.Integral);
        captured.From.Should().Be(WindowFrom);
        captured.To.Should().Be(WindowTo);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task Aggregation_IntegralWithoutWindow_IsRefusedWithoutAnEngineCall(bool withFrom, bool withTo)
    {
        var result = await StreamDataAggregationTools.QueryAggregation(
            MockServer.Object, ArchiveRtId,
            aggregations: [new() { Function = AggregationFunctionDto.integral, AttributePath = "ActivePower" }],
            from: withFrom ? WindowFrom : null, to: withTo ? WindowTo : null);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("integral").And.Contain("from and to");
        _mockStreamRepo.Verify(r => r.ExecuteAggregationQueryAsync(
            It.IsAny<OctoObjectId>(), It.IsAny<StreamDataAggregationQueryOptions>()), Times.Never);
    }

    [Fact]
    public async Task Aggregation_IntegralWithInvertedWindow_IsRefused()
    {
        var result = await StreamDataAggregationTools.QueryAggregation(
            MockServer.Object, ArchiveRtId,
            aggregations: [new() { Function = AggregationFunctionDto.integral, AttributePath = "ActivePower" }],
            from: WindowTo, to: WindowFrom);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("less than");
    }

    [Fact]
    public async Task Grouping_Integral_PerGroup()
    {
        _mockStreamRepo
            .Setup(r => r.ExecuteGroupedAggregationQueryAsync(It.IsAny<OctoObjectId>(),
                It.IsAny<StreamDataGroupedAggregationQueryOptions>()))
            .ReturnsAsync(Result(
                Row(values: [("rtId", "a"), ("activepower_integral", 900.0)]),
                Row(values: [("rtId", "b"), ("activepower_integral", 750.0)])));

        var result = await StreamDataAggregationTools.QueryGrouping(
            MockServer.Object, ArchiveRtId,
            groupByAttributePaths: ["rtId"],
            aggregations: [new() { Function = AggregationFunctionDto.integral, AttributePath = "ActivePower", Alias = "energyWh" }],
            from: WindowFrom, to: WindowTo);

        result.IsSuccess.Should().BeTrue(result.ErrorMessage ?? "");
        result.Rows.Select(r => (r["rtId"], r["energyWh"])).Should().Equal(("a", 900.0), ("b", 750.0));
    }

    [Fact]
    public async Task Grouping_IntegralWithoutWindow_IsRefused()
    {
        var result = await StreamDataAggregationTools.QueryGrouping(
            MockServer.Object, ArchiveRtId,
            groupByAttributePaths: ["rtId"],
            aggregations: [new() { Function = AggregationFunctionDto.integral, AttributePath = "ActivePower" }]);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("from and to");
    }

    [Fact]
    public async Task Downsampling_Integral_IsRefusedWithoutAnEngineCall()
    {
        var result = await StreamDataAggregationTools.QueryDownsampling(
            MockServer.Object, ArchiveRtId,
            aggregations: [new() { Function = AggregationFunctionDto.integral, AttributePath = "ActivePower" }],
            from: WindowFrom, to: WindowTo, limit: 4);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("integral");
        _mockStreamRepo.Verify(r => r.ExecuteDownsamplingQueryAsync(
            It.IsAny<OctoObjectId>(), It.IsAny<StreamDataDownsamplingQueryOptions>()), Times.Never);
    }

    [Fact]
    public async Task Aggregation_LegacyEngineKey_IsStillRead()
    {
        _mockStreamRepo
            .Setup(r => r.ExecuteAggregationQueryAsync(It.IsAny<OctoObjectId>(),
                It.IsAny<StreamDataAggregationQueryOptions>()))
            .ReturnsAsync(Result(Row(values: [("Sum(Power)", 10.0)])));

        var result = await StreamDataAggregationTools.QueryAggregation(
            MockServer.Object, ArchiveRtId,
            aggregations: [new() { Function = AggregationFunctionDto.sum, AttributePath = "Power" }]);

        result.Rows.Single()["sum_Power"].Should().Be(10.0);
    }

    [Fact]
    public void ToolDescriptions_StateTheIntegralUnitAndTheRequiredWindow()
    {
        foreach (var name in new[] { nameof(StreamDataAggregationTools.QueryAggregation), nameof(StreamDataAggregationTools.QueryGrouping) })
        {
            var description = typeof(StreamDataAggregationTools).GetMethod(name)!
                .GetCustomAttributes(typeof(System.ComponentModel.DescriptionAttribute), false)
                .Cast<System.ComponentModel.DescriptionAttribute>().Single().Description;
            description.Should().Contain("integral").And.Contain("hours").And.Contain("REQUIRED");
        }
    }
}
