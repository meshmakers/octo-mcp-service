// ReSharper disable UnusedAutoPropertyAccessor.Global

namespace Meshmakers.Octo.Backend.McpServices.Models;

/// <summary>
///     Response for available models query
/// </summary>
public sealed class AvailableModelsResponse: ErrorResponse
{
    /// <summary>
    ///     Total number of available Construction Kit models
    /// </summary>
    public int? TotalModels { get; init; }

    /// <summary>
    ///     List of available model IDs
    /// </summary>
    public List<string>? Models { get; init; }
}