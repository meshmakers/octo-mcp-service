using FluentAssertions;
using Meshmakers.Octo.Backend.McpServices.Models;
using Meshmakers.Octo.Backend.McpServices.Services;
using Meshmakers.Octo.Backend.McpServices.Services.Agents;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Xunit;

namespace McpServices.Tests.Services;

/// <summary>
///     AB#9729: the static mandate check. Invariant: a token of a client that is an agent never executes a tool outside
///     its mandate; when the mandate cannot be read, the call is refused (fail closed). One test per refusal code and per
///     abuse case of the story (disabled agent, deleted mandate, agent of another tenant with the same client id, user
///     token unaffected), plus the fail-closed paths of the lead's conditions (lookup error, missing tenant, several
///     agents per client id).
/// </summary>
public class AgentMandateGuardTests
{
    private const string Tenant = "tenant-a";
    private const string OtherTenant = "tenant-b";
    private const string AgentClient = "agent-builder";

    private const int Low = 0;
    private const int Medium = 1;
    private const int High = 2;

    private readonly AgentMandateTestTenant _tenant = new(Tenant);
    private readonly AgentMandateTestTenant _otherTenant = new(OtherTenant);
    private readonly List<string> _tenantsLookedUp = [];

    private static McpCallerPrincipal Client(string clientId, string? tenantId = Tenant, params string[] roles) =>
        new(null, clientId, tenantId, roles, ["octo_agent"]);

    private Task<AgentMandateDecision> CheckAsync(McpCallerPrincipal? caller, string tool,
        McpRiskLevel risk = McpRiskLevel.Low) =>
        AgentMandateGuard.CheckAsync(caller, tool, risk, tenantId =>
        {
            _tenantsLookedUp.Add(tenantId);
            return Task.FromResult(tenantId == OtherTenant ? _otherTenant.Repository.Object : _tenant.Repository.Object);
        }, () => _tenant.CkCache.Object);

    // ---- callers that are no agent -----------------------------------------------------------------------------------

    [Fact]
    public async Task UserToken_WithoutClient_IsNotChecked()
    {
        _tenant.AddAgent("Builder", AgentClient, Low);

        var decision = await CheckAsync(new McpCallerPrincipal("user-1", null, Tenant, ["TenantManagement"]),
            "delete_tenant", McpRiskLevel.High);

        decision.Should().Be(AgentMandateDecision.NotAnAgent);
        _tenantsLookedUp.Should().BeEmpty("a caller without client id is no agent; nothing is read");
    }

    [Theory]
    [InlineData("octo-cli", null)] // client-credentials client that is no agent
    [InlineData("octo-studio", "user-7")] // interactive user signed in through Studio
    public async Task ClientThatIsNoAgent_IsAllowedEveryTool(string clientId, string? subject)
    {
        _tenant.AddAgent("Builder", AgentClient, Low, ("query_entities", Low));

        var decision = await CheckAsync(new McpCallerPrincipal(subject, clientId, Tenant, []), "delete_entity",
            McpRiskLevel.High);

        decision.Allowed.Should().BeTrue();
        decision.AgentName.Should().BeNull();
    }

    [Fact]
    public async Task UnauthenticatedCaller_IsLeftToTheTool()
    {
        (await CheckAsync(null, "query_entities")).Should().Be(AgentMandateDecision.NotAnAgent);
    }

    [Fact]
    public async Task TenantWithoutAgentTypes_HasNoAgents()
    {
        _tenant.GivenAgentTypes(false);

        // A client without the agent scope (with it: AgentScopeInATenantWithoutAgentTypes_IsRefusedWithNotAnAgent).
        (await CheckAsync(new McpCallerPrincipal(null, AgentClient, Tenant, []), "delete_entity", McpRiskLevel.High))
            .Allowed.Should().BeTrue();
    }

    // ---- grant and risk ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task GrantedLowTool_IsAllowed_AndReadAsTheSystem()
    {
        _tenant.AddAgent("Builder", AgentClient, Low, ("query_entities", Low));

        var decision = await CheckAsync(Client(AgentClient), "query_entities");

        decision.Allowed.Should().BeTrue();
        decision.AgentName.Should().Be("Builder");
        _tenant.OpenedSessions.Should().NotBeEmpty().And.OnlyContain(c => c.IsSystem,
            "the agent's own client may not be able to read the agent types");
    }

    [Fact]
    public async Task UngrantedTool_IsRefusedWithToolNotGranted()
    {
        _tenant.AddAgent("Builder", AgentClient, High, ("query_entities", Low));

        var decision = await CheckAsync(Client(AgentClient, Tenant, "TenantManagement"), "update_agent",
            McpRiskLevel.Medium);

        decision.Allowed.Should().BeFalse();
        decision.Code.Should().Be(AgentMandateGuard.ToolNotGranted);
        decision.Message.Should().Contain("update_agent").And.Contain("Builder");
    }

    [Fact]
    public async Task ToolAboveTheGrantsMaxRisk_IsRefusedWithRiskAboveCeiling()
    {
        _tenant.AddAgent("Builder", AgentClient, High, ("deploy_pipeline", Medium));

        var decision = await CheckAsync(Client(AgentClient), "deploy_pipeline", McpRiskLevel.High);

        decision.Code.Should().Be(AgentMandateGuard.RiskAboveCeiling);
        decision.Message.Should().Contain("High").And.Contain("grant admits Medium");
    }

    [Fact]
    public async Task ToolAboveTheMandatesRiskCeiling_IsRefusedWithRiskAboveCeiling()
    {
        // A grant above the ceiling cannot be written by the agent tools, but a seed or an older record may hold it.
        _tenant.AddAgent("Builder", AgentClient, Medium, ("deploy_pipeline", High));

        var decision = await CheckAsync(Client(AgentClient), "deploy_pipeline", McpRiskLevel.High);

        decision.Code.Should().Be(AgentMandateGuard.RiskAboveCeiling);
        decision.Message.Should().Contain("risk ceiling Medium");
    }

    [Fact]
    public async Task GrantWithoutMaxRisk_AdmitsLowOnly()
    {
        // Odin 2026-11-24: the engine stores no defaults inside records; an absent MaxRisk reads as Low.
        _tenant.AddAgent("Builder", AgentClient, High, ("query_entities", null), ("deploy_pipeline", null));

        (await CheckAsync(Client(AgentClient), "query_entities")).Allowed.Should().BeTrue();
        (await CheckAsync(Client(AgentClient), "deploy_pipeline", McpRiskLevel.Medium)).Code.Should()
            .Be(AgentMandateGuard.RiskAboveCeiling);
    }

    [Fact]
    public async Task PrefixGrant_GrantsMatchingTools_TheHighestMatchingGrantDecides()
    {
        _tenant.AddAgent("Builder", AgentClient, Medium, ("knowledge_*", Low), ("knowledge_update", Medium));

        (await CheckAsync(Client(AgentClient), "knowledge_search")).Allowed.Should().BeTrue();
        (await CheckAsync(Client(AgentClient), "knowledge_update", McpRiskLevel.Medium)).Allowed.Should().BeTrue();
        (await CheckAsync(Client(AgentClient), "knowledge_link", McpRiskLevel.Medium)).Code.Should()
            .Be(AgentMandateGuard.RiskAboveCeiling);
        (await CheckAsync(Client(AgentClient), "query_entities")).Code.Should().Be(AgentMandateGuard.ToolNotGranted);
    }

    [Theory]
    [InlineData("*")] // a grant of every tool is never valid
    [InlineData("knowledge*search")] // '*' only at the end
    [InlineData("**")]
    [InlineData("")]
    [InlineData("Query_Entities")] // names are exact (ordinal)
    [InlineData("query_entities ")]
    public async Task InvalidOrNonMatchingPattern_GrantsNothing(string pattern)
    {
        _tenant.AddAgent("Builder", AgentClient, High, (pattern, High));

        (await CheckAsync(Client(AgentClient), "query_entities")).Code.Should().Be(AgentMandateGuard.ToolNotGranted);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(-1)]
    public async Task GrantWithAnUnknownMaxRisk_GrantsNothing(int maxRisk)
    {
        _tenant.AddAgent("Builder", AgentClient, High, ("query_entities", maxRisk));

        (await CheckAsync(Client(AgentClient), "query_entities")).Code.Should().Be(AgentMandateGuard.ToolNotGranted);
    }

    // ---- kill switch ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task DisabledAgent_IsRefusedWithAgentDisabled_EvenForAGrantedTool()
    {
        var (agent, _) = _tenant.AddAgent("Builder", AgentClient, Low, ("query_entities", Low));
        _tenant.SetEnabled(agent, false);

        var decision = await CheckAsync(Client(AgentClient), "query_entities");

        decision.Code.Should().Be(AgentMandateGuard.AgentDisabled);
        decision.Message.Should().Contain("Builder");
    }

    [Fact]
    public async Task AgentWithoutAStoredEnabledValue_IsTreatedAsDisabled()
    {
        var agent = _tenant.AddAgentOnly("Builder", AgentClient, enabled: null);
        _tenant.Govern(agent, _tenant.AddMandate(Low, ("query_entities", Low)));

        (await CheckAsync(Client(AgentClient), "query_entities")).Code.Should().Be(AgentMandateGuard.AgentDisabled);
    }

    [Fact]
    public async Task NoCache_ADisableAndAGrantChangeTakeEffectOnTheNextCall()
    {
        var (agent, mandate) = _tenant.AddAgent("Builder", AgentClient, Low, ("query_entities", Low));
        (await CheckAsync(Client(AgentClient), "query_entities")).Allowed.Should().BeTrue();

        _tenant.SetGrants(mandate, ("get_type_schema", Low));
        (await CheckAsync(Client(AgentClient), "query_entities")).Code.Should().Be(AgentMandateGuard.ToolNotGranted);

        _tenant.SetEnabled(agent, false);
        (await CheckAsync(Client(AgentClient), "get_type_schema")).Code.Should().Be(AgentMandateGuard.AgentDisabled);
    }

    // ---- fail closed -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task DeletedMandate_IsRefusedWithMandateUnavailable()
    {
        var (_, mandate) = _tenant.AddAgent("Builder", AgentClient, Low, ("query_entities", Low));
        _tenant.Remove(mandate);

        (await CheckAsync(Client(AgentClient), "query_entities")).Code.Should().Be(AgentMandateGuard.MandateUnavailable);
    }

    [Fact]
    public async Task AgentWithoutMandate_IsRefusedWithMandateUnavailable()
    {
        _tenant.AddAgentOnly("Builder", AgentClient);

        (await CheckAsync(Client(AgentClient), "query_entities")).Code.Should().Be(AgentMandateGuard.MandateUnavailable);
    }

    [Fact]
    public async Task AgentGovernedByTwoMandates_IsRefusedWithMandateUnavailable()
    {
        var (agent, _) = _tenant.AddAgent("Builder", AgentClient, Low, ("query_entities", Low));
        _tenant.Govern(agent, _tenant.AddMandate(High, ("delete_entity", High)));

        (await CheckAsync(Client(AgentClient), "query_entities")).Code.Should().Be(AgentMandateGuard.MandateUnavailable);
    }

    [Fact]
    public async Task TwoAgentsWithTheSameClientId_AreRefusedWithMandateUnavailable()
    {
        _tenant.AddAgent("Builder", AgentClient, Low, ("query_entities", Low));
        _tenant.AddAgent("Builder copy", AgentClient, High, ("delete_entity", High));

        var decision = await CheckAsync(Client(AgentClient), "query_entities");

        decision.Code.Should().Be(AgentMandateGuard.MandateUnavailable);
        decision.Message.Should().Contain("2 agents");
    }

    [Fact]
    public async Task StoreUnavailable_IsRefusedWithMandateUnavailable()
    {
        _tenant.AddAgent("Builder", AgentClient, Low, ("query_entities", Low));
        _tenant.ReadFails = true;

        (await CheckAsync(Client(AgentClient), "query_entities")).Code.Should().Be(AgentMandateGuard.MandateUnavailable);
    }

    [Fact]
    public async Task TenantRepositoryLookupFails_IsRefusedWithMandateUnavailable()
    {
        var decision = await AgentMandateGuard.CheckAsync(Client(AgentClient), "query_entities", McpRiskLevel.Low,
            _ => Task.FromException<ITenantRepository>(new InvalidOperationException("tenant not found")),
            () => _tenant.CkCache.Object);

        decision.Code.Should().Be(AgentMandateGuard.MandateUnavailable);
    }

    [Fact]
    public async Task ClientTokenWithoutTenantClaim_IsRefusedWithMandateUnavailable()
    {
        var decision = await CheckAsync(Client(AgentClient, tenantId: null), "query_entities");

        decision.Code.Should().Be(AgentMandateGuard.MandateUnavailable);
        _tenantsLookedUp.Should().BeEmpty();
    }

    [Fact]
    public async Task UnknownRiskCeiling_IsRefusedWithMandateUnavailable()
    {
        _tenant.AddAgent("Builder", AgentClient, 7, ("query_entities", Low));

        (await CheckAsync(Client(AgentClient), "query_entities")).Code.Should().Be(AgentMandateGuard.MandateUnavailable);
    }

    [Fact]
    public async Task AgentListAtTheReadLimit_UnknownClient_IsRefusedWithMandateUnavailable()
    {
        for (var i = 0; i < AgentModel.MaxAgents; i++)
        {
            _tenant.AddAgentOnly($"Agent {i}", $"agent-{i}");
        }

        (await CheckAsync(Client("agent-not-in-the-first-page"), "query_entities")).Code.Should()
            .Be(AgentMandateGuard.MandateUnavailable);
    }

    // ---- tenant boundary ------------------------------------------------------------------------------------------

    [Fact]
    public async Task OnlyTheTokensTenantIsSearched_AnAgentOfAnotherTenantWithTheSameClientIdIsNeverUsed()
    {
        // Tenant B has an agent "agent-builder" with a wide mandate; tenant A has one with a narrow mandate.
        _otherTenant.AddAgent("Wide", AgentClient, High, ("delete_entity", High));
        _tenant.AddAgent("Narrow", AgentClient, Low, ("query_entities", Low));

        var decision = await CheckAsync(Client(AgentClient, Tenant), "delete_entity", McpRiskLevel.High);

        decision.Code.Should().Be(AgentMandateGuard.ToolNotGranted);
        decision.AgentName.Should().Be("Narrow");
        _tenantsLookedUp.Should().Equal(Tenant);
        _otherTenant.OpenedSessions.Should().BeEmpty();
    }

    [Fact]
    public async Task ClientIdOfAnAgentOfAnotherTenant_IsNoAgentInTheTokensTenant()
    {
        _otherTenant.AddAgent("Wide", AgentClient, High, ("delete_entity", High));

        var decision = await CheckAsync(new McpCallerPrincipal(null, AgentClient, Tenant, []), "query_entities");

        decision.Should().Be(AgentMandateDecision.NotAnAgent,
            "tenant A has no agent with this client id; tenant B's mandate does not apply to tenant A's token");
        _otherTenant.OpenedSessions.Should().BeEmpty();
    }

    [Fact]
    public async Task DelegatedToken_OfAnAgentClient_IsRefused()
    {
        // On-behalf-of: the token carries the user's subject but the agent's client id. It cannot carry octo_agent
        // (a user token with it is refused, AB#9729), so it is refused as an agent token without the agent scope.
        _tenant.AddAgent("Builder", AgentClient, Low, ("query_entities", Low));

        var decision = await CheckAsync(new McpCallerPrincipal("user-1", AgentClient, Tenant, ["TenantManagement"]),
            "query_entities");

        decision.Allowed.Should().BeFalse();
        decision.Code.Should().Be(AgentMandateGuard.AgentScopeMissing);
    }

    [Fact]
    public async Task AgentClientTokenWithoutTheAgentScope_IsRefused_EvenForAGrantedTool()
    {
        // AB#9729: an agent client created before option A (or seeded with octo_api) must not run any tool, because its
        // token would reach the platform unexchanged and without act.
        _tenant.AddAgent("Builder", AgentClient, Low, ("query_entities", Low));

        var decision = await CheckAsync(new McpCallerPrincipal(null, AgentClient, Tenant, [], ["octo_api"]),
            "query_entities");

        decision.Code.Should().Be(AgentMandateGuard.AgentScopeMissing);
        decision.AgentName.Should().Be("Builder");
    }

    [Theory]
    [InlineData("knowledge_*", "knowledge_search", true)]
    [InlineData("knowledge_*", "knowledge_", true)]
    [InlineData("knowledge_*", "knowledgesearch", false)]
    [InlineData("query_entities", "query_entities", true)]
    [InlineData("*", "query_entities", false)]
    [InlineData(null, "query_entities", false)]
    public void Matches_FollowsTheGrantPatternRules(string? pattern, string tool, bool expected) =>
        AgentMandateGuard.Matches(pattern, tool).Should().Be(expected);

    // ---- AB#9729 option A: the agent scope octo_agent ------------------------------------------------------------

    [Fact]
    public async Task AgentScopeOnAUserToken_IsRefusedWithNotAnAgent()
    {
        var decision = await CheckAsync(new McpCallerPrincipal("user-1", "octo-cli", Tenant, [], ["octo_agent"]),
            "query_entities");

        decision.Allowed.Should().BeFalse();
        decision.Code.Should().Be(AgentMandateGuard.NotAnAgent);
    }

    [Fact]
    public async Task AgentScopeOnAClientThatIsNoAgent_IsRefusedWithNotAnAgent()
    {
        _tenant.AddAgent("Builder", AgentClient, Low, ("query_entities", Low));

        var decision = await CheckAsync(new McpCallerPrincipal(null, "not-an-agent", Tenant, [], ["octo_agent"]),
            "query_entities");

        decision.Allowed.Should().BeFalse("a client with octo_agent that is no agent must not pass as an ordinary caller");
        decision.Code.Should().Be(AgentMandateGuard.NotAnAgent);
    }

    [Fact]
    public async Task AgentScopeInATenantWithoutAgentTypes_IsRefusedWithNotAnAgent()
    {
        _tenant.GivenAgentTypes(false);

        var decision = await CheckAsync(new McpCallerPrincipal(null, AgentClient, Tenant, [], ["octo_agent"]),
            "query_entities");

        decision.Code.Should().Be(AgentMandateGuard.NotAnAgent);
    }

    [Fact]
    public async Task AgentScopeOfAnAgentOfAnotherTenant_IsRefusedWithNotAnAgent()
    {
        _otherTenant.AddAgent("Builder", AgentClient, Low, ("query_entities", Low));

        var decision = await CheckAsync(new McpCallerPrincipal(null, AgentClient, Tenant, [], ["octo_agent"]),
            "query_entities");

        decision.Code.Should().Be(AgentMandateGuard.NotAnAgent);
        _tenantsLookedUp.Should().NotContain(OtherTenant);
    }

    [Fact]
    public async Task AgentScopeOfAnEnabledAgent_IsJudgedByItsMandateAsBefore()
    {
        _tenant.AddAgent("Builder", AgentClient, Low, ("query_entities", Low));
        var caller = new McpCallerPrincipal(null, AgentClient, Tenant, [], ["octo_agent"]);

        (await CheckAsync(caller, "query_entities")).Allowed.Should().BeTrue();
        (await CheckAsync(caller, "delete_entity")).Code.Should().Be(AgentMandateGuard.ToolNotGranted);
    }
}
