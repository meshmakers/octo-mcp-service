using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;

namespace Meshmakers.Octo.Backend.McpServices.Models;

/// <summary>
///     Schema response for a specific attribute
/// </summary>
public sealed class AttributeSchemaResponse
{
    /// <summary>
    ///     Gets or sets the full attribute identifier
    /// </summary>
    public required string AttributePath { get; init; }

    /// <summary>
    ///     Gets or sets the value type of the attribute
    /// </summary>
    public required AttributeValueTypesDto ValueType { get; init; }
}