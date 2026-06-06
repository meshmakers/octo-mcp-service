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

    /// <summary>Regular expression match (maps to engine MatchRegEx).</summary>
    Regex,

    /// <summary>
    ///     SQL-style wildcard string match — value uses <c>%</c> as the wildcard (e.g. <c>"%inverter%"</c>).
    ///     For substring/prefix/suffix without wildcards prefer Contains / StartsWith / EndsWith.
    /// </summary>
    Like,

    /// <summary>
    ///     Element-wise equality on a scalar-array attribute (engine <c>AnyEq</c>): true when at least one
    ///     element equals the comparison value. Use on array-typed CK attributes.
    /// </summary>
    AnyEq,

    /// <summary>
    ///     Element-wise SQL-style match on a scalar-array attribute (engine <c>AnyLike</c>): true when at
    ///     least one element matches the wildcard pattern.
    /// </summary>
    AnyLike
}