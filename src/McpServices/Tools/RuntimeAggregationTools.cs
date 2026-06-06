using System.ComponentModel;
using Meshmakers.Octo.Backend.McpServices.Models.Aggregation;
using Meshmakers.Octo.Backend.McpServices.Models.Filters;
using Meshmakers.Octo.Backend.McpServices.Services;
using Meshmakers.Octo.ConstructionKit.Contracts;
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
