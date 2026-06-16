using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;

namespace Meshmakers.Octo.Backend.McpServices.Models;

/// <summary>Common envelope for communication-controller tool responses.</summary>
public class CommunicationResponse
{
    /// <summary>True when the underlying service call succeeded.</summary>
    public bool IsSuccess { get; set; }

    /// <summary>Error message when <see cref="IsSuccess"/> is false.</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>Optional human-readable status message.</summary>
    public string? Message { get; set; }

    /// <summary>Tenant the operation was executed against.</summary>
    public string? TenantId { get; set; }
}

/// <summary>Response for enable_communication / disable_communication.</summary>
public class CommunicationLifecycleResponse : CommunicationResponse
{
    /// <summary>Tenant whose communication controller was toggled.</summary>
    public string? TargetTenantId { get; set; }
}

/// <summary>Response for get_adapters.</summary>
public class GetAdaptersResponse : CommunicationResponse
{
    /// <summary>Adapters configured for the tenant.</summary>
    public List<AdapterSummaryDto> Adapters { get; set; } = [];

    /// <summary>Total number of adapters.</summary>
    public int TotalCount { get; set; }
}

/// <summary>Response for get_adapter.</summary>
public class GetAdapterResponse : CommunicationResponse
{
    /// <summary>Full adapter configuration.</summary>
    public AdapterConfigurationDto? Adapter { get; set; }

    /// <summary>Adapter runtime ID that was queried.</summary>
    public string? AdapterId { get; set; }
}

/// <summary>Response for get_adapter_nodes.</summary>
public class GetAdapterNodesResponse : CommunicationResponse
{
    /// <summary>Aggregated node descriptors from connected adapters (raw JSON).</summary>
    public string? NodesJson { get; set; }
}

/// <summary>Response for get_pipeline_schema.</summary>
public class GetPipelineSchemaResponse : CommunicationResponse
{
    /// <summary>Adapter runtime ID the schema applies to.</summary>
    public string? AdapterId { get; set; }

    /// <summary>Composite pipeline JSON Schema (raw JSON string).</summary>
    public string? SchemaJson { get; set; }
}

/// <summary>Response for get_pipeline_status / deploy_pipeline.</summary>
public class PipelineDeploymentResponse : CommunicationResponse
{
    /// <summary>Pipeline runtime ID.</summary>
    public string? PipelineId { get; set; }

    /// <summary>Deployment result payload.</summary>
    public DeploymentResultDto? DeploymentResult { get; set; }
}

/// <summary>Response for execute_pipeline.</summary>
public class ExecutePipelineResponse : CommunicationResponse
{
    /// <summary>Pipeline runtime ID.</summary>
    public string? PipelineId { get; set; }

    /// <summary>ID of the started execution.</summary>
    public string? ExecutionId { get; set; }
}

/// <summary>
/// Response for dry_run_pipeline (M4-B.2). Carries the execution id and the
/// SDK-side catalog of Load nodes that DO honour the dry-run flag so the agent
/// can compare against the debug stream and reason about which side effects
/// (if any) might have fired despite the dry-run setting.
/// </summary>
public class DryRunPipelineResponse : CommunicationResponse
{
    /// <summary>Pipeline runtime ID.</summary>
    public string? PipelineId { get; set; }

    /// <summary>ID of the started dry-run execution.</summary>
    public string? ExecutionId { get; set; }

    /// <summary>
    /// NodeName@Version keys of every SDK-shipped Load node that honours
    /// <c>IPipelineExecutionMode.IsDryRun</c>. The agent compares this set against
    /// the actual Load nodes in the pipeline definition (read via
    /// <c>get_pipeline_schema</c>) to populate the converse —
    /// <c>LoadNodesNotHonouringDryRun</c> — locally. Adapter-specific Load nodes
    /// (Modbus, IEC, OPC-UA, etc.) opt in via their own catalog and are NOT
    /// listed here.
    /// </summary>
    public string[]? SdkHonouredLoadNodes { get; set; }
}

/// <summary>Response for set_pipeline_debug.</summary>
public class SetPipelineDebugResponse : CommunicationResponse
{
    /// <summary>Pipeline runtime ID.</summary>
    public string? PipelineId { get; set; }

    /// <summary>Resulting debug state from the server.</summary>
    public SetPipelineDebugResultDto? Result { get; set; }
}

/// <summary>Response for get_pipeline_debug.</summary>
public class GetPipelineDebugResponse : CommunicationResponse
{
    /// <summary>Pipeline runtime ID.</summary>
    public string? PipelineId { get; set; }

    /// <summary>Persisted debug state.</summary>
    public PipelineDebugStateDto? State { get; set; }
}

/// <summary>Response for get_pipeline_executions.</summary>
public class GetPipelineExecutionsResponse : CommunicationResponse
{
    /// <summary>Pipeline runtime ID.</summary>
    public string? PipelineId { get; set; }

    /// <summary>Execution history.</summary>
    public List<PipelineExecutionDataDto> Executions { get; set; } = [];

    /// <summary>Total number of executions.</summary>
    public int TotalCount { get; set; }
}

/// <summary>Response for get_latest_pipeline_execution.</summary>
public class GetLatestPipelineExecutionResponse : CommunicationResponse
{
    /// <summary>Pipeline runtime ID.</summary>
    public string? PipelineId { get; set; }

    /// <summary>Latest execution payload.</summary>
    public PipelineExecutionDataDto? Execution { get; set; }
}

/// <summary>Response for get_pipeline_debug_points.</summary>
public class GetPipelineDebugPointsResponse : CommunicationResponse
{
    /// <summary>Pipeline runtime ID.</summary>
    public string? PipelineId { get; set; }

    /// <summary>Execution ID that was queried.</summary>
    public Guid ExecutionId { get; set; }

    /// <summary>Raw JSON of debug point nodes for the execution.</summary>
    public string? DebugPointsJson { get; set; }
}

/// <summary>Generic response when a single named resource is acted on (triggers, data flows, workloads).</summary>
public class CommunicationActionResponse : CommunicationResponse
{
    /// <summary>Runtime ID of the resource that was acted on.</summary>
    public string? ResourceId { get; set; }
}

/// <summary>Response for get_pools.</summary>
public class GetPoolsResponse : CommunicationResponse
{
    /// <summary>Pools configured for the tenant.</summary>
    public List<PoolSummaryDto> Pools { get; set; } = [];

    /// <summary>Total number of pools.</summary>
    public int TotalCount { get; set; }
}

/// <summary>Response for get_data_flow_status.</summary>
public class GetDataFlowStatusResponse : CommunicationResponse
{
    /// <summary>Data flow runtime ID.</summary>
    public string? DataFlowId { get; set; }

    /// <summary>Aggregated execution status payload.</summary>
    public DataFlowStatusDto? Status { get; set; }
}

/// <summary>Response for get_workloads_by_chart.</summary>
public class GetWorkloadsByChartResponse : CommunicationResponse
{
    /// <summary>Chart name that was queried.</summary>
    public string? ChartName { get; set; }

    /// <summary>Workloads referencing that chart.</summary>
    public List<WorkloadSummaryDto> Workloads { get; set; } = [];

    /// <summary>Total number of workloads.</summary>
    public int TotalCount { get; set; }
}

/// <summary>Response for move_pipelines.</summary>
public class MovePipelinesResponse : CommunicationResponse
{
    /// <summary>Target adapter the pipelines were moved to.</summary>
    public string? TargetAdapterId { get; set; }

    /// <summary>Per-pipeline outcome list from the bulk move.</summary>
    public MovePipelinesToAdapterResponseDto? Result { get; set; }

    /// <summary>How many of the requested moves succeeded.</summary>
    public int SuccessCount { get; set; }

    /// <summary>How many failed (atomic per-pipeline, batch does not abort).</summary>
    public int FailureCount { get; set; }
}

/// <summary>
///     Response for <c>validate_pipeline_definition</c> (M4-B.1). Distinguishes "tool
///     call succeeded but the definition has schema errors" (<see cref="IsValid"/>=false,
///     <see cref="CommunicationResponse.IsSuccess"/>=true, <see cref="Errors"/> populated) from
///     "tool call itself failed" (<see cref="CommunicationResponse.IsSuccess"/>=false).
/// </summary>
public class ValidatePipelineDefinitionResponse : CommunicationResponse
{
    /// <summary>Adapter runtime ID the schema validation ran against.</summary>
    public string? AdapterId { get; set; }

    /// <summary>
    ///     Whether the pipeline definition satisfies the adapter's composite JSON Schema.
    ///     Distinct from <see cref="CommunicationResponse.IsSuccess"/> — a successful tool call
    ///     can still report an invalid definition with errors.
    /// </summary>
    public bool IsValid { get; set; }

    /// <summary>
    ///     Number of nodes the validator counted in the definition's <c>nodes[]</c> array.
    ///     Sanity gauge — an empty pipeline can pass schema but is rarely intended.
    /// </summary>
    public int NodeCount { get; set; }

    /// <summary>Per-error list. Empty when <see cref="IsValid"/> is true.</summary>
    public List<ValidatePipelineDefinitionError> Errors { get; set; } = new();
}

/// <summary>One validation error from <c>validate_pipeline_definition</c>.</summary>
public class ValidatePipelineDefinitionError
{
    /// <summary>JSON pointer into the pipeline definition where the error occurred.</summary>
    public required string Path { get; set; }

    /// <summary>JSON pointer into the schema document that triggered the error.</summary>
    public string? SchemaPath { get; set; }

    /// <summary>Schema keyword that fired (e.g. <c>required</c>, <c>type</c>, <c>enum</c>).</summary>
    public string? Keyword { get; set; }

    /// <summary>Human-readable error message from the schema evaluator.</summary>
    public required string Message { get; set; }
}
