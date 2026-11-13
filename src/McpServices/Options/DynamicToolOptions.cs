namespace Meshmakers.Octo.Backend.McpServices.Options;

/// <summary>
///     Configuration options for dynamic tool generation and caching
/// </summary>
public class DynamicToolOptions
{
    /// <summary>
    ///     Enable dynamic tool generation based on CK models
    /// </summary>
    public bool EnableDynamicToolGeneration { get; set; } = true;

    /// <summary>
    ///     Cache duration for CK type graphs in minutes
    /// </summary>
    public int CkTypeGraphCacheDurationMinutes { get; set; } = 30;

    /// <summary>
    ///     Hard page size limit of the query tools (MaxPageSize of Feature AB#8793). It cannot be raised by
    ///     configuration: a <see cref="MaxQueryResultLimit" /> above it takes no effect (the effective maximum stays
    ///     1000) and the health check reports the misconfiguration.
    /// </summary>
    public const int MaxPageSize = 1000;

    /// <summary>
    ///     Maximum number of entities a query tool returns in a single call (<c>query_entities</c>,
    ///     <c>query_entities_simple</c>, <c>navigate_associations</c>, <c>get_association_tree</c> roots and
    ///     <c>execute_runtime_query</c>). A larger requested limit is refused, never silently capped (AB#8904).
    ///     May be lowered; values above <see cref="MaxPageSize" /> (1000) are ignored (see
    ///     <see cref="GetEffectiveMaxQueryResultLimit" />).
    /// </summary>
    public int MaxQueryResultLimit { get; set; } = MaxPageSize;

    /// <summary>
    ///     Page size the query tools use when the caller passes no limit/take (AB#8904). Values above the effective
    ///     maximum are lowered to it.
    /// </summary>
    public int DefaultQueryLimit { get; set; } = 100;

    /// <summary>
    ///     The maximum the query tools enforce: <see cref="MaxQueryResultLimit" /> between 1 and
    ///     <see cref="MaxPageSize" />; a value of 0 or less falls back to <see cref="MaxPageSize" />.
    /// </summary>
    public int GetEffectiveMaxQueryResultLimit() =>
        MaxQueryResultLimit > 0 ? Math.Min(MaxQueryResultLimit, MaxPageSize) : MaxPageSize;

    /// <summary>
    ///     The page size the query tools use without a limit: <see cref="DefaultQueryLimit" />, at most
    ///     <see cref="GetEffectiveMaxQueryResultLimit" />; a value of 0 or less falls back to 100.
    /// </summary>
    public int GetEffectiveDefaultQueryLimit()
    {
        var max = GetEffectiveMaxQueryResultLimit();
        return Math.Min(DefaultQueryLimit > 0 ? DefaultQueryLimit : 100, max);
    }

    /// <summary>
    ///     Enable detailed error responses for debugging
    /// </summary>
    public bool EnableDetailedErrors { get; set; } = false;

    /// <summary>
    ///     Timeout for long-running analytics operations in seconds
    /// </summary>
    public int AnalyticsTimeoutSeconds { get; set; } = 300;

    /// <summary>
    ///     Enable tool usage statistics collection
    /// </summary>
    public bool EnableToolStatistics { get; set; } = true;

    /// <summary>
    ///     Models to preload for faster tool generation
    /// </summary>
    public string[] PreloadModels { get; set; } =
    [
        "System-1.0.0",
        "Basic-1.0.0",
        "EnergyCommunity-1.0.0",
        "Industry.Basic-1.0.0"
    ];

    /// <summary>
    ///     Types to exclude from dynamic tool generation
    /// </summary>
    public string[] ExcludedTypes { get; set; } =
    [
        "System-1.0.0/AutoIncrement-1.0.0",
        "System-1.0.0/Tenant-1.0.0"
    ];

    /// <summary>
    ///     Enable domain-specific tool categories
    /// </summary>
    public DomainToolOptions DomainTools { get; set; } = new();
}

/// <summary>
///     Configuration for domain-specific tools
/// </summary>
public class DomainToolOptions
{
    /// <summary>
    ///     Enable energy community specific tools
    /// </summary>
    public bool EnableEnergyTools { get; set; } = true;

    /// <summary>
    ///     Enable industrial IoT and maintenance tools
    /// </summary>
    public bool EnableIndustryTools { get; set; } = true;

    /// <summary>
    ///     Enable advanced analytics and reporting tools
    /// </summary>
    public bool EnableAnalyticsTools { get; set; } = true;

    /// <summary>
    ///     Enable environmental monitoring tools
    /// </summary>
    public bool EnableEnvironmentTools { get; set; } = false;

    /// <summary>
    ///     Maximum date range for analytics queries in days
    /// </summary>
    public int MaxAnalyticsDateRangeDays { get; set; } = 365;

    /// <summary>
    ///     Enable forecasting capabilities
    /// </summary>
    public bool EnableForecasting { get; set; } = true;
}