// ReSharper disable UnusedAutoPropertyAccessor.Global
using Meshmakers.Octo.ConstructionKit.Contracts;

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

/// <summary>
/// Basic metadata for a Construction Kit type
/// </summary>
public sealed class CkTypeMetadata
{
    /// <summary>
    /// Full Construction Kit Type ID (e.g., 'EnergyCommunity-1.0.0/Customer-1.0.0')
    /// </summary>
    public required string CkTypeId { get; init; }
    
    /// <summary>
    /// Model ID that contains this type (e.g., 'EnergyCommunity-1.0.0')
    /// </summary>
    public required string ModelId { get; init; }
    
    /// <summary>
    /// Type identifier within the model (e.g., 'Customer')
    /// </summary>
    public required string TypeId { get; init; }
    
    /// <summary>
    /// Human-readable type name (e.g., 'Customer')
    /// </summary>
    public required string TypeName { get; init; }
    
    /// <summary>
    /// Version information of the type
    /// </summary>
    public required CkVersion Version { get; init; }
    
    /// <summary>
    /// Indicates if this type is abstract and cannot be instantiated
    /// </summary>
    public required bool IsAbstract { get; init; }
    
    /// <summary>
    /// Indicates if this type cannot be inherited from
    /// </summary>
    public required bool IsFinal { get; init; }
    
    /// <summary>
    /// Indicates if this type can be used as a collection root for queries
    /// </summary>
    public required bool IsCollectionRoot { get; init; }
    
    /// <summary>
    /// Optional description of the type's purpose
    /// </summary>
    public string? Description { get; init; }
    
    /// <summary>
    /// Base type this type inherits from, if any
    /// </summary>
    public string? DerivedFrom { get; init; }
    
    /// <summary>
    /// Number of attributes defined on this type
    /// </summary>
    public required int AttributeCount { get; init; }
    
    /// <summary>
    /// Number of incoming associations to this type
    /// </summary>
    public required int InAssociationCount { get; init; }
    
    /// <summary>
    /// Number of outgoing associations from this type
    /// </summary>
    public required int OutAssociationCount { get; init; }
}

/// <summary>
/// Response for available types query
/// </summary>
public sealed class AvailableTypesResponse
{
    /// <summary>
    /// Total number of types returned
    /// </summary>
    public required int TotalTypes { get; init; }
    
    /// <summary>
    /// Whether abstract types were included in the query
    /// </summary>
    public required bool IncludeAbstract { get; init; }
    
    /// <summary>
    /// Model ID filter that was applied, if any
    /// </summary>
    public string? ModelIdFilter { get; init; }
    
    /// <summary>
    /// List of type metadata matching the query criteria
    /// </summary>
    public required List<CkTypeMetadata> Types { get; init; }
}

/// <summary>
/// Response for type search
/// </summary>
public sealed class SearchTypesResponse
{
    /// <summary>
    /// The search term that was used
    /// </summary>
    public required string SearchTerm { get; init; }
    
    /// <summary>
    /// Number of types that matched the search criteria
    /// </summary>
    public required int MatchCount { get; init; }
    
    /// <summary>
    /// Whether abstract types were included in the search
    /// </summary>
    public required bool IncludeAbstract { get; init; }
    
    /// <summary>
    /// List of types that matched the search criteria
    /// </summary>
    public required List<CkTypeMetadata> Matches { get; init; }
}

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
    public object? Attributes { get; init; }
    
    /// <summary>
    /// Association definitions for relationships
    /// </summary>
    public object? Associations { get; init; }
    
    /// <summary>
    /// Schema details for entity creation and validation
    /// </summary>
    public required TypeSchemaDetails Schema { get; init; }
}

/// <summary>
/// Schema details for type creation and validation
/// </summary>
public sealed class TypeSchemaDetails
{
    /// <summary>
    /// Indicates if entities of this type can be created (not abstract)
    /// </summary>
    public required bool CanCreate { get; init; }
    
    /// <summary>
    /// Attributes that must be provided when creating entities
    /// </summary>
    public object? RequiredAttributes { get; init; }
    
    /// <summary>
    /// Attributes that are optional when creating entities
    /// </summary>
    public object? OptionalAttributes { get; init; }
}

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
