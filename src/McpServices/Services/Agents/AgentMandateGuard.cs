using Meshmakers.Octo.Backend.McpServices.Models;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;

namespace Meshmakers.Octo.Backend.McpServices.Services.Agents;

/// <summary>The outcome of the static mandate check of one tool call.</summary>
/// <param name="Allowed">True when the tool may run.</param>
/// <param name="Code">Refusal code (<see cref="AgentMandateGuard" /> constants), null when allowed.</param>
/// <param name="Message">Refusal reason for the caller, null when allowed.</param>
/// <param name="AgentName">The name of the calling agent, null when the caller is no agent.</param>
internal sealed record AgentMandateDecision(bool Allowed, string? Code, string? Message, string? AgentName)
{
    public static readonly AgentMandateDecision NotAnAgent = new(true, null, null, null);

    public static AgentMandateDecision Refuse(string code, string message, string? agentName = null) =>
        new(false, code, message, agentName);
}

/// <summary>
///     The static mandate check of AB#9729 (concept <c>tenant-builder-agent-concept.md</c> §3.4, without run token and
///     policy decision point): a caller whose <c>client_id</c> is the <c>ServiceAccountClientId</c> of an agent of its
///     own tenant (the token's <c>tenant_id</c>) may call a tool only when that agent is enabled and its mandate grants
///     the tool at the tool's risk. Callers that are no agent pass unchanged.
///     <para>
///     Order of the checks: agent found exactly once → <c>Enabled</c> is true (<see cref="AgentDisabled" />) → exactly one
///     readable <c>GovernedBy</c> mandate → a grant matches the tool (<see cref="ToolNotGranted" />) → tool risk ≤ the
///     highest <c>MaxRisk</c> of the matching grants and ≤ the mandate's <c>RiskCeiling</c>
///     (<see cref="RiskAboveCeiling" />). A grant without <c>MaxRisk</c> admits Low (the engine stores no record
///     defaults).
///     </para>
///     <para>
///     Fail closed (<see cref="MandateUnavailable" />): a caller with a client id but no <c>tenant_id</c>, a lookup that
///     throws, more than one agent with the same client id, the agent list at its read limit, a missing, unreadable or
///     ambiguous mandate, a risk ceiling outside Low..High. A grant with an invalid pattern or an unknown
///     <c>MaxRisk</c> grants nothing. No cache: every call reads the agent and its mandate, so a disable or a grant change
///     takes effect on the next call.
///     </para>
///     <para>
///     The lookup runs as the system: an agent's own client usually has no read rights on the agent types, and the
///     check must not depend on what the caller may read. Only the token's own tenant is searched, so an agent of another
///     tenant with the same client id is never consulted.
///     </para>
/// </summary>
internal static class AgentMandateGuard
{
    public const string AgentDisabled = "AgentDisabled";
    public const string ToolNotGranted = "ToolNotGranted";
    public const string RiskAboveCeiling = "RiskAboveCeiling";
    public const string MandateUnavailable = "MandateUnavailable";

    /// <summary>
    ///     AB#9729: a token with the agent scope <c>octo_agent</c> whose caller is no agent of the token's tenant (a user
    ///     token, a client that is no agent, a tenant without agent types).
    /// </summary>
    public const string NotAnAgent = "NotAnAgent";

    /// <summary>Checks one tool call of <paramref name="caller" />.</summary>
    /// <param name="caller">The validated request principal, null when unauthenticated.</param>
    /// <param name="toolName">The tool the caller wants to run.</param>
    /// <param name="toolRisk">The risk class of the tool.</param>
    /// <param name="tenantRepository">Returns the repository of a tenant id.</param>
    /// <param name="ckCache">Returns the CK cache, to tell whether the tenant has the agent types at all.</param>
    public static async Task<AgentMandateDecision> CheckAsync(McpCallerPrincipal? caller, string toolName,
        McpRiskLevel toolRisk, Func<string, Task<ITenantRepository>> tenantRepository, Func<ICkCacheService> ckCache)
    {
        // AB#9729: the agent scope is valid only for a client token whose client is an agent of the token's tenant. A
        // user token never acts as an agent, and a non-agent client with octo_agent is refused instead of passing as an
        // ordinary caller.
        if (caller?.HasAgentScope == true && caller.IsUserPrincipal)
        {
            return AgentMandateDecision.Refuse(NotAnAgent,
                "A user token with the agent scope octo_agent is refused; the scope belongs to agent clients only.");
        }

        var decision = await CheckClientAsync(caller, toolName, toolRisk, tenantRepository, ckCache);
        if (ReferenceEquals(decision, AgentMandateDecision.NotAnAgent) && caller?.HasAgentScope == true)
        {
            return AgentMandateDecision.Refuse(NotAnAgent,
                $"Client '{caller.ClientId}' carries the agent scope octo_agent but is no agent of tenant " +
                $"'{caller.TenantId}'; the call is refused.");
        }

        return decision;
    }

    private static async Task<AgentMandateDecision> CheckClientAsync(McpCallerPrincipal? caller, string toolName,
        McpRiskLevel toolRisk, Func<string, Task<ITenantRepository>> tenantRepository, Func<ICkCacheService> ckCache)
    {
        var clientId = caller?.ClientId;
        if (string.IsNullOrEmpty(clientId))
        {
            // No client: no agent. Unauthenticated calls are refused by the tools themselves.
            return AgentMandateDecision.NotAnAgent;
        }

        if (string.IsNullOrWhiteSpace(caller!.TenantId))
        {
            return AgentMandateDecision.Refuse(MandateUnavailable,
                $"The token of client '{clientId}' carries no tenant_id, so it cannot be told whether it is an agent; " +
                "the call is refused (fail closed).");
        }

        RtEntity agent;
        RtEntity mandate;
        try
        {
            var repository = await tenantRepository(caller.TenantId);
            var cache = ckCache();
            await repository.LoadCacheForTenantAsync(cache);
            if (!cache.TryGetRtCkType(repository.TenantId, AgentModel.AgentTypeId, out _))
            {
                // The tenant has no agent types (System.Ai below 4.12.0 or not enabled), hence no agents.
                return AgentMandateDecision.NotAnAgent;
            }

            using var session = await repository.GetSessionAsync(RtSecurityContext.System);
            var agents = await AgentModel.ListAgentsAsync(repository, session);
            var matches = agents
                .Where(a => string.Equals(AgentModel.ClientIdOf(a), clientId, StringComparison.Ordinal))
                .ToList();
            if (matches.Count == 0)
            {
                if (agents.Count >= AgentModel.MaxAgents)
                {
                    return AgentMandateDecision.Refuse(MandateUnavailable,
                        $"Tenant '{caller.TenantId}' holds more agents than the mandate check reads ({AgentModel.MaxAgents}); " +
                        "the call is refused (fail closed).");
                }

                return AgentMandateDecision.NotAnAgent;
            }

            if (matches.Count > 1)
            {
                return AgentMandateDecision.Refuse(MandateUnavailable,
                    $"Client '{clientId}' is the service account of {matches.Count} agents; the mandate is ambiguous and " +
                    "the call is refused (fail closed).");
            }

            agent = matches[0];
            var agentName = NameOf(agent);
            if (agent.GetAttributeValueOrDefault("Enabled") is not true)
            {
                return AgentMandateDecision.Refuse(AgentDisabled,
                    $"Agent '{agentName}' is disabled (kill switch); it may not call any tool.", agentName);
            }

            var mandateIds = await AgentModel.GoverningMandateIdsAsync(repository, session, agent.RtId);
            if (mandateIds.Count != 1)
            {
                return AgentMandateDecision.Refuse(MandateUnavailable,
                    mandateIds.Count == 0
                        ? $"Agent '{agentName}' has no mandate; the call is refused (fail closed)."
                        : $"Agent '{agentName}' is governed by {mandateIds.Count} mandates; the call is refused (fail closed).",
                    agentName);
            }

            var stored = await AgentModel.GetMandateAsync(repository, session, mandateIds[0]);
            if (stored == null)
            {
                return AgentMandateDecision.Refuse(MandateUnavailable,
                    $"The mandate of agent '{agentName}' cannot be read; the call is refused (fail closed).", agentName);
            }

            mandate = stored;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return AgentMandateDecision.Refuse(MandateUnavailable,
                $"The mandate of client '{clientId}' cannot be read ({ex.GetType().Name}); the call is refused (fail closed).");
        }

        return Judge(NameOf(agent), mandate, toolName, toolRisk);
    }

    /// <summary>The grant and risk part of the check against one stored mandate.</summary>
    internal static AgentMandateDecision Judge(string agentName, RtEntity mandate, string toolName, McpRiskLevel toolRisk)
    {
        var ceiling = AgentModel.ToInt(mandate.GetAttributeValueOrDefault("RiskCeiling"));
        if (ceiling is < 0 or > 2)
        {
            return AgentMandateDecision.Refuse(MandateUnavailable,
                $"The mandate of agent '{agentName}' has an unknown risk ceiling ({ceiling}); the call is refused (fail closed).",
                agentName);
        }

        int? grantedRisk = null;
        foreach (var grant in AgentModel.ReadRawGrants(mandate))
        {
            if (!Matches(grant.Pattern, toolName) || grant.MaxRisk is < 0 or > 2)
            {
                continue;
            }

            grantedRisk = Math.Max(grantedRisk ?? 0, grant.MaxRisk);
        }

        var risk = (int)toolRisk;
        if (grantedRisk == null)
        {
            return AgentMandateDecision.Refuse(ToolNotGranted,
                $"Agent '{agentName}' may not call '{toolName}': its mandate grants no such tool.", agentName);
        }

        if (risk > grantedRisk.Value)
        {
            return AgentMandateDecision.Refuse(RiskAboveCeiling,
                $"Agent '{agentName}' may not call '{toolName}': the tool is {AgentModel.RiskNames[risk]} risk, its grant " +
                $"admits {AgentModel.RiskNames[grantedRisk.Value]} at most.", agentName);
        }

        if (risk > ceiling)
        {
            return AgentMandateDecision.Refuse(RiskAboveCeiling,
                $"Agent '{agentName}' may not call '{toolName}': the tool is {AgentModel.RiskNames[risk]} risk, above the " +
                $"mandate's risk ceiling {AgentModel.RiskNames[ceiling]}.", agentName);
        }

        return new AgentMandateDecision(true, null, null, agentName);
    }

    /// <summary>
    ///     True when <paramref name="pattern" /> grants <paramref name="toolName" />: an exact name (ordinal), or a prefix
    ///     ending in a single trailing '*' with at least one character before it. Anything else grants nothing.
    /// </summary>
    internal static bool Matches(string? pattern, string toolName)
    {
        if (string.IsNullOrEmpty(pattern) || string.IsNullOrEmpty(toolName))
        {
            return false;
        }

        var star = pattern.IndexOf('*', StringComparison.Ordinal);
        if (star < 0)
        {
            return string.Equals(pattern, toolName, StringComparison.Ordinal);
        }

        return star == pattern.Length - 1 && star > 0 &&
               toolName.StartsWith(pattern[..star], StringComparison.Ordinal);
    }

    private static string NameOf(RtEntity agent) =>
        agent.GetAttributeValueOrDefault("Name")?.ToString() ?? agent.RtId.ToString();
}
