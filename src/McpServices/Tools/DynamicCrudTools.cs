using System.ComponentModel;
using System.Text.Json;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
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
        var tenantRepository = await httpContextAccessor.GetTenantRepositoryAsync();

        using var session = await tenantRepository.GetSessionAsync();

        try
        {
            var typeGraph = await tenantRepository.GetCkTypeGraphAsync(new CkId<CkTypeId>(ckTypeId));
            
            // Build query operation
            var queryOperation = new DataQueryOperation();
            
            // Parse filters if provided
            if (!string.IsNullOrEmpty(filters))
            {
                var filterDict = JsonSerializer.Deserialize<Dictionary<string, object>>(filters);
                queryOperation.FieldFilters = BuildFieldFilters(filterDict, typeGraph);
            }

            var results = await tenantRepository.GetRtEntitiesByTypeAsync(
                session, 
                new CkId<CkTypeId>(ckTypeId), 
                queryOperation, 
                offset, 
                limit);

            return new
            {
                typeId = ckTypeId,
                totalCount = results.TotalCount,
                returnedCount = results.Items.Count,
                entities = results.Items.Select(entity => FormatEntity(entity, typeGraph))
            };
        }
        catch (Exception ex)
        {
            return new
            {
                error = "Failed to query entities",
                message = ex.Message,
                typeId = ckTypeId
            };
        }
    }

    /// <summary>
    /// Get a single entity by its runtime ID
    /// </summary>
    /// <param name="server">MCP Server instance</param>
    /// <param name="ckTypeId">Construction Kit Type ID</param>
    /// <param name="entityId">Runtime entity ID</param>
    /// <returns>Entity data or null if not found</returns>
    [McpServerTool(Name = "get_entity_by_id")]
    [Description("Get a single entity by its runtime ID")]
    public static async Task<object> GetEntityById(
        IMcpServer server,
        string ckTypeId,
        string entityId)
    {
        var httpContextAccessor = server.Services!.GetRequiredService<IHttpContextAccessor>();
        var tenantRepository = await httpContextAccessor.GetTenantRepositoryAsync();

        using var session = await tenantRepository.GetSessionAsync();

        try
        {
            var typeGraph = await tenantRepository.GetCkTypeGraphAsync(new CkId<CkTypeId>(ckTypeId));
            var rtEntityId = new RtEntityId(new OctoObjectId(entityId));
            
            var entity = await tenantRepository.GetRtEntityByRtIdAsync(session, rtEntityId);
            
            if (entity == null)
            {
                return new { error = "Entity not found", entityId, ckTypeId };
            }

            return new
            {
                typeId = ckTypeId,
                entity = FormatEntity(entity, typeGraph)
            };
        }
        catch (Exception ex)
        {
            return new
            {
                error = "Failed to get entity",
                message = ex.Message,
                entityId,
                ckTypeId
            };
        }
    }

    /// <summary>
    /// Create a new entity of specified CK type
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
        var tenantRepository = await httpContextAccessor.GetTenantRepositoryAsync();

        using var session = await tenantRepository.GetSessionAsync();

        try
        {
            var typeGraph = await tenantRepository.GetCkTypeGraphAsync(new CkId<CkTypeId>(ckTypeId));
            var dataDict = JsonSerializer.Deserialize<Dictionary<string, object>>(entityData);
            
            // Create transient entity
            var entity = await tenantRepository.CreateTransientRtEntityAsync(new CkId<CkTypeId>(ckTypeId));
            
            // Populate entity with provided data
            PopulateEntity(entity, dataDict!, typeGraph);
            
            // Insert entity
            await tenantRepository.InsertOneRtEntityAsync(session, new CkId<CkTypeId>(ckTypeId), entity);
            await session.CommitAsync();

            return new
            {
                success = true,
                typeId = ckTypeId,
                entityId = entity.RtId.ToString(),
                entity = FormatEntity(entity, typeGraph)
            };
        }
        catch (Exception ex)
        {
            await session.RollbackAsync();
            return new
            {
                error = "Failed to create entity",
                message = ex.Message,
                ckTypeId
            };
        }
    }

    /// <summary>
    /// Update an existing entity
    /// </summary>
    /// <param name="server">MCP Server instance</param>
    /// <param name="ckTypeId">Construction Kit Type ID</param>
    /// <param name="entityId">Runtime entity ID</param>
    /// <param name="entityData">Updated entity data as JSON object</param>
    /// <returns>Updated entity</returns>
    [McpServerTool(Name = "update_entity")]
    [Description("Update an existing entity with new data")]
    public static async Task<object> UpdateEntity(
        IMcpServer server,
        string ckTypeId,
        string entityId,
        string entityData)
    {
        var httpContextAccessor = server.Services!.GetRequiredService<IHttpContextAccessor>();
        var tenantRepository = await httpContextAccessor.GetTenantRepositoryAsync();

        using var session = await tenantRepository.GetSessionAsync();

        try
        {
            var typeGraph = await tenantRepository.GetCkTypeGraphAsync(new CkId<CkTypeId>(ckTypeId));
            var dataDict = JsonSerializer.Deserialize<Dictionary<string, object>>(entityData);
            var rtId = new OctoObjectId(entityId);
            
            // Get existing entity
            var existingEntity = await tenantRepository.GetRtEntityByRtIdAsync(session, new RtEntityId(rtId));
            if (existingEntity == null)
            {
                return new { error = "Entity not found", entityId, ckTypeId };
            }

            // Create update entity with only changed fields
            var updateEntity = await tenantRepository.CreateTransientRtEntityAsync(new CkId<CkTypeId>(ckTypeId));
            updateEntity.RtId = rtId;
            
            // Populate with update data
            PopulateEntity(updateEntity, dataDict!, typeGraph);
            
            // Update entity
            await tenantRepository.UpdateOneRtEntityByIdAsync(session, new CkId<CkTypeId>(ckTypeId), rtId, updateEntity);
            await session.CommitAsync();

            // Get updated entity
            var updatedEntity = await tenantRepository.GetRtEntityByRtIdAsync(session, new RtEntityId(rtId));

            return new
            {
                success = true,
                typeId = ckTypeId,
                entityId,
                entity = FormatEntity(updatedEntity!, typeGraph)
            };
        }
        catch (Exception ex)
        {
            await session.RollbackAsync();
            return new
            {
                error = "Failed to update entity",
                message = ex.Message,
                entityId,
                ckTypeId
            };
        }
    }

    /// <summary>
    /// Delete an entity by its runtime ID
    /// </summary>
    /// <param name="server">MCP Server instance</param>
    /// <param name="ckTypeId">Construction Kit Type ID</param>
    /// <param name="entityId">Runtime entity ID</param>
    /// <returns>Deletion result</returns>
    [McpServerTool(Name = "delete_entity")]
    [Description("Delete an entity by its runtime ID")]
    public static async Task<object> DeleteEntity(
        IMcpServer server,
        string ckTypeId,
        string entityId)
    {
        var httpContextAccessor = server.Services!.GetRequiredService<IHttpContextAccessor>();
        var tenantRepository = await httpContextAccessor.GetTenantRepositoryAsync();

        using var session = await tenantRepository.GetSessionAsync();

        try
        {
            var rtId = new OctoObjectId(entityId);
            
            // Check if entity exists
            var existingEntity = await tenantRepository.GetRtEntityByRtIdAsync(session, new RtEntityId(rtId));
            if (existingEntity == null)
            {
                return new { error = "Entity not found", entityId, ckTypeId };
            }

            // Delete entity
            await tenantRepository.DeleteOneRtEntityByRtIdAsync(session, new CkId<CkTypeId>(ckTypeId), rtId);
            await session.CommitAsync();

            return new
            {
                success = true,
                message = "Entity deleted successfully",
                typeId = ckTypeId,
                entityId
            };
        }
        catch (Exception ex)
        {
            await session.RollbackAsync();
            return new
            {
                error = "Failed to delete entity",
                message = ex.Message,
                entityId,
                ckTypeId
            };
        }
    }

    #region Helper Methods

    private static ICollection<FieldFilter> BuildFieldFilters(Dictionary<string, object>? filterDict, CkTypeGraph typeGraph)
    {
        var filters = new List<FieldFilter>();
        
        if (filterDict == null) return filters;

        foreach (var kvp in filterDict)
        {
            // Simple equality filter for now
            filters.Add(new FieldFilter
            {
                AttributePath = kvp.Key,
                Operator = FieldFilterOperator.Equals,
                ComparisonValue = kvp.Value?.ToString()
            });
        }

        return filters;
    }

    private static object FormatEntity(RtEntity entity, CkTypeGraph typeGraph)
    {
        var result = new Dictionary<string, object>
        {
            ["_id"] = entity.RtId.ToString(),
            ["_ckTypeId"] = entity.CkTypeId.ToString(),
            ["_createdAt"] = entity.CreatedAt,
            ["_modifiedAt"] = entity.ModifiedAt
        };

        // Add all attributes from the entity
        foreach (var attribute in entity.Attributes)
        {
            result[attribute.Key] = attribute.Value ?? "null";
        }

        return result;
    }

    private static void PopulateEntity(RtEntity entity, Dictionary<string, object> dataDict, CkTypeGraph typeGraph)
    {
        foreach (var kvp in dataDict)
        {
            if (kvp.Key.StartsWith("_")) continue; // Skip system fields
            
            entity.Attributes[kvp.Key] = kvp.Value;
        }
    }

    #endregion
}
