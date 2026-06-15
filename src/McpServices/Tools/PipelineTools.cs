using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using Meshmakers.Octo.Backend.McpServices.Models;
using Meshmakers.Octo.Backend.McpServices.Services;
using ModelContextProtocol.Server;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

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
    [McpRisk(McpRiskLevel.High)]
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
    [McpRisk(McpRiskLevel.High)]
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
    [McpRisk(McpRiskLevel.Medium)]
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

    /// <summary>
    ///     M4-B.1 — validate a pipeline-definition string against the adapter's composite
    ///     JSON Schema BEFORE handing it to <c>deploy_pipeline</c> (which only validates as
    ///     a side effect of deploy). Lets the agent lint a candidate definition without
    ///     leaving a half-deployed pipeline on the adapter if the definition is wrong.
    ///     Pure-read against the adapter's schema endpoint; no mutation.
    /// </summary>
    [McpServerTool(Name = "validate_pipeline_definition")]
    [McpRisk(McpRiskLevel.Low)]
    [Description(
        "Validate a pipeline definition (YAML or JSON) against the target adapter's " +
        "composite pipeline JSON Schema (M4-B.1). Returns the per-error list with JSON " +
        "pointers + node count. Use this BEFORE deploy_pipeline so the agent can iterate " +
        "on the definition without leaving a half-deployed pipeline behind on validation " +
        "failure. Read-only — never touches the adapter's deployed state.")]
    public static async Task<ValidatePipelineDefinitionResponse> ValidatePipelineDefinition(
        McpServer server,
        [Description("Adapter runtime ID whose pipeline schema the definition is validated against.")]
        string adapterId,
        [Description("Pipeline definition (YAML or JSON) as a string. The tool auto-detects format from the leading non-whitespace character.")]
        string pipelineDefinition,
        [Description("Tenant to operate on. Falls back to URL route.")]
        string? tenantId = null)
    {
        if (string.IsNullOrWhiteSpace(adapterId))
        {
            return new ValidatePipelineDefinitionResponse
            {
                IsSuccess = false, ErrorMessage = "adapterId is required."
            };
        }
        if (string.IsNullOrWhiteSpace(pipelineDefinition))
        {
            return new ValidatePipelineDefinitionResponse
            {
                IsSuccess = false, ErrorMessage = "pipelineDefinition is required."
            };
        }

        var ctx = CommunicationClientContext.TryBuild(server, tenantId);
        if (ctx.Error != null)
        {
            return new ValidatePipelineDefinitionResponse { IsSuccess = false, ErrorMessage = ctx.Error };
        }

        // 1. Pull the adapter's composite JSON Schema (same one deploy_pipeline validates against).
        string schemaJson;
        try
        {
            schemaJson = await ctx.Client!.GetPipelineSchemaAsync(adapterId);
        }
        catch (Exception ex)
        {
            return new ValidatePipelineDefinitionResponse
            {
                IsSuccess = false,
                TenantId = ctx.TenantId,
                AdapterId = adapterId,
                ErrorMessage = $"Failed to fetch pipeline schema for adapter {adapterId}: {ex.Message}",
            };
        }

        // 2. Parse the definition (auto-detect YAML vs JSON by leading non-whitespace char —
        //    `{` or `[` → JSON; anything else → YAML).
        JsonNode? definitionNode;
        try
        {
            definitionNode = ParsePipelineDefinitionToJsonNode(pipelineDefinition);
        }
        catch (Exception ex)
        {
            return new ValidatePipelineDefinitionResponse
            {
                IsSuccess = true, // tool call itself succeeded; the definition just doesn't parse
                IsValid = false,
                TenantId = ctx.TenantId,
                AdapterId = adapterId,
                Message = "Definition does not parse as YAML or JSON — see Errors for the first parse failure.",
                Errors = { new ValidatePipelineDefinitionError { Path = "$", Message = ex.Message } },
            };
        }
        if (definitionNode is null)
        {
            return new ValidatePipelineDefinitionResponse
            {
                IsSuccess = true,
                IsValid = false,
                TenantId = ctx.TenantId,
                AdapterId = adapterId,
                Message = "Definition parsed to a null root — empty or whitespace-only input.",
                Errors = { new ValidatePipelineDefinitionError { Path = "$", Message = "empty definition" } },
            };
        }

        // 3. JSON-Schema-validate the parsed definition.
        JsonSchema schema;
        try
        {
            schema = JsonSchema.FromText(schemaJson);
        }
        catch (Exception ex)
        {
            return new ValidatePipelineDefinitionResponse
            {
                IsSuccess = false,
                TenantId = ctx.TenantId,
                AdapterId = adapterId,
                ErrorMessage =
                    $"The adapter's pipeline schema did not parse as a JSON Schema document: {ex.Message}. " +
                    "This is an adapter-side bug (the schema endpoint returned an invalid JSON Schema), not a " +
                    "problem with the supplied pipelineDefinition.",
            };
        }

        // JsonSchema.Net's Evaluate expects a JsonElement, not a JsonNode — round-trip via
        // JsonDocument so the schema evaluator sees the same representation it's built on.
        using var definitionDoc = JsonDocument.Parse(definitionNode.ToJsonString());
        var evaluation = schema.Evaluate(definitionDoc.RootElement, new EvaluationOptions
        {
            OutputFormat = OutputFormat.List,
        });

        var errors = new List<ValidatePipelineDefinitionError>();
        if (!evaluation.IsValid && evaluation.Details != null)
        {
            foreach (var detail in evaluation.Details)
            {
                if (detail.Errors is null) continue;
                foreach (var (keyword, message) in detail.Errors)
                {
                    errors.Add(new ValidatePipelineDefinitionError
                    {
                        Path = detail.InstanceLocation.ToString(),
                        SchemaPath = detail.EvaluationPath.ToString(),
                        Keyword = keyword,
                        Message = message,
                    });
                }
            }
        }

        // 4. Cheap node-count sanity: count nodes[] entries if present in the root object.
        int nodeCount = 0;
        if (definitionNode is JsonObject root && root.TryGetPropertyValue("nodes", out var nodes) && nodes is JsonArray arr)
        {
            nodeCount = arr.Count;
        }

        return new ValidatePipelineDefinitionResponse
        {
            IsSuccess = true,
            TenantId = ctx.TenantId,
            AdapterId = adapterId,
            IsValid = evaluation.IsValid,
            NodeCount = nodeCount,
            Errors = errors,
            Message = evaluation.IsValid
                ? $"Definition is valid against the adapter's composite schema ({nodeCount} node(s))."
                : $"Definition has {errors.Count} schema error(s); see Errors for details.",
        };
    }

    /// <summary>
    ///     Auto-detect YAML vs JSON from the first non-whitespace character. <c>{</c> or
    ///     <c>[</c> → JSON. Anything else → YAML, deserialized via YamlDotNet into a
    ///     dynamic object and re-serialized through System.Text.Json to land as a
    ///     <see cref="JsonNode"/> the schema evaluator understands.
    /// </summary>
    private static JsonNode? ParsePipelineDefinitionToJsonNode(string pipelineDefinition)
    {
        var firstNonWs = pipelineDefinition.AsSpan().TrimStart();
        if (firstNonWs.IsEmpty)
        {
            return null;
        }
        if (firstNonWs[0] == '{' || firstNonWs[0] == '[')
        {
            return JsonNode.Parse(pipelineDefinition);
        }
        // YAML — round-trip via YamlDotNet → object → System.Text.Json node.
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .Build();
        var obj = deserializer.Deserialize<object?>(pipelineDefinition);
        if (obj is null) return null;
        var serializer = new SerializerBuilder()
            .JsonCompatible()
            .Build();
        var asJson = serializer.Serialize(obj);
        return JsonNode.Parse(asJson);
    }
}
