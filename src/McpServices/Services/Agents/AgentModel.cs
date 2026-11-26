using System.Globalization;
using System.Text.Json;
using Meshmakers.Octo.Backend.McpServices.Models;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;

namespace Meshmakers.Octo.Backend.McpServices.Services.Agents;

/// <summary>The mandate values of a create or update call after validation.</summary>
internal sealed record MandateValues(
    IReadOnlyList<AgentToolGrantDto> Grants,
    int RiskCeiling,
    int ChangeBudgetPerRun,
    int WriteMode,
    IReadOnlyList<string>? AllowedCkTypes);

/// <summary>
///     The System.Ai agent runtime types (<c>Agent</c>, <c>Mandate</c>, record <c>AgentToolGrant</c>, role
///     <c>GovernedBy</c>, System.Ai 4.12.0) as the agent tools read and write them: names, enum values, validation of
///     mandate grants against the tools of this server, entity building and mapping to the response DTOs.
///     The engine stores no defaults inside records, so a grant without <c>MaxRisk</c> reads as Low; the tools always
///     write every mandate value explicitly.
/// </summary>
internal static class AgentModel
{
    public const string AgentType = "System.Ai/Agent";
    public const string MandateType = "System.Ai/Mandate";
    public const string ToolGrantRecord = "System.Ai/AgentToolGrant";
    public const string GovernedByRole = "System.Ai/GovernedBy";

    /// <summary>
    ///     The in-process write channel of the agent tools (AB#9729). System.Ai 4.12.0 declares
    ///     <c>writeAccess { channels: [System.Ai/AgentAdministration] }</c> on <c>Agent</c> and <c>Mandate</c>, so only a
    ///     session carrying it (and system sessions) may write them.
    /// </summary>
    public const string WriteChannel = "System.Ai/AgentAdministration";

    /// <summary>Upper bound of the agents and mandates read per call (a tenant holds a handful).</summary>
    public const int MaxAgents = 500;

    public const int MaxGrants = 200;

    public static readonly string[] RiskNames = ["Low", "Medium", "High"];
    public static readonly string[] AutonomyNames = ["ProposeOnly", "ActWithinBudget"];
    public static readonly string[] RunnerNames = ["AgentSdk", "ClaudeCodeCli"];
    public static readonly string[] WriteModeNames = ["Propose", "Direct"];

    /// <summary>The narrow default mandate: no grants, risk Low only, no direct change, every write a proposal.</summary>
    public static MandateValues DefaultMandate => new([], 0, 0, 0, null);

    public static RtCkId<CkTypeId> AgentTypeId => new(AgentType);
    public static RtCkId<CkTypeId> MandateTypeId => new(MandateType);
    public static RtCkId<CkAssociationRoleId> GovernedByRoleId => new(GovernedByRole);

    /// <summary>Index of <paramref name="value" /> in <paramref name="names" /> (case-insensitive), or null.</summary>
    public static int? Parse(string? value, string[] names)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var index = Array.FindIndex(names, n => string.Equals(n, value.Trim(), StringComparison.OrdinalIgnoreCase));
        return index < 0 ? null : index;
    }

    public static string Name(string[] names, int value) =>
        value >= 0 && value < names.Length ? names[value] : value.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    ///     Validates the grants of a mandate against the tools of this server and the mandate's risk ceiling. A tool name
    ///     must name a tool of this server; a prefix (<c>knowledge_*</c>) must match at least one. Returns the first
    ///     refusal (code, message naming the grant) or the normalised grants.
    /// </summary>
    public static (IReadOnlyList<AgentToolGrantDto>? Grants, string? Code, string? Error) ValidateGrants(
        IReadOnlyList<AgentToolGrantDto>? grants, int riskCeiling, IReadOnlyCollection<string> toolNames)
    {
        var result = new List<AgentToolGrantDto>();
        if (grants == null)
        {
            return (result, null, null);
        }

        if (grants.Count > MaxGrants)
        {
            return (null, "InvalidArgument", $"A mandate holds at most {MaxGrants} tool grants.");
        }

        foreach (var grant in grants)
        {
            var pattern = grant?.ToolPattern?.Trim() ?? string.Empty;
            if (pattern.Length == 0)
            {
                return (null, "InvalidArgument", "Every tool grant needs a toolPattern (a tool name or a prefix ending in '*').");
            }

            var star = pattern.IndexOf('*', StringComparison.Ordinal);
            if (star >= 0)
            {
                if (star != pattern.Length - 1 || pattern.Length == 1)
                {
                    return (null, "InvalidArgument",
                        $"Tool grant '{pattern}': '*' is allowed only at the end of a prefix (e.g. 'knowledge_*'); " +
                        "a grant of every tool ('*') is not allowed.");
                }

                var prefix = pattern[..^1];
                if (!toolNames.Any(t => t.StartsWith(prefix, StringComparison.Ordinal)))
                {
                    return (null, "UnknownTool",
                        $"Tool grant '{pattern}' matches no tool of this MCP server: no tool name starts with '{prefix}'.");
                }
            }
            else if (!toolNames.Contains(pattern))
            {
                return (null, "UnknownTool",
                    $"Tool grant names the tool '{pattern}', which this MCP server does not have. Use a name from tools/list.");
            }

            var maxRisk = 0;
            if (!string.IsNullOrWhiteSpace(grant!.MaxRisk))
            {
                var parsed = Parse(grant.MaxRisk, RiskNames);
                if (parsed == null)
                {
                    return (null, "InvalidArgument",
                        $"Tool grant '{pattern}': unknown maxRisk '{grant.MaxRisk}'; use Low, Medium or High.");
                }

                maxRisk = parsed.Value;
            }

            if (maxRisk > riskCeiling)
            {
                return (null, "RiskAboveCeiling",
                    $"Tool grant '{pattern}' admits {RiskNames[maxRisk]}, above the mandate's risk ceiling " +
                    $"{RiskNames[riskCeiling]}. Lower the grant or raise riskCeiling.");
            }

            string? constraints = null;
            if (!string.IsNullOrWhiteSpace(grant.ArgumentConstraints))
            {
                try
                {
                    using var document = JsonDocument.Parse(grant.ArgumentConstraints);
                    if (document.RootElement.ValueKind != JsonValueKind.Object)
                    {
                        return (null, "InvalidArgument",
                            $"Tool grant '{pattern}': argumentConstraints must be a JSON object.");
                    }
                }
                catch (JsonException)
                {
                    return (null, "InvalidArgument", $"Tool grant '{pattern}': argumentConstraints is not valid JSON.");
                }

                constraints = grant.ArgumentConstraints.Trim();
            }

            result.Add(new AgentToolGrantDto { ToolPattern = pattern, MaxRisk = RiskNames[maxRisk], ArgumentConstraints = constraints });
        }

        return (result, null, null);
    }

    /// <summary>Checks the CK type ids of <c>allowedCkTypes</c> for the form <c>Model/Type</c>.</summary>
    public static string? ValidateCkTypes(IReadOnlyList<string>? ckTypes)
    {
        if (ckTypes == null)
        {
            return null;
        }

        var bad = ckTypes.FirstOrDefault(t => string.IsNullOrWhiteSpace(t) || t.Trim().Split('/').Length != 2 ||
                                              t.Trim().Split('/').Any(string.IsNullOrWhiteSpace));
        return bad == null ? null : $"allowedCkTypes: '{bad}' is no CK type id of the form 'Model/Type'.";
    }

    /// <summary>The default service-account client id of an agent: <c>agent-</c> plus the name in lower-case words.</summary>
    public static string DefaultClientId(string name)
    {
        var chars = name.Trim().ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray();
        var slug = string.Join('-', new string(chars).Split('-', StringSplitOptions.RemoveEmptyEntries));
        if (slug.Length > 60)
        {
            slug = slug[..60].TrimEnd('-');
        }

        return "agent-" + (slug.Length == 0 ? "unnamed" : slug);
    }

    /// <summary>A client id may hold ASCII letters, digits, '-', '_' and '.' (2-100 characters).</summary>
    public static bool IsValidClientId(string clientId) =>
        clientId.Length is >= 2 and <= 100 &&
        clientId.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');

    public static void SetMandate(RtEntity mandate, MandateValues values)
    {
        mandate.SetAttributeValue("ToolGrants", AttributeValueTypesDto.RecordArray, values.Grants
            .Select(g => new RtRecord(new RtCkId<CkRecordId>(ToolGrantRecord), GrantAttributes(g))).ToList());
        mandate.SetAttributeValue("RiskCeiling", AttributeValueTypesDto.Enum, values.RiskCeiling);
        mandate.SetAttributeValue("ChangeBudgetPerRun", AttributeValueTypesDto.Int, values.ChangeBudgetPerRun);
        mandate.SetAttributeValue("WriteMode", AttributeValueTypesDto.Enum, values.WriteMode);
        mandate.SetAttributeValue("AllowedCkTypes", AttributeValueTypesDto.StringArray,
            values.AllowedCkTypes?.Select(t => t.Trim()).ToList() ?? []);
    }

    private static Dictionary<string, object?> GrantAttributes(AgentToolGrantDto grant)
    {
        // MaxRisk is always written: the engine materialises no record defaults.
        var attributes = new Dictionary<string, object?>
        {
            ["ToolPattern"] = grant.ToolPattern,
            ["MaxRisk"] = Parse(grant.MaxRisk, RiskNames) ?? 0
        };
        if (grant.ArgumentConstraints != null)
        {
            attributes["ArgumentConstraints"] = grant.ArgumentConstraints;
        }

        return attributes;
    }

    /// <summary>The current mandate values of a stored mandate (missing values read as the CK defaults).</summary>
    public static MandateValues ReadMandate(RtEntity mandate)
    {
        var allowed = ReadStrings(mandate, "AllowedCkTypes");
        return new MandateValues(ReadGrants(mandate), ToInt(mandate.GetAttributeValueOrDefault("RiskCeiling")),
            ToInt(mandate.GetAttributeValueOrDefault("ChangeBudgetPerRun")),
            ToInt(mandate.GetAttributeValueOrDefault("WriteMode")), allowed.Count == 0 ? null : allowed);
    }

    public static AgentMandateDto ToMandateDto(RtEntity mandate, int governedAgents)
    {
        var values = ReadMandate(mandate);
        return new AgentMandateDto
        {
            RtId = mandate.RtId.ToString(),
            Name = mandate.GetAttributeValueOrDefault("Name")?.ToString(),
            ToolGrants = values.Grants.ToList(),
            RiskCeiling = Name(RiskNames, values.RiskCeiling),
            ChangeBudgetPerRun = values.ChangeBudgetPerRun,
            WriteMode = Name(WriteModeNames, values.WriteMode),
            AllowedCkTypes = values.AllowedCkTypes?.ToList(),
            GovernedAgentCount = governedAgents
        };
    }

    public static AgentDto ToAgentDto(RtEntity agent, AgentMandateDto? mandate) => new()
    {
        RtId = agent.RtId.ToString(),
        Name = agent.GetAttributeValueOrDefault("Name")?.ToString(),
        Instructions = agent.GetAttributeValueOrDefault("Instructions")?.ToString(),
        Version = Math.Max(1, ToInt(agent.GetAttributeValueOrDefault("Version"))),
        Enabled = agent.GetAttributeValueOrDefault("Enabled") is not false,
        AutonomyLevel = Name(AutonomyNames, ToInt(agent.GetAttributeValueOrDefault("AutonomyLevel"))),
        RunnerKind = Name(RunnerNames, ToInt(agent.GetAttributeValueOrDefault("RunnerKind"))),
        Model = NullIfEmpty(agent.GetAttributeValueOrDefault("Model")?.ToString()),
        SponsorUserId = NullIfEmpty(agent.GetAttributeValueOrDefault("SponsorUserId")?.ToString()),
        ServiceAccountClientId = NullIfEmpty(agent.GetAttributeValueOrDefault("ServiceAccountClientId")?.ToString()),
        Mandate = mandate
    };

    public static string ClientIdOf(RtEntity agent) =>
        agent.GetAttributeValueOrDefault("ServiceAccountClientId")?.ToString() ?? string.Empty;

    public static int VersionOf(RtEntity agent) => Math.Max(1, ToInt(agent.GetAttributeValueOrDefault("Version")));

    /// <summary>All agents of the tenant (at most <see cref="MaxAgents" />).</summary>
    public static async Task<IReadOnlyList<RtEntity>> ListAgentsAsync(ITenantRepository repository, IOctoSession session)
    {
        var result = await repository.GetRtEntitiesByTypeAsync(session, AgentTypeId, RtEntityQueryOptions.Create(), 0,
            MaxAgents);
        return result.Items.ToList();
    }

    /// <summary>The agent named by runtime id or by name (case-insensitive), or null.</summary>
    public static async Task<RtEntity?> FindAgentAsync(ITenantRepository repository, IOctoSession session, string agent)
    {
        var key = agent.Trim();
        if (OctoObjectId.TryParse(key, out var rtId))
        {
            var byId = await repository.GetRtEntityByRtIdAsync(session, new RtEntityId(AgentTypeId, rtId));
            if (byId != null)
            {
                return byId;
            }
        }

        var agents = await ListAgentsAsync(repository, session);
        return agents.FirstOrDefault(a =>
            string.Equals(a.GetAttributeValueOrDefault("Name")?.ToString(), key, StringComparison.OrdinalIgnoreCase));
    }

    public static async Task<RtEntity?> GetMandateAsync(ITenantRepository repository, IOctoSession session,
        OctoObjectId mandateRtId) =>
        await repository.GetRtEntityByRtIdAsync(session, new RtEntityId(MandateTypeId, mandateRtId));

    /// <summary>The runtime id of the mandate that governs <paramref name="agentRtId" />, or null.</summary>
    public static async Task<OctoObjectId?> GoverningMandateIdAsync(ITenantRepository repository, IOctoSession session,
        OctoObjectId agentRtId)
    {
        var associations = await repository.GetRtAssociationsAsync(session, new RtEntityId(AgentTypeId, agentRtId),
            RtAssociationExtendedQueryOptions.Create(GraphDirections.Outbound, GovernedByRoleId));
        return associations.Items.Select(a => (OctoObjectId?)a.TargetRtId).FirstOrDefault();
    }

    /// <summary>The runtime ids of every mandate that governs <paramref name="agentRtId" /> (the mandate check needs exactly one).</summary>
    public static async Task<IReadOnlyList<OctoObjectId>> GoverningMandateIdsAsync(ITenantRepository repository,
        IOctoSession session, OctoObjectId agentRtId)
    {
        var associations = await repository.GetRtAssociationsAsync(session, new RtEntityId(AgentTypeId, agentRtId),
            RtAssociationExtendedQueryOptions.Create(GraphDirections.Outbound, GovernedByRoleId));
        return associations.Items.Select(a => a.TargetRtId).Distinct().ToList();
    }

    /// <summary>
    ///     The stored grants of a mandate as pattern and risk index, without normalising (the mandate check judges them).
    ///     An absent <c>MaxRisk</c> reads as 0 (Low): the engine stores no record defaults.
    /// </summary>
    public static IReadOnlyList<(string? Pattern, int MaxRisk)> ReadRawGrants(RtEntity mandate)
    {
        var records = mandate.GetAttributeValueOrDefault("ToolGrants") is IEnumerable<RtRecord> list
            ? list
            : (IEnumerable<RtRecord>?)mandate.GetRtRecordAttributeValuesOrDefault<RtRecord>("ToolGrants") ?? [];
        return records.Select(r => (Val(r, "ToolPattern")?.ToString(), ToInt(Val(r, "MaxRisk")))).ToList();
    }

    /// <summary>Number of agents a mandate governs.</summary>
    public static async Task<int> GovernedAgentCountAsync(ITenantRepository repository, IOctoSession session,
        OctoObjectId mandateRtId)
    {
        var associations = await repository.GetRtAssociationsAsync(session, new RtEntityId(MandateTypeId, mandateRtId),
            RtAssociationExtendedQueryOptions.Create(GraphDirections.Inbound, GovernedByRoleId));
        return associations.Items.Count();
    }

    /// <summary>The mandate DTO of an agent (null when it has none).</summary>
    public static async Task<AgentMandateDto?> MandateOfAsync(ITenantRepository repository, IOctoSession session,
        RtEntity agent)
    {
        var mandateId = await GoverningMandateIdAsync(repository, session, agent.RtId);
        if (mandateId == null)
        {
            return null;
        }

        var mandate = await GetMandateAsync(repository, session, mandateId.Value);
        return mandate == null
            ? null
            : ToMandateDto(mandate, await GovernedAgentCountAsync(repository, session, mandateId.Value));
    }

    private static List<AgentToolGrantDto> ReadGrants(RtEntity mandate)
    {
        var records = mandate.GetAttributeValueOrDefault("ToolGrants") is IEnumerable<RtRecord> list
            ? list
            : (IEnumerable<RtRecord>?)mandate.GetRtRecordAttributeValuesOrDefault<RtRecord>("ToolGrants") ?? [];
        return records.Select(r => new AgentToolGrantDto
        {
            ToolPattern = Val(r, "ToolPattern")?.ToString() ?? string.Empty,
            // A grant stored without MaxRisk admits Low only (record defaults are not materialised).
            MaxRisk = Name(RiskNames, ToInt(Val(r, "MaxRisk"))),
            ArgumentConstraints = NullIfEmpty(Val(r, "ArgumentConstraints")?.ToString())
        }).ToList();
    }

    private static List<string> ReadStrings(RtEntity entity, string attributeName) =>
        entity.GetAttributeValueOrDefault(attributeName) switch
        {
            IEnumerable<string> strings => strings.ToList(),
            IEnumerable<object> objects => objects.Select(o => o?.ToString()).OfType<string>().ToList(),
            _ => []
        };

    private static object? Val(RtRecord record, string name) =>
        record.Attributes.TryGetValue(name, out var value) ? value : null;

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;

    internal static int ToInt(object? value) => value switch
    {
        null => 0,
        int i => i,
        long l => (int)l,
        Enum e => Convert.ToInt32(e, CultureInfo.InvariantCulture),
        _ => int.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0
    };
}
