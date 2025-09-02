namespace Meshmakers.Octo.Backend.McpServices.Models;

/// <summary>
///     Error response for failed operations
/// </summary>
public abstract class ErrorResponse
{
    /// <summary>
    ///     Whether the operation was successful
    /// </summary>
    public required bool IsSuccess { get; init; }

    /// <summary>
    ///     Detailed error message
    /// </summary>
    public string? ErrorMessage { get; init; }
}