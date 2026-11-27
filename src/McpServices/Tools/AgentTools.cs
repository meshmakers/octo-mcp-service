using System.ComponentModel;
using System.Security.Cryptography;
using Duende.IdentityModel;
using Meshmakers.Common.Shared;
using Meshmakers.Octo.Communication.Contracts;
using Meshmakers.Octo.Backend.McpServices.MethodClients.DataTransferObjects.System.Identity.v3;
using Meshmakers.Octo.Backend.McpServices.Models;
using Meshmakers.Octo.Backend.McpServices.Services;
using Meshmakers.Octo.Backend.McpServices.Services.Agents;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using ModelContextProtocol.Server;

namespace Meshmakers.Octo.Backend.McpServices.Tools;

/// <summary>
///     Agent management: create, update, enable/disable, list and read the agents of a tenant (System.Ai 4.12.0
///     <c>Agent</c>) together with their mandate (<c>Mandate</c>, tool grants validated against the tools of this
///     server) and their service-account client in the tenant identity. The agent and mandate entities are written
///     directly in the engine with the caller's own session (family 2), so the caller's CK roles decide whether the write
///     is allowed; the client is created and switched through the System.Identity CK methods (family 1), which check the
///     caller's identity roles. The writing tools first require a tenant administrator (TenantManagement or
///     UserManagement) and refuse every agent's service account (<c>AgentCaller</c>); then their engine session carries
///     the write channel <see cref="AgentModel.WriteChannel" />, the only way System.Ai 4.12.0 admits a write of
///     <c>Agent</c> and <c>Mandate</c> (AB#9729: agent administration goes through the agent API, GraphQL and the generic
///     entity tools are refused by the engine). Entities are written first: a caller without the right to write them
///     changes nothing in the identity. A failed identity step removes what the call wrote.
/// </summary>
[McpServerToolType]
public static class AgentTools
{
    private const string NoAgentTypes =
        "Tenant '{0}' has no agent types: it needs System.Ai 4.12.0 or later (blueprint System.Ai.Default, applied on EnableAi).";

    /// <summary>Creates an agent, its mandate and its service-account client.</summary>
    [McpServerTool(Name = "create_agent")]
    [McpRisk(McpRiskLevel.Medium)]
    [Description(
        "Create an agent of the tenant with its mandate and its service-account client (tenant administrators only: " +
        "role TenantManagement or UserManagement). The agent runs as an OAuth " +
        "client (client_credentials, id 'agent-<name>' unless serviceAccountClientId is given) created in the tenant " +
        "identity with exactly the roles passed in 'roles' (none by default). The mandate decides which tools the agent " +
        "may call: either pass mandateRtId to use an existing mandate, or the mandate values (toolGrants, riskCeiling, " +
        "changeBudgetPerRun, writeMode, allowedCkTypes) for a new one. Defaults are deliberately narrow: no grants, " +
        "risk ceiling Low, change budget 0, write mode Propose. Every toolGrants entry must name a tool of this server " +
        "(or a prefix ending in '*' that matches one); an unknown tool is refused with UnknownTool and nothing is " +
        "created. The client secret is never returned: when clientSecret is omitted a random one is generated and " +
        "discarded, and the runtime obtains the agent's credential through its own path.")]
    public static async Task<AgentResponse> CreateAgent(
        McpServer server,
        [Description("Name of the agent, unique in the tenant (e.g. 'Knowledge curator').")] string name,
        [Description("Persona and standing instructions of the agent (system prompt part). Never put a credential here.")]
        string instructions,
        [Description("Model id, e.g. 'claude-sonnet-4-6'. Omitted: the tenant default.")] string? model = null,
        [Description("ProposeOnly (default: every write becomes a proposal) or ActWithinBudget.")] string? autonomyLevel = null,
        [Description("Runner: AgentSdk (default) or ClaudeCodeCli.")] string? runnerKind = null,
        [Description("User id of the accountable person. Omitted: the calling user (none for a client caller).")]
        string? sponsorUserId = null,
        [Description("Whether the agent may start runs (default true).")] bool enabled = true,
        [Description("Runtime id of an existing Mandate to govern the agent. Leave the mandate values empty then.")]
        string? mandateRtId = null,
        [Description("Tool grants of the new mandate: toolPattern (tool name or prefix ending in '*'), maxRisk " +
                     "(Low default, Medium, High; at most riskCeiling), argumentConstraints (optional JSON object).")]
        List<AgentToolGrantDto>? toolGrants = null,
        [Description("Highest risk class any tool call may have: Low (default), Medium, High.")] string? riskCeiling = null,
        [Description("Executed changes one run may make (default 0: no direct change).")] int? changeBudgetPerRun = null,
        [Description("Propose (default: every write becomes a proposal; until the proposal flow exists, writing tools are refused with ProposeRequired) or Direct.")] string? writeMode = null,
        [Description("CK type ids the agent may write, e.g. 'System.Ai/KnowledgePage'. Omitted: the grants decide alone.")]
        List<string>? allowedCkTypes = null,
        [Description("client_id of the service-account client. Omitted: 'agent-' plus the name in lower-case words.")]
        string? serviceAccountClientId = null,
        [Description("Role names or ids for the service-account client (default: none).")] List<string>? roles = null,
        [Description("SENSITIVE: set as the client's secret, never echoed in a result, an error or a log. Omitted: a " +
                     "random secret is generated and discarded.")]
        string? clientSecret = null,
        [Description("Tenant to operate on. Falls back to URL route.")] string? tenantId = null)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(instructions))
        {
            return Refuse("InvalidArgument", "name and instructions are required.");
        }

        var autonomy = ParseOptional(autonomyLevel, AgentModel.AutonomyNames, "autonomyLevel", out var error);
        var runner = error == null ? ParseOptional(runnerKind, AgentModel.RunnerNames, "runnerKind", out error) : 0;
        if (error != null)
        {
            return Refuse("InvalidArgument", error);
        }

        var hasMandateValues = toolGrants != null || riskCeiling != null || changeBudgetPerRun != null ||
                               writeMode != null || allowedCkTypes != null;
        OctoObjectId? existingMandateId = null;
        if (!string.IsNullOrWhiteSpace(mandateRtId))
        {
            if (hasMandateValues)
            {
                return Refuse("InvalidArgument", "Pass either mandateRtId or mandate values, not both.");
            }

            if (!OctoObjectId.TryParse(mandateRtId.Trim(), out var parsedMandate))
            {
                return Refuse("InvalidArgument", $"mandateRtId '{mandateRtId}' is no runtime id.");
            }

            existingMandateId = parsedMandate;
        }

        var mandateValues = AgentModel.DefaultMandate;
        if (existingMandateId == null)
        {
            var (values, code, mandateError) = BuildMandate(server, AgentModel.DefaultMandate, toolGrants, riskCeiling,
                changeBudgetPerRun, writeMode, allowedCkTypes);
            if (mandateError != null)
            {
                return Refuse(code!, mandateError);
            }

            mandateValues = values!;
        }

        var clientId = string.IsNullOrWhiteSpace(serviceAccountClientId)
            ? AgentModel.DefaultClientId(name)
            : serviceAccountClientId.Trim();
        if (!AgentModel.IsValidClientId(clientId))
        {
            return Refuse("InvalidArgument",
                $"serviceAccountClientId '{clientId}' is invalid: ASCII letters, digits, '-', '_', '.' (2-100 characters).");
        }

        var engine = await OpenAsync(server, tenantId, write: true);
        if (engine.Error != null)
        {
            return Refuse(engine.Code ?? "Unauthorized", engine.Error, engine.TenantId);
        }

        var principal = McpCallerPrincipal.FromServer(server);
        var sponsor = string.IsNullOrWhiteSpace(sponsorUserId) ? principal?.UserSubjectId : sponsorUserId.Trim();
        var agentRtId = OctoObjectId.GenerateNewId();
        RtEntity? newMandate = null;
        // What this call has written so far, so that every failure after the first write - a refusal, an identity
        // error or an exception - removes it again (no orphaned agent, mandate or client).
        var entitiesWritten = false;
        var clientCreated = false;
        IdentityClientContext? identity = null;
        try
        {
            using (var session = await engine.Repo.GetSessionAsync(engine.Security!))
            {
                var agents = await AgentModel.ListAgentsAsync(engine.Repo, session);
                if (agents.Any(a => string.Equals(a.GetAttributeValueOrDefault("Name")?.ToString(), name.Trim(),
                        StringComparison.OrdinalIgnoreCase)))
                {
                    return Refuse("AgentExists", $"An agent named '{name.Trim()}' exists already.", engine.TenantId);
                }

                if (agents.Any(a => string.Equals(AgentModel.ClientIdOf(a), clientId, StringComparison.Ordinal)))
                {
                    return Refuse("AgentExists", $"An agent runs as client '{clientId}' already.", engine.TenantId);
                }

                if (existingMandateId != null &&
                    await AgentModel.GetMandateAsync(engine.Repo, session, existingMandateId.Value) == null)
                {
                    return Refuse("MandateNotFound", $"No mandate with runtime id '{existingMandateId}'.", engine.TenantId);
                }

                var agent = new RtEntity { RtId = agentRtId, CkTypeId = AgentModel.AgentTypeId };
                agent.SetAttributeValue("Name", AttributeValueTypesDto.String, name.Trim());
                agent.SetAttributeValue("Instructions", AttributeValueTypesDto.String, instructions);
                agent.SetAttributeValue("Version", AttributeValueTypesDto.Int, 1);
                agent.SetAttributeValue("Enabled", AttributeValueTypesDto.Boolean, enabled);
                agent.SetAttributeValue("AutonomyLevel", AttributeValueTypesDto.Enum, autonomy);
                agent.SetAttributeValue("RunnerKind", AttributeValueTypesDto.Enum, runner);
                agent.SetAttributeValue("ServiceAccountClientId", AttributeValueTypesDto.String, clientId);
                if (!string.IsNullOrWhiteSpace(model))
                {
                    agent.SetAttributeValue("Model", AttributeValueTypesDto.String, model.Trim());
                }

                if (!string.IsNullOrWhiteSpace(sponsor))
                {
                    agent.SetAttributeValue("SponsorUserId", AttributeValueTypesDto.String, sponsor);
                }

                var updates = new List<IEntityUpdateInfo<RtEntity>>
                    { EntityUpdateInfo<RtEntity>.CreateInsert(AgentModel.AgentTypeId, agent) };
                var mandateId = existingMandateId;
                if (mandateId == null)
                {
                    newMandate = new RtEntity { RtId = OctoObjectId.GenerateNewId(), CkTypeId = AgentModel.MandateTypeId };
                    newMandate.SetAttributeValue("Name", AttributeValueTypesDto.String, $"{name.Trim()} mandate");
                    AgentModel.SetMandate(newMandate, mandateValues);
                    updates.Add(EntityUpdateInfo<RtEntity>.CreateInsert(AgentModel.MandateTypeId, newMandate));
                    mandateId = newMandate.RtId;
                }

                var associations = new List<AssociationUpdateInfo> { GovernedBy(agentRtId, mandateId.Value) };
                var failure = await ApplyAsync(engine.Repo, session, updates, associations);
                if (failure != null)
                {
                    return Refuse("WriteRefused", failure, engine.TenantId);
                }

                entitiesWritten = true;
            }

            // Identity step: client, then roles. On failure remove the client (if created) and the entities.
            identity = await IdentityClientContext.TryBuildAsync(server, tenantId, withTenantClient: true);
            string? identityError = identity.Error;
            var assigned = new List<string>();
            if (identityError == null)
            {
                var secret = string.IsNullOrEmpty(clientSecret) ? GenerateSecret() : clientSecret;
                // The client follows the agent's Enabled: a disabled agent gets a disabled client, so it cannot obtain
                // a token before an administrator enables it (set_agent_enabled switches both).
                var created = await IdentityMethodCall.RunAsync(() => Clients(identity).CreateClientAsync(clientId, enabled,
                    $"Agent {name.Trim()}", null, [OidcConstants.GrantTypes.ClientCredentials], null, null, null,
                    [Constants.AgentScope], false, true, secret, null, null, null, null, null),
                    secretMethod: true);
                if (!created.IsSuccess)
                {
                    identityError = $"The service-account client '{clientId}' could not be created: " +
                                    IdentityMethodCall.SafeText(created.Error, clientSecret);
                }
                else
                {
                    clientCreated = true;
                    if (roles is { Count: > 0 })
                    {
                        var roleNames = await IdentityRtIds.RoleNamesAsync(identity, roles);
                        var clientRtId = created.Value ?? await IdentityRtIds.ClientAsync(identity, clientId);
                        var set = await IdentityMethodCall.RunAsync(() => Clients(identity).SetRolesAsync(clientRtId, roleNames));
                        if (!set.IsSuccess)
                        {
                            identityError = $"The roles of client '{clientId}' could not be set: " +
                                            IdentityMethodCall.SafeText(set.Error);
                        }
                        else
                        {
                            assigned.AddRange(roleNames);
                        }
                    }
                }
            }

            if (identityError != null)
            {
                var cleanup = await RollBackAsync(engine, identity, clientCreated ? clientId : null, agentRtId,
                    newMandate?.RtId, existingMandateId ?? newMandate?.RtId);
                entitiesWritten = clientCreated = false;
                return Refuse("IdentityFailed", identityError + " Nothing was kept" + cleanup, engine.TenantId);
            }

            using var readSession = await engine.Repo.GetSessionAsync(engine.Security!);
            var stored = await engine.Repo.GetRtEntityByRtIdAsync(readSession, new RtEntityId(AgentModel.AgentTypeId, agentRtId));
            var dto = stored == null
                ? null
                : AgentModel.ToAgentDto(stored, await AgentModel.MandateOfAsync(engine.Repo, readSession, stored));
            if (dto != null)
            {
                dto.ServiceAccountClientEnabled = enabled;
            }

            return new AgentResponse
            {
                IsSuccess = true,
                TenantId = engine.TenantId,
                Agent = dto,
                AssignedRoles = assigned,
                Message = $"Agent '{name.Trim()}' created; it runs as client '{clientId}' with " +
                          (assigned.Count == 0 ? "no roles." : $"role(s) {string.Join(", ", assigned)}.")
            };
        }
        catch (Exception ex)
        {
            var cleanup = entitiesWritten || clientCreated
                ? " Nothing was kept" + await RollBackAsync(engine, identity, clientCreated ? clientId : null, agentRtId,
                    newMandate?.RtId, existingMandateId ?? newMandate?.RtId)
                : string.Empty;
            return Refuse("Failed", IdentityMethodCall.SafeText(ex.Message, clientSecret) + cleanup, engine.TenantId);
        }
    }

    /// <summary>Updates an agent, its mandate and the roles of its client.</summary>
    [McpServerTool(Name = "update_agent")]
    [McpRisk(McpRiskLevel.Medium)]
    [Description(
        "Update an agent (tenant administrators only; agents never manage agents). Every parameter is optional; " +
        "an omitted one keeps its value. Mandate values change the " +
        "agent's own mandate (toolGrants and allowedCkTypes replace the list, [] clears it); a mandate that governs " +
        "several agents is not changed here (pass mandateRtId to switch the agent to another mandate instead). Grants " +
        "are validated like in create_agent (UnknownTool, RiskAboveCeiling). 'roles' replaces the roles of the " +
        "service-account client. A change of instructions, model or mandate raises the agent's version. Use " +
        "set_agent_enabled to switch the agent on or off.")]
    public static async Task<AgentResponse> UpdateAgent(
        McpServer server,
        [Description("Runtime id or name of the agent.")] string agent,
        [Description("New name (unique in the tenant).")] string? name = null,
        [Description("New persona and standing instructions.")] string? instructions = null,
        [Description("New model id; \"\" clears it (tenant default).")] string? model = null,
        [Description("ProposeOnly or ActWithinBudget.")] string? autonomyLevel = null,
        [Description("AgentSdk or ClaudeCodeCli.")] string? runnerKind = null,
        [Description("New sponsor user id.")] string? sponsorUserId = null,
        [Description("Runtime id of another Mandate to govern the agent. Leave the mandate values empty then.")]
        string? mandateRtId = null,
        [Description("New tool grants (replace the list): toolPattern, maxRisk, argumentConstraints.")]
        List<AgentToolGrantDto>? toolGrants = null,
        [Description("New risk ceiling: Low, Medium, High.")] string? riskCeiling = null,
        [Description("New change budget per run (0 or more).")] int? changeBudgetPerRun = null,
        [Description("Propose (writing tools refused with ProposeRequired until the proposal flow exists) or Direct.")] string? writeMode = null,
        [Description("New list of writable CK type ids (replaces the list; [] clears it).")]
        List<string>? allowedCkTypes = null,
        [Description("Role names or ids the service-account client gets instead of its current roles.")]
        List<string>? roles = null,
        [Description("Tenant to operate on. Falls back to URL route.")] string? tenantId = null)
    {
        if (string.IsNullOrWhiteSpace(agent))
        {
            return Refuse("InvalidArgument", "agent is required.");
        }

        int? autonomy = null, runner = null;
        string? error = null;
        if (autonomyLevel != null)
        {
            autonomy = ParseOptional(autonomyLevel, AgentModel.AutonomyNames, "autonomyLevel", out error);
        }

        if (error == null && runnerKind != null)
        {
            runner = ParseOptional(runnerKind, AgentModel.RunnerNames, "runnerKind", out error);
        }

        if (error != null)
        {
            return Refuse("InvalidArgument", error);
        }

        if (name != null && string.IsNullOrWhiteSpace(name))
        {
            return Refuse("InvalidArgument", "name must not be empty.");
        }

        var hasMandateValues = toolGrants != null || riskCeiling != null || changeBudgetPerRun != null ||
                               writeMode != null || allowedCkTypes != null;
        OctoObjectId? switchTo = null;
        if (!string.IsNullOrWhiteSpace(mandateRtId))
        {
            if (hasMandateValues)
            {
                return Refuse("InvalidArgument", "Pass either mandateRtId or mandate values, not both.");
            }

            if (!OctoObjectId.TryParse(mandateRtId.Trim(), out var parsed))
            {
                return Refuse("InvalidArgument", $"mandateRtId '{mandateRtId}' is no runtime id.");
            }

            switchTo = parsed;
        }

        var engine = await OpenAsync(server, tenantId, write: true);
        if (engine.Error != null)
        {
            return Refuse(engine.Code ?? "Unauthorized", engine.Error, engine.TenantId);
        }

        try
        {
            RtEntity? stored;
            using (var session = await engine.Repo.GetSessionAsync(engine.Security!))
            {
                stored = await AgentModel.FindAgentAsync(engine.Repo, session, agent);
                if (stored == null)
                {
                    return Refuse("AgentNotFound", $"No agent '{agent}'.", engine.TenantId);
                }


                if (name != null)
                {
                    var agents = await AgentModel.ListAgentsAsync(engine.Repo, session);
                    if (agents.Any(a => a.RtId != stored.RtId && string.Equals(
                            a.GetAttributeValueOrDefault("Name")?.ToString(), name.Trim(), StringComparison.OrdinalIgnoreCase)))
                    {
                        return Refuse("AgentExists", $"An agent named '{name.Trim()}' exists already.", engine.TenantId);
                    }
                }

                var updates = new List<IEntityUpdateInfo<RtEntity>>();
                var associations = new List<AssociationUpdateInfo>();
                var currentMandateId = await AgentModel.GoverningMandateIdAsync(engine.Repo, session, stored.RtId);
                var mandateChanged = false;

                if (switchTo != null && switchTo != currentMandateId)
                {
                    if (await AgentModel.GetMandateAsync(engine.Repo, session, switchTo.Value) == null)
                    {
                        return Refuse("MandateNotFound", $"No mandate with runtime id '{switchTo}'.", engine.TenantId);
                    }

                    if (currentMandateId != null)
                    {
                        associations.Add(AssociationUpdateInfo.CreateDelete(
                            new RtEntityId(AgentModel.AgentTypeId, stored.RtId),
                            new RtEntityId(AgentModel.MandateTypeId, currentMandateId.Value), AgentModel.GovernedByRoleId));
                    }

                    associations.Add(GovernedBy(stored.RtId, switchTo.Value));
                    mandateChanged = true;
                }
                else if (hasMandateValues)
                {
                    var current = currentMandateId == null
                        ? null
                        : await AgentModel.GetMandateAsync(engine.Repo, session, currentMandateId.Value);
                    if (current != null &&
                        await AgentModel.GovernedAgentCountAsync(engine.Repo, session, current.RtId) > 1)
                    {
                        return Refuse("SharedMandate",
                            $"The mandate '{current.GetAttributeValueOrDefault("Name")}' governs several agents; " +
                            "change it for all of them deliberately or switch this agent with mandateRtId.",
                            engine.TenantId);
                    }

                    var (values, code, mandateError) = BuildMandate(server,
                        current == null ? AgentModel.DefaultMandate : AgentModel.ReadMandate(current), toolGrants,
                        riskCeiling, changeBudgetPerRun, writeMode, allowedCkTypes);
                    if (mandateError != null)
                    {
                        return Refuse(code!, mandateError, engine.TenantId);
                    }

                    var mandate = new RtEntity
                        { RtId = current?.RtId ?? OctoObjectId.GenerateNewId(), CkTypeId = AgentModel.MandateTypeId };
                    AgentModel.SetMandate(mandate, values!);
                    if (current == null)
                    {
                        mandate.SetAttributeValue("Name", AttributeValueTypesDto.String,
                            $"{stored.GetAttributeValueOrDefault("Name")} mandate");
                        updates.Add(EntityUpdateInfo<RtEntity>.CreateInsert(AgentModel.MandateTypeId, mandate));
                        associations.Add(GovernedBy(stored.RtId, mandate.RtId));
                    }
                    else
                    {
                        updates.Add(EntityUpdateInfo<RtEntity>.CreateUpdate(
                            new RtEntityId(AgentModel.MandateTypeId, mandate.RtId), mandate));
                    }

                    mandateChanged = true;
                }

                var change = new RtEntity { RtId = stored.RtId, CkTypeId = AgentModel.AgentTypeId };
                var agentChanged = false;
                var versionRaised = mandateChanged;
                if (name != null)
                {
                    change.SetAttributeValue("Name", AttributeValueTypesDto.String, name.Trim());
                    agentChanged = true;
                }

                if (instructions != null)
                {
                    if (string.IsNullOrWhiteSpace(instructions))
                    {
                        return Refuse("InvalidArgument", "instructions must not be empty.", engine.TenantId);
                    }

                    versionRaised |= instructions != stored.GetAttributeValueOrDefault("Instructions")?.ToString();
                    change.SetAttributeValue("Instructions", AttributeValueTypesDto.String, instructions);
                    agentChanged = true;
                }

                if (model != null)
                {
                    versionRaised |= model.Trim() != (stored.GetAttributeValueOrDefault("Model")?.ToString() ?? string.Empty);
                    change.SetAttributeValue("Model", AttributeValueTypesDto.String, model.Trim());
                    agentChanged = true;
                }

                if (autonomy != null)
                {
                    change.SetAttributeValue("AutonomyLevel", AttributeValueTypesDto.Enum, autonomy.Value);
                    agentChanged = true;
                }

                if (runner != null)
                {
                    change.SetAttributeValue("RunnerKind", AttributeValueTypesDto.Enum, runner.Value);
                    agentChanged = true;
                }

                if (sponsorUserId != null)
                {
                    change.SetAttributeValue("SponsorUserId", AttributeValueTypesDto.String, sponsorUserId.Trim());
                    agentChanged = true;
                }

                if (versionRaised)
                {
                    change.SetAttributeValue("Version", AttributeValueTypesDto.Int, AgentModel.VersionOf(stored) + 1);
                    agentChanged = true;
                }

                if (agentChanged)
                {
                    updates.Add(EntityUpdateInfo<RtEntity>.CreateUpdate(
                        new RtEntityId(AgentModel.AgentTypeId, stored.RtId), change));
                }

                if (updates.Count > 0 || associations.Count > 0)
                {
                    var failure = await ApplyAsync(engine.Repo, session, updates, associations);
                    if (failure != null)
                    {
                        return Refuse("WriteRefused", failure, engine.TenantId);
                    }
                }
            }

            var assigned = new List<string>();
            if (roles != null)
            {
                var clientId = AgentModel.ClientIdOf(stored);
                var identity = await IdentityClientContext.TryBuildAsync(server, tenantId, withTenantClient: true);
                if (identity.Error != null)
                {
                    return Refuse("IdentityFailed", "The agent was updated, its client roles were not: " + identity.Error,
                        engine.TenantId);
                }

                var roleNames = await IdentityRtIds.RoleNamesAsync(identity, roles);
                var set = await IdentityMethodCall.RunAsync(async () =>
                    await Clients(identity).SetRolesAsync(await IdentityRtIds.ClientAsync(identity, clientId), roleNames));
                if (!set.IsSuccess)
                {
                    return Refuse("IdentityFailed", $"The agent was updated, the roles of client '{clientId}' were not: " +
                                                    IdentityMethodCall.SafeText(set.Error), engine.TenantId);
                }

                assigned.AddRange(roleNames);
            }

            var response = await ReadAgentAsync(engine, stored.RtId);
            response.AssignedRoles = assigned;
            response.Message = $"Agent '{response.Agent?.Name}' updated (version {response.Agent?.Version}).";
            return response;
        }
        catch (Exception ex)
        {
            return Refuse("Failed", IdentityMethodCall.SafeText(ex.Message), engine.TenantId);
        }
    }

    /// <summary>Enables or disables an agent and its service-account client.</summary>
    [McpServerTool(Name = "set_agent_enabled")]
    [McpRisk(McpRiskLevel.Medium)]
    [Description(
        "Switch an agent on or off (kill switch; tenant administrators only, never an agent). Disabling " +
        "sets the agent's Enabled to false first, then disables its service-account client in the tenant identity, so the client can no longer obtain tokens. Enabling switches " +
        "the client on first, then the agent.")]
    public static async Task<AgentResponse> SetAgentEnabled(
        McpServer server,
        [Description("Runtime id or name of the agent.")] string agent,
        [Description("True to enable, false to disable.")] bool enabled,
        [Description("Tenant to operate on. Falls back to URL route.")] string? tenantId = null)
    {
        if (string.IsNullOrWhiteSpace(agent))
        {
            return Refuse("InvalidArgument", "agent is required.");
        }

        var engine = await OpenAsync(server, tenantId, write: true);
        if (engine.Error != null)
        {
            return Refuse(engine.Code ?? "Unauthorized", engine.Error, engine.TenantId);
        }

        try
        {
            RtEntity? stored;
            using (var session = await engine.Repo.GetSessionAsync(engine.Security!))
            {
                stored = await AgentModel.FindAgentAsync(engine.Repo, session, agent);
            }

            if (stored == null)
            {
                return Refuse("AgentNotFound", $"No agent '{agent}'.", engine.TenantId);
            }


            var clientId = AgentModel.ClientIdOf(stored);
            var identity = await IdentityClientContext.TryBuildAsync(server, tenantId, withTenantClient: true);

            if (enabled)
            {
                // Client first: an agent marked enabled whose client stays off could not run anyway, but the reverse
                // order would leave an enabled client behind a failed agent write.
                var clientError = await SetClientEnabledAsync(identity, clientId, true);
                if (clientError != null)
                {
                    return Refuse("IdentityFailed", clientError + " The agent stays disabled.", engine.TenantId);
                }

                var failure = await WriteEnabledAsync(engine, stored.RtId, true);
                if (failure != null)
                {
                    await SetClientEnabledAsync(identity, clientId, false);
                    return Refuse("WriteRefused", failure + " The client was switched off again.", engine.TenantId);
                }
            }
            else
            {
                // Agent first: the kill switch takes effect even when the identity step fails.
                var failure = await WriteEnabledAsync(engine, stored.RtId, false);
                if (failure != null)
                {
                    return Refuse("WriteRefused", failure, engine.TenantId);
                }

                var clientError = await SetClientEnabledAsync(identity, clientId, false);
                if (clientError != null)
                {
                    var partial = await ReadAgentAsync(engine, stored.RtId);
                    partial.IsSuccess = false;
                    partial.ErrorCode = "IdentityFailed";
                    partial.ErrorMessage = "The agent is disabled, but its client is still enabled: " + clientError;
                    return partial;
                }
            }

            var response = await ReadAgentAsync(engine, stored.RtId);
            if (response.Agent != null)
            {
                response.Agent.ServiceAccountClientEnabled = enabled;
            }

            response.Message = $"Agent '{response.Agent?.Name}' {(enabled ? "enabled" : "disabled")}; client " +
                               $"'{clientId}' {(enabled ? "enabled" : "disabled")}.";
            return response;
        }
        catch (Exception ex)
        {
            return Refuse("Failed", IdentityMethodCall.SafeText(ex.Message), engine.TenantId);
        }
    }

    /// <summary>Lists the agents of the tenant with their mandates.</summary>
    [McpServerTool(Name = "list_agents")]
    [Description("List the agents of the tenant with their mandates (tool grants, risk ceiling, change budget, write " +
                 "mode). The state of the service-account clients is not read here; use get_agent for one agent.")]
    public static async Task<ListAgentsResponse> ListAgents(
        McpServer server,
        [Description("Tenant to operate on. Falls back to URL route.")] string? tenantId = null)
    {
        var engine = await OpenAsync(server, tenantId);
        if (engine.Error != null)
        {
            return new ListAgentsResponse { TenantId = engine.TenantId, ErrorMessage = engine.Error };
        }

        try
        {
            using var session = await engine.Repo.GetSessionAsync(engine.Security!);
            var result = new List<AgentDto>();
            foreach (var agent in await AgentModel.ListAgentsAsync(engine.Repo, session))
            {
                result.Add(AgentModel.ToAgentDto(agent, await AgentModel.MandateOfAsync(engine.Repo, session, agent)));
            }

            return new ListAgentsResponse
            {
                IsSuccess = true,
                TenantId = engine.TenantId,
                Agents = result.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList(),
                TotalCount = result.Count
            };
        }
        catch (Exception ex)
        {
            return new ListAgentsResponse { TenantId = engine.TenantId, ErrorMessage = ex.Message };
        }
    }

    /// <summary>Reads one agent with its mandate and the state of its client.</summary>
    [McpServerTool(Name = "get_agent")]
    [Description("Get one agent by runtime id or name, with its mandate and whether its service-account client is " +
                 "enabled in the tenant identity.")]
    public static async Task<AgentResponse> GetAgent(
        McpServer server,
        [Description("Runtime id or name of the agent.")] string agent,
        [Description("Tenant to operate on. Falls back to URL route.")] string? tenantId = null)
    {
        if (string.IsNullOrWhiteSpace(agent))
        {
            return Refuse("InvalidArgument", "agent is required.");
        }

        var engine = await OpenAsync(server, tenantId);
        if (engine.Error != null)
        {
            return Refuse(engine.Code ?? "Unauthorized", engine.Error, engine.TenantId);
        }

        try
        {
            RtEntity? stored;
            using (var session = await engine.Repo.GetSessionAsync(engine.Security!))
            {
                stored = await AgentModel.FindAgentAsync(engine.Repo, session, agent);
            }

            if (stored == null)
            {
                return Refuse("AgentNotFound", $"No agent '{agent}'.", engine.TenantId);
            }

            var response = await ReadAgentAsync(engine, stored.RtId);
            var clientId = AgentModel.ClientIdOf(stored);
            var identity = await IdentityClientContext.TryBuildAsync(server, tenantId);
            if (identity.Error == null && response.Agent != null && clientId.Length > 0)
            {
                try
                {
                    var client = await identity.Client!.GetClient(clientId);
                    response.Agent.ServiceAccountClientEnabled = client?.IsEnabled;
                }
                catch (Exception ex)
                {
                    response.Message = $"The state of client '{clientId}' could not be read: " +
                                       IdentityMethodCall.SafeText(ex.Message);
                }
            }

            return response;
        }
        catch (Exception ex)
        {
            return Refuse("Failed", IdentityMethodCall.SafeText(ex.Message), engine.TenantId);
        }
    }

    // ---------------------------------------------------------------------------------------------------------------

    internal sealed record AgentEngineContext(
        ITenantRepository? Repository,
        RtSecurityContext? Security,
        string? TenantId,
        string? Error,
        string? Code = null)
    {
        public ITenantRepository Repo => Repository ?? throw new InvalidOperationException("No repository.");
    }

    /// <summary>The caller's security context and the tenant repository; refuses tenants without the agent types.</summary>
    internal static async Task<AgentEngineContext> OpenAsync(McpServer server, string? tenantId, bool write = false)
    {
        var tenantResolution = server.Services!.GetRequiredService<ITenantResolutionService>();
        var security = await RuntimeSecurityContextResolver.ResolveAsync(server, tenantResolution, tenantId);
        if (security.Error != null)
        {
            return new AgentEngineContext(null, null, security.ResolvedTenantId, security.Error);
        }

        // The writing tools are for tenant administrators only (the roles the identity requires for the client
        // methods: TenantManagement or UserManagement). Checked before anything is written: the write channel these
        // tools put on their session (below) is what admits the write in the engine (AB#9729).
        if (write && !IsTenantAdministrator(security.SecurityContext))
        {
            return new AgentEngineContext(null, null, security.ResolvedTenantId,
                $"Managing agents needs a tenant administrator (role {CommonConstants.TenantManagementRole} or " +
                $"{CommonConstants.UserManagementRole}) in tenant '{security.ResolvedTenantId}'; the caller has neither.",
                "NotTenantAdministrator");
        }

        var repository = await tenantResolution.GetTenantRepositoryAsync(tenantId);
        var ckCache = server.Services!.GetRequiredService<ICkCacheService>();
        await repository.LoadCacheForTenantAsync(ckCache);
        if (!ckCache.TryGetRtCkType(repository.TenantId, AgentModel.AgentTypeId, out _) ||
            !ckCache.TryGetRtCkType(repository.TenantId, AgentModel.MandateTypeId, out _))
        {
            return new AgentEngineContext(null, null, repository.TenantId,
                string.Format(System.Globalization.CultureInfo.InvariantCulture, NoAgentTypes, repository.TenantId),
                "NoAgentTypes");
        }

        if (!write)
        {
            return new AgentEngineContext(repository, security.SecurityContext, repository.TenantId, null);
        }

        // AB#9729: an agent never manages agents - not itself, not another agent, not a new one - whatever roles its
        // service account holds. A failed read refuses (fail closed).
        if (await AgentCallerAsync(server, repository) is { } agentCaller)
        {
            return new AgentEngineContext(null, null, repository.TenantId, agentCaller, "AgentCaller");
        }

        // The channel is set here, in service code, never from caller input; the caller's identity and roles stay.
        return new AgentEngineContext(repository, security.SecurityContext!.WithWriteChannel(AgentModel.WriteChannel),
            repository.TenantId, null);
    }

    /// <summary>
    ///     The refusal text when the caller's client id is the service account of an agent of the tenant, else null.
    ///     The lookup runs as the system (an agent's client may not be able to read the agent types), in the resolved
    ///     tenant only.
    /// </summary>
    private static async Task<string?> AgentCallerAsync(McpServer server, ITenantRepository repository)
    {
        var callerClientId = McpCallerPrincipal.FromServer(server)?.ClientId;
        if (string.IsNullOrEmpty(callerClientId))
        {
            return null;
        }

        try
        {
            using var session = await repository.GetSessionAsync(RtSecurityContext.System);
            var agents = await AgentModel.ListAgentsAsync(repository, session);
            if (agents.Count >= AgentModel.MaxAgents)
            {
                return $"The agents of tenant '{repository.TenantId}' could not be read completely; managing agents is " +
                       "refused until they can (fail closed).";
            }

            var own = agents.FirstOrDefault(a => string.Equals(AgentModel.ClientIdOf(a), callerClientId, StringComparison.Ordinal));
            return own == null
                ? null
                : $"Client '{callerClientId}' is the service account of agent " +
                  $"'{own.GetAttributeValueOrDefault("Name")}'; agents never manage agents, their own included.";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return $"The agents of tenant '{repository.TenantId}' could not be read; managing agents is refused (fail closed).";
        }
    }

    /// <summary>True when the caller holds TenantManagement or UserManagement in the resolved tenant.</summary>
    internal static bool IsTenantAdministrator(RtSecurityContext? security) =>
        security != null && (security.Roles ?? []).Any(r =>
            string.Equals(r, CommonConstants.TenantManagementRole, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(r, CommonConstants.UserManagementRole, StringComparison.OrdinalIgnoreCase));

    private static (MandateValues? Values, string? Code, string? Error) BuildMandate(McpServer server,
        MandateValues current, List<AgentToolGrantDto>? toolGrants, string? riskCeiling, int? changeBudgetPerRun,
        string? writeMode, List<string>? allowedCkTypes)
    {
        var ceiling = current.RiskCeiling;
        if (riskCeiling != null)
        {
            var parsed = AgentModel.Parse(riskCeiling, AgentModel.RiskNames);
            if (parsed == null)
            {
                return (null, "InvalidArgument", $"Unknown riskCeiling '{riskCeiling}'; use Low, Medium or High.");
            }

            ceiling = parsed.Value;
        }

        var mode = current.WriteMode;
        if (writeMode != null)
        {
            var parsed = AgentModel.Parse(writeMode, AgentModel.WriteModeNames);
            if (parsed == null)
            {
                return (null, "InvalidArgument", $"Unknown writeMode '{writeMode}'; use Propose or Direct.");
            }

            mode = parsed.Value;
        }

        if (changeBudgetPerRun is < 0)
        {
            return (null, "InvalidArgument", "changeBudgetPerRun must be 0 or more.");
        }

        var ckTypeError = AgentModel.ValidateCkTypes(allowedCkTypes);
        if (ckTypeError != null)
        {
            return (null, "InvalidArgument", ckTypeError);
        }

        // Kept grants are checked again: a lowered ceiling must not leave a grant above it.
        var registry = server.Services!.GetService<IToolRiskRegistry>() ?? new ToolRiskRegistry();
        var (grants, code, error) = AgentModel.ValidateGrants(toolGrants ?? current.Grants.ToList(), ceiling,
            registry.GetAll().Keys.ToHashSet(StringComparer.Ordinal));
        if (error != null)
        {
            return (null, code, error);
        }

        var allowed = allowedCkTypes == null
            ? current.AllowedCkTypes
            : allowedCkTypes.Count == 0 ? null : allowedCkTypes.Select(t => t.Trim()).ToList();
        return (new MandateValues(grants!, ceiling, changeBudgetPerRun ?? current.ChangeBudgetPerRun, mode, allowed),
            null, null);
    }

    private static int ParseOptional(string? value, string[] names, string parameter, out string? error)
    {
        error = null;
        if (value == null)
        {
            return 0;
        }

        var parsed = AgentModel.Parse(value, names);
        if (parsed == null)
        {
            error = $"Unknown {parameter} '{value}'; use {string.Join(" or ", names)}.";
            return 0;
        }

        return parsed.Value;
    }

    private static AssociationUpdateInfo GovernedBy(OctoObjectId agentRtId, OctoObjectId mandateRtId) =>
        AssociationUpdateInfo.CreateInsert(new RtEntityId(AgentModel.AgentTypeId, agentRtId),
            new RtEntityId(AgentModel.MandateTypeId, mandateRtId), AgentModel.GovernedByRoleId);

    /// <summary>Applies one write in a transaction; returns the failure text or null.</summary>
    private static async Task<string?> ApplyAsync(ITenantRepository repository, IOctoSession session,
        IReadOnlyList<IEntityUpdateInfo<RtEntity>> updates, IReadOnlyList<AssociationUpdateInfo> associations)
    {
        session.StartTransaction();
        var result = new OperationResult();
        try
        {
            await repository.ApplyChangesAsync(session, updates, associations, result);
            if (result.HasErrors || result.HasFatalErrors)
            {
                await session.AbortTransactionAsync();
                return $"The write was refused: {result.GetMessages()}";
            }

            await session.CommitTransactionAsync();
            return null;
        }
        catch
        {
            try
            {
                await session.AbortTransactionAsync();
            }
            catch (Exception)
            {
                // The original exception is the one to report.
            }

            throw;
        }
    }

    private static async Task<string?> WriteEnabledAsync(AgentEngineContext engine, OctoObjectId agentRtId, bool enabled)
    {
        using var session = await engine.Repo.GetSessionAsync(engine.Security!);
        var change = new RtEntity { RtId = agentRtId, CkTypeId = AgentModel.AgentTypeId };
        change.SetAttributeValue("Enabled", AttributeValueTypesDto.Boolean, enabled);
        return await ApplyAsync(engine.Repo, session,
            [EntityUpdateInfo<RtEntity>.CreateUpdate(new RtEntityId(AgentModel.AgentTypeId, agentRtId), change)], []);
    }

    private static async Task<string?> SetClientEnabledAsync(IdentityClientContext identity, string clientId, bool enabled)
    {
        if (identity.Error != null)
        {
            return identity.Error;
        }

        if (clientId.Length == 0)
        {
            return "The agent names no service-account client.";
        }

        var outcome = await IdentityMethodCall.RunAsync(async () =>
            await Clients(identity).UpdateClientAsync(await IdentityRtIds.ClientAsync(identity, clientId), enabled: enabled));
        return outcome.IsSuccess
            ? null
            : $"Client '{clientId}' could not be {(enabled ? "enabled" : "disabled")}: " +
              IdentityMethodCall.SafeText(outcome.Error);
    }

    /// <summary>Removes what a failed create wrote; returns a note on anything that could not be removed.</summary>
    private static async Task<string> RollBackAsync(AgentEngineContext engine, IdentityClientContext? identity,
        string? createdClientId, OctoObjectId agentRtId, OctoObjectId? createdMandateRtId, OctoObjectId? linkedMandateRtId)
    {
        var notes = new List<string>();
        if (createdClientId != null)
        {
            var deleted = identity == null
                ? null
                : await IdentityMethodCall.RunAsync(async () =>
                    await Clients(identity).DeleteClientAsync(await IdentityRtIds.ClientAsync(identity, createdClientId)));
            if (deleted is not { IsSuccess: true })
            {
                notes.Add($"client '{createdClientId}' remains and must be deleted by hand");
            }
        }

        try
        {
            using var session = await engine.Repo.GetSessionAsync(engine.Security!);
            var updates = new List<IEntityUpdateInfo<RtEntity>>
                { EntityUpdateInfo<RtEntity>.CreateDelete(new RtEntityId(AgentModel.AgentTypeId, agentRtId)) };
            if (createdMandateRtId != null)
            {
                updates.Add(EntityUpdateInfo<RtEntity>.CreateDelete(
                    new RtEntityId(AgentModel.MandateTypeId, createdMandateRtId.Value)));
            }

            var associations = linkedMandateRtId == null
                ? new List<AssociationUpdateInfo>()
                :
                [
                    AssociationUpdateInfo.CreateDelete(new RtEntityId(AgentModel.AgentTypeId, agentRtId),
                        new RtEntityId(AgentModel.MandateTypeId, linkedMandateRtId.Value), AgentModel.GovernedByRoleId)
                ];
            if (await ApplyAsync(engine.Repo, session, updates, associations) != null)
            {
                notes.Add($"agent '{agentRtId}' remains and must be deleted by hand");
            }
        }
        catch (Exception)
        {
            notes.Add($"agent '{agentRtId}' remains and must be deleted by hand");
        }

        return notes.Count == 0 ? "." : $", except: {string.Join("; ", notes)}.";
    }

    private static async Task<AgentResponse> ReadAgentAsync(AgentEngineContext engine, OctoObjectId agentRtId)
    {
        using var session = await engine.Repo.GetSessionAsync(engine.Security!);
        var agent = await engine.Repo.GetRtEntityByRtIdAsync(session, new RtEntityId(AgentModel.AgentTypeId, agentRtId));
        return agent == null
            ? Refuse("AgentNotFound", $"No agent '{agentRtId}'.", engine.TenantId)
            : new AgentResponse
            {
                IsSuccess = true,
                TenantId = engine.TenantId,
                Agent = AgentModel.ToAgentDto(agent, await AgentModel.MandateOfAsync(engine.Repo, session, agent))
            };
    }

    private static RtClientMethodClient Clients(IdentityClientContext ctx) => new(ctx.TenantClient!);

    /// <summary>A random client secret that nobody sees: 32 bytes, base64url.</summary>
    private static string GenerateSecret() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static AgentResponse Refuse(string code, string message, string? tenantId = null) =>
        new() { IsSuccess = false, ErrorCode = code, ErrorMessage = message, TenantId = tenantId };
}
