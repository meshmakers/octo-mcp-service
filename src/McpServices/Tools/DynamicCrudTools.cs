using System.ComponentModel;
using System.Text.Json;
using Meshmakers.Octo.Backend.McpServices.Models;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Services.Infrastructure;
using ModelContextProtocol.Server;

namespace Meshmakers.Octo.Backend.McpServices.Tools;

/// <summary>
/// Dynamic CRUD operations for all Construction Kit types
/// </summary>
[McpServerToolType]
public sealed class DynamicCrudTools
{
    /// <summary>
    /// Query entities of any CK type with optional filters
    /// </summary>
    /// <param name="server">MCP Server instance</param>
    /// <param name="ckTypeId">Construction Kit Type ID (e.g., 'EnergyCommunity-1.0.0/Customer-1.0.0')</param>
    /// <param name="filters">Optional filters as JSON object</param>
    /// <param name="limit">Maximum number of results to return</param>
    /// <param name="offset">Number of results to skip</param>
    /// <returns>Query results with entity data</returns>
    [McpServerTool(Name = "query_entities")]
    [Description("Query entities of any Construction Kit type with optional filters, sorting and pagination")]
    public static async Task<object> QueryEntities(
        IMcpServer server,
        string ckTypeId,
        string? filters = null,
        int? limit = null,
        int? offset = null)
    {
        var httpContextAccessor = server.Services!.GetRequiredService<IHttpContextAccessor>();
        var ckCacheService = server.Services!.GetRequiredService<ICkCacheService>();
        var tenantRepository = await httpContextAccessor.GetTenantRepositoryAsync();
        var tenantId = httpContextAccessor.GetTenantId();

        using var session = await tenantRepository.GetSessionAsync();

        try
        {
            await tenantRepository.GetCkTypeGraphAsync(new CkId<CkTypeId>(ckTypeId));

            // Build query operation
            var queryOperation = DataQueryOperation.Create();

            // Parse filters if provided
            if (!string.IsNullOrEmpty(filters))
            {
                var filterDict = JsonSerializer.Deserialize<Dictionary<string, object>>(filters);
                BuildFieldFilters(filterDict, queryOperation);
            }

            var results = await tenantRepository.GetRtEntitiesByTypeAsync(
                session,
                new CkId<CkTypeId>(ckTypeId),
                queryOperation,
                offset,
                limit);

            return new QueryEntitiesResponse
            {
                CkTypeId = ckTypeId,
                TotalCount = results.TotalCount,
                ReturnedCount = results.Items.Count(),
                Entities = EntityMapper.MapToDto(results.Items, ckCacheService, tenantId)
            };
        }
        catch (Exception ex)
        {
            return new EntityOperationError
            {
                Error = "Failed to query entities",
                Message = ex.Message,
                CkTypeId = ckTypeId
            };
        }
    }

    /// <summary>
    /// Get a single entity by its runtime ID
    /// </summary>
    /// <param name="server">MCP Server instance</param>
    /// <param name="ckTypeId">Construction Kit Type ID</param>
    /// <param name="rtId">Runtime entity ID</param>
    /// <returns>Entity data or null if not found</returns>
    [McpServerTool(Name = "get_entity_by_id")]
    [Description("Get a single entity by its runtime ID")]
    public static async Task<object> GetEntityById(
        IMcpServer server,
        string ckTypeId,
        string rtId)
    {
        var httpContextAccessor = server.Services!.GetRequiredService<IHttpContextAccessor>();
        var tenantRepository = await httpContextAccessor.GetTenantRepositoryAsync();

        using var session = await tenantRepository.GetSessionAsync();

        try
        {
            var rtEntityId = new RtEntityId(new CkId<CkTypeId>(ckTypeId),
                new OctoObjectId(rtId));

            var entity = await tenantRepository.GetRtEntityByRtIdAsync(session, rtEntityId);

            if (entity == null)
            {
                return new EntityNotFoundResponse { 
                    Error = "Entity not found",
                    RtId = rtId, 
                    CkTypeId = ckTypeId 
                };
            }

            return new GetEntityResponse
            {
                TypeId = ckTypeId,
                Entity = entity
            };
        }
        catch (Exception ex)
        {
            return new EntityOperationError
            {
                Error = "Failed to get entity",
                Message = ex.Message,
                RtId = rtId,
                CkTypeId = ckTypeId
            };
        }
    }

    /// <summary>
    /// Create a new entity of the specified CK type
    /// </summary>
    /// <param name="server">MCP Server instance</param>
    /// <param name="ckTypeId">Construction Kit Type ID</param>
    /// <param name="entityData">Entity data as JSON object</param>
    /// <returns>Created entity with runtime ID</returns>
    [McpServerTool(Name = "create_entity")]
    [Description("Create a new entity of specified Construction Kit type")]
    public static async Task<object> CreateEntity(
        IMcpServer server,
        string ckTypeId,
        string entityData)
    {
        var httpContextAccessor = server.Services!.GetRequiredService<IHttpContextAccessor>();
        var ckCacheService = server.Services!.GetRequiredService<ICkCacheService>();
        var tenantRepository = await httpContextAccessor.GetTenantRepositoryAsync();

        using var session = await tenantRepository.GetSessionAsync();

        try
        {
            var dataDict = JsonSerializer.Deserialize<Dictionary<string, object>>(entityData);

            // Create transient entity
            var entity = await tenantRepository.CreateTransientRtEntityAsync(new CkId<CkTypeId>(ckTypeId));

            // Populate entity with provided data
            PopulateEntity(ckCacheService, httpContextAccessor.GetTenantId(), entity, dataDict!);

            // Insert entity
            await tenantRepository.InsertOneRtEntityAsync(session, new CkId<CkTypeId>(ckTypeId), entity);
            await session.CommitTransactionAsync();

            return new CreateEntityResponse
            {
                Success = true,
                CkTypeId = ckTypeId,
                RtId = entity.RtId.ToString(),
                Entity = entity
            };
        }
        catch (Exception ex)
        {
            await session.AbortTransactionAsync();
            return new EntityOperationError
            {
                Error = "Failed to create entity",
                Message = ex.Message,
                CkTypeId = ckTypeId
            };
        }
    }

    /// <summary>
    /// Update an existing entity
    /// </summary>
    /// <param name="server">MCP Server instance</param>
    /// <param name="ckTypeId">Construction Kit Type ID</param>
    /// <param name="rtId">Runtime entity ID</param>
    /// <param name="entityData">Updated entity data as JSON object</param>
    /// <returns>Updated entity</returns>
    [McpServerTool(Name = "update_entity")]
    [Description("Update an existing entity with new data")]
    public static async Task<object> UpdateEntity(
        IMcpServer server,
        string ckTypeId,
        string rtId,
        string entityData)
    {
        var httpContextAccessor = server.Services!.GetRequiredService<IHttpContextAccessor>();
        var ckCacheService = server.Services!.GetRequiredService<ICkCacheService>();
        var tenantRepository = await httpContextAccessor.GetTenantRepositoryAsync();

        using var session = await tenantRepository.GetSessionAsync();

        try
        {
            var dataDict = JsonSerializer.Deserialize<Dictionary<string, object>>(entityData);

            // Get existing entity
            var existingEntity = await tenantRepository.GetRtEntityByRtIdAsync(session, new RtEntityId(rtId));
            if (existingEntity == null)
            {
                return new EntityNotFoundResponse { 
                    Error = "Entity not found",
                    RtId = rtId, 
                    CkTypeId = ckTypeId 
                };
            }

            // Create update entity with only changed fields
            var updateEntity = await tenantRepository.CreateTransientRtEntityAsync(new CkId<CkTypeId>(ckTypeId));
            updateEntity.RtId = new OctoObjectId(rtId);

            // Populate with update data
            PopulateEntity(ckCacheService, httpContextAccessor.GetTenantId(), updateEntity, dataDict!);

            // Update entity
            await tenantRepository.UpdateOneRtEntityByIdAsync(session, new CkId<CkTypeId>(ckTypeId),
                new OctoObjectId(rtId), updateEntity);
            await session.CommitTransactionAsync();

            // Get updated entity
            var updatedEntity = await tenantRepository.GetRtEntityByRtIdAsync(session, new RtEntityId(rtId));

            return new UpdateEntityResponse
            {
                Success = true,
                TypeId = ckTypeId,
                RtId = rtId,
                Entity = updatedEntity
            };
        }
        catch (Exception ex)
        {
            await session.AbortTransactionAsync();
            return new EntityOperationError
            {
                Error = "Failed to update entity",
                Message = ex.Message,
                RtId = rtId,
                CkTypeId = ckTypeId
            };
        }
    }

    /// <summary>
    /// Delete an entity by its runtime ID
    /// </summary>
    /// <param name="server">MCP Server instance</param>
    /// <param name="ckTypeId">Construction Kit Type ID</param>
    /// <param name="rtId">Runtime entity ID</param>
    /// <returns>Deletion result</returns>
    [McpServerTool(Name = "delete_entity")]
    [Description("Delete an entity by its runtime ID")]
    public static async Task<object> DeleteEntity(
        IMcpServer server,
        string ckTypeId,
        string rtId)
    {
        var httpContextAccessor = server.Services!.GetRequiredService<IHttpContextAccessor>();
        var tenantRepository = await httpContextAccessor.GetTenantRepositoryAsync();

        using var session = await tenantRepository.GetSessionAsync();

        try
        {
            // Check if entity exists
            var existingEntity = await tenantRepository.GetRtEntityByRtIdAsync(session, new RtEntityId(rtId));
            if (existingEntity == null)
            {
                return new EntityNotFoundResponse { 
                    Error = "Entity not found",
                    RtId = rtId, 
                    CkTypeId = ckTypeId 
                };
            }

            // Delete entity
            await tenantRepository.DeleteOneRtEntityByRtIdAsync(session, new CkId<CkTypeId>(ckTypeId),
                new OctoObjectId(rtId));
            await session.CommitTransactionAsync();

            return new DeleteEntityResponse
            {
                Success = true,
                Message = "Entity deleted successfully",
                CkTypeId = ckTypeId,
                RtId = rtId
            };
        }
        catch (Exception ex)
        {
            await session.AbortTransactionAsync();
            return new EntityOperationError
            {
                Error = "Failed to delete entity",
                Message = ex.Message,
                EntityId = rtId,
                CkTypeId = ckTypeId
            };
        }
    }

    /// <summary>
    /// Navigate associations from a source entity to find related entities
    /// </summary>
    /// <param name="server">MCP Server instance</param>
    /// <param name="ckTypeId">Source Construction Kit Type ID</param>
    /// <param name="rtId">Source Runtime entity ID</param>
    /// <param name="associationPath">Dot-separated path of associations to follow (e.g., "Facilities.Children")</param>
    /// <param name="targetTypeId">Optional target type ID to filter results</param>
    /// <param name="filters">Optional filters for the target entities as JSON object</param>
    /// <returns>Related entities following the association path</returns>
    [McpServerTool(Name = "navigate_associations")]
    [Description("Navigate associations from a source entity to find related entities")]
    public static async Task<object> NavigateAssociations(
        IMcpServer server,
        string ckTypeId,
        string rtId,
        string associationPath,
        string? targetTypeId = null,
        string? filters = null)
    {
        var httpContextAccessor = server.Services!.GetRequiredService<IHttpContextAccessor>();
        var ckCacheService = server.Services!.GetRequiredService<ICkCacheService>();
        var tenantRepository = await httpContextAccessor.GetTenantRepositoryAsync();
        var tenantId = httpContextAccessor.GetTenantId();

        using var session = await tenantRepository.GetSessionAsync();

        try
        {
            // Get the source entity
            var sourceEntityId = new RtEntityId(new CkId<CkTypeId>(ckTypeId), new OctoObjectId(rtId));
            var sourceEntity = await tenantRepository.GetRtEntityByRtIdAsync(session, sourceEntityId);

            if (sourceEntity == null)
            {
                return new EntityNotFoundResponse
                {
                    Error = "Source entity not found",
                    RtId = rtId,
                    CkTypeId = ckTypeId
                };
            }

            // Navigate the association path
            var currentEntities = new List<(RtEntity entity, CkId<CkTypeId> typeId)> 
                { (sourceEntity, new CkId<CkTypeId>(ckTypeId)) };
            var pathSteps = associationPath.Split('.');

            foreach (var step in pathSteps)
            {
                var nextEntities = new List<(RtEntity entity, CkId<CkTypeId> typeId)>();

                foreach (var (entity, entityTypeId) in currentEntities)
                {
                    // Get association navigation property
                    var typeGraph = ckCacheService.GetCkType(tenantId, entityTypeId);
                    var outAssociation = typeGraph.Associations.Out.All.FirstOrDefault(a => a.NavigationPropertyName == step);
                    var inAssociation = typeGraph.Associations.In.All.FirstOrDefault(a => a.NavigationPropertyName == step);

                    if (outAssociation == null && inAssociation == null)
                    {
                        return new EntityOperationError
                        {
                            Error = "Association not found",
                            Message = $"Association '{step}' not found on type '{entityTypeId}'",
                            CkTypeId = ckTypeId,
                            RtId = rtId
                        };
                    }

                    // Handle outgoing association
                    if (outAssociation != null)
                    {
                        var queryOperation = DataQueryOperation.Create();
                        var associatedResults = await tenantRepository.GetRtAssociationTargetsAsync(
                            session, 
                            entity.RtId, 
                            entityTypeId,
                            outAssociation.CkRoleId, 
                            outAssociation.TargetCkTypeId, 
                            GraphDirections.Outbound,
                            null, 
                            queryOperation);

                        foreach (var associatedEntity in associatedResults.Items)
                        {
                            nextEntities.Add((associatedEntity, outAssociation.TargetCkTypeId));
                        }
                    }

                    // Handle incoming association
                    if (inAssociation != null)
                    {
                        var queryOperation = DataQueryOperation.Create();
                        var associatedResults = await tenantRepository.GetRtAssociationTargetsAsync(
                            session, 
                            entity.RtId, 
                            entityTypeId,
                            inAssociation.CkRoleId, 
                            inAssociation.OriginCkTypeId, 
                            GraphDirections.Inbound,
                            null, 
                            queryOperation);

                        foreach (var associatedEntity in associatedResults.Items)
                        {
                            nextEntities.Add((associatedEntity, inAssociation.OriginCkTypeId));
                        }
                    }
                }

                currentEntities = nextEntities;
            }

            var resultEntities = currentEntities.Select(ce => ce.entity).ToList();

            // Filter by target type if specified
            if (!string.IsNullOrEmpty(targetTypeId))
            {
                var targetCkTypeId = new CkId<CkTypeId>(targetTypeId);
                resultEntities = resultEntities.Where(e => e.CkTypeId != null && e.CkTypeId.Equals(targetCkTypeId)).ToList();
            }

            // Apply additional filters if provided
            if (!string.IsNullOrEmpty(filters) && resultEntities.Any())
            {
                var filterDict = JsonSerializer.Deserialize<Dictionary<string, object>>(filters);
                resultEntities = ApplyEntityFilters(resultEntities, filterDict, ckCacheService, tenantId);
            }

            return new NavigateAssociationsResponse
            {
                SourceCkTypeId = ckTypeId,
                SourceRtId = rtId,
                AssociationPath = associationPath,
                TargetTypeId = targetTypeId,
                ResultCount = resultEntities.Count,
                Entities = EntityMapper.MapToDto(resultEntities, ckCacheService, tenantId)
            };
        }
        catch (Exception ex)
        {
            return new EntityOperationError
            {
                Error = "Failed to navigate associations",
                Message = ex.Message,
                CkTypeId = ckTypeId,
                RtId = rtId
            };
        }
    }

    #region Helper Methods

    private static void BuildFieldFilters(Dictionary<string, object>? filterDict,
        DataQueryOperation dataQueryOperation)
    {
        if (filterDict == null)
        {
            return;
        }

        foreach (var kvp in filterDict)
        {
            // Simple equality filter for now
            dataQueryOperation.FieldEquals(kvp.Key, kvp.Value);
        }
    }

    private static void PopulateEntity(ICkCacheService ckCacheService, string tenantId, RtEntity entity, Dictionary<string, object> dataDict)
    {
        foreach (var kvp in dataDict)
        {
            if (kvp.Key.StartsWith("_"))
            {
                continue; // Skip system fields
            }

            entity.SetAttributeValueByAccessPath(ckCacheService, tenantId, kvp.Key, kvp.Value);
        }
    }

    private static List<RtEntity> ApplyEntityFilters(List<RtEntity> entities, Dictionary<string, object>? filterDict, ICkCacheService ckCacheService, string tenantId)
    {
        if (filterDict == null || !filterDict.Any())
        {
            return entities;
        }

        return entities.Where(entity =>
        {
            foreach (var filter in filterDict)
            {
                var attributeValue = entity.GetAttributeValueByAccessPath(ckCacheService, tenantId, filter.Key);
                if (attributeValue == null || !attributeValue.Equals(filter.Value))
                {
                    return false;
                }
            }
            return true;
        }).ToList();
    }

    #endregion
}