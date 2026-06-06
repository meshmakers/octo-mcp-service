using FluentAssertions;
using Meshmakers.Octo.Backend.McpServices.Tools;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v2;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Runtime.Engine.Repositories.Query;
using Moq;
using Xunit;

namespace McpServices.Tests.Tools;

/// <summary>
///     Tests for the two persisted-query MCP tools — <c>execute_runtime_query</c> and
///     <c>execute_stream_data_query</c>. Covers the polymorphic dispatch path for each CK subtype plus the
///     validation and not-found responses.
/// </summary>
public class PersistedQueryToolsTests : TestBase
{
    private const string QueryRtId = "507f1f77bcf86cd799439011";
    private const string ArchiveRtId = "507f1f77bcf86cd799439012";

    // CkTypeId format is Name-VersionUint, not SemVer.
    private static readonly RtCkId<CkTypeId> SensorCkType = new("EnergyCommunity-1/Sensor-1");

    // ── execute_runtime_query ─────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteRuntimeQuery_MissingRtId_ReturnsValidationError()
    {
        var result = await RuntimeAggregationTools.ExecuteRuntimeQuery(
            MockServer.Object, queryRtId: "");

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("required");
        MockTenantRepository.Verify(r => r.GetRtEntityByRtIdAsync<RtPersistentQuery>(
            It.IsAny<IOctoSession>(), It.IsAny<OctoObjectId>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteRuntimeQuery_NotFound_ReturnsNotFoundError()
    {
        MockTenantRepository
            .Setup(r => r.GetRtEntityByRtIdAsync<RtPersistentQuery>(
                It.IsAny<IOctoSession>(), It.IsAny<OctoObjectId>()))
            .ReturnsAsync((RtPersistentQuery?)null);

        var result = await RuntimeAggregationTools.ExecuteRuntimeQuery(
            MockServer.Object, QueryRtId);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("not found");
        result.QueryRtId.Should().Be(QueryRtId);
    }

    [Fact]
    public async Task ExecuteRuntimeQuery_SimpleSubtype_ReturnsEntitiesProjectedToColumns()
    {
        var simple = new RtSimpleRtQuery { QueryCkTypeId = SensorCkType.ToString() };
        simple.Columns.Add("Power");
        simple.Columns.Add("Temperature");

        MockTenantRepository
            .Setup(r => r.GetRtEntityByRtIdAsync<RtPersistentQuery>(
                It.IsAny<IOctoSession>(), It.IsAny<OctoObjectId>()))
            .ReturnsAsync(simple);

        // Repo returns 0 entities — we only verify the dispatch + response envelope.
        MockTenantRepository
            .Setup(r => r.GetRtEntitiesByTypeAsync(
                It.IsAny<IOctoSession>(),
                It.IsAny<RtCkId<CkTypeId>>(),
                It.IsAny<RtEntityQueryOptions>(),
                It.IsAny<int?>(),
                It.IsAny<int?>()))
            .ReturnsAsync(new ResultSet<RtEntity>([], 0, null, null));

        var result = await RuntimeAggregationTools.ExecuteRuntimeQuery(
            MockServer.Object, QueryRtId);

        result.IsSuccess.Should().BeTrue(result.ErrorMessage ?? "");
        result.QuerySubtype.Should().Be(nameof(RtSimpleRtQuery));
        result.CkTypeId.Should().Be(SensorCkType.ToString());
        result.Entities.Should().NotBeNull();
        result.Entities!.Count.Should().Be(0);
        result.RowCount.Should().Be(0);
    }

    [Fact]
    public async Task ExecuteRuntimeQuery_AggregationSubtype_ReturnsScalarRow()
    {
        var aggregation = new RtAggregationRtQuery { QueryCkTypeId = SensorCkType.ToString() };
        aggregation.Columns.Add(new RtAggregationQueryColumnRecord
        {
            AttributePath = "Power",
            AggregationType = RtAggregationTypesEnum.Sum
        });

        MockTenantRepository
            .Setup(r => r.GetRtEntityByRtIdAsync<RtPersistentQuery>(
                It.IsAny<IOctoSession>(), It.IsAny<OctoObjectId>()))
            .ReturnsAsync(aggregation);

        var scalar = new AggregationResult(
            count: 0,
            countStatistics: [],
            minStatistics: [],
            maxStatistics: [],
            avgStatistics: [],
            sumStatistics: [new StatisticsResult { AttributePath = "Power", Value = 250.0 }]);

        MockTenantRepository
            .Setup(r => r.GetRtEntitiesByTypeAsync(
                It.IsAny<IOctoSession>(),
                It.IsAny<RtCkId<CkTypeId>>(),
                It.IsAny<RtEntityQueryOptions>(),
                It.IsAny<int?>(),
                It.IsAny<int?>()))
            .ReturnsAsync(new ResultSet<RtEntity>([], 0, scalar, null));

        var result = await RuntimeAggregationTools.ExecuteRuntimeQuery(
            MockServer.Object, QueryRtId);

        result.IsSuccess.Should().BeTrue(result.ErrorMessage ?? "");
        result.QuerySubtype.Should().Be(nameof(RtAggregationRtQuery));
        result.Rows.Should().NotBeNull().And.HaveCount(1);
        result.Rows![0]["sum_Power"].Should().Be(250.0);
    }

    [Fact]
    public async Task ExecuteRuntimeQuery_GroupingSubtype_RejectsEmptyGroupingColumns()
    {
        var grouping = new RtGroupingAggregationRtQuery { QueryCkTypeId = SensorCkType.ToString() };
        // No GroupingColumns added — should be rejected before the engine call.
        grouping.Columns.Add(new RtAggregationQueryColumnRecord
        {
            AttributePath = "Power",
            AggregationType = RtAggregationTypesEnum.Sum
        });

        MockTenantRepository
            .Setup(r => r.GetRtEntityByRtIdAsync<RtPersistentQuery>(
                It.IsAny<IOctoSession>(), It.IsAny<OctoObjectId>()))
            .ReturnsAsync(grouping);

        var result = await RuntimeAggregationTools.ExecuteRuntimeQuery(
            MockServer.Object, QueryRtId);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("GroupingColumns");
        result.QuerySubtype.Should().Be(nameof(RtGroupingAggregationRtQuery));
        MockTenantRepository.Verify(r => r.GetRtEntitiesByTypeAsync(
            It.IsAny<IOctoSession>(), It.IsAny<RtCkId<CkTypeId>>(),
            It.IsAny<RtEntityQueryOptions>(), It.IsAny<int?>(), It.IsAny<int?>()),
            Times.Never);
    }

    // ── execute_stream_data_query ─────────────────────────────────────────────

    private readonly Mock<ITenantContext> _tenantCtx = new();
    private readonly Mock<IStreamDataRepository> _streamRepo = new();

    private void WireStreamDataMocks()
    {
        MockTenantResolution
            .Setup(t => t.GetTenantContextAsync(It.IsAny<string?>()))
            .ReturnsAsync(_tenantCtx.Object);
        _tenantCtx.Setup(c => c.TenantId).Returns("test-tenant");
        _tenantCtx.Setup(c => c.GetStreamDataRepository()).Returns(_streamRepo.Object);
    }

    [Fact]
    public async Task ExecuteStreamDataQuery_MissingRtId_ReturnsValidationError()
    {
        var result = await StreamDataAggregationTools.ExecuteStreamDataQuery(
            MockServer.Object, queryRtId: "");

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("required");
    }

    [Fact]
    public async Task ExecuteStreamDataQuery_StreamDataNotEnabled_ReturnsError()
    {
        WireStreamDataMocks();
        _tenantCtx.Setup(c => c.GetStreamDataRepository()).Returns((IStreamDataRepository?)null);

        var result = await StreamDataAggregationTools.ExecuteStreamDataQuery(
            MockServer.Object, QueryRtId);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("not enabled");
    }

    [Fact]
    public async Task ExecuteStreamDataQuery_NotFound_ReturnsNotFoundError()
    {
        WireStreamDataMocks();
        MockTenantRepository
            .Setup(r => r.GetRtEntityByRtIdAsync<RtStreamDataQuery>(
                It.IsAny<IOctoSession>(), It.IsAny<OctoObjectId>()))
            .ReturnsAsync((RtStreamDataQuery?)null);

        var result = await StreamDataAggregationTools.ExecuteStreamDataQuery(
            MockServer.Object, QueryRtId);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("not found");
    }

    [Fact]
    public async Task ExecuteStreamDataQuery_MissingArchiveRtId_ReturnsValidationError()
    {
        WireStreamDataMocks();
        var simple = new RtSimpleSdQuery { QueryCkTypeId = SensorCkType.ToString() };
        // No ArchiveRtId set.
        MockTenantRepository
            .Setup(r => r.GetRtEntityByRtIdAsync<RtStreamDataQuery>(
                It.IsAny<IOctoSession>(), It.IsAny<OctoObjectId>()))
            .ReturnsAsync(simple);

        var result = await StreamDataAggregationTools.ExecuteStreamDataQuery(
            MockServer.Object, QueryRtId);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("ArchiveRtId");
        result.QuerySubtype.Should().Be(nameof(RtSimpleSdQuery));
    }

    [Fact]
    public async Task ExecuteStreamDataQuery_SimpleSubtype_ReturnsRows()
    {
        WireStreamDataMocks();
        var simple = new RtSimpleSdQuery
        {
            QueryCkTypeId = SensorCkType.ToString(),
            ArchiveRtId = ArchiveRtId
        };
        simple.Columns.Add("Power");

        MockTenantRepository
            .Setup(r => r.GetRtEntityByRtIdAsync<RtStreamDataQuery>(
                It.IsAny<IOctoSession>(), It.IsAny<OctoObjectId>()))
            .ReturnsAsync(simple);

        var queryResult = new StreamDataQueryResult
        {
            Rows =
            [
                new StreamDataRow
                {
                    Timestamp = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                    Values = new Dictionary<string, object?> { ["Power"] = 100.0 }
                }
            ],
            TotalCount = 1
        };

        _streamRepo
            .Setup(r => r.ExecuteQueryAsync(It.IsAny<OctoObjectId>(),
                It.IsAny<StreamDataQueryOptions>()))
            .ReturnsAsync(queryResult);

        var result = await StreamDataAggregationTools.ExecuteStreamDataQuery(
            MockServer.Object, QueryRtId);

        result.IsSuccess.Should().BeTrue(result.ErrorMessage ?? "");
        result.QuerySubtype.Should().Be(nameof(RtSimpleSdQuery));
        result.ArchiveRtId.Should().Be(ArchiveRtId);
        result.StreamRows.Should().NotBeNull().And.HaveCount(1);
        result.StreamRows![0].Values["Power"].Should().Be(100.0);
        result.TotalCount.Should().Be(1);
    }

    [Fact]
    public async Task ExecuteStreamDataQuery_AggregationSubtype_ReturnsScalarRow()
    {
        WireStreamDataMocks();
        var aggregation = new RtAggregationSdQuery
        {
            QueryCkTypeId = SensorCkType.ToString(),
            ArchiveRtId = ArchiveRtId
        };
        aggregation.Columns.Add(new RtAggregationQueryColumnRecord
        {
            AttributePath = "Power",
            AggregationType = RtAggregationTypesEnum.Sum
        });

        MockTenantRepository
            .Setup(r => r.GetRtEntityByRtIdAsync<RtStreamDataQuery>(
                It.IsAny<IOctoSession>(), It.IsAny<OctoObjectId>()))
            .ReturnsAsync(aggregation);

        var queryResult = new StreamDataQueryResult
        {
            Rows =
            [
                new StreamDataRow
                {
                    Values = new Dictionary<string, object?> { ["Sum(Power)"] = 500.0 }
                }
            ],
            TotalCount = 1
        };

        _streamRepo
            .Setup(r => r.ExecuteAggregationQueryAsync(It.IsAny<OctoObjectId>(),
                It.IsAny<StreamDataAggregationQueryOptions>()))
            .ReturnsAsync(queryResult);

        var result = await StreamDataAggregationTools.ExecuteStreamDataQuery(
            MockServer.Object, QueryRtId);

        result.IsSuccess.Should().BeTrue(result.ErrorMessage ?? "");
        result.QuerySubtype.Should().Be(nameof(RtAggregationSdQuery));
        result.Rows.Should().NotBeNull().And.HaveCount(1);
        result.Rows![0]["sum_Power"].Should().Be(500.0);
    }

    [Fact]
    public async Task ExecuteStreamDataQuery_GroupingSubtype_RejectsEmptyGroupingColumns()
    {
        WireStreamDataMocks();
        var grouping = new RtGroupingAggregationSdQuery
        {
            QueryCkTypeId = SensorCkType.ToString(),
            ArchiveRtId = ArchiveRtId
        };
        // No grouping columns set.
        grouping.Columns.Add(new RtAggregationQueryColumnRecord
        {
            AttributePath = "Power",
            AggregationType = RtAggregationTypesEnum.Sum
        });

        MockTenantRepository
            .Setup(r => r.GetRtEntityByRtIdAsync<RtStreamDataQuery>(
                It.IsAny<IOctoSession>(), It.IsAny<OctoObjectId>()))
            .ReturnsAsync(grouping);

        var result = await StreamDataAggregationTools.ExecuteStreamDataQuery(
            MockServer.Object, QueryRtId);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("GroupingColumns");
        result.QuerySubtype.Should().Be(nameof(RtGroupingAggregationSdQuery));
        _streamRepo.Verify(r => r.ExecuteGroupedAggregationQueryAsync(
            It.IsAny<OctoObjectId>(), It.IsAny<StreamDataGroupedAggregationQueryOptions>()),
            Times.Never);
    }

    [Fact]
    public async Task ExecuteStreamDataQuery_DownsamplingSubtype_RejectsMissingTimeRange()
    {
        WireStreamDataMocks();
        var downsampling = new RtDownsamplingSdQuery
        {
            QueryCkTypeId = SensorCkType.ToString(),
            ArchiveRtId = ArchiveRtId
        };
        downsampling.Columns.Add(new RtAggregationQueryColumnRecord
        {
            AttributePath = "Power",
            AggregationType = RtAggregationTypesEnum.Average
        });

        MockTenantRepository
            .Setup(r => r.GetRtEntityByRtIdAsync<RtStreamDataQuery>(
                It.IsAny<IOctoSession>(), It.IsAny<OctoObjectId>()))
            .ReturnsAsync(downsampling);

        var result = await StreamDataAggregationTools.ExecuteStreamDataQuery(
            MockServer.Object, QueryRtId);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("from < to");
        _streamRepo.Verify(r => r.ExecuteDownsamplingQueryAsync(
            It.IsAny<OctoObjectId>(), It.IsAny<StreamDataDownsamplingQueryOptions>()),
            Times.Never);
    }

    [Fact]
    public async Task ExecuteStreamDataQuery_DownsamplingSubtype_RejectsMissingLimit()
    {
        WireStreamDataMocks();
        var downsampling = new RtDownsamplingSdQuery
        {
            QueryCkTypeId = SensorCkType.ToString(),
            ArchiveRtId = ArchiveRtId
        };
        downsampling.Columns.Add(new RtAggregationQueryColumnRecord
        {
            AttributePath = "Power",
            AggregationType = RtAggregationTypesEnum.Average
        });

        MockTenantRepository
            .Setup(r => r.GetRtEntityByRtIdAsync<RtStreamDataQuery>(
                It.IsAny<IOctoSession>(), It.IsAny<OctoObjectId>()))
            .ReturnsAsync(downsampling);

        // From/To via overrides, but limit absent → error.
        var result = await StreamDataAggregationTools.ExecuteStreamDataQuery(
            MockServer.Object, QueryRtId,
            fromOverride: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            toOverride: new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc));

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("bucket limit");
    }

    [Fact]
    public async Task ExecuteStreamDataQuery_DownsamplingSubtype_OverridesEnableExecution()
    {
        WireStreamDataMocks();
        var downsampling = new RtDownsamplingSdQuery
        {
            QueryCkTypeId = SensorCkType.ToString(),
            ArchiveRtId = ArchiveRtId
        };
        downsampling.Columns.Add(new RtAggregationQueryColumnRecord
        {
            AttributePath = "Power",
            AggregationType = RtAggregationTypesEnum.Average
        });

        MockTenantRepository
            .Setup(r => r.GetRtEntityByRtIdAsync<RtStreamDataQuery>(
                It.IsAny<IOctoSession>(), It.IsAny<OctoObjectId>()))
            .ReturnsAsync(downsampling);

        var queryResult = new StreamDataQueryResult
        {
            Rows =
            [
                new StreamDataRow
                {
                    Timestamp = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                    Values = new Dictionary<string, object?> { ["Average(Power)"] = 50.0 }
                }
            ],
            TotalCount = 1
        };

        _streamRepo
            .Setup(r => r.ExecuteDownsamplingQueryAsync(It.IsAny<OctoObjectId>(),
                It.IsAny<StreamDataDownsamplingQueryOptions>()))
            .ReturnsAsync(queryResult);

        var result = await StreamDataAggregationTools.ExecuteStreamDataQuery(
            MockServer.Object, QueryRtId,
            fromOverride: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            toOverride: new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc),
            limitOverride: 24);

        result.IsSuccess.Should().BeTrue(result.ErrorMessage ?? "");
        result.QuerySubtype.Should().Be(nameof(RtDownsamplingSdQuery));
        result.Rows.Should().NotBeNull().And.HaveCount(1);
    }
}
