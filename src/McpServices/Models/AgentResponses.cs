namespace Meshmakers.Octo.Backend.McpServices.Models;

/// <summary>One tool grant of an agent mandate (System.Ai record <c>AgentToolGrant</c>).</summary>
public class AgentToolGrantDto
{
    /// <summary>MCP tool name as in tools/list (e.g. <c>query_entities</c>) or a prefix ending in <c>*</c> (e.g. <c>knowledge_*</c>).</summary>
    public string ToolPattern { get; set; } = string.Empty;

    /// <summary>
    ///     Highest risk class (Low, Medium, High) the grant admits for the matching tools. Omitted on input: Low. On output
    ///     a grant stored without it reads as Low.
    /// </summary>
    public string? MaxRisk { get; set; }

    /// <summary>Optional JSON object of per-argument constraints, e.g. <c>{"ckTypeId": ["System.Ai/KnowledgePage"]}</c>.</summary>
    public string? ArgumentConstraints { get; set; }
}

/// <summary>The mandate that governs an agent (System.Ai <c>Mandate</c>).</summary>
public class AgentMandateDto
{
    /// <summary>Runtime id of the mandate.</summary>
    public string RtId { get; set; } = string.Empty;

    /// <summary>Name of the mandate.</summary>
    public string? Name { get; set; }

    /// <summary>Tool grants; an empty list grants nothing.</summary>
    public List<AgentToolGrantDto> ToolGrants { get; set; } = [];

    /// <summary>Highest risk class any tool call of a governed agent may have (Low, Medium, High).</summary>
    public string RiskCeiling { get; set; } = "Low";

    /// <summary>Executed changes one run may make; 0 allows no direct change.</summary>
    public int ChangeBudgetPerRun { get; set; }

    /// <summary>Propose (every granted write becomes a proposal) or Direct.</summary>
    public string WriteMode { get; set; } = "Propose";

    /// <summary>CK type ids the agent may write; null when the grants decide alone.</summary>
    public List<string>? AllowedCkTypes { get; set; }

    /// <summary>Number of agents this mandate governs (a blueprint may share one mandate between agents).</summary>
    public int GovernedAgentCount { get; set; }
}

/// <summary>An agent of the tenant (System.Ai <c>Agent</c>) with its mandate.</summary>
public class AgentDto
{
    /// <summary>Runtime id of the agent.</summary>
    public string RtId { get; set; } = string.Empty;

    /// <summary>Name of the agent, unique in the tenant.</summary>
    public string? Name { get; set; }

    /// <summary>Persona and standing instructions.</summary>
    public string? Instructions { get; set; }

    /// <summary>Version of the agent definition, raised on every change of instructions, model or mandate.</summary>
    public int Version { get; set; }

    /// <summary>Kill switch: false means the agent starts no run.</summary>
    public bool Enabled { get; set; }

    /// <summary>ProposeOnly or ActWithinBudget.</summary>
    public string AutonomyLevel { get; set; } = "ProposeOnly";

    /// <summary>Runner the orchestrator starts for a run (AgentSdk, ClaudeCodeCli).</summary>
    public string RunnerKind { get; set; } = "AgentSdk";

    /// <summary>Model id, or null for the tenant default.</summary>
    public string? Model { get; set; }

    /// <summary>User id of the person accountable for the agent.</summary>
    public string? SponsorUserId { get; set; }

    /// <summary>client_id of the service-account client the agent runs as.</summary>
    public string? ServiceAccountClientId { get; set; }

    /// <summary>
    ///     Whether the service-account client is enabled in the tenant identity; null when it was not read (list) or could
    ///     not be found.
    /// </summary>
    public bool? ServiceAccountClientEnabled { get; set; }

    /// <summary>The governing mandate; null for an agent without mandate (it may call no tool).</summary>
    public AgentMandateDto? Mandate { get; set; }
}

/// <summary>Response of the single-agent tools.</summary>
public class AgentResponse : IdentityResponse
{
    /// <summary>Stable error code of a refusal (e.g. <c>UnknownTool</c>, <c>AgentNotFound</c>), null on success.</summary>
    public string? ErrorCode { get; set; }

    /// <summary>The agent after the call.</summary>
    public AgentDto? Agent { get; set; }

    /// <summary>Roles assigned to the service-account client by this call.</summary>
    public List<string> AssignedRoles { get; set; } = [];
}

/// <summary>Response of <c>list_agents</c>.</summary>
public class ListAgentsResponse : IdentityResponse
{
    /// <summary>The agents of the tenant.</summary>
    public List<AgentDto> Agents { get; set; } = [];

    /// <summary>Number of agents returned.</summary>
    public int TotalCount { get; set; }
}
