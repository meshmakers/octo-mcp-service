using System.ComponentModel;
using System.Globalization;
using Meshmakers.Common.Shared;
using Meshmakers.Octo.Backend.McpServices.Models;
using Meshmakers.Octo.Backend.McpServices.Services;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Services.Infrastructure.Services;
using ModelContextProtocol.Server;

// ReSharper disable UnusedMember.Global

namespace Meshmakers.Octo.Backend.McpServices.Tools;

/// <summary>
///     Tools for discovering and exploring Construction Kit schemas
/// </summary>
[McpServerToolType]
// ReSharper disable once UnusedType.Global
public sealed class SchemaDiscoveryTools
{
    /// <summary>
    ///     Get all models available in the system
    /// </summary>
    /// <param name="server">MCP Server instance</param>
    /// <param name="tenantId">Optional tenant ID. If not specified, the tenant is resolved from the URL route.</param>
    /// <returns>List of available Construction Kit models</returns>
    [McpServerTool(Name = "get_available_models")]
    [Description("Get all available Construction Kit models in the system")]
    public static async Task<AvailableModelsResponse> GetAvailableModels(
        McpServer server,
        string? tenantId = null)
    {
        try
        {
            var tenantResolution = server.Services!.GetRequiredService<ITenantResolutionService>();
            var ckCacheService = server.Services!.GetRequiredService<ICkCacheService>();

            var tenantRepository = await tenantResolution.GetTenantRepositoryAsync(tenantId);
            await tenantRepository.LoadCacheForTenantAsync(ckCacheService);

            var modelIds = ckCacheService.GetCkModelIds(tenantRepository.TenantId);

            return new AvailableModelsResponse
            {
                IsSuccess = true,
                TotalModels = modelIds.Count,
                Models = modelIds.OrderBy(m => m.Name).Select(m => m.ToString(CultureInfo.InvariantCulture)).ToList()
            };
        }
        catch (Exception ex)
        {
            return new AvailableModelsResponse
            {
                IsSuccess = false,
                ErrorMessage = ex.Message
            };
        }
    }

    /// <summary>
    ///     Get all available Construction Kit types in the system
    /// </summary>
    /// <param name="server">MCP Server instance</param>
    /// <param name="includeAbstract">Include abstract types in results</param>
    /// <param name="ckModelId">Filter by specific model ID (e.g., 'EnergyCommunity-1.0.0')</param>
    /// <param name="tenantId">Optional tenant ID. If not specified, the tenant is resolved from the URL route.</param>
    /// <returns>List of available CK types with basic metadata</returns>
    [McpServerTool(Name = "get_available_types")]
    [Description("Get all available Construction Kit types with their basic metadata")]
    // ReSharper disable once MemberCanBePrivate.Global
    public static async Task<AvailableTypesResponse> GetAvailableTypes(
        McpServer server,
        bool includeAbstract = false,
        string? ckModelId = null,
        string? tenantId = null)
    {
        try
        {
            var tenantResolution = server.Services!.GetRequiredService<ITenantResolutionService>();
            var ckCacheService = server.Services!.GetRequiredService<ICkCacheService>();

            var tenantRepository = await tenantResolution.GetTenantRepositoryAsync(tenantId);
            await tenantRepository.LoadCacheForTenantAsync(ckCacheService);

            // Get all available type graphs from the cache
            var availableTypes = new List<CkTypeInfo>();

            var typeGraphs = ckCacheService.GetCkTypes(tenantRepository.TenantId);

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

                availableTypes.Add(new CkTypeInfo
                {
                    CkTypeId = ckTypeGraph.CkTypeId.SemanticVersionedFullName,
                    ModelId = ckTypeGraph.CkTypeId.ModelId.ToString(CultureInfo.InvariantCulture),
                    TypeId = ckTypeGraph.CkTypeId.ElementId.SemanticVersionedFullName,
                    TypeName = ckTypeGraph.CkTypeId.ElementId.SemanticVersionedFullName,
                    Version = ckTypeGraph.CkTypeId.ElementId.Version,
                    IsAbstract = ckTypeGraph.IsAbstract,
                    IsFinal = ckTypeGraph.IsFinal,
                    IsCollectionRoot = ckTypeGraph.IsCollectionRoot,
                    Description = ckTypeGraph.Description,
                    DerivedFrom = ckTypeGraph.DerivedFromCkTypeId?.ToString(),
                });
            }

            return new AvailableTypesResponse
            {
                IsSuccess = true,
                TotalTypes = availableTypes.Count,
                IncludeAbstract = includeAbstract,
                ModelIdFilter = ckModelId,
                Types = availableTypes.OrderBy(t => t.TypeId).ToList()
            };
        }
        catch (Exception ex)
        {
            return new AvailableTypesResponse
            {
                IsSuccess = false,
                ErrorMessage = ex.Message
            };
        }
    }

    /// <summary>
    ///     Get detailed schema information for a specific Construction Kit type
    /// </summary>
    /// <param name="server">MCP Server instance</param>
    /// <param name="ckTypeId">Construction Kit Type ID</param>
    /// <param name="tenantId">Optional tenant ID. If not specified, the tenant is resolved from the URL route.</param>
    [McpServerTool(Name = "get_type_schema")]
    [Description("Get detailed schema information for a specific Construction Kit type")]
    public static async Task<TypeSchemaResponse> GetTypeSchema(
        McpServer server,
        string ckTypeId,
        string? tenantId = null)
    {
        try
        {
            var tenantResolution = server.Services!.GetRequiredService<ITenantResolutionService>();
            var ckCacheService = server.Services!.GetRequiredService<ICkCacheService>();

            var tenantRepository = await tenantResolution.GetTenantRepositoryAsync(tenantId);
            await tenantRepository.LoadCacheForTenantAsync(ckCacheService);

            var typeGraph = ckCacheService.GetCkType(tenantRepository.TenantId, new CkId<CkTypeId>(ckTypeId));

            return new TypeSchemaResponse
            {
                IsSuccess = true,
                CkTypeId = typeGraph.CkTypeId.ToString(),
                ModelId = typeGraph.CkTypeId.ModelId.ToString(CultureInfo.InvariantCulture),
                TypeName = typeGraph.CkTypeId.ElementId.SemanticVersionedFullName,
                Version = typeGraph.CkTypeId.ElementId.Version.ToString(),
                IsAbstract = typeGraph.IsAbstract,
                IsFinal = typeGraph.IsFinal,
                IsCollectionRoot = typeGraph.IsCollectionRoot,
                Description = typeGraph.Description,
                DerivedFrom = typeGraph.DerivedFromCkTypeId?.ToString(),
                InheritanceHierarchy = typeGraph.GetBaseTypes(false),
                Indexes = typeGraph.Indexes,
                Attributes =
                    ckCacheService.GetCkTypeQueryColumnPaths(tenantRepository.TenantId, typeGraph.CkTypeId,
                            new CkTypeQueryColumnOptions { IgnoreNavigationProperties = true })
                        .Select(c =>
                            new AttributeSchemaResponse { AttributePath = c.Path, ValueType = c.ValueType, Description = c.Description }),
                InboundAssociations = typeGraph.Associations.In.All.Select(a => new AssociationSchemaResponse
                {
                    AssociationRoleId = a.CkRoleId.FullName,
                    TargetCkTypeId = a.TargetCkTypeId.FullName,
                    Cardinality = (CkTypeAssociationCardinalityDto)a.Multiplicity,
                    Direction = CkTypeAssociationDirectionDto.Inbound
                }),
                OutboundAssociations = typeGraph.Associations.Out.All.Select(a => new AssociationSchemaResponse
                {
                    AssociationRoleId = a.CkRoleId.FullName,
                    TargetCkTypeId = a.TargetCkTypeId.FullName,
                    Cardinality = (CkTypeAssociationCardinalityDto)a.Multiplicity,
                    Direction = CkTypeAssociationDirectionDto.Outbound
                }),
                Schema = new TypeSchemaDetails
                {
                    CanCreate = !typeGraph.IsAbstract,
                    RequiredAttributes = typeGraph.AllAttributes.Where(a => !a.Value.IsOptional).Select(a =>
                        new AttributeSchemaResponse
                            { AttributePath = a.Value.AttributeName.ToCamelCase(), ValueType = a.Value.ValueType }),
                    OptionalAttributes = typeGraph.AllAttributes.Where(a => a.Value.IsOptional).Select(a =>
                        new AttributeSchemaResponse
                            { AttributePath = a.Value.AttributeName.ToCamelCase(), ValueType = a.Value.ValueType })
                }
            };
        }
        catch (Exception ex)
        {
            return new TypeSchemaResponse
            {
                IsSuccess = false,
                ErrorMessage = ex.Message,
                CkTypeId = ckTypeId
            };
        }
    }

    /// <summary>
    ///     Search for types by name or description
    /// </summary>
    /// <param name="server">MCP Server instance</param>
    /// <param name="searchTerm">Search term to look for in type names or descriptions</param>
    /// <param name="includeAbstract">Include abstract types in search results</param>
    /// <param name="tenantId">Optional tenant ID. If not specified, the tenant is resolved from the URL route.</param>
    /// <returns>Matching types</returns>
    [McpServerTool(Name = "search_types")]
    [Description("Search for Construction Kit types by name or description")]
    public static async Task<SearchTypesResponse> SearchTypes(
        McpServer server,
        string searchTerm,
        bool includeAbstract = false,
        string? tenantId = null)
    {
        try
        {
            var allTypesResult = await GetAvailableTypes(server, includeAbstract, tenantId: tenantId);

            // Safe casting to concrete type
            if (!allTypesResult.IsSuccess)
            {
                return new SearchTypesResponse
                {
                    IsSuccess = false,
                    ErrorMessage = allTypesResult.ErrorMessage,
                    SearchTerm = searchTerm,
                    IncludeAbstract = includeAbstract,
                };
            }

            if (allTypesResult.Types == null || !allTypesResult.Types.Any())
            {
                return new SearchTypesResponse
                {
                    IsSuccess = true,
                    SearchTerm = searchTerm,
                    MatchCount = 0,
                    IncludeAbstract = includeAbstract,
                    Matches = new List<CkTypeInfo>()
                };
            }

            var matchingTypes = allTypesResult.Types.Where(t =>
                t.TypeId.Contains(searchTerm, StringComparison.OrdinalIgnoreCase) ||
                t.TypeName.Contains(searchTerm, StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrEmpty(t.Description) &&
                 t.Description.Contains(searchTerm, StringComparison.OrdinalIgnoreCase))
            ).ToList();

            return new SearchTypesResponse
            {
                IsSuccess = true,
                SearchTerm = searchTerm,
                MatchCount = matchingTypes.Count,
                IncludeAbstract = includeAbstract,
                Matches = matchingTypes
            };
        }
        catch (Exception ex)
        {
            return new SearchTypesResponse
            {
                IsSuccess = false,
                ErrorMessage = ex.Message,
                SearchTerm = searchTerm,
                IncludeAbstract = includeAbstract
            };
        }
    }
}
