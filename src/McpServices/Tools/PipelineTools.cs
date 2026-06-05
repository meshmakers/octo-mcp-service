using System.ComponentModel;
using Meshmakers.Octo.Backend.McpServices.Models;
using Meshmakers.Octo.Backend.McpServices.Services;
using ModelContextProtocol.Server;

// ReSharper disable UnusedMember.Global

namespace Meshmakers.Octo.Backend.McpServices.Tools;

/// <summary>
///     Pipeline deployment, execution, and debug tools. Mirrors octo-cli Pipeline commands.
///     Difference vs CLI: deploy_pipeline takes the pipeline definition inline (string), not a file path.
/// </summary>
[McpServerToolType]
public sealed class PipelineTools
{
    /// <summary>Get the deployment state of a pipeline.</summary>
    [McpServerTool(Name = "get_pipeline_status")]
    [Description("Get the deployment state of a pipeline. Equivalent to octo-cli GetPipelineStatus.")]
    public static async Task<PipelineDeploymentResponse> GetPipelineStatus(
        McpServer server,
        [Description("Pipeline runtime ID.")] string pipelineId,
        [Description("Tenant to operate on. Falls back to URL route.")] string? tenantId = null)
    {
        if (string.IsNullOrWhiteSpace(pipelineId))
        {
            return new PipelineDeploymentResponse { IsSuccess = false, ErrorMessage = "pipelineId is required." };
        }

        var ctx = CommunicationClientContext.TryBuild(server, tenantId);
        if (ctx.Error != null)
        {
            return new PipelineDeploymentResponse { IsSuccess = false, ErrorMessage = ctx.Error };
        }

        try
        {
            var dr = await ctx.Client!.GetPipelineDeploymentStateAsync(pipelineId);
            return new PipelineDeploymentResponse
            {
                IsSuccess = true,
                TenantId = ctx.TenantId,
                PipelineId = pipelineId,
                DeploymentResult = dr
            };
        }
        catch (Exception ex)
        {
            return new PipelineDeploymentResponse { IsSuccess = false, ErrorMessage = ex.Message };
        }
    }

    /// <summary>Deploy a pipeline definition (YAML/JSON string) to the corresponding adapter.</summary>
    [McpServerTool(Name = "deploy_pipeline")]
    [Description(
        "Deploy a pipeline definition (YAML or JSON) to the given adapter. The definition is passed inline as " +
        "a string — unlike the CLI which reads from a file. Equivalent to octo-cli DeployPipeline.")]
    public static async Task<PipelineDeploymentResponse> DeployPipeline(
        McpServer server,
        [Description("Adapter runtime ID the pipeline runs on.")] string adapterId,
        [Description("Pipeline runtime ID.")] string pipelineId,
        [Description("Pipeline definition (YAML or JSON) as a string.")] string pipelineDefinition,
        [Description("Tenant to operate on. Falls back to URL route.")] string? tenantId = null)
    {
        if (string.IsNullOrWhiteSpace(adapterId) || string.IsNullOrWhiteSpace(pipelineId) ||
            string.IsNullOrWhiteSpace(pipelineDefinition))
        {
            return new PipelineDeploymentResponse
            {
                IsSuccess = false,
                ErrorMessage = "adapterId, pipelineId and pipelineDefinition are required."
            };
        }

        var ctx = CommunicationClientContext.TryBuild(server, tenantId);
        if (ctx.Error != null)
        {
            return new PipelineDeploymentResponse { IsSuccess = false, ErrorMessage = ctx.Error };
        }

        try
        {
            await ctx.Client!.DeployPipelineAsync(adapterId, pipelineId, pipelineDefinition);
            return new PipelineDeploymentResponse
            {
                IsSuccess = true,
                TenantId = ctx.TenantId,
                PipelineId = pipelineId,
                Message = $"Pipeline '{pipelineId}' deployed to adapter '{adapterId}'."
            };
        }
        catch (Exception ex)
        {
            return new PipelineDeploymentResponse { IsSuccess = false, ErrorMessage = ex.Message };
        }
    }

    /// <summary>Execute a pipeline and return the execution ID.</summary>
    [McpServerTool(Name = "execute_pipeline")]
    [Description("Execute a pipeline and return the execution ID. Equivalent to octo-cli ExecutePipeline.")]
    public static async Task<ExecutePipelineResponse> ExecutePipeline(
        McpServer server,
        [Description("Pipeline runtime ID.")] string pipelineId,
        [Description("Optional pipeline input (string — JSON, YAML, or plain).")] string? pipelineInput = null,
        [Description("Tenant to operate on. Falls back to URL route.")] string? tenantId = null)
    {
        if (string.IsNullOrWhiteSpace(pipelineId))
        {
            return new ExecutePipelineResponse { IsSuccess = false, ErrorMessage = "pipelineId is required." };
        }

        var ctx = CommunicationClientContext.TryBuild(server, tenantId);
        if (ctx.Error != null)
        {
            return new ExecutePipelineResponse { IsSuccess = false, ErrorMessage = ctx.Error };
        }

        try
        {
            var executionId = await ctx.Client!.ExecutePipelineAsync(pipelineId, pipelineInput);
            return new ExecutePipelineResponse
            {
                IsSuccess = true,
                TenantId = ctx.TenantId,
                PipelineId = pipelineId,
                ExecutionId = executionId,
                Message = $"Pipeline '{pipelineId}' execution started (id={executionId})."
            };
        }
        catch (Exception ex)
        {
            return new ExecutePipelineResponse { IsSuccess = false, ErrorMessage = ex.Message };
        }
    }

    /// <summary>Toggle debug capture on a pipeline.</summary>
    [McpServerTool(Name = "set_pipeline_debug")]
    [Description(
        "Enable or disable debug capture for a pipeline. Re-pushes the adapter configuration so the change " +
        "takes effect immediately when the adapter is online. Equivalent to octo-cli SetPipelineDebug.")]
    public static async Task<SetPipelineDebugResponse> SetPipelineDebug(
        McpServer server,
        [Description("Pipeline runtime ID.")] string pipelineId,
        [Description("True to enable debug capture, false to disable.")] bool enabled,
        [Description("Tenant to operate on. Falls back to URL route.")] string? tenantId = null)
    {
        if (string.IsNullOrWhiteSpace(pipelineId))
        {
            return new SetPipelineDebugResponse { IsSuccess = false, ErrorMessage = "pipelineId is required." };
        }

        var ctx = CommunicationClientContext.TryBuild(server, tenantId);
        if (ctx.Error != null)
        {
            return new SetPipelineDebugResponse { IsSuccess = false, ErrorMessage = ctx.Error };
        }

        try
        {
            var dto = await ctx.Client!.SetPipelineDebuggingAsync(pipelineId, enabled);
            return new SetPipelineDebugResponse
            {
                IsSuccess = true,
                TenantId = ctx.TenantId,
                PipelineId = pipelineId,
                Result = dto,
                Message = enabled
                    ? $"Debug enabled on pipeline '{pipelineId}' (applied to running adapter: {dto.AppliedToRunningAdapter})."
                    : $"Debug disabled on pipeline '{pipelineId}'."
            };
        }
        catch (Exception ex)
        {
            return new SetPipelineDebugResponse { IsSuccess = false, ErrorMessage = ex.Message };
        }
    }

    /// <summary>Get the persisted debug state of a pipeline.</summary>
    [McpServerTool(Name = "get_pipeline_debug")]
    [Description("Get the persisted debug state of a pipeline. Equivalent to octo-cli GetPipelineDebug.")]
    public static async Task<GetPipelineDebugResponse> GetPipelineDebug(
        McpServer server,
        [Description("Pipeline runtime ID.")] string pipelineId,
        [Description("Tenant to operate on. Falls back to URL route.")] string? tenantId = null)
    {
        if (string.IsNullOrWhiteSpace(pipelineId))
        {
            return new GetPipelineDebugResponse { IsSuccess = false, ErrorMessage = "pipelineId is required." };
        }

        var ctx = CommunicationClientContext.TryBuild(server, tenantId);
        if (ctx.Error != null)
        {
            return new GetPipelineDebugResponse { IsSuccess = false, ErrorMessage = ctx.Error };
        }

        try
        {
            var state = await ctx.Client!.GetPipelineDebuggingAsync(pipelineId);
            return new GetPipelineDebugResponse
            {
                IsSuccess = true,
                TenantId = ctx.TenantId,
                PipelineId = pipelineId,
                State = state
            };
        }
        catch (Exception ex)
        {
            return new GetPipelineDebugResponse { IsSuccess = false, ErrorMessage = ex.Message };
        }
    }

    /// <summary>Get pipeline execution history.</summary>
    [McpServerTool(Name = "get_pipeline_executions")]
    [Description("Return pipeline execution history. Equivalent to octo-cli GetPipelineExecutions.")]
    public static async Task<GetPipelineExecutionsResponse> GetPipelineExecutions(
        McpServer server,
        [Description("Pipeline runtime ID.")] string pipelineId,
        [Description("Tenant to operate on. Falls back to URL route.")] string? tenantId = null)
    {
        if (string.IsNullOrWhiteSpace(pipelineId))
        {
            return new GetPipelineExecutionsResponse { IsSuccess = false, ErrorMessage = "pipelineId is required." };
        }

        var ctx = CommunicationClientContext.TryBuild(server, tenantId);
        if (ctx.Error != null)
        {
            return new GetPipelineExecutionsResponse { IsSuccess = false, ErrorMessage = ctx.Error };
        }

        try
        {
            var executions = (await ctx.Client!.GetPipelineExecutionsAsync(pipelineId)).ToList();
            return new GetPipelineExecutionsResponse
            {
                IsSuccess = true,
                TenantId = ctx.TenantId,
                PipelineId = pipelineId,
                Executions = executions,
                TotalCount = executions.Count,
                Message = executions.Count == 0
                    ? $"No executions for pipeline '{pipelineId}'."
                    : $"{executions.Count} execution(s) for pipeline '{pipelineId}'."
            };
        }
        catch (Exception ex)
        {
            return new GetPipelineExecutionsResponse { IsSuccess = false, ErrorMessage = ex.Message };
        }
    }

    /// <summary>Get the most recent pipeline execution.</summary>
    [McpServerTool(Name = "get_latest_pipeline_execution")]
    [Description("Return the most recent pipeline execution. Equivalent to octo-cli GetLatestPipelineExecution.")]
    public static async Task<GetLatestPipelineExecutionResponse> GetLatestPipelineExecution(
        McpServer server,
        [Description("Pipeline runtime ID.")] string pipelineId,
        [Description("Tenant to operate on. Falls back to URL route.")] string? tenantId = null)
    {
        if (string.IsNullOrWhiteSpace(pipelineId))
        {
            return new GetLatestPipelineExecutionResponse
            {
                IsSuccess = false,
                ErrorMessage = "pipelineId is required."
            };
        }

        var ctx = CommunicationClientContext.TryBuild(server, tenantId);
        if (ctx.Error != null)
        {
            return new GetLatestPipelineExecutionResponse { IsSuccess = false, ErrorMessage = ctx.Error };
        }

        try
        {
            var exec = await ctx.Client!.GetLatestPipelineExecutionAsync(pipelineId);
            return new GetLatestPipelineExecutionResponse
            {
                IsSuccess = true,
                TenantId = ctx.TenantId,
                PipelineId = pipelineId,
                Execution = exec
            };
        }
        catch (Exception ex)
        {
            return new GetLatestPipelineExecutionResponse { IsSuccess = false, ErrorMessage = ex.Message };
        }
    }

    /// <summary>Get debug points for a specific pipeline execution.</summary>
    [McpServerTool(Name = "get_pipeline_debug_points")]
    [Description(
        "Return debug-point nodes for a specific pipeline execution (raw JSON). Equivalent to octo-cli " +
        "GetPipelineDebugPoints.")]
    public static async Task<GetPipelineDebugPointsResponse> GetPipelineDebugPoints(
        McpServer server,
        [Description("Pipeline runtime ID.")] string pipelineId,
        [Description("Execution ID (GUID) returned by execute_pipeline / get_pipeline_executions.")] Guid executionId,
        [Description("Tenant to operate on. Falls back to URL route.")] string? tenantId = null)
    {
        if (string.IsNullOrWhiteSpace(pipelineId))
        {
            return new GetPipelineDebugPointsResponse
            {
                IsSuccess = false,
                ErrorMessage = "pipelineId is required."
            };
        }

        var ctx = CommunicationClientContext.TryBuild(server, tenantId);
        if (ctx.Error != null)
        {
            return new GetPipelineDebugPointsResponse { IsSuccess = false, ErrorMessage = ctx.Error };
        }

        try
        {
            var json = await ctx.Client!.GetPipelineExecutionDebugPointsAsync(pipelineId, executionId);
            return new GetPipelineDebugPointsResponse
            {
                IsSuccess = true,
                TenantId = ctx.TenantId,
                PipelineId = pipelineId,
                ExecutionId = executionId,
                DebugPointsJson = json
            };
        }
        catch (Exception ex)
        {
            return new GetPipelineDebugPointsResponse { IsSuccess = false, ErrorMessage = ex.Message };
        }
    }
}
