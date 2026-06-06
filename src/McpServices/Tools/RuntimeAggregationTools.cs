using System.ComponentModel;
using Meshmakers.Octo.Backend.McpServices.Models.Aggregation;
using Meshmakers.Octo.Backend.McpServices.Models.Filters;
using Meshmakers.Octo.Backend.McpServices.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v2;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using ModelContextProtocol.Server;

// ReSharper disable UnusedMember.Global

namespace Meshmakers.Octo.Backend.McpServices.Tools;

/// <summary>
///     Runtime entity aggregation tools — transient aggregation + grouped aggregation.
///     Mirrors GraphQL <c>Runtime.TransientQuery.Aggregation</c> + <c>.GroupingAggregation</c>.
/// </summary>
[McpServerToolType]
public sealed class RuntimeAggregationTools
{
    /// <summary>Execute a persisted runtime query by its RtId.</summary>
    [McpServerTool(Name = "execute_runtime_query")]
    [Description(
        "Execute a persisted runtime query (RtPersistentQuery) by its RtId. Loads the entity and " +
        "dispatches on its CK subtype: RtSimpleRtQuery returns entity DTOs filtered to the configured " +
        "columns; RtAggregationRtQuery returns one row with the persisted aggregation columns; " +
        "RtGroupingAggregationRtQuery returns one row per distinct group. Optional extraFilters are " +
        "AND-combined with the persisted query's field filters. Mirrors GraphQL Runtime.RuntimeQuery.")]
    public static async Task<PersistedRuntimeQueryResponse> ExecuteRuntimeQuery(
        McpServer server,
        [Description("Runtime id of the persisted RtPersistentQuery entity to execute.")] string queryRtId,
        [Description("Optional additional field filters AND-combined with the persisted query's filters.")]
        FieldFilterCriteriaDto? extraFilters = null,
        [Description("Optional skip (offset) — only honored for RtSimpleRtQuery.")] int? skip = null,
        [Description("Optional take (limit) — only honored for RtSimpleRtQuery.")] int? take = null,
        [Description("Tenant id. Falls back to URL route.")] string? tenantId = null)
    {
        if (string.IsNullOrWhiteSpace(queryRtId))
        {
            return new PersistedRuntimeQueryResponse
            {
                IsSuccess = false,
                ErrorMessage = "queryRtId is required."
            };
        }

        var tenantResolution = server.Services!.GetRequiredService<ITenantResolutionService>();
        var tenantRepository = await tenantResolution.GetTenantRepositoryAsync(tenantId);
        var resolvedTenantId = tenantRepository.TenantId;
        var rtEntityToDtoMapper = server.Services!.GetRequiredService<IRtEntityToDtoMapper>();

        using var session = await tenantRepository.GetSessionAsync();
        session.StartTransaction();

        try
        {
            var queryId = new OctoObjectId(queryRtId);
            var rtQuery = await tenantRepository.GetRtEntityByRtIdAsync<RtPersistentQuery>(session, queryId);
            if (rtQuery == null)
            {
                return new PersistedRuntimeQueryResponse
                {
                    IsSuccess = false,
                    ErrorMessage = $"Persisted query '{queryRtId}' not found.",
                    QueryRtId = queryRtId,
                    TenantId = resolvedTenantId
                };
            }

            return rtQuery switch
            {
                RtSimpleRtQuery simple => await ExecutePersistedSimpleAsync(
                    simple, tenantRepository, session, rtEntityToDtoMapper,
                    extraFilters, skip, take, queryRtId, resolvedTenantId),
                RtAggregationRtQuery aggregation => await ExecutePersistedAggregationAsync(
                    aggregation, tenantRepository, session, extraFilters, queryRtId, resolvedTenantId),
                RtGroupingAggregationRtQuery grouping => await ExecutePersistedGroupingAsync(
                    grouping, tenantRepository, session, extraFilters, queryRtId, resolvedTenantId),
                _ => new PersistedRuntimeQueryResponse
                {
                    IsSuccess = false,
                    ErrorMessage = $"Unknown persisted query subtype: {rtQuery.GetType().Name}",
                    QueryRtId = queryRtId,
                    QuerySubtype = rtQuery.GetType().Name,
                    TenantId = resolvedTenantId
                }
            };
        }
        catch (Exception ex)
        {
            return new PersistedRuntimeQueryResponse
            {
                IsSuccess = false,
                ErrorMessage = ex.Message,
                QueryRtId = queryRtId,
                TenantId = resolvedTenantId
            };
        }
    }

    /// <summary>Execute a transient aggregation query over runtime entities of a CK type.</summary>
    [McpServerTool(Name = "query_entities_aggregation")]
    [Description(
        "Execute a transient aggregation query over runtime entities of the given CK type. Returns a single " +
        "row with the requested aggregations under their aliases (or default names like 'sum_Power'). Use " +
        "query_entities_grouping if you want groups. Equivalent to GraphQL Runtime.TransientQuery.Aggregation.")]
    public static async Task<AggregationResultResponse> QueryEntitiesAggregation(
        McpServer server,
        [Description("CK type id, e.g. 'EnergyCommunity-1.0.0/Sensor-1.0.0'.")] string ckTypeId,
        [Description("Aggregation columns to compute. At least one required.")]
        List<AggregationColumnDto> aggregations,
        [Description("Optional filter (And/Or with nested support).")] FieldFilterCriteriaDto? filters = null,
        [Description("Tenant id. Falls back to URL route.")] string? tenantId = null)
        => await RunQuery(server, ckTypeId, aggregations, filters, groupByAttributePaths: null, tenantId);

    /// <summary>Execute a transient grouped aggregation query.</summary>
    [McpServerTool(Name = "query_entities_grouping")]
    [Description(
        "Execute a transient grouped-aggregation query. Returns one row per distinct combination of " +
        "groupByAttributePaths values; each row carries the group-key columns plus the aggregation aliases. " +
        "Equivalent to GraphQL Runtime.TransientQuery.GroupingAggregation.")]
    public static async Task<AggregationResultResponse> QueryEntitiesGrouping(
        McpServer server,
        [Description("CK type id.")] string ckTypeId,
        [Description("Attribute paths to group by (e.g. ['FacilityId', 'Region']).")]
        List<string> groupByAttributePaths,
        [Description("Aggregation columns to compute. At least one required.")]
        List<AggregationColumnDto> aggregations,
        [Description("Optional filter (And/Or with nested support).")] FieldFilterCriteriaDto? filters = null,
        [Description("Tenant id. Falls back to URL route.")] string? tenantId = null)
        => await RunQuery(server, ckTypeId, aggregations, filters, groupByAttributePaths, tenantId);

    private static async Task<AggregationResultResponse> RunQuery(
        McpServer server,
        string ckTypeId,
        List<AggregationColumnDto>? aggregations,
        FieldFilterCriteriaDto? filters,
        List<string>? groupByAttributePaths,
        string? tenantId)
    {
        if (string.IsNullOrWhiteSpace(ckTypeId))
        {
            return new AggregationResultResponse { IsSuccess = false, ErrorMessage = "ckTypeId is required." };
        }

        var aggError = AggregationMapper.Validate(aggregations);
        if (aggError != null)
        {
            return new AggregationResultResponse { IsSuccess = false, ErrorMessage = aggError };
        }

        if (groupByAttributePaths != null)
        {
            var grpError = AggregationMapper.ValidateGroupBy(groupByAttributePaths);
            if (grpError != null)
            {
                return new AggregationResultResponse { IsSuccess = false, ErrorMessage = grpError };
            }
        }

        var tenantResolution = server.Services!.GetRequiredService<ITenantResolutionService>();
        var tenantRepository = await tenantResolution.GetTenantRepositoryAsync(tenantId);
        var resolvedTenantId = tenantRepository.TenantId;

        using var session = await tenantRepository.GetSessionAsync();
        session.StartTransaction();

        try
        {
            await tenantRepository.GetCkTypeGraphAsync(new RtCkId<CkTypeId>(ckTypeId));

            var queryOperation = RtEntityQueryOptions.Create();

            if (filters != null)
            {
                BuildTypedFilters(filters, queryOperation);
            }

            // Either grouped (Field aggregation) or scalar (Result aggregation), exclusive.
            AggregationInput aggregationInput = groupByAttributePaths != null
                ? queryOperation.AggregateFieldGroupBy(groupByAttributePaths.ToArray())
                : queryOperation.AggregateResult();

            AggregationMapper.ApplyToAggregationInput(aggregationInput, aggregations!);

            // Engine call. Limit=0 because we don't want entity rows; the aggregation result is on the
            // result set even when no items are returned.
            var results = await tenantRepository.GetRtEntitiesByTypeAsync(
                session,
                new RtCkId<CkTypeId>(ckTypeId),
                queryOperation,
                skip: 0,
                take: 0);

            var rows = groupByAttributePaths != null
                ? ProjectGroupedResults(results.FieldAggregationResult, aggregations!)
                : ProjectScalarResult(results.AggregationResult, aggregations!);

            return new AggregationResultResponse
            {
                IsSuccess = true,
                TenantId = resolvedTenantId,
                Rows = rows,
                RowCount = rows.Count,
                Message = rows.Count == 0
                    ? "No rows produced (possibly no entities matched the filters)."
                    : $"{rows.Count} row(s) produced."
            };
        }
        catch (Exception ex)
        {
            return new AggregationResultResponse { IsSuccess = false, ErrorMessage = ex.Message };
        }
    }

    private static List<Dictionary<string, object?>> ProjectScalarResult(
        AggregationResult? scalar,
        IReadOnlyList<AggregationColumnDto> columns)
    {
        if (scalar == null)
        {
            return [];
        }

        var row = BuildAggregationRow(scalar, columns);
        return [row];
    }

    private static List<Dictionary<string, object?>> ProjectGroupedResults(
        IEnumerable<FieldAggregationResult>? groups,
        IReadOnlyList<AggregationColumnDto> columns)
    {
        if (groups == null)
        {
            return [];
        }

        var rows = new List<Dictionary<string, object?>>();
        foreach (var group in groups)
        {
            var row = BuildAggregationRow(group, columns);

            // Prepend the group-by key columns.
            var keys = group.Keys.ToList();
            var paths = group.GroupByAttributePaths.ToList();
            for (var i = 0; i < paths.Count; i++)
            {
                var key = paths[i];
                var value = i < keys.Count ? keys[i] : null;
                row[key] = value;
            }

            rows.Add(row);
        }

        return rows;
    }

    private static Dictionary<string, object?> BuildAggregationRow(
        AggregationResult source,
        IReadOnlyList<AggregationColumnDto> columns)
    {
        var row = new Dictionary<string, object?>();

        foreach (var col in columns)
        {
            var alias = AggregationMapper.DeriveAlias(col);
            object? value = col.Function switch
            {
                AggregationFunctionDto.count when string.IsNullOrWhiteSpace(col.AttributePath) =>
                    source.Count,
                AggregationFunctionDto.count => FindStat(source.CountStatistics, col.AttributePath!),
                AggregationFunctionDto.sum => FindStat(source.SumStatistics, col.AttributePath!),
                AggregationFunctionDto.avg => FindStat(source.AvgStatistics, col.AttributePath!),
                AggregationFunctionDto.min => FindStat(source.MinStatistics, col.AttributePath!),
                AggregationFunctionDto.max => FindStat(source.MaxStatistics, col.AttributePath!),
                _ => null
            };

            row[alias] = value;
        }

        return row;
    }

    private static object? FindStat(IEnumerable<StatisticsResult> stats, string attributePath) =>
        stats.FirstOrDefault(s =>
            string.Equals(s.AttributePath, attributePath, StringComparison.OrdinalIgnoreCase))
            ?.Value;

    // ── Persisted-query executors ────────────────────────────────────────────────────────────

    private static async Task<PersistedRuntimeQueryResponse> ExecutePersistedSimpleAsync(
        RtSimpleRtQuery query,
        ITenantRepository tenantRepository,
        IOctoSession session,
        IRtEntityToDtoMapper rtEntityToDtoMapper,
        FieldFilterCriteriaDto? extraFilters,
        int? skip,
        int? take,
        string queryRtId,
        string resolvedTenantId)
    {
        var ckTypeId = query.QueryCkTypeId;
        var queryOptions = RtEntityQueryOptions.Create();

        ApplyPersistedFieldFilters(query.FieldFilter, queryOptions);
        if (extraFilters != null)
        {
            BuildTypedFilters(extraFilters, queryOptions);
        }

        var results = await tenantRepository.GetRtEntitiesByTypeAsync(
            session, ckTypeId, queryOptions, skip, take);

        var entities = results.Items
            .Select(e => rtEntityToDtoMapper.ConvertToDto(
                resolvedTenantId, e, AttributeValueResolveFlags.ResolveEnumsToNames))
            .ToList();

        // Filter projected attributes to the persisted column list — keeps response shape tight.
        var columnPaths = query.Columns?.ToList() ?? [];
        if (columnPaths.Count > 0)
        {
            var pathSet = new HashSet<string>(columnPaths, StringComparer.OrdinalIgnoreCase);
            foreach (var entity in entities)
            {
                RuntimeEntityCrudTools.FilterAttributes(entity, pathSet);
            }
        }

        return new PersistedRuntimeQueryResponse
        {
            IsSuccess = true,
            TenantId = resolvedTenantId,
            QueryRtId = queryRtId,
            QuerySubtype = nameof(RtSimpleRtQuery),
            CkTypeId = ckTypeId.ToString(),
            Entities = entities,
            RowCount = entities.Count,
            TotalCount = results.TotalCount
        };
    }

    private static async Task<PersistedRuntimeQueryResponse> ExecutePersistedAggregationAsync(
        RtAggregationRtQuery query,
        ITenantRepository tenantRepository,
        IOctoSession session,
        FieldFilterCriteriaDto? extraFilters,
        string queryRtId,
        string resolvedTenantId)
    {
        var ckTypeId = query.QueryCkTypeId;
        var columns = MapPersistedAggregationColumns(query.Columns);

        var queryOptions = RtEntityQueryOptions.Create();
        ApplyPersistedFieldFilters(query.FieldFilter, queryOptions);
        if (extraFilters != null)
        {
            BuildTypedFilters(extraFilters, queryOptions);
        }

        var aggregationInput = queryOptions.AggregateResult();
        AggregationMapper.ApplyToAggregationInput(aggregationInput, columns);

        var results = await tenantRepository.GetRtEntitiesByTypeAsync(
            session, ckTypeId, queryOptions, skip: 0, take: 0);

        var rows = ProjectScalarResult(results.AggregationResult, columns);

        return new PersistedRuntimeQueryResponse
        {
            IsSuccess = true,
            TenantId = resolvedTenantId,
            QueryRtId = queryRtId,
            QuerySubtype = nameof(RtAggregationRtQuery),
            CkTypeId = ckTypeId.ToString(),
            Rows = rows,
            RowCount = rows.Count
        };
    }

    private static async Task<PersistedRuntimeQueryResponse> ExecutePersistedGroupingAsync(
        RtGroupingAggregationRtQuery query,
        ITenantRepository tenantRepository,
        IOctoSession session,
        FieldFilterCriteriaDto? extraFilters,
        string queryRtId,
        string resolvedTenantId)
    {
        var ckTypeId = query.QueryCkTypeId;
        var groupBy = query.GroupingColumns?.ToArray() ?? [];
        if (groupBy.Length == 0)
        {
            return new PersistedRuntimeQueryResponse
            {
                IsSuccess = false,
                ErrorMessage = "Persisted grouping aggregation query has no GroupingColumns.",
                QueryRtId = queryRtId,
                QuerySubtype = nameof(RtGroupingAggregationRtQuery),
                CkTypeId = ckTypeId.ToString(),
                TenantId = resolvedTenantId
            };
        }

        var columns = MapPersistedAggregationColumns(query.Columns);

        var queryOptions = RtEntityQueryOptions.Create();
        ApplyPersistedFieldFilters(query.FieldFilter, queryOptions);
        if (extraFilters != null)
        {
            BuildTypedFilters(extraFilters, queryOptions);
        }

        var aggregationInput = queryOptions.AggregateFieldGroupBy(groupBy);
        AggregationMapper.ApplyToAggregationInput(aggregationInput, columns);

        var results = await tenantRepository.GetRtEntitiesByTypeAsync(
            session, ckTypeId, queryOptions, skip: 0, take: 0);

        var rows = ProjectGroupedResults(results.FieldAggregationResult, columns);

        return new PersistedRuntimeQueryResponse
        {
            IsSuccess = true,
            TenantId = resolvedTenantId,
            QueryRtId = queryRtId,
            QuerySubtype = nameof(RtGroupingAggregationRtQuery),
            CkTypeId = ckTypeId.ToString(),
            Rows = rows,
            RowCount = rows.Count
        };
    }

    /// <summary>Maps the persisted AggregationQueryColumn list to the MCP-side DTO shape.</summary>
    internal static List<AggregationColumnDto> MapPersistedAggregationColumns(
        IEnumerable<RtAggregationQueryColumnRecord>? persistedColumns)
    {
        if (persistedColumns == null)
        {
            return [];
        }

        return persistedColumns
            .Select(c => new AggregationColumnDto
            {
                AttributePath = c.AttributePath,
                Function = AggregationMapper.MapCkAggregationName(c.AggregationType.ToString())
            })
            .ToList();
    }

    /// <summary>
    ///     Applies the persisted query's FieldFilter (a CK-encoded list of comparisons) onto the engine-side
    ///     <see cref="RtEntityQueryOptions"/>. The persisted operator enum aligns numerically with the engine
    ///     <see cref="FieldFilterOperator"/> — mirroring the cast pattern used in the GraphQL resolvers
    ///     (RtQueryDtoType.CreateRtQueryDto).
    /// </summary>
    private static void ApplyPersistedFieldFilters(
        IEnumerable<RtFieldFilterRecord>? persistedFilter,
        RtEntityQueryOptions queryOptions)
    {
        if (persistedFilter == null)
        {
            return;
        }

        foreach (var f in persistedFilter)
        {
            queryOptions.FieldFilter(f.AttributePath, (FieldFilterOperator)(int)f.Operator, f.ComparisonValue);
        }
    }

    // ── Filter binding — copied from RuntimeEntityCrudTools to avoid coupling ────────────────

    private static void BuildTypedFilters(FieldFilterCriteriaDto filterCriteriaDto,
        FieldFilterCriteria fieldFilterCriteria)
    {
        foreach (var filter in filterCriteriaDto.Fields)
        {
            switch (filter.Operator)
            {
                case FilterOperatorDto.Equals:
                    fieldFilterCriteria.FieldEquals(filter.AttributePath, filter.Value);
                    break;
                case FilterOperatorDto.NotEquals:
                    fieldFilterCriteria.FieldNotEquals(filter.AttributePath, filter.Value);
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
                case FilterOperatorDto.In when filter.Value is IEnumerable<object> values:
                    fieldFilterCriteria.FieldIn(filter.AttributePath, values);
                    break;
                case FilterOperatorDto.NotIn when filter.Value is IEnumerable<object> notInValues:
                    fieldFilterCriteria.FieldNotIn(filter.AttributePath, notInValues);
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
                case FilterOperatorDto.IsNull:
                    fieldFilterCriteria.FieldIsNull(filter.AttributePath);
                    break;
                case FilterOperatorDto.IsNotNull:
                    fieldFilterCriteria.FieldIsNotNull(filter.AttributePath);
                    break;
                case FilterOperatorDto.Regex:
                    fieldFilterCriteria.FieldMatchRegex(filter.AttributePath, filter.Value);
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
                // In/NotIn already handled above when filter.Value is an IEnumerable<object>.
                // Anything not matched at this point is a value-shape mismatch on In/NotIn
                // (caller passed a non-collection) — fall through silently to preserve the
                // legacy behavior of skipping that filter entry rather than failing the whole call.
                case FilterOperatorDto.In:
                case FilterOperatorDto.NotIn:
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(filter.Operator), filter.Operator,
                        $"Unknown filter operator: {filter.Operator}");
            }
        }

        if (filterCriteriaDto.NestedFilters?.Any() == true)
        {
            foreach (var nestedDto in filterCriteriaDto.NestedFilters)
            {
                var nested = FieldFilterCriteria.Create((LogicalOperators)nestedDto.Operator);
                BuildTypedFilters(nestedDto, nested);
                fieldFilterCriteria.AddNestedFilter(nested);
            }
        }
    }
}
