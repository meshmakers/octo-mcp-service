using System.ComponentModel;
using Meshmakers.Octo.Backend.McpServices.Models;
using Meshmakers.Octo.Backend.McpServices.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using ModelContextProtocol.Server;

// ReSharper disable UnusedMember.Global

namespace Meshmakers.Octo.Backend.McpServices.Tools;

/// <summary>
///     Data Flow + Trigger + Pool tools (Communication Controller), including the adapter pool queue
///     (AB#4924 §10). Mirrors the smaller octo-cli commands. Bundled into one class because each
///     subsystem only has 1–3 operations.
/// </summary>
[McpServerToolType]
public sealed class DataFlowTriggerPoolTools
{
    // ── Data Flows ──────────────────────────────────────────────────────────

    /// <summary>Deploy a data flow.</summary>
    [McpServerTool(Name = "deploy_data_flow")]
    [McpRisk(McpRiskLevel.High)]
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
    [McpRisk(McpRiskLevel.High)]
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

        var ctx = await CommunicationClientContext.TryBuildAsync(server, tenantId);
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
    [McpRisk(McpRiskLevel.High)]
    [Description("Deploy all pipeline triggers for the tenant. Equivalent to octo-cli DeployTriggers.")]
    public static async Task<CommunicationResponse> DeployTriggers(
        McpServer server,
        [Description("Tenant to operate on. Falls back to URL route.")] string? tenantId = null)
    {
        var ctx = await CommunicationClientContext.TryBuildAsync(server, tenantId);
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
    [McpRisk(McpRiskLevel.High)]
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

        var ctx = await CommunicationClientContext.TryBuildAsync(server, tenantId);
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
        var ctx = await CommunicationClientContext.TryBuildAsync(server, tenantId);
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

    /// <summary>Undeploy a pool. Destructive: requires confirm.</summary>
    [McpServerTool(Name = "undeploy_pool")]
    [McpRisk(McpRiskLevel.High)]
    [Description(
        "Undeploy a pool. DESTRUCTIVE - the Communication Operator removes the pool's cluster resources; " +
        "undeploy its workloads first (undeploy_workload). Requires confirm=true. Needed before " +
        "disable_communication, which is refused while pools or workloads are deployed. Equivalent to " +
        "octo-cli UndeployPool.")]
    public static async Task<CommunicationActionResponse> UndeployPool(
        McpServer server,
        [Description("Pool runtime ID.")] string poolId,
        [Description("Must be true to actually undeploy.")] bool confirm = false,
        [Description("Tenant to operate on. Falls back to URL route.")] string? tenantId = null)
        => await SingleResourceAction(server, tenantId, poolId,
            requiredConfirm: true, confirm,
            (client, id) => client.UndeployPoolAsync(id),
            successMessage: id => $"Pool '{id}' undeploy triggered.");

    // ── Adapter pool queue (AB#4924 §10) ────────────────────────────────────

    /// <summary>The queue of one adapter pool: what waits for a lease and what is leased right now.</summary>
    [McpServerTool(Name = "get_adapter_pool_queue")]
    [Description(
        "Show an adapter pool's queue: every execution waiting for a lease, plus the executions the pool " +
        "currently has leased out. Read-only. The tenant is the LENDING tenant that owns the pool; each entry " +
        "names the borrowing tenant whose work it is. POSITION IS REPORTED AS A PAIR — positionInTenant (where " +
        "the item sits in its own tenant's queue) and tenantsAheadInRotation (how many other tenants take a turn " +
        "first) — because the pool serves tenants round-robin and there is no single global rank; do not compute " +
        "one. Entries with leasedOnMemberId set are already running and cannot be cancelled with " +
        "cancel_queued_execution. An empty list means an idle pool, not an error. Manual (non-pooled) adapters " +
        "have no queue at all. Equivalent to octo-cli GetAdapterPoolQueue.")]
    public static async Task<GetAdapterPoolQueueResponse> GetAdapterPoolQueue(
        McpServer server,
        [Description("Adapter pool runtime ID in the lending tenant.")] string adapterPoolId,
        [Description("Tenant to operate on. Falls back to URL route.")] string? tenantId = null)
    {
        if (string.IsNullOrWhiteSpace(adapterPoolId))
        {
            return new GetAdapterPoolQueueResponse { IsSuccess = false, ErrorMessage = "adapterPoolId is required." };
        }

        var ctx = await CommunicationClientContext.TryBuildAsync(server, tenantId);
        if (ctx.Error != null)
        {
            return new GetAdapterPoolQueueResponse { IsSuccess = false, ErrorMessage = ctx.Error };
        }

        try
        {
            var entries = (await ctx.Client!.GetAdapterPoolQueueAsync(adapterPoolId)).ToList();
            var leased = entries.Count(e => e.IsLeased);
            var waiting = entries.Count - leased;

            return new GetAdapterPoolQueueResponse
            {
                IsSuccess = true,
                TenantId = ctx.TenantId,
                AdapterPoolId = adapterPoolId,
                Entries = entries,
                WaitingCount = waiting,
                LeasedCount = leased,
                Message = entries.Count == 0
                    ? $"Adapter pool '{adapterPoolId}' has an empty queue."
                    : $"{waiting} waiting, {leased} leased. Position is per tenant plus tenants ahead in the " +
                      "rotation; there is no global rank."
            };
        }
        catch (Exception ex)
        {
            return new GetAdapterPoolQueueResponse { IsSuccess = false, ErrorMessage = ex.Message };
        }
    }

    /// <summary>
    ///     Cancels one entry that is still waiting in an adapter pool's queue. Destructive: requires
    ///     confirm.
    /// </summary>
    /// <remarks>
    ///     🔴 <b>High, not Medium, deliberately.</b> The taxonomy would read a single queue entry as a
    ///     "single-instance delete", but the work item is destroyed rather than archived — the
    ///     execution becomes <c>Cancelled</c> and is never leased — and every other destructive verb
    ///     in this class pauses the worker for confirmation. An AI discarding a tenant's queued
    ///     nightly run without the user seeing the proposal is exactly the case the approval gate
    ///     exists for.
    /// </remarks>
    [McpServerTool(Name = "cancel_queued_execution")]
    [McpRisk(McpRiskLevel.High)]
    [Description(
        "Cancel ONE execution that is still WAITING in an adapter pool's queue. DESTRUCTIVE — the execution " +
        "becomes Cancelled and never runs; it cannot be un-cancelled. Requires confirm=true. This is NOT 'stop " +
        "that pipeline': an execution that already holds a lease is refused (HTTP 409) and reported as " +
        "outcome=AlreadyLeased with nothing cancelled, because interrupting a running pipeline is a different " +
        "operation on a different path. Find execution ids with get_adapter_pool_queue. The tenant is the " +
        "LENDING tenant that owns the pool, not the borrowing tenant whose work is cancelled. Equivalent to " +
        "octo-cli CancelQueuedExecution.")]
    public static async Task<CancelQueuedExecutionResponse> CancelQueuedExecution(
        McpServer server,
        [Description("Adapter pool runtime ID in the lending tenant.")] string adapterPoolId,
        [Description("The queued execution to cancel.")] string executionId,
        [Description("Must be true to actually cancel.")] bool confirm = false,
        [Description("Tenant to operate on. Falls back to URL route.")] string? tenantId = null)
    {
        if (string.IsNullOrWhiteSpace(adapterPoolId))
        {
            return new CancelQueuedExecutionResponse
            {
                IsSuccess = false, ErrorMessage = "adapterPoolId is required."
            };
        }

        if (string.IsNullOrWhiteSpace(executionId))
        {
            return new CancelQueuedExecutionResponse { IsSuccess = false, ErrorMessage = "executionId is required." };
        }

        if (!confirm)
        {
            return new CancelQueuedExecutionResponse
            {
                IsSuccess = false,
                ErrorMessage =
                    $"Refusing to cancel queued execution '{executionId}' without confirm=true. It would never run."
            };
        }

        var ctx = await CommunicationClientContext.TryBuildAsync(server, tenantId);
        if (ctx.Error != null)
        {
            return new CancelQueuedExecutionResponse { IsSuccess = false, ErrorMessage = ctx.Error };
        }

        try
        {
            var result = await ctx.Client!.CancelQueuedExecutionAsync(adapterPoolId, executionId);

            // 🔴 AlreadyLeased and NotFound are outcomes of a call that worked, so IsSuccess stays
            // true and the distinction travels in Outcome/WasCancelled. Reporting the 409 as a
            // failure would invite the caller to retry, and retrying is not what it needs to do —
            // interrupting the running pipeline is a different operation.
            var message = result.Outcome switch
            {
                AdapterPoolQueueCancellationOutcome.Cancelled =>
                    $"Queued execution '{executionId}' was cancelled and will never run.",
                AdapterPoolQueueCancellationOutcome.AlreadyLeased =>
                    $"Execution '{executionId}' already holds a lease and is no longer queued — NOTHING was " +
                    "cancelled. Stopping it means interrupting the running pipeline, which is a different " +
                    "operation.",
                _ =>
                    $"No queued execution '{executionId}' belongs to adapter pool '{adapterPoolId}' — it was never " +
                    "queued here, or it already finished, failed or was cancelled."
            };

            return new CancelQueuedExecutionResponse
            {
                IsSuccess = true,
                TenantId = ctx.TenantId,
                AdapterPoolId = adapterPoolId,
                ExecutionId = executionId,
                Outcome = result.Outcome,
                WasCancelled = result.IsCancelled,
                Message = string.IsNullOrWhiteSpace(result.ServerMessage)
                    ? message
                    : $"{message} Server said: {result.ServerMessage}"
            };
        }
        catch (Exception ex)
        {
            return new CancelQueuedExecutionResponse { IsSuccess = false, ErrorMessage = ex.Message };
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

        var ctx = await CommunicationClientContext.TryBuildAsync(server, tenantId);
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
