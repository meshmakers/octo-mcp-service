using Meshmakers.Octo.Backend.McpServices.Models.Filters;
using Meshmakers.Octo.Backend.McpServices.Utils;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;

namespace Meshmakers.Octo.Backend.McpServices.Extensions;

/// <summary>
/// Extension methods for working with MCP filters
/// </summary>
public static class FilterExtensions
{
    /// <summary>
    /// Converts a MCP FieldFilterDto to a database layer FieldFilter
    /// </summary>
    /// <param name="mcpFieldFilter">The MCP field filter to convert</param>
    /// <returns>A database layer FieldFilter</returns>
    public static FieldFilter ToFieldFilter(this FieldFilterDto mcpFieldFilter)
    {
        return McpFilterBuilder.CreateFieldFilter(mcpFieldFilter);
    }

    /// <summary>
    /// Converts a MCP EntityFilterDto to a database layer EntityFilter
    /// </summary>
    /// <param name="mcpEntityFilter">The MCP entity filter to convert</param>
    /// <returns>A database layer EntityFilter</returns>
    public static EntityFilter ToEntityFilter(this EntityFilterDto mcpEntityFilter)
    {
        return McpFilterBuilder.CreateEntityFilter(mcpEntityFilter);
    }

    /// <summary>
    /// Converts a collection of MCP FieldFilterDto to database layer FieldFilter
    /// </summary>
    /// <param name="mcpFieldFilters">The MCP field filters to convert</param>
    /// <returns>A collection of database layer FieldFilter</returns>
    public static IEnumerable<FieldFilter> ToFieldFilters(this IEnumerable<FieldFilterDto> mcpFieldFilters)
    {
        return McpFilterBuilder.CreateFieldFilters(mcpFieldFilters);
    }

    /// <summary>
    /// Converts a collection of MCP FieldFilterDto to a database layer EntityFilter with specified logical operator
    /// </summary>
    /// <param name="mcpFieldFilters">The MCP field filters to convert</param>
    /// <param name="logicalOperator">The logical operator to use (default: And)</param>
    /// <returns>A database layer EntityFilter</returns>
    public static EntityFilter ToEntityFilter(
        this IEnumerable<FieldFilterDto> mcpFieldFilters, 
        LogicalOperatorDto logicalOperator = LogicalOperatorDto.And)
    {
        return McpFilterBuilder.CreateEntityFilterFromFields(mcpFieldFilters, logicalOperator);
    }

    /// <summary>
    /// Converts a database layer FieldFilter to a MCP FieldFilterDto
    /// </summary>
    /// <param name="fieldFilter">The database field filter to convert</param>
    /// <returns>A MCP FieldFilterDto</returns>
    public static FieldFilterDto ToMcpFieldFilter(this FieldFilter fieldFilter)
    {
        return McpFilterBuilder.CreateMcpFieldFilter(fieldFilter);
    }

    /// <summary>
    /// Converts a database layer EntityFilter to a MCP EntityFilterDto
    /// </summary>
    /// <param name="entityFilter">The database entity filter to convert</param>
    /// <returns>A MCP EntityFilterDto</returns>
    public static EntityFilterDto ToMcpEntityFilter(this EntityFilter entityFilter)
    {
        return McpFilterBuilder.CreateMcpEntityFilter(entityFilter);
    }

    /// <summary>
    /// Converts a collection of database layer FieldFilter to MCP FieldFilterDto
    /// </summary>
    /// <param name="fieldFilters">The database field filters to convert</param>
    /// <returns>A collection of MCP FieldFilterDto</returns>
    public static IEnumerable<FieldFilterDto> ToMcpFieldFilters(this IEnumerable<FieldFilter> fieldFilters)
    {
        return fieldFilters.Select(ff => ff.ToMcpFieldFilter());
    }

    /// <summary>
    /// Checks if an EntityFilterDto has any conditions
    /// </summary>
    /// <param name="entityFilter">The entity filter to check</param>
    /// <returns>True if the filter has any field filters or nested filters</returns>
    public static bool HasConditions(this EntityFilterDto entityFilter)
    {
        return entityFilter?.Fields?.Any() == true || entityFilter?.NestedFilters?.Any() == true;
    }

    /// <summary>
    /// Adds a field filter to an EntityFilterDto
    /// </summary>
    /// <param name="entityFilter">The entity filter to add to</param>
    /// <param name="fieldFilter">The field filter to add</param>
    /// <returns>The same EntityFilterDto for method chaining</returns>
    public static EntityFilterDto AddField(this EntityFilterDto entityFilter, FieldFilterDto fieldFilter)
    {
        if (entityFilter == null)
            throw new ArgumentNullException(nameof(entityFilter));
        if (fieldFilter == null)
            throw new ArgumentNullException(nameof(fieldFilter));

        entityFilter.Fields.Add(fieldFilter);
        return entityFilter;
    }

    /// <summary>
    /// Adds multiple field filters to an EntityFilterDto
    /// </summary>
    /// <param name="entityFilter">The entity filter to add to</param>
    /// <param name="fieldFilters">The field filters to add</param>
    /// <returns>The same EntityFilterDto for method chaining</returns>
    public static EntityFilterDto AddFields(this EntityFilterDto entityFilter, IEnumerable<FieldFilterDto> fieldFilters)
    {
        if (entityFilter == null)
            throw new ArgumentNullException(nameof(entityFilter));
        if (fieldFilters == null)
            throw new ArgumentNullException(nameof(fieldFilters));

        entityFilter.Fields.AddRange(fieldFilters);
        return entityFilter;
    }

    /// <summary>
    /// Adds a nested filter to an EntityFilterDto
    /// </summary>
    /// <param name="entityFilter">The entity filter to add to</param>
    /// <param name="nestedFilter">The nested filter to add</param>
    /// <returns>The same EntityFilterDto for method chaining</returns>
    public static EntityFilterDto AddNestedFilter(this EntityFilterDto entityFilter, EntityFilterDto nestedFilter)
    {
        if (entityFilter == null)
            throw new ArgumentNullException(nameof(entityFilter));
        if (nestedFilter == null)
            throw new ArgumentNullException(nameof(nestedFilter));

        entityFilter.NestedFilters ??= new List<EntityFilterDto>();
        entityFilter.NestedFilters.Add(nestedFilter);
        return entityFilter;
    }

    /// <summary>
    /// Creates a simple field filter for equality comparison
    /// </summary>
    /// <param name="fieldPath">The field path</param>
    /// <param name="value">The value to compare</param>
    /// <returns>A new FieldFilterDto</returns>
    public static FieldFilterDto CreateEqualsFilter(string fieldPath, object? value)
    {
        return new FieldFilterDto
        {
            FieldPath = fieldPath,
            Operator = FilterOperatorDto.Equals,
            Value = value
        };
    }

    /// <summary>
    /// Creates a simple field filter for contains comparison
    /// </summary>
    /// <param name="fieldPath">The field path</param>
    /// <param name="value">The value to search for</param>
    /// <returns>A new FieldFilterDto</returns>
    public static FieldFilterDto CreateContainsFilter(string fieldPath, string value)
    {
        return new FieldFilterDto
        {
            FieldPath = fieldPath,
            Operator = FilterOperatorDto.Contains,
            Value = value
        };
    }

    /// <summary>
    /// Creates a simple field filter for range comparison (between)
    /// </summary>
    /// <param name="fieldPath">The field path</param>
    /// <param name="lowerValue">The lower bound value</param>
    /// <param name="upperValue">The upper bound value</param>
    /// <returns>A new FieldFilterDto</returns>
    public static FieldFilterDto CreateBetweenFilter(string fieldPath, object? lowerValue, object? upperValue)
    {
        return new FieldFilterDto
        {
            FieldPath = fieldPath,
            Operator = FilterOperatorDto.Between,
            Value = lowerValue,
            SecondValue = upperValue
        };
    }

    /// <summary>
    /// Creates a simple field filter for "in" comparison
    /// </summary>
    /// <param name="fieldPath">The field path</param>
    /// <param name="values">The values to check against</param>
    /// <returns>A new FieldFilterDto</returns>
    public static FieldFilterDto CreateInFilter(string fieldPath, IEnumerable<object?> values)
    {
        return new FieldFilterDto
        {
            FieldPath = fieldPath,
            Operator = FilterOperatorDto.In,
            Value = values?.ToList()
        };
    }

    /// <summary>
    /// Creates a simple field filter for null check
    /// </summary>
    /// <param name="fieldPath">The field path</param>
    /// <param name="isNull">True to check for null, false to check for not null</param>
    /// <returns>A new FieldFilterDto</returns>
    public static FieldFilterDto CreateNullFilter(string fieldPath, bool isNull = true)
    {
        return new FieldFilterDto
        {
            FieldPath = fieldPath,
            Operator = isNull ? FilterOperatorDto.IsNull : FilterOperatorDto.IsNotNull,
            Value = null
        };
    }
}