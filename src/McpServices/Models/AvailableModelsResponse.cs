// ReSharper disable UnusedAutoPropertyAccessor.Global

namespace Meshmakers.Octo.Backend.McpServices.Models;

/// <summary>
/// Response for available models query
/// </summary>
public sealed class AvailableModelsResponse
{
    /// <summary>
    /// Total number of available Construction Kit models
    /// </summary>
    public required int TotalModels { get; init; }
    
    /// <summary>
    /// List of available model IDs
    /// </summary>
    public required List<string> Models { get; init; }
}