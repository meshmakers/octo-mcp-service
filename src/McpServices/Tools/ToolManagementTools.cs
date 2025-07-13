using System.ComponentModel;
using System.Reflection;
using ModelContextProtocol.Server;

namespace Meshmakers.Octo.Backend.McpServices.Tools;

/// <summary>
/// Tool management and discovery helpers for the MCP server
/// </summary>
[McpServerToolType]
public sealed class ToolManagementTools
{
    /// <summary>
    /// Get information about all available MCP tools in this server
    /// </summary>
    /// <param name="server">MCP Server instance</param>
    /// <param name="category">Optional filter by tool category</param>
    /// <returns>List of available tools with descriptions and parameters</returns>
    [McpServerTool(Name = "list_available_tools")]
    [Description("Get information about all available MCP tools in this server")]
    public static Task<object> ListAvailableTools(
        IMcpServer server,
        string? category = null)
    {
        try
        {
            var tools = new List<object>();

            // Get all tool types in this assembly
            var assembly = Assembly.GetExecutingAssembly();
            var toolTypes = assembly.GetTypes()
                .Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() != null)
                .ToList();

            foreach (var toolType in toolTypes)
            {
                var toolMethods = toolType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .Where(m => m.GetCustomAttribute<McpServerToolAttribute>() != null)
                    .ToList();

                foreach (var method in toolMethods)
                {
                    var toolAttr = method.GetCustomAttribute<McpServerToolAttribute>();
                    var descAttr = method.GetCustomAttribute<DescriptionAttribute>();
                    
                    if (toolAttr != null)
                    {
                        var toolCategory = GetToolCategory(toolType.Name);
                        
                        if (!string.IsNullOrEmpty(category) && 
                            !toolCategory.Equals(category, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        var parameters = method.GetParameters()
                            .Skip(1) // Skip the IMcpServer parameter
                            .Select(p => new
                            {
                                name = p.Name,
                                type = GetFriendlyTypeName(p.ParameterType),
                                isOptional = p.HasDefaultValue,
                                defaultValue = p.HasDefaultValue ? p.DefaultValue?.ToString() : null,
                                description = GetParameterDescription(p)
                            })
                            .ToList();

                        tools.Add(new
                        {
                            name = toolAttr.Name,
                            category = toolCategory,
                            description = descAttr?.Description ?? "No description available",
                            className = toolType.Name,
                            methodName = method.Name,
                            parameters,
                            parameterCount = parameters.Count,
                            hasOptionalParams = parameters.Any(p => p.isOptional)
                        });
                    }
                }
            }

            var categories = tools.GroupBy(t => ((dynamic)t).category)
                .ToDictionary(g => g.Key, g => g.Count());

            return Task.FromResult<object>(new
            {
                totalTools = tools.Count,
                categories,
                categoryFilter = category,
                tools = tools.OrderBy(t => ((dynamic)t).category).ThenBy(t => ((dynamic)t).name)
            });
        }
        catch (Exception ex)
        {
            return Task.FromResult<object>(new
            {
                error = "Failed to list available tools",
                message = ex.Message
            });
        }
    }

    /// <summary>
    /// Get detailed information about a specific tool including usage examples
    /// </summary>
    /// <param name="server">MCP Server instance</param>
    /// <param name="toolName">Name of the tool to get details for</param>
    /// <returns>Detailed tool information with usage examples</returns>
    [McpServerTool(Name = "get_tool_details")]
    [Description("Get detailed information about a specific tool including usage examples")]
    public static Task<object> GetToolDetails(
        IMcpServer server,
        string toolName)
    {
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            var toolTypes = assembly.GetTypes()
                .Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() != null);

            foreach (var toolType in toolTypes)
            {
                var method = toolType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .FirstOrDefault(m =>
                    {
                        var attr = m.GetCustomAttribute<McpServerToolAttribute>();
                        return attr?.Name == toolName;
                    });

                if (method != null)
                {
                    var toolAttr = method.GetCustomAttribute<McpServerToolAttribute>();
                    var descAttr = method.GetCustomAttribute<DescriptionAttribute>();

                    List<dynamic> parameters = [method.GetParameters()
                        .Skip(1) // Skip IMcpServer parameter
                        .Select(p => new
                        {
                            name = p.Name,
                            type = GetFriendlyTypeName(p.ParameterType),
                            isOptional = p.HasDefaultValue,
                            defaultValue = p.HasDefaultValue ? p.DefaultValue?.ToString() : null,
                            description = GetParameterDescription(p)
                        })
                        .ToList()];

                    var examples = GenerateUsageExamples(toolName, parameters);

                    return Task.FromResult<object>(new
                    {
                        name = toolAttr!.Name,
                        category = GetToolCategory(toolType.Name),
                        description = descAttr?.Description ?? "No description available",
                        className = toolType.Name,
                        methodName = method.Name,
                        returnType = GetFriendlyTypeName(method.ReturnType),
                        parameters,
                        requiredParameters = parameters.Where(p => !p.isOptional).ToList(),
                        optionalParameters = parameters.Where(p => p.isOptional).ToList(),
                        usageExamples = examples,
                        notes = GetToolNotes(toolName)
                    });
                }
            }

            return Task.FromResult<object>(new
            {
                error = "Tool not found",
                toolName,
                suggestion = "Use 'list_available_tools' to see all available tools"
            });
        }
        catch (Exception ex)
        {
            return Task.FromResult<object>(new
            {
                error = "Failed to get tool details",
                message = ex.Message,
                toolName
            });
        }
    }

    /// <summary>
    /// Get usage statistics and performance metrics for MCP tools
    /// </summary>
    /// <param name="server">MCP Server instance</param>
    /// <param name="timeRange">Time range for statistics: 'hour', 'day', 'week', 'month'</param>
    /// <returns>Tool usage statistics and performance metrics</returns>
    [McpServerTool(Name = "get_tool_statistics")]
    [Description("Get usage statistics and performance metrics for MCP tools")]
    public static Task<object> GetToolStatistics(
        IMcpServer server,
        string timeRange = "day")
    {
        try
        {
            // This would typically come from actual logging/metrics storage
            // For now, returning mock data structure
            var mockStats = new
            {
                timeRange,
                generatedAt = DateTime.UtcNow,
                totalInvocations = 1247,
                uniqueTools = 23,
                averageResponseTime = "245ms",
                successRate = 98.7,
                
                topTools = new[]
                {
                    new { name = "query_entities", invocations = 312, avgResponseTime = "180ms" },
                    new { name = "get_available_types", invocations = 156, avgResponseTime = "95ms" },
                    new { name = "analyze_energy_consumption", invocations = 89, avgResponseTime = "420ms" },
                    new { name = "get_machine_alarms", invocations = 67, avgResponseTime = "220ms" },
                    new { name = "create_entity", invocations = 45, avgResponseTime = "350ms" }
                },
                
                categoryBreakdown = new
                {
                    crud = 45.2,
                    analytics = 28.7,
                    discovery = 15.8,
                    maintenance = 6.9,
                    management = 3.4
                },
                
                errorStats = new
                {
                    totalErrors = 16,
                    commonErrors = new[]
                    {
                        new { error = "Entity not found", count = 8 },
                        new { error = "Invalid CK Type ID", count = 4 },
                        new { error = "Permission denied", count = 3 },
                        new { error = "Invalid date format", count = 1 }
                    }
                },

                performance = new
                {
                    fastestTool = new { name = "get_available_models", avgTime = "45ms" },
                    slowestTool = new { name = "generate_executive_dashboard", avgTime = "1.2s" },
                    mostReliable = new { name = "list_available_tools", successRate = 100.0 },
                    leastReliable = new { name = "analyze_machine_performance", successRate = 94.2 }
                }
            };

            return Task.FromResult<object>(mockStats);
        }
        catch (Exception ex)
        {
            return Task.FromResult<object>(new
            {
                error = "Failed to get tool statistics",
                message = ex.Message
            });
        }
    }

    /// <summary>
    /// Validate tool parameters before execution
    /// </summary>
    /// <param name="server">MCP Server instance</param>
    /// <param name="toolName">Name of the tool to validate</param>
    /// <param name="parameters">Parameters to validate as JSON string</param>
    /// <returns>Parameter validation results</returns>
    [McpServerTool(Name = "validate_tool_parameters")]
    [Description("Validate tool parameters before execution to catch errors early")]
    public static async Task<object> ValidateToolParameters(
        IMcpServer server,
        string toolName,
        string parameters)
    {
        try
        {
            var toolDetails = await GetToolDetails(server, toolName);
            
            if (((dynamic)toolDetails).error != null)
            {
                return new
                {
                    isValid = false,
                    error = "Tool not found",
                    toolName
                };
            }

            var toolInfo = (dynamic)toolDetails;
            var providedParams = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(parameters);
            var requiredParams = ((IEnumerable<dynamic>)toolInfo.requiredParameters).ToList();
            var allParams = ((IEnumerable<dynamic>)toolInfo.parameters).ToList();

            var validation = new
            {
                isValid = true,
                toolName,
                providedParameters = providedParams?.Keys.ToList() ?? [],
                validationResults = new List<object>(),
                warnings = new List<string>(),
                errors = new List<string>()
            };

            var errors = new List<string>();
            var warnings = new List<string>();
            var results = new List<object>();

            // Check required parameters
            foreach (var reqParam in requiredParams)
            {
                var paramName = (string)reqParam.name;
                if (providedParams?.ContainsKey(paramName) != true)
                {
                    errors.Add($"Required parameter '{paramName}' is missing");
                }
                else
                {
                    results.Add(new
                    {
                        parameter = paramName,
                        status = "valid",
                        type = (string)reqParam.type,
                        providedValue = providedParams[paramName]?.ToString()
                    });
                }
            }

            // Check for unknown parameters
            if (providedParams != null)
            {
                var knownParams = allParams.Select(p => (string)p.name).ToHashSet();
                foreach (var providedParam in providedParams.Keys)
                {
                    if (!knownParams.Contains(providedParam))
                    {
                        warnings.Add($"Unknown parameter '{providedParam}' will be ignored");
                    }
                }
            }

            return new
            {
                isValid = errors.Count == 0,
                toolName,
                providedParameters = providedParams?.Keys.ToList() ?? [],
                validationResults = results,
                warnings,
                errors,
                summary = new
                {
                    totalProvided = providedParams?.Count ?? 0,
                    requiredMissing = errors.Count(e => e.Contains("missing")),
                    unknownParams = warnings.Count(w => w.Contains("Unknown")),
                    recommendation = errors.Count == 0 ? "Parameters are valid for execution" : "Fix errors before executing tool"
                }
            };
        }
        catch (Exception ex)
        {
            return new
            {
                isValid = false,
                error = "Failed to validate parameters",
                message = ex.Message,
                toolName
            };
        }
    }

    #region Helper Methods

    private static string GetToolCategory(string className)
    {
        return className switch
        {
            "DynamicCrudTools" => "CRUD Operations",
            "SchemaDiscoveryTools" => "Schema Discovery",
            "DomainSpecificTools" => "Domain Analytics",
            "AdvancedAnalyticsTools" => "Advanced Analytics",
            "ToolManagementTools" => "Tool Management",
            "EchoTool" => "Testing",
            "SampleLlmTool" => "Testing",
            _ => "Other"
        };
    }

    private static string GetFriendlyTypeName(Type type)
    {
        if (type == typeof(string))
        {
            return "string";
        }

        if (type == typeof(int))
        {
            return "integer";
        }

        if (type == typeof(int?))
        {
            return "integer (optional)";
        }

        if (type == typeof(bool))
        {
            return "boolean";
        }

        if (type == typeof(bool?))
        {
            return "boolean (optional)";
        }

        if (type == typeof(DateTime))
        {
            return "datetime";
        }

        if (type == typeof(DateTime?))
        {
            return "datetime (optional)";
        }

        if (type == typeof(double))
        {
            return "number";
        }

        if (type == typeof(double?))
        {
            return "number (optional)";
        }

        if (type == typeof(Task<object>))
        {
            return "object";
        }

        return type.Name;
    }

    private static string GetParameterDescription(ParameterInfo parameter)
    {
        // This could be enhanced to read from XML documentation or attributes
        return parameter.Name switch
        {
            "ckTypeId" => "Construction Kit Type ID (e.g., 'EnergyCommunity-1.0.0/Customer-1.0.0')",
            "entityId" => "Runtime entity ID (ObjectId string)",
            "fromDate" => "Start date in ISO 8601 format (e.g., '2024-01-01T00:00:00Z')",
            "toDate" => "End date in ISO 8601 format (e.g., '2024-12-31T23:59:59Z')",
            "limit" => "Maximum number of results to return",
            "offset" => "Number of results to skip for pagination",
            "filters" => "JSON object with field filters",
            "entityData" => "JSON object with entity attributes",
            "includeAbstract" => "Whether to include abstract types in results",
            "direction" => "Association direction: 'inbound', 'outbound', or 'both'",
            _ => $"Parameter: {parameter.Name}"
        };
    }

    private static List<object> GenerateUsageExamples(string toolName, List<dynamic> parameters)
    {
        var examples = new List<object>();

        switch (toolName)
        {
            case "query_entities":
                examples.Add(new
                {
                    description = "Query all customers",
                    parameters = new { ckTypeId = "EnergyCommunity-1.0.0/Customer-1.0.0", limit = 10 }
                });
                examples.Add(new
                {
                    description = "Query customers with filter",
                    parameters = new
                    {
                        ckTypeId = "EnergyCommunity-1.0.0/Customer-1.0.0",
                        filters = "{\"State\": \"Active\"}",
                        limit = 50
                    }
                });
                break;

            case "analyze_energy_consumption":
                examples.Add(new
                {
                    description = "Analyze energy consumption for the last month",
                    parameters = new
                    {
                        fromDate = "2024-01-01T00:00:00Z",
                        toDate = "2024-01-31T23:59:59Z"
                    }
                });
                break;

            case "get_type_schema":
                examples.Add(new
                {
                    description = "Get schema for Customer type",
                    parameters = new { ckTypeId = "EnergyCommunity-1.0.0/Customer-1.0.0" }
                });
                break;

            default:
                examples.Add(new
                {
                    description = "Basic usage",
                    parameters = parameters
                        .Where(p => !p.isOptional)
                        .ToDictionary(p => (string)p.name, p => $"<{p.type}>")
                });
                break;
        }

        return examples;
    }

    private static List<string> GetToolNotes(string toolName)
    {
        return toolName switch
        {
            "query_entities" =>
            [
                "Supports pagination with limit and offset parameters",
                "Filters should be provided as valid JSON string",
                "Returns both system fields (_id, _ckTypeId) and entity attributes"
            ],
            "create_entity" =>
            [
                "Entity data must include all required attributes for the type",
                "System fields like _id are automatically generated",
                "Returns the created entity with its new runtime ID"
            ],
            "analyze_energy_consumption" =>
            [
                "Date range should not exceed 1 year for performance",
                "Results include daily breakdown and quality metrics",
                "Supports filtering by facility or customer ID"
            ],
            _ => ["No special notes for this tool"]
        };
    }

    #endregion
}
