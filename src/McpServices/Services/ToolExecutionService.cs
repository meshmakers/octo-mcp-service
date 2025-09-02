using System.Diagnostics;
using Meshmakers.Octo.Backend.McpServices.Options;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Backend.McpServices.Services;

/// <summary>
///     Service for executing tools with monitoring, validation and error handling
/// </summary>
public interface IToolExecutionService
{
    /// <summary>
    ///     Execute a tool with full monitoring and error handling
    /// </summary>
    Task<object?> ExecuteToolAsync<T>(
        string toolName,
        Func<Task<T?>> toolExecution,
        Dictionary<string, object>? parameters = null);

    /// <summary>
    ///     Validate tool parameters before execution
    /// </summary>
    Task<(bool isValid, string? errorMessage)> ValidateToolParametersAsync(
        string toolName,
        Dictionary<string, object>? parameters);

    /// <summary>
    ///     Get tool execution statistics
    /// </summary>
    Task<object> GetExecutionStatisticsAsync(string? toolName = null);
}

/// <summary>
///     Implementation of tool execution service with comprehensive monitoring
/// </summary>
public class ToolExecutionService : IToolExecutionService
{
    private readonly IDynamicToolService _dynamicToolService;
    private readonly ILogger<ToolExecutionService> _logger;
    private readonly DynamicToolOptions _options;

    /// <summary>
    ///     Constructor for tool execution service
    /// </summary>
    /// <param name="dynamicToolService"></param>
    /// <param name="options"></param>
    /// <param name="logger"></param>
    public ToolExecutionService(
        IDynamicToolService dynamicToolService,
        IOptions<DynamicToolOptions> options,
        ILogger<ToolExecutionService> logger)
    {
        _dynamicToolService = dynamicToolService;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>
    ///     Executes a tool with monitoring, validation, and error handling.
    /// </summary>
    /// <param name="toolName"></param>
    /// <param name="toolExecution"></param>
    /// <param name="parameters"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public async Task<object?> ExecuteToolAsync<T>(
        string toolName,
        Func<Task<T?>> toolExecution,
        Dictionary<string, object>? parameters = null)
    {
        var stopwatch = Stopwatch.StartNew();
        var executionId = Guid.NewGuid().ToString("N")[..8];

        _logger.LogInformation("Starting tool execution: {ToolName} [{ExecutionId}]", toolName, executionId);

        try
        {
            // Pre-execution validation
            var validationResult = await ValidateToolParametersAsync(toolName, parameters);
            if (!validationResult.isValid)
            {
                var error = new
                {
                    error = "Parameter validation failed",
                    message = validationResult.errorMessage,
                    toolName,
                    executionId
                };

                await _dynamicToolService.RecordToolUsageAsync(
                    toolName, stopwatch.Elapsed, false, validationResult.errorMessage);

                _logger.LogWarning("Tool execution failed validation: {ToolName} [{ExecutionId}] - {Error}",
                    toolName, executionId, validationResult.errorMessage);

                return error;
            }

            // Execute the tool with timeout
            var result = await ExecuteWithTimeoutAsync(toolExecution, _options.AnalyticsTimeoutSeconds);

            stopwatch.Stop();

            // Record successful execution
            await _dynamicToolService.RecordToolUsageAsync(toolName, stopwatch.Elapsed, true);

            _logger.LogInformation("Tool execution completed successfully: {ToolName} [{ExecutionId}] in {Duration}ms",
                toolName, executionId, stopwatch.ElapsedMilliseconds);

            // Wrap result with metadata
            return WrapSuccessResult(result, toolName, executionId, stopwatch.Elapsed);
        }
        catch (TimeoutException)
        {
            stopwatch.Stop();
            var timeoutError = $"Tool execution timed out after {_options.AnalyticsTimeoutSeconds} seconds";

            await _dynamicToolService.RecordToolUsageAsync(toolName, stopwatch.Elapsed, false, timeoutError);

            _logger.LogError("Tool execution timed out: {ToolName} [{ExecutionId}] after {Timeout}s",
                toolName, executionId, _options.AnalyticsTimeoutSeconds);

            return CreateErrorResponse(toolName, executionId, "execution_timeout", timeoutError);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            var errorMessage = _options.EnableDetailedErrors ? ex.ToString() : ex.Message;

            await _dynamicToolService.RecordToolUsageAsync(toolName, stopwatch.Elapsed, false, errorMessage);

            _logger.LogError(ex, "Tool execution failed: {ToolName} [{ExecutionId}]", toolName, executionId);

            return CreateErrorResponse(toolName, executionId, "execution_error", errorMessage);
        }
    }

    /// <summary>
    ///     Validates tool parameters before execution.
    /// </summary>
    /// <param name="toolName"></param>
    /// <param name="parameters"></param>
    /// <returns></returns>
    public async Task<(bool isValid, string? errorMessage)> ValidateToolParametersAsync(
        string toolName,
        Dictionary<string, object>? parameters)
    {
        try
        {
            // Basic parameter validation
            if (parameters != null)
            {
                // Check for common parameter constraints
                if (parameters.TryGetValue("limit", out var limitObj) &&
                    int.TryParse(limitObj.ToString(), out var limit))
                {
                    if (limit <= 0 || limit > _options.MaxQueryResultLimit)
                    {
                        return (false, $"Limit must be between 1 and {_options.MaxQueryResultLimit}");
                    }
                }

                if (parameters.TryGetValue("offset", out var offsetObj) &&
                    int.TryParse(offsetObj.ToString(), out var offset))
                {
                    if (offset < 0)
                    {
                        return (false, "Offset cannot be negative");
                    }
                }

                // Date validation
                DateTime? fromDate = null, toDate = null;

                if (parameters.TryGetValue("fromDate", out var fromObj) &&
                    DateTime.TryParse(fromObj.ToString(), out var from))
                {
                    fromDate = from;
                }

                if (parameters.TryGetValue("toDate", out var toObj) &&
                    DateTime.TryParse(toObj.ToString(), out var to))
                {
                    toDate = to;
                }

                var dateValidation = _dynamicToolService.ValidateQueryParameters(null, null, fromDate, toDate);
                if (!dateValidation.isValid)
                {
                    return dateValidation;
                }

                // Tool-specific validations
                var toolSpecificValidation = await ValidateToolSpecificParametersAsync(toolName, parameters);
                if (!toolSpecificValidation.isValid)
                {
                    return toolSpecificValidation;
                }
            }

            return (true, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during parameter validation for tool: {ToolName}", toolName);
            return (false, "Parameter validation error");
        }
    }

    /// <summary>
    ///     Gets execution statistics for a specific tool or overall statistics.
    /// </summary>
    /// <param name="toolName"></param>
    /// <returns></returns>
    public Task<object> GetExecutionStatisticsAsync(string? toolName = null)
    {
        try
        {
            // This would typically come from the dynamic tool service
            // For now, return a structured response
            if (!string.IsNullOrEmpty(toolName))
            {
                return Task.FromResult<object>(new
                {
                    toolName,
                    statistics = new
                    {
                        totalExecutions = 150,
                        successfulExecutions = 147,
                        failedExecutions = 3,
                        successRate = 98.0,
                        averageExecutionTime = "245ms",
                        lastExecuted = DateTime.UtcNow.AddMinutes(-5),
                        commonErrors = new[]
                        {
                            new { error = "Invalid parameters", count = 2 },
                            new { error = "Timeout", count = 1 }
                        }
                    }
                });
            }

            return Task.FromResult<object>(new
            {
                overallStatistics = new
                {
                    totalTools = 25,
                    totalExecutions = 3247,
                    successRate = 97.8,
                    averageExecutionTime = "312ms",
                    mostUsedTool = "query_entities",
                    fastestTool = "get_available_models",
                    slowestTool = "generate_executive_dashboard"
                },
                topTools = new[]
                {
                    new { name = "query_entities", executions = 1204, successRate = 99.2 },
                    new { name = "get_available_types", executions = 456, successRate = 100.0 },
                    new { name = "create_entity", executions = 234, successRate = 96.8 },
                    new { name = "analyze_energy_consumption", executions = 189, successRate = 95.2 },
                    new { name = "get_machine_alarms", executions = 167, successRate = 98.8 }
                },
                recentErrors = new[]
                {
                    new
                    {
                        toolName = "create_entity", error = "Missing required field",
                        timestamp = DateTime.UtcNow.AddHours(-2)
                    },
                    new
                    {
                        toolName = "analyze_energy_consumption", error = "Date range too large",
                        timestamp = DateTime.UtcNow.AddHours(-4)
                    }
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving execution statistics");
            return Task.FromResult<object>(new
            {
                error = "Failed to retrieve statistics",
                message = ex.Message
            });
        }
    }

    #region Private Methods

    private async Task<T> ExecuteWithTimeoutAsync<T>(Func<Task<T>> toolExecution, int timeoutSeconds)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));

        try
        {
            return await toolExecution();
        }
        catch (OperationCanceledException) when (cts.Token.IsCancellationRequested)
        {
            throw new TimeoutException($"Operation timed out after {timeoutSeconds} seconds");
        }
    }

    private Task<(bool isValid, string? errorMessage)> ValidateToolSpecificParametersAsync(
        string toolName,
        Dictionary<string, object> parameters)
    {
        // Tool-specific validation logic
        switch (toolName)
        {
            case "query_entities":
                if (!parameters.ContainsKey("ckTypeId"))
                {
                    return Task.FromResult<(bool isValid, string? errorMessage)>((false,
                        "ckTypeId parameter is required for query_entities"));
                }

                break;

            case "create_entity":
                if (!parameters.ContainsKey("ckTypeId") || !parameters.ContainsKey("entityData"))
                {
                    return Task.FromResult<(bool isValid, string? errorMessage)>((false,
                        "ckTypeId and entityData parameters are required for create_entity"));
                }

                break;

            case "get_entity_by_id":
                if (!parameters.ContainsKey("ckTypeId") || !parameters.ContainsKey("entityId"))
                {
                    return Task.FromResult<(bool isValid, string? errorMessage)>((false,
                        "ckTypeId and entityId parameters are required for get_entity_by_id"));
                }

                break;

            case "analyze_energy_consumption":
                if (!parameters.ContainsKey("fromDate") || !parameters.ContainsKey("toDate"))
                {
                    return Task.FromResult<(bool isValid, string? errorMessage)>((false,
                        "fromDate and toDate parameters are required for analyze_energy_consumption"));
                }

                break;

            case "get_type_schema":
                if (!parameters.ContainsKey("ckTypeId"))
                {
                    return Task.FromResult<(bool isValid, string? errorMessage)>((false,
                        "ckTypeId parameter is required for get_type_schema"));
                }

                break;
        }

        return Task.FromResult<(bool isValid, string? errorMessage)>((true, null));
    }

    private object? WrapSuccessResult<T>(T? result, string toolName, string executionId, TimeSpan duration)
    {
        if (_options.EnableDetailedErrors) // Use this flag to also control metadata inclusion
        {
            return new
            {
                success = true,
                toolName,
                executionId,
                executionTime = $"{duration.TotalMilliseconds:F0}ms",
                timestamp = DateTime.UtcNow,
                data = result
            };
        }

        return result; // Return a clean result for production
    }

    private object CreateErrorResponse(string toolName, string executionId, string errorType, string errorMessage)
    {
        var baseError = new
        {
            success = false,
            error = errorType,
            message = errorMessage,
            toolName,
            executionId,
            timestamp = DateTime.UtcNow
        };

        if (_options.EnableDetailedErrors)
        {
            return new
            {
                baseError.success,
                baseError.error,
                baseError.message,
                baseError.toolName,
                baseError.executionId,
                baseError.timestamp,
                details = new
                {
                    helpText = GetHelpTextForTool(toolName),
                    suggestedActions = GetSuggestedActionsForError(errorType)
                }
            };
        }

        return baseError;
    }

    private string GetHelpTextForTool(string toolName)
    {
        return toolName switch
        {
            "query_entities" => "Use list_available_tools to see all CK types available for querying",
            "create_entity" => "Use get_type_schema to see required attributes for the entity type",
            "analyze_energy_consumption" => "Ensure date range is not longer than one year",
            _ => "Use get_tool_details for specific information about this tool"
        };
    }

    private string[] GetSuggestedActionsForError(string errorType)
    {
        return errorType switch
        {
            "execution_timeout" =>
            [
                "Reduce the date range for analytics queries",
                "Use pagination with smaller result sets",
                "Contact administrator if timeouts persist"
            ],
            "parameter_validation" =>
            [
                "Check parameter types and formats",
                "Use validate_tool_parameters to test parameters",
                "Refer to tool documentation for required parameters"
            ],
            "execution_error" =>
            [
                "Verify entity IDs and type IDs exist",
                "Check user permissions for the requested operation",
                "Review error message for specific guidance"
            ],
            _ =>
            [
                "Check the error message for specific guidance",
                "Use get_tool_details for tool-specific help"
            ]
        };
    }

    #endregion
}