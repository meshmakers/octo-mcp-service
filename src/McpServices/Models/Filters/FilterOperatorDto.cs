namespace Meshmakers.Octo.Backend.McpServices.Models.Filters;

/// <summary>
///     Available filter operators for fields
/// </summary>
public enum FilterOperatorDto
{
    /// <summary>Equality</summary>
    Equals,

    /// <summary>Inequality</summary>
    NotEquals,

    /// <summary>Contains substring</summary>
    Contains,

    /// <summary>Starts with</summary>
    StartsWith,

    /// <summary>Ends with</summary>
    EndsWith,

    /// <summary>Greater than</summary>
    GreaterThan,

    /// <summary>Greater than or equal</summary>
    GreaterThanOrEqual,

    /// <summary>Less than</summary>
    LessThan,

    /// <summary>Less than or equal</summary>
    LessThanOrEqual,

    /// <summary>Between two values</summary>
    Between,

    /// <summary>In list of values</summary>
    In,

    /// <summary>Not in list of values</summary>
    NotIn,

    /// <summary>Is NULL</summary>
    IsNull,

    /// <summary>Is not NULL</summary>
    IsNotNull,

    /// <summary>Regular expression</summary>
    Regex
}