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

    #endregion
}