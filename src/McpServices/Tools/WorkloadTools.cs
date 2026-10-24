using System.ComponentModel;
using Meshmakers.Octo.Backend.McpServices.Models;
using Meshmakers.Octo.Backend.McpServices.Services;
using Meshmakers.Octo.Backend.McpServices.Services.WorkloadResources;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using ModelContextProtocol.Server;

// ReSharper disable UnusedMember.Global

namespace Meshmakers.Octo.Backend.McpServices.Tools;

/// <summary>
///     Workload CI/CD chart rollout + pipeline reassignment tools (Communication Controller, Epic 3054).
///     Mirrors the octo-cli Workload + MovePipelines commands.
/// </summary>
[McpServerToolType]
public sealed class WorkloadTools
{
    /// <summary>List workloads in the tenant that reference the given chart name.</summary>
    [McpServerTool(Name = "get_workloads_by_chart")]
    [Description(
        "List workloads in the tenant whose ChartName matches the given name. Empty when the chart is not used " +
        "in this tenant — CI scripts treat that as a silent-skip signal. Equivalent to octo-cli " +
        "GetWorkloadsByChart.")]
    public static async Task<GetWorkloadsByChartResponse> GetWorkloadsByChart(
        McpServer server,
        [Description("Chart name to match (e.g. 'octo-mesh-adapter').")] string chartName,
        [Description("Tenant to operate on. Falls back to URL route.")] string? tenantId = null)
    {
        if (string.IsNullOrWhiteSpace(chartName))
        {
            return new GetWorkloadsByChartResponse { IsSuccess = false, ErrorMessage = "chartName is required." };
        }

        var ctx = await CommunicationClientContext.TryBuildAsync(server, tenantId);
        if (ctx.Error != null)
        {
            return new GetWorkloadsByChartResponse { IsSuccess = false, ErrorMessage = ctx.Error };
        }

        try
        {
            var workloads = (await ctx.Client!.GetWorkloadsByChartAsync(chartName)).ToList();
            return new GetWorkloadsByChartResponse
            {
                IsSuccess = true,
                TenantId = ctx.TenantId,
                ChartName = chartName,
                Workloads = workloads,
                TotalCount = workloads.Count,
                Message = workloads.Count == 0
                    ? $"No workloads use chart '{chartName}' in this tenant."
                    : $"{workloads.Count} workload(s) reference chart '{chartName}'."
            };
        }
        catch (Exception ex)
        {
            return new GetWorkloadsByChartResponse { IsSuccess = false, ErrorMessage = ex.Message };
        }
    }

    /// <summary>Stage a new ChartVersion on a workload. Does NOT deploy.</summary>
    [McpServerTool(Name = "update_workload_chart_version")]
    [McpRisk(McpRiskLevel.Medium)]
    [Description(
        "Set ChartVersion on a single workload. Server validates the value against a SemVer regex. Does NOT " +
        "trigger a deploy — call deploy_workload afterwards if needed. Equivalent to octo-cli " +
        "UpdateWorkloadChartVersion.")]
    public static async Task<CommunicationActionResponse> UpdateWorkloadChartVersion(
        McpServer server,
        [Description("Workload runtime ID.")] string workloadId,
        [Description("Target chart version (SemVer).")] string chartVersion,
        [Description("Tenant to operate on. Falls back to URL route.")] string? tenantId = null)
    {
        if (string.IsNullOrWhiteSpace(workloadId) || string.IsNullOrWhiteSpace(chartVersion))
        {
            return new CommunicationActionResponse
            {
                IsSuccess = false,
                ErrorMessage = "workloadId and chartVersion are required."
            };
        }

        var ctx = await CommunicationClientContext.TryBuildAsync(server, tenantId);
        if (ctx.Error != null)
        {
            return new CommunicationActionResponse { IsSuccess = false, ErrorMessage = ctx.Error };
        }

        try
        {
            await ctx.Client!.UpdateWorkloadChartVersionAsync(workloadId, chartVersion);
            return new CommunicationActionResponse
            {
                IsSuccess = true,
                TenantId = ctx.TenantId,
                ResourceId = workloadId,
                Message = $"Workload '{workloadId}' staged to chart version '{chartVersion}'. " +
                          "Call deploy_workload to roll it out."
            };
        }
        catch (Exception ex)
        {
            return new CommunicationActionResponse { IsSuccess = false, ErrorMessage = ex.Message };
        }
    }

    /// <summary>Show the typed CPU and memory settings of an adapter or application.</summary>
    [McpServerTool(Name = "get_workload_resources")]
    [Description(
        "Show the typed CPU request/limit and memory request/limit of an adapter or application and the last " +
        "deployment warning (e.g. a typed value overridden by an explicit Helm value). A null value is not set: the " +
        "chart default applies. Needs System.Communication 4.10.0 in the tenant. Adapters also carry these values " +
        "in get_adapter.")]
    public static async Task<WorkloadResourcesResponse> GetWorkloadResources(
        McpServer server,
        [Description("Runtime ID of the adapter or application.")] string workloadId,
        [Description("Tenant to operate on. Falls back to URL route.")] string? tenantId = null)
    {
        if (string.IsNullOrWhiteSpace(workloadId))
        {
            return new WorkloadResourcesResponse { IsSuccess = false, ErrorMessage = "workloadId is required." };
        }

        var ctx = await AssetClientContext.TryBuildAsync(server, tenantId);
        if (ctx.Error != null)
        {
            return new WorkloadResourcesResponse { IsSuccess = false, ErrorMessage = ctx.Error };
        }

        try
        {
            var state = await new WorkloadResourcesAccess(ctx.Client!).FindAsync(ctx.TenantId!, workloadId.Trim());
            return state == null ? NotFound(ctx.TenantId, workloadId) : ToResponse(ctx.TenantId, state);
        }
        catch (Exception ex)
        {
            return new WorkloadResourcesResponse { IsSuccess = false, ErrorMessage = ex.Message };
        }
    }

    /// <summary>Set or clear the typed CPU and memory settings of an adapter or application. Does NOT deploy.</summary>
    [McpServerTool(Name = "update_workload_resources")]
    [McpRisk(McpRiskLevel.Medium)]
    [Description(
        "Set or clear the typed CPU request/limit and memory request/limit of an adapter or application. A " +
        "parameter that is left out stays unchanged; an empty string clears that value (the chart default applies " +
        "again). Quantities (e.g. '250m', '0.5', '512Mi', '1Gi') and request <= limit are validated before anything " +
        "is written, with the same messages the communication controller uses at deploy. Writes runtime state only " +
        "and does NOT deploy — call deploy_workload afterwards. Precedence at deploy: chart default < these " +
        "settings < explicit valuesYaml/values[] overrides of the workload. Adapter pools are sized with " +
        "PoolMember* settings instead. Needs System.Communication 4.10.0. Equivalent to octo-cli " +
        "UpdateWorkloadResources.")]
    public static async Task<WorkloadResourcesResponse> UpdateWorkloadResources(
        McpServer server,
        [Description("Runtime ID of the adapter or application.")] string workloadId,
        [Description("CPU request, e.g. '250m'. Empty string clears; omit to keep.")] string? cpuRequest = null,
        [Description("CPU limit, e.g. '1'. Empty string clears; omit to keep.")] string? cpuLimit = null,
        [Description("Memory request, e.g. '512Mi'. Empty string clears; omit to keep.")] string? memoryRequest = null,
        [Description("Memory limit, e.g. '1Gi'. Empty string clears; omit to keep.")] string? memoryLimit = null,
        [Description("Tenant to operate on. Falls back to URL route.")] string? tenantId = null)
    {
        if (string.IsNullOrWhiteSpace(workloadId))
        {
            return new WorkloadResourcesResponse { IsSuccess = false, ErrorMessage = "workloadId is required." };
        }

        var changes = new Dictionary<string, string?>(StringComparer.Ordinal);
        AddChange(changes, "CpuRequest", cpuRequest);
        AddChange(changes, "CpuLimit", cpuLimit);
        AddChange(changes, "MemoryRequest", memoryRequest);
        AddChange(changes, "MemoryLimit", memoryLimit);
        if (changes.Count == 0)
        {
            return new WorkloadResourcesResponse
            {
                IsSuccess = false,
                ErrorMessage = "Give at least one of cpuRequest, cpuLimit, memoryRequest or memoryLimit " +
                               "(an empty string clears a value). Use get_workload_resources to read them."
            };
        }

        var ctx = await AssetClientContext.TryBuildAsync(server, tenantId);
        if (ctx.Error != null)
        {
            return new WorkloadResourcesResponse { IsSuccess = false, ErrorMessage = ctx.Error };
        }

        try
        {
            var access = new WorkloadResourcesAccess(ctx.Client!);
            var rtId = workloadId.Trim();
            var workload = await access.FindAsync(ctx.TenantId!, rtId);
            if (workload == null)
            {
                return NotFound(ctx.TenantId, workloadId);
            }

            var target = workload.Settings;
            foreach (var (name, value) in changes)
            {
                target = target.With(name, value);
            }

            var errors = target.Validate();
            if (errors.Count > 0)
            {
                return new WorkloadResourcesResponse
                {
                    IsSuccess = false,
                    TenantId = ctx.TenantId,
                    WorkloadId = rtId,
                    ErrorMessage = $"Nothing was written. {string.Join("; ", errors)}."
                };
            }

            var updated = await access.UpdateAsync(ctx.TenantId!, workload, changes);
            var response = ToResponse(ctx.TenantId, updated);
            response.Message = $"Resources of {updated.Kind} '{rtId}' updated. Nothing was deployed — " +
                               "call deploy_workload to apply them.";
            return response;
        }
        catch (Exception ex)
        {
            return new WorkloadResourcesResponse { IsSuccess = false, ErrorMessage = ex.Message };
        }
    }

    private static void AddChange(Dictionary<string, string?> changes, string attribute, string? value)
    {
        if (value != null)
        {
            changes[attribute] = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
    }

    private static WorkloadResourcesResponse NotFound(string? tenantId, string workloadId) =>
        new()
        {
            IsSuccess = false,
            TenantId = tenantId,
            WorkloadId = workloadId,
            ErrorMessage = $"No adapter or application with id '{workloadId}' in this tenant. " +
                           "Adapter pools are sized with their PoolMember* settings."
        };

    private static WorkloadResourcesResponse ToResponse(string? tenantId, WorkloadResourcesState state) =>
        new()
        {
            IsSuccess = true,
            TenantId = tenantId,
            WorkloadId = state.RtId,
            WorkloadName = state.Name,
            WorkloadKind = state.Kind,
            Resources = ToInfo(state.Settings)
        };

    internal static WorkloadResourcesInfo ToInfo(WorkloadResourceSettings settings) =>
        new()
        {
            CpuRequest = settings.CpuRequest,
            CpuLimit = settings.CpuLimit,
            MemoryRequest = settings.MemoryRequest,
            MemoryLimit = settings.MemoryLimit,
            LastDeploymentWarning = settings.LastDeploymentWarning
        };

    /// <summary>Trigger a deploy of a workload via its parent pool.</summary>
    [McpServerTool(Name = "deploy_workload")]
    [McpRisk(McpRiskLevel.High)]
    [Description(
        "Trigger a deploy of one workload through its parent pool. Equivalent to octo-cli DeployWorkload.")]
    public static async Task<CommunicationActionResponse> DeployWorkload(
        McpServer server,
        [Description("Workload runtime ID.")] string workloadId,
        [Description("Tenant to operate on. Falls back to URL route.")] string? tenantId = null)
    {
        if (string.IsNullOrWhiteSpace(workloadId))
        {
            return new CommunicationActionResponse { IsSuccess = false, ErrorMessage = "workloadId is required." };
        }

        var ctx = await CommunicationClientContext.TryBuildAsync(server, tenantId);
        if (ctx.Error != null)
        {
            return new CommunicationActionResponse { IsSuccess = false, ErrorMessage = ctx.Error };
        }

        try
        {
            await ctx.Client!.DeployWorkloadAsync(workloadId);
            return new CommunicationActionResponse
            {
                IsSuccess = true,
                TenantId = ctx.TenantId,
                ResourceId = workloadId,
                Message = $"Workload '{workloadId}' deploy triggered."
            };
        }
        catch (Exception ex)
        {
            return new CommunicationActionResponse { IsSuccess = false, ErrorMessage = ex.Message };
        }
    }

    /// <summary>Trigger an undeploy of a workload. Destructive: requires confirm.</summary>
    [McpServerTool(Name = "undeploy_workload")]
    [McpRisk(McpRiskLevel.High)]
    [Description(
        "Trigger an undeploy of one workload. DESTRUCTIVE — workload stops running until re-deployed. " +
        "Requires confirm=true. Equivalent to octo-cli UndeployWorkload.")]
    public static async Task<CommunicationActionResponse> UndeployWorkload(
        McpServer server,
        [Description("Workload runtime ID.")] string workloadId,
        [Description("Must be true to actually undeploy.")] bool confirm = false,
        [Description("Tenant to operate on. Falls back to URL route.")] string? tenantId = null)
    {
        if (string.IsNullOrWhiteSpace(workloadId))
        {
            return new CommunicationActionResponse { IsSuccess = false, ErrorMessage = "workloadId is required." };
        }

        if (!confirm)
        {
            return new CommunicationActionResponse
            {
                IsSuccess = false,
                ErrorMessage = $"Refusing to undeploy workload '{workloadId}' without confirm=true."
            };
        }

        var ctx = await CommunicationClientContext.TryBuildAsync(server, tenantId);
        if (ctx.Error != null)
        {
            return new CommunicationActionResponse { IsSuccess = false, ErrorMessage = ctx.Error };
        }

        try
        {
            await ctx.Client!.UndeployWorkloadAsync(workloadId);
            return new CommunicationActionResponse
            {
                IsSuccess = true,
                TenantId = ctx.TenantId,
                ResourceId = workloadId,
                Message = $"Workload '{workloadId}' undeploy triggered."
            };
        }
        catch (Exception ex)
        {
            return new CommunicationActionResponse { IsSuccess = false, ErrorMessage = ex.Message };
        }
    }

    /// <summary>Reassign pipelines from their current adapter to a new target adapter. Destructive: requires confirm.</summary>
    [McpServerTool(Name = "move_pipelines")]
    [McpRisk(McpRiskLevel.High)]
    [Description(
        "Bulk-reassign pipelines from their current adapter to a new target adapter. Source and target adapter " +
        "must share the same CkTypeId; per-pipeline failures are reported in the result list without aborting " +
        "the batch. DESTRUCTIVE — changes the live Pipeline.Executes association. Requires confirm=true. With " +
        "redeploy=true, each successfully moved pipeline is re-deployed on the target adapter (failures don't " +
        "roll the move back). Equivalent to octo-cli MovePipelines.")]
    public static async Task<MovePipelinesResponse> MovePipelines(
        McpServer server,
        [Description("Pipeline runtime IDs to reassign.")] List<string> pipelineIds,
        [Description("Target adapter runtime ID.")] string targetAdapterId,
        [Description("Whether to also redeploy each moved pipeline on the new adapter.")] bool redeploy = false,
        [Description("Must be true to actually move.")] bool confirm = false,
        [Description("Tenant to operate on. Falls back to URL route.")] string? tenantId = null)
    {
        if (pipelineIds == null || pipelineIds.Count == 0 || string.IsNullOrWhiteSpace(targetAdapterId))
        {
            return new MovePipelinesResponse
            {
                IsSuccess = false,
                ErrorMessage = "pipelineIds (non-empty) and targetAdapterId are required."
            };
        }

        if (!confirm)
        {
            return new MovePipelinesResponse
            {
                IsSuccess = false,
                ErrorMessage =
                    $"Refusing to move {pipelineIds.Count} pipeline(s) to adapter '{targetAdapterId}' without confirm=true."
            };
        }

        var ctx = await CommunicationClientContext.TryBuildAsync(server, tenantId);
        if (ctx.Error != null)
        {
            return new MovePipelinesResponse { IsSuccess = false, ErrorMessage = ctx.Error };
        }

        try
        {
            var response = await ctx.Client!.MovePipelinesToAdapterAsync(
                new MovePipelinesToAdapterRequestDto(pipelineIds, targetAdapterId, redeploy));

            var success = response.Results.Count(r => r.Success);
            var failure = response.Results.Count(r => !r.Success);

            return new MovePipelinesResponse
            {
                IsSuccess = true,
                TenantId = ctx.TenantId,
                TargetAdapterId = targetAdapterId,
                Result = response,
                SuccessCount = success,
                FailureCount = failure,
                Message = failure == 0
                    ? $"Moved {success} pipeline(s) to adapter '{targetAdapterId}'" +
                      (redeploy ? " (redeploy requested)." : ".")
                    : $"Moved {success} pipeline(s) to '{targetAdapterId}', {failure} failed — inspect Result for details."
            };
        }
        catch (Exception ex)
        {
            return new MovePipelinesResponse { IsSuccess = false, ErrorMessage = ex.Message };
        }
    }
}
