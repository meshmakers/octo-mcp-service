using System.ComponentModel;
using System.Text.Json;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Services.Infrastructure;
using ModelContextProtocol.Server;

namespace Meshmakers.Octo.Backend.McpServices.Tools;

/// <summary>
/// Domain-specific tools for Energy Community, Industrial IoT, and Maintenance operations
/// </summary>
[McpServerToolType]
public sealed class DomainSpecificTools
{
    #region Energy Community Tools

    /// <summary>
    /// Analyze energy consumption patterns for customers or facilities
    /// </summary>
    /// <param name="server">MCP Server instance</param>
    /// <param name="fromDate">Start date for analysis (ISO 8601 format)</param>
    /// <param name="toDate">End date for analysis (ISO 8601 format)</param>
    /// <param name="facilityId">Optional: Specific facility ID to analyze</param>
    /// <param name="customerId">Optional: Specific customer ID to analyze</param>
    /// <returns>Energy consumption analysis</returns>
    [McpServerTool(Name = "analyze_energy_consumption")]
    [Description("Analyze energy consumption patterns for customers or facilities over a time period")]
    public static async Task<object> AnalyzeEnergyConsumption(
        IMcpServer server,
        string fromDate,
        string toDate,
        string? facilityId = null,
        string? customerId = null)
    {
        var httpContextAccessor = server.Services!.GetRequiredService<IHttpContextAccessor>();
        var tenantRepository = await httpContextAccessor.GetTenantRepositoryAsync();

        using var session = await tenantRepository.GetSessionAsync();

        try
        {
            var from = DateTime.Parse(fromDate);
            var to = DateTime.Parse(toDate);

            // Query energy quantities
            var queryOperation = new DataQueryOperation();
            var fieldFilters = new List<FieldFilter>();

            // Time range filter
            fieldFilters.Add(new FieldFilter
            {
                AttributePath = "TimeRange.From",
                Operator = FieldFilterOperator.GreaterEqualThan,
                ComparisonValue = from.ToString("O")
            });
            fieldFilters.Add(new FieldFilter
            {
                AttributePath = "TimeRange.To",
                Operator = FieldFilterOperator.LessEqualThan,
                ComparisonValue = to.ToString("O")
            });

            queryOperation.FieldFilters = fieldFilters;

            var energyQuantities = await tenantRepository.GetRtEntitiesByTypeAsync(
                session,
                new CkId<CkTypeId>("EnergyCommunity-1.0.0/EnergyQuantity-1.0.0"),
                queryOperation);

            // Calculate totals and aggregations
            double totalConsumption = 0;
            var dailyConsumption = new Dictionary<string, double>();
            var qualityBreakdown = new Dictionary<string, int>();

            foreach (var quantity in energyQuantities.Items)
            {
                if (quantity.Attributes.TryGetValue("Quantity", out var quantityValue) &&
                    double.TryParse(quantityValue?.ToString(), out var qty))
                {
                    totalConsumption += qty;

                    // Daily aggregation
                    if (quantity.Attributes.TryGetValue("TimeRange.From", out var fromValue) &&
                        DateTime.TryParse(fromValue?.ToString(), out var date))
                    {
                        var dayKey = date.ToString("yyyy-MM-dd");
                        dailyConsumption[dayKey] = dailyConsumption.GetValueOrDefault(dayKey, 0) + qty;
                    }

                    // Data quality breakdown
                    if (quantity.Attributes.TryGetValue("DataQuality", out var qualityValue))
                    {
                        var quality = qualityValue?.ToString() ?? "Unknown";
                        qualityBreakdown[quality] = qualityBreakdown.GetValueOrDefault(quality, 0) + 1;
                    }
                }
            }

            return new
            {
                analysisPeriod = new { from = fromDate, to = toDate },
                facilityId,
                customerId,
                summary = new
                {
                    totalConsumption = Math.Round(totalConsumption, 2),
                    unit = "kWh",
                    averageDailyConsumption = Math.Round(totalConsumption / Math.Max((to - from).Days, 1), 2),
                    measurementCount = energyQuantities.Items.Count,
                    dataQualityBreakdown = qualityBreakdown
                },
                dailyBreakdown = dailyConsumption.OrderBy(kvp => kvp.Key).ToDictionary(kvp => kvp.Key, kvp => Math.Round(kvp.Value, 2))
            };
        }
        catch (Exception ex)
        {
            return new
            {
                error = "Failed to analyze energy consumption",
                message = ex.Message
            };
        }
    }

    /// <summary>
    /// Get active billing documents for customers
    /// </summary>
    /// <param name="server">MCP Server instance</param>
    /// <param name="customerId">Optional: Specific customer ID</param>
    /// <param name="documentState">Optional: Filter by document state (Draft, Completed, Sent, Paid)</param>
    /// <returns>Active billing documents</returns>
    [McpServerTool(Name = "get_billing_documents")]
    [Description("Get billing documents for customers, optionally filtered by customer or state")]
    public static async Task<object> GetBillingDocuments(
        IMcpServer server,
        string? customerId = null,
        string? documentState = null)
    {
        var httpContextAccessor = server.Services!.GetRequiredService<IHttpContextAccessor>();
        var tenantRepository = await httpContextAccessor.GetTenantRepositoryAsync();

        using var session = await tenantRepository.GetSessionAsync();

        try
        {
            var queryOperation = new DataQueryOperation();
            var fieldFilters = new List<FieldFilter>();

            if (!string.IsNullOrEmpty(customerId))
            {
                fieldFilters.Add(new FieldFilter
                {
                    AttributePath = "CustomerNumber",
                    Operator = FieldFilterOperator.Equals,
                    ComparisonValue = customerId
                });
            }

            if (!string.IsNullOrEmpty(documentState))
            {
                fieldFilters.Add(new FieldFilter
                {
                    AttributePath = "BillingDocumentState",
                    Operator = FieldFilterOperator.Equals,
                    ComparisonValue = documentState
                });
            }

            queryOperation.FieldFilters = fieldFilters;

            var billingDocs = await tenantRepository.GetRtEntitiesByTypeAsync(
                session,
                new CkId<CkTypeId>("EnergyCommunity-1.0.0/BillingDocument-1.0.0"),
                queryOperation);

            var formattedDocs = billingDocs.Items.Select(doc => new
            {
                id = doc.RtId.ToString(),
                documentNumber = doc.Attributes.GetValueOrDefault("DocumentNumber"),
                customerNumber = doc.Attributes.GetValueOrDefault("CustomerNumber"),
                documentDate = doc.Attributes.GetValueOrDefault("DocumentDate"),
                grossTotal = doc.Attributes.GetValueOrDefault("GrossTotal"),
                billingType = doc.Attributes.GetValueOrDefault("BillingType"),
                documentState = doc.Attributes.GetValueOrDefault("BillingDocumentState"),
                timeRange = doc.Attributes.GetValueOrDefault("TimeRange"),
                createdAt = doc.CreatedAt,
                modifiedAt = doc.ModifiedAt
            });

            return new
            {
                totalDocuments = billingDocs.TotalCount,
                filters = new { customerId, documentState },
                documents = formattedDocs
            };
        }
        catch (Exception ex)
        {
            return new
            {
                error = "Failed to get billing documents",
                message = ex.Message
            };
        }
    }

    #endregion

    #region Industrial IoT Tools

    /// <summary>
    /// Get active machine alarms and their status
    /// </summary>
    /// <param name="server">MCP Server instance</param>
    /// <param name="machineId">Optional: Specific machine ID</param>
    /// <param name="priorityLevel">Optional: Filter by priority (Low, Medium, High, Critical)</param>
    /// <param name="alarmState">Optional: Filter by alarm state</param>
    /// <returns>Active machine alarms</returns>
    [McpServerTool(Name = "get_machine_alarms")]
    [Description("Get active machine alarms, optionally filtered by machine, priority or state")]
    public static async Task<object> GetMachineAlarms(
        IMcpServer server,
        string? machineId = null,
        string? priorityLevel = null,
        string? alarmState = null)
    {
        var httpContextAccessor = server.Services!.GetRequiredService<IHttpContextAccessor>();
        var tenantRepository = await httpContextAccessor.GetTenantRepositoryAsync();

        using var session = await tenantRepository.GetSessionAsync();

        try
        {
            var queryOperation = new DataQueryOperation();
            var fieldFilters = new List<FieldFilter>();

            if (!string.IsNullOrEmpty(priorityLevel))
            {
                fieldFilters.Add(new FieldFilter
                {
                    AttributePath = "EventAlarmPriority",
                    Operator = FieldFilterOperator.Equals,
                    ComparisonValue = priorityLevel
                });
            }

            if (!string.IsNullOrEmpty(alarmState))
            {
                fieldFilters.Add(new FieldFilter
                {
                    AttributePath = "State",
                    Operator = FieldFilterOperator.Equals,
                    ComparisonValue = alarmState
                });
            }

            queryOperation.FieldFilters = fieldFilters;

            var alarms = await tenantRepository.GetRtEntitiesByTypeAsync(
                session,
                new CkId<CkTypeId>("Industry.Basic-1.0.0/Alarm-1.0.0"),
                queryOperation);

            var priorityStats = new Dictionary<string, int>();
            var stateStats = new Dictionary<string, int>();

            var formattedAlarms = alarms.Items.Select(alarm =>
            {
                var priority = alarm.Attributes.GetValueOrDefault("EventAlarmPriority")?.ToString() ?? "Unknown";
                var state = alarm.Attributes.GetValueOrDefault("State")?.ToString() ?? "Unknown";

                priorityStats[priority] = priorityStats.GetValueOrDefault(priority, 0) + 1;
                stateStats[state] = stateStats.GetValueOrDefault(state, 0) + 1;

                return new
                {
                    id = alarm.RtId.ToString(),
                    name = alarm.Attributes.GetValueOrDefault("Name"),
                    message = alarm.Attributes.GetValueOrDefault("Message"),
                    time = alarm.Attributes.GetValueOrDefault("Time"),
                    priority = priority,
                    state = state,
                    alarmType = alarm.Attributes.GetValueOrDefault("Type"),
                    source = alarm.Attributes.GetValueOrDefault("Source"),
                    cause = alarm.Attributes.GetValueOrDefault("Cause"),
                    reactivatedCount = alarm.Attributes.GetValueOrDefault("ReactivatedCount"),
                    tagName = alarm.Attributes.GetValueOrDefault("TagName"),
                    createdAt = alarm.CreatedAt
                };
            });

            return new
            {
                totalAlarms = alarms.TotalCount,
                filters = new { machineId, priorityLevel, alarmState },
                statistics = new
                {
                    byPriority = priorityStats,
                    byState = stateStats
                },
                alarms = formattedAlarms.OrderByDescending(a => a.createdAt)
            };
        }
        catch (Exception ex)
        {
            return new
            {
                error = "Failed to get machine alarms",
                message = ex.Message
            };
        }
    }

    /// <summary>
    /// Get machine status and operating hours
    /// </summary>
    /// <param name="server">MCP Server instance</param>
    /// <param name="machineId">Optional: Specific machine ID</param>
    /// <param name="includeMetrics">Include operating metrics like hours and power</param>
    /// <returns>Machine status information</returns>
    [McpServerTool(Name = "get_machine_status")]
    [Description("Get current machine status, operating hours and performance metrics")]
    public static async Task<object> GetMachineStatus(
        IMcpServer server,
        string? machineId = null,
        bool includeMetrics = true)
    {
        var httpContextAccessor = server.Services!.GetRequiredService<IHttpContextAccessor>();
        var tenantRepository = await httpContextAccessor.GetTenantRepositoryAsync();

        using var session = await tenantRepository.GetSessionAsync();

        try
        {
            var queryOperation = new DataQueryOperation();
            var fieldFilters = new List<FieldFilter>();

            if (!string.IsNullOrEmpty(machineId))
            {
                fieldFilters.Add(new FieldFilter
                {
                    AttributePath = "RtId",
                    Operator = FieldFilterOperator.Equals,
                    ComparisonValue = machineId
                });
            }

            queryOperation.FieldFilters = fieldFilters;

            var machines = await tenantRepository.GetRtEntitiesByTypeAsync(
                session,
                new CkId<CkTypeId>("Industry.Basic-1.0.0/Machine-1.0.0"),
                queryOperation);

            var stateBreakdown = new Dictionary<string, int>();
            
            var formattedMachines = machines.Items.Select(machine =>
            {
                var state = machine.Attributes.GetValueOrDefault("MachineState")?.ToString() ?? "Unknown";
                stateBreakdown[state] = stateBreakdown.GetValueOrDefault(state, 0) + 1;

                var result = new
                {
                    id = machine.RtId.ToString(),
                    name = machine.Attributes.GetValueOrDefault("Name"),
                    description = machine.Attributes.GetValueOrDefault("Description"),
                    machineState = state,
                    operatingHours = includeMetrics ? machine.Attributes.GetValueOrDefault("OperatingHours") : null,
                    standStillCounter = includeMetrics ? machine.Attributes.GetValueOrDefault("StandStillCounter") : null,
                    namePlate = machine.Attributes.GetValueOrDefault("NamePlate"),
                    lastModified = machine.ModifiedAt
                };

                return result;
            });

            return new
            {
                totalMachines = machines.TotalCount,
                stateBreakdown,
                includeMetrics,
                machines = formattedMachines.OrderBy(m => m.name)
            };
        }
        catch (Exception ex)
        {
            return new
            {
                error = "Failed to get machine status",
                message = ex.Message
            };
        }
    }

    #endregion

    #region Maintenance Tools

    /// <summary>
    /// Get maintenance orders with their current status
    /// </summary>
    /// <param name="server">MCP Server instance</param>
    /// <param name="orderState">Optional: Filter by order state</param>
    /// <param name="serviceType">Optional: Filter by service type</param>
    /// <param name="fromDate">Optional: Filter orders created after this date</param>
    /// <returns>Maintenance orders</returns>
    [McpServerTool(Name = "get_maintenance_orders")]
    [Description("Get maintenance orders with status, costs and scheduling information")]
    public static async Task<object> GetMaintenanceOrders(
        IMcpServer server,
        string? orderState = null,
        string? serviceType = null,
        string? fromDate = null)
    {
        var httpContextAccessor = server.Services!.GetRequiredService<IHttpContextAccessor>();
        var tenantRepository = await httpContextAccessor.GetTenantRepositoryAsync();

        using var session = await tenantRepository.GetSessionAsync();

        try
        {
            var queryOperation = new DataQueryOperation();
            var fieldFilters = new List<FieldFilter>();

            if (!string.IsNullOrEmpty(orderState))
            {
                fieldFilters.Add(new FieldFilter
                {
                    AttributePath = "OrderState",
                    Operator = FieldFilterOperator.Equals,
                    ComparisonValue = orderState
                });
            }

            if (!string.IsNullOrEmpty(serviceType))
            {
                fieldFilters.Add(new FieldFilter
                {
                    AttributePath = "ServiceType",
                    Operator = FieldFilterOperator.Equals,
                    ComparisonValue = serviceType
                });
            }

            if (!string.IsNullOrEmpty(fromDate))
            {
                fieldFilters.Add(new FieldFilter
                {
                    AttributePath = "CreatedAt",
                    Operator = FieldFilterOperator.GreaterEqualThan,
                    ComparisonValue = fromDate
                });
            }

            queryOperation.FieldFilters = fieldFilters;

            var orders = await tenantRepository.GetRtEntitiesByTypeAsync(
                session,
                new CkId<CkTypeId>("Industry.Maintenance-1.0.0/Order-1.0.0"),
                queryOperation);

            var stateStats = new Dictionary<string, int>();
            var serviceStats = new Dictionary<string, int>();
            double totalPlannedCosts = 0;
            double totalActualCosts = 0;

            var formattedOrders = orders.Items.Select(order =>
            {
                var state = order.Attributes.GetValueOrDefault("OrderState")?.ToString() ?? "Unknown";
                var service = order.Attributes.GetValueOrDefault("ServiceType")?.ToString() ?? "Unknown";

                stateStats[state] = stateStats.GetValueOrDefault(state, 0) + 1;
                serviceStats[service] = serviceStats.GetValueOrDefault(service, 0) + 1;

                if (double.TryParse(order.Attributes.GetValueOrDefault("PlannedCosts")?.ToString(), out var planned))
                    totalPlannedCosts += planned;

                if (double.TryParse(order.Attributes.GetValueOrDefault("ActualCosts")?.ToString(), out var actual))
                    totalActualCosts += actual;

                return new
                {
                    id = order.RtId.ToString(),
                    orderNumber = order.Attributes.GetValueOrDefault("OrderNumber"),
                    orderText = order.Attributes.GetValueOrDefault("OrderText"),
                    serviceType = service,
                    orderType = order.Attributes.GetValueOrDefault("OrderType"),
                    orderState = state,
                    createdAt = order.Attributes.GetValueOrDefault("CreatedAt"),
                    plannedCosts = order.Attributes.GetValueOrDefault("PlannedCosts"),
                    actualCosts = order.Attributes.GetValueOrDefault("ActualCosts"),
                    projectNumber = order.Attributes.GetValueOrDefault("ProjectNumber")
                };
            });

            return new
            {
                totalOrders = orders.TotalCount,
                filters = new { orderState, serviceType, fromDate },
                statistics = new
                {
                    byState = stateStats,
                    byServiceType = serviceStats,
                    costs = new
                    {
                        totalPlanned = Math.Round(totalPlannedCosts, 2),
                        totalActual = Math.Round(totalActualCosts, 2),
                        variance = Math.Round(totalActualCosts - totalPlannedCosts, 2)
                    }
                },
                orders = formattedOrders.OrderByDescending(o => o.createdAt)
            };
        }
        catch (Exception ex)
        {
            return new
            {
                error = "Failed to get maintenance orders",
                message = ex.Message
            };
        }
    }

    #endregion

    #region Association Tools

    /// <summary>
    /// Get associations (relationships) for a specific entity
    /// </summary>
    /// <param name="server">MCP Server instance</param>
    /// <param name="entityId">Runtime entity ID</param>
    /// <param name="direction">Association direction: 'inbound', 'outbound', or 'both'</param>
    /// <param name="roleId">Optional: Specific association role ID to filter</param>
    /// <returns>Entity associations</returns>
    [McpServerTool(Name = "get_entity_associations")]
    [Description("Get associations (relationships) for a specific entity")]
    public static async Task<object> GetEntityAssociations(
        IMcpServer server,
        string entityId,
        string direction = "both",
        string? roleId = null)
    {
        var httpContextAccessor = server.Services!.GetRequiredService<IHttpContextAccessor>();
        var tenantRepository = await httpContextAccessor.GetTenantRepositoryAsync();

        using var session = await tenantRepository.GetSessionAsync();

        try
        {
            var rtId = new OctoObjectId(entityId);
            var graphDirection = direction.ToLower() switch
            {
                "inbound" => GraphDirections.Inbound,
                "outbound" => GraphDirections.Outbound,
                _ => GraphDirections.Both
            };

            var associations = string.IsNullOrEmpty(roleId)
                ? await tenantRepository.GetRtAssociationsAsync(session, rtId, graphDirection)
                : await tenantRepository.GetRtAssociationsAsync(session, rtId, graphDirection, new CkId<CkAssociationRoleId>(roleId));

            var formattedAssociations = associations.Select(assoc => new
            {
                id = assoc.RtId.ToString(),
                roleId = assoc.CkAssociationRoleId.ToString(),
                originEntityId = assoc.OriginRtEntityId.ToString(),
                targetEntityId = assoc.TargetRtEntityId.ToString(),
                isInbound = assoc.TargetRtEntityId.ObjectId.ToString() == entityId,
                createdAt = assoc.CreatedAt,
                attributes = assoc.Attributes
            });

            return new
            {
                entityId,
                direction,
                roleId,
                associationCount = associations.Count,
                associations = formattedAssociations
            };
        }
        catch (Exception ex)
        {
            return new
            {
                error = "Failed to get entity associations",
                message = ex.Message,
                entityId
            };
        }
    }

    #endregion
}
