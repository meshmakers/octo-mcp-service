using System.ComponentModel;
using System.Globalization;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.ModelRepositories;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Services.Infrastructure;
using ModelContextProtocol.Server;

namespace Meshmakers.Octo.Backend.McpServices.Tools;

/// <summary>
/// Tools for discovering and exploring Construction Kit schemas
/// </summary>
[McpServerToolType]
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
            var httpContextAccessor = server.Services!.GetRequiredService<IHttpContextAccessor>();
            var ckCacheService = server.Services!.GetRequiredService<ICkCacheService>();

            var tenantRepository = await httpContextAccessor.GetTenantRepositoryAsync();
            await tenantRepository.LoadCacheForTenantAsync(ckCacheService);

            var modelIds = ckCacheService.GetCkModelIds(httpContextAccessor.GetTenantId());

            return new
            {
                totalModels = modelIds.Count,
                models = modelIds.OrderBy(m => m.ModelId)
            };
        }
        catch (Exception ex)
        {
            return new
            {
                error = "Failed to get available models",
                message = ex.Message
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
            var httpContextAccessor = server.Services!.GetRequiredService<IHttpContextAccessor>();
            var ckCacheService = server.Services!.GetRequiredService<ICkCacheService>();

            var tenantRepository = await httpContextAccessor.GetTenantRepositoryAsync();
            await tenantRepository.LoadCacheForTenantAsync(ckCacheService);

            // Get all available type graphs from the cache
            var availableTypes = new List<object>();

            var typeGraphs = ckCacheService.GetCkTypes(httpContextAccessor.GetTenantId());

            foreach (var ckTypeGraph in typeGraphs)
            {
                if (!string.IsNullOrEmpty(ckModelId) &&
                    !ckTypeGraph.CkTypeId.ModelId.SemanticVersionedFullName.StartsWith(ckModelId))
                {
                    continue;
                }

                if (!includeAbstract && ckTypeGraph.IsAbstract)
                {
                    continue;
                }

                availableTypes.Add(new
                {
                    ckTypeId = ckTypeGraph.CkTypeId.SemanticVersionedFullName,
                    modelId = ckTypeGraph.CkTypeId.ModelId.ToString(CultureInfo.InvariantCulture),
                    typeId = ckTypeGraph.CkTypeId.Key.SemanticVersionedFullName,
                    version = ckTypeGraph.CkTypeId.Key.Version,
                    isAbstract = ckTypeGraph.IsAbstract,
                    isFinal = ckTypeGraph.IsFinal,
                    isCollectionRoot = ckTypeGraph.IsCollectionRoot,
                    description = ckTypeGraph.Description,
                    derivedFrom = ckTypeGraph.DerivedFromCkTypeId?.ToString(),
                    attributeCount = ckTypeGraph.AllAttributes.Count,
                    inAssociationCount = ckTypeGraph.Associations.In.All.Count,
                    outAssociationCount = ckTypeGraph.Associations.Out.All.Count,
                });
            }

            return new
            {
                totalTypes = availableTypes.Count,
                includeAbstract,
                modelIdFilter = ckModelId,
                types = availableTypes.OrderBy(t => ((dynamic)t).typeId)
            };
        }
        catch (Exception ex)
        {
            return new
            {
                error = "Failed to get available types",
                message = ex.Message
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
            var httpContextAccessor = server.Services!.GetRequiredService<IHttpContextAccessor>();
            var ckCacheService = server.Services!.GetRequiredService<ICkCacheService>();

            var tenantRepository = await httpContextAccessor.GetTenantRepositoryAsync();
            await tenantRepository.LoadCacheForTenantAsync(ckCacheService);

            var typeGraph = ckCacheService.GetCkType(httpContextAccessor.GetTenantId(), new CkId<CkTypeId>(ckTypeId));

            return new
            {
                typeId = typeGraph.CkTypeId.ToString(),
                modelId = typeGraph.CkTypeId.ModelId.ToString(CultureInfo.InvariantCulture),
                typeName = typeGraph.CkTypeId.Key.SemanticVersionedFullName,
                version = typeGraph.CkTypeId.Key.Version.ToString(),
                isAbstract = typeGraph.IsAbstract,
                isFinal = typeGraph.IsFinal,
                isCollectionRoot = typeGraph.IsCollectionRoot,
                isStreamType = typeGraph.IsStreamType,
                description = typeGraph.Description,
                derivedFrom = typeGraph.DerivedFromCkTypeId?.ToString(),
                inheritanceHierarchy = typeGraph.GetBaseTypes(false),
                indexes = typeGraph.Indexes,
                attributes = typeGraph.AllAttributes.Values,
                associations = typeGraph.Associations,
                schema = new
                {
                    canCreate = !typeGraph.IsAbstract,
                    requiredAttributes = typeGraph.AllAttributes.Where(a => !a.Value.IsOptional),
                    optionalAttributes = typeGraph.AllAttributes.Where(a => a.Value.IsOptional)
                }
            };
        }
        catch (Exception ex)
        {
            return new
            {
                error = "Failed to get type schema",
                message = ex.Message,
                ckTypeId
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
            var allTypes = ((dynamic)allTypesResult).types as IEnumerable<dynamic>;

            if (allTypes == null)
            {
                return new { error = "Failed to get types for search" };
            }

            var matchingTypes = allTypes.Where(t =>
                ((string)t.typeId).Contains(searchTerm, StringComparison.OrdinalIgnoreCase) ||
                ((string)t.typeName).Contains(searchTerm, StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrEmpty((string)t.description) &&
                 ((string)t.description).Contains(searchTerm, StringComparison.OrdinalIgnoreCase))
            ).ToList();

            return new
            {
                searchTerm,
                matchCount = matchingTypes.Count,
                includeAbstract,
                matches = matchingTypes
            };
        }
        catch (Exception ex)
        {
            return new
            {
                error = "Failed to search types",
                message = ex.Message,
                searchTerm
            };
        }
    }
}