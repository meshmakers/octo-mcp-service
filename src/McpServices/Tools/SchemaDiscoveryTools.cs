using System.ComponentModel;
using System.Globalization;
using Meshmakers.Octo.Backend.McpServices.Models;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Services.Infrastructure;
using Meshmakers.Octo.Services.Infrastructure.Services;
using ModelContextProtocol.Server;

// ReSharper disable UnusedMember.Global

namespace Meshmakers.Octo.Backend.McpServices.Tools;

/// <summary>
/// Tools for discovering and exploring Construction Kit schemas
/// </summary>
[McpServerToolType]
// ReSharper disable once UnusedType.Global
public sealed class SchemaDiscoveryTools
{
    /// <summary>
    /// Get all models available in the system
    /// </summary>
    /// <param name="server">MCP Server instance</param>
    /// <returns>List of available Construction Kit models</returns>
    [McpServerTool(Name = "get_available_models")]
    [Description("Get all available Construction Kit models in the system")]
    public static async Task<object> GetAvailableModels(IMcpServer server)
    {
        try
        {
            var httpContextAccessor = server.Services!.GetRequiredService<IOctoHttpContextAccessor>();
            var ckCacheService = server.Services!.GetRequiredService<ICkCacheService>();

            var tenantRepository = await httpContextAccessor.GetTenantRepositoryAsync();
            await tenantRepository.LoadCacheForTenantAsync(ckCacheService);

            var modelIds = ckCacheService.GetCkModelIds(httpContextAccessor.GetTenantId());

            return new AvailableModelsResponse
            {
                TotalModels = modelIds.Count,
                Models = modelIds.OrderBy(m => m.ModelId).Select(m => m.ToString(CultureInfo.InvariantCulture)).ToList()
            };
        }
        catch (Exception ex)
        {
            return new ErrorResponse
            {
                Error = "Failed to get available models",
                Message = ex.Message
            };
        }
    }

    /// <summary>
    /// Get all available Construction Kit types in the system
    /// </summary>
    /// <param name="server">MCP Server instance</param>
    /// <param name="includeAbstract">Include abstract types in results</param>
    /// <param name="ckModelId">Filter by specific model ID (e.g., 'EnergyCommunity-1.0.0')</param>
    /// <returns>List of available CK types with basic metadata</returns>
    [McpServerTool(Name = "get_available_types")]
    [Description("Get all available Construction Kit types with their basic metadata")]
    // ReSharper disable once MemberCanBePrivate.Global
    public static async Task<object> GetAvailableTypes(
        IMcpServer server,
        bool includeAbstract = false,
        string? ckModelId = null)
    {
        try
        {
            var httpContextAccessor = server.Services!.GetRequiredService<IOctoHttpContextAccessor>();
            var ckCacheService = server.Services!.GetRequiredService<ICkCacheService>();

            var tenantRepository = await httpContextAccessor.GetTenantRepositoryAsync();
            await tenantRepository.LoadCacheForTenantAsync(ckCacheService);

            // Get all available type graphs from the cache
            var availableTypes = new List<CkTypeMetadata>();

            var typeGraphs = ckCacheService.GetCkTypes(httpContextAccessor.GetTenantId());

            foreach (var ckTypeGraph in typeGraphs)
            {
                if (!string.IsNullOrEmpty(ckModelId) &&
                    !ckTypeGraph.CkTypeId.ModelId.FullName.StartsWith(ckModelId))
                {
                    continue;
                }

                if (!includeAbstract && ckTypeGraph.IsAbstract)
                {
                    continue;
                }

                availableTypes.Add(new CkTypeMetadata
                {
                    CkTypeId = ckTypeGraph.CkTypeId.SemanticVersionedFullName,
                    ModelId = ckTypeGraph.CkTypeId.ModelId.ToString(CultureInfo.InvariantCulture),
                    TypeId = ckTypeGraph.CkTypeId.Key.SemanticVersionedFullName,
                    TypeName = ckTypeGraph.CkTypeId.Key.SemanticVersionedFullName, // Fix: Add TypeName
                    Version = ckTypeGraph.CkTypeId.Key.Version,
                    IsAbstract = ckTypeGraph.IsAbstract,
                    IsFinal = ckTypeGraph.IsFinal,
                    IsCollectionRoot = ckTypeGraph.IsCollectionRoot,
                    Description = ckTypeGraph.Description,
                    DerivedFrom = ckTypeGraph.DerivedFromCkTypeId?.ToString(),
                    Attributes =
                        ckCacheService.GetCkTypeQueryColumnPaths(tenantRepository.TenantId, ckTypeGraph.CkTypeId, true).Select(c =>
                            new AttributeSchemaResponse{ AttributePath = c.Path, ValueType = c.ValueType}),
                    InboundAssociations = ckTypeGraph.Associations.In.All.Select(a=> new AssociationSchemaResponse
                    {
                        AssociationRoleId = a.CkRoleId.FullName,
                        TargetCkTypeId = a.TargetCkTypeId.FullName,
                        Cardinality = (CkTypeAssociationCardinalityDto) a.Multiplicity,
                        Direction = CkTypeAssociationDirectionDto.Inbound
                    }),
                    OutboundAssociations = ckTypeGraph.Associations.Out.All.Select(a=> new AssociationSchemaResponse
                    {
                        AssociationRoleId = a.CkRoleId.FullName,
                        TargetCkTypeId = a.TargetCkTypeId.FullName,
                        Cardinality = (CkTypeAssociationCardinalityDto) a.Multiplicity,
                        Direction = CkTypeAssociationDirectionDto.Outbound
                    }),
                });
            }

            return new AvailableTypesResponse
            {
                TotalTypes = availableTypes.Count,
                IncludeAbstract = includeAbstract,
                ModelIdFilter = ckModelId,
                Types = availableTypes.OrderBy(t => t.TypeId).ToList()
            };
        }
        catch (Exception ex)
        {
            return new ErrorResponse
            {
                Error = "Failed to get available types",
                Message = ex.Message
            };
        }
    }

    /// <summary>
    /// Get detailed schema information for a specific Construction Kit type
    /// </summary>
    /// <param name="server">MCP Server instance</param>
    /// <param name="ckTypeId">Construction Kit Type ID</param>
    [McpServerTool(Name = "get_type_schema")]
    [Description("Get detailed schema information for a specific Construction Kit type")]
    public static async Task<object> GetTypeSchema(
        IMcpServer server,
        string ckTypeId)
    {
        try
        {
            var httpContextAccessor = server.Services!.GetRequiredService<IOctoHttpContextAccessor>();
            var ckCacheService = server.Services!.GetRequiredService<ICkCacheService>();

            var tenantRepository = await httpContextAccessor.GetTenantRepositoryAsync();
            await tenantRepository.LoadCacheForTenantAsync(ckCacheService);

            var typeGraph = ckCacheService.GetCkType(httpContextAccessor.GetTenantId(), new CkId<CkTypeId>(ckTypeId));

            return new TypeSchemaResponse
            {
                TypeId = typeGraph.CkTypeId.ToString(),
                ModelId = typeGraph.CkTypeId.ModelId.ToString(CultureInfo.InvariantCulture),
                TypeName = typeGraph.CkTypeId.Key.SemanticVersionedFullName,
                Version = typeGraph.CkTypeId.Key.Version.ToString(),
                IsAbstract = typeGraph.IsAbstract,
                IsFinal = typeGraph.IsFinal,
                IsCollectionRoot = typeGraph.IsCollectionRoot,
                IsStreamType = typeGraph.IsStreamType,
                Description = typeGraph.Description,
                DerivedFrom = typeGraph.DerivedFromCkTypeId?.ToString(),
                InheritanceHierarchy = typeGraph.GetBaseTypes(false),
                Indexes = typeGraph.Indexes,
                Attributes =
                    ckCacheService.GetCkTypeQueryColumnPaths(tenantRepository.TenantId, typeGraph.CkTypeId, true).Select(c =>
                        new AttributeSchemaResponse{ AttributePath = c.Path, ValueType = c.ValueType}),
                InboundAssociations = typeGraph.Associations.In.All.Select(a=> new AssociationSchemaResponse
                {
                    AssociationRoleId = a.CkRoleId.FullName,
                    TargetCkTypeId = a.TargetCkTypeId.FullName,
                    Cardinality = (CkTypeAssociationCardinalityDto) a.Multiplicity,
                    Direction = CkTypeAssociationDirectionDto.Inbound
                }),
                OutboundAssociations = typeGraph.Associations.Out.All.Select(a=> new AssociationSchemaResponse
                {
                    AssociationRoleId = a.CkRoleId.FullName,
                    TargetCkTypeId = a.TargetCkTypeId.FullName,
                    Cardinality = (CkTypeAssociationCardinalityDto) a.Multiplicity,
                    Direction = CkTypeAssociationDirectionDto.Outbound
                }),
                Schema = new TypeSchemaDetails
                {
                    CanCreate = !typeGraph.IsAbstract,
                    RequiredAttributes = typeGraph.AllAttributes.Where(a => !a.Value.IsOptional),
                    OptionalAttributes = typeGraph.AllAttributes.Where(a => a.Value.IsOptional)
                }
            };
        }
        catch (Exception ex)
        {
            return new ErrorResponse
            {
                Error = "Failed to get type schema",
                Message = ex.Message,
                CkTypeId = ckTypeId
            };
        }
    }

    /// <summary>
    /// Search for types by name or description
    /// </summary>
    /// <param name="server">MCP Server instance</param>
    /// <param name="searchTerm">Search term to look for in type names or descriptions</param>
    /// <param name="includeAbstract">Include abstract types in search results</param>
    /// <returns>Matching types</returns>
    [McpServerTool(Name = "search_types")]
    [Description("Search for Construction Kit types by name or description")]
    public static async Task<object> SearchTypes(
        IMcpServer server,
        string searchTerm,
        bool includeAbstract = false)
    {
        try
        {
            var allTypesResult = await GetAvailableTypes(server, includeAbstract);

            // Safe casting to concrete type
            if (allTypesResult is not AvailableTypesResponse typesResponse)
            {
                return new ErrorResponse
                {
                    Error = "Failed to get types for search",
                    Message = "Unable to retrieve type information from cache"
                };
            }

            var matchingTypes = typesResponse.Types.Where(t =>
                t.TypeId.Contains(searchTerm, StringComparison.OrdinalIgnoreCase) ||
                t.TypeName.Contains(searchTerm, StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrEmpty(t.Description) &&
                 t.Description.Contains(searchTerm, StringComparison.OrdinalIgnoreCase))
            ).ToList();

            return new SearchTypesResponse
            {
                SearchTerm = searchTerm,
                MatchCount = matchingTypes.Count,
                IncludeAbstract = includeAbstract,
                Matches = matchingTypes
            };
        }
        catch (Exception ex)
        {
            return new ErrorResponse
            {
                Error = "Failed to search types",
                Message = ex.Message,
                SearchTerm = searchTerm
            };
        }
    }
}