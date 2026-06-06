using System.ComponentModel;
using System.Text.Json;
using Meshmakers.Octo.Backend.McpServices.Models;
using Meshmakers.Octo.Backend.McpServices.Models.Filters;
using Meshmakers.Octo.Backend.McpServices.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Services.Infrastructure.Services;
using ModelContextProtocol.Server;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using CkTypeAssociationDirectionDto = Meshmakers.Octo.Backend.McpServices.Models.CkTypeAssociationDirectionDto;
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
    /// <param name="attributePaths">Optional list of attribute paths to include in the response. If null, all attributes are returned.</param>
    /// <param name="limit">Maximum number of results to return</param>
    /// <param name="offset">Number of results to skip</param>
    /// <param name="tenantId">Optional tenant ID. If not specified, the tenant is resolved from the URL route.</param>
    /// <returns>Query results with entity data</returns>
    [McpServerTool(Name = "query_entities")]
    [Description(
        "Query entities of any Construction Kit type with optional filters. For simple equality filters, pass a JSON object like {\"FirstName\": \"Gerald\", \"LastName\": \"Lochner\"}. For complex filters with operators, use the EntityFilterDto format. Use attributePaths to request only specific attributes and reduce response size.")]
    public static async Task<QueryEntitiesResponse> QueryEntities(
        McpServer server,
        string ckTypeId,
        FieldFilterCriteriaDto? filters = null,
        List<string>? attributePaths = null,
        int? limit = null,
        int? offset = null,
        string? tenantId = null)
    {
        var tenantResolution = server.Services!.GetRequiredService<ITenantResolutionService>();
        var rtEntityToDtoMapper = server.Services!.GetRequiredService<IRtEntityToDtoMapper>();
        var tenantRepository = await tenantResolution.GetTenantRepositoryAsync(tenantId);
        var resolvedTenantId = tenantRepository.TenantId;

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

            var entities = results.Items.Select(e =>
                    rtEntityToDtoMapper.ConvertToDto(resolvedTenantId, e, AttributeValueResolveFlags.ResolveEnumsToNames))
                .ToList();

            // Filter attributes if attributePaths is specified
            if (attributePaths is { Count: > 0 })
            {
                var pathSet = new HashSet<string>(attributePaths, StringComparer.OrdinalIgnoreCase);
                foreach (var entity in entities)
                {
                    FilterAttributes(entity, pathSet);
                }
            }

            return new QueryEntitiesResponse
            {
                IsSuccess = true,
                CkTypeId = ckTypeId,
                TotalCount = results.TotalCount,
                ReturnedCount = results.Items.Count(),
                Entities = entities
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
    /// <param name="attributePaths">Optional list of attribute paths to include in the response (e.g., ["Name", "ControlType", "States.Name"]). If null, all attributes are returned. Use this to reduce response size.</param>
    /// <param name="limit">Maximum number of results to return</param>
    /// <param name="offset">Number of results to skip</param>
    /// <param name="tenantId">Optional tenant ID. If not specified, the tenant is resolved from the URL route.</param>
    /// <returns>Query results with entity data</returns>
    [McpServerTool(Name = "query_entities_simple")]
    [Description(
        "Query entities with simple equality filters - optimized for Claude. Pass filters as a JSON object where keys are field names and values are the exact values to match. Use attributePaths to request only specific attributes and reduce response size (e.g., [\"Name\", \"ControlType\", \"States.Name\"]).")]
    public static async Task<QueryEntitiesResponse> QueryEntitiesSimple(
        McpServer server,
        string ckTypeId,
        List<SimpleFilterDto>? simpleFilters = null,
        List<string>? attributePaths = null,
        int? limit = null,
        int? offset = null,
        string? tenantId = null)
    {
        var tenantResolution = server.Services!.GetRequiredService<ITenantResolutionService>();
        var rtEntityToDtoMapper = server.Services!.GetRequiredService<IRtEntityToDtoMapper>();
        var tenantRepository = await tenantResolution.GetTenantRepositoryAsync(tenantId);
        var resolvedTenantId = tenantRepository.TenantId;

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

            var entities = results.Items.Select(e =>
                    rtEntityToDtoMapper.ConvertToDto(resolvedTenantId, e, AttributeValueResolveFlags.ResolveEnumsToNames))
                .ToList();

            // Filter attributes if attributePaths is specified
            if (attributePaths is { Count: > 0 })
            {
                var pathSet = new HashSet<string>(attributePaths, StringComparer.OrdinalIgnoreCase);
                foreach (var entity in entities)
                {
                    FilterAttributes(entity, pathSet);
                }
            }

            return new QueryEntitiesResponse
            {
                IsSuccess = true,
                CkTypeId = ckTypeId,
                TotalCount = results.TotalCount,
                ReturnedCount = results.Items.Count(),
                Entities = entities
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
    /// <param name="tenantId">Optional tenant ID. If not specified, the tenant is resolved from the URL route.</param>
    /// <returns>Entity data or null if not found</returns>
    [McpServerTool(Name = "get_entity_by_id")]
    [Description("Get a single entity by its runtime ID")]
    public static async Task<GetEntityResponse> GetEntityById(
        McpServer server,
        string ckTypeId,
        string rtId,
        string? tenantId = null)
    {
        var tenantResolution = server.Services!.GetRequiredService<ITenantResolutionService>();
        var tenantRepository = await tenantResolution.GetTenantRepositoryAsync(tenantId);
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
    /// <param name="tenantId">Optional tenant ID. If not specified, the tenant is resolved from the URL route.</param>
    /// <returns>Created entity with runtime ID</returns>
    [McpServerTool(Name = "create_entity")]
    [McpRisk(McpRiskLevel.Medium)]
    [Description("Create a new entity of specified Construction Kit type")]
    public static async Task<CreateEntityResponse> CreateEntity(
        McpServer server,
        string ckTypeId,
        List<AttributeUpdateItem> entityData,
        string? tenantId = null)
    {
        var tenantResolution = server.Services!.GetRequiredService<ITenantResolutionService>();
        var ckCacheService = server.Services!.GetRequiredService<ICkCacheService>();
        var tenantRepository = await tenantResolution.GetTenantRepositoryAsync(tenantId);
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
    /// <param name="tenantId">Optional tenant ID. If not specified, the tenant is resolved from the URL route.</param>
    /// <returns>Updated entity</returns>
    [McpServerTool(Name = "update_entity")]
    [McpRisk(McpRiskLevel.Medium)]
    [Description("Update an existing entity with new data")]
    public static async Task<UpdateEntityResponse> UpdateEntity(
        McpServer server, string rtId, string ckTypeId, List<AttributeUpdateItem> entityData,
        string? tenantId = null)
    {
        var tenantResolution = server.Services!.GetRequiredService<ITenantResolutionService>();
        var ckCacheService = server.Services!.GetRequiredService<ICkCacheService>();
        var tenantRepository = await tenantResolution.GetTenantRepositoryAsync(tenantId);
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
    /// <param name="tenantId">Optional tenant ID. If not specified, the tenant is resolved from the URL route.</param>
    /// <returns>Deletion result</returns>
    [McpServerTool(Name = "delete_entity")]
    [McpRisk(McpRiskLevel.Medium)]
    [Description("Delete an entity by its runtime ID")]
    public static async Task<DeleteEntityResponse> DeleteEntity(
        McpServer server,
        string ckTypeId,
        string rtId,
        string? tenantId = null)
    {
        var tenantResolution = server.Services!.GetRequiredService<ITenantResolutionService>();
        var tenantRepository = await tenantResolution.GetTenantRepositoryAsync(tenantId);

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
    /// <param name="attributePaths">Optional list of attribute paths to include in the response. If null, all attributes are returned.</param>
    /// <param name="tenantId">Optional tenant ID. If not specified, the tenant is resolved from the URL route.</param>
    /// <returns>Related entities following the association path</returns>
    [McpServerTool(Name = "navigate_associations")]
    [Description(
        "Navigate associations from a source entity to find related entities. Use dot notation for the association path (e.g., 'Facilities.Children'). Optionally filter results by type and/or field values.")]
    public static async Task<NavigateAssociationsResponse> NavigateAssociations(
        McpServer server,
        string ckTypeId,
        string rtId,
        string ckRoleId,
        CkTypeAssociationDirectionDto direction,
        string targetTypeId,
        List<SimpleFilterDto>? filters = null,
        List<string>? attributePaths = null,
        string? tenantId = null)
    {
        var tenantResolution = server.Services!.GetRequiredService<ITenantResolutionService>();
        var tenantRepository = await tenantResolution.GetTenantRepositoryAsync(tenantId);
        var resolvedTenantId = tenantRepository.TenantId;
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

            // Resolve IDs via RtCkId (handles both short and versioned formats)
            var resolvedCkTypeId = new RtCkId<CkTypeId>(ckTypeId);
            var resolvedRoleId = new RtCkId<CkAssociationRoleId>(ckRoleId);
            var resolvedTargetTypeId = new RtCkId<CkTypeId>(targetTypeId);

            var queryOperation = BuildFilter(filters);

            // Handle association
            var associatedResults = await tenantRepository.GetRtAssociationTargetsAsync(
                session,
                new OctoObjectId(rtId),
                resolvedCkTypeId,
                resolvedRoleId,
                resolvedTargetTypeId,
                (GraphDirections)direction,
                null,
                queryOperation);


            var entities = associatedResults.Items.Select(e =>
                    rtEntityToDtoMapper.ConvertToDto(resolvedTenantId, e, AttributeValueResolveFlags.ResolveEnumsToNames))
                .ToList();

            // Filter attributes if attributePaths is specified
            if (attributePaths is { Count: > 0 })
            {
                var pathSet = new HashSet<string>(attributePaths, StringComparer.OrdinalIgnoreCase);
                foreach (var entity in entities)
                {
                    FilterAttributes(entity, pathSet);
                }
            }

            return new NavigateAssociationsResponse
            {
                IsSuccess = true,
                OriginCkTypeId = ckTypeId,
                OriginRtId = rtId,
                CkRoleId = ckRoleId,
                TargetTypeId = targetTypeId,
                TotalCount = associatedResults.TotalCount,
                Entities = entities
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

    /// <summary>
    ///     Traverse an association tree recursively from root entities of a given type.
    ///     Returns a hierarchical structure with children at each level.
    /// </summary>
    /// <param name="server">MCP Server instance</param>
    /// <param name="rootCkTypeId">CK Type ID of the root entities (e.g., 'Loxone/Room')</param>
    /// <param name="ckRoleId">Association role to traverse (e.g., 'System/ParentChild')</param>
    /// <param name="direction">Direction to find children: 'Inbound' means children point TO root via this role</param>
    /// <param name="maxDepth">Maximum depth to traverse (1 = direct children only, 2 = children + grandchildren, etc.)</param>
    /// <param name="attributePaths">Optional list of attribute paths to include per entity</param>
    /// <param name="rootFilters">Optional filters for root entities</param>
    /// <param name="tenantId">Optional tenant ID. If not specified, the tenant is resolved from the URL route.</param>
    /// <returns>Tree structure with root entities and their nested children</returns>
    [McpServerTool(Name = "get_association_tree")]
    [Description(
        "Traverse an association tree recursively. Starts from all entities of rootCkTypeId, then follows the specified association (ckRoleId) to find children at each depth level. " +
        "Example: rootCkTypeId='Loxone/Room', ckRoleId='System/ParentChild', direction='Inbound', maxDepth=2 returns all Rooms with their Categories and Controls. " +
        "Use attributePaths to reduce response size.")]
    public static async Task<AssociationTreeResponse> GetAssociationTree(
        McpServer server,
        string rootCkTypeId,
        string ckRoleId,
        CkTypeAssociationDirectionDto direction,
        int maxDepth = 2,
        List<string>? attributePaths = null,
        List<SimpleFilterDto>? rootFilters = null,
        string? tenantId = null)
    {
        var tenantResolution = server.Services!.GetRequiredService<ITenantResolutionService>();
        var tenantRepository = await tenantResolution.GetTenantRepositoryAsync(tenantId);
        var resolvedTenantId = tenantRepository.TenantId;
        var rtEntityToDtoMapper = server.Services!.GetRequiredService<IRtEntityToDtoMapper>();

        using var session = await tenantRepository.GetSessionAsync();
        session.StartTransaction();

        try
        {
            var resolvedRoleId = new RtCkId<CkAssociationRoleId>(ckRoleId);
            var pathSet = attributePaths is { Count: > 0 }
                ? new HashSet<string>(attributePaths, StringComparer.OrdinalIgnoreCase)
                : null;

            // Query root entities
            var rootQuery = BuildFilter(rootFilters);
            var rootResults = await tenantRepository.GetRtEntitiesByTypeAsync(
                session, new RtCkId<CkTypeId>(rootCkTypeId), rootQuery, null, null);

            var rootNodes = new List<AssociationTreeNode>();
            foreach (var rootEntity in rootResults.Items)
            {
                var dto = rtEntityToDtoMapper.ConvertToDto(resolvedTenantId, rootEntity,
                    AttributeValueResolveFlags.ResolveEnumsToNames);
                if (pathSet != null)
                {
                    FilterAttributes(dto, pathSet);
                }

                var children = maxDepth > 0
                    ? await GetChildrenRecursiveAsync(session, tenantRepository, rtEntityToDtoMapper,
                        resolvedTenantId, rootEntity.RtId, rootEntity.CkTypeId!,
                        resolvedRoleId, direction, maxDepth - 1, pathSet)
                    : null;

                rootNodes.Add(new AssociationTreeNode
                {
                    RtId = rootEntity.RtId.ToString(),
                    CkTypeId = rootEntity.CkTypeId?.ToString() ?? rootCkTypeId,
                    Attributes = dto.Attributes,
                    Children = children
                });
            }

            return new AssociationTreeResponse
            {
                IsSuccess = true,
                CkRoleId = ckRoleId,
                Direction = direction.ToString(),
                MaxDepth = maxDepth,
                Nodes = rootNodes
            };
        }
        catch (Exception ex)
        {
            return new AssociationTreeResponse
            {
                IsSuccess = false,
                ErrorMessage = ex.Message,
                CkRoleId = ckRoleId,
                Direction = direction.ToString(),
                MaxDepth = maxDepth
            };
        }
    }

    private static async Task<IList<AssociationTreeNode>?> GetChildrenRecursiveAsync(
        IOctoSession session,
        ITenantRepository tenantRepository,
        IRtEntityToDtoMapper rtEntityToDtoMapper,
        string tenantId,
        OctoObjectId parentRtId,
        RtCkId<CkTypeId> parentCkTypeId,
        RtCkId<CkAssociationRoleId> roleId,
        CkTypeAssociationDirectionDto direction,
        int remainingDepth,
        HashSet<string>? pathSet)
    {
        // Find target types from the type graph's associations
        var parentTypeGraph = await tenantRepository.GetCkTypeGraphAsync(parentCkTypeId);
        var associations = direction == CkTypeAssociationDirectionDto.Inbound
            ? parentTypeGraph.Associations.In.All
            : parentTypeGraph.Associations.Out.All;

        // Get distinct target type IDs for the matching role
        // Compare using RtCkId (unversioned model + major version only)
        var targetTypeIds = associations
            .Where(a => a.CkRoleId.ToRtCkId().Equals(roleId))
            .Select(a => a.TargetCkTypeId)
            .Distinct()
            .ToList();

        if (targetTypeIds.Count == 0)
        {
            return null;
        }

        // Query children for each target type
        var children = new List<AssociationTreeNode>();
        foreach (var targetTypeId in targetTypeIds)
        {
            var childResults = await tenantRepository.GetRtAssociationTargetsAsync(
                session, parentRtId, parentCkTypeId, roleId,
                targetTypeId.ToRtCkId(),
                (GraphDirections)direction, null, RtEntityQueryOptions.Create());

            foreach (var childEntity in childResults.Items)
        {
            var dto = rtEntityToDtoMapper.ConvertToDto(tenantId, childEntity,
                AttributeValueResolveFlags.ResolveEnumsToNames);
            if (pathSet != null)
            {
                FilterAttributes(dto, pathSet);
            }

            var grandChildren = remainingDepth > 0
                ? await GetChildrenRecursiveAsync(session, tenantRepository, rtEntityToDtoMapper,
                    tenantId, childEntity.RtId, childEntity.CkTypeId!,
                    roleId, direction, remainingDepth - 1, pathSet)
                : null;

            children.Add(new AssociationTreeNode
            {
                RtId = childEntity.RtId.ToString(),
                CkTypeId = childEntity.CkTypeId?.ToString() ?? "",
                Attributes = dto.Attributes,
                Children = grandChildren
            });
            }
        }

        return children.Count > 0 ? children : null;
    }

    #region Helper Methods

    /// <summary>
    ///     Filters entity DTO attributes to only include those matching the specified paths.
    ///     Supports dot notation for record attributes (e.g., "States.Name" keeps the Name
    ///     attribute within each States record).
    /// </summary>
    internal static void FilterAttributes(RtTypeWithAttributesDto entityDto, HashSet<string> pathSet)
    {
        if (entityDto.Attributes == null)
        {
            return;
        }

        // Build lookup: top-level attribute name → set of sub-paths (for records)
        var topLevelPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var recordSubPaths = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in pathSet)
        {
            var dotIndex = path.IndexOf('.');
            if (dotIndex > 0)
            {
                var topLevel = path[..dotIndex];
                var subPath = path[(dotIndex + 1)..];
                topLevelPaths.Add(topLevel);
                if (!recordSubPaths.TryGetValue(topLevel, out var subPaths))
                {
                    subPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    recordSubPaths[topLevel] = subPaths;
                }

                subPaths.Add(subPath);
            }
            else
            {
                topLevelPaths.Add(path);
            }
        }

        // Filter: keep only matching attributes
        var filtered = new List<RtEntityAttributeDto>();
        foreach (var attr in entityDto.Attributes)
        {
            if (!topLevelPaths.Contains(attr.AttributeName))
            {
                continue;
            }

            // If there are sub-paths for this attribute, filter record attributes
            if (recordSubPaths.TryGetValue(attr.AttributeName, out var subPathSet) && attr.Value != null)
            {
                FilterRecordAttributes(attr, subPathSet);
            }

            filtered.Add(attr);
        }

        entityDto.Attributes = filtered;
    }

    private static void FilterRecordAttributes(RtEntityAttributeDto attr, HashSet<string> subPathSet)
    {
        if (attr.Value is RtRecordDto record)
        {
            FilterAttributes(record, subPathSet);
        }
        else if (attr.Value is IEnumerable<object> records)
        {
            foreach (var item in records)
            {
                if (item is RtRecordDto recordItem)
                {
                    FilterAttributes(recordItem, subPathSet);
                }
            }
        }
    }

    /// <summary>
    ///     Resolves a short association role ID (e.g., "System/ParentChild") to the full versioned ID
    ///     (e.g., "System-2.0.9/ParentChild-1") by matching against the type graph's associations.
    /// </summary>
    private static string? ResolveAssociationRoleId(CkTypeGraph typeGraph, string ckRoleId,
        CkTypeAssociationDirectionDto direction)
    {
        var associations = direction == CkTypeAssociationDirectionDto.Inbound
            ? typeGraph.Associations.In.All
            : typeGraph.Associations.Out.All;

        // Try exact match first
        var match = associations.FirstOrDefault(a => a.CkRoleId.ToString() == ckRoleId);
        if (match != null)
        {
            return match.CkRoleId.ToString();
        }

        // Try short-name match: "System/ParentChild" matches "System-2.0.9/ParentChild-1"
        var parts = ckRoleId.Split('/');
        if (parts.Length == 2)
        {
            var modelPrefix = parts[0]; // e.g., "System"
            var roleName = parts[1]; // e.g., "ParentChild"

            match = associations.FirstOrDefault(a =>
            {
                var roleStr = a.CkRoleId.ToString();
                var aParts = roleStr.Split('/');
                if (aParts.Length != 2)
                {
                    return false;
                }

                return aParts[0].StartsWith(modelPrefix, StringComparison.OrdinalIgnoreCase) &&
                       aParts[1].StartsWith(roleName, StringComparison.OrdinalIgnoreCase);
            });
        }

        return match?.CkRoleId.ToString();
    }

    /// <summary>
    ///     Matches a versioned CK role ID against a potentially short role ID.
    ///     E.g., "System-2.0.9/ParentChild-1" matches "System/ParentChild".
    /// </summary>
    private static bool MatchesRoleId(string fullRoleId, string shortOrFullRoleId)
    {
        if (fullRoleId == shortOrFullRoleId)
        {
            return true;
        }

        var fullParts = fullRoleId.Split('/');
        var shortParts = shortOrFullRoleId.Split('/');
        if (fullParts.Length != 2 || shortParts.Length != 2)
        {
            return false;
        }

        // "System-2.0.9" starts with "System", "ParentChild-1" starts with "ParentChild"
        return fullParts[0].StartsWith(shortParts[0], StringComparison.OrdinalIgnoreCase) &&
               fullParts[1].StartsWith(shortParts[1], StringComparison.OrdinalIgnoreCase);
    }

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
            case FilterOperatorDto.Like:
                fieldFilterCriteria.FieldLike(filter.AttributePath, filter.Value);
                break;
            case FilterOperatorDto.AnyEq:
                fieldFilterCriteria.FieldAnyEq(filter.AttributePath, filter.Value);
                break;
            case FilterOperatorDto.AnyLike:
                fieldFilterCriteria.FieldAnyLike(filter.AttributePath, filter.Value);
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