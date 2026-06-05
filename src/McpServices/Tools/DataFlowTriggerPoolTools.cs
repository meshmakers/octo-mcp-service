using System.ComponentModel;
using Meshmakers.Octo.Backend.McpServices.Models;
using Meshmakers.Octo.Backend.McpServices.Services;
using ModelContextProtocol.Server;

// ReSharper disable UnusedMember.Global

namespace Meshmakers.Octo.Backend.McpServices.Tools;

/// <summary>
///     Data Flow + Trigger + Pool tools (Communication Controller). Mirrors the smaller octo-cli commands.
///     Bundled into one class because each subsystem only has 1–3 operations.
/// </summary>
[McpServerToolType]
public sealed class DataFlowTriggerPoolTools
{
    // ── Data Flows ──────────────────────────────────────────────────────────

    /// <summary>Deploy a data flow.</summary>
    [McpServerTool(Name = "deploy_data_flow")]
    [Description("Deploy a data flow. Equivalent to octo-cli DeployDataFlow.")]
    public static async Task<CommunicationActionResponse> DeployDataFlow(
        McpServer server,
        [Description("Data flow runtime ID.")] string dataFlowId,
        [Description("Tenant to operate on. Falls back to URL route.")] string? tenantId = null)
        => await SingleResourceAction(server, tenantId, dataFlowId,
            requiredConfirm: false, confirm: true,
            (client, id) => client.DeployDataFlowAsync(id),
            successMessage: id => $"Data flow '{id}' deployed.");

    /// <summary>Undeploy a data flow. Destructive: requires confirm.</summary>
    [McpServerTool(Name = "undeploy_data_flow")]
    [Description(
        "Undeploy a data flow. DESTRUCTIVE — stops the data flow until re-deployed. Requires confirm=true. " +
        "Equivalent to octo-cli UndeployDataFlow.")]
    public static async Task<CommunicationActionResponse> UndeployDataFlow(
        McpServer server,
        [Description("Data flow runtime ID.")] string dataFlowId,
        [Description("Must be true to actually undeploy.")] bool confirm = false,
        [Description("Tenant to operate on. Falls back to URL route.")] string? tenantId = null)
        => await SingleResourceAction(server, tenantId, dataFlowId,
            requiredConfirm: true, confirm,
            (client, id) => client.UndeployDataFlowAsync(id),
            successMessage: id => $"Data flow '{id}' undeployed.");

    /// <summary>Get aggregated execution status of a data flow.</summary>
    [McpServerTool(Name = "get_data_flow_status")]
    [Description("Get the aggregated execution status of a data flow. Equivalent to octo-cli GetDataFlowStatus.")]
    public static async Task<GetDataFlowStatusResponse> GetDataFlowStatus(
        McpServer server,
        [Description("Data flow runtime ID.")] string dataFlowId,
        [Description("Tenant to operate on. Falls back to URL route.")] string? tenantId = null)
    {
        if (string.IsNullOrWhiteSpace(dataFlowId))
        {
            return new GetDataFlowStatusResponse { IsSuccess = false, ErrorMessage = "dataFlowId is required." };
        }

        var ctx = CommunicationClientContext.TryBuild(server, tenantId);
        if (ctx.Error != null)
        {
            return new GetDataFlowStatusResponse { IsSuccess = false, ErrorMessage = ctx.Error };
        }

        try
        {
            var status = await ctx.Client!.GetDataFlowStatusAsync(dataFlowId);
            return new GetDataFlowStatusResponse
            {
                IsSuccess = true,
                TenantId = ctx.TenantId,
                DataFlowId = dataFlowId,
                Status = status
            };
        }
        catch (Exception ex)
        {
            return new GetDataFlowStatusResponse { IsSuccess = false, ErrorMessage = ex.Message };
        }
    }

    // ── Triggers ────────────────────────────────────────────────────────────

    /// <summary>Deploy all pipeline triggers for the tenant.</summary>
    [McpServerTool(Name = "deploy_triggers")]
    [Description("Deploy all pipeline triggers for the tenant. Equivalent to octo-cli DeployTriggers.")]
    public static async Task<CommunicationResponse> DeployTriggers(
        McpServer server,
        [Description("Tenant to operate on. Falls back to URL route.")] string? tenantId = null)
    {
        var ctx = CommunicationClientContext.TryBuild(server, tenantId);
        if (ctx.Error != null)
        {
            return new CommunicationResponse { IsSuccess = false, ErrorMessage = ctx.Error };
        }

        try
        {
            await ctx.Client!.DeployTriggersAsync();
            return new CommunicationResponse
            {
                IsSuccess = true,
                TenantId = ctx.TenantId,
                Message = $"Triggers deployed for tenant '{ctx.TenantId}'."
            };
        }
        catch (Exception ex)
        {
            return new CommunicationResponse { IsSuccess = false, ErrorMessage = ex.Message };
        }
    }

    /// <summary>Undeploy all pipeline triggers for the tenant. Destructive: requires confirm.</summary>
    [McpServerTool(Name = "undeploy_triggers")]
    [Description(
        "Undeploy all pipeline triggers for the tenant. DESTRUCTIVE — pipelines stop firing on their triggers. " +
        "Requires confirm=true. Equivalent to octo-cli UndeployTriggers.")]
    public static async Task<CommunicationResponse> UndeployTriggers(
        McpServer server,
        [Description("Must be true to actually undeploy.")] bool confirm = false,
        [Description("Tenant to operate on. Falls back to URL route.")] string? tenantId = null)
    {
        if (!confirm)
        {
            return new CommunicationResponse
            {
                IsSuccess = false,
                ErrorMessage = "Refusing to undeploy triggers without confirm=true."
            };
        }

        var ctx = CommunicationClientContext.TryBuild(server, tenantId);
        if (ctx.Error != null)
        {
            return new CommunicationResponse { IsSuccess = false, ErrorMessage = ctx.Error };
        }

        try
        {
            await ctx.Client!.UndeployTriggersAsync();
            return new CommunicationResponse
            {
                IsSuccess = true,
                TenantId = ctx.TenantId,
                Message = $"Triggers undeployed for tenant '{ctx.TenantId}'."
            };
        }
        catch (Exception ex)
        {
            return new CommunicationResponse { IsSuccess = false, ErrorMessage = ex.Message };
        }
    }

    // ── Pools ───────────────────────────────────────────────────────────────

    /// <summary>List all pools for the tenant.</summary>
    [McpServerTool(Name = "get_pools")]
    [Description("List all pools configured for the tenant. Equivalent to octo-cli GetPools.")]
    public static async Task<GetPoolsResponse> GetPools(
        McpServer server,
        [Description("Tenant to operate on. Falls back to URL route.")] string? tenantId = null)
    {
        var ctx = CommunicationClientContext.TryBuild(server, tenantId);
        if (ctx.Error != null)
        {
            return new GetPoolsResponse { IsSuccess = false, ErrorMessage = ctx.Error };
        }

        try
        {
            var pools = (await ctx.Client!.GetPoolsAsync()).ToList();
            return new GetPoolsResponse
            {
                IsSuccess = true,
                TenantId = ctx.TenantId,
                Pools = pools,
                TotalCount = pools.Count,
                Message = pools.Count == 0 ? "No pools configured." : $"{pools.Count} pool(s)."
            };
        }
        catch (Exception ex)
        {
            return new GetPoolsResponse { IsSuccess = false, ErrorMessage = ex.Message };
        }
    }

    private static async Task<CommunicationActionResponse> SingleResourceAction(
        McpServer server,
        string? tenantId,
        string resourceId,
        bool requiredConfirm,
        bool confirm,
        Func<Sdk.ServiceClient.CommunicationControllerServices.ICommunicationServicesClient, string, Task> action,
        Func<string, string> successMessage)
    {
        if (string.IsNullOrWhiteSpace(resourceId))
        {
            return new CommunicationActionResponse { IsSuccess = false, ErrorMessage = "resource id is required." };
        }

        if (requiredConfirm && !confirm)
        {
            return new CommunicationActionResponse
            {
                IsSuccess = false,
                ErrorMessage = $"Refusing to act on '{resourceId}' without confirm=true."
            };
        }

        var ctx = CommunicationClientContext.TryBuild(server, tenantId);
        if (ctx.Error != null)
        {
            return new CommunicationActionResponse { IsSuccess = false, ErrorMessage = ctx.Error };
        }

        try
        {
            await action(ctx.Client!, resourceId);
            return new CommunicationActionResponse
            {
                IsSuccess = true,
                TenantId = ctx.TenantId,
                ResourceId = resourceId,
                Message = successMessage(resourceId)
            };
        }
        catch (Exception ex)
        {
            return new CommunicationActionResponse { IsSuccess = false, ErrorMessage = ex.Message };
        }
    }
}
