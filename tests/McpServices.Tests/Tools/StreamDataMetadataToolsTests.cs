using FluentAssertions;
using Meshmakers.Octo.Backend.McpServices.Tools;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Moq;
using Xunit;

namespace McpServices.Tests.Tools;

public class StreamDataMetadataToolsTests : TestBase
{
    private const string Archive1 = "507f1f77bcf86cd799439011";
    private const string Archive2 = "507f1f77bcf86cd799439012";
    private const string RollupId = "507f1f77bcf86cd799439013";
    private const string SourceArchive = "507f1f77bcf86cd799439014";

    private static readonly RtCkId<CkTypeId> SensorCkType = new("EnergyCommunity-1/Sensor-1");

    private readonly Mock<ITenantContext> _tenantCtx = new();
    private readonly Mock<IStreamDataRepository> _streamRepo = new();
    private readonly Mock<IRollupArchiveRuntimeStore> _rollupStore = new();
    private readonly Mock<IArchiveRuntimeStore> _archiveStore = new();

    public StreamDataMetadataToolsTests()
    {
        MockTenantResolution
            .Setup(t => t.GetTenantContextAsync(It.IsAny<string?>()))
            .ReturnsAsync(_tenantCtx.Object);
        _tenantCtx.Setup(c => c.TenantId).Returns("test-tenant");
        _tenantCtx.Setup(c => c.GetStreamDataRepository()).Returns(_streamRepo.Object);
        _tenantCtx.Setup(c => c.GetRollupArchiveRuntimeStore()).Returns(_rollupStore.Object);
        _tenantCtx.Setup(c => c.GetArchiveRuntimeStore()).Returns(_archiveStore.Object);
    }

    // ── get_archive_storage_stats ───────────────────────────────────────────

    [Fact]
    public async Task GetStats_HappyPath_ProjectsEachInputId()
    {
        var stats = new Dictionary<OctoObjectId, ArchiveStorageStats>
        {
            [new OctoObjectId(Archive1)] = new(
                new OctoObjectId(Archive1), TableExists: true,
                RecordCount: 1000, SizeBytes: 50000, Health: ArchiveStorageHealth.Good),
            [new OctoObjectId(Archive2)] = new(
                new OctoObjectId(Archive2), TableExists: true,
                RecordCount: 2000, SizeBytes: 90000, Health: ArchiveStorageHealth.Warning)
        };
        _streamRepo
            .Setup(r => r.GetArchiveStatsAsync(It.IsAny<IReadOnlyList<OctoObjectId>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(stats);

        var result = await StreamDataMetadataTools.GetArchiveStorageStats(
            MockServer.Object, [Archive1, Archive2]);

        result.IsSuccess.Should().BeTrue();
        result.Stats.Should().HaveCount(2);
        result.Stats[0].ArchiveRtId.Should().Be(Archive1);
        result.Stats[0].RecordCount.Should().Be(1000);
        result.Stats[0].Health.Should().Be("Good");
        result.Stats[1].Health.Should().Be("Warning");
    }

    [Fact]
    public async Task GetStats_MissingArchive_ReturnsPlaceholderRow()
    {
        // Only archive1 in stats; archive2 missing should fall back to placeholder.
        var stats = new Dictionary<OctoObjectId, ArchiveStorageStats>
        {
            [new OctoObjectId(Archive1)] = new(
                new OctoObjectId(Archive1), true, 100, 200, ArchiveStorageHealth.Good)
        };
        _streamRepo
            .Setup(r => r.GetArchiveStatsAsync(It.IsAny<IReadOnlyList<OctoObjectId>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(stats);

        var result = await StreamDataMetadataTools.GetArchiveStorageStats(
            MockServer.Object, [Archive1, Archive2]);

        result.IsSuccess.Should().BeTrue();
        result.Stats.Should().HaveCount(2);
        result.Stats[1].TableExists.Should().BeFalse();
        result.Stats[1].Health.Should().Be("Unknown");
    }

    [Fact]
    public async Task GetStats_StreamDataNotEnabled_ReturnsPlaceholders()
    {
        _tenantCtx.Setup(c => c.GetStreamDataRepository()).Returns((IStreamDataRepository?)null);

        var result = await StreamDataMetadataTools.GetArchiveStorageStats(
            MockServer.Object, [Archive1, Archive2]);

        result.IsSuccess.Should().BeTrue();
        result.Stats.Should().AllSatisfy(s =>
        {
            s.TableExists.Should().BeFalse();
            s.Health.Should().Be("Unknown");
        });
        result.Message.Should().Contain("not enabled");
    }

    [Fact]
    public async Task GetStats_EmptyInputList_ReturnsEmpty()
    {
        var result = await StreamDataMetadataTools.GetArchiveStorageStats(
            MockServer.Object, []);

        result.IsSuccess.Should().BeTrue();
        result.Stats.Should().BeEmpty();
        _streamRepo.Verify(r => r.GetArchiveStatsAsync(
            It.IsAny<IReadOnlyList<OctoObjectId>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── get_rollup_query_metadata ───────────────────────────────────────────

    [Fact]
    public async Task GetRollupMetadata_HappyPath_ReturnsBucketAndPaths()
    {
        var snapshot = new RollupArchiveSnapshot(
            RtId: new OctoObjectId(RollupId),
            TargetCkTypeId: SensorCkType,
            Status: CkArchiveStatus.Activated,
            RtWellKnownName: "hourly",
            SourceArchiveRtId: new OctoObjectId(SourceArchive),
            BucketSize: TimeSpan.FromHours(1),
            WatermarkLag: TimeSpan.FromMinutes(1),
            LastAggregatedBucketEnd: null,
            Aggregations:
            [
                new CkRollupAggregationSpec(
                    SourcePath: "Power",
                    Function: CkRollupFunction.Avg,
                    TargetColumnName: null),
                new CkRollupAggregationSpec(
                    SourcePath: "Temperature",
                    Function: CkRollupFunction.Max,
                    TargetColumnName: null)
            ],
            FrozenUntil: null);

        _rollupStore.Setup(s => s.GetAsync(It.IsAny<OctoObjectId>())).ReturnsAsync(snapshot);

        // RollupLogicalPathResolver walks the chain via the archive store — for a single-step rollup
        // the parent is a raw archive (RollupAggregations==null), so the resolver returns SourcePath
        // as-is.
        var rawSource = new ArchiveSnapshot(
            new OctoObjectId(SourceArchive), SensorCkType, CkArchiveStatus.Activated,
            RtWellKnownName: null, Columns: []);
        _archiveStore.Setup(s => s.GetAsync(It.IsAny<OctoObjectId>())).ReturnsAsync(rawSource);

        var result = await StreamDataMetadataTools.GetRollupQueryMetadata(
            MockServer.Object, RollupId);

        result.IsSuccess.Should().BeTrue();
        result.Resolved.Should().BeTrue();
        result.BucketSizeMs.Should().Be(3_600_000);
        result.LogicalSourcePaths.Should().BeEquivalentTo(["Power", "Temperature"]);
    }

    [Fact]
    public async Task GetRollupMetadata_CascadeRollup_BackResolvesPhysicalColumnsToLogicalPaths()
    {
        // Monthly is a cascade rollup over Daily. Its specs reference Daily's physical storage columns
        // (amountValue_sum / amountValue_count) — the studio's column picker needs them collapsed back
        // to the original CK attribute path. This is the gap ROADMAP #3 closes.
        var monthlyRtId = new OctoObjectId("507f1f77bcf86cd799439020");
        var dailyRtId = new OctoObjectId("507f1f77bcf86cd799439021");
        var rawRtId = new OctoObjectId("507f1f77bcf86cd799439022");

        var monthly = new RollupArchiveSnapshot(
            RtId: monthlyRtId,
            TargetCkTypeId: SensorCkType,
            Status: CkArchiveStatus.Activated,
            RtWellKnownName: "monthly",
            SourceArchiveRtId: dailyRtId,
            BucketSize: TimeSpan.FromDays(30),
            WatermarkLag: TimeSpan.FromMinutes(15),
            LastAggregatedBucketEnd: null,
            Aggregations:
            [
                new CkRollupAggregationSpec("amountValue_sum", CkRollupFunction.Sum, null),
                new CkRollupAggregationSpec("amountValue_count", CkRollupFunction.Sum, null)
            ],
            FrozenUntil: null);

        var daily = new RollupArchiveSnapshot(
            RtId: dailyRtId,
            TargetCkTypeId: SensorCkType,
            Status: CkArchiveStatus.Activated,
            RtWellKnownName: "daily",
            SourceArchiveRtId: rawRtId,
            BucketSize: TimeSpan.FromDays(1),
            WatermarkLag: TimeSpan.FromMinutes(5),
            LastAggregatedBucketEnd: null,
            Aggregations:
            [
                new CkRollupAggregationSpec("amountValue", CkRollupFunction.Sum, null),
                new CkRollupAggregationSpec("amountValue", CkRollupFunction.Count, null)
            ],
            FrozenUntil: null);

        // Archive-store view: daily appears as an ArchiveSnapshot with RollupAggregations set (cascade
        // signal); raw appears with RollupAggregations==null (chain termination).
        var dailyAsArchive = new ArchiveSnapshot(
            dailyRtId, SensorCkType, CkArchiveStatus.Activated, "daily", Columns: [])
        {
            RollupAggregations = daily.Aggregations
        };
        var rawAsArchive = new ArchiveSnapshot(
            rawRtId, SensorCkType, CkArchiveStatus.Activated, "raw", Columns: []);

        _rollupStore.Setup(s => s.GetAsync(monthlyRtId)).ReturnsAsync(monthly);
        _rollupStore.Setup(s => s.GetAsync(dailyRtId)).ReturnsAsync(daily);
        _archiveStore.Setup(s => s.GetAsync(dailyRtId)).ReturnsAsync(dailyAsArchive);
        _archiveStore.Setup(s => s.GetAsync(rawRtId)).ReturnsAsync(rawAsArchive);

        var result = await StreamDataMetadataTools.GetRollupQueryMetadata(
            MockServer.Object, monthlyRtId.ToString());

        result.IsSuccess.Should().BeTrue();
        result.Resolved.Should().BeTrue();
        // Two physical columns (_sum + _count) collapse to one logical CK attribute path.
        result.LogicalSourcePaths.Should().BeEquivalentTo(["amountValue"]);
    }

    [Fact]
    public async Task GetRollupMetadata_NotFound_ReturnsResolvedFalse()
    {
        _rollupStore.Setup(s => s.GetAsync(It.IsAny<OctoObjectId>()))
            .ReturnsAsync((RollupArchiveSnapshot?)null);

        var result = await StreamDataMetadataTools.GetRollupQueryMetadata(
            MockServer.Object, RollupId);

        result.IsSuccess.Should().BeTrue();
        result.Resolved.Should().BeFalse();
        result.Message.Should().Contain("No rollup");
    }

    [Fact]
    public async Task GetRollupMetadata_StreamDataNotEnabled_ReturnsResolvedFalse()
    {
        _tenantCtx.Setup(c => c.GetRollupArchiveRuntimeStore()).Returns((IRollupArchiveRuntimeStore?)null);

        var result = await StreamDataMetadataTools.GetRollupQueryMetadata(
            MockServer.Object, RollupId);

        result.IsSuccess.Should().BeTrue();
        result.Resolved.Should().BeFalse();
        result.Message.Should().Contain("not enabled");
    }

    [Fact]
    public async Task GetRollupMetadata_MissingRtId_ReturnsValidationError()
    {
        var result = await StreamDataMetadataTools.GetRollupQueryMetadata(
            MockServer.Object, rollupRtId: "");

        result.IsSuccess.Should().BeFalse();
        _rollupStore.Verify(s => s.GetAsync(It.IsAny<OctoObjectId>()), Times.Never);
    }

    // ── resolve_series_query (AB#4290) ──────────────────────────────────────

    private static readonly DateTime YearFrom = new(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime YearTo = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static async IAsyncEnumerable<RollupArchiveSnapshot> ToAsyncEnumerable(
        IEnumerable<RollupArchiveSnapshot> items)
    {
        foreach (var i in items)
        {
            yield return i;
            await Task.CompletedTask;
        }
    }

    private ArchiveSnapshot GivenBaseArchive(TimeSpan? period)
    {
        var snapshot = new ArchiveSnapshot(
            new OctoObjectId(SourceArchive), SensorCkType, CkArchiveStatus.Activated, "raw", Columns: [])
        {
            IsTimeRange = period is not null,
            Period = period,
        };
        _archiveStore.Setup(s => s.GetAsync(new OctoObjectId(SourceArchive))).ReturnsAsync(snapshot);
        return snapshot;
    }

    private void GivenRollups(params RollupArchiveSnapshot[] rollups) =>
        _rollupStore.Setup(s => s.EnumerateAsync()).Returns(() => ToAsyncEnumerable(rollups));

    private static RollupArchiveSnapshot SumRollup(string rtId, TimeSpan bucket, string path = "Amount.Value") =>
        new(new OctoObjectId(rtId), SensorCkType, CkArchiveStatus.Activated, "rollup",
            new OctoObjectId(SourceArchive), bucket, TimeSpan.FromMinutes(5), null,
            [new CkRollupAggregationSpec(path, CkRollupFunction.Sum, null)], null);

    [Fact]
    public async Task ResolveSeries_HappyPath_PicksSumRollup()
    {
        GivenBaseArchive(TimeSpan.FromMinutes(15));
        var hourly = SumRollup(RollupId, TimeSpan.FromHours(1));
        GivenRollups(hourly);

        var result = await StreamDataMetadataTools.ResolveSeriesQuery(
            MockServer.Object, SourceArchive, YearFrom, YearTo, "Amount.Value", "sum", 600);

        result.IsSuccess.Should().BeTrue();
        result.Resolved.Should().BeTrue();
        result.Signal.Should().Be("Ok");
        result.ArchiveRtId.Should().Be(RollupId);
        result.Points.Should().Be(600);
        result.ReducingFunction.Should().Be("Sum");
    }

    [Fact]
    public async Task ResolveSeries_NoCompatibleRollup_RefusesWithSignal()
    {
        GivenBaseArchive(TimeSpan.FromMinutes(15));
        GivenRollups(); // none

        var result = await StreamDataMetadataTools.ResolveSeriesQuery(
            MockServer.Object, SourceArchive, YearFrom, YearTo, "Amount.Value", "sum", 600);

        result.IsSuccess.Should().BeTrue();
        result.Resolved.Should().BeTrue();
        result.Signal.Should().Be("NoSuitableRollup");
        result.ArchiveRtId.Should().Be(SourceArchive);
    }

    [Fact]
    public async Task ResolveSeries_StreamDataNotEnabled_ReturnsResolvedFalse()
    {
        _tenantCtx.Setup(c => c.GetRollupArchiveRuntimeStore()).Returns((IRollupArchiveRuntimeStore?)null);

        var result = await StreamDataMetadataTools.ResolveSeriesQuery(
            MockServer.Object, SourceArchive, YearFrom, YearTo, "Amount.Value", "sum", 600);

        result.IsSuccess.Should().BeTrue();
        result.Resolved.Should().BeFalse();
        result.Message.Should().Contain("not enabled");
    }

    [Fact]
    public async Task ResolveSeries_MissingBaseArchiveRtId_ReturnsValidationError()
    {
        var result = await StreamDataMetadataTools.ResolveSeriesQuery(
            MockServer.Object, "", YearFrom, YearTo, "Amount.Value", "sum", 600);

        result.IsSuccess.Should().BeFalse();
        _archiveStore.Verify(s => s.GetAsync(It.IsAny<OctoObjectId>()), Times.Never);
    }

    [Fact]
    public async Task ResolveSeries_InvalidFunction_ReturnsValidationError()
    {
        var result = await StreamDataMetadataTools.ResolveSeriesQuery(
            MockServer.Object, SourceArchive, YearFrom, YearTo, "Amount.Value", "bogus", 600);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("requiredAggregation");
    }

    [Fact]
    public async Task ResolveSeries_InvalidTimeWindow_ReturnsValidationError()
    {
        var result = await StreamDataMetadataTools.ResolveSeriesQuery(
            MockServer.Object, SourceArchive, YearTo, YearFrom, "Amount.Value", "sum", 600);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("from must be earlier");
    }
}
