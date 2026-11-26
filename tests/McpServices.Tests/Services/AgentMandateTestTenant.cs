using Meshmakers.Octo.Backend.McpServices.Services.Agents;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Engine.Repositories.Query;
using Moq;

namespace McpServices.Tests.Services;

/// <summary>
///     An in-memory tenant for the mandate check (AB#9729): agents, mandates and <c>GovernedBy</c> edges behind a mocked
///     <see cref="ITenantRepository" />, plus the CK cache answer for the agent types. Records which sessions were opened
///     (the check must read as the system).
/// </summary>
internal sealed class AgentMandateTestTenant
{
    private readonly Dictionary<OctoObjectId, RtEntity> _entities = new();
    private readonly List<(OctoObjectId Agent, OctoObjectId Mandate)> _governedBy = [];

    public AgentMandateTestTenant(string tenantId)
    {
        TenantId = tenantId;
        Repository = new Mock<ITenantRepository>();
        Repository.Setup(r => r.TenantId).Returns(tenantId);
        var sessions = Repository.As<ISecureSessionFactory>();
        sessions.Setup(f => f.GetSessionAsync(It.IsAny<RtSecurityContext>()))
            .ReturnsAsync((RtSecurityContext context) =>
            {
                OpenedSessions.Add(context);
                return new Mock<IOctoSession>().Object;
            });
        Repository.Setup(r => r.GetSessionAsync())
            .ThrowsAsync(new InvalidOperationException("Use GetSessionAsync(RtSecurityContext)."));
        Repository
            .Setup(r => r.GetRtEntitiesByTypeAsync(It.IsAny<IOctoSession>(), It.IsAny<RtCkId<CkTypeId>>(),
                It.IsAny<RtEntityQueryOptions>(), It.IsAny<int?>(), It.IsAny<int?>()))
            .ReturnsAsync((IOctoSession _, RtCkId<CkTypeId> type, RtEntityQueryOptions _, int? _, int? take) =>
            {
                if (ReadFails)
                {
                    throw new TimeoutException("store unavailable");
                }

                var items = _entities.Values.Where(e => Equals(e.CkTypeId, type)).Take(take ?? int.MaxValue).ToList();
                return new ResultSet<RtEntity>(items, items.Count, null, null);
            });
        Repository
            .Setup(r => r.GetRtEntityByRtIdAsync(It.IsAny<IOctoSession>(), It.IsAny<RtEntityId>()))
            .ReturnsAsync((IOctoSession _, RtEntityId id) =>
                _entities.TryGetValue(id.RtId, out var e) && Equals(e.CkTypeId, id.CkTypeId) ? e : null);
        Repository
            .Setup(r => r.GetRtAssociationsAsync(It.IsAny<IOctoSession>(), It.IsAny<RtEntityId>(),
                It.IsAny<RtAssociationExtendedQueryOptions>()))
            .ReturnsAsync((IOctoSession _, RtEntityId origin, RtAssociationExtendedQueryOptions _) =>
            {
                var items = _governedBy.Where(g => g.Agent == origin.RtId)
                    .Select(g => new RtAssociation { OriginRtId = g.Agent, TargetRtId = g.Mandate }).ToList();
                return new ResultSet<RtAssociation>(items, items.Count, null, null);
            });

        CkCache = new Mock<ICkCacheService>();
        GivenAgentTypes(true);
    }

    public string TenantId { get; }

    public Mock<ITenantRepository> Repository { get; }

    public Mock<ICkCacheService> CkCache { get; }

    public List<RtSecurityContext> OpenedSessions { get; } = [];

    /// <summary>When true, reading the agents throws (store unavailable).</summary>
    public bool ReadFails { get; set; }

    public void GivenAgentTypes(bool present)
    {
        CkTypeGraph? type = present ? CkGraphTestBuilder.BuildType("System.Ai-4.12.0/Agent-1") : null;
        CkCache.Setup(c => c.TryGetRtCkType(It.IsAny<string>(), It.IsAny<RtCkId<CkTypeId>>(), out type))
            .Returns(present);
    }

    /// <summary>Adds an agent with its own mandate; returns both.</summary>
    public (RtEntity Agent, RtEntity Mandate) AddAgent(string name, string clientId, int riskCeiling,
        params (string Pattern, int? MaxRisk)[] grants)
    {
        var mandate = AddMandate(riskCeiling, grants);
        var agent = AddAgentOnly(name, clientId);
        Govern(agent, mandate);
        return (agent, mandate);
    }

    /// <summary>Adds an agent without mandate; <paramref name="enabled" /> null leaves the attribute out.</summary>
    public RtEntity AddAgentOnly(string name, string clientId, bool? enabled = true)
    {
        var agent = new RtEntity { RtId = OctoObjectId.GenerateNewId(), CkTypeId = AgentModel.AgentTypeId };
        agent.SetAttributeValue("Name", AttributeValueTypesDto.String, name);
        agent.SetAttributeValue("ServiceAccountClientId", AttributeValueTypesDto.String, clientId);
        if (enabled != null)
        {
            agent.SetAttributeValue("Enabled", AttributeValueTypesDto.Boolean, enabled.Value);
        }

        _entities[agent.RtId] = agent;
        return agent;
    }

    public RtEntity AddMandate(int riskCeiling, params (string Pattern, int? MaxRisk)[] grants)
    {
        var mandate = new RtEntity { RtId = OctoObjectId.GenerateNewId(), CkTypeId = AgentModel.MandateTypeId };
        mandate.SetAttributeValue("RiskCeiling", AttributeValueTypesDto.Enum, riskCeiling);
        mandate.SetAttributeValue("ToolGrants", AttributeValueTypesDto.RecordArray, grants.Select(g =>
        {
            var attributes = new Dictionary<string, object?> { ["ToolPattern"] = g.Pattern };
            if (g.MaxRisk != null)
            {
                attributes["MaxRisk"] = g.MaxRisk.Value;
            }

            return new RtRecord(new RtCkId<CkRecordId>(AgentModel.ToolGrantRecord), attributes);
        }).ToList());
        _entities[mandate.RtId] = mandate;
        return mandate;
    }

    public void Govern(RtEntity agent, RtEntity mandate) => _governedBy.Add((agent.RtId, mandate.RtId));

    public void Remove(RtEntity entity) => _entities.Remove(entity.RtId);

    public void SetEnabled(RtEntity agent, bool enabled) =>
        agent.SetAttributeValue("Enabled", AttributeValueTypesDto.Boolean, enabled);

    public void SetGrants(RtEntity mandate, params (string Pattern, int? MaxRisk)[] grants)
    {
        var replacement = AddMandate(0, grants);
        _entities.Remove(replacement.RtId);
        mandate.SetAttributeValue("ToolGrants", AttributeValueTypesDto.RecordArray,
            replacement.GetAttributeValueOrDefault("ToolGrants"));
    }
}
