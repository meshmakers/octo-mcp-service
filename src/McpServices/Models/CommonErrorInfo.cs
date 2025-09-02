namespace Meshmakers.Octo.Backend.McpServices.Models;

/// <summary>
///     Information about a common error
/// </summary>
public sealed class CommonErrorInfo
{
    /// <summary>
    ///     Error message or type
    /// </summary>
    public required string Error { get; init; }

    /// <summary>
    ///     Number of times this error occurred
    /// </summary>
    public required int Count { get; init; }
}