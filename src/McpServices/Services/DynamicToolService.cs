using System.Collections.Concurrent;
using Meshmakers.Octo.Backend.McpServices.Options;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Backend.McpServices.Services;

/// <summary>
/// Service for dynamic tool generation and CK type caching
/// </summary>
public interface IDynamicToolService
{
    /// <summary>
    /// Get cached CK type graph or load it if not cached
    /// </summary>
    Task<CkTypeGraph> GetCkTypeGraphAsync(IRuntimeRepository repository, CkId<CkTypeId> ckTypeId);

    /// <summary>
    /// Get all available CK types for the tenant
    /// </summary>
    Task<IEnumerable<CkTypeDto>> GetAvailableTypesAsync(IRuntimeRepository repository);

    /// <summary>
    /// Check if a CK type is excluded from tool generation
    /// </summary>
    bool IsTypeExcluded(string ckTypeId);

    /// <summary>
    /// Get domain-specific configuration
    /// </summary>
    DomainToolOptions GetDomainOptions();

    /// <summary>
    /// Validate query parameters against configuration limits
    /// </summary>
    (bool isValid, string? errorMessage) ValidateQueryParameters(int? limit, int? offset, DateTime? fromDate, DateTime? toDate);

    /// <summary>
    /// Record tool usage statistics
    /// </summary>
    Task RecordToolUsageAsync(string toolName, TimeSpan executionTime, bool success, string? errorMessage = null);

    /// <summary>
    /// Clear the CK type cache
    /// </summary>
    void ClearCache();
}

/// <summary>
/// Implementation of dynamic tool service with caching
/// </summary>
internal class DynamicToolService(IOptions<DynamicToolOptions> options, ILogger<DynamicToolService> logger)
    : IDynamicToolService
{
    private readonly DynamicToolOptions _options = options.Value;
    private readonly ConcurrentDictionary<string, (CkTypeGraph typeGraph, DateTime cachedAt)> _typeGraphCache = new();
    private readonly ConcurrentDictionary<string, List<CkTypeDto>> _availableTypesCache = new();
    private readonly ConcurrentDictionary<string, ToolUsageStats> _usageStats = new();

    public async Task<CkTypeGraph> GetCkTypeGraphAsync(IRuntimeRepository repository, CkId<CkTypeId> ckTypeId)
    {
        var cacheKey = $"{repository.TenantId}:{ckTypeId}";
        var now = DateTime.UtcNow;

        // Check cache
        if (_typeGraphCache.TryGetValue(cacheKey, out var cached))
        {
            var cacheAge = now - cached.cachedAt;
            if (cacheAge.TotalMinutes < _options.CkTypeGraphCacheDurationMinutes)
            {
                logger.LogDebug("Retrieved CK type graph from cache: {CkTypeId}", ckTypeId);
                return cached.typeGraph;
            }
        }

        // Load from repository
        try
        {
            logger.LogDebug("Loading CK type graph from repository: {CkTypeId}", ckTypeId);
            var typeGraph = await repository.GetCkTypeGraphAsync(ckTypeId);
            
            // Cache the result
            _typeGraphCache[cacheKey] = (typeGraph, now);
            
            return typeGraph;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load CK type graph: {CkTypeId}", ckTypeId);
            throw;
        }
    }

    public async Task<IEnumerable<CkTypeDto>> GetAvailableTypesAsync(IRuntimeRepository repository)
    {
        var cacheKey = repository.TenantId;
        
        // Check cache
        if (_availableTypesCache.TryGetValue(cacheKey, out var cached))
        {
            logger.LogDebug("Retrieved available types from cache for tenant: {TenantId}", repository.TenantId);
            return cached;
        }

        // Build a list of available types by attempting to load known types
        var availableTypes = new List<CkTypeDto>();
        var knownTypes = GetKnownTypeIds();

        foreach (var typeId in knownTypes)
        {
            if (IsTypeExcluded(typeId))
                continue;

            try
            {
                var typeGraph = await GetCkTypeGraphAsync(repository, new CkId<CkTypeId>(typeId));
                availableTypes.Add(typeGraph.TypeWithAttributes);
            }
            catch
            {
                // Type not available in this tenant - skip
                continue;
            }
        }

        // Cache the result
        _availableTypesCache[cacheKey] = availableTypes;
        
        logger.LogInformation("Discovered {Count} available types for tenant: {TenantId}",
            availableTypes.Count, repository.TenantId);

        return availableTypes;
    }

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
                return (false, "Limit must be greater than 0");
            
            if (limit.Value > _options.MaxQueryResultLimit)
                return (false, $"Limit cannot exceed {_options.MaxQueryResultLimit}");
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
                return (false, "From date must be before to date");

            var dateRange = toDate.Value - fromDate.Value;
            if (dateRange.TotalDays > _options.DomainTools.MaxAnalyticsDateRangeDays)
                return (false, $"Date range cannot exceed {_options.DomainTools.MaxAnalyticsDateRangeDays} days");
        }

        return (true, null);
    }

    public async Task RecordToolUsageAsync(string toolName, TimeSpan executionTime, bool success, string? errorMessage = null)
    {
        if (!_options.EnableToolStatistics)
            return;

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
    }

    public void ClearCache()
    {
        _typeGraphCache.Clear();
        _availableTypesCache.Clear();
        logger.LogInformation("Cleared all caches");
    }

    private static string[] GetKnownTypeIds()
    {
        return
        [
            // System types
            "System-1.0.0/Entity-1.0.0",
            "System-1.0.0/Query-1.0.0",
            "System-1.0.0/AutoIncrement-1.0.0",
            "System-1.0.0/Configuration-1.0.0",
            "System-1.0.0/Tenant-1.0.0",

            // Basic types
            "Basic-1.0.0/NamedEntity-1.0.0",
            "Basic-1.0.0/Document-1.0.0",
            "Basic-1.0.0/TreeNode-1.0.0",
            "Basic-1.0.0/Asset-1.0.0",
            "Basic-1.0.0/Tree-1.0.0",
            "Basic-1.0.0/City-1.0.0",
            "Basic-1.0.0/Country-1.0.0",
            "Basic-1.0.0/State-1.0.0",
            "Basic-1.0.0/District-1.0.0",

            // Energy Community types
            "EnergyCommunity-1.0.0/Customer-1.0.0",
            "EnergyCommunity-1.0.0/MeteringPoint-1.0.0",
            "EnergyCommunity-1.0.0/Consumer-1.0.0",
            "EnergyCommunity-1.0.0/Producer-1.0.0",
            "EnergyCommunity-1.0.0/OperatingFacility-1.0.0",
            "EnergyCommunity-1.0.0/EnergyQuantity-1.0.0",
            "EnergyCommunity-1.0.0/EnergyPrice-1.0.0",
            "EnergyCommunity-1.0.0/BillingDocument-1.0.0",
            "EnergyCommunity-1.0.0/BillingDocumentLineItem-1.0.0",

            // Industry Basic types
            "Industry.Basic-1.0.0/Machine-1.0.0",
            "Industry.Basic-1.0.0/Event-1.0.0",
            "Industry.Basic-1.0.0/Alarm-1.0.0",

            // Industry Energy types
            "Industry.Energy-1.0.0/EnergyMeter-1.0.0",
            "Industry.Energy-1.0.0/Battery-1.0.0",
            "Industry.Energy-1.0.0/Inverter-1.0.0",
            "Industry.Energy-1.0.0/Photovoltaic-1.0.0",
            "Industry.Energy-1.0.0/Photovoltaic.Module-1.0.0",
            "Industry.Energy-1.0.0/Photovoltaic.String-1.0.0",

            // Industry Fluid types
            "Industry.Fluid-1.0.0/HeatMeter-1.0.0",
            "Industry.Fluid-1.0.0/WaterMeter-1.0.0",

            // Industry Maintenance types
            "Industry.Maintenance-1.0.0/Order-1.0.0",
            "Industry.Maintenance-1.0.0/OrderCosts-1.0.0",
            "Industry.Maintenance-1.0.0/OrderFeedback-1.0.0",
            "Industry.Maintenance-1.0.0/Employee-1.0.0",
            "Industry.Maintenance-1.0.0/CostCenter-1.0.0",
            "Industry.Maintenance-1.0.0/Workplace-1.0.0",
            "Industry.Maintenance-1.0.0/Account-1.0.0",
            "Industry.Maintenance-1.0.0/JournalEntry-1.0.0",

            // Environment types
            "Environment-1.0.0/EnvironmentalGoal-1.0.0",
            "Environment-1.0.0/WasteMeter-1.0.0",

            // System Identity types
            "System.Identity-1.0.0/User-1.0.0",
            "System.Identity-1.0.0/Role-1.0.0",
            "System.Identity-1.0.0/Client-1.0.0",
            "System.Identity-1.0.0/Resource-1.0.0",
            "System.Identity-1.0.0/ApiResource-1.0.0",
            "System.Identity-1.0.0/ApiScope-1.0.0",
            "System.Identity-1.0.0/IdentityResource-1.0.0",
            "System.Identity-1.0.0/IdentityProvider-1.0.0",
            "System.Identity-1.0.0/Permission-1.0.0",
            "System.Identity-1.0.0/PermissionRole-1.0.0",
            "System.Identity-1.0.0/PersistedGrant-1.0.0",

            // System Communication types
            "System.Communication-1.0.0/DeployableEntity-1.0.0",
            "System.Communication-1.0.0/Adapter-1.0.0",
            "System.Communication-1.0.0/Pipeline-1.0.0",
            "System.Communication-1.0.0/DataPipeline-1.0.0",
            "System.Communication-1.0.0/Pool-1.0.0",
            "System.Communication-1.0.0/Tag-1.0.0",

            // System Notification types
            "System.Notification-1.0.0/Event-1.0.0",
            "System.Notification-1.0.0/StatefulEvent-1.0.0",
            "System.Notification-1.0.0/NotificationTemplate-1.0.0",

            // Demo types
            "OctoSdkDemo-1.0.0/Customer-1.0.0",
            "OctoSdkDemo-1.0.0/MeteringPoint-1.0.0",
            "OctoSdkDemo-1.0.0/OperatingFacility-1.0.0"
        ];
    }
}

/// <summary>
/// Tool usage statistics for monitoring and performance analysis
/// </summary>
public class ToolUsageStats
{
    public string ToolName { get; set; } = string.Empty;
    public int TotalInvocations { get; set; }
    public int SuccessfulInvocations { get; set; }
    public int FailedInvocations { get; set; }
    public TimeSpan TotalExecutionTime { get; set; }
    public TimeSpan AverageExecutionTime { get; set; }
    public DateTime LastInvocation { get; set; }
    public List<string> ErrorMessages { get; set; } = new();
    
    public double SuccessRate => TotalInvocations > 0 ? (double)SuccessfulInvocations / TotalInvocations * 100 : 0;
}
