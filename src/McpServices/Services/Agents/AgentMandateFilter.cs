using System.Text.Json;
using Meshmakers.Octo.Backend.McpServices.Models;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Services.Infrastructure.Services;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Meshmakers.Octo.Backend.McpServices.Services.Agents;

/// <summary>
///     The policy enforcement point of AB#9729: a <c>tools/call</c> request filter in front of every tool of this server
///     (static, dynamic and future ones alike). It runs <see cref="AgentMandateGuard" /> for the validated request
///     principal; a refusal returns an error result with the code and reason and the tool never runs. Callers that are
///     no agent go straight to the tool. Every decision about an agent caller is logged (client id, tenant, tool, agent,
///     outcome, code; refusals at Warning; no token content, no arguments).
/// </summary>
internal static class AgentMandateFilter
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>The filter, registered by <c>McpServerSetup.WithOctoCapabilities</c>.</summary>
    public static McpRequestHandler<CallToolRequestParams, CallToolResult> Filter(
        McpRequestHandler<CallToolRequestParams, CallToolResult> next) =>
        async (context, cancellationToken) =>
        {
            var toolName = context.Params?.Name ?? string.Empty;
            var services = context.Server.Services;
            var caller = McpCallerPrincipal.FromServer(context.Server);

            var decision = await AgentMandateGuard.CheckAsync(caller, toolName, RiskOf(services, toolName),
                tenantId => FindTenantRepositoryAsync(services, tenantId), () => CkCache(services));

            if (decision.AgentName != null || !decision.Allowed)
            {
                Log(services, caller, toolName, decision);
            }

            return decision.Allowed
                ? await next(context, cancellationToken)
                : Refusal(toolName, decision);
        };

    /// <summary>
    ///     The name under which a <c>resources/read</c> is judged. No mandate grants resources: an agent's client and every
    ///     token with the agent scope are refused, whatever the mandate grants (AB#9729). The resources read tenant data
    ///     (knowledge pages, CK schema) with the caller's roles; an agent reads knowledge through the tools its mandate
    ///     grants (<c>knowledge_get</c>, ...), so the kill switch and the grants hold for every read.
    /// </summary>
    internal const string ReadResourceName = "resources/read";

    /// <summary>The <c>resources/read</c> filter, registered by <c>McpServerSetup.WithOctoCapabilities</c>.</summary>
    public static McpRequestHandler<ReadResourceRequestParams, ReadResourceResult> ReadResourceFilter(
        McpRequestHandler<ReadResourceRequestParams, ReadResourceResult> next) =>
        async (context, cancellationToken) =>
        {
            var services = context.Server.Services;
            var caller = McpCallerPrincipal.FromServer(context.Server);

            var decision = await JudgeResourceReadAsync(caller,
                tenantId => FindTenantRepositoryAsync(services, tenantId), () => CkCache(services));
            if (!decision.Allowed)
            {
                Log(services, caller, ReadResourceName, decision);
                throw new McpProtocolException($"{decision.Code}: {decision.Message}", McpErrorCode.InvalidRequest);
            }

            return await next(context, cancellationToken);
        };

    /// <summary>
    ///     Judges a <c>resources/read</c>: refused for every caller the mandate check knows as an agent (enabled or not)
    ///     and for every refusal of the check itself (agent scope on a non-agent, fail closed); others pass.
    /// </summary>
    internal static async Task<AgentMandateDecision> JudgeResourceReadAsync(McpCallerPrincipal? caller,
        Func<string, Task<ITenantRepository>> tenantRepository, Func<ICkCacheService> ckCache)
    {
        var decision = await AgentMandateGuard.CheckAsync(caller, ReadResourceName, McpRiskLevel.High, tenantRepository,
            ckCache);
        // A refusal before the grants are judged (AgentDisabled, AgentScopeMissing, NotAnAgent, MandateUnavailable) keeps
        // its code; past that point no grant can admit a resource read.
        if (!decision.Allowed && decision.Code is not (AgentMandateGuard.ToolNotGranted or AgentMandateGuard.RiskAboveCeiling))
        {
            return decision;
        }

        return decision.AgentName == null
            ? decision
            : AgentMandateDecision.Refuse(AgentMandateGuard.ForbiddenForAgents,
                $"Agent '{decision.AgentName}' reads no MCP resources; it reads knowledge through the tools its mandate " +
                "grants. The read is refused.", decision.AgentName);
    }

    /// <summary>The error result of a refused call: the code and reason as JSON text, <c>isError</c> set.</summary>
    internal static CallToolResult Refusal(string toolName, AgentMandateDecision decision) => new()
    {
        IsError = true,
        Content =
        [
            new TextContentBlock
            {
                Text = JsonSerializer.Serialize(new
                {
                    isSuccess = false,
                    errorCode = decision.Code,
                    errorMessage = decision.Message,
                    tool = toolName,
                    agent = decision.AgentName
                }, JsonOptions)
            }
        ]
    };

    /// <summary>The tool's risk class; High (fail closed) when the risk registry is not available.</summary>
    private static McpRiskLevel RiskOf(IServiceProvider? services, string toolName) =>
        services?.GetService<IToolRiskRegistry>()?.GetRiskLevel(toolName) ?? McpRiskLevel.High;

    private static ICkCacheService CkCache(IServiceProvider? services) =>
        services?.GetService<ICkCacheService>() ??
        throw new InvalidOperationException("No CK cache service registered.");

    private static async Task<ITenantRepository> FindTenantRepositoryAsync(IServiceProvider? services, string tenantId)
    {
        var systemContext = services?.GetService<ISystemContext>() ??
                            throw new InvalidOperationException("No system context registered.");
        return await systemContext.FindTenantRepositoryAsync(tenantId);
    }

    private static void Log(IServiceProvider? services, McpCallerPrincipal? caller, string toolName,
        AgentMandateDecision decision)
    {
        var logger = services?.GetService<ILoggerFactory>()?.CreateLogger(typeof(AgentMandateFilter).FullName!);
        if (logger == null)
        {
            return;
        }

        if (decision.Allowed)
        {
            logger.LogInformation(
                "Agent mandate check: allowed tool {Tool} for agent {Agent} (client {ClientId}, tenant {TenantId})",
                toolName, decision.AgentName, caller?.ClientId, caller?.TenantId);
        }
        else
        {
            logger.LogWarning(
                "Agent mandate check: refused tool {Tool} with {Code} for agent {Agent} (client {ClientId}, tenant {TenantId})",
                toolName, decision.Code, decision.AgentName, caller?.ClientId, caller?.TenantId);
        }
    }
}
