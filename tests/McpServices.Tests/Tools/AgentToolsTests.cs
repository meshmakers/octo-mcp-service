using System.Text.Json;
using FluentAssertions;
using Meshmakers.Octo.Backend.McpServices.Models;
using Meshmakers.Octo.Backend.McpServices.Services;
using Meshmakers.Octo.Backend.McpServices.Services.Agents;
using Meshmakers.Octo.Backend.McpServices.Tools;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.ConstructionKit.Contracts.Messages;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Engine.Repositories.Query;
using Moq;
using Xunit;

namespace McpServices.Tests.Tools;

/// <summary>
///     The agent tools against an in-memory repository (entities by type and id, associations, ApplyChanges) and the
///     CK method fake of the identity client.
/// </summary>
public class AgentToolsTests : ToolTestBase
{
    private const string ClientRtId = "65f1c0ffee0000000000c1c1";
    private const string CreateClient = "SystemIdentityClientCreateClient";
    private const string SetRoles = "SystemIdentityClientSetRoles";
    private const string UpdateClient = "SystemIdentityClientUpdateClient";
    private const string DeleteClient = "SystemIdentityClientDeleteClient";

    private readonly Dictionary<OctoObjectId, RtEntity> _entities = new();
    private readonly List<(OctoObjectId Origin, OctoObjectId Target, string Role)> _associations = [];
    private readonly List<string> _writeOrder = [];

    public AgentToolsTests()
    {
        GivenAuthenticated();
        // The writing agent tools need a tenant administrator.
        GivenAuthenticatedCaller(DefaultTestTenantId, DefaultCallerSubjectId, "TenantManagement");
        TestServiceProvider.RegisterService<IToolRiskRegistry>(new ToolRiskRegistry());
        GivenAgentTypes(true);

        MockIdentityClient.Setup(c => c.GetClient(It.IsAny<string>()))
            .ReturnsAsync((string id) => new ClientDto { ClientId = id, RtId = ClientRtId, IsEnabled = true });
        MockIdentityClient.Setup(c => c.GetRoles()).ReturnsAsync(
        [
            new RoleDto { Id = "660000000000000000000002", Name = "KnowledgeEditor" }
        ]);
        _ = CkMethods; // installs the CK method fake (every method succeeds)

        MockTenantRepository
            .Setup(r => r.GetRtEntitiesByTypeAsync(It.IsAny<IOctoSession>(), It.IsAny<RtCkId<CkTypeId>>(),
                It.IsAny<RtEntityQueryOptions>(), It.IsAny<int?>(), It.IsAny<int?>()))
            .ReturnsAsync((IOctoSession _, RtCkId<CkTypeId> type, RtEntityQueryOptions _, int? _, int? _) =>
            {
                var items = _entities.Values.Where(e => Equals(e.CkTypeId, type)).ToList();
                return new ResultSet<RtEntity>(items, items.Count, null, null);
            });
        MockTenantRepository
            .Setup(r => r.GetRtEntityByRtIdAsync(It.IsAny<IOctoSession>(), It.IsAny<RtEntityId>()))
            .ReturnsAsync((IOctoSession _, RtEntityId id) =>
                _entities.TryGetValue(id.RtId, out var e) && Equals(e.CkTypeId, id.CkTypeId) ? e : null);
        MockTenantRepository
            .Setup(r => r.GetRtAssociationsAsync(It.IsAny<IOctoSession>(), It.IsAny<RtEntityId>(),
                It.IsAny<RtAssociationExtendedQueryOptions>()))
            .ReturnsAsync((IOctoSession _, RtEntityId origin, RtAssociationExtendedQueryOptions o) =>
            {
                var inbound = o.Direction == GraphDirections.Inbound;
                var items = _associations
                    .Where(a => (inbound ? a.Target : a.Origin) == origin.RtId && a.Role == o.RoleId!.ToString())
                    .Select(a => new RtAssociation { OriginRtId = a.Origin, TargetRtId = a.Target })
                    .ToList();
                return new ResultSet<RtAssociation>(items, items.Count, null, null);
            });
        MockTenantRepository
            .Setup(r => r.ApplyChangesAsync(It.IsAny<IOctoSession>(), It.IsAny<IReadOnlyList<IEntityUpdateInfo<RtEntity>>>(),
                It.IsAny<IReadOnlyList<AssociationUpdateInfo>>(), It.IsAny<OperationResult>()))
            .Returns((IOctoSession _, IReadOnlyList<IEntityUpdateInfo<RtEntity>> updates,
                IReadOnlyList<AssociationUpdateInfo> associations, OperationResult _) =>
            {
                Apply(updates, associations);
                return Task.CompletedTask;
            });
    }

    private void Apply(IReadOnlyList<IEntityUpdateInfo<RtEntity>> updates, IReadOnlyList<AssociationUpdateInfo> associations)
    {
        _writeOrder.Add("engine");
        foreach (var update in updates)
        {
            if (update.ModOption == EntityModOptions.Delete)
            {
                _entities.Remove(update.RtId!.Value);
                continue;
            }

            var entity = update.RtEntity!;
            if (update.ModOption == EntityModOptions.Insert || !_entities.TryGetValue(entity.RtId, out var stored))
            {
                _entities[entity.RtId] = entity;
                continue;
            }

            foreach (var attribute in entity.Attributes)
            {
                stored.SetAttributeRawValue(attribute.Key, attribute.Value);
            }
        }

        foreach (var a in associations)
        {
            if (a.ModOption == AssociationModOptionsDto.Delete)
            {
                _associations.RemoveAll(x => x.Origin == a.Origin.RtId && x.Target == a.Target.RtId &&
                                             x.Role == a.RoleId.ToString());
            }
            else
            {
                _associations.Add((a.Origin.RtId, a.Target.RtId, a.RoleId.ToString()));
            }
        }
    }

    private void GivenAgentTypes(bool present)
    {
        CkTypeGraph? type = present ? CkGraphTestBuilder.BuildType("System.Ai-4.12.0/Agent-1") : null;
        MockCkCacheService.Setup(c => c.TryGetRtCkType(It.IsAny<string>(), It.IsAny<RtCkId<CkTypeId>>(), out type))
            .Returns(present);
    }

    /// <summary>The engine refuses every write, as it does for a caller without write rights on the agent types.</summary>
    private void GivenEngineRefusesWrites() =>
        MockTenantRepository
            .Setup(r => r.ApplyChangesAsync(It.IsAny<IOctoSession>(), It.IsAny<IReadOnlyList<IEntityUpdateInfo<RtEntity>>>(),
                It.IsAny<IReadOnlyList<AssociationUpdateInfo>>(), It.IsAny<OperationResult>()))
            .Callback((IOctoSession _, IReadOnlyList<IEntityUpdateInfo<RtEntity>> _,
                IReadOnlyList<AssociationUpdateInfo> _, OperationResult result) =>
                result.AddMessage(new OperationMessage(MessageLevel.Error, null, 144,
                    "Access denied: the caller may not write System.Ai/Agent.")))
            .Returns(Task.CompletedTask);

    private IEnumerable<RtEntity> Of(string type) => _entities.Values.Where(e => e.CkTypeId!.ToString() == type);

    private Task<AgentResponse> CreateAsync(string name = "Knowledge curator", List<AgentToolGrantDto>? grants = null,
        string? riskCeiling = null, List<string>? roles = null, string? clientSecret = null, string? mandateRtId = null) =>
        AgentTools.CreateAgent(MockServer.Object, name, "Keep the knowledge pages consistent.", toolGrants: grants,
            riskCeiling: riskCeiling, roles: roles, clientSecret: clientSecret, mandateRtId: mandateRtId);

    // ---- create_agent -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Create_WritesAgentAndNarrowDefaultMandate_AndAClientWithoutRoles()
    {
        var result = await CreateAsync();

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        var agent = Of(AgentModel.AgentType).Should().ContainSingle().Subject;
        agent.GetAttributeValueOrDefault("ServiceAccountClientId").Should().Be("agent-knowledge-curator");
        agent.GetAttributeValueOrDefault("SponsorUserId").Should().Be(DefaultCallerSubjectId);
        agent.GetAttributeValueOrDefault("Version").Should().Be(1);
        var mandate = Of(AgentModel.MandateType).Should().ContainSingle().Subject;
        mandate.GetAttributeValueOrDefault("RiskCeiling").Should().Be(0);
        mandate.GetAttributeValueOrDefault("ChangeBudgetPerRun").Should().Be(0);
        mandate.GetAttributeValueOrDefault("WriteMode").Should().Be(0);
        _associations.Should().ContainSingle(a => a.Origin == agent.RtId && a.Target == mandate.RtId &&
                                                  a.Role == AgentModel.GovernedByRole);

        var input = CkMethods.InputOf(CreateClient);
        CkMethodFake.Str(input, "clientId").Should().Be("agent-knowledge-curator");
        CkMethodFake.Strings(input.GetProperty("allowedGrantTypes")).Should().Equal("client_credentials");
        CkMethodFake.Strings(input.GetProperty("allowedScopes")).Should().Equal(["octo_agent"],
            "AB#9729: an agent client gets the agent scope only, never octo_api, so its token opens no platform API");
        CkMethodFake.Str(input, "clientSecret").Should().NotBeNullOrEmpty("a random secret is generated");
        CkMethods.OperationNames.Should().NotContain(SetRoles, "no roles by default");
        result.AssignedRoles.Should().BeEmpty();
        result.Agent!.Mandate!.RiskCeiling.Should().Be("Low");
        result.Agent.Mandate.WriteMode.Should().Be("Propose");
        result.Agent.ServiceAccountClientEnabled.Should().BeTrue();
    }

    [Fact]
    public async Task Create_GrantNamingAnUnknownTool_FailsNamingTheTool_AndChangesNothing()
    {
        var result = await CreateAsync(grants: [new AgentToolGrantDto { ToolPattern = "knowledge_search" },
            new AgentToolGrantDto { ToolPattern = "drop_all_tenants" }]);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("UnknownTool");
        result.ErrorMessage.Should().Contain("drop_all_tenants");
        _entities.Should().BeEmpty();
        CkMethods.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Create_PrefixGrant_MustMatchATool()
    {
        var ok = await CreateAsync(grants: [new AgentToolGrantDto { ToolPattern = "knowledge_*" }]);
        ok.IsSuccess.Should().BeTrue(ok.ErrorMessage);

        var bad = await CreateAsync("Other", [new AgentToolGrantDto { ToolPattern = "nothing_like_this_*" }]);
        bad.ErrorCode.Should().Be("UnknownTool");
        bad.ErrorMessage.Should().Contain("nothing_like_this_*");

        var all = await CreateAsync("Third", [new AgentToolGrantDto { ToolPattern = "*" }]);
        all.ErrorCode.Should().Be("InvalidArgument");
    }

    [Fact]
    public async Task Create_GrantAboveTheRiskCeiling_IsRefused()
    {
        var result = await CreateAsync(grants: [new AgentToolGrantDto { ToolPattern = "delete_client", MaxRisk = "High" }],
            riskCeiling: "Medium");

        result.ErrorCode.Should().Be("RiskAboveCeiling");
        result.ErrorMessage.Should().Contain("delete_client");
        _entities.Should().BeEmpty();
    }

    [Fact]
    public async Task Create_InvalidRiskCeilingOrConstraints_AreRefused()
    {
        (await CreateAsync(riskCeiling: "Extreme")).ErrorCode.Should().Be("InvalidArgument");
        (await CreateAsync(grants: [new AgentToolGrantDto { ToolPattern = "knowledge_get", ArgumentConstraints = "[1]" }]))
            .ErrorCode.Should().Be("InvalidArgument");
        _entities.Should().BeEmpty();
    }

    [Fact]
    public async Task Create_GrantWithoutMaxRisk_IsStoredAsLowExplicitly()
    {
        await CreateAsync(grants: [new AgentToolGrantDto { ToolPattern = "knowledge_get" }]);

        var mandate = Of(AgentModel.MandateType).Single();
        var record = ((IEnumerable<RtRecord>)mandate.GetAttributeValueOrDefault("ToolGrants")!).Single();
        record.Attributes["ToolPattern"].Should().Be("knowledge_get");
        record.Attributes["MaxRisk"].Should().Be(0, "the engine materialises no record defaults");
    }

    [Fact]
    public async Task Create_WithRoles_AssignsExactlyThoseRoles()
    {
        var result = await CreateAsync(roles: ["660000000000000000000002"]);

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        CkMethodFake.Strings(CkMethods.InputOf(SetRoles).GetProperty("roleNames")).Should().Equal("KnowledgeEditor");
        result.AssignedRoles.Should().Equal("KnowledgeEditor");
    }

    [Fact]
    public async Task Create_CallerWithoutWriteRights_IsRefused_AndTheIdentityIsNotTouched()
    {
        GivenEngineRefusesWrites();

        var result = await CreateAsync();

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("WriteRefused");
        result.ErrorMessage.Should().Contain("Access denied");
        CkMethods.Requests.Should().BeEmpty();
        MockSession.Verify(s => s.CommitTransactionAsync(), Times.Never);
    }

    [Theory]
    [InlineData("agent-knowledge-curator")] // the agent's own service account, no roles
    [InlineData("some-integration")] // any other client with octo_api but without an admin role
    public async Task WritingTools_RoleLessClient_IsRefusedBeforeAnyWrite(string clientId)
    {
        await CreateAsync();
        CkMethods.Requests.Clear();
        var before = Of(AgentModel.MandateType).Single().GetAttributeValueOrDefault("ChangeBudgetPerRun");
        GivenServicePrincipalCaller(clientId);

        var create = await CreateAsync("Rogue");
        var update = await AgentTools.UpdateAgent(MockServer.Object, "Knowledge curator", changeBudgetPerRun: 7);
        var disable = await AgentTools.SetAgentEnabled(MockServer.Object, "Knowledge curator", false);

        foreach (var result in new[] { create, update, disable })
        {
            result.IsSuccess.Should().BeFalse();
            result.ErrorCode.Should().Be("NotTenantAdministrator");
            result.ErrorMessage.Should().Contain("TenantManagement");
        }

        Of(AgentModel.AgentType).Should().ContainSingle();
        Of(AgentModel.MandateType).Single().GetAttributeValueOrDefault("ChangeBudgetPerRun").Should().Be(before);
        Of(AgentModel.AgentType).Single().GetAttributeValueOrDefault("Enabled").Should().Be(true);
        CkMethods.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task WritingTools_UserWithoutAdminRole_IsRefused_ReadsStillWork()
    {
        await CreateAsync();
        GivenAuthenticatedCaller(DefaultTestTenantId, DefaultCallerSubjectId, "KnowledgeEditor");

        (await AgentTools.UpdateAgent(MockServer.Object, "Knowledge curator", instructions: "x"))
            .ErrorCode.Should().Be("NotTenantAdministrator");
        (await AgentTools.ListAgents(MockServer.Object)).TotalCount.Should().Be(1);
        (await AgentTools.GetAgent(MockServer.Object, "Knowledge curator")).IsSuccess.Should().BeTrue();
    }

    [Theory]
    [InlineData(null)] // client-credentials token of the agent's own client
    [InlineData("user-1")] // on-behalf-of token: a person's subject, the agent's client id
    public async Task WritingTools_TheAgentsOwnClient_IsRefusedEvenAsAdministrator(string? subject)
    {
        await CreateAsync();
        CkMethods.Requests.Clear();
        var writesBefore = _writeOrder.Count;
        GivenRequestPrincipal(BuildPrincipal(DefaultTestTenantId, subject, "agent-knowledge-curator", "TenantManagement"));

        var update = await AgentTools.UpdateAgent(MockServer.Object, "Knowledge curator", changeBudgetPerRun: 7);
        var disable = await AgentTools.SetAgentEnabled(MockServer.Object, "Knowledge curator", false);
        var create = await CreateAsync("Second", grants: [new AgentToolGrantDto { ToolPattern = "query_entities" }]);

        foreach (var result in new[] { update, disable, create })
        {
            result.ErrorCode.Should().Be("AgentCaller");
            result.ErrorMessage.Should().Contain("agent-knowledge-curator").And.Contain("agents never manage agents");
        }

        _writeOrder.Count.Should().Be(writesBefore, "nothing is written");
        CkMethods.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task WritingTools_AnotherAgentsClient_IsRefused_EvenAsAdministrator()
    {
        // An agent with admin roles could otherwise raise a sibling's (or a shared) mandate or create a wider agent.
        await CreateAsync();
        await CreateAsync("Other agent");
        CkMethods.Requests.Clear();
        GivenServicePrincipalCaller("agent-other-agent", DefaultTestTenantId, "TenantManagement", "UserManagement");

        (await AgentTools.UpdateAgent(MockServer.Object, "Knowledge curator", changeBudgetPerRun: 7))
            .ErrorCode.Should().Be("AgentCaller");
        (await CreateAsync("Wider")).ErrorCode.Should().Be("AgentCaller");
        Of(AgentModel.AgentType).Should().HaveCount(2);
        CkMethods.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task WritingTools_AgentLookupFails_AreRefusedFailClosed()
    {
        await CreateAsync();
        GivenServicePrincipalCaller("some-admin-client", DefaultTestTenantId, "TenantManagement");
        MockTenantRepository
            .Setup(r => r.GetRtEntitiesByTypeAsync(It.IsAny<IOctoSession>(), It.IsAny<RtCkId<CkTypeId>>(),
                It.IsAny<RtEntityQueryOptions>(), It.IsAny<int?>(), It.IsAny<int?>()))
            .ThrowsAsync(new TimeoutException("store unavailable"));

        var result = await AgentTools.UpdateAgent(MockServer.Object, "Knowledge curator", changeBudgetPerRun: 7);

        result.ErrorCode.Should().Be("AgentCaller");
        result.ErrorMessage.Should().Contain("fail closed");
    }

    [Fact]
    public void WriteChannel_IsTheChannelOfTheSystemAiWriteAccessBlock()
    {
        // System.Ai 4.12.0 (octo-ai-services, types/agent.yaml and mandate.yaml) declares
        // writeAccess { channels: [System.Ai/AgentAdministration] }; AgentWriteAccessTests there pins the same literal
        // against the compiled model in a real tenant and proves that every caller without it is refused (message 144).
        AgentModel.WriteChannel.Should().Be("System.Ai/AgentAdministration");
        CkWritePrincipals.IsValidChannel(AgentModel.WriteChannel).Should().BeTrue();
    }

    [Fact]
    public async Task WritingTools_WriteOnTheAgentAdministrationChannel_ForTheCallerItself_ReadsDoNot()
    {
        await CreateAsync();
        await AgentTools.UpdateAgent(MockServer.Object, "Knowledge curator", changeBudgetPerRun: 2);
        await AgentTools.SetAgentEnabled(MockServer.Object, "Knowledge curator", false);
        var writeContexts = TakeOpenedSessionContexts();

        writeContexts.Where(c => !c.IsSystem).Should().NotBeEmpty().And.OnlyContain(c =>
            c.WriteChannel == AgentModel.WriteChannel && c.SubjectId == DefaultCallerSubjectId,
            "the caller's own session carries the channel; identity and roles stay the caller's");
        writeContexts.Where(c => c.IsSystem).Should().OnlyContain(c => c.WriteChannel == null,
            "the system session only reads the agents for the agent-caller refusal");

        await AgentTools.ListAgents(MockServer.Object);
        await AgentTools.GetAgent(MockServer.Object, "Knowledge curator");
        TakeOpenedSessionContexts().Should().NotBeEmpty().And.OnlyContain(c => c.WriteChannel == null && !c.IsSystem,
            "the read tools get no write privilege");
    }

    [Fact]
    public async Task GenericEntityTools_OnAMandate_OpenTheirSessionWithoutTheChannel()
    {
        // The engine refuses a write of System.Ai/Mandate without the channel (message 144, AgentWriteAccessTests in
        // octo-ai-services); the generic tools never get it, whoever calls them.
        await CreateAsync();
        TakeOpenedSessionContexts();
        GivenServicePrincipalCaller("agent-knowledge-curator", DefaultTestTenantId, "TenantManagement");
        GivenEngineRefusesWrites();
        var mandate = Of(AgentModel.MandateType).Single();

        await RuntimeEntityCrudTools.UpdateEntity(MockServer.Object, mandate.RtId.ToString(), AgentModel.MandateType,
            [new AttributeUpdateItem { AttributePath = "changeBudgetPerRun", Value = 7 }]);

        TakeOpenedSessionContexts().Should().OnlyContain(c => c.WriteChannel == null);
        Of(AgentModel.MandateType).Single().GetAttributeValueOrDefault("ChangeBudgetPerRun").Should().Be(0);
    }

    /// <summary>The security contexts of every session the tools opened since the last call.</summary>
    private List<RtSecurityContext> TakeOpenedSessionContexts()
    {
        var contexts = MockSecureSessionFactory.Invocations
            .Where(i => i.Method.Name == nameof(ISecureSessionFactory.GetSessionAsync))
            .Select(i => (RtSecurityContext)i.Arguments[0]!).ToList();
        MockSecureSessionFactory.Invocations.Clear();
        return contexts;
    }

    [Fact]
    public async Task Create_IdentityRefusesTheClient_RemovesTheEntities_AndNeverEchoesTheSecret()
    {
        CkMethods.Fails("METHOD_FORBIDDEN", "refused for secret s3cret-value");

        var result = await CreateAsync(clientSecret: "s3cret-value");

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("IdentityFailed");
        _entities.Should().BeEmpty("the agent and its new mandate are removed again");
        _associations.Should().BeEmpty();
        JsonSerializer.Serialize(result).Should().NotContain("s3cret-value");
    }

    [Fact]
    public async Task Create_RolesCallThrows_DeletesTheCreatedClient_AndRemovesTheEntities()
    {
        _ = CkMethods;
        MockTenantClient
            .Setup(c => c.SendMutationAsync<It.IsAnyType>(It.Is<GraphQL.GraphQLRequest>(r => r.OperationName == SetRoles)))
            .Callback(new InvocationAction(i => CkMethods.Requests.Add((GraphQL.GraphQLRequest)i.Arguments[0]!)))
            .Throws(new InvalidOperationException("roles refused"));

        var result = await CreateAsync(roles: ["660000000000000000000002"], clientSecret: "s3cret-value");

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("Failed", "a transport error of the roles call is no declared identity error");
        CkMethods.OperationNames.Should().ContainInOrder(CreateClient, SetRoles, DeleteClient);
        _entities.Should().BeEmpty("no orphaned agent or mandate");
        _associations.Should().BeEmpty();
        JsonSerializer.Serialize(result).Should().NotContain("s3cret-value");
    }

    [Fact]
    public async Task Create_ThrowsAfterTheWrites_DeletesTheClient_AndRemovesTheEntities()
    {
        // The read-back after a successful create throws: before the fix the agent, its mandate and its client stayed.
        MockTenantRepository
            .Setup(r => r.GetRtEntityByRtIdAsync(It.IsAny<IOctoSession>(), It.IsAny<RtEntityId>()))
            .ReturnsAsync((IOctoSession _, RtEntityId id) =>
            {
                if (Equals(id.CkTypeId, AgentModel.AgentTypeId))
                {
                    throw new TimeoutException("store unavailable");
                }

                return _entities.TryGetValue(id.RtId, out var e) && Equals(e.CkTypeId, id.CkTypeId) ? e : null;
            });

        var result = await CreateAsync(clientSecret: "s3cret-value");

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("Failed");
        result.ErrorMessage.Should().Contain("Nothing was kept");
        CkMethods.OperationNames.Should().ContainInOrder(CreateClient, DeleteClient);
        _entities.Should().BeEmpty("no orphaned agent or mandate");
        _associations.Should().BeEmpty();
        JsonSerializer.Serialize(result).Should().NotContain("s3cret-value");
    }

    [Fact]
    public async Task Create_Disabled_WritesADisabledAgent_AndADisabledClient()
    {
        var result = await AgentTools.CreateAgent(MockServer.Object, "Knowledge curator",
            "Keep the knowledge pages consistent.", enabled: false);

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        Of(AgentModel.AgentType).Single().GetAttributeValueOrDefault("Enabled").Should().Be(false);
        var input = CkMethods.InputOf(CreateClient);
        (CkMethodFake.Bool(input, "isEnabled") ?? CkMethodFake.Bool(input, "enabled")).Should()
            .BeFalse("a disabled agent must not be able to obtain a token");
        result.Agent!.Enabled.Should().BeFalse();
        result.Agent.ServiceAccountClientEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task Create_SecretIsNeverInTheResult()
    {
        var result = await CreateAsync(clientSecret: "s3cret-value");

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        JsonSerializer.Serialize(result).Should().NotContain("s3cret-value");
        _entities.Values.SelectMany(e => e.Attributes.Values).Select(v => v?.ToString())
            .Should().NotContain("s3cret-value", "the agent entity holds no credential");
    }

    [Fact]
    public async Task Create_DuplicateName_IsRefused()
    {
        await CreateAsync();
        var second = await CreateAsync("knowledge CURATOR");

        second.ErrorCode.Should().Be("AgentExists");
        Of(AgentModel.AgentType).Should().ContainSingle();
    }

    [Fact]
    public async Task Create_WithExistingMandate_LinksIt_AndRefusesMandateValuesBesideIt()
    {
        var first = await CreateAsync();
        var mandateRtId = first.Agent!.Mandate!.RtId;

        var second = await CreateAsync("Second", mandateRtId: mandateRtId);
        second.IsSuccess.Should().BeTrue(second.ErrorMessage);
        Of(AgentModel.MandateType).Should().ContainSingle();
        second.Agent!.Mandate!.GovernedAgentCount.Should().Be(2);

        var both = await AgentTools.CreateAgent(MockServer.Object, "Third", "x", mandateRtId: mandateRtId,
            riskCeiling: "Low");
        both.ErrorCode.Should().Be("InvalidArgument");
    }

    [Fact]
    public async Task Create_MissingArguments_AreRefusedWithoutCalls()
    {
        var result = await AgentTools.CreateAgent(MockServer.Object, "", "");

        result.ErrorCode.Should().Be("InvalidArgument");
        CkMethods.Requests.Should().BeEmpty();
        MockTenantRepository.Verify(r => r.ApplyChangesAsync(It.IsAny<IOctoSession>(),
            It.IsAny<IReadOnlyList<IEntityUpdateInfo<RtEntity>>>(), It.IsAny<IReadOnlyList<AssociationUpdateInfo>>(),
            It.IsAny<OperationResult>()), Times.Never);
    }

    [Fact]
    public async Task Create_Unauthenticated_IsRefused()
    {
        GivenUnauthenticatedCaller();

        var result = await CreateAsync();

        result.IsSuccess.Should().BeFalse();
        _entities.Should().BeEmpty();
        CkMethods.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Create_TenantWithoutAgentTypes_NamesTheNeededVersion()
    {
        GivenAgentTypes(false);

        var result = await CreateAsync();

        result.ErrorCode.Should().Be("NoAgentTypes");
        result.ErrorMessage.Should().Contain("System.Ai 4.12.0");
    }

    // ---- update_agent -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Update_InstructionsAndGrants_RaiseTheVersion_AndReplaceTheGrants()
    {
        await CreateAsync(grants: [new AgentToolGrantDto { ToolPattern = "knowledge_get" }]);

        var result = await AgentTools.UpdateAgent(MockServer.Object, "Knowledge curator",
            instructions: "New persona.", riskCeiling: "Medium",
            toolGrants: [new AgentToolGrantDto { ToolPattern = "knowledge_upsert_page", MaxRisk = "Medium" }]);

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        result.Agent!.Version.Should().Be(2);
        result.Agent.Instructions.Should().Be("New persona.");
        result.Agent.Mandate!.ToolGrants.Should().ContainSingle(g => g.ToolPattern == "knowledge_upsert_page" &&
                                                                      g.MaxRisk == "Medium");
        result.Agent.Mandate.RiskCeiling.Should().Be("Medium");
    }

    [Fact]
    public async Task Update_UnknownTool_IsRefused_AndNothingChanges()
    {
        await CreateAsync();

        var result = await AgentTools.UpdateAgent(MockServer.Object, "Knowledge curator",
            toolGrants: [new AgentToolGrantDto { ToolPattern = "no_such_tool" }]);

        result.ErrorCode.Should().Be("UnknownTool");
        result.ErrorMessage.Should().Contain("no_such_tool");
        Of(AgentModel.AgentType).Single().GetAttributeValueOrDefault("Version").Should().Be(1);
    }

    [Fact]
    public async Task Update_LoweringTheCeiling_BelowAKeptGrant_IsRefused()
    {
        await CreateAsync(grants: [new AgentToolGrantDto { ToolPattern = "knowledge_get", MaxRisk = "Medium" }],
            riskCeiling: "Medium");

        var result = await AgentTools.UpdateAgent(MockServer.Object, "Knowledge curator", riskCeiling: "Low");

        result.ErrorCode.Should().Be("RiskAboveCeiling");
    }

    [Fact]
    public async Task Update_SharedMandate_IsNotChangedHere()
    {
        var first = await CreateAsync();
        await CreateAsync("Second", mandateRtId: first.Agent!.Mandate!.RtId);

        var result = await AgentTools.UpdateAgent(MockServer.Object, "Second", changeBudgetPerRun: 5);

        result.ErrorCode.Should().Be("SharedMandate");
    }

    [Fact]
    public async Task Update_Roles_ReplacesTheClientRoles()
    {
        await CreateAsync();

        var result = await AgentTools.UpdateAgent(MockServer.Object, "Knowledge curator", roles: ["KnowledgeEditor"]);

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        CkMethodFake.Strings(CkMethods.InputOf(SetRoles).GetProperty("roleNames")).Should().Equal("KnowledgeEditor");
        result.Agent!.Version.Should().Be(1, "roles are no change of instructions, model or mandate");
    }

    [Fact]
    public async Task Update_UnknownAgent_AndCallerWithoutRights_AreRefused()
    {
        (await AgentTools.UpdateAgent(MockServer.Object, "ghost", instructions: "x")).ErrorCode.Should().Be("AgentNotFound");

        await CreateAsync();
        GivenEngineRefusesWrites();
        var refused = await AgentTools.UpdateAgent(MockServer.Object, "Knowledge curator", instructions: "x");
        refused.ErrorCode.Should().Be("WriteRefused");
    }

    // ---- set_agent_enabled ------------------------------------------------------------------------------------------

    [Fact]
    public async Task Disable_SwitchesTheAgentOffFirst_ThenDisablesTheClient()
    {
        await CreateAsync();
        CkMethods.Requests.Clear();
        _writeOrder.Clear();
        MockTenantClient.Invocations.Clear();

        var result = await AgentTools.SetAgentEnabled(MockServer.Object, "Knowledge curator", false);

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        result.Agent!.Enabled.Should().BeFalse();
        result.Agent.ServiceAccountClientEnabled.Should().BeFalse();
        Of(AgentModel.AgentType).Single().GetAttributeValueOrDefault("Enabled").Should().Be(false);
        var input = CkMethods.InputOf(UpdateClient);
        CkMethodFake.Bool(input, "enabled").Should().BeFalse();
        _writeOrder.Should().Equal("engine");
    }

    [Fact]
    public async Task Disable_WhenTheIdentityFails_KeepsTheAgentDisabled_AndSaysSo()
    {
        await CreateAsync();
        CkMethods.Fails("METHOD_FORBIDDEN");

        var result = await AgentTools.SetAgentEnabled(MockServer.Object, "Knowledge curator", false);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("IdentityFailed");
        result.ErrorMessage.Should().Contain("still enabled");
        Of(AgentModel.AgentType).Single().GetAttributeValueOrDefault("Enabled").Should().Be(false);
    }

    [Fact]
    public async Task Enable_SwitchesTheClientOnFirst()
    {
        await CreateAsync();
        await AgentTools.SetAgentEnabled(MockServer.Object, "Knowledge curator", false);
        CkMethods.Fails("METHOD_FORBIDDEN");

        var result = await AgentTools.SetAgentEnabled(MockServer.Object, "Knowledge curator", true);

        result.ErrorCode.Should().Be("IdentityFailed");
        Of(AgentModel.AgentType).Single().GetAttributeValueOrDefault("Enabled").Should().Be(false,
            "the agent stays off when its client cannot be switched on");
    }

    [Fact]
    public async Task SetEnabled_CallerWithoutWriteRights_IsRefused_AndTheClientIsNotTouched()
    {
        await CreateAsync();
        CkMethods.Requests.Clear();
        GivenEngineRefusesWrites();

        var result = await AgentTools.SetAgentEnabled(MockServer.Object, "Knowledge curator", false);

        result.ErrorCode.Should().Be("WriteRefused");
        CkMethods.Requests.Should().BeEmpty();
    }

    // ---- list_agents / get_agent ------------------------------------------------------------------------------------

    [Fact]
    public async Task ListAndGet_ReturnTheAgentsWithMandate_AndGetReadsTheClientState()
    {
        await CreateAsync(grants: [new AgentToolGrantDto { ToolPattern = "knowledge_get" }]);
        await CreateAsync("Builder");
        MockIdentityClient.Setup(c => c.GetClient("agent-builder"))
            .ReturnsAsync(new ClientDto { ClientId = "agent-builder", RtId = ClientRtId, IsEnabled = false });

        var list = await AgentTools.ListAgents(MockServer.Object);
        list.IsSuccess.Should().BeTrue(list.ErrorMessage);
        list.Agents.Select(a => a.Name).Should().Equal("Builder", "Knowledge curator");
        list.Agents[1].Mandate!.ToolGrants.Should().ContainSingle(g => g.MaxRisk == "Low");

        var get = await AgentTools.GetAgent(MockServer.Object, "builder");
        get.IsSuccess.Should().BeTrue(get.ErrorMessage);
        get.Agent!.ServiceAccountClientEnabled.Should().BeFalse();

        (await AgentTools.GetAgent(MockServer.Object, get.Agent.RtId)).Agent!.Name.Should().Be("Builder");
        (await AgentTools.GetAgent(MockServer.Object, "ghost")).ErrorCode.Should().Be("AgentNotFound");
    }

    [Fact]
    public void AgentModel_ReadsAGrantWithoutMaxRiskAsLow()
    {
        var mandate = new RtEntity { RtId = OctoObjectId.GenerateNewId(), CkTypeId = AgentModel.MandateTypeId };
        mandate.SetAttributeValue("ToolGrants", AttributeValueTypesDto.RecordArray, new List<RtRecord>
        {
            new(new RtCkId<CkRecordId>(AgentModel.ToolGrantRecord),
                new Dictionary<string, object?> { ["ToolPattern"] = "knowledge_get" })
        });

        AgentModel.ReadMandate(mandate).Grants.Should().ContainSingle(g => g.MaxRisk == "Low");
    }

    [Fact]
    public void RiskClassification_WritesAreMedium_ReadsAreLow()
    {
        var registry = new ToolRiskRegistry();

        registry.GetRiskLevel("create_agent").Should().Be(McpRiskLevel.Medium);
        registry.GetRiskLevel("update_agent").Should().Be(McpRiskLevel.Medium);
        registry.GetRiskLevel("set_agent_enabled").Should().Be(McpRiskLevel.Medium);
        registry.GetRiskLevel("list_agents").Should().Be(McpRiskLevel.Low);
        registry.GetRiskLevel("get_agent").Should().Be(McpRiskLevel.Low);
        registry.GetAll().Keys.Should().Contain(["create_agent", "update_agent", "set_agent_enabled", "list_agents", "get_agent"]);
    }

    [Theory]
    [InlineData("Knowledge curator", "agent-knowledge-curator")]
    [InlineData("  Ärger & Co. 2 ", "agent-rger-co-2")]
    [InlineData("!!!", "agent-unnamed")]
    public void DefaultClientId_IsAgentPlusLowerCaseWords(string name, string expected) =>
        AgentModel.DefaultClientId(name).Should().Be(expected);
}
