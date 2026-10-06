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
        var security = await RuntimeSecurityContextResolver.ResolveAsync(server, tenantResolution, tenantId);
        if (security.Error != null)
        {
            return new QueryEntitiesResponse
            {
                IsSuccess = false,
                ErrorMessage = security.Error,
                CkTypeId = ckTypeId
            };
        }

        var rtEntityToDtoMapper = server.Services!.GetRequiredService<IRtEntityToDtoMapper>();
        var tenantRepository = await tenantResolution.GetTenantRepositoryAsync(tenantId);
        var resolvedTenantId = tenantRepository.TenantId;

        var secretUsage = SecretAttributePaths.FindNotQueryableUsage(
            server.Services!.GetRequiredService<ICkCacheService>(), resolvedTenantId,
            ckTypeId, SecretRelevantFilterUsages(filters));
        if (secretUsage != null)
        {
            return new QueryEntitiesResponse { IsSuccess = false, ErrorMessage = secretUsage, CkTypeId = ckTypeId };
        }

        using var session = await tenantRepository.GetSessionAsync(security.SecurityContext!);
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
                ErrorMessage = DescribeQueryException(ex),
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
        var security = await RuntimeSecurityContextResolver.ResolveAsync(server, tenantResolution, tenantId);
        if (security.Error != null)
        {
            return new QueryEntitiesResponse
            {
                IsSuccess = false,
                ErrorMessage = security.Error,
                CkTypeId = ckTypeId
            };
        }

        var rtEntityToDtoMapper = server.Services!.GetRequiredService<IRtEntityToDtoMapper>();
        var tenantRepository = await tenantResolution.GetTenantRepositoryAsync(tenantId);
        var resolvedTenantId = tenantRepository.TenantId;

        // Simple filters are equality filters, which a Secret attribute never supports (AB#5543).
        var secretUsage = SecretAttributePaths.FindNotQueryableUsage(
            server.Services!.GetRequiredService<ICkCacheService>(), resolvedTenantId,
            ckTypeId,
            (simpleFilters ?? []).Select(f => ((string?)f.AttributePath, "filter operator 'Equals'")));
        if (secretUsage != null)
        {
            return new QueryEntitiesResponse { IsSuccess = false, ErrorMessage = secretUsage, CkTypeId = ckTypeId };
        }

        using var session = await tenantRepository.GetSessionAsync(security.SecurityContext!);
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
                ErrorMessage = DescribeQueryException(ex),
                CkTypeId = ckTypeId
            };
        }
    }

    /// <summary>
    ///     Translates a low-level engine query exception into an actionable message for the AI client.
    ///     A field filter that targets a Record-typed attribute directly (e.g. <c>Vendor</c>) throws an
    ///     <see cref="InvalidCastException" /> deep in the query engine (it tries to cast the scalar
    ///     comparison value to <c>RtRecord</c>). Record sub-path filtering with dot notation IS supported,
    ///     so rewrite that failure into a hint to filter a record sub-field instead. Every other exception
    ///     message passes through unchanged.
    /// </summary>
    /// <param name="ex">The exception thrown while building or executing the query.</param>
    /// <returns>An actionable error message for the tool response.</returns>
    internal static string DescribeQueryException(Exception ex)
    {
        var secretError = SecretErrors.TryDescribe(ex);
        if (secretError != null)
        {
            return secretError;
        }

        if (ex.Message.Contains("RtRecord", StringComparison.Ordinal))
        {
            return "Cannot filter on a Record attribute directly — a Record attribute holds a composite " +
                   "object, not a scalar value. Filter on a record sub-field using dot notation instead, " +
                   "e.g. 'Vendor.CompanyName' or 'Vendor.LastName'. (engine: " + ex.Message + ")";
        }

        return ex.Message;
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
        var security = await RuntimeSecurityContextResolver.ResolveAsync(server, tenantResolution, tenantId);
        if (security.Error != null)
        {
            return new GetEntityResponse
            {
                IsSuccess = false,
                ErrorMessage = security.Error,
                TypeId = ckTypeId
            };
        }

        var tenantRepository = await tenantResolution.GetTenantRepositoryAsync(tenantId);
        var rtEntityToDtoMapper = server.Services!.GetRequiredService<IRtEntityToDtoMapper>();

        using var session = await tenantRepository.GetSessionAsync(security.SecurityContext!);
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
                ErrorMessage = SecretErrors.Describe(ex),
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
    /// <remarks>
    ///     Secret attributes (AB#5543) are not written here: this tool is medium risk, and setting a secret is
    ///     high risk. A non-empty value for a Secret attribute is refused with a pointer to
    ///     <c>set_entity_secrets</c>; <c>null</c>, <c>""</c> or the read marker <c>{ "isSet": … }</c> are ignored
    ///     (= "not set").
    /// </remarks>
    [McpServerTool(Name = "create_entity")]
    [McpRisk(McpRiskLevel.Medium)]
    [Description(
        "Create a new entity of specified Construction Kit type. Secret attributes cannot be set here: a non-empty " +
        "value for a Secret attribute is refused — create the entity without it, then call set_entity_secrets " +
        "(high risk), or use create_entity_with_secrets (high risk) when the type has a required secret. null, " +
        "\"\" or an echoed {\"isSet\": …} marker for a Secret attribute are ignored.")]
    public static Task<CreateEntityResponse> CreateEntity(
        McpServer server,
        string ckTypeId,
        List<AttributeUpdateItem> entityData,
        string? tenantId = null)
    {
        return CreateCoreAsync(server, ckTypeId, entityData ?? [], null, tenantId);
    }

    /// <summary>
    ///     Creates an entity together with its Secret attributes in one insert (AB#5543). High risk: the caller
    ///     hands credentials to the platform. Needed for CK types with a <i>required</i> secret, which the
    ///     engine refuses to insert without a value (rule engine message 2), so <c>create_entity</c> +
    ///     <c>set_entity_secrets</c> cannot create them.
    /// </summary>
    /// <param name="server">MCP Server instance</param>
    /// <param name="ckTypeId">Construction Kit Type ID</param>
    /// <param name="entityData">Non-secret attributes, same rules as <c>create_entity</c></param>
    /// <param name="secrets">Secret attribute paths and their non-empty string values</param>
    /// <param name="tenantId">Optional tenant ID. If not specified, the tenant is resolved from the URL route.</param>
    /// <returns>The created entity; Secret attributes appear only as <c>secretIsSet</c>.</returns>
    [McpServerTool(Name = "create_entity_with_secrets")]
    [McpRisk(McpRiskLevel.High)]
    [Description(
        "Create a new entity together with its Secret attributes (value type SECRET, e.g. passwords, API keys) in " +
        "one insert. HIGH RISK. Use it for CK types with a required secret, which create_entity cannot create. " +
        "'entityData' holds the non-secret attributes (same rules as create_entity; secret values there are " +
        "refused); each entry in 'secrets' must target a Secret attribute with a non-empty string value (any " +
        "non-empty string is an ordinary value — '<...>' or 'TODO_SET_*' have no special meaning). Values are " +
        "encrypted server-side and are never returned — the response shows secretIsSet only.")]
    public static Task<CreateEntityResponse> CreateEntityWithSecrets(
        McpServer server,
        [Description("Construction Kit type ID of the new entity.")] string ckTypeId,
        [Description(
            "Non-secret attributes: [{attributePath: 'Name', value: 'smtp'}]. Secret values are refused here — " +
            "put them into 'secrets'.")]
        List<AttributeUpdateItem>? entityData = null,
        [Description(
            "Secret values to set: [{attributePath: 'Password', value: '<new secret>'}]. Only Secret attributes " +
            "are accepted; at least one entry is required.")]
        List<AttributeUpdateItem>? secrets = null,
        [Description("Tenant to operate on. Falls back to URL route.")] string? tenantId = null)
    {
        if (secrets == null || secrets.Count == 0)
        {
            return Task.FromResult(new CreateEntityResponse
            {
                IsSuccess = false,
                ErrorMessage = "Provide at least one entry in 'secrets'. Use create_entity to create an entity " +
                               "without secrets.",
                CkTypeId = ckTypeId
            });
        }

        return CreateCoreAsync(server, ckTypeId, entityData ?? [], secrets, tenantId);
    }

    private static async Task<CreateEntityResponse> CreateCoreAsync(
        McpServer server,
        string ckTypeId,
        List<AttributeUpdateItem> entityData,
        List<AttributeUpdateItem>? secrets,
        string? tenantId)
    {
        var tenantResolution = server.Services!.GetRequiredService<ITenantResolutionService>();
        var security = await RuntimeSecurityContextResolver.ResolveAsync(server, tenantResolution, tenantId);
        if (security.Error != null)
        {
            return new CreateEntityResponse
            {
                IsSuccess = false,
                ErrorMessage = security.Error,
                CkTypeId = ckTypeId
            };
        }

        var ckCacheService = server.Services!.GetRequiredService<ICkCacheService>();
        var tenantRepository = await tenantResolution.GetTenantRepositoryAsync(tenantId);
        var rtEntityToDtoMapper = server.Services!.GetRequiredService<IRtEntityToDtoMapper>();

        var secretError = PrepareSecretAwareWrites(ckCacheService, tenantRepository.TenantId,
            ckTypeId, entityData, SecretWritePolicy.RefuseSecretValues,
            out var effectiveData);
        if (secretError == null && secrets != null)
        {
            secretError = PrepareSecretAwareWrites(ckCacheService, tenantRepository.TenantId,
                ckTypeId, secrets, SecretWritePolicy.SecretValuesOnly, out var effectiveSecrets);
            effectiveData.AddRange(effectiveSecrets);
        }

        if (secretError != null)
        {
            return new CreateEntityResponse
            {
                IsSuccess = false,
                ErrorMessage = secretError,
                CkTypeId = ckTypeId
            };
        }

        using var session = await tenantRepository.GetSessionAsync(security.SecurityContext!);
        session.StartTransaction();

        try
        {
            // Create transient entity
            var entity = await tenantRepository.CreateTransientRtEntityAsync(new CkId<CkTypeId>(ckTypeId));

            Assign(entity, ckCacheService, tenantRepository.TenantId, effectiveData);

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
                ErrorMessage = SecretErrors.Describe(ex),
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
    /// <param name="expectedVersion">
    ///     Optional optimistic-lock token. When present and the stored <c>RtVersion</c>
    ///     does not match, the call returns <c>IsSuccess=false</c> + <c>IsConflict=true</c>
    ///     with the current <c>RtVersion</c> and entity payload — no write happens.
    /// </param>
    /// <param name="clearSecretAttributes">
    ///     Optional names of Secret attributes to clear (camelCase or PascalCase). Clearing a required
    ///     secret is refused by the engine.
    /// </param>
    /// <param name="tenantId">Optional tenant ID. If not specified, the tenant is resolved from the URL route.</param>
    /// <returns>Updated entity</returns>
    /// <remarks>
    ///     Secret attributes (AB#5543): setting a value is refused (use the high-risk <c>set_entity_secrets</c>);
    ///     <c>null</c>, <c>""</c> or the read marker leave the stored secret unchanged; clearing is explicit via
    ///     <paramref name="clearSecretAttributes" />.
    /// </remarks>
    [McpServerTool(Name = "update_entity")]
    [McpRisk(McpRiskLevel.Medium)]
    [Description(
        "Update an existing entity with new data. Secret attributes: a non-empty value is refused — use " +
        "set_entity_secrets (high risk) to set or rotate a secret; null, \"\" or an echoed {\"isSet\": …} marker " +
        "leave the stored secret unchanged; use clearSecretAttributes to clear an optional secret. Secret values " +
        "are never returned — entities show secretIsSet instead.")]
    public static Task<UpdateEntityResponse> UpdateEntity(
        McpServer server, string rtId, string ckTypeId, List<AttributeUpdateItem> entityData,
        [Description(
            "Optional optimistic-lock token. Pass the RtVersion the caller read with the entity. " +
            "If the server's current RtVersion differs, the write is refused and the response " +
            "carries IsConflict=true plus the current RtVersion + Entity so the caller can " +
            "rebase. Omit to skip the check (last-write-wins).")]
        ulong? expectedVersion = null,
        [Description(
            "Optional names of Secret attributes to clear (camelCase or PascalCase, e.g. [\"password\"]). " +
            "Clearing a required secret, a non-secret attribute, or setting and clearing the same secret is refused.")]
        List<string>? clearSecretAttributes = null,
        string? tenantId = null)
    {
        return UpdateCoreAsync(server, rtId, ckTypeId, entityData ?? [], clearSecretAttributes, expectedVersion,
            tenantId, SecretWritePolicy.RefuseSecretValues);
    }

    /// <summary>
    ///     Sets, rotates or clears Secret attributes of an existing entity (AB#5543). High risk: the caller
    ///     hands a credential to the platform. Values are encrypted by the engine and never returned.
    /// </summary>
    /// <param name="server">MCP Server instance</param>
    /// <param name="rtId">The runtime ID of the entity</param>
    /// <param name="ckTypeId">The Construction Kit Type ID of the entity</param>
    /// <param name="secrets">Secret attribute paths and their new non-empty string values</param>
    /// <param name="clearSecretAttributes">Optional names of Secret attributes to clear</param>
    /// <param name="expectedVersion">Optional optimistic-lock token (see <c>update_entity</c>)</param>
    /// <param name="tenantId">Optional tenant ID. If not specified, the tenant is resolved from the URL route.</param>
    /// <returns>The updated entity; Secret attributes appear only as <c>secretIsSet</c>.</returns>
    [McpServerTool(Name = "set_entity_secrets")]
    [McpRisk(McpRiskLevel.High)]
    [Description(
        "Set, rotate or clear Secret attributes (value type SECRET, e.g. passwords, API keys, client secrets) of " +
        "an existing entity. HIGH RISK. Each entry in 'secrets' must target a Secret attribute (dot notation " +
        "through records, e.g. 'Endpoints.Token') with a non-empty string value (any non-empty string is an " +
        "ordinary value; use clearSecretAttributes to clear). Other attributes are never touched. " +
        "Values are encrypted server-side and are never returned — the response shows secretIsSet only.")]
    public static Task<UpdateEntityResponse> SetEntitySecrets(
        McpServer server,
        [Description("Runtime ID (24-hex) of the entity.")] string rtId,
        [Description("Construction Kit type ID of the entity.")] string ckTypeId,
        [Description(
            "Secret values to set: [{attributePath: 'Password', value: '<new secret>'}]. Only Secret attributes " +
            "are accepted.")]
        List<AttributeUpdateItem>? secrets = null,
        [Description("Optional names of Secret attributes to clear (camelCase or PascalCase).")]
        List<string>? clearSecretAttributes = null,
        [Description("Optional optimistic-lock token (the RtVersion read with the entity).")]
        ulong? expectedVersion = null,
        [Description("Tenant to operate on. Falls back to URL route.")] string? tenantId = null)
    {
        if ((secrets == null || secrets.Count == 0) && (clearSecretAttributes == null || clearSecretAttributes.Count == 0))
        {
            return Task.FromResult(new UpdateEntityResponse
            {
                IsSuccess = false,
                ErrorMessage = "Provide at least one entry in 'secrets' or 'clearSecretAttributes'.",
                TypeId = ckTypeId,
                RtId = rtId
            });
        }

        return UpdateCoreAsync(server, rtId, ckTypeId, secrets ?? [], clearSecretAttributes, expectedVersion,
            tenantId, SecretWritePolicy.SecretValuesOnly);
    }

    private static async Task<UpdateEntityResponse> UpdateCoreAsync(
        McpServer server, string rtId, string ckTypeId, List<AttributeUpdateItem> entityData,
        List<string>? clearSecretAttributes, ulong? expectedVersion, string? tenantId, SecretWritePolicy policy)
    {
        var tenantResolution = server.Services!.GetRequiredService<ITenantResolutionService>();
        var security = await RuntimeSecurityContextResolver.ResolveAsync(server, tenantResolution, tenantId);
        if (security.Error != null)
        {
            return new UpdateEntityResponse
            {
                IsSuccess = false,
                ErrorMessage = security.Error,
                TypeId = ckTypeId,
                RtId = rtId
            };
        }

        var ckCacheService = server.Services!.GetRequiredService<ICkCacheService>();
        var tenantRepository = await tenantResolution.GetTenantRepositoryAsync(tenantId);
        var rtEntityToDtoMapper = server.Services!.GetRequiredService<IRtEntityToDtoMapper>();
        var secretError = PrepareSecretAwareWrites(ckCacheService, tenantRepository.TenantId, ckTypeId,
            entityData, policy, out var effectiveData);
        if (secretError != null)
        {
            return new UpdateEntityResponse
            {
                IsSuccess = false,
                ErrorMessage = secretError,
                TypeId = ckTypeId,
                RtId = rtId
            };
        }

        var clearList = clearSecretAttributes?
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => SecretAttributePaths.NormaliseTopLevelName(ckCacheService, tenantRepository.TenantId,
                ckTypeId, n.Trim()))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        using var session = await tenantRepository.GetSessionAsync(security.SecurityContext!);
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

            // Optimistic-lock gate. Abort *inside* the transaction (so any reads we already
            // did roll back cleanly) and surface the current row to the caller — they get
            // enough to decide retry-with-merge without a second round-trip.
            if (expectedVersion.HasValue && existingEntity.RtVersion != expectedVersion.Value)
            {
                await session.AbortTransactionAsync();
                return new UpdateEntityResponse
                {
                    IsSuccess = false,
                    IsConflict = true,
                    CurrentRtVersion = existingEntity.RtVersion,
                    TypeId = ckTypeId,
                    RtId = rtId,
                    Entity = rtEntityToDtoMapper.ConvertToDto(tenantRepository.TenantId, existingEntity,
                        AttributeValueResolveFlags.ResolveEnumsToNames),
                    ErrorMessage =
                        $"Stale expected_version: caller had {expectedVersion.Value}, current is {existingEntity.RtVersion}."
                };
            }

            Assign(existingEntity, ckCacheService, tenantRepository.TenantId, effectiveData);
            // Bump RtVersion explicitly — the engine's Mongo layer does not auto-increment
            // it on update_one (auto-bump lives only on the bulk-mutation path). Without
            // this, the next optimistic-locked update would never see a fresh token and
            // the contract degrades to "lock once and lose forever". Saturate at ulong.MaxValue
            // so the math never throws — an entity reaching 2^64 writes is theoretical only.
            existingEntity.RtVersion = existingEntity.RtVersion == ulong.MaxValue
                ? existingEntity.RtVersion
                : existingEntity.RtVersion + 1;

            if (clearList is { Count: > 0 })
            {
                // Clearing goes through the bulk funnel with an explicit clear list (AB#5532): the rule
                // engine validates the names (messages 21-23) and turns each entry into an explicit null.
                var updateInfo = EntityUpdateInfo<RtEntity>.CreateUpdate(rtEntityId, existingEntity, clearList);
                await tenantRepository.ApplyChangesAsync(session, new List<IEntityUpdateInfo<RtEntity>> { updateInfo },
                    new OperationResult());
            }
            else
            {
                // Update entity
                await tenantRepository.UpdateOneRtEntityByIdAsync(session, rtEntityId.CkTypeId, rtEntityId.RtId,
                    existingEntity);
            }

            await session.CommitTransactionAsync();

            // Get updated entity

            using var readSession = await tenantRepository.GetSessionAsync(security.SecurityContext!);
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
                CurrentRtVersion = updatedEntity.RtVersion,
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
                ErrorMessage = SecretErrors.Describe(ex),
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
    /// <param name="expectedVersion">
    ///     Optional optimistic-lock token. When present and the stored <c>RtVersion</c>
    ///     does not match, the call returns <c>IsSuccess=false</c> + <c>IsConflict=true</c>
    ///     with the current <c>RtVersion</c> and entity payload — no delete happens.
    /// </param>
    /// <param name="tenantId">Optional tenant ID. If not specified, the tenant is resolved from the URL route.</param>
    /// <returns>Deletion result</returns>
    [McpServerTool(Name = "delete_entity")]
    [McpRisk(McpRiskLevel.Medium)]
    [Description("Delete an entity by its runtime ID")]
    public static async Task<DeleteEntityResponse> DeleteEntity(
        McpServer server,
        string ckTypeId,
        string rtId,
        [Description(
            "Optional optimistic-lock token. Pass the RtVersion the caller read with the entity. " +
            "If the server's current RtVersion differs, the delete is refused and the response " +
            "carries IsConflict=true plus the current RtVersion + Entity so the caller can " +
            "decide whether the entity changed in a way that makes the delete still appropriate.")]
        ulong? expectedVersion = null,
        string? tenantId = null)
    {
        var tenantResolution = server.Services!.GetRequiredService<ITenantResolutionService>();
        var security = await RuntimeSecurityContextResolver.ResolveAsync(server, tenantResolution, tenantId);
        if (security.Error != null)
        {
            return new DeleteEntityResponse
            {
                IsSuccess = false,
                ErrorMessage = security.Error,
                CkTypeId = ckTypeId,
                RtId = rtId
            };
        }

        var tenantRepository = await tenantResolution.GetTenantRepositoryAsync(tenantId);
        var rtEntityToDtoMapper = server.Services!.GetRequiredService<IRtEntityToDtoMapper>();

        using var session = await tenantRepository.GetSessionAsync(security.SecurityContext!);
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

            // Optimistic-lock gate — mirrors update_entity. Same contract: abort the
            // transaction, return current row + version so the caller can decide retry.
            if (expectedVersion.HasValue && existingEntity.RtVersion != expectedVersion.Value)
            {
                await session.AbortTransactionAsync();
                return new DeleteEntityResponse
                {
                    IsSuccess = false,
                    IsConflict = true,
                    CurrentRtVersion = existingEntity.RtVersion,
                    CkTypeId = ckTypeId,
                    RtId = rtId,
                    Entity = rtEntityToDtoMapper.ConvertToDto(tenantRepository.TenantId, existingEntity,
                        AttributeValueResolveFlags.ResolveEnumsToNames),
                    ErrorMessage =
                        $"Stale expected_version: caller had {expectedVersion.Value}, current is {existingEntity.RtVersion}."
                };
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
                ErrorMessage = SecretErrors.Describe(ex),
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
        var security = await RuntimeSecurityContextResolver.ResolveAsync(server, tenantResolution, tenantId);
        if (security.Error != null)
        {
            return new NavigateAssociationsResponse
            {
                IsSuccess = false,
                ErrorMessage = security.Error,
                OriginCkTypeId = ckTypeId,
                OriginRtId = rtId,
                CkRoleId = ckRoleId,
                TargetTypeId = targetTypeId
            };
        }

        var tenantRepository = await tenantResolution.GetTenantRepositoryAsync(tenantId);
        var resolvedTenantId = tenantRepository.TenantId;
        var rtEntityToDtoMapper = server.Services!.GetRequiredService<IRtEntityToDtoMapper>();

        using var session = await tenantRepository.GetSessionAsync(security.SecurityContext!);
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
                ErrorMessage = SecretErrors.Describe(ex),
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
        var security = await RuntimeSecurityContextResolver.ResolveAsync(server, tenantResolution, tenantId);
        if (security.Error != null)
        {
            return new AssociationTreeResponse
            {
                IsSuccess = false,
                ErrorMessage = security.Error,
                CkRoleId = ckRoleId,
                Direction = direction.ToString(),
                MaxDepth = maxDepth
            };
        }

        var tenantRepository = await tenantResolution.GetTenantRepositoryAsync(tenantId);
        var resolvedTenantId = tenantRepository.TenantId;
        var rtEntityToDtoMapper = server.Services!.GetRequiredService<IRtEntityToDtoMapper>();

        using var session = await tenantRepository.GetSessionAsync(security.SecurityContext!);
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
                ErrorMessage = SecretErrors.Describe(ex),
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

    /// <summary>
    ///     Lists the filter usages that are not allowed on a Secret attribute (every operator except
    ///     IsNull / IsNotNull), recursively through nested filter groups. Used for the Secret pre-validation of
    ///     the query and aggregation tools (AB#5543).
    /// </summary>
    internal static IEnumerable<(string? Path, string Operation)> SecretRelevantFilterUsages(
        FieldFilterCriteriaDto? filters)
    {
        if (filters == null)
        {
            yield break;
        }

        foreach (var field in filters.Fields)
        {
            if (field.Operator is not (FilterOperatorDto.IsNull or FilterOperatorDto.IsNotNull))
            {
                yield return (field.AttributePath, $"filter operator '{field.Operator}'");
            }
        }

        foreach (var nested in filters.NestedFilters ?? [])
        {
            foreach (var usage in SecretRelevantFilterUsages(nested))
            {
                yield return usage;
            }
        }
    }

    /// <summary>
    ///     How a write tool treats values that target Secret attributes (AB#5543).
    /// </summary>
    internal enum SecretWritePolicy
    {
        /// <summary>
        ///     Medium-risk generic writes: non-empty secret values are refused (route to set_entity_secrets),
        ///     "unchanged" inputs for secrets are dropped, other attributes pass through.
        /// </summary>
        RefuseSecretValues,

        /// <summary>
        ///     High-risk <c>set_entity_secrets</c> / <c>secrets</c> of <c>create_entity_with_secrets</c>: only Secret
        ///     attributes with a non-empty string value are accepted (placeholder-looking strings are ordinary values).
        /// </summary>
        SecretValuesOnly
    }

    /// <summary>
    ///     Classifies each attribute write against the CK type graph and applies the
    ///     <paramref name="policy" />. Returns an error message (never containing a value) or <c>null</c> with
    ///     the writes that should actually be assigned.
    /// </summary>
    internal static string? PrepareSecretAwareWrites(ICkCacheService ckCacheService, string tenantId,
        string ckTypeId, List<AttributeUpdateItem> entityData, SecretWritePolicy policy,
        out List<AttributeUpdateItem> effectiveData)
    {
        effectiveData = new List<AttributeUpdateItem>(entityData.Count);
        foreach (var item in entityData)
        {
            var isSecret = SecretAttributePaths.IsSecretPath(ckCacheService, tenantId, ckTypeId, item.AttributePath);
            if (!isSecret)
            {
                if (policy == SecretWritePolicy.SecretValuesOnly)
                {
                    return $"Attribute '{item.AttributePath}' is not a Secret attribute of '{ckTypeId}'. " +
                           "'secrets' only accepts Secret attributes; pass other attributes via update_entity " +
                           "(existing entity) or entityData (create_entity_with_secrets).";
                }

                effectiveData.Add(item);
                continue;
            }

            var kind = ClassifySecretInput(item.Value, out var text);
            switch (kind)
            {
                case SecretInputKind.Unchanged when policy == SecretWritePolicy.RefuseSecretValues:
                    // null / "" / echoed {isSet} marker: the stored secret stays as it is (concept §4.3).
                    continue;
                case SecretInputKind.Unchanged:
                    return $"Secret attribute '{item.AttributePath}' needs a non-empty string value. " +
                           "Use clearSecretAttributes to clear a secret.";
                case SecretInputKind.Invalid:
                    return $"Secret attribute '{item.AttributePath}' only accepts a string value.";
            }

            if (policy == SecretWritePolicy.RefuseSecretValues)
            {
                return $"Attribute '{item.AttributePath}' is a Secret attribute. Setting a secret is a high-risk " +
                       "operation and is not done by this tool: call set_entity_secrets for an existing entity, or " +
                       "create_entity_with_secrets to create an entity together with its secrets.";
            }

            effectiveData.Add(new AttributeUpdateItem { AttributePath = item.AttributePath, Value = text });
        }

        return null;
    }

    private enum SecretInputKind
    {
        Unchanged,
        Value,
        Invalid
    }

    private static SecretInputKind ClassifySecretInput(object? value, out string? text)
    {
        text = null;
        switch (value)
        {
            case null:
                return SecretInputKind.Unchanged;
            case string s:
                text = s;
                return s.Length == 0 ? SecretInputKind.Unchanged : SecretInputKind.Value;
            case JsonElement json:
                switch (json.ValueKind)
                {
                    case JsonValueKind.Null:
                    case JsonValueKind.Undefined:
                    case JsonValueKind.Object: // echoed read marker { "isSet": ... }
                        return SecretInputKind.Unchanged;
                    case JsonValueKind.String:
                        text = json.GetString();
                        return string.IsNullOrEmpty(text) ? SecretInputKind.Unchanged : SecretInputKind.Value;
                    default:
                        return SecretInputKind.Invalid;
                }
            case IDictionary<string, object?>:
            case OctoSecretStateDto:
                return SecretInputKind.Unchanged;
            default:
                return SecretInputKind.Invalid;
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
                case string stringValue:
                    value = stringValue;
                    break;
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