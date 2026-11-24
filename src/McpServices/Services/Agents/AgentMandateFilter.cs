using System.Text.Json;
using Meshmakers.Octo.Backend.McpServices.Models;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Services.Infrastructure.Services;
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
