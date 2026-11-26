using System.Collections.Frozen;
using System.Reflection;
using Meshmakers.Octo.Backend.McpServices.Tools;
using ModelContextProtocol.Server;

namespace Meshmakers.Octo.Backend.McpServices.Services.Agents;

/// <summary>
///     AB#9729: the tools an agent never runs, whatever its mandate grants and whatever roles its client holds: every tool
///     of the identity administration (clients, client secrets, API scopes and resources, roles, users, groups, identity
///     providers, external user mappings, e-mail domain rules, cross-tenant grants, client overlays, admin provisioning),
///     the agent tools, and the tenant dump/restore and secret maintenance tools (a restore of an old dump would bring
///     back an earlier, broader mandate or client), and the LLM spend cap tools (an agent never raises its own budget). With any of them an agent could widen its own client (add
///     <c>octo_api</c>, roles or a secret), take over the exchange client <c>octo-mcpServices-agent-exchange</c> or
///     create a client outside the mandate check, and so raise its own mandate. The whole tool class counts, reads included, so a new tool of one of
///     these classes is covered without a list to maintain.
/// </summary>
internal static class AgentForbiddenTools
{
    /// <summary>The tool classes whose tools are refused for agents.</summary>
    internal static readonly IReadOnlyList<Type> ToolTypes =
    [
        typeof(AgentTools),
        typeof(ClientManagementTools),
        typeof(ApiSecretTools),
        typeof(ApiScopeTools),
        typeof(ApiResourceTools),
        typeof(RoleManagementTools),
        typeof(UserManagementTools),
        typeof(GroupManagementTools),
        typeof(IdentityProviderTools),
        typeof(ExternalTenantUserMappingTools),
        typeof(EmailDomainGroupRuleTools),
        typeof(CrossTenantGrantPermissionTools),
        typeof(ClientOverlayTools),
        typeof(AdminProvisioningTools),
        typeof(TenantBackupTools),
        typeof(SecretMaintenanceTools),
        typeof(AiSpendTools)
    ];

    private static readonly Lazy<FrozenSet<string>> Names = new(() => ToolTypes
        .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static |
                                      BindingFlags.Instance | BindingFlags.DeclaredOnly))
        .Select(m => m.GetCustomAttribute<McpServerToolAttribute>()?.Name)
        .Where(n => !string.IsNullOrEmpty(n))
        .Select(n => n!)
        .ToFrozenSet(StringComparer.Ordinal));

    /// <summary>Every tool name of <see cref="ToolTypes" />.</summary>
    internal static IReadOnlySet<string> All => Names.Value;

    /// <summary>True when an agent may never run <paramref name="toolName" />.</summary>
    internal static bool Contains(string toolName) => Names.Value.Contains(toolName);
}
