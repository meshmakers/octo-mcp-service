using Meshmakers.Octo.Backend.McpServices.Options;

namespace Meshmakers.Octo.Backend.McpServices.Services;

/// <summary>
///     Service for dynamic tool generation and CK type caching
/// </summary>
public interface IDynamicToolService
{
    /// <summary>
    ///     Get domain-specific configuration
    /// </summary>
    DomainToolOptions GetDomainOptions();

    /// <summary>
    ///     Validate query parameters against configuration limits
    /// </summary>
    (bool isValid, string? errorMessage) ValidateQueryParameters(int? limit, int? offset, DateTime? fromDate,
        DateTime? toDate);

    /// <summary>
    ///     Record tool usage statistics
    /// </summary>
    Task RecordToolUsageAsync(string toolName, TimeSpan executionTime, bool success, string? errorMessage = null);
}