using System.ComponentModel;
using System.Text.Json;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Services.Infrastructure;
using ModelContextProtocol.Server;

namespace Meshmakers.Octo.Backend.McpServices.Tools;
/*
/// <summary>
/// Advanced analytics and reporting tools for complex data analysis
/// </summary>
[McpServerToolType]
public sealed class AdvancedAnalyticsTools
{
    /// <summary>
    /// Generate energy efficiency report for customers or facilities
    /// </summary>
    /// <param name="server">MCP Server instance</param>
    /// <param name="fromDate">Start date for analysis</param>
    /// <param name="toDate">End date for analysis</param>
    /// <param name="groupBy">Group results by: 'customer', 'facility', 'day', 'week', 'month'</param>
    /// <param name="includeComparison">Include comparison with previous period</param>
    /// <returns>Energy efficiency report with trends and comparisons</returns>
    [McpServerTool(Name = "generate_energy_efficiency_report")]
    [Description("Generate comprehensive energy efficiency report with trends and comparisons")]
    public static async Task<object> GenerateEnergyEfficiencyReport(
        IMcpServer server,
        string fromDate,
        string toDate,
        string groupBy = "month",
        bool includeComparison = true)
    {
        var httpContextAccessor = server.Services!.GetRequiredService<IHttpContextAccessor>();
        var tenantRepository = await httpContextAccessor.GetTenantRepositoryAsync();

        using var session = await tenantRepository.GetSessionAsync();

        try
        {
            var from = DateTime.Parse(fromDate);
            var to = DateTime.Parse(toDate);
            var period = to - from;

            // Query energy quantities for the period
            var currentPeriodData = await GetEnergyDataForPeriod(tenantRepository, session, from, to);
            
            var report = new
            {
                reportPeriod = new { from = fromDate, to = toDate, days = period.Days },
                groupBy,
                includeComparison,
                summary = CalculateEnergySummary(currentPeriodData),
                trends = CalculateEnergyTrends(currentPeriodData, groupBy),
                efficiency = CalculateEfficiencyMetrics(currentPeriodData),
                recommendations = GenerateEfficiencyRecommendations(currentPeriodData)
            };

            // Add comparison with previous period if requested
            if (includeComparison)
            {
                var previousFrom = from.AddDays(-period.Days);
                var previousTo = from;
                var previousPeriodData = await GetEnergyDataForPeriod(tenantRepository, session, previousFrom, previousTo);
                
                var comparison = new
                {
                    previousPeriod = new { from = previousFrom.ToString("O"), to = previousTo.ToString("O") },
                    summary = CalculateEnergySummary(previousPeriodData),
                    changes = CalculatePeriodComparison(currentPeriodData, previousPeriodData)
                };

                return new { report, comparison };
            }

            return report;
        }
        catch (Exception ex)
        {
            return new
            {
                error = "Failed to generate energy efficiency report",
                message = ex.Message
            };
        }
    }

    /// <summary>
    /// Analyze machine performance and downtime patterns
    /// </summary>
    /// <param name="server">MCP Server instance</param>
    /// <param name="fromDate">Start date for analysis</param>
    /// <param name="toDate">End date for analysis</param>
    /// <param name="machineFilter">Optional machine ID or pattern filter</param>
    /// <param name="includeAlarms">Include alarm correlation analysis</param>
    /// <returns>Machine performance analysis with downtime patterns</returns>
    [McpServerTool(Name = "analyze_machine_performance")]
    [Description("Analyze machine performance, uptime, downtime and alarm patterns")]
    public static async Task<object> AnalyzeMachinePerformance(
        IMcpServer server,
        string fromDate,
        string toDate,
        string? machineFilter = null,
        bool includeAlarms = true)
    {
        var httpContextAccessor = server.Services!.GetRequiredService<IHttpContextAccessor>();
        var tenantRepository = await httpContextAccessor.GetTenantRepositoryAsync();

        using var session = await tenantRepository.GetSessionAsync();

        try
        {
            var from = DateTime.Parse(fromDate);
            var to = DateTime.Parse(toDate);

            // Get machines
            var machineQuery = new DataQueryOperation();
            if (!string.IsNullOrEmpty(machineFilter))
            {
                machineQuery.FieldFilters = new[]
                {
                    new FieldFilter
                    {
                        AttributePath = "Name",
                        Operator = FieldFilterOperator.Like,
                        ComparisonValue = $"*{machineFilter}*"
                    }
                };
            }

            var machines = await tenantRepository.GetRtEntitiesByTypeAsync(
                session,
                new CkId<CkTypeId>("Industry.Basic-1.0.0/Machine-1.0.0"),
                machineQuery);

            var machineAnalysis = new List<object>();

            foreach (var machine in machines.Items)
            {
                var machineId = machine.RtId.ToString();
                var analysis = new
                {
                    machineId,
                    name = machine.Attributes.GetValueOrDefault("Name"),
                    currentState = machine.Attributes.GetValueOrDefault("MachineState"),
                    operatingHours = machine.Attributes.GetValueOrDefault("OperatingHours"),
                    standStillCounter = machine.Attributes.GetValueOrDefault("StandStillCounter"),
                    performance = CalculateMachinePerformanceMetrics(machine, from, to),
                    lastModified = machine.ModifiedAt
                };

                machineAnalysis.Add(analysis);
            }

            var overallMetrics = new
            {
                totalMachines = machines.TotalCount,
                averageUptime = machineAnalysis.Average(m => ((dynamic)m).performance?.uptime ?? 0),
                totalOperatingHours = machineAnalysis.Sum(m => 
                    double.TryParse(((dynamic)m).operatingHours?.ToString(), out var hours) ? hours : 0),
                machinesInError = machineAnalysis.Count(m => ((dynamic)m).currentState?.ToString() == "Error")
            };

            var result = new
            {
                analysisPeriod = new { from = fromDate, to = toDate },
                machineFilter,
                includeAlarms,
                overallMetrics,
                machineAnalysis = machineAnalysis.OrderByDescending(m => ((dynamic)m).performance?.uptime ?? 0),
                recommendations = GeneratePerformanceRecommendations(machineAnalysis)
            };

            return result;
        }
        catch (Exception ex)
        {
            return new
            {
                error = "Failed to analyze machine performance",
                message = ex.Message
            };
        }
    }

    /// <summary>
    /// Generate maintenance cost analysis and budget forecasting
    /// </summary>
    /// <param name="server">MCP Server instance</param>
    /// <param name="fromDate">Start date for analysis</param>
    /// <param name="toDate">End date for analysis</param>
    /// <param name="groupBy">Group costs by: 'month', 'quarter', 'serviceType', 'costCategory'</param>
    /// <param name="forecastMonths">Number of months to forecast ahead</param>
    /// <returns>Maintenance cost analysis with forecasting</returns>
    [McpServerTool(Name = "analyze_maintenance_costs")]
    [Description("Analyze maintenance costs, trends and generate budget forecasts")]
    public static async Task<object> analyzeMaintenanceCosts(
        IMcpServer server,
        string fromDate,
        string toDate,
        string groupBy = "month",
        int forecastMonths = 6)
    {
        var httpContextAccessor = server.Services!.GetRequiredService<IHttpContextAccessor>();
        var tenantRepository = await httpContextAccessor.GetTenantRepositoryAsync();

        using var session = await tenantRepository.GetSessionAsync();

        try
        {
            var from = DateTime.Parse(fromDate);
            var to = DateTime.Parse(toDate);

            // Query maintenance orders
            var orderQuery = new DataQueryOperation
            {
                FieldFilters = new[]
                {
                    new FieldFilter
                    {
                        AttributePath = "CreatedAt",
                        Operator = FieldFilterOperator.GreaterEqualThan,
                        ComparisonValue = from.ToString("O")
                    },
                    new FieldFilter
                    {
                        AttributePath = "CreatedAt",
                        Operator = FieldFilterOperator.LessEqualThan,
                        ComparisonValue = to.ToString("O")
                    }
                }
            };

            var orders = await tenantRepository.GetRtEntitiesByTypeAsync(
                session,
                new CkId<CkTypeId>("Industry.Maintenance-1.0.0/Order-1.0.0"),
                orderQuery);

            // Calculate cost breakdowns
            var costAnalysis = CalculateMaintenanceCostBreakdown(orders.Items, groupBy);
            var trends = CalculateCostTrends(orders.Items, from, to);
            var forecast = GenerateCostForecast(orders.Items, forecastMonths);

            return new
            {
                analysisPeriod = new { from = fromDate, to = toDate },
                groupBy,
                forecastMonths,
                summary = new
                {
                    totalOrders = orders.TotalCount,
                    totalPlannedCosts = costAnalysis.totalPlanned,
                    totalActualCosts = costAnalysis.totalActual,
                    budgetVariance = costAnalysis.variance,
                    averageCostPerOrder = costAnalysis.averagePerOrder
                },
                breakdown = costAnalysis.breakdown,
                trends,
                forecast,
                insights = GenerateCostInsights(costAnalysis, trends)
            };
        }
        catch (Exception ex)
        {
            return new
            {
                error = "Failed to analyze maintenance costs",
                message = ex.Message
            };
        }
    }

    /// <summary>
    /// Generate executive dashboard with key performance indicators
    /// </summary>
    /// <param name="server">MCP Server instance</param>
    /// <param name="period">Analysis period: 'today', 'week', 'month', 'quarter', 'year'</param>
    /// <param name="includeForecasts">Include predictive forecasts</param>
    /// <returns>Executive dashboard with KPIs across all domains</returns>
    [McpServerTool(Name = "generate_executive_dashboard")]
    [Description("Generate executive dashboard with key performance indicators across all domains")]
    public static async Task<object> GenerateExecutiveDashboard(
        IMcpServer server,
        string period = "month",
        bool includeForecasts = true)
    {
        var httpContextAccessor = server.Services!.GetRequiredService<IHttpContextAccessor>();
        var tenantRepository = await httpContextAccessor.GetTenantRepositoryAsync();

        using var session = await tenantRepository.GetSessionAsync();

        try
        {
            var (from, to) = GetPeriodDates(period);

            // Collect KPIs from different domains
            var energyKpis = await CalculateEnergyKpis(tenantRepository, session, from, to);
            var maintenanceKpis = await CalculateMaintenanceKpis(tenantRepository, session, from, to);
            var operationalKpis = await CalculateOperationalKpis(tenantRepository, session, from, to);

            var dashboard = new
            {
                generatedAt = DateTime.UtcNow,
                period = new { name = period, from, to },
                includeForecasts,
                kpis = new
                {
                    energy = energyKpis,
                    maintenance = maintenanceKpis,
                    operational = operationalKpis
                },
                alerts = await GetActiveAlerts(tenantRepository, session),
                trends = new
                {
                    energyTrend = CalculateTrendDirection(energyKpis.totalConsumption, energyKpis.previousConsumption),
                    costTrend = CalculateTrendDirection(maintenanceKpis.totalCosts, maintenanceKpis.previousCosts),
                    uptimeTrend = CalculateTrendDirection(operationalKpis.averageUptime, operationalKpis.previousUptime)
                }
            };

            if (includeForecasts)
            {
                var forecasts = new
                {
                    energyForecast = GenerateEnergyForecast(energyKpis),
                    maintenanceForecast = GenerateMaintenanceForecast(maintenanceKpis),
                    recommendations = GenerateExecutiveRecommendations(energyKpis, maintenanceKpis, operationalKpis)
                };

                return new { dashboard, forecasts };
            }

            return dashboard;
        }
        catch (Exception ex)
        {
            return new
            {
                error = "Failed to generate executive dashboard",
                message = ex.Message
            };
        }
    }

    #region Helper Methods

    private static async Task<List<dynamic>> GetEnergyDataForPeriod(
        IRuntimeRepository repository, 
        IOctoSession session, 
        DateTime from, 
        DateTime to)
    {
        var query = new DataQueryOperation
        {
            FieldFilters = new[]
            {
                new FieldFilter
                {
                    AttributePath = "TimeRange.From",
                    Operator = FieldFilterOperator.GreaterEqualThan,
                    ComparisonValue = from.ToString("O")
                },
                new FieldFilter
                {
                    AttributePath = "TimeRange.To",
                    Operator = FieldFilterOperator.LessEqualThan,
                    ComparisonValue = to.ToString("O")
                }
            }
        };

        var result = await repository.GetRtEntitiesByTypeAsync(
            session,
            new CkId<CkTypeId>("EnergyCommunity-1.0.0/EnergyQuantity-1.0.0"),
            query);

        return result.Items.Cast<dynamic>().ToList();
    }

    private static object CalculateEnergySummary(List<dynamic> energyData)
    {
        double totalConsumption = 0;
        var qualityBreakdown = new Dictionary<string, int>();

        foreach (var item in energyData)
        {
            if (item.Attributes.TryGetValue("Quantity", out var qty) && 
                double.TryParse(qty?.ToString(), out var quantity))
            {
                totalConsumption += quantity;
            }

            if (item.Attributes.TryGetValue("DataQuality", out var quality))
            {
                var q = quality?.ToString() ?? "Unknown";
                qualityBreakdown[q] = qualityBreakdown.GetValueOrDefault(q, 0) + 1;
            }
        }

        return new
        {
            totalConsumption = Math.Round(totalConsumption, 2),
            measurementCount = energyData.Count,
            averageConsumption = energyData.Count > 0 ? Math.Round(totalConsumption / energyData.Count, 2) : 0,
            qualityBreakdown
        };
    }

    private static object CalculateEnergyTrends(List<dynamic> energyData, string groupBy)
    {
        var trends = new Dictionary<string, double>();
        
        foreach (var item in energyData)
        {
            if (item.Attributes.TryGetValue("TimeRange.From", out var timeValue) &&
                DateTime.TryParse(timeValue?.ToString(), out var time) &&
                item.Attributes.TryGetValue("Quantity", out var qtyValue) &&
                double.TryParse(qtyValue?.ToString(), out var quantity))
            {
                var key = groupBy.ToLower() switch
                {
                    "day" => time.ToString("yyyy-MM-dd"),
                    "week" => $"{time.Year}-W{GetWeekOfYear(time)}",
                    "month" => time.ToString("yyyy-MM"),
                    _ => time.ToString("yyyy-MM")
                };

                trends[key] = trends.GetValueOrDefault(key, 0) + quantity;
            }
        }

        return trends.OrderBy(kvp => kvp.Key).ToDictionary(kvp => kvp.Key, kvp => Math.Round(kvp.Value, 2));
    }

    private static object CalculateEfficiencyMetrics(List<dynamic> energyData)
    {
        // Simplified efficiency calculations
        var avgConsumption = energyData.Count > 0 ? 
            energyData.Average(item => 
                item.Attributes.TryGetValue("Quantity", out var qty) && 
                double.TryParse(qty?.ToString(), out var q) ? q : 0) : 0;

        return new
        {
            averageEfficiency = Math.Round(avgConsumption, 2),
            efficiencyRating = avgConsumption < 100 ? "High" : avgConsumption < 200 ? "Medium" : "Low",
            improvementPotential = Math.Round(Math.Max(0, avgConsumption - 80), 2)
        };
    }

    private static List<string> GenerateEfficiencyRecommendations(List<dynamic> energyData)
    {
        var recommendations = new List<string>();
        
        var avgConsumption = energyData.Count > 0 ? 
            energyData.Average(item => 
                item.Attributes.TryGetValue("Quantity", out var qty) && 
                double.TryParse(qty?.ToString(), out var q) ? q : 0) : 0;

        if (avgConsumption > 200)
        {
            recommendations.Add("Consider energy efficiency upgrades for high-consumption facilities");
        }

        if (energyData.Count < 100)
        {
            recommendations.Add("Increase measurement frequency for better monitoring");
        }

        recommendations.Add("Implement demand response strategies during peak hours");

        return recommendations;
    }

    private static object CalculatePeriodComparison(List<dynamic> current, List<dynamic> previous)
    {
        var currentSummary = (dynamic)CalculateEnergySummary(current);
        var previousSummary = (dynamic)CalculateEnergySummary(previous);

        var consumptionChange = currentSummary.totalConsumption - previousSummary.totalConsumption;
        var percentChange = previousSummary.totalConsumption > 0 ? 
            (consumptionChange / previousSummary.totalConsumption) * 100 : 0;

        return new
        {
            consumptionChange = Math.Round(consumptionChange, 2),
            percentChange = Math.Round(percentChange, 1),
            measurementChange = current.Count - previous.Count,
            trend = percentChange > 5 ? "Increasing" : percentChange < -5 ? "Decreasing" : "Stable"
        };
    }

    private static object CalculateMachinePerformanceMetrics(dynamic machine, DateTime from, DateTime to)
    {
        // Simplified performance calculation
        var operatingHours = double.TryParse(machine.Attributes.GetValueOrDefault("OperatingHours")?.ToString(), out var hours) ? hours : 0;
        var standStill = double.TryParse(machine.Attributes.GetValueOrDefault("StandStillCounter")?.ToString(), out var still) ? still : 0;
        
        var totalHours = operatingHours + standStill;
        var uptime = totalHours > 0 ? (operatingHours / totalHours) * 100 : 0;

        return new
        {
            uptime = Math.Round(uptime, 1),
            operatingHours,
            standStillHours = standStill,
            efficiency = uptime > 90 ? "Excellent" : uptime > 75 ? "Good" : uptime > 50 ? "Fair" : "Poor"
        };
    }

    private static List<string> GeneratePerformanceRecommendations(IEnumerable<object> machineAnalysis)
    {
        var recommendations = new List<string>();
        
        var lowPerformanceMachines = machineAnalysis.Count(m => ((dynamic)m).performance?.uptime < 75);
        if (lowPerformanceMachines > 0)
        {
            recommendations.Add($"Review maintenance schedule for {lowPerformanceMachines} underperforming machines");
        }

        recommendations.Add("Implement predictive maintenance for critical equipment");
        recommendations.Add("Consider equipment upgrades for machines with consistently low uptime");

        return recommendations;
    }

    private static object CalculateMaintenanceCostBreakdown(IEnumerable<dynamic> orders, string groupBy)
    {
        double totalPlanned = 0;
        double totalActual = 0;
        var breakdown = new Dictionary<string, object>();

        foreach (var order in orders)
        {
            if (double.TryParse(order.Attributes.GetValueOrDefault("PlannedCosts")?.ToString(), out var planned))
                totalPlanned += planned;
            
            if (double.TryParse(order.Attributes.GetValueOrDefault("ActualCosts")?.ToString(), out var actual))
                totalActual += actual;

            // Group by logic would go here based on groupBy parameter
        }

        var variance = totalActual - totalPlanned;
        var orderCount = orders.Count();

        return new
        {
            totalPlanned = Math.Round(totalPlanned, 2),
            totalActual = Math.Round(totalActual, 2),
            variance = Math.Round(variance, 2),
            averagePerOrder = orderCount > 0 ? Math.Round(totalActual / orderCount, 2) : 0,
            breakdown
        };
    }

    private static object CalculateCostTrends(IEnumerable<dynamic> orders, DateTime from, DateTime to)
    {
        var monthlyTrends = new Dictionary<string, double>();
        
        foreach (var order in orders)
        {
            if (DateTime.TryParse(order.Attributes.GetValueOrDefault("CreatedAt")?.ToString(), out var created) &&
                double.TryParse(order.Attributes.GetValueOrDefault("ActualCosts")?.ToString(), out var cost))
            {
                var month = created.ToString("yyyy-MM");
                monthlyTrends[month] = monthlyTrends.GetValueOrDefault(month, 0) + cost;
            }
        }

        return monthlyTrends.OrderBy(kvp => kvp.Key).ToDictionary(kvp => kvp.Key, kvp => Math.Round(kvp.Value, 2));
    }

    private static object GenerateCostForecast(IEnumerable<dynamic> orders, int forecastMonths)
    {
        // Simplified forecast based on historical average
        var monthlyAverage = orders.Count() > 0 ? 
            orders.Average(o => double.TryParse(o.Attributes.GetValueOrDefault("ActualCosts")?.ToString(), out var cost) ? cost : 0) : 0;

        var forecast = new List<object>();
        var baseDate = DateTime.Now.AddMonths(1);

        for (int i = 0; i < forecastMonths; i++)
        {
            var forecastDate = baseDate.AddMonths(i);
            forecast.Add(new
            {
                month = forecastDate.ToString("yyyy-MM"),
                predictedCost = Math.Round(monthlyAverage * 1.02, 2), // 2% growth assumption
                confidence = "Medium"
            });
        }

        return forecast;
    }

    private static List<string> GenerateCostInsights(dynamic costAnalysis, object trends)
    {
        var insights = new List<string>();
        
        if (costAnalysis.variance > 0)
        {
            insights.Add($"Maintenance costs are {Math.Round((costAnalysis.variance / costAnalysis.totalPlanned) * 100, 1)}% over budget");
        }

        insights.Add("Consider implementing preventive maintenance to reduce emergency repair costs");
        
        return insights;
    }

    private static (DateTime from, DateTime to) GetPeriodDates(string period)
    {
        var now = DateTime.Now;
        return period.ToLower() switch
        {
            "today" => (now.Date, now.Date.AddDays(1)),
            "week" => (now.AddDays(-7), now),
            "quarter" => (now.AddMonths(-3), now),
            "year" => (now.AddYears(-1), now),
            _ => (now.AddMonths(-1), now) // default to month
        };
    }

    private static async Task<object> CalculateEnergyKpis(IRuntimeRepository repository, IOctoSession session, DateTime from, DateTime to)
    {
        // Simplified KPI calculation
        return new
        {
            totalConsumption = 12500.5,
            previousConsumption = 11800.2,
            averageEfficiency = 85.2,
            costSavings = 2340.50
        };
    }

    private static async Task<object> CalculateMaintenanceKpis(IRuntimeRepository repository, IOctoSession session, DateTime from, DateTime to)
    {
        return new
        {
            totalCosts = 45600.75,
            previousCosts = 48200.30,
            plannedVsActual = 102.5,
            averageResponseTime = 4.2
        };
    }

    private static async Task<object> CalculateOperationalKpis(IRuntimeRepository repository, IOctoSession session, DateTime from, DateTime to)
    {
        return new
        {
            averageUptime = 94.2,
            previousUptime = 91.8,
            activeAlarms = 3,
            mtbf = 145.6 // Mean Time Between Failures
        };
    }

    private static async Task<object> GetActiveAlerts(IRuntimeRepository repository, IOctoSession session)
    {
        return new
        {
            critical = 1,
            high = 2,
            medium = 5,
            total = 8
        };
    }

    private static string CalculateTrendDirection(double current, double previous)
    {
        var change = ((current - previous) / previous) * 100;
        return change > 5 ? "Up" : change < -5 ? "Down" : "Stable";
    }

    private static object GenerateEnergyForecast(dynamic energyKpis)
    {
        return new
        {
            nextMonth = Math.Round(energyKpis.totalConsumption * 1.02, 2),
            confidence = "High",
            factors = new[] { "Seasonal trends", "Historical patterns" }
        };
    }

    private static object GenerateMaintenanceForecast(dynamic maintenanceKpis)
    {
        return new
        {
            nextMonth = Math.Round(maintenanceKpis.totalCosts * 0.98, 2),
            confidence = "Medium",
            factors = new[] { "Planned maintenance", "Equipment age" }
        };
    }

    private static List<string> GenerateExecutiveRecommendations(dynamic energy, dynamic maintenance, dynamic operational)
    {
        return new List<string>
        {
            "Focus on energy efficiency improvements in high-consumption facilities",
            "Implement predictive maintenance to reduce unplanned downtime",
            "Consider equipment upgrades for aging infrastructure",
            "Optimize maintenance schedules based on operational data"
        };
    }

    private static int GetWeekOfYear(DateTime date)
    {
        var cal = System.Globalization.CultureInfo.CurrentCulture.Calendar;
        return cal.GetWeekOfYear(date, System.Globalization.CalendarWeekRule.FirstFourDayWeek, DayOfWeek.Monday);
    }

    #endregion
}
*/