namespace Meshmakers.Octo.Backend.McpServices.Models;

/// <summary>
///     Basic metadata for a Construction Kit type
/// </summary>
public class CkTypeInfo
{
    /// <summary>
    ///     Full Construction Kit Type ID (e.g., 'EnergyCommunity-1.0.0/Customer-1')
    /// </summary>
    public required string CkTypeId { get; init; }

    /// <summary>
    ///     Model ID that contains this type (e.g., 'EnergyCommunity-1.0.0')
    /// </summary>
    public required string ModelId { get; init; }

    /// <summary>
    ///     Type identifier within the model (e.g., 'Customer')
    /// </summary>
    public required string TypeId { get; init; }

    /// <summary>
    ///     Human-readable type name (e.g., 'Customer')
    /// </summary>
    public required string TypeName { get; init; }

    /// <summary>
    ///     Version information of the type
    /// </summary>
    public required uint Version { get; init; }

    /// <summary>
    ///     Indicates if this type is abstract and cannot be instantiated
    /// </summary>
    public required bool IsAbstract { get; init; }

    /// <summary>
    ///     Indicates if this type cannot be inherited from
    /// </summary>
    public required bool IsFinal { get; init; }

    /// <summary>
    ///     Indicates if this type can be used as a collection root for queries
    /// </summary>
    public required bool IsCollectionRoot { get; init; }

    /// <summary>
    ///     Optional description of the type's purpose
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    ///     Base type this type inherits from, if any
    /// </summary>
    public string? DerivedFrom { get; init; }
}