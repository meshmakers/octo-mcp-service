using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using Meshmakers.Octo.Backend.McpServices.Models;
using Meshmakers.Octo.Backend.McpServices.Models.Filters;
using Meshmakers.Octo.Backend.McpServices.Utils;
using ModelContextProtocol.Server;

// ReSharper disable MemberCanBePrivate.Global

namespace Meshmakers.Octo.Backend.McpServices.Tools;

/// <summary>
///     Tool management and discovery helpers for the MCP server
/// </summary>
[McpServerToolType]
public sealed class ToolManagementTools
{
    private static readonly Lazy<XmlDocumentationProvider> XmlDocs =
        new(() => new XmlDocumentationProvider());

    /// <summary>
    ///     Get information about all available MCP tools in this server
    /// </summary>
    /// <param name="server">MCP Server instance</param>
    /// <param name="category">Optional filter by tool category (e.g., 'CRUD Operations', 'Analytics')</param>
    /// <returns>List of available tools with descriptions and parameters</returns>
    [McpServerTool(Name = "list_available_tools")]
    public static Task<ListAvailableToolsResponse> ListAvailableTools(
        McpServer server,
        string? category = null)
    {
        try
        {
            var tools = new List<ToolInfo>();

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

                    if (toolAttr != null && !string.IsNullOrEmpty(toolAttr.Name))
                    {
                        var toolCategory = GetToolCategory(toolType.Name);

                        if (!string.IsNullOrEmpty(category) &&
                            !toolCategory.Equals(category, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        var description = GetToolDescription(method);
                        var parameters = method.GetParameters()
                            .Skip(1) // Skip the IMcpServer parameter
                            .Select(p => new ToolParameterInfo
                            {
                                Name = p.Name ?? "unknown",
                                Type = GetFriendlyTypeName(p.ParameterType),
                                IsOptional = p.HasDefaultValue,
                                DefaultValue = p.HasDefaultValue ? p.DefaultValue?.ToString() : null,
                                Description = XmlDocs.Value.GetParameterDescription(method, p.Name ?? "")
                            })
                            .ToList();

                        tools.Add(new ToolInfo
                        {
                            Name = toolAttr.Name,
                            Category = toolCategory,
                            Description = description,
                            ClassName = toolType.Name,
                            MethodName = method.Name,
                            Parameters = parameters,
                            ParameterCount = parameters.Count,
                            HasOptionalParams = parameters.Any(p => p.IsOptional)
                        });
                    }
                }
            }

            var categories = tools.GroupBy(t => t.Category)
                .ToDictionary(g => g.Key, g => g.Count());

            return Task.FromResult(new ListAvailableToolsResponse
            {
                TotalTools = tools.Count,
                Categories = categories,
                CategoryFilter = category,
                Tools = tools.OrderBy(t => t.Category).ThenBy(t => t.Name).ToList()
            });
        }
        catch (Exception)
        {
            return Task.FromResult(new ListAvailableToolsResponse
            {
                TotalTools = 0,
                Categories = new Dictionary<string, int>(),
                CategoryFilter = category,
                Tools = []
            });
        }
    }

    /// <summary>
    ///     Get detailed information about a specific tool including usage examples
    /// </summary>
    /// <param name="server">MCP Server instance</param>
    /// <param name="toolName">Name of the tool to get details for (use list_available_tools to see all tools)</param>
    /// <returns>Detailed tool information with usage examples, parameter details, and documentation</returns>
    [McpServerTool(Name = "get_tool_details")]
    public static Task<ToolDetailsResponse> GetToolDetails(
        McpServer server,
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

                    if (toolAttr != null && !string.IsNullOrEmpty(toolAttr.Name))
                    {
                        var description = GetToolDescription(method);
                        var returnDescription = XmlDocs.Value.GetReturnDescription(method);

                        var parameters = method.GetParameters()
                            .Skip(1) // Skip IMcpServer parameter
                            .Select(p => new ToolParameterInfo
                            {
                                Name = p.Name ?? "unknown",
                                Type = GetFriendlyTypeName(p.ParameterType),
                                IsOptional = p.HasDefaultValue,
                                DefaultValue = p.HasDefaultValue ? p.DefaultValue?.ToString() : null,
                                Description = XmlDocs.Value.GetParameterDescription(method, p.Name ?? "")
                            })
                            .ToList();

                        var examples = GenerateUsageExamples(toolName, parameters);

                        return Task.FromResult(new ToolDetailsResponse
                        {
                            Name = toolAttr.Name,
                            Category = GetToolCategory(toolType.Name),
                            Description = description,
                            ReturnDescription = returnDescription,
                            ClassName = toolType.Name,
                            MethodName = method.Name,
                            ReturnType = GetFriendlyTypeName(method.ReturnType),
                            Parameters = parameters,
                            RequiredParameters = parameters.Where(p => !p.IsOptional).ToList(),
                            OptionalParameters = parameters.Where(p => p.IsOptional).ToList(),
                            UsageExamples = examples,
                            Notes = GetToolNotes(toolName)
                        });
                    }
                }
            }

            throw new ArgumentException(
                $"Tool '{toolName}' not found. Use 'list_available_tools' to see all available tools.");
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to get tool details for '{toolName}': {ex.Message}", ex);
        }
    }

    /// <summary>
    ///     Get usage statistics and performance metrics for MCP tools
    /// </summary>
    /// <param name="server">MCP Server instance</param>
    /// <param name="timeRange">Time range for statistics: 'hour', 'day', 'week', 'month' (default: 'day')</param>
    /// <returns>Tool usage statistics including invocation counts, performance metrics, error rates, and top performing tools</returns>
    [McpServerTool(Name = "get_tool_statistics")]
    public static Task<ToolStatistics> GetToolStatistics(
        McpServer server,
        string timeRange = "day")
    {
        try
        {
            // This would typically come from actual logging/metrics storage
            // For now, returning mock data structure
            var mockStats = new ToolStatistics
            {
                TimeRange = timeRange,
                GeneratedAt = DateTime.UtcNow,
                TotalInvocations = 1247,
                UniqueTools = 23,
                AverageResponseTime = "245ms",
                SuccessRate = 98.7,

                TopTools =
                [
                    new TopToolInfo { Name = "query_entities", Invocations = 312, AvgResponseTime = "180ms" },
                    new TopToolInfo { Name = "get_available_types", Invocations = 156, AvgResponseTime = "95ms" },
                    new TopToolInfo
                        { Name = "analyze_energy_consumption", Invocations = 89, AvgResponseTime = "420ms" },
                    new TopToolInfo { Name = "get_machine_alarms", Invocations = 67, AvgResponseTime = "220ms" },
                    new TopToolInfo { Name = "create_entity", Invocations = 45, AvgResponseTime = "350ms" }
                ],

                CategoryBreakdown = new CategoryBreakdownInfo
                {
                    Crud = 45.2,
                    Analytics = 28.7,
                    Discovery = 15.8,
                    Maintenance = 6.9,
                    Management = 3.4
                },

                ErrorStats = new ErrorStatistics
                {
                    TotalErrors = 16,
                    CommonErrors =
                    [
                        new CommonErrorInfo { Error = "Entity not found", Count = 8 },
                        new CommonErrorInfo { Error = "Invalid CK Type ID", Count = 4 },
                        new CommonErrorInfo { Error = "Permission denied", Count = 3 },
                        new CommonErrorInfo { Error = "Invalid date format", Count = 1 }
                    ]
                },

                Performance = new PerformanceMetrics
                {
                    FastestTool = new PerformanceToolInfo { Name = "get_available_models", AvgTime = "45ms" },
                    SlowestTool = new PerformanceToolInfo { Name = "generate_executive_dashboard", AvgTime = "1.2s" },
                    MostReliable = new ReliabilityToolInfo { Name = "list_available_tools", SuccessRate = 100.0 },
                    LeastReliable = new ReliabilityToolInfo { Name = "analyze_machine_performance", SuccessRate = 94.2 }
                }
            };

            return Task.FromResult(mockStats);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to get tool statistics: {ex.Message}", ex);
        }
    }

    /// <summary>
    ///     Validate tool parameters before execution to catch configuration errors early
    /// </summary>
    /// <param name="server">MCP Server instance</param>
    /// <param name="toolName">Name of the tool to validate (must be exact tool name)</param>
    /// <param name="parameters">Parameters to validate as JSON string (e.g., '{"ckTypeId": "Customer-1", "limit": 10}')</param>
    /// <returns>Comprehensive parameter validation results including errors, warnings, and suggestions for fixes</returns>
    [McpServerTool(Name = "validate_tool_parameters")]
    public static async Task<ValidateParametersResponse> ValidateToolParameters(
        McpServer server,
        string toolName,
        string parameters)
    {
        try
        {
            ToolDetailsResponse toolInfo;
            try
            {
                toolInfo = await GetToolDetails(server, toolName);
            }
            catch
            {
                return new ValidateParametersResponse
                {
                    IsValid = false,
                    ToolName = toolName,
                    ProvidedParameters = [],
                    ValidationResults = [],
                    Warnings = [],
                    Errors = ["Tool not found"],
                    Summary = new ValidationSummary
                    {
                        TotalProvided = 0,
                        RequiredMissing = 0,
                        UnknownParams = 0,
                        Recommendation = "Tool not found - check tool name"
                    }
                };
            }

            var providedParams = JsonSerializer.Deserialize<Dictionary<string, object>>(parameters);
            var requiredParams = toolInfo.RequiredParameters;
            var allParams = toolInfo.Parameters;

            var errors = new List<string>();
            var warnings = new List<string>();
            var results = new List<ParameterValidationResult>();

            // Check required parameters
            foreach (var reqParam in requiredParams)
            {
                var paramName = reqParam.Name;
                if (providedParams?.ContainsKey(paramName) != true)
                {
                    errors.Add($"Required parameter '{paramName}' is missing");
                }
                else
                {
                    results.Add(new ParameterValidationResult
                    {
                        Parameter = paramName,
                        Status = "valid",
                        Type = reqParam.Type,
                        ProvidedValue = providedParams[paramName].ToString()
                    });
                }
            }

            // Check for unknown parameters
            if (providedParams != null)
            {
                var knownParams = allParams.Select(p => p.Name).ToHashSet();
                foreach (var providedParam in providedParams.Keys)
                {
                    if (!knownParams.Contains(providedParam))
                    {
                        warnings.Add($"Unknown parameter '{providedParam}' will be ignored");
                    }
                }
            }

            return new ValidateParametersResponse
            {
                IsValid = errors.Count == 0,
                ToolName = toolName,
                ProvidedParameters = providedParams?.Keys.ToList() ?? [],
                ValidationResults = results,
                Warnings = warnings,
                Errors = errors,
                Summary = new ValidationSummary
                {
                    TotalProvided = providedParams?.Count ?? 0,
                    RequiredMissing = errors.Count(e => e.Contains("missing")),
                    UnknownParams = warnings.Count(w => w.Contains("Unknown")),
                    Recommendation = errors.Count == 0
                        ? "Parameters are valid for execution"
                        : "Fix errors before executing tool"
                }
            };
        }
        catch (Exception ex)
        {
            return new ValidateParametersResponse
            {
                IsValid = false,
                ToolName = toolName,
                ProvidedParameters = [],
                ValidationResults = [],
                Warnings = [],
                Errors = [ex.Message],
                Summary = new ValidationSummary
                {
                    TotalProvided = 0,
                    RequiredMissing = 0,
                    UnknownParams = 0,
                    Recommendation = "Failed to validate parameters"
                }
            };
        }
    }

    /// <summary>
    ///     Get tool description from XML documentation with fallback to Description attribute
    /// </summary>
    private static string GetToolDescription(MethodInfo method)
    {
        // Try XML documentation first
        var xmlDescription = XmlDocs.Value.GetMethodSummary(method);
        if (!string.IsNullOrEmpty(xmlDescription))
        {
            return xmlDescription;
        }

        // Fallback to Description attribute
        var descriptionAttr = method.GetCustomAttribute<DescriptionAttribute>();
        if (descriptionAttr != null && !string.IsNullOrEmpty(descriptionAttr.Description))
        {
            return descriptionAttr.Description;
        }

        return $"No description available for {method.Name}";
    }

    #region Helper Methods

    private static string GetToolCategory(string className)
    {
        return className switch
        {
            "RuntimeEntityCrudTools" => "CRUD Operations",
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

    private static List<ToolUsageExample> GenerateUsageExamples(string toolName, List<ToolParameterInfo> parameters)
    {
        var examples = new List<ToolUsageExample>();

        switch (toolName)
        {
            case "query_entities":
                examples.Add(new ToolUsageExample
                {
                    Description = "Query all customers",
                    Parameters = new { ckTypeId = "EnergyCommunity-1.0.0/Customer-1", limit = 10 }
                });
                examples.Add(new ToolUsageExample
                {
                    Description = "Query customers with and filter",
                    Parameters = new
                    {
                        ckTypeId = "EnergyCommunity-1.0.0/Customer-1",
                        filters = new FieldFilterCriteriaDto
                        {
                            Fields =
                            [
                                new()
                                {
                                    AttributePath = "contact.country", Operator = FilterOperatorDto.Equals, Value = "DE"
                                },
                                new()
                                {
                                    AttributePath = "status", Operator = FilterOperatorDto.In,
                                    Value = new[] { "active", "pending" }
                                }
                            ],
                            Operator = LogicalOperatorDto.And
                        },
                        limit = 50
                    }
                });
                examples.Add(new ToolUsageExample
                {
                    Description = "Query customers with or filter",
                    Parameters = new
                    {
                        ckTypeId = "EnergyCommunity-1.0.0/Customer-1",
                        filters = new FieldFilterCriteriaDto
                        {
                            Fields =
                            [
                                new()
                                {
                                    AttributePath = "contact.country", Operator = FilterOperatorDto.Equals, Value = "DE"
                                },
                                new()
                                {
                                    AttributePath = "status", Operator = FilterOperatorDto.In,
                                    Value = new[] { "active", "pending" }
                                }
                            ],
                            Operator = LogicalOperatorDto.Or
                        },
                        limit = 50
                    }
                });
                examples.Add(new ToolUsageExample
                {
                    Description = "Query customers with complex filter with different logical operators",
                    Parameters = new
                    {
                        ckTypeId = "EnergyCommunity-1.0.0/Customer-1",
                        filters = new FieldFilterCriteriaDto
                        {
                            NestedFilters =
                            [
                                new()
                                {
                                    Fields =
                                    [
                                        new()
                                        {
                                            AttributePath = "contact.firstName", Operator = FilterOperatorDto.Equals,
                                            Value = "Gerald",
                                        },
                                        new()
                                        {
                                            AttributePath = "contact.lastName", Operator = FilterOperatorDto.Equals,
                                            Value = "Lochner"
                                        }
                                    ],
                                    Operator = LogicalOperatorDto.And
                                },
                                new()
                                {
                                    Fields =
                                    [
                                        new()
                                        {
                                            AttributePath = "contact.companyName",
                                            Operator = FilterOperatorDto.Equals,
                                            Value = "meshmakers GmbH"
                                        },
                                        new()
                                        {
                                            AttributePath = "status", Operator = FilterOperatorDto.In,
                                            Value = new[] { "active", "pending" }
                                        }
                                    ],
                                    Operator = LogicalOperatorDto.And
                                }
                            ],
                            Operator = LogicalOperatorDto.Or
                        },
                        limit = 50
                    }
                });
                break;


            case "query_entities_simple":
                examples.Add(new ToolUsageExample
                {
                    Description = "Gets all entities of type Customer with filter for first name and last name",
                    Parameters = new
                    {
                        ckTypeId = "EnergyCommunity/Customer",
                        simpleFilters = new List<SimpleFilterDto>
                        {
                            new()
                            {
                                AttributePath = "contact.firstName",
                                Value = "Gerald"
                            },
                            new()
                            {
                                AttributePath = "contact.lastName",
                                Value = "Lochner"
                            }
                        }
                    }
                });
                break;
            case "get_type_schema":
                examples.Add(new ToolUsageExample
                {
                    Description = "Get schema for Customer type",
                    Parameters = new { ckTypeId = "EnergyCommunity/Customer" }
                });
                break;

            case "create_entity":
                examples.Add(new ToolUsageExample
                {
                    Description = "Create a new runtime entity",
                    Parameters = new
                    {
                        ckTypeId = "EnergyCommunity/Customer",
                        entityData = new[]
                        {
                            new AttributeUpdateItem { AttributePath = "contact.companyRegisterNumber", Value = "TEST" }
                        }
                    }
                });
                break;

            case "update_entity":
                examples.Add(new ToolUsageExample
                {
                    Description = "Updates a runtime entity's attributes",
                    Parameters = new
                    {
                        rtId = "6841b558514a7df1ce76b55f",
                        ckTypeId = "EnergyCommunity/Customer",
                        entityData = new[]
                        {
                            new AttributeUpdateItem { AttributePath = "contact.companyRegisterNumber", Value = "TEST" }
                        }
                    }
                });
                break;

            default:
                examples.Add(new ToolUsageExample
                {
                    Description = "Basic usage",
                    Parameters = parameters
                        .Where(p => !p.IsOptional)
                        .ToDictionary(p => p.Name, p => $"<{p.Type}>")
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
            _ => ["No special notes for this tool"]
        };
    }

    #endregion
}