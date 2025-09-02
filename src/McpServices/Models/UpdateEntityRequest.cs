namespace Meshmakers.Octo.Backend.McpServices.Models;

/// <summary>
///     Defines update information of an entity
/// </summary>
public class UpdateEntityRequest
{
    /// <summary>
    ///     Gets or sets the Runtime ID of the entity to be updated
    /// </summary>
    public required string RtId { get; set; }

    /// <summary>
    ///     Gets or sets the Construction Kit Type ID of the entity to be updated
    /// </summary>
    public required string CkTypeId { get; set; }

    /// <summary>
    ///     Gets or sets the attributes of the entity to be updated as a dictionary of key-value pairs
    /// </summary>
    public required List<AttributeUpdateItem> Attributes { get; set; }
}

/// <summary>
///     Defines an attribute update item with path and value
/// </summary>
public class AttributeUpdateItem
{
    /// <summary>
    ///     Gets or sets the attribute path
    /// </summary>
    public required string AttributePath { get; set; }

    /// <summary>
    ///     Gets or sets the attribute value
    /// </summary>
    public required object? Value { get; set; }
}