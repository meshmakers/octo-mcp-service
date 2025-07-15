// ReSharper disable UnusedAutoPropertyAccessor.Global
namespace Meshmakers.Octo.Backend.McpServices.Models;

/// <summary>
/// Information about a single tool parameter
/// </summary>
public sealed class ToolParameterInfo
{
    /// <summary>
    /// Name of the parameter
    /// </summary>
    public required string Name { get; init; }
    
    /// <summary>
    /// Data type of the parameter (e.g., 'string', 'integer', 'boolean')
    /// </summary>
    public required string Type { get; init; }
    
    /// <summary>
    /// Indicates if this parameter is optional
    /// </summary>
    public required bool IsOptional { get; init; }
    
    /// <summary>
    /// Default value if the parameter is optional
    /// </summary>
    public string? DefaultValue { get; init; }
    
    /// <summary>
    /// Description of what this parameter does
    /// </summary>
    public required string Description { get; init; }
}

/// <summary>
/// Basic information about an available tool
/// </summary>
public sealed class ToolInfo
{
    /// <summary>
    /// Name of the tool as used in MCP calls
    /// </summary>
    public required string Name { get; init; }
    
    /// <summary>
    /// Category this tool belongs to (e.g., 'CRUD Operations', 'Schema Discovery')
    /// </summary>
    public required string Category { get; init; }
    
    /// <summary>
    /// Human-readable description of what the tool does
    /// </summary>
    public required string Description { get; init; }
    
    /// <summary>
    /// .NET class name containing the tool implementation
    /// </summary>
    public required string ClassName { get; init; }
    
    /// <summary>
    /// .NET method name implementing the tool
    /// </summary>
    public required string MethodName { get; init; }
    
    /// <summary>
    /// List of parameters this tool accepts
    /// </summary>
    public required List<ToolParameterInfo> Parameters { get; init; }
    
    /// <summary>
    /// Total number of parameters
    /// </summary>
    public required int ParameterCount { get; init; }
    
    /// <summary>
    /// Indicates if this tool has any optional parameters
    /// </summary>
    public required bool HasOptionalParams { get; init; }
}

/// <summary>
/// Response for listing available tools
/// </summary>
public sealed class ListAvailableToolsResponse
{
    /// <summary>
    /// Total number of available tools
    /// </summary>
    public required int TotalTools { get; init; }
    
    /// <summary>
    /// Breakdown of tools by category with counts
    /// </summary>
    public required Dictionary<string, int> Categories { get; init; }
    
    /// <summary>
    /// Category filter that was applied, if any
    /// </summary>
    public string? CategoryFilter { get; init; }
    
    /// <summary>
    /// List of available tools
    /// </summary>
    public required List<ToolInfo> Tools { get; init; }
}

/// <summary>
/// Usage example for a tool
/// </summary>
public sealed class ToolUsageExample
{
    /// <summary>
    /// Description of what this example demonstrates
    /// </summary>
    public required string Description { get; init; }
    
    /// <summary>
    /// Example parameters to use with the tool
    /// </summary>
    public required object Parameters { get; init; }
}

/// <summary>
/// Detailed information about a specific tool
/// </summary>
public sealed class ToolDetailsResponse
{
    /// <summary>
    /// Name of the tool
    /// </summary>
    public required string Name { get; init; }
    
    /// <summary>
    /// Category the tool belongs to
    /// </summary>
    public required string Category { get; init; }
    
    /// <summary>
    /// Detailed description of the tool's functionality
    /// </summary>
    public required string Description { get; init; }
    
    /// <summary>
    /// .NET class name implementing the tool
    /// </summary>
    public required string ClassName { get; init; }
    
    /// <summary>
    /// .NET method name implementing the tool
    /// </summary>
    public required string MethodName { get; init; }
    
    /// <summary>
    /// Return type of the tool method
    /// </summary>
    public required string ReturnType { get; init; }
    
    /// <summary>
    /// All parameters accepted by this tool
    /// </summary>
    public required List<ToolParameterInfo> Parameters { get; init; }
    
    /// <summary>
    /// Parameters that must be provided (no default values)
    /// </summary>
    public required List<ToolParameterInfo> RequiredParameters { get; init; }
    
    /// <summary>
    /// Parameters that have default values and are optional
    /// </summary>
    public required List<ToolParameterInfo> OptionalParameters { get; init; }
    
    /// <summary>
    /// Example usage scenarios for this tool
    /// </summary>
    public required List<ToolUsageExample> UsageExamples { get; init; }
    
    /// <summary>
    /// Additional notes and tips for using this tool
    /// </summary>
    public required List<string> Notes { get; init; }

    /// <summary>
    /// Return type description, if applicable
    /// </summary>
    public string? ReturnDescription { get; init; }
}

/// <summary>
/// Statistics about tool performance
/// </summary>
public sealed class ToolStatistics
{
    /// <summary>
    /// Time range these statistics cover
    /// </summary>
    public required string TimeRange { get; init; }
    
    /// <summary>
    /// When these statistics were generated
    /// </summary>
    public required DateTime GeneratedAt { get; init; }
    
    /// <summary>
    /// Total number of tool invocations in the time period
    /// </summary>
    public required int TotalInvocations { get; init; }
    
    /// <summary>
    /// Number of unique tools that were used
    /// </summary>
    public required int UniqueTools { get; init; }
    
    /// <summary>
    /// Average response time across all tools
    /// </summary>
    public required string AverageResponseTime { get; init; }
    
    /// <summary>
    /// Overall success rate as a percentage
    /// </summary>
    public required double SuccessRate { get; init; }
    
    /// <summary>
    /// Most frequently used tools
    /// </summary>
    public required List<TopToolInfo> TopTools { get; init; }
    
    /// <summary>
    /// Breakdown of tool usage by category
    /// </summary>
    public required CategoryBreakdownInfo CategoryBreakdown { get; init; }
    
    /// <summary>
    /// Error statistics and common issues
    /// </summary>
    public required ErrorStatistics ErrorStats { get; init; }
    
    /// <summary>
    /// Performance metrics for tools
    /// </summary>
    public required PerformanceMetrics Performance { get; init; }
}

/// <summary>
/// Information about a frequently used tool
/// </summary>
public sealed class TopToolInfo
{
    /// <summary>
    /// Name of the tool
    /// </summary>
    public required string Name { get; init; }
    
    /// <summary>
    /// Number of times this tool was called
    /// </summary>
    public required int Invocations { get; init; }
    
    /// <summary>
    /// Average response time for this tool
    /// </summary>
    public required string AvgResponseTime { get; init; }
}

/// <summary>
/// Breakdown of tool usage by category
/// </summary>
public sealed class CategoryBreakdownInfo
{
    /// <summary>
    /// Percentage of CRUD operation calls
    /// </summary>
    public required double Crud { get; init; }
    
    /// <summary>
    /// Percentage of analytics calls
    /// </summary>
    public required double Analytics { get; init; }
    
    /// <summary>
    /// Percentage of discovery calls
    /// </summary>
    public required double Discovery { get; init; }
    
    /// <summary>
    /// Percentage of maintenance calls
    /// </summary>
    public required double Maintenance { get; init; }
    
    /// <summary>
    /// Percentage of management calls
    /// </summary>
    public required double Management { get; init; }
}

/// <summary>
/// Error statistics for tool usage
/// </summary>
public sealed class ErrorStatistics
{
    /// <summary>
    /// Total number of errors in the time period
    /// </summary>
    public required int TotalErrors { get; init; }
    
    /// <summary>
    /// Most common error types
    /// </summary>
    public required List<CommonErrorInfo> CommonErrors { get; init; }
}

/// <summary>
/// Information about a common error
/// </summary>
public sealed class CommonErrorInfo
{
    /// <summary>
    /// Error message or type
    /// </summary>
    public required string Error { get; init; }
    
    /// <summary>
    /// Number of times this error occurred
    /// </summary>
    public required int Count { get; init; }
}

/// <summary>
/// Performance metrics for tools
/// </summary>
public sealed class PerformanceMetrics
{
    /// <summary>
    /// Tool with the best average response time
    /// </summary>
    public required PerformanceToolInfo FastestTool { get; init; }
    
    /// <summary>
    /// Tool with the worst average response time
    /// </summary>
    public required PerformanceToolInfo SlowestTool { get; init; }
    
    /// <summary>
    /// Tool with the highest success rate
    /// </summary>
    public required ReliabilityToolInfo MostReliable { get; init; }
    
    /// <summary>
    /// Tool with the lowest success rate
    /// </summary>
    public required ReliabilityToolInfo LeastReliable { get; init; }
}

/// <summary>
/// Performance information for a specific tool
/// </summary>
public sealed class PerformanceToolInfo
{
    /// <summary>
    /// Name of the tool
    /// </summary>
    public required string Name { get; init; }
    
    /// <summary>
    /// Average response time
    /// </summary>
    public required string AvgTime { get; init; }
}

/// <summary>
/// Reliability information for a specific tool
/// </summary>
public sealed class ReliabilityToolInfo
{
    /// <summary>
    /// Name of the tool
    /// </summary>
    public required string Name { get; init; }
    
    /// <summary>
    /// Success rate as a percentage
    /// </summary>
    public required double SuccessRate { get; init; }
}

/// <summary>
/// Parameter validation result for a single parameter
/// </summary>
public sealed class ParameterValidationResult
{
    /// <summary>
    /// Name of the parameter that was validated
    /// </summary>
    public required string Parameter { get; init; }
    
    /// <summary>
    /// Validation status (e.g., 'valid', 'invalid', 'missing')
    /// </summary>
    public required string Status { get; init; }
    
    /// <summary>
    /// Expected type for this parameter
    /// </summary>
    public required string Type { get; init; }
    
    /// <summary>
    /// Value that was provided for validation
    /// </summary>
    public string? ProvidedValue { get; init; }
}

/// <summary>
/// Summary of parameter validation results
/// </summary>
public sealed class ValidationSummary
{
    /// <summary>
    /// Total number of parameters provided
    /// </summary>
    public required int TotalProvided { get; init; }
    
    /// <summary>
    /// Number of required parameters that are missing
    /// </summary>
    public required int RequiredMissing { get; init; }
    
    /// <summary>
    /// Number of unknown parameters that will be ignored
    /// </summary>
    public required int UnknownParams { get; init; }
    
    /// <summary>
    /// Recommendation based on validation results
    /// </summary>
    public required string Recommendation { get; init; }
}

/// <summary>
/// Response for parameter validation
/// </summary>
public sealed class ValidateParametersResponse
{
    /// <summary>
    /// Indicates if all provided parameters are valid
    /// </summary>
    public required bool IsValid { get; init; }
    
    /// <summary>
    /// Name of the tool being validated
    /// </summary>
    public required string ToolName { get; init; }
    
    /// <summary>
    /// List of parameter names that were provided
    /// </summary>
    public required List<string> ProvidedParameters { get; init; }
    
    /// <summary>
    /// Detailed validation results for each parameter
    /// </summary>
    public required List<ParameterValidationResult> ValidationResults { get; init; }
    
    /// <summary>
    /// Warning messages about the provided parameters
    /// </summary>
    public required List<string> Warnings { get; init; }
    
    /// <summary>
    /// Error messages about invalid or missing parameters
    /// </summary>
    public required List<string> Errors { get; init; }
    
    /// <summary>
    /// Summary of the validation results
    /// </summary>
    public required ValidationSummary Summary { get; init; }
}

/// <summary>
/// Error response for tool management operations
/// </summary>
public sealed class ToolManagementError
{
    /// <summary>
    /// Short error description
    /// </summary>
    public required string Error { get; init; }
    
    /// <summary>
    /// Detailed error message
    /// </summary>
    public required string Message { get; init; }
    
    /// <summary>
    /// Tool name related to the error, if applicable
    /// </summary>
    public string? ToolName { get; init; }
    
    /// <summary>
    /// Suggestion for resolving the error
    /// </summary>
    public string? Suggestion { get; init; }
}
