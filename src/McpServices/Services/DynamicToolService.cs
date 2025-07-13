using System.Collections.Concurrent;
using Meshmakers.Octo.Backend.McpServices.Options;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Backend.McpServices.Services;

/// <summary>
/// Implementation of dynamic tool service with caching
/// </summary>
internal class DynamicToolService(IOptions<DynamicToolOptions> options, ILogger<DynamicToolService> logger)
    : IDynamicToolService
{
    private readonly DynamicToolOptions _options = options.Value;
    private readonly ConcurrentDictionary<string, ToolUsageStats> _usageStats = new();

    public bool IsTypeExcluded(string ckTypeId)
    {
        return _options.ExcludedTypes.Contains(ckTypeId);
    }

    public DomainToolOptions GetDomainOptions()
    {
        return _options.DomainTools;
    }

    public (bool isValid, string? errorMessage) ValidateQueryParameters(int? limit, int? offset, DateTime? fromDate, DateTime? toDate)
    {
        // Validate limit
        if (limit.HasValue)
        {
            if (limit.Value <= 0)
            {
                return (false, "Limit must be greater than 0");
            }

            if (limit.Value > _options.MaxQueryResultLimit)
            {
                return (false, $"Limit cannot exceed {_options.MaxQueryResultLimit}");
            }
        }

        // Validate offset
        if (offset.HasValue && offset.Value < 0)
        {
            return (false, "Offset cannot be negative");
        }

        // Validate date range
        if (fromDate.HasValue && toDate.HasValue)
        {
            if (fromDate.Value >= toDate.Value)
            {
                return (false, "From date must be before to date");
            }

            var dateRange = toDate.Value - fromDate.Value;
            if (dateRange.TotalDays > _options.DomainTools.MaxAnalyticsDateRangeDays)
            {
                return (false, $"Date range cannot exceed {_options.DomainTools.MaxAnalyticsDateRangeDays} days");
            }
        }

        return (true, null);
    }

    public Task RecordToolUsageAsync(string toolName, TimeSpan executionTime, bool success, string? errorMessage = null)
    {
        if (!_options.EnableToolStatistics)
        {
            return Task.CompletedTask;
        }

        var stats = _usageStats.GetOrAdd(toolName, _ => new ToolUsageStats { ToolName = toolName });
        
        lock (stats)
        {
            stats.TotalInvocations++;
            stats.TotalExecutionTime += executionTime;
            
            if (success)
            {
                stats.SuccessfulInvocations++;
            }
            else
            {
                stats.FailedInvocations++;
                if (!string.IsNullOrEmpty(errorMessage))
                {
                    stats.ErrorMessages.Add(errorMessage);
                    if (stats.ErrorMessages.Count > 100) // Keep only recent errors
                    {
                        stats.ErrorMessages.RemoveRange(0, 50);
                    }
                }
            }

            stats.LastInvocation = DateTime.UtcNow;
            stats.AverageExecutionTime = TimeSpan.FromMilliseconds(
                stats.TotalExecutionTime.TotalMilliseconds / stats.TotalInvocations);
        }

        logger.LogDebug("Recorded tool usage: {ToolName}, Success: {Success}, Duration: {Duration}ms",
            toolName, success, executionTime.TotalMilliseconds);
        return Task.CompletedTask;
    }
}