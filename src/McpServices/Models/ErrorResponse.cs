namespace Meshmakers.Octo.Backend.McpServices.Models;

/// <summary>
/// Error response for failed operations
/// </summary>
public sealed class ErrorResponse
{
    /// <summary>
    /// Short error description
    /// </summary>
    public required string Error { get; init; }
    
    /// <summary>
    /// Detailed error message
    /// </summary>
    public required string Message { get; init; }
    
    /// <summary>
    /// Search term that caused the error, if applicable
    /// </summary>
    public string? SearchTerm { get; init; }
    
    /// <summary>
    /// Construction Kit Type ID that caused the error, if applicable
    /// </summary>
    public string? CkTypeId { get; init; }
}