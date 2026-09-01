using FluentAssertions;
using Meshmakers.Octo.Backend.McpServices.Models;
using Meshmakers.Octo.Backend.McpServices.Models.Aggregation;
using Meshmakers.Octo.Backend.McpServices.Resources;
using Meshmakers.Octo.Backend.McpServices.Tools;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Server;
using Moq;
using Xunit;

namespace McpServices.Tests.Tools;

/// <summary>
///     AB#5030 — the direct-engine tool families (2 and 3) must (a) refuse a <c>tenantId</c> the
///     caller's session token was not issued for and (b) open their runtime sessions with the
///     caller's <see cref="RtSecurityContext" /> instead of a system session.
/// </summary>
public class RuntimeTenantGateTests : TestBase
{
    private const string TestCkTypeId = "TestModule-1.0.0/Customer-1";
    private const string TestRtId = "507f1f77bcf86cd799439011";

    // ── RuntimeEntityCrudTools (family 2) ───────────────────────────────────────────────────

    [Fact]
    public async Task QueryEntities_ForeignTenant_IsDeniedAndOpensNoSession()
    {
        GivenForeignTenantCall();

        var result = await RuntimeEntityCrudTools.QueryEntities(
            MockServer.Object, TestCkTypeId, tenantId: ForeignTenantId);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("denied");
        MockTenantRepository.Verify(r => r.GetSessionAsync(), Times.Never);
    }

    [Fact]
    public async Task QueryEntities_Unauthenticated_ReturnsNotAuthenticated()
    {
        GivenUnauthenticatedCaller();

        var result = await RuntimeEntityCrudTools.QueryEntities(MockServer.Object, TestCkTypeId);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().StartWith("Not authenticated");
        MockTenantRepository.Verify(r => r.GetSessionAsync(), Times.Never);
    }

    [Fact]
    public async Task CreateEntity_ForeignTenant_IsDeniedAndOpensNoSession()
    {
        GivenForeignTenantCall();

        var result = await RuntimeEntityCrudTools.CreateEntity(
            MockServer.Object, TestCkTypeId, [], ForeignTenantId);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("denied");
        MockTenantRepository.Verify(r => r.GetSessionAsync(), Times.Never);
    }

    [Fact]
    public async Task CreateEntity_Unauthenticated_ReturnsNotAuthenticated()
    {
        GivenUnauthenticatedCaller();

        var result = await RuntimeEntityCrudTools.CreateEntity(MockServer.Object, TestCkTypeId, []);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().StartWith("Not authenticated");
    }

    [Fact]
    public async Task UpdateEntity_ForeignTenant_IsDenied()
    {
        GivenForeignTenantCall();

        var result = await RuntimeEntityCrudTools.UpdateEntity(
            MockServer.Object, TestRtId, TestCkTypeId, [], tenantId: ForeignTenantId);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("denied");
        MockTenantRepository.Verify(r => r.GetSessionAsync(), Times.Never);
    }

    [Fact]
    public async Task DeleteEntity_ForeignTenant_IsDenied()
    {
        GivenForeignTenantCall();

        var result = await RuntimeEntityCrudTools.DeleteEntity(
            MockServer.Object, TestCkTypeId, TestRtId, tenantId: ForeignTenantId);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("denied");
        MockTenantRepository.Verify(r => r.GetSessionAsync(), Times.Never);
    }

    [Fact]
    public async Task GetEntityById_Unauthenticated_ReturnsNotAuthenticated()
    {
        GivenUnauthenticatedCaller();

        var result = await RuntimeEntityCrudTools.GetEntityById(MockServer.Object, TestCkTypeId, TestRtId);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().StartWith("Not authenticated");
    }

    [Fact]
    public async Task NavigateAssociations_ForeignTenant_IsDenied()
    {
        GivenForeignTenantCall();

        var result = await RuntimeEntityCrudTools.NavigateAssociations(
            MockServer.Object, TestCkTypeId, TestRtId, "System/ParentChild",
            CkTypeAssociationDirectionDto.Outbound, TestCkTypeId, tenantId: ForeignTenantId);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("denied");
    }

    [Fact]
    public async Task GetAssociationTree_ForeignTenant_IsDenied()
    {
        GivenForeignTenantCall();

        var result = await RuntimeEntityCrudTools.GetAssociationTree(
            MockServer.Object, TestCkTypeId, "System/ParentChild",
            CkTypeAssociationDirectionDto.Outbound, tenantId: ForeignTenantId);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("denied");
    }

    // ── RuntimeAggregationTools (family 3) ──────────────────────────────────────────────────

    [Fact]
    public async Task QueryEntitiesAggregation_ForeignTenant_IsDeniedAndOpensNoSession()
    {
        GivenForeignTenantCall();

        var result = await RuntimeAggregationTools.QueryEntitiesAggregation(
            MockServer.Object, TestCkTypeId,
            [new AggregationColumnDto { Function = AggregationFunctionDto.count }],
            tenantId: ForeignTenantId);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("denied");
        MockTenantRepository.Verify(r => r.GetSessionAsync(), Times.Never);
    }

    [Fact]
    public async Task QueryEntitiesGrouping_Unauthenticated_ReturnsNotAuthenticated()
    {
        GivenUnauthenticatedCaller();

        var result = await RuntimeAggregationTools.QueryEntitiesGrouping(
            MockServer.Object, TestCkTypeId, ["Region"],
            [new AggregationColumnDto { Function = AggregationFunctionDto.count }]);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().StartWith("Not authenticated");
    }

    [Fact]
    public async Task ExecuteRuntimeQuery_ForeignTenant_IsDeniedAndOpensNoSession()
    {
        GivenForeignTenantCall();

        var result = await RuntimeAggregationTools.ExecuteRuntimeQuery(
            MockServer.Object, TestRtId, tenantId: ForeignTenantId);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("denied");
        MockTenantRepository.Verify(r => r.GetSessionAsync(), Times.Never);
    }

    // ── StreamData tools (family 3) ─────────────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteStreamDataQuery_ForeignTenant_IsDenied()
    {
        GivenForeignTenantCall();

        var result = await StreamDataAggregationTools.ExecuteStreamDataQuery(
            MockServer.Object, TestRtId, tenantId: ForeignTenantId);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("denied");
        MockTenantRepository.Verify(r => r.GetSessionAsync(), Times.Never);
    }

    [Fact]
    public async Task QueryStreamDataSimple_ForeignTenant_IsDenied()
    {
        GivenForeignTenantCall();

        var result = await StreamDataAggregationTools.QuerySimple(
            MockServer.Object, TestRtId, ["Value"], tenantId: ForeignTenantId);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("denied");
        MockTenantResolution.Verify(t => t.GetTenantContextAsync(It.IsAny<string?>()), Times.Never);
    }

    [Fact]
    public async Task QueryStreamDataSimple_Unauthenticated_ReturnsNotAuthenticated()
    {
        GivenUnauthenticatedCaller();

        var result = await StreamDataAggregationTools.QuerySimple(MockServer.Object, TestRtId, ["Value"]);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().StartWith("Not authenticated");
    }

    [Fact]
    public async Task GetArchiveStorageStats_ForeignTenant_IsDenied()
    {
        GivenForeignTenantCall();

        var result = await StreamDataMetadataTools.GetArchiveStorageStats(
            MockServer.Object, [TestRtId], ForeignTenantId);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("denied");
        MockTenantResolution.Verify(t => t.GetTenantContextAsync(It.IsAny<string?>()), Times.Never);
    }

    [Fact]
    public async Task GetRollupQueryMetadata_Unauthenticated_ReturnsNotAuthenticated()
    {
        GivenUnauthenticatedCaller();

        var result = await StreamDataMetadataTools.GetRollupQueryMetadata(MockServer.Object, TestRtId);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().StartWith("Not authenticated");
    }

    [Fact]
    public async Task ResolveSeriesQuery_ForeignTenant_IsDenied()
    {
        GivenForeignTenantCall();

        var result = await StreamDataMetadataTools.ResolveSeriesQuery(
            MockServer.Object, TestRtId,
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc),
            "Amount.Value", "sum", tenantId: ForeignTenantId);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("denied");
    }

    // ── SchemaDiscoveryTools (family 2, gate only) ──────────────────────────────────────────

    [Fact]
    public async Task GetAvailableModels_ForeignTenant_IsDenied()
    {
        GivenForeignTenantCall();

        var result = await SchemaDiscoveryTools.GetAvailableModels(MockServer.Object, ForeignTenantId);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("denied");
        MockTenantResolution.Verify(t => t.GetTenantRepositoryAsync(It.IsAny<string?>()), Times.Never);
    }

    [Fact]
    public async Task GetAvailableTypes_Unauthenticated_ReturnsNotAuthenticated()
    {
        GivenUnauthenticatedCaller();

        var result = await SchemaDiscoveryTools.GetAvailableTypes(MockServer.Object);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().StartWith("Not authenticated");
    }

    [Fact]
    public async Task SearchTypes_ForeignTenant_InheritsTheGateFromGetAvailableTypes()
    {
        GivenForeignTenantCall();

        var result = await SchemaDiscoveryTools.SearchTypes(
            MockServer.Object, "Customer", tenantId: ForeignTenantId);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("denied");
    }

    [Fact]
    public async Task GetTypeSchema_ForeignTenant_IsDenied()
    {
        GivenForeignTenantCall();

        var result = await SchemaDiscoveryTools.GetTypeSchema(
            MockServer.Object, TestCkTypeId, ForeignTenantId);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("denied");
    }

    [Fact]
    public async Task GetAvailableArchivePaths_ForeignTenant_IsDenied()
    {
        GivenForeignTenantCall();

        var result = await SchemaDiscoveryTools.GetAvailableArchivePaths(
            MockServer.Object, TestCkTypeId, tenantId: ForeignTenantId);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("denied");
    }

    // ── Resources ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CkSchemaResource_ForeignTenant_RendersDeniedError()
    {
        GivenForeignTenantCall();

        var markdown = await CkSchemaResources.GetSystemSchemaAsync(MockServer.Object, ForeignTenantId);

        markdown.Should().Contain("denied");
        MockTenantResolution.Verify(t => t.GetTenantRepositoryAsync(It.IsAny<string?>()), Times.Never);
    }

    [Fact]
    public async Task KnowledgeResource_ForeignTenant_RendersDeniedError()
    {
        GivenForeignTenantCall();

        var markdown = await KnowledgeResources.GetKnowledgeSourceAsync(
            MockServer.Object, ForeignTenantId, TestRtId);

        markdown.Should().Contain("denied");
        MockTenantRepository.Verify(r => r.GetSessionAsync(), Times.Never);
    }

    [Fact]
    public async Task KnowledgeResource_Unauthenticated_RendersNotAuthenticatedError()
    {
        GivenUnauthenticatedCaller();

        var markdown = await KnowledgeResources.GetKnowledgeSourceAsync(
            MockServer.Object, DefaultTestTenantId, TestRtId);

        markdown.Should().Contain("Not authenticated");
    }

    // ── Gate variants reaching the tools ────────────────────────────────────────────────────────

    [Fact]
    public async Task QueryEntities_UserWithoutTenantClaim_IsDeniedAndOpensNoSession()
    {
        GivenCallerWithoutTenantClaim();

        var result = await RuntimeEntityCrudTools.QueryEntities(MockServer.Object, TestCkTypeId);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("denied").And.Contain("tenant_id");
        MockSecureSessionFactory.Verify(f => f.GetSessionAsync(It.IsAny<RtSecurityContext>()), Times.Never);
    }

    [Fact]
    public async Task QueryEntities_ServicePrincipal_ReachesAnyTenantAsItsClientId()
    {
        // AB#5032 exemption — the AI Adapter worker and the mesh-adapter node depend on it.
        MockTenantResolution.Setup(t => t.ResolveTenantId(ForeignTenantId)).Returns(ForeignTenantId);
        GivenServicePrincipalCaller("octo-ai-worker", DefaultTestTenantId, "ServiceRole");

        await RuntimeEntityCrudTools.QueryEntities(
            MockServer.Object, TestCkTypeId, tenantId: ForeignTenantId);

        MockSecureSessionFactory.Verify(f => f.GetSessionAsync(It.Is<RtSecurityContext>(c =>
            !c.IsSystem && c.SubjectId == "octo-ai-worker")), Times.Once);
    }

    [Fact]
    public async Task QueryEntities_CrossTenant_UsesTheShadowIdentityOfTheExchangedToken()
    {
        GivenSuccessfulTenantExchange(ForeignTenantId, "shadow-user-in-b", "BReader");

        await RuntimeEntityCrudTools.QueryEntities(
            MockServer.Object, TestCkTypeId, tenantId: ForeignTenantId);

        MockSecureSessionFactory.Verify(f => f.GetSessionAsync(It.Is<RtSecurityContext>(c =>
            c.SubjectId == "shadow-user-in-b"
            && c.Roles.Contains("BReader")
            && !c.Roles.Contains(DefaultCallerRole))), Times.Once);
    }

    [Fact]
    public async Task QueryEntities_FailedExchange_DoesNotClaimTheCallerIsUnauthenticated()
    {
        // M2 — "Not authenticated" would send the AI client into a pointless device-flow re-login.
        GivenFailingTenantExchange();

        var result = await RuntimeEntityCrudTools.QueryEntities(
            MockServer.Object, TestCkTypeId, tenantId: ForeignTenantId);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().NotStartWith("Not authenticated");
        result.ErrorMessage.Should().Contain("exchange failed");
    }

    // ── Every session-opening call site carries the caller's context (H1) ───────────────────────

    /// <summary>
    ///     One entry per direct-engine call site that opens a runtime session. Before AB#5030 all of them
    ///     opened a parameterless system session; <see cref="TestBase" /> now makes that overload throw,
    ///     so a site that regresses fails here instead of quietly running with system privileges.
    /// </summary>
    public static TheoryData<string, Func<McpServer, Task>> SessionOpeningCallSites => new()
    {
        { "query_entities", s => RuntimeEntityCrudTools.QueryEntities(s, TestCkTypeId) },
        { "query_entities_simple", s => RuntimeEntityCrudTools.QueryEntitiesSimple(s, TestCkTypeId) },
        { "get_entity_by_id", s => RuntimeEntityCrudTools.GetEntityById(s, TestCkTypeId, TestRtId) },
        { "create_entity", s => RuntimeEntityCrudTools.CreateEntity(s, TestCkTypeId, []) },
        { "update_entity", s => RuntimeEntityCrudTools.UpdateEntity(s, TestRtId, TestCkTypeId, []) },
        { "delete_entity", s => RuntimeEntityCrudTools.DeleteEntity(s, TestCkTypeId, TestRtId) },
        {
            "navigate_associations",
            s => RuntimeEntityCrudTools.NavigateAssociations(
                s, TestCkTypeId, TestRtId, "System/ParentChild",
                CkTypeAssociationDirectionDto.Outbound, TestCkTypeId)
        },
        {
            "get_association_tree",
            s => RuntimeEntityCrudTools.GetAssociationTree(
                s, TestCkTypeId, "System/ParentChild", CkTypeAssociationDirectionDto.Outbound)
        },
        { "execute_runtime_query", s => RuntimeAggregationTools.ExecuteRuntimeQuery(s, TestRtId) },
        {
            "query_entities_aggregation",
            s => RuntimeAggregationTools.QueryEntitiesAggregation(
                s, TestCkTypeId, [new AggregationColumnDto { Function = AggregationFunctionDto.count }])
        },
        {
            "query_entities_grouping",
            s => RuntimeAggregationTools.QueryEntitiesGrouping(
                s, TestCkTypeId, ["Region"],
                [new AggregationColumnDto { Function = AggregationFunctionDto.count }])
        },
        {
            "execute_stream_data_query",
            s => StreamDataAggregationTools.ExecuteStreamDataQuery(s, TestRtId)
        },
        {
            "knowledge_resource",
            s => KnowledgeResources.GetKnowledgeSourceAsync(s, DefaultTestTenantId, TestRtId)
        }
    };

    [Theory]
    [MemberData(nameof(SessionOpeningCallSites))]
    public async Task DirectEngineCallSite_OpensSessionWithCallerSecurityContext(
        string callSite, Func<McpServer, Task> invoke)
    {
        GivenStreamDataEnabled();
        GivenKnowledgeResourceDependencies();

        await invoke(MockServer.Object);

        MockSecureSessionFactory.Verify(
            f => f.GetSessionAsync(It.Is<RtSecurityContext>(c =>
                !c.IsSystem
                && c.SubjectId == DefaultCallerSubjectId
                && c.Roles.Contains(DefaultCallerRole))),
            Times.AtLeastOnce,
            $"'{callSite}' must open its runtime session as the caller, not as the system");
    }

    [Theory]
    [MemberData(nameof(SessionOpeningCallSites))]
    public async Task DirectEngineCallSite_Unauthenticated_OpensNoSession(
        string callSite, Func<McpServer, Task> invoke)
    {
        GivenStreamDataEnabled();
        GivenKnowledgeResourceDependencies();
        GivenUnauthenticatedCaller();

        await invoke(MockServer.Object);

        MockSecureSessionFactory.Verify(
            f => f.GetSessionAsync(It.IsAny<RtSecurityContext>()),
            Times.Never,
            $"'{callSite}' must not touch the engine without an authenticated caller");
    }

    [Fact]
    public async Task UpdateEntity_PostCommitReadSession_AlsoCarriesTheCallerContext()
    {
        // update_entity is the one tool that opens TWO sessions: the write transaction and a second
        // read session for the post-commit payload. The theory above only reaches the first one (its
        // arrangement makes the pre-check throw), so the second site needs its own arrangement —
        // otherwise a regression there would run the read with whatever the extension falls back to.
        var rtId = OctoObjectId.GenerateNewId();
        var existing = new RtEntity
        {
            RtId = rtId, CkTypeId = new RtCkId<CkTypeId>(TestCkTypeId), RtVersion = 7
        };
        var updated = new RtEntity
        {
            RtId = rtId, CkTypeId = new RtCkId<CkTypeId>(TestCkTypeId), RtVersion = 8
        };
        MockTenantRepository
            .SetupSequence(r => r.GetRtEntityByRtIdAsync(It.IsAny<IOctoSession>(), It.IsAny<RtEntityId>()))
            .ReturnsAsync(existing)
            .ReturnsAsync(updated);

        var result = await RuntimeEntityCrudTools.UpdateEntity(
            MockServer.Object, rtId.ToString(), TestCkTypeId, [], expectedVersion: 7);

        result.IsSuccess.Should().BeTrue("the post-commit read path must actually have been reached");
        MockSecureSessionFactory.Verify(
            f => f.GetSessionAsync(It.Is<RtSecurityContext>(c =>
                !c.IsSystem
                && c.SubjectId == DefaultCallerSubjectId
                && c.Roles.Contains(DefaultCallerRole))),
            Times.Exactly(2),
            "both the write transaction and the post-commit read session must act as the caller");
    }

    /// <summary>
    ///     Makes <c>GetTenantContextAsync</c> hand out a context whose stream-data repository exists, so
    ///     the stream-data call sites reach their session-opening line.
    /// </summary>
    private void GivenStreamDataEnabled()
    {
        var streamRepo = new Mock<IStreamDataRepository>();
        var tenantContext = new Mock<ITenantContext>();
        tenantContext.Setup(c => c.GetStreamDataRepository()).Returns(streamRepo.Object);
        MockTenantResolution
            .Setup(t => t.GetTenantContextAsync(It.IsAny<string?>()))
            .ReturnsAsync(tenantContext.Object);
    }

    /// <summary>
    ///     Registers the extra services <see cref="KnowledgeResources" /> resolves between the gate and
    ///     its session (an unresolved one would surface as a rendered error before the session is opened).
    /// </summary>
    private void GivenKnowledgeResourceDependencies()
    {
        TestServiceProvider.RegisterService(new Mock<IHttpClientFactory>().Object);
        TestServiceProvider.RegisterService<ILoggerFactory>(NullLoggerFactory.Instance);
    }
}
