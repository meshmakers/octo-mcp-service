using Meshmakers.Octo.Backend.McpServices.Models.Filters;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;

namespace Meshmakers.Octo.Backend.McpServices.Utils;

/// <summary>
/// Builder class for creating database layer filters from MCP DTOs
/// </summary>
public class McpFilterBuilder
{
    /// <summary>
    /// Creates a FieldFilter from a MCP FieldFilterDto
    /// </summary>
    /// <param name="mcpFieldFilter">The MCP field filter DTO</param>
    /// <returns>A database layer FieldFilter</returns>
    /// <exception cref="ArgumentNullException">Thrown when mcpFieldFilter is null</exception>
    /// <exception cref="ArgumentException">Thrown when FieldPath is null or empty</exception>
    public static FieldFilter CreateFieldFilter(FieldFilterDto mcpFieldFilter)
    {
        if (mcpFieldFilter == null)
            throw new ArgumentNullException(nameof(mcpFieldFilter));

        if (string.IsNullOrEmpty(mcpFieldFilter.FieldPath))
            throw new ArgumentException("FieldPath is required", nameof(mcpFieldFilter));

        var fieldOperator = ConvertOperator(mcpFieldFilter.Operator);

        return new FieldFilter(
            mcpFieldFilter.FieldPath,
            fieldOperator,
            mcpFieldFilter.Value,
            mcpFieldFilter.SecondValue
        );
    }

    /// <summary>
    /// Creates an EntityFilter from a MCP EntityFilterDto
    /// </summary>
    /// <param name="mcpEntityFilter">The MCP entity filter DTO</param>
    /// <returns>A database layer EntityFilter</returns>
    /// <exception cref="ArgumentNullException">Thrown when mcpEntityFilter is null</exception>
    public static EntityFilter CreateEntityFilter(EntityFilterDto mcpEntityFilter)
    {
        if (mcpEntityFilter == null)
            throw new ArgumentNullException(nameof(mcpEntityFilter));

        var logicalOperator = ConvertLogicalOperator(mcpEntityFilter.Operator);
        var entityFilter = new EntityFilter(logicalOperator);

        // Convert field filters
        if (mcpEntityFilter.Fields?.Any() == true)
        {
            var fieldFilters = mcpEntityFilter.Fields.Select(CreateFieldFilter);
            entityFilter.AddFields(fieldFilters);
        }

        // Convert nested filters
        if (mcpEntityFilter.NestedFilters?.Any() == true)
        {
            var nestedFilters = mcpEntityFilter.NestedFilters.Select(CreateEntityFilter);
            entityFilter.AddNestedFilters(nestedFilters);
        }

        return entityFilter;
    }

    /// <summary>
    /// Creates a collection of FieldFilter from a collection of MCP FieldFilterDto
    /// </summary>
    /// <param name="mcpFieldFilters">The MCP field filter DTOs</param>
    /// <returns>A collection of database layer FieldFilter</returns>
    /// <exception cref="ArgumentNullException">Thrown when mcpFieldFilters is null</exception>
    public static IEnumerable<FieldFilter> CreateFieldFilters(IEnumerable<FieldFilterDto> mcpFieldFilters)
    {
        if (mcpFieldFilters == null)
            throw new ArgumentNullException(nameof(mcpFieldFilters));

        return mcpFieldFilters.Select(CreateFieldFilter);
    }

    /// <summary>
    /// Creates a simple EntityFilter with AND logic from a collection of MCP FieldFilterDto
    /// </summary>
    /// <param name="mcpFieldFilters">The MCP field filter DTOs</param>
    /// <param name="logicalOperator">The logical operator to use (default: And)</param>
    /// <returns>A database layer EntityFilter</returns>
    /// <exception cref="ArgumentNullException">Thrown when mcpFieldFilters is null</exception>
    public static EntityFilter CreateEntityFilterFromFields(
        IEnumerable<FieldFilterDto> mcpFieldFilters,
        LogicalOperatorDto logicalOperator = LogicalOperatorDto.And)
    {
        if (mcpFieldFilters == null)
            throw new ArgumentNullException(nameof(mcpFieldFilters));

        var dbLogicalOperator = ConvertLogicalOperator(logicalOperator);
        var fieldFilters = CreateFieldFilters(mcpFieldFilters);
        
        return new EntityFilter(fieldFilters, dbLogicalOperator);
    }

    /// <summary>
    /// Converts a MCP FilterOperatorDto to a database layer FieldFilterOperator
    /// </summary>
    /// <param name="mcpOperator">The MCP operator to convert</param>
    /// <returns>The corresponding database layer operator</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the operator is not supported</exception>
    public static FieldFilterOperator ConvertOperator(FilterOperatorDto mcpOperator)
    {
        return mcpOperator switch
        {
            FilterOperatorDto.Equals => FieldFilterOperator.Equals,
            FilterOperatorDto.NotEquals => FieldFilterOperator.NotEquals,
            FilterOperatorDto.Contains => FieldFilterOperator.Contains,
            FilterOperatorDto.StartsWith => FieldFilterOperator.StartsWith,
            FilterOperatorDto.EndsWith => FieldFilterOperator.EndsWith,
            FilterOperatorDto.GreaterThan => FieldFilterOperator.GreaterThan,
            FilterOperatorDto.GreaterThanOrEqual => FieldFilterOperator.GreaterEqualThan,
            FilterOperatorDto.LessThan => FieldFilterOperator.LessThan,
            FilterOperatorDto.LessThanOrEqual => FieldFilterOperator.LessEqualThan,
            FilterOperatorDto.Between => FieldFilterOperator.Between,
            FilterOperatorDto.In => FieldFilterOperator.In,
            FilterOperatorDto.NotIn => FieldFilterOperator.NotIn,
            FilterOperatorDto.IsNull => FieldFilterOperator.IsNull,
            FilterOperatorDto.IsNotNull => FieldFilterOperator.IsNotNull,
            FilterOperatorDto.Regex => FieldFilterOperator.MatchRegEx,
            _ => throw new ArgumentOutOfRangeException(nameof(mcpOperator), mcpOperator, 
                $"Unsupported MCP operator: {mcpOperator}")
        };
    }

    /// <summary>
    /// Converts a MCP LogicalOperatorDto to a database layer LogicalOperator
    /// </summary>
    /// <param name="mcpOperator">The MCP logical operator to convert</param>
    /// <returns>The corresponding database layer logical operator</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the operator is not supported</exception>
    public static LogicalOperator ConvertLogicalOperator(LogicalOperatorDto mcpOperator)
    {
        return mcpOperator switch
        {
            LogicalOperatorDto.And => LogicalOperator.And,
            LogicalOperatorDto.Or => LogicalOperator.Or,
            _ => throw new ArgumentOutOfRangeException(nameof(mcpOperator), mcpOperator, 
                $"Unsupported MCP logical operator: {mcpOperator}")
        };
    }

    /// <summary>
    /// Converts a database layer FieldFilterOperator to a MCP FilterOperatorDto
    /// </summary>
    /// <param name="dbOperator">The database operator to convert</param>
    /// <returns>The corresponding MCP operator</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the operator is not supported</exception>
    public static FilterOperatorDto ConvertToMcpOperator(FieldFilterOperator dbOperator)
    {
        return dbOperator switch
        {
            FieldFilterOperator.Equals => FilterOperatorDto.Equals,
            FieldFilterOperator.NotEquals => FilterOperatorDto.NotEquals,
            FieldFilterOperator.Contains => FilterOperatorDto.Contains,
            FieldFilterOperator.StartsWith => FilterOperatorDto.StartsWith,
            FieldFilterOperator.EndsWith => FilterOperatorDto.EndsWith,
            FieldFilterOperator.GreaterThan => FilterOperatorDto.GreaterThan,
            FieldFilterOperator.GreaterEqualThan => FilterOperatorDto.GreaterThanOrEqual,
            FieldFilterOperator.LessThan => FilterOperatorDto.LessThan,
            FieldFilterOperator.LessEqualThan => FilterOperatorDto.LessThanOrEqual,
            FieldFilterOperator.Between => FilterOperatorDto.Between,
            FieldFilterOperator.In => FilterOperatorDto.In,
            FieldFilterOperator.NotIn => FilterOperatorDto.NotIn,
            FieldFilterOperator.IsNull => FilterOperatorDto.IsNull,
            FieldFilterOperator.IsNotNull => FilterOperatorDto.IsNotNull,
            FieldFilterOperator.MatchRegEx => FilterOperatorDto.Regex,
            // For backward compatibility, map older operators to closest MCP equivalent
            FieldFilterOperator.Like => FilterOperatorDto.Contains,
            FieldFilterOperator.AnyEq => FilterOperatorDto.Equals,
            FieldFilterOperator.AnyLike => FilterOperatorDto.Contains,
            FieldFilterOperator.Match => FilterOperatorDto.Equals,
            _ => throw new ArgumentOutOfRangeException(nameof(dbOperator), dbOperator, 
                $"Unsupported database operator: {dbOperator}")
        };
    }

    /// <summary>
    /// Converts a database layer LogicalOperator to a MCP LogicalOperatorDto
    /// </summary>
    /// <param name="dbOperator">The database logical operator to convert</param>
    /// <returns>The corresponding MCP logical operator</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the operator is not supported</exception>
    public static LogicalOperatorDto ConvertToMcpLogicalOperator(LogicalOperator dbOperator)
    {
        return dbOperator switch
        {
            LogicalOperator.And => LogicalOperatorDto.And,
            LogicalOperator.Or => LogicalOperatorDto.Or,
            _ => throw new ArgumentOutOfRangeException(nameof(dbOperator), dbOperator, 
                $"Unsupported database logical operator: {dbOperator}")
        };
    }

    /// <summary>
    /// Creates a MCP FieldFilterDto from a database layer FieldFilter
    /// </summary>
    /// <param name="fieldFilter">The database field filter</param>
    /// <returns>A MCP field filter DTO</returns>
    /// <exception cref="ArgumentNullException">Thrown when fieldFilter is null</exception>
    public static FieldFilterDto CreateMcpFieldFilter(FieldFilter fieldFilter)
    {
        if (fieldFilter == null)
            throw new ArgumentNullException(nameof(fieldFilter));

        return new FieldFilterDto
        {
            FieldPath = fieldFilter.AttributePath,
            Operator = ConvertToMcpOperator(fieldFilter.Operator),
            Value = fieldFilter.ComparisonValue,
            SecondValue = fieldFilter.SecondaryValue
        };
    }

    /// <summary>
    /// Creates a MCP EntityFilterDto from a database layer EntityFilter
    /// </summary>
    /// <param name="entityFilter">The database entity filter</param>
    /// <returns>A MCP entity filter DTO</returns>
    /// <exception cref="ArgumentNullException">Thrown when entityFilter is null</exception>
    public static EntityFilterDto CreateMcpEntityFilter(EntityFilter entityFilter)
    {
        if (entityFilter == null)
            throw new ArgumentNullException(nameof(entityFilter));

        return new EntityFilterDto
        {
            Operator = ConvertToMcpLogicalOperator(entityFilter.Operator),
            Fields = entityFilter.Fields.Select(CreateMcpFieldFilter).ToList(),
            NestedFilters = entityFilter.NestedFilters.Any() 
                ? entityFilter.NestedFilters.Select(CreateMcpEntityFilter).ToList() 
                : null
        };
    }
}