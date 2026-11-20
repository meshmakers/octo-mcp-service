using System.ComponentModel;
using Meshmakers.Octo.Backend.McpServices.MethodClients.DataTransferObjects.System.Communication.v4;
using Meshmakers.Octo.Backend.McpServices.Models;
using Meshmakers.Octo.Backend.McpServices.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Sdk.ServiceClient.AssetRepositoryServices.Tenants;
using Meshmakers.Octo.Sdk.ServiceClient.AssetRepositoryServices.Tenants.Methods;
using ModelContextProtocol.Server;

// ReSharper disable UnusedMember.Global

namespace Meshmakers.Octo.Backend.McpServices.Tools;

/// <summary>
///     Data Flow + Trigger + Pool tools (Communication Controller), including the adapter pool queue
///     (AB#4924 §10). Mirrors the smaller octo-cli commands. Bundled into one class because each
///     subsystem only has 1–3 operations. The writes run the CK methods of <c>System.Communication</c> through the
///     generated method clients (<see cref="CommunicationMethodCall" />); the reads stay REST.
/// </summary>
[McpServerToolType]
public sealed class DataFlowTriggerDeploymentSiteTools
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
            (tenant, id) => new RtDataFlowMethodClient(tenant).DeployAsync(id),
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
            (tenant, id) => new RtDataFlowMethodClient(tenant).UndeployAsync(id),
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
        var ctx = await CommunicationClientContext.TryBuildAsync(server, tenantId, withTenantClient: true);
        if (ctx.Error != null)
        {
            return new CommunicationResponse { IsSuccess = false, ErrorMessage = ctx.Error };
        }

        try
        {
            var outcome = await CommunicationMethodCall.RunAsync(() =>
                new RtPipelineTriggerMethodClient(ctx.TenantClient!).DeployAllAsync());
            if (!outcome.IsSuccess)
            {
                return new CommunicationResponse { IsSuccess = false, ErrorMessage = outcome.Error };
            }

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

        var ctx = await CommunicationClientContext.TryBuildAsync(server, tenantId, withTenantClient: true);
        if (ctx.Error != null)
        {
            return new CommunicationResponse { IsSuccess = false, ErrorMessage = ctx.Error };
        }

        try
        {
            var outcome = await CommunicationMethodCall.RunAsync(() =>
                new RtPipelineTriggerMethodClient(ctx.TenantClient!).UndeployAllAsync());
            if (!outcome.IsSuccess)
            {
                return new CommunicationResponse { IsSuccess = false, ErrorMessage = outcome.Error };
            }

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

    // ── DeploymentSites ───────────────────────────────────────────────────────────────

    /// <summary>List all deploymentSites for the tenant.</summary>
    [McpServerTool(Name = "get_deployment_sites")]
    [Description("List all deployment sites configured for the tenant. Equivalent to octo-cli GetDeploymentSites.")]
    public static async Task<GetDeploymentSitesResponse> GetDeploymentSites(
        McpServer server,
        [Description("Tenant to operate on. Falls back to URL route.")] string? tenantId = null)
    {
        var ctx = await CommunicationClientContext.TryBuildAsync(server, tenantId);
        if (ctx.Error != null)
        {
            return new GetDeploymentSitesResponse { IsSuccess = false, ErrorMessage = ctx.Error };
        }

        try
        {
            var deploymentSites = (await ctx.Client!.GetDeploymentSitesAsync()).ToList();
            return new GetDeploymentSitesResponse
            {
                IsSuccess = true,
                TenantId = ctx.TenantId,
                DeploymentSites = deploymentSites,
                TotalCount = deploymentSites.Count,
                Message = deploymentSites.Count == 0 ? "No deployment sites configured." : $"{deploymentSites.Count} deployment site(s)."
            };
        }
        catch (Exception ex)
        {
            return new GetDeploymentSitesResponse { IsSuccess = false, ErrorMessage = ex.Message };
        }
    }

    /// <summary>Undeploy a deploymentSite. Destructive: requires confirm.</summary>
    [McpServerTool(Name = "undeploy_deployment_site")]
    [McpRisk(McpRiskLevel.High)]
    [Description(
        "Undeploy a deployment site. DESTRUCTIVE - the Communication Operator removes the deployment site's cluster resources; " +
        "undeploy its workloads first (undeploy_workload). Requires confirm=true. Needed before " +
        "disable_communication, which is refused while deployment sites or workloads are deployed. Equivalent to " +
        "octo-cli UndeployDeploymentSite.")]
    public static async Task<CommunicationActionResponse> UndeployDeploymentSite(
        McpServer server,
        [Description("Deployment site runtime ID.")] string deploymentSiteId,
        [Description("Must be true to actually undeploy.")] bool confirm = false,
        [Description("Tenant to operate on. Falls back to URL route.")] string? tenantId = null)
        => await SingleResourceAction(server, tenantId, deploymentSiteId,
            requiredConfirm: true, confirm,
            (tenant, id) => new RtDeploymentSiteMethodClient(tenant).UndeployAsync(id),
            successMessage: id => $"Deployment site '{id}' undeploy triggered.");

    // ── Adapter pool queue (AB#4924 §10) ────────────────────────────────────

    /// <summary>The queue of one adapter pool: what waits for a lease and what is leased right now.</summary>
    [McpServerTool(Name = "get_adapter_pool_queue")]
    [Description(
        "Show an adapter pool's queue: every execution waiting for a lease, plus the executions the pool " +
        "currently has leased out. Read-only. The tenant is the LENDING tenant that owns the deployment site; each entry " +
        "names the borrowing tenant whose work it is. POSITION IS REPORTED AS A PAIR — positionInTenant (where " +
        "the item sits in its own tenant's queue) and tenantsAheadInRotation (how many other tenants take a turn " +
        "first) — because the deployment site serves tenants round-robin and there is no single global rank; do not compute " +
        "one. Entries with leasedOnMemberId set are already running and cannot be cancelled with " +
        "cancel_queued_execution. An empty list means an idle deployment site, not an error. Manual (non-pooled) adapters " +
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
        "that pipeline': an execution that already holds a lease is refused (EXECUTION_ALREADY_LEASED) and reported as " +
        "outcome=AlreadyLeased with nothing cancelled, because interrupting a running pipeline is a different " +
        "operation on a different path. Find execution ids with get_adapter_pool_queue. The tenant is the " +
        "LENDING tenant that owns the deployment site, not the borrowing tenant whose work is cancelled. Equivalent to " +
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

        var invalidId = CommunicationMethodCall.InvalidIdError(("adapterPoolId", adapterPoolId));
        if (invalidId != null)
        {
            return new CancelQueuedExecutionResponse { IsSuccess = false, ErrorMessage = invalidId };
        }

        var ctx = await CommunicationClientContext.TryBuildAsync(server, tenantId, withTenantClient: true);
        if (ctx.Error != null)
        {
            return new CancelQueuedExecutionResponse { IsSuccess = false, ErrorMessage = ctx.Error };
        }

        try
        {
            var outcome = await CommunicationMethodCall.RunAsync(() =>
                new RtAdapterPoolMethodClient(ctx.TenantClient!).CancelQueuedExecutionAsync(adapterPoolId, executionId));

            // The method answers the two refusals as declared errors; they stay outcomes of their own, as the REST
            // answers 409 and 404 did.
            AdapterPoolQueueCancellationResultDto result;
            if (outcome.IsSuccess)
            {
                result = new AdapterPoolQueueCancellationResultDto
                    { Outcome = AdapterPoolQueueCancellationOutcome.Cancelled };
            }
            else if (outcome.ErrorCode == RtAdapterPoolMethodClient.CancelQueuedExecutionErrors.ExecutionAlreadyLeased)
            {
                result = new AdapterPoolQueueCancellationResultDto
                {
                    Outcome = AdapterPoolQueueCancellationOutcome.AlreadyLeased,
                    ServerMessage = ErrorTextRedactor.Redact(outcome.ErrorMessage ?? string.Empty)
                };
            }
            else if (outcome.ErrorCode == RtAdapterPoolMethodClient.CancelQueuedExecutionErrors.QueuedExecutionNotFound)
            {
                result = new AdapterPoolQueueCancellationResultDto
                {
                    Outcome = AdapterPoolQueueCancellationOutcome.NotFound,
                    ServerMessage = ErrorTextRedactor.Redact(outcome.ErrorMessage ?? string.Empty)
                };
            }
            else
            {
                return new CancelQueuedExecutionResponse { IsSuccess = false, ErrorMessage = outcome.Error };
            }

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
        Func<ITenantClient, string, Task<CkMethodResult>> action,
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

        var invalidId = CommunicationMethodCall.InvalidIdError(("resource id", resourceId));
        if (invalidId != null)
        {
            return new CommunicationActionResponse { IsSuccess = false, ErrorMessage = invalidId };
        }

        var ctx = await CommunicationClientContext.TryBuildAsync(server, tenantId, withTenantClient: true);
        if (ctx.Error != null)
        {
            return new CommunicationActionResponse { IsSuccess = false, ErrorMessage = ctx.Error };
        }

        try
        {
            var outcome = await CommunicationMethodCall.RunAsync(() => action(ctx.TenantClient!, resourceId));
            if (!outcome.IsSuccess)
            {
                return new CommunicationActionResponse { IsSuccess = false, ErrorMessage = outcome.Error };
            }

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
