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

    // -- AB#5346: the engine's real key format ---------------------------------
    // CrateDbStreamDataRepository keys group columns by storage name (concatenated, lower-cased path) and
    // aggregations by "{column}_{function}". The archive below has the mixed-case dotted attributes of the
    // h24 / voestalpine repro, so the archive's real field resolver is used, not the fallback.

    private void GivenMeterArchive()
    {
        var snapshot = new ArchiveSnapshot(
            RtId: new OctoObjectId(ArchiveRtId),
            TargetCkTypeId: SensorCkType,
            Status: CkArchiveStatus.Activated,
            RtWellKnownName: "MeterArchive",
            Columns:
            [
                new CkArchiveColumnSpec("ObisCode", Indexed: true, Required: false),
                new CkArchiveColumnSpec("Amount.Value", Indexed: false, Required: false),
                new CkArchiveColumnSpec("Amount.Unit", Indexed: false, Required: false)
            ]);
        _mockArchiveStore
            .Setup(s => s.GetAsync(It.IsAny<OctoObjectId>()))
            .ReturnsAsync(snapshot);
    }

    [Fact]
    public async Task Grouping_DottedMixedCasePaths_ReadsStorageGroupKeysAndCountKey()
    {
        GivenMeterArchive();
        _mockStreamRepo
            .Setup(r => r.ExecuteGroupedAggregationQueryAsync(It.IsAny<OctoObjectId>(),
                It.IsAny<StreamDataGroupedAggregationQueryOptions>()))
            .ReturnsAsync(Result(
                Row(values:
                [
                    ("obiscode", "1-1:2.9.0 G.01T"),
                    ("amountunit", "KWh"),
                    ("amountvalue_sum", 3341.56),
                    ("amountvalue_count", 2976L)
                ]),
                Row(values:
                [
                    ("obiscode", "1-1:1.9.0 G.01T"),
                    ("amountunit", "KWh"),
                    ("amountvalue_sum", 12.5),
                    ("amountvalue_count", 96L)
                ])));

        var result = await StreamDataAggregationTools.QueryGrouping(
            MockServer.Object, ArchiveRtId,
            groupByAttributePaths: ["ObisCode", "Amount.Unit"],
            aggregations:
            [
                new() { Function = AggregationFunctionDto.sum, AttributePath = "Amount.Value", Alias = "s" },
                new() { Function = AggregationFunctionDto.count, AttributePath = "Amount.Value" }
            ]);

        result.IsSuccess.Should().BeTrue(result.ErrorMessage ?? "");
        result.Rows.Should().HaveCount(2);
        result.Rows[0]["ObisCode"].Should().Be("1-1:2.9.0 G.01T");
        result.Rows[0]["Amount.Unit"].Should().Be("KWh");
        result.Rows[0]["s"].Should().Be(3341.56);
        result.Rows[0]["count_Amount_Value"].Should().Be(2976L);
        result.Rows[1]["ObisCode"].Should().Be("1-1:1.9.0 G.01T");
        result.Rows[1]["count_Amount_Value"].Should().Be(96L);
        result.Rows.SelectMany(r => r.Values).Should().NotContainNulls();
    }

    [Fact]
    public async Task Aggregation_CountOnDottedMixedCasePath_ReadsEngineKey()
    {
        GivenMeterArchive();
        _mockStreamRepo
            .Setup(r => r.ExecuteAggregationQueryAsync(It.IsAny<OctoObjectId>(),
                It.IsAny<StreamDataAggregationQueryOptions>()))
            .ReturnsAsync(Result(Row(values: [("amountvalue_count", 2976L), ("amountvalue_max", 4.2)])));

        var result = await StreamDataAggregationTools.QueryAggregation(
            MockServer.Object, ArchiveRtId,
            aggregations:
            [
                new() { Function = AggregationFunctionDto.count, AttributePath = "Amount.Value" },
                new() { Function = AggregationFunctionDto.max, AttributePath = "Amount.Value", Alias = "peak" }
            ]);

        result.IsSuccess.Should().BeTrue(result.ErrorMessage ?? "");
        result.Rows.Single()["count_Amount_Value"].Should().Be(2976L);
        result.Rows.Single()["peak"].Should().Be(4.2);
    }

    [Fact]
    public async Task Downsampling_CountOnDottedMixedCasePath_ReadsEngineKeyPerBucket()
    {
        GivenMeterArchive();
        var b1 = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
        _mockStreamRepo
            .Setup(r => r.ExecuteDownsamplingQueryAsync(It.IsAny<OctoObjectId>(),
                It.IsAny<StreamDataDownsamplingQueryOptions>()))
            .ReturnsAsync(Result(
                Row(b1, ("amountvalue_count", 96L), ("amountvalue_sum", 10.0)),
                Row(b1.AddDays(1), ("amountvalue_count", 0L), ("amountvalue_sum", null))));

        var result = await StreamDataAggregationTools.QueryDownsampling(
            MockServer.Object, ArchiveRtId,
            aggregations:
            [
                new() { Function = AggregationFunctionDto.count, AttributePath = "Amount.Value", Alias = "n" },
                new() { Function = AggregationFunctionDto.sum, AttributePath = "Amount.Value", Alias = "s" }
            ],
            from: b1, to: b1.AddDays(2), limit: 2);

        result.IsSuccess.Should().BeTrue(result.ErrorMessage ?? "");
        result.Rows[0]["n"].Should().Be(96L);
        result.Rows[0]["s"].Should().Be(10.0);
        result.Rows[1]["n"].Should().Be(0L);
        result.Rows[1]["s"].Should().BeNull();
    }

    [Fact]
    public async Task Aggregation_LegacyFunctionKey_IsNotTheContract()
    {
        // The engine never writes "{Function}({path})"; a row carrying only that key has no value for the column.
        _mockStreamRepo
            .Setup(r => r.ExecuteAggregationQueryAsync(It.IsAny<OctoObjectId>(),
                It.IsAny<StreamDataAggregationQueryOptions>()))
            .ReturnsAsync(Result(Row(values: [("Sum(Power)", 10.0)])));

        var result = await StreamDataAggregationTools.QueryAggregation(
            MockServer.Object, ArchiveRtId,
            aggregations: [new() { Function = AggregationFunctionDto.sum, AttributePath = "Power" }]);

        result.Rows.Single()["sum_Power"].Should().BeNull();
    }

    [Fact]
    public async Task Aggregation_CountWithoutPath_IsRefusedWithoutAnEngineCall()
    {
        var result = await StreamDataAggregationTools.QueryAggregation(
            MockServer.Object, ArchiveRtId,
            aggregations: [new() { Function = AggregationFunctionDto.count }]);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("attributePath is required").And.Contain("rtId");
        _mockStreamRepo.Verify(r => r.ExecuteAggregationQueryAsync(
            It.IsAny<OctoObjectId>(), It.IsAny<StreamDataAggregationQueryOptions>()), Times.Never);
    }

    [Fact]
    public async Task Grouping_CountWithoutPath_IsRefusedWithoutAnEngineCall()
    {
        var result = await StreamDataAggregationTools.QueryGrouping(
            MockServer.Object, ArchiveRtId,
            groupByAttributePaths: ["FacilityId"],
            aggregations: [new() { Function = AggregationFunctionDto.count }]);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("attributePath is required");
        _mockStreamRepo.Verify(r => r.ExecuteGroupedAggregationQueryAsync(
            It.IsAny<OctoObjectId>(), It.IsAny<StreamDataGroupedAggregationQueryOptions>()), Times.Never);
    }

    [Fact]
    public async Task Downsampling_CountWithoutPath_IsRefusedWithoutAnEngineCall()
    {
        var from = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
        var result = await StreamDataAggregationTools.QueryDownsampling(
            MockServer.Object, ArchiveRtId,
            aggregations: [new() { Function = AggregationFunctionDto.count }],
            from: from, to: from.AddDays(1), limit: 24);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("attributePath is required");
        _mockStreamRepo.Verify(r => r.ExecuteDownsamplingQueryAsync(
            It.IsAny<OctoObjectId>(), It.IsAny<StreamDataDownsamplingQueryOptions>()), Times.Never);
    }

    // -- AB#7362: local-day bins ---------------------------------------------

    private StreamDataDownsamplingQueryOptions? _capturedOptions;

    private void CaptureDownsampling(params StreamDataRow[] rows) =>
        _mockStreamRepo
            .Setup(r => r.ExecuteDownsamplingQueryAsync(It.IsAny<OctoObjectId>(),
                It.IsAny<StreamDataDownsamplingQueryOptions>()))
            .Callback<OctoObjectId, StreamDataDownsamplingQueryOptions>((_, o) => _capturedOptions = o)
            .ReturnsAsync(Result(rows));

    [Fact]
    public async Task Downsampling_GasDayBins_PassesZoneIntervalOriginAndReturnsTheLocalDays()
    {
        // Gas days 2026-03-27/28/29 06:00 Vienna: 06:00 CET = 05:00Z, the 03-29 day starts after the DST change
        // (06:00 CEST = 04:00Z). The 03-28 gas day spans the change and has 23 hours.
        GivenMeterArchive();
        var d1 = new DateTime(2026, 3, 27, 5, 0, 0, DateTimeKind.Utc);
        var d2 = new DateTime(2026, 3, 28, 5, 0, 0, DateTimeKind.Utc);
        var d3 = new DateTime(2026, 3, 29, 4, 0, 0, DateTimeKind.Utc);
        CaptureDownsampling(
            Row(d1, ("amountvalue_count", 24L)),
            Row(d2, ("amountvalue_count", 23L)),
            Row(d3, ("amountvalue_count", 24L)));

        var result = await StreamDataAggregationTools.QueryDownsampling(
            MockServer.Object, ArchiveRtId,
            aggregations: [new() { Function = AggregationFunctionDto.count, AttributePath = "Amount.Value", Alias = "n" }],
            from: d1, to: new DateTime(2026, 3, 30, 4, 0, 0, DateTimeKind.Utc),
            timeZone: "Europe/Vienna", binIntervalDays: 1, binOrigin: "06:00");

        result.IsSuccess.Should().BeTrue(result.ErrorMessage ?? "");
        result.Rows.Select(r => r["bucketStart"]).Should().Equal(d1, d2, d3);
        result.Rows.Select(r => r["n"]).Should().Equal(24L, 23L, 24L);

        var options = _capturedOptions;
        options.Should().NotBeNull();
        options!.HasLocalDayBins.Should().BeTrue();
        options.TimeZone.Should().Be("Europe/Vienna");
        options.BinInterval.Should().Be(TimeSpan.FromDays(1));
        options.BinOrigin.Should().Be(TimeSpan.FromHours(6));
        options.Limit.Should().BeNull("the engine sizes local-day bins from the range");
    }

    [Fact]
    public async Task Downsampling_WithoutLocalDayParameters_KeepsTheFixedWidthOptions()
    {
        CaptureDownsampling(Row(new DateTime(2026, 6, 6, 0, 0, 0, DateTimeKind.Utc), ("power_avg", 1.0)));
        var from = new DateTime(2026, 6, 6, 0, 0, 0, DateTimeKind.Utc);

        var result = await StreamDataAggregationTools.QueryDownsampling(
            MockServer.Object, ArchiveRtId,
            aggregations: [new() { Function = AggregationFunctionDto.avg, AttributePath = "Power" }],
            from: from, to: from.AddDays(1), limit: 24);

        result.IsSuccess.Should().BeTrue(result.ErrorMessage ?? "");
        _capturedOptions.Should().NotBeNull();
        var options = _capturedOptions!;
        options.HasLocalDayBins.Should().BeFalse();
        options.TimeZone.Should().BeNull();
        options.BinInterval.Should().BeNull();
        options.BinOrigin.Should().BeNull();
        options.Limit.Should().Be(24);
    }

    [Fact]
    public async Task Downsampling_OnlyBinOrigin_CutsUtcDays()
    {
        CaptureDownsampling();
        var from = new DateTime(2026, 6, 1, 6, 0, 0, DateTimeKind.Utc);

        var result = await StreamDataAggregationTools.QueryDownsampling(
            MockServer.Object, ArchiveRtId,
            aggregations: [new() { Function = AggregationFunctionDto.avg, AttributePath = "Power" }],
            from: from, to: from.AddDays(2), binOrigin: "6:00");

        result.IsSuccess.Should().BeTrue(result.ErrorMessage ?? "");
        _capturedOptions.Should().NotBeNull();
        var options = _capturedOptions!;
        options.HasLocalDayBins.Should().BeTrue();
        options.TimeZone.Should().BeNull();
        options.BinOrigin.Should().Be(TimeSpan.FromHours(6));
    }

    [Theory]
    [InlineData(null, null, "24:00", "binOrigin '24:00' is invalid")]
    [InlineData(null, null, "06:00:30", "binOrigin '06:00:30' is invalid")]
    [InlineData(null, null, "6", "binOrigin '6' is invalid")]
    [InlineData(null, 0, null, "binIntervalDays 0 is invalid")]
    [InlineData(null, 10_001, null, "binIntervalDays 10001 is invalid")]
    [InlineData("Mars/Olympus_Mons", null, null, "Mars/Olympus_Mons")]
    public async Task Downsampling_InvalidLocalDayArguments_AreRefusedWithoutAnEngineCall(
        string? timeZone, int? binIntervalDays, string? binOrigin, string expected)
    {
        var from = new DateTime(2026, 3, 27, 5, 0, 0, DateTimeKind.Utc);

        var result = await StreamDataAggregationTools.QueryDownsampling(
            MockServer.Object, ArchiveRtId,
            aggregations: [new() { Function = AggregationFunctionDto.avg, AttributePath = "Power" }],
            from: from, to: from.AddDays(3),
            timeZone: timeZone, binIntervalDays: binIntervalDays, binOrigin: binOrigin);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain(expected);
        _mockStreamRepo.Verify(r => r.ExecuteDownsamplingQueryAsync(
            It.IsAny<OctoObjectId>(), It.IsAny<StreamDataDownsamplingQueryOptions>()), Times.Never);
    }

    [Fact]
    public async Task Downsampling_LocalDayBins_IgnoreLimit()
    {
        // CodeRabbit on #45: limit is ignored with local-day bins, so limit 0 must not be refused.
        CaptureDownsampling();
        var from = new DateTime(2026, 3, 27, 5, 0, 0, DateTimeKind.Utc);

        var result = await StreamDataAggregationTools.QueryDownsampling(
            MockServer.Object, ArchiveRtId,
            aggregations: [new() { Function = AggregationFunctionDto.avg, AttributePath = "Power" }],
            from: from, to: from.AddDays(3), limit: 0, timeZone: "Europe/Vienna");

        result.IsSuccess.Should().BeTrue(result.ErrorMessage ?? "");
        _capturedOptions.Should().NotBeNull();
        _capturedOptions!.Limit.Should().BeNull();
    }

    [Fact]
    public async Task Downsampling_EngineRefusesTheBins_ReturnsTheEngineMessage()
    {
        _mockStreamRepo
            .Setup(r => r.ExecuteDownsamplingQueryAsync(It.IsAny<OctoObjectId>(),
                It.IsAny<StreamDataDownsamplingQueryOptions>()))
            .ThrowsAsync(new DownsamplingBinOptionsInvalidException(
                "the time range needs more than 10000 bins. Ask for a shorter range or a longer BinInterval."));
        var from = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        var result = await StreamDataAggregationTools.QueryDownsampling(
            MockServer.Object, ArchiveRtId,
            aggregations: [new() { Function = AggregationFunctionDto.avg, AttributePath = "Power" }],
            from: from, to: from.AddYears(40), timeZone: "Europe/Vienna");

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("more than 10000 bins");
    }

    [Fact]
    public void ToolDescription_Downsampling_NamesTheGasDayExample()
    {
        var description = typeof(StreamDataAggregationTools).GetMethod(nameof(StreamDataAggregationTools.QueryDownsampling))!
            .GetCustomAttributes(typeof(System.ComponentModel.DescriptionAttribute), false)
            .Cast<System.ComponentModel.DescriptionAttribute>().Single().Description;
        description.Should().Contain("Europe/Vienna").And.Contain("06:00").And.Contain("binIntervalDays");
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
