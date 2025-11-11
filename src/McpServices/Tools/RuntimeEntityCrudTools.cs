using System.ComponentModel;
using System.Text.Json;
using Meshmakers.Octo.Backend.McpServices.Models;
using Meshmakers.Octo.Backend.McpServices.Models.Filters;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Services.Infrastructure.Services;
using ModelContextProtocol.Server;
using FieldFilterDto = Meshmakers.Octo.Backend.McpServices.Models.Filters.FieldFilterDto;

// ReSharper disable UnusedMember.Global

namespace Meshmakers.Octo.Backend.McpServices.Tools;

/// <summary>
///     CRUD operations for Runtime Model of OctoMesh
/// </summary>
[McpServerToolType]
public sealed class RuntimeEntityCrudTools
{
    /// <summary>
    ///     Query entities of any CK type with optional filters
    /// </summary>
    /// <param name="server">MCP Server instance</param>
    /// <param name="ckTypeId">Construction Kit Type ID (e.g., 'EnergyCommunity-1.0.0/Customer-1')</param>
    /// <param name="filters">
    ///     Optional filters - can be:
    ///     1. Simple JSON string for equality filters: {"contact.firstName": "Gerald", "contact.lastName": "Lochner"}
    ///     2. Complex EntityFilterDto object for advanced filtering with operators
    /// </param>
    /// <param name="limit">Maximum number of results to return</param>
    /// <param name="offset">Number of results to skip</param>
    /// <returns>Query results with entity data</returns>
    [McpServerTool(Name = "query_entities")]
    [Description(
        "Query entities of any Construction Kit type with optional filters. For simple equality filters, pass a JSON object like {\"FirstName\": \"Gerald\", \"LastName\": \"Lochner\"}. For complex filters with operators, use the EntityFilterDto format.")]
    public static async Task<QueryEntitiesResponse> QueryEntities(
        IMcpServer server,
        string ckTypeId,
        FieldFilterCriteriaDto? filters = null,
        int? limit = null,
        int? offset = null)
    {
        var httpContextAccessor = server.Services!.GetRequiredService<IOctoHttpContextAccessor>();
        var rtEntityToDtoMapper = server.Services!.GetRequiredService<IRtEntityToDtoMapper>();
        var tenantRepository = await httpContextAccessor.GetTenantRepositoryAsync();
        var tenantId = httpContextAccessor.GetTenantId();

        using var session = await tenantRepository.GetSessionAsync();
        session.StartTransaction();

        try
        {
            await tenantRepository.GetCkTypeGraphAsync(new RtCkId<CkTypeId>(ckTypeId));

            // Build query operation
            var queryOperation = RtEntityQueryOptions.Create();


            // Parse filters if provided
            if (filters != null)
            {
                if (filters.Operator == LogicalOperatorDto.Or)
                {
                    queryOperation = RtEntityQueryOptions.Create(LogicalOperators.Or);
                }

                BuildTypedFilters(filters, queryOperation);
            }

            var results = await tenantRepository.GetRtEntitiesByTypeAsync(
                session,
                new RtCkId<CkTypeId>(ckTypeId),
                queryOperation,
                offset,
                limit);

            return new QueryEntitiesResponse
            {
                IsSuccess = true,
                CkTypeId = ckTypeId,
                TotalCount = results.TotalCount,
                ReturnedCount = results.Items.Count(),
                Entities = results.Items.Select(e =>
                        rtEntityToDtoMapper.ConvertToDto(tenantId, e, AttributeValueResolveFlags.ResolveEnumsToNames))
                    .ToList()
            };
        }
        catch (Exception ex)
        {
            return new QueryEntitiesResponse
            {
                IsSuccess = false,
                ErrorMessage = ex.Message,
                CkTypeId = ckTypeId
            };
        }
    }

    /// <summary>
    ///     Query entities with simple filters for Claude compatibility
    /// </summary>
    /// <param name="server">MCP Server instance</param>
    /// <param name="ckTypeId">Construction Kit Type ID (e.g., 'EnergyCommunity/Customer')</param>
    /// <param name="simpleFilters">Simple filters as a JSON array (e.g., [{attributePath: "FirstName", value: "Gerald"}, {attributePath: "LastName", value: "Lochner"}])</param>
    /// <param name="limit">Maximum number of results to return</param>
    /// <param name="offset">Number of results to skip</param>
    /// <returns>Query results with entity data</returns>
    [McpServerTool(Name = "query_entities_simple")]
    [Description(
        "Query entities with simple equality filters - optimized for Claude. Pass filters as a JSON object where keys are field names and values are the exact values to match.")]
    public static async Task<QueryEntitiesResponse> QueryEntitiesSimple(
        IMcpServer server,
        string ckTypeId,
        List<SimpleFilterDto>? simpleFilters = null,
        int? limit = null,
        int? offset = null)
    {
        var httpContextAccessor = server.Services!.GetRequiredService<IOctoHttpContextAccessor>();
        var rtEntityToDtoMapper = server.Services!.GetRequiredService<IRtEntityToDtoMapper>();
        var tenantRepository = await httpContextAccessor.GetTenantRepositoryAsync();
        var tenantId = httpContextAccessor.GetTenantId();

        using var session = await tenantRepository.GetSessionAsync();
        session.StartTransaction();

        try
        {
            await tenantRepository.GetCkTypeGraphAsync(new RtCkId<CkTypeId>(ckTypeId));

            var queryOperation = BuildFilter(simpleFilters);

            var results = await tenantRepository.GetRtEntitiesByTypeAsync(
                session,
                new RtCkId<CkTypeId>(ckTypeId),
                queryOperation,
                offset,
                limit);

            return new QueryEntitiesResponse
            {
                IsSuccess = true,
                CkTypeId = ckTypeId,
                TotalCount = results.TotalCount,
                ReturnedCount = results.Items.Count(),
                Entities = results.Items.Select(e =>
                        rtEntityToDtoMapper.ConvertToDto(tenantId, e, AttributeValueResolveFlags.ResolveEnumsToNames))
                    .ToList()
            };
        }
        catch (Exception ex)
        {
            return new QueryEntitiesResponse
            {
                IsSuccess = false,
                ErrorMessage = ex.Message,
                CkTypeId = ckTypeId
            };
        }
    }

    /// <summary>
    ///     Get a single entity by its runtime ID
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
        var rtEntityToDtoMapper = server.Services!.GetRequiredService<IRtEntityToDtoMapper>();

        using var session = await tenantRepository.GetSessionAsync();
        session.StartTransaction();

        try
        {
            var rtEntityId = new RtEntityId(new RtCkId<CkTypeId>(ckTypeId),
                new OctoObjectId(rtId));

            var entity = await tenantRepository.GetRtEntityByRtIdAsync(session, rtEntityId);

            if (entity == null)
            {
                throw new ArgumentException($"Entity with ID '{rtId}' not found in type '{ckTypeId}'");
            }

            return new GetEntityResponse
            {
                IsSuccess = true,
                TypeId = ckTypeId,
                Entity = rtEntityToDtoMapper.ConvertToDto(tenantRepository.TenantId, entity,
                    AttributeValueResolveFlags.ResolveEnumsToNames)
            };
        }
        catch (Exception ex)
        {
            return new GetEntityResponse
            {
                IsSuccess = false,
                ErrorMessage = ex.Message,
                TypeId = ckTypeId
            };
        }
    }

    /// <summary>
    ///     Create a new entity of the specified CK type
    /// </summary>
    /// <param name="server">MCP Server instance</param>
    /// <param name="ckTypeId">Construction Kit Type ID</param>
    /// <param name="entityData">
    ///     JSON formated an array of attribute path and value to be updated.
    ///     For example,  [{attributePath: 'contact.test', value: 'test'}]
    /// </param>
    /// <returns>Created entity with runtime ID</returns>
    [McpServerTool(Name = "create_entity")]
    [Description("Create a new entity of specified Construction Kit type")]
    public static async Task<CreateEntityResponse> CreateEntity(
        IMcpServer server,
        string ckTypeId,
        List<AttributeUpdateItem> entityData)
    {
        var httpContextAccessor = server.Services!.GetRequiredService<IOctoHttpContextAccessor>();
        var ckCacheService = server.Services!.GetRequiredService<ICkCacheService>();
        var tenantRepository = await httpContextAccessor.GetTenantRepositoryAsync();
        var rtEntityToDtoMapper = server.Services!.GetRequiredService<IRtEntityToDtoMapper>();

        using var session = await tenantRepository.GetSessionAsync();
        session.StartTransaction();

        try
        {
            // Create transient entity
            var entity = await tenantRepository.CreateTransientRtEntityAsync(new CkId<CkTypeId>(ckTypeId));

            Assign(entity, ckCacheService, tenantRepository.TenantId, entityData);

            // Insert entity
            await tenantRepository.InsertOneRtEntityAsync(session, new RtCkId<CkTypeId>(ckTypeId), entity);
            await session.CommitTransactionAsync();

            return new CreateEntityResponse
            {
                IsSuccess = true,
                CkTypeId = ckTypeId,
                RtId = entity.RtId.ToString(),
                Entity = rtEntityToDtoMapper.ConvertToDto(tenantRepository.TenantId, entity,
                    AttributeValueResolveFlags.ResolveEnumsToNames)
            };
        }
        catch (Exception ex)
        {
            await session.AbortTransactionAsync();

            return new CreateEntityResponse
            {
                IsSuccess = false,
                ErrorMessage = ex.Message,
                CkTypeId = ckTypeId
            };
        }
    }

    /// <summary>
    ///     Update an existing entity
    /// </summary>
    /// <param name="server">MCP Server instance</param>
    /// <param name="rtId">The runtime ID of the entity to update</param>
    /// <param name="ckTypeId">The Construction Kit Type ID of the entity</param>
    /// <param name="entityData">
    ///     JSON formated an array of attribute path and value to be updated.
    ///     For example,  [{attributePath: 'contact.test', value: 'test'}]
    /// </param>
    /// <returns>Updated entity</returns>
    [McpServerTool(Name = "update_entity")]
    [Description("Update an existing entity with new data")]
    public static async Task<UpdateEntityResponse> UpdateEntity(
        IMcpServer server, string rtId, string ckTypeId, List<AttributeUpdateItem> entityData)
    {
        var httpContextAccessor = server.Services!.GetRequiredService<IOctoHttpContextAccessor>();
        var ckCacheService = server.Services!.GetRequiredService<ICkCacheService>();
        var tenantRepository = await httpContextAccessor.GetTenantRepositoryAsync();
        var rtEntityToDtoMapper = server.Services!.GetRequiredService<IRtEntityToDtoMapper>();

        using var session = await tenantRepository.GetSessionAsync();
        session.StartTransaction();
        try
        {
            var rtEntityId = new RtEntityId(ckTypeId, OctoObjectId.Parse(rtId));
            // Get existing entity
            var existingEntity = await tenantRepository.GetRtEntityByRtIdAsync(session, rtEntityId);
            if (existingEntity == null)
            {
                throw new ArgumentException(
                    $"Entity with ID '{rtId}' not found in type '{ckTypeId}'");
            }

            Assign(existingEntity, ckCacheService, tenantRepository.TenantId, entityData);

            // Update entity
            await tenantRepository.UpdateOneRtEntityByIdAsync(session, rtEntityId.CkTypeId, rtEntityId.RtId,
                existingEntity);
            await session.CommitTransactionAsync();

            // Get updated entity

            var readSession = await tenantRepository.GetSessionAsync();
            readSession.StartTransaction();

            var updatedEntity = await tenantRepository.GetRtEntityByRtIdAsync(readSession, rtEntityId);
            await readSession.CommitTransactionAsync();

            if (updatedEntity == null)
            {
                throw McpServerException.EntityNotFound(rtEntityId);
            }

            return new UpdateEntityResponse
            {
                IsSuccess = true,
                TypeId = ckTypeId,
                RtId = rtId,
                Entity = rtEntityToDtoMapper.ConvertToDto(tenantRepository.TenantId, updatedEntity,
                    AttributeValueResolveFlags.ResolveEnumsToNames)
            };
        }
        catch (Exception ex)
        {
            await session.AbortTransactionAsync();

            return new UpdateEntityResponse
            {
                IsSuccess = false,
                ErrorMessage = ex.Message,
                TypeId = ckTypeId,
                RtId = rtId
            };
        }
    }

    /// <summary>
    ///     Delete an entity by its runtime ID
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
        session.StartTransaction();

        try
        {
            // Check if entity exists
            var existingEntity = await tenantRepository.GetRtEntityByRtIdAsync(session,
                new RtEntityId(new RtCkId<CkTypeId>(ckTypeId), new OctoObjectId(rtId)));
            if (existingEntity == null)
            {
                throw new ArgumentException($"Entity with ID '{rtId}' not found in type '{ckTypeId}'");
            }

            // Delete entity
            await tenantRepository.DeleteOneRtEntityByRtIdAsync(session, new RtCkId<CkTypeId>(ckTypeId),
                new OctoObjectId(rtId), DeleteOptions.Default);
            await session.CommitTransactionAsync();

            return new DeleteEntityResponse
            {
                IsSuccess = true,
                CkTypeId = ckTypeId,
                RtId = rtId
            };
        }
        catch (Exception ex)
        {
            await session.AbortTransactionAsync();

            return new DeleteEntityResponse
            {
                IsSuccess = false,
                ErrorMessage = ex.Message,
                CkTypeId = ckTypeId,
                RtId = rtId
            };
        }
    }

    /// <summary>
    ///     Navigate associations from a source entity to find related entities
    /// </summary>
    /// <param name="server">MCP Server instance</param>
    /// <param name="ckTypeId">Source Construction Kit Type ID</param>
    /// <param name="rtId">Source Runtime entity ID</param>
    /// <param name="ckRoleId">The construction kit role id to use (e.g., "System/ParentChild")</param>
    /// <param name="direction">The direction of the association to navigate (inbound or outbound)</param>
    /// <param name="targetTypeId">Optional target type ID to filter results</param>
    /// <param name="filters">Optional filters for the target entities as a JSON array (e.g., [{attributePath: "FirstName", value: "Gerald"}, {attributePath: "LastName", value: "Lochner"}])</param>
    /// <returns>Related entities following the association path</returns>
    [McpServerTool(Name = "navigate_associations")]
    [Description(
        "Navigate associations from a source entity to find related entities. Use dot notation for the association path (e.g., 'Facilities.Children'). Optionally filter results by type and/or field values.")]
    public static async Task<NavigateAssociationsResponse> NavigateAssociations(
        IMcpServer server,
        string ckTypeId,
        string rtId,
        string ckRoleId,
        CkTypeAssociationDirectionDto direction,
        string targetTypeId,
        List<SimpleFilterDto>? filters = null)
    {
        var httpContextAccessor = server.Services!.GetRequiredService<IOctoHttpContextAccessor>();
        var ckCacheService = server.Services!.GetRequiredService<ICkCacheService>();
        var tenantRepository = await httpContextAccessor.GetTenantRepositoryAsync();
        var tenantId = httpContextAccessor.GetTenantId();
        var rtEntityToDtoMapper = server.Services!.GetRequiredService<IRtEntityToDtoMapper>();

        using var session = await tenantRepository.GetSessionAsync();
        session.StartTransaction();

        try
        {
            // Get the source entity
            var sourceEntityId = new RtEntityId(new RtCkId<CkTypeId>(ckTypeId), new OctoObjectId(rtId));
            var sourceEntity = await tenantRepository.GetRtEntityByRtIdAsync(session, sourceEntityId);

            if (sourceEntity == null)
            {
                throw new ArgumentException($"Source entity with ID '{rtId}' not found in type '{ckTypeId}'");
            }


            // Get association navigation property
            var typeGraph = ckCacheService.GetCkType(tenantId, ckTypeId);
            if (direction == CkTypeAssociationDirectionDto.Inbound)
            {
                var inAssociation =
                    typeGraph.Associations.In.All.FirstOrDefault(a => a.CkRoleId == ckRoleId);
                if (inAssociation == null)
                {
                    throw new ArgumentException($"Association '{ckRoleId}' not found on type '{ckTypeId}'");
                }
            }
            else
            {
                var outAssociation =
                    typeGraph.Associations.Out.All.FirstOrDefault(a => a.CkRoleId == ckRoleId);
                if (outAssociation == null)
                {
                    throw new ArgumentException($"Association '{ckRoleId}' not found on type '{ckTypeId}'");
                }
            }

            var queryOperation = BuildFilter(filters);

            // Handle association
            var associatedResults = await tenantRepository.GetRtAssociationTargetsAsync(
                session,
                new OctoObjectId(rtId),
                ckTypeId,
                ckRoleId,
                targetTypeId,
                (GraphDirections)direction,
                null,
                queryOperation);


            return new NavigateAssociationsResponse
            {
                IsSuccess = true,
                OriginCkTypeId = ckTypeId,
                OriginRtId = rtId,
                CkRoleId = ckRoleId,
                TargetTypeId = targetTypeId,
                TotalCount = associatedResults.TotalCount,
                Entities = associatedResults.Items.Select(e =>
                        rtEntityToDtoMapper.ConvertToDto(tenantId, e, AttributeValueResolveFlags.ResolveEnumsToNames))
                    .ToList()
            };
        }
        catch (Exception ex)
        {
            return new NavigateAssociationsResponse
            {
                IsSuccess = false,
                ErrorMessage = ex.Message,
                OriginCkTypeId = ckTypeId,
                OriginRtId = rtId,
                CkRoleId = ckRoleId,
                TargetTypeId = targetTypeId
            };
        }
    }

    #region Helper Methods

    private static RtEntityQueryOptions BuildFilter(List<SimpleFilterDto>? simpleFilters)
    {
        // Build query operation
        var queryOperation = RtEntityQueryOptions.Create();

        // Parse simple filters if provided
        if (simpleFilters is { Count: > 0 })
        {
            foreach (var simpleFilterDto in simpleFilters)
            {
                queryOperation.FieldEquals(simpleFilterDto.AttributePath, simpleFilterDto.Value);
            }
        }

        return queryOperation;
    }


    /// <summary>
    ///     Baut typisierte Filter in DataQueryOperation ein
    /// </summary>
    private static void BuildTypedFilters(FieldFilterCriteriaDto filterCriteriaDto,
        FieldFilterCriteria fieldFilterCriteria)
    {
        ApplyFieldFilters(filterCriteriaDto, fieldFilterCriteria);

        // Handle nested filters with logical operators
        if (filterCriteriaDto.NestedFilters?.Any() == true)
        {
            foreach (var nestedFilterDto in filterCriteriaDto.NestedFilters)
            {
                var nestedFilter = FieldFilterCriteria.Create((LogicalOperators)nestedFilterDto.Operator);
                BuildTypedFilters(nestedFilterDto, nestedFilter);
                fieldFilterCriteria.AddNestedFilter(nestedFilter);
            }
        }
    }

    private static void ApplyFieldFilters(FieldFilterCriteriaDto filterCriteriaDto,
        FieldFilterCriteria fieldFilterCriteria)
    {
        foreach (var fieldFilter in filterCriteriaDto.Fields)
        {
            ApplyFieldFilter(fieldFilter, fieldFilterCriteria);
        }
    }

    /// <summary>
    ///     Wendet einen einzelnen Feld-Filter an
    /// </summary>
    private static void ApplyFieldFilter(FieldFilterDto filter, FieldFilterCriteria fieldFilterCriteria)
    {
        switch (filter.Operator)
        {
            case FilterOperatorDto.Equals:
                fieldFilterCriteria.FieldEquals(filter.AttributePath, filter.Value);
                break;
            case FilterOperatorDto.NotEquals:
                fieldFilterCriteria.FieldNotEquals(filter.AttributePath, filter.Value);
                break;
            case FilterOperatorDto.Contains:
                fieldFilterCriteria.FieldContains(filter.AttributePath, filter.Value?.ToString());
                break;
            case FilterOperatorDto.StartsWith:
                fieldFilterCriteria.FieldStartsWith(filter.AttributePath, filter.Value?.ToString());
                break;
            case FilterOperatorDto.EndsWith:
                fieldFilterCriteria.FieldEndsWith(filter.AttributePath, filter.Value?.ToString());
                break;
            case FilterOperatorDto.GreaterThan:
                fieldFilterCriteria.FieldGreaterThan(filter.AttributePath, filter.Value);
                break;
            case FilterOperatorDto.GreaterThanOrEqual:
                fieldFilterCriteria.FieldGreaterThanOrEqual(filter.AttributePath, filter.Value);
                break;
            case FilterOperatorDto.LessThan:
                fieldFilterCriteria.FieldLessThan(filter.AttributePath, filter.Value);
                break;
            case FilterOperatorDto.LessThanOrEqual:
                fieldFilterCriteria.FieldLessThanOrEqual(filter.AttributePath, filter.Value);
                break;
            case FilterOperatorDto.Between:
                fieldFilterCriteria.FieldBetween(filter.AttributePath, filter.Value, filter.SecondValue);
                break;
            case FilterOperatorDto.In:
                if (filter.Value is IEnumerable<object> values)
                {
                    fieldFilterCriteria.FieldIn(filter.AttributePath, values);
                }

                break;
            case FilterOperatorDto.NotIn:
                if (filter.Value is IEnumerable<object> notInValues)
                {
                    fieldFilterCriteria.FieldNotIn(filter.AttributePath, notInValues);
                }

                break;
            case FilterOperatorDto.IsNull:
                fieldFilterCriteria.FieldIsNull(filter.AttributePath);
                break;
            case FilterOperatorDto.IsNotNull:
                fieldFilterCriteria.FieldIsNotNull(filter.AttributePath);
                break;
            case FilterOperatorDto.Regex:
                fieldFilterCriteria.FieldRegex(filter.AttributePath, filter.Value?.ToString());
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(filter.Operator), filter.Operator,
                    $@"Filter operator {filter.Operator} unsupported");
        }
    }

    private static void Assign(RtEntity rtEntity, ICkCacheService ckCacheService, string tenantId,
        List<AttributeUpdateItem> entityData)
    {
        foreach (var attributeUpdateItem in entityData)
        {
            object? value = null;

            switch (attributeUpdateItem.Value)
            {
                case JsonElement jsonElement:
                    if (jsonElement.ValueKind == JsonValueKind.String)
                    {
                        value = jsonElement.GetString();
                    }
                    else if (jsonElement.ValueKind == JsonValueKind.Number)
                    {
                        if (jsonElement.TryGetInt32(out var intValue))
                        {
                            value = intValue;
                        }
                        else if (jsonElement.TryGetInt64(out var longValue))
                        {
                            value = longValue;
                        }
                        else if (jsonElement.TryGetDouble(out var doubleValue))
                        {
                            value = doubleValue;
                        }
                    }
                    else if (jsonElement.ValueKind == JsonValueKind.True)
                    {
                        value = true;
                    }
                    else if (jsonElement.ValueKind == JsonValueKind.False)
                    {
                        value = false;
                    }
                    else if (jsonElement.ValueKind == JsonValueKind.Null)
                    {
                        value = null;
                    }
                    else
                    {
                        throw new ArgumentException(
                            $"Unsupported JSON value type for attribute '{attributeUpdateItem.AttributePath}'");
                    }

                    break;
            }

            rtEntity.SetAttributeValueByAccessPath(ckCacheService, tenantId, attributeUpdateItem.AttributePath, value);
        }
    }

    #endregion
}