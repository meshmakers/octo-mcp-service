namespace Meshmakers.Octo.Backend.McpServices.Options;

/// <summary>
/// Configuration options for dynamic tool generation and caching
/// </summary>
public class DynamicToolOptions
{
    /// <summary>
    /// Enable dynamic tool generation based on CK models
    /// </summary>
    public bool EnableDynamicToolGeneration { get; set; } = true;

    /// <summary>
    /// Cache duration for CK type graphs in minutes
    /// </summary>
    public int CkTypeGraphCacheDurationMinutes { get; set; } = 30;

    /// <summary>
    /// Maximum number of entities to return in a single query
    /// </summary>
    public int MaxQueryResultLimit { get; set; } = 1000;

    /// <summary>
    /// Default limit for query results when not specified
    /// </summary>
    public int DefaultQueryLimit { get; set; } = 100;

    /// <summary>
    /// Enable detailed error responses for debugging
    /// </summary>
    public bool EnableDetailedErrors { get; set; } = false;

    /// <summary>
    /// Timeout for long-running analytics operations in seconds
    /// </summary>
    public int AnalyticsTimeoutSeconds { get; set; } = 300;

    /// <summary>
    /// Enable tool usage statistics collection
    /// </summary>
    public bool EnableToolStatistics { get; set; } = true;

    /// <summary>
    /// Models to preload for faster tool generation
    /// </summary>
    public string[] PreloadModels { get; set; } =
    [
        "System-1.0.0",
        "Basic-1.0.0", 
        "EnergyCommunity-1.0.0",
        "Industry.Basic-1.0.0"
    ];

    /// <summary>
    /// Types to exclude from dynamic tool generation
    /// </summary>
    public string[] ExcludedTypes { get; set; } = 
    {
        "System-1.0.0/AutoIncrement-1.0.0",
        "System-1.0.0/Tenant-1.0.0"
    };

    /// <summary>
    /// Enable domain-specific tool categories
    /// </summary>
    public DomainToolOptions DomainTools { get; set; } = new();
}

/// <summary>
/// Configuration for domain-specific tools
/// </summary>
public class DomainToolOptions
{
    /// <summary>
    /// Enable energy community specific tools
    /// </summary>
    public bool EnableEnergyTools { get; set; } = true;

    /// <summary>
    /// Enable industrial IoT and maintenance tools
    /// </summary>
    public bool EnableIndustryTools { get; set; } = true;

    /// <summary>
    /// Enable advanced analytics and reporting tools
    /// </summary>
    public bool EnableAnalyticsTools { get; set; } = true;

    /// <summary>
    /// Enable environmental monitoring tools
    /// </summary>
    public bool EnableEnvironmentTools { get; set; } = false;

    /// <summary>
    /// Maximum date range for analytics queries in days
    /// </summary>
    public int MaxAnalyticsDateRangeDays { get; set; } = 365;

    /// <summary>
    /// Enable forecasting capabilities
    /// </summary>
    public bool EnableForecasting { get; set; } = true;
}
