namespace Meshmakers.Octo.Backend.McpServices.Models;

/// <summary>
///     Paging state of an entity query tool result (AB#8904). Every entity query tool returns at most one page: the
///     default page size is 100, the maximum 1000. Use <see cref="NextSkip" /> to fetch the next page.
/// </summary>
public sealed class QueryPagingInfo
{
    /// <summary>
    ///     Number of items skipped before this page (the skip/offset that was applied).
    /// </summary>
    public required int Skip { get; init; }

    /// <summary>
    ///     Page size that was applied (the requested limit/take, or the default page size when none was given).
    /// </summary>
    public required int Take { get; init; }

    /// <summary>
    ///     True when the page size was not given and the default page size was applied.
    /// </summary>
    public required bool DefaultTakeApplied { get; init; }

    /// <summary>
    ///     Maximum page size the tool accepts. A larger limit/take is refused.
    /// </summary>
    public required int MaxTake { get; init; }

    /// <summary>
    ///     Number of items returned in this page.
    /// </summary>
    public required int ReturnedCount { get; init; }

    /// <summary>
    ///     True when more items may exist after this page.
    /// </summary>
    public required bool HasMore { get; init; }

    /// <summary>
    ///     Skip/offset value for the next page, or null when this is the last page.
    /// </summary>
    public int? NextSkip { get; init; }

    /// <summary>
    ///     Human-readable hint how to fetch the next page, or null when this is the last page.
    /// </summary>
    public string? Hint { get; init; }
}
