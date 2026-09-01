using FluentAssertions;
using Meshmakers.Octo.Backend.McpServices.Models.Aggregation;
using Meshmakers.Octo.Backend.McpServices.Tools;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v2;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.DataPermissions;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Moq;
using Xunit;

namespace McpServices.Tests.Tools;

/// <summary>
///     AB#5038 — the stream-data CK-type permission gate. The MCP surface must answer exactly like the
///     asset-repo GraphQL <c>DataPermissionStreamGuard</c>: a full Read grant passes, an owner-scoped
///     grant is refused (stream rows carry no creator, so row-level filtering is impossible — accepted
///     limitation F4 of AB#4969), an unprotected type passes untouched, and AuditOnly policies never
///     block.
/// </summary>
public class StreamDataPermissionGateTests : TestBase
{
    private const string ArchiveRtId = "507f1f77bcf86cd799439011";
    private const string RollupRtId = "507f1f77bcf86cd799439013";
    private const string QueryRtId = "507f1f77bcf86cd799439014";

    private static readonly RtCkId<CkTypeId> SensorCkType = new("EnergyCommunity-1/Sensor-1");

    /// <summary>
    ///     The exact refusal the GraphQL guard renders for the same situation. Pinned literally so a
    ///     wording drift on either surface shows up as a failing test.
    /// </summary>
    private static string ExpectedRefusal =>
        $"Access denied: missing data permission 'Read' on '{SensorCkType.SemanticVersionedFullName}' "
        + "for stream data.";

    private readonly Mock<ITenantContext> _tenantCtx = new();
    private readonly Mock<IStreamDataRepository> _streamRepo = new();
    private readonly Mock<IArchiveRuntimeStore> _archiveStore = new();
    private readonly Mock<IRollupArchiveRuntimeStore> _rollupStore = new();

    public StreamDataPermissionGateTests()
    {
        MockTenantResolution
            .Setup(t => t.GetTenantContextAsync(It.IsAny<string?>()))
            .ReturnsAsync(_tenantCtx.Object);
        _tenantCtx.Setup(c => c.TenantId).Returns(DefaultTestTenantId);
        _tenantCtx.Setup(c => c.GetStreamDataRepository()).Returns(_streamRepo.Object);
        _tenantCtx.Setup(c => c.GetArchiveRuntimeStore()).Returns(_archiveStore.Object);
        _tenantCtx.Setup(c => c.GetRollupArchiveRuntimeStore()).Returns(_rollupStore.Object);

        _archiveStore
            .Setup(s => s.GetAsync(It.IsAny<OctoObjectId>()))
            .ReturnsAsync(new ArchiveSnapshot(
                new OctoObjectId(ArchiveRtId), SensorCkType, CkArchiveStatus.Activated,
                "TestArchive", Columns: []));

        _streamRepo
            .Setup(r => r.ExecuteQueryAsync(It.IsAny<OctoObjectId>(),
                It.IsAny<StreamDataQueryOptions>()))
            .ReturnsAsync(new StreamDataQueryResult { Rows = [], TotalCount = 0 });
    }

    // ── Arrangements ────────────────────────────────────────────────────────

    /// <summary>
    ///     Registers a policy table with one rule protecting <see cref="SensorCkType" />.
    /// </summary>
    /// <param name="ownedOnly">True to grant the caller an owner-scoped read instead of a full read.</param>
    /// <param name="auditOnly">True to mark the policy audit-only (must never block).</param>
    /// <param name="grantedRole">Role the permission is granted to; defaults to the caller's role.</param>
    private void GivenPolicy(bool ownedOnly = false, bool auditOnly = false,
        string grantedRole = DefaultCallerRole)
    {
        var rule = new RtDataPolicyRule(
            PermissionId: "Test.StreamRead",
            TargetCkTypeIds: [SensorCkType.SemanticVersionedFullName],
            Actions: [RtDataAction.Read],
            OwnedOnly: ownedOnly,
            AuditOnly: auditOnly,
            GrantedRoleNames: [grantedRole]);

        TestServiceProvider.RegisterService<IDataPermissionResolver>(
            new StubDataPermissionResolver(new RtDataPolicyTable([rule])));
    }

    /// <summary>
    ///     Registers a resolver whose policy table is empty — the tenant has no data permissions at all.
    /// </summary>
    private void GivenNoPolicies() =>
        TestServiceProvider.RegisterService<IDataPermissionResolver>(
            new StubDataPermissionResolver(RtDataPolicyTable.Empty));

    // ── Transient queries (shared StreamDataContext resolution path) ─────────

    [Fact]
    public async Task Simple_FullReadGrant_Allowed()
    {
        GivenPolicy();

        var result = await StreamDataAggregationTools.QuerySimple(
            MockServer.Object, ArchiveRtId, ["Power"]);

        result.IsSuccess.Should().BeTrue();
        _streamRepo.Verify(r => r.ExecuteQueryAsync(It.IsAny<OctoObjectId>(),
            It.IsAny<StreamDataQueryOptions>()), Times.Once);
    }

    [Fact]
    public async Task Simple_OwnerScopedGrant_RefusedWithTheGraphQlWording()
    {
        GivenPolicy(ownedOnly: true);

        var result = await StreamDataAggregationTools.QuerySimple(
            MockServer.Object, ArchiveRtId, ["Power"]);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Be(ExpectedRefusal);
        // Refused before any CrateDB SQL is built — same ordering guarantee as the GraphQL guard.
        _streamRepo.Verify(r => r.ExecuteQueryAsync(It.IsAny<OctoObjectId>(),
            It.IsAny<StreamDataQueryOptions>()), Times.Never);
    }

    [Fact]
    public async Task Simple_NoGrantAtAllOnProtectedType_Refused()
    {
        // The policy protects the type but grants it to a role the caller does not have.
        GivenPolicy(grantedRole: "SomeOtherRole");

        var result = await StreamDataAggregationTools.QuerySimple(
            MockServer.Object, ArchiveRtId, ["Power"]);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Be(ExpectedRefusal);
    }

    [Fact]
    public async Task Simple_UnprotectedType_Allowed()
    {
        GivenNoPolicies();

        var result = await StreamDataAggregationTools.QuerySimple(
            MockServer.Object, ArchiveRtId, ["Power"]);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Simple_AuditOnlyPolicy_Allowed()
    {
        // Enforcement view ignores AuditOnly rules — includeAuditOnlyPolicies: false, as on the
        // GraphQL side. An owner-scoped AuditOnly grant must therefore still pass.
        GivenPolicy(ownedOnly: true, auditOnly: true);

        var result = await StreamDataAggregationTools.QuerySimple(
            MockServer.Object, ArchiveRtId, ["Power"]);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Simple_ResolverNotWired_GuardIsDormant()
    {
        // No IDataPermissionResolver registered: the guard no-ops exactly like the GraphQL one, which
        // resolves it with GetService and returns when it is absent.
        var result = await StreamDataAggregationTools.QuerySimple(
            MockServer.Object, ArchiveRtId, ["Power"]);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Aggregation_OwnerScopedGrant_Refused()
    {
        GivenPolicy(ownedOnly: true);

        var result = await StreamDataAggregationTools.QueryAggregation(
            MockServer.Object, ArchiveRtId,
            [new() { Function = AggregationFunctionDto.count }]);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Be(ExpectedRefusal);
    }

    [Fact]
    public async Task Downsampling_OwnerScopedGrant_Refused()
    {
        GivenPolicy(ownedOnly: true);

        var result = await StreamDataAggregationTools.QueryDownsampling(
            MockServer.Object, ArchiveRtId,
            [new() { Function = AggregationFunctionDto.avg, AttributePath = "Power" }],
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc),
            100);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Be(ExpectedRefusal);
    }

    // ── Persisted query (guards on the persisted QueryCkTypeId) ─────────────

    [Fact]
    public async Task PersistedQuery_OwnerScopedGrant_Refused()
    {
        GivenPolicy(ownedOnly: true);
        GivenPersistedQuery();

        var result = await StreamDataAggregationTools.ExecuteStreamDataQuery(
            MockServer.Object, QueryRtId);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Be(ExpectedRefusal);
        _streamRepo.Verify(r => r.ExecuteQueryAsync(It.IsAny<OctoObjectId>(),
            It.IsAny<StreamDataQueryOptions>()), Times.Never);
    }

    [Fact]
    public async Task PersistedQuery_FullReadGrant_Allowed()
    {
        GivenPolicy();
        GivenPersistedQuery();

        var result = await StreamDataAggregationTools.ExecuteStreamDataQuery(
            MockServer.Object, QueryRtId);

        result.IsSuccess.Should().BeTrue();
    }

    private void GivenPersistedQuery()
    {
        var query = new RtSimpleSdQuery
        {
            ArchiveRtId = ArchiveRtId,
            QueryCkTypeId = SensorCkType.ToString()
        };
        query.Columns.Add("Power");

        MockTenantRepository
            .Setup(r => r.GetRtEntityByRtIdAsync<RtStreamDataQuery>(
                It.IsAny<IOctoSession>(), It.IsAny<OctoObjectId>()))
            .ReturnsAsync(query);
    }

    // ── Metadata tools ──────────────────────────────────────────────────────

    [Fact]
    public async Task StorageStats_OwnerScopedGrant_Refused()
    {
        GivenPolicy(ownedOnly: true);

        var result = await StreamDataMetadataTools.GetArchiveStorageStats(
            MockServer.Object, [ArchiveRtId]);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Be(ExpectedRefusal);
        _streamRepo.Verify(r => r.GetArchiveStatsAsync(
            It.IsAny<IReadOnlyList<OctoObjectId>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task StorageStats_UnprotectedTenant_DoesNotTouchTheArchiveStore()
    {
        // The dormant path must stay the single GetArchiveStatsAsync round-trip it has always been —
        // no snapshot lookups just to find a CK type nothing protects.
        GivenNoPolicies();
        _streamRepo
            .Setup(r => r.GetArchiveStatsAsync(It.IsAny<IReadOnlyList<OctoObjectId>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<OctoObjectId, ArchiveStorageStats>());

        var result = await StreamDataMetadataTools.GetArchiveStorageStats(
            MockServer.Object, [ArchiveRtId]);

        result.IsSuccess.Should().BeTrue();
        _archiveStore.Verify(s => s.GetAsync(It.IsAny<OctoObjectId>()), Times.Never);
    }

    [Fact]
    public async Task RollupMetadata_OwnerScopedGrant_Refused()
    {
        GivenPolicy(ownedOnly: true);
        _rollupStore
            .Setup(s => s.GetAsync(It.IsAny<OctoObjectId>()))
            .ReturnsAsync(new RollupArchiveSnapshot(
                new OctoObjectId(RollupRtId), SensorCkType, CkArchiveStatus.Activated, "hourly",
                new OctoObjectId(ArchiveRtId), TimeSpan.FromHours(1), TimeSpan.FromMinutes(1), null,
                [new CkRollupAggregationSpec("Power", CkRollupFunction.Avg, null)], null));

        var result = await StreamDataMetadataTools.GetRollupQueryMetadata(
            MockServer.Object, RollupRtId);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Be(ExpectedRefusal);
    }

    [Fact]
    public async Task ResolveSeries_OwnerScopedGrant_Refused()
    {
        GivenPolicy(ownedOnly: true);

        var result = await StreamDataMetadataTools.ResolveSeriesQuery(
            MockServer.Object, ArchiveRtId,
            new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            "Power", "sum");

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Be(ExpectedRefusal);
    }

    /// <summary>
    ///     Hands out a fixed policy table for any repository, standing in for the engine's TTL-cached
    ///     <see cref="IDataPermissionResolver" />.
    /// </summary>
    private sealed class StubDataPermissionResolver(RtDataPolicyTable table) : IDataPermissionResolver
    {
        public Task<RtDataPolicyTable> GetPolicyTableAsync(IRuntimeRepository runtimeRepository) =>
            Task.FromResult(table);

        public void Invalidate(string tenantId)
        {
        }
    }
}
