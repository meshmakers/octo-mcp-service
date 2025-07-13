namespace Meshmakers.Octo.Backend.McpServices.Services;

/// <summary>
/// Tool usage statistics for monitoring and performance analysis
/// </summary>
public class ToolUsageStats
{
    /// <summary>
    /// Gets or sets the name of the tool
    /// </summary>
    public string ToolName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the total number of invocations
    /// </summary>
    public int TotalInvocations { get; set; }

    /// <summary>
    /// Gets or sets the number of successful invocations
    /// </summary>
    public int SuccessfulInvocations { get; set; }

    /// <summary>
    /// Gets or sets the number of failed invocations
    /// </summary>
    public int FailedInvocations { get; set; }

    /// <summary>
    /// Gets or sets the total execution time across all invocations
    /// </summary>
    public TimeSpan TotalExecutionTime { get; set; }

    /// <summary>
    /// Gets or sets the average execution time per invocation
    /// </summary>
    public TimeSpan AverageExecutionTime { get; set; }

    /// <summary>
    /// Gets or sets the last invocation timestamp
    /// </summary>
    public DateTime LastInvocation { get; set; }

    /// <summary>
    /// Gets or sets the list of error messages from failed invocations
    /// </summary>
    public List<string> ErrorMessages { get; set; } = new();

    /// <summary>
    /// Gets the success rate as a percentage
    /// </summary>
    public double SuccessRate => TotalInvocations > 0 ? (double)SuccessfulInvocations / TotalInvocations * 100 : 0;
}