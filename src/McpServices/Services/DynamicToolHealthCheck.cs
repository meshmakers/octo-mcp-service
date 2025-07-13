using Microsoft.Extensions.Diagnostics.HealthChecks;
using Meshmakers.Octo.Backend.McpServices.Options;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Backend.McpServices.Services;

/// <summary>
/// Health check for dynamic tool services and configuration
/// </summary>
public class DynamicToolHealthCheck : IHealthCheck
{
    private readonly IDynamicToolService _dynamicToolService;
    private readonly DynamicToolOptions _options;
    private readonly ILogger<DynamicToolHealthCheck> _logger;

    /// <summary>
    /// Constructor for dynamic tool health check
    /// </summary>
    /// <param name="dynamicToolService">Dynamic tool service instance</param>
    /// <param name="options">Dynamic tool options</param>
    /// <param name="logger">Logger for health check</param>
    public DynamicToolHealthCheck(
        IDynamicToolService dynamicToolService,
        IOptions<DynamicToolOptions> options,
        ILogger<DynamicToolHealthCheck> logger)
    {
        _dynamicToolService = dynamicToolService;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>
    /// Checks the health of the dynamic tool service
    /// </summary>
    /// <param name="context">Health check context</param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var healthData = new Dictionary<string, object>
            {
                ["dynamic_tool_generation_enabled"] = _options.EnableDynamicToolGeneration,
                ["tool_statistics_enabled"] = _options.EnableToolStatistics,
                ["max_query_limit"] = _options.MaxQueryResultLimit,
                ["default_query_limit"] = _options.DefaultQueryLimit,
                ["analytics_timeout_seconds"] = _options.AnalyticsTimeoutSeconds,
                ["cache_duration_minutes"] = _options.CkTypeGraphCacheDurationMinutes
            };

            // Check domain tool configurations
            var domainOptions = _dynamicToolService.GetDomainOptions();
            healthData["domain_tools"] = new
            {
                energy_tools_enabled = domainOptions.EnableEnergyTools,
                industry_tools_enabled = domainOptions.EnableIndustryTools,
                analytics_tools_enabled = domainOptions.EnableAnalyticsTools,
                environment_tools_enabled = domainOptions.EnableEnvironmentTools,
                forecasting_enabled = domainOptions.EnableForecasting,
                max_date_range_days = domainOptions.MaxAnalyticsDateRangeDays
            };

            // Validate configuration sanity
            var issues = new List<string>();

            if (_options.MaxQueryResultLimit <= 0)
            {
                issues.Add("MaxQueryResultLimit must be greater than 0");
            }

            if (_options.DefaultQueryLimit <= 0 || _options.DefaultQueryLimit > _options.MaxQueryResultLimit)
            {
                issues.Add("DefaultQueryLimit must be between 1 and MaxQueryResultLimit");
            }

            if (_options.AnalyticsTimeoutSeconds <= 0)
            {
                issues.Add("AnalyticsTimeoutSeconds must be greater than 0");
            }

            if (_options.CkTypeGraphCacheDurationMinutes <= 0)
            {
                issues.Add("CkTypeGraphCacheDurationMinutes must be greater than 0");
            }

            if (domainOptions.MaxAnalyticsDateRangeDays <= 0)
            {
                issues.Add("MaxAnalyticsDateRangeDays must be greater than 0");
            }

            healthData["configuration_issues"] = issues;
            healthData["preload_models"] = _options.PreloadModels;
            healthData["excluded_types"] = _options.ExcludedTypes;

            // Test basic service functionality
            var testValidation = _dynamicToolService.ValidateQueryParameters(10, 0, null, null);
            healthData["parameter_validation_working"] = testValidation.isValid;

            if (issues.Any())
            {
                return Task.FromResult(HealthCheckResult.Degraded(
                    $"Dynamic tool service has configuration issues: {string.Join(", ", issues)}",
                    data: healthData));
            }

            var status = _options.EnableDynamicToolGeneration 
                ? HealthStatus.Healthy 
                : HealthStatus.Degraded;

            var description = _options.EnableDynamicToolGeneration
                ? "Dynamic tool service is healthy and operational"
                : "Dynamic tool generation is disabled";

            return Task.FromResult(new HealthCheckResult(status, description, data: healthData));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Health check failed for dynamic tool service");
            
            return Task.FromResult(HealthCheckResult.Unhealthy(
                "Dynamic tool service health check failed",
                ex,
                new Dictionary<string, object>
                {
                    ["error"] = ex.Message,
                    ["error_type"] = ex.GetType().Name
                }));
        }
    }
}
