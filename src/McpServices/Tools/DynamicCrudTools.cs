using System.ComponentModel;
using System.Text.Json;
using Meshmakers.Octo.Backend.McpServices.Models;
using Meshmakers.Octo.Backend.McpServices.Models.Filters;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Services.Infrastructure;
using Meshmakers.Octo.Services.Infrastructure.Services;
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
    /// <param name="filters">Optional filters - can be:
    /// 1. Simple JSON string for equality filters: {"FirstName": "Gerald", "LastName": "Lochner"}
    /// 2. Complex EntityFilterDto object for advanced filtering with operators
    /// </param>
    /// <param name="limit">Maximum number of results to return</param>
    /// <param name="offset">Number of results to skip</param>
    /// <returns>Query results with entity data</returns>
    [McpServerTool(Name = "query_entities")]
    [Description("Query entities of any Construction Kit type with optional filters. For simple equality filters, pass a JSON object like {\"FirstName\": \"Gerald\", \"LastName\": \"Lochner\"}. For complex filters with operators, use the EntityFilterDto format.")]
    public static async Task<QueryEntitiesResponse> QueryEntities(
        IMcpServer server,
        string ckTypeId,
        object? filters = null,
        int? limit = null,
        int? offset = null)
    {
        var httpContextAccessor = server.Services!.GetRequiredService<IOctoHttpContextAccessor>();
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
            if (filters != null)
            {
                // Check if filters is already an EntityFilterDto
                if (filters is EntityFilterDto entityFilter)
                {
                    BuildTypedFilters(entityFilter, queryOperation);
                }
                // Try to parse as simple JSON string filter
                else if (filters is string filterString && !string.IsNullOrEmpty(filterString))
                {
                    try
                    {
                        var filterDict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(filterString);
                        if (filterDict != null)
                        {
                            foreach (var kvp in filterDict)
                            {
                                object? value = null;
                                
                                // Handle different JSON value types
                                switch (kvp.Value.ValueKind)
                                {
                                    case JsonValueKind.String:
                                        value = kvp.Value.GetString();
                                        break;
                                    case JsonValueKind.Number:
                                        if (kvp.Value.TryGetInt32(out var intValue))
                                            value = intValue;
                                        else if (kvp.Value.TryGetInt64(out var longValue))
                                            value = longValue;
                                        else if (kvp.Value.TryGetDouble(out var doubleValue))
                                            value = doubleValue;
                                        break;
                                    case JsonValueKind.True:
                                        value = true;
                                        break;
                                    case JsonValueKind.False:
                                        value = false;
                                        break;
                                    case JsonValueKind.Null:
                                        // Skip null values in simple filters
                                        continue;
                                }
                                
                                if (value != null)
                                {
                                    queryOperation.FieldEquals(kvp.Key, value);
                                }
                            }
                        }
                    }
                    catch
                    {
                        // If parsing as simple filter fails, try to deserialize as EntityFilterDto
                        var complexFilter = JsonSerializer.Deserialize<EntityFilterDto>(filterString);
                        if (complexFilter != null)
                        {
                            BuildTypedFilters(complexFilter, queryOperation);
                        }
                    }
                }
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
            throw new InvalidOperationException($"Failed to query entities for type '{ckTypeId}': {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Query entities with simple filters for Claude compatibility
    /// </summary>
    /// <param name="server">MCP Server instance</param>
    /// <param name="ckTypeId">Construction Kit Type ID (e.g., 'EnergyCommunity/Customer')</param>
    /// <param name="simpleFilters">Simple filters as JSON object (e.g., {"FirstName": "Gerald", "LastName": "Lochner"})</param>
    /// <param name="limit">Maximum number of results to return</param>
    /// <param name="offset">Number of results to skip</param>
    /// <returns>Query results with entity data</returns>
    [McpServerTool(Name = "query_entities_simple")]
    [Description("Query entities with simple equality filters - optimized for Claude. Pass filters as a JSON object where keys are field names and values are the exact values to match.")]
    public static async Task<QueryEntitiesResponse> QueryEntitiesSimple(
        IMcpServer server,
        string ckTypeId,
        string? simpleFilters = null,
        int? limit = null,
        int? offset = null)
    {
        var httpContextAccessor = server.Services!.GetRequiredService<IOctoHttpContextAccessor>();
        var ckCacheService = server.Services!.GetRequiredService<ICkCacheService>();
        var tenantRepository = await httpContextAccessor.GetTenantRepositoryAsync();
        var tenantId = httpContextAccessor.GetTenantId();

        using var session = await tenantRepository.GetSessionAsync();

        try
        {
            await tenantRepository.GetCkTypeGraphAsync(new CkId<CkTypeId>(ckTypeId));

            // Build query operation
            var queryOperation = DataQueryOperation.Create();

            // Parse simple filters if provided
            if (!string.IsNullOrEmpty(simpleFilters))
            {
                var filterDict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(simpleFilters);
                if (filterDict != null)
                {
                    foreach (var kvp in filterDict)
                    {
                        object? value = null;
                        
                        // Handle different JSON value types
                        switch (kvp.Value.ValueKind)
                        {
                            case JsonValueKind.String:
                                value = kvp.Value.GetString();
                                break;
                            case JsonValueKind.Number:
                                if (kvp.Value.TryGetInt32(out var intValue))
                                    value = intValue;
                                else if (kvp.Value.TryGetInt64(out var longValue))
                                    value = longValue;
                                else if (kvp.Value.TryGetDouble(out var doubleValue))
                                    value = doubleValue;
                                break;
                            case JsonValueKind.True:
                                value = true;
                                break;
                            case JsonValueKind.False:
                                value = false;
                                break;
                            case JsonValueKind.Null:
                                // Skip null values in simple filters
                                continue;
                        }
                        
                        if (value != null)
                        {
                            queryOperation.FieldEquals(kvp.Key, value);
                        }
                    }
                }
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
            throw new InvalidOperationException($"Failed to query entities for type '{ckTypeId}': {ex.Message}", ex);
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
    public static async Task<GetEntityResponse> GetEntityById(
        IMcpServer server,
        string ckTypeId,
        string rtId)
    {
        var httpContextAccessor = server.Services!.GetRequiredService<IOctoHttpContextAccessor>();
        var tenantRepository = await httpContextAccessor.GetTenantRepositoryAsync();

        using var session = await tenantRepository.GetSessionAsync();

        try
        {
            var rtEntityId = new RtEntityId(new CkId<CkTypeId>(ckTypeId),
                new OctoObjectId(rtId));

            var entity = await tenantRepository.GetRtEntityByRtIdAsync(session, rtEntityId);

            if (entity == null)
            {
                throw new ArgumentException($"Entity with ID '{rtId}' not found in type '{ckTypeId}'");
            }

            return new GetEntityResponse
            {
                TypeId = ckTypeId,
                Entity = entity
            };
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to get entity '{rtId}' of type '{ckTypeId}': {ex.Message}", ex);
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
    public static async Task<CreateEntityResponse> CreateEntity(
        IMcpServer server,
        string ckTypeId,
        string entityData)
    {
        var httpContextAccessor = server.Services!.GetRequiredService<IOctoHttpContextAccessor>();
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
            throw new InvalidOperationException($"Failed to create entity of type '{ckTypeId}': {ex.Message}", ex);
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
    public static async Task<UpdateEntityResponse> UpdateEntity(
        IMcpServer server,
        string ckTypeId,
        string rtId,
        string entityData)
    {
        var httpContextAccessor = server.Services!.GetRequiredService<IOctoHttpContextAccessor>();
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
                throw new ArgumentException($"Entity with ID '{rtId}' not found in type '{ckTypeId}'");
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
            throw new InvalidOperationException($"Failed to update entity '{rtId}' of type '{ckTypeId}': {ex.Message}", ex);
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
    public static async Task<DeleteEntityResponse> DeleteEntity(
        IMcpServer server,
        string ckTypeId,
        string rtId)
    {
        var httpContextAccessor = server.Services!.GetRequiredService<IOctoHttpContextAccessor>();
        var tenantRepository = await httpContextAccessor.GetTenantRepositoryAsync();

        using var session = await tenantRepository.GetSessionAsync();

        try
        {
            // Check if entity exists
            var existingEntity = await tenantRepository.GetRtEntityByRtIdAsync(session, new RtEntityId(rtId));
            if (existingEntity == null)
            {
                throw new ArgumentException($"Entity with ID '{rtId}' not found in type '{ckTypeId}'");
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
            throw new InvalidOperationException($"Failed to delete entity '{rtId}' of type '{ckTypeId}': {ex.Message}", ex);
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
    /// <param name="filters">Optional filters for the target entities as JSON object (e.g., {"Status": "Active"})</param>
    /// <returns>Related entities following the association path</returns>
    [McpServerTool(Name = "navigate_associations")]
    [Description("Navigate associations from a source entity to find related entities. Use dot notation for the association path (e.g., 'Facilities.Children'). Optionally filter results by type and/or field values.")]
    public static async Task<NavigateAssociationsResponse> NavigateAssociations(
        IMcpServer server,
        string ckTypeId,
        string rtId,
        string associationPath,
        string? targetTypeId = null,
        string? filters = null)
    {
        var httpContextAccessor = server.Services!.GetRequiredService<IOctoHttpContextAccessor>();
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
                throw new ArgumentException($"Source entity with ID '{rtId}' not found in type '{ckTypeId}'");
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
                    var outAssociation =
                        typeGraph.Associations.Out.All.FirstOrDefault(a => a.NavigationPropertyName == step);
                    var inAssociation =
                        typeGraph.Associations.In.All.FirstOrDefault(a => a.NavigationPropertyName == step);

                    if (outAssociation == null && inAssociation == null)
                    {
                        throw new ArgumentException($"Association '{step}' not found on type '{entityTypeId}'");
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
                resultEntities = resultEntities.Where(e => e.CkTypeId != null && e.CkTypeId.Equals(targetCkTypeId))
                    .ToList();
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
            throw new InvalidOperationException($"Failed to navigate associations from '{ckTypeId}' entity '{rtId}' along path '{associationPath}': {ex.Message}", ex);
        }
    }

    #region Helper Methods

    /// <summary>
    /// Baut typisierte Filter in DataQueryOperation ein
    /// </summary>
    private static void BuildTypedFilters(EntityFilterDto filterDto, DataQueryOperation queryOperation)
    {
        foreach (var fieldFilter in filterDto.Fields)
        {
            ApplyFieldFilter(fieldFilter, queryOperation);
        }

        // Handle nested filters with logical operators
        if (filterDto.NestedFilters?.Any() == true)
        {
            if (filterDto.Operator == LogicalOperatorDto.Or)
            {
                queryOperation.BeginOr();
            }

            foreach (var nestedFilter in filterDto.NestedFilters)
            {
                BuildTypedFilters(nestedFilter, queryOperation);
            }

            if (filterDto.Operator == LogicalOperatorDto.Or)
            {
                queryOperation.EndOr();
            }
        }
    }

    /// <summary>
    /// Wendet einen einzelnen Feld-Filter an
    /// </summary>
    private static void ApplyFieldFilter(FieldFilterDto filter, DataQueryOperation queryOperation)
    {
        switch (filter.Operator)
        {
            case FilterOperatorDto.Equals:
                queryOperation.FieldEquals(filter.FieldPath, filter.Value);
                break;
            case FilterOperatorDto.NotEquals:
                queryOperation.FieldNotEquals(filter.FieldPath, filter.Value);
                break;
            case FilterOperatorDto.Contains:
                queryOperation.FieldContains(filter.FieldPath, filter.Value?.ToString());
                break;
            case FilterOperatorDto.StartsWith:
                queryOperation.FieldStartsWith(filter.FieldPath, filter.Value?.ToString());
                break;
            case FilterOperatorDto.EndsWith:
                queryOperation.FieldEndsWith(filter.FieldPath, filter.Value?.ToString());
                break;
            case FilterOperatorDto.GreaterThan:
                queryOperation.FieldGreaterThan(filter.FieldPath, filter.Value);
                break;
            case FilterOperatorDto.GreaterThanOrEqual:
                queryOperation.FieldGreaterThanOrEqual(filter.FieldPath, filter.Value);
                break;
            case FilterOperatorDto.LessThan:
                queryOperation.FieldLessThan(filter.FieldPath, filter.Value);
                break;
            case FilterOperatorDto.LessThanOrEqual:
                queryOperation.FieldLessThanOrEqual(filter.FieldPath, filter.Value);
                break;
            case FilterOperatorDto.Between:
                queryOperation.FieldBetween(filter.FieldPath, filter.Value, filter.SecondValue);
                break;
            case FilterOperatorDto.In:
                if (filter.Value is IEnumerable<object> values)
                    queryOperation.FieldIn(filter.FieldPath, values);
                break;
            case FilterOperatorDto.NotIn:
                if (filter.Value is IEnumerable<object> notInValues)
                    queryOperation.FieldNotIn(filter.FieldPath, notInValues);
                break;
            case FilterOperatorDto.IsNull:
                queryOperation.FieldIsNull(filter.FieldPath);
                break;
            case FilterOperatorDto.IsNotNull:
                queryOperation.FieldIsNotNull(filter.FieldPath);
                break;
            case FilterOperatorDto.Regex:
                queryOperation.FieldRegex(filter.FieldPath, filter.Value?.ToString());
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(filter.Operator), filter.Operator,
                    "Unbekannter Filter-Operator");
        }
    }

    private static void PopulateEntity(ICkCacheService ckCacheService, string tenantId, RtEntity entity,
        Dictionary<string, object> dataDict)
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

    private static List<RtEntity> ApplyEntityFilters(List<RtEntity> entities, Dictionary<string, object>? filterDict,
        ICkCacheService ckCacheService, string tenantId)
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