namespace Meshmakers.Octo.Backend.McpServices.Models;

/// <summary>
/// Detailed schema information for a specific type
/// </summary>
public sealed class TypeSchemaResponse
{
    /// <summary>
    /// Full type identifier
    /// </summary>
    public required string TypeId { get; init; }
    
    /// <summary>
    /// Model ID containing this type
    /// </summary>
    public required string ModelId { get; init; }
    
    /// <summary>
    /// Human-readable type name
    /// </summary>
    public required string TypeName { get; init; }
    
    /// <summary>
    /// Version string of the type
    /// </summary>
    public required string Version { get; init; }
    
    /// <summary>
    /// Indicates if this type is abstract
    /// </summary>
    public required bool IsAbstract { get; init; }
    
    /// <summary>
    /// Indicates if this type is final (cannot be inherited)
    /// </summary>
    public required bool IsFinal { get; init; }
    
    /// <summary>
    /// Indicates if this type can be used as a collection root
    /// </summary>
    public required bool IsCollectionRoot { get; init; }
    
    /// <summary>
    /// Indicates if this type is a stream type for real-time data
    /// </summary>
    public required bool IsStreamType { get; init; }
    
    /// <summary>
    /// Optional description of the type
    /// </summary>
    public string? Description { get; init; }
    
    /// <summary>
    /// Base type this type derives from, if any
    /// </summary>
    public string? DerivedFrom { get; init; }
    
    /// <summary>
    /// Complete inheritance hierarchy information
    /// </summary>
    public object? InheritanceHierarchy { get; init; }
    
    /// <summary>
    /// Index definitions for optimized queries
    /// </summary>
    public object? Indexes { get; init; }
    
    /// <summary>
    /// All attributes available on this type
    /// </summary>
    public required IEnumerable<AttributeSchemaResponse> Attributes { get; init; }
    
    /// <summary>
    /// Association definitions for income associations
    /// </summary>
    public required IEnumerable<AssociationSchemaResponse> InboundAssociations { get; init; }

    /// <summary>
    /// Association definitions for outgoing associations
    /// </summary>
    public required IEnumerable<AssociationSchemaResponse> OutboundAssociations { get; init; }

    /// <summary>
    /// Schema details for entity creation and validation
    /// </summary>
    public required TypeSchemaDetails Schema { get; init; }
}