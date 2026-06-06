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

    public StreamDataMetadataToolsTests()
    {
        MockTenantResolution
            .Setup(t => t.GetTenantContextAsync(It.IsAny<string?>()))
            .ReturnsAsync(_tenantCtx.Object);
        _tenantCtx.Setup(c => c.TenantId).Returns("test-tenant");
        _tenantCtx.Setup(c => c.GetStreamDataRepository()).Returns(_streamRepo.Object);
        _tenantCtx.Setup(c => c.GetRollupArchiveRuntimeStore()).Returns(_rollupStore.Object);
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

        var result = await StreamDataMetadataTools.GetRollupQueryMetadata(
            MockServer.Object, RollupId);

        result.IsSuccess.Should().BeTrue();
        result.Resolved.Should().BeTrue();
        result.BucketSizeMs.Should().Be(3_600_000);
        result.LogicalSourcePaths.Should().BeEquivalentTo(["Power", "Temperature"]);
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
}
