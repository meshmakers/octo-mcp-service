using Meshmakers.Octo.Backend.McpServices.Models;
using Meshmakers.Octo.Backend.McpServices.Options;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;

namespace Meshmakers.Octo.Backend.McpServices.Services;

/// <summary>
///     Page size rules of the entity query tools (AB#8904, Feature AB#8793). The tools read the engine directly, so
///     the asset-repo API guard (AB#8898) never sees them: without a limit/take they use the default page size
///     (<see cref="DynamicToolOptions.DefaultQueryLimit" />, 100), a limit/take above the maximum
///     (<see cref="DynamicToolOptions.MaxQueryResultLimit" />, at most <see cref="DynamicToolOptions.MaxPageSize" />)
///     is refused before any engine query runs, never silently capped.
/// </summary>
internal sealed class QueryPaging
{
    private QueryPaging(int skip, int take, bool defaultTakeApplied, int maxTake, string skipArgument)
    {
        Skip = skip;
        Take = take;
        DefaultTakeApplied = defaultTakeApplied;
        MaxTake = maxTake;
        SkipArgument = skipArgument;
    }

    /// <summary>Items to skip.</summary>
    public int Skip { get; }

    /// <summary>Page size passed to the engine.</summary>
    public int Take { get; }

    /// <summary>True when the caller gave no page size.</summary>
    public bool DefaultTakeApplied { get; }

    /// <summary>Maximum page size.</summary>
    public int MaxTake { get; }

    /// <summary>Name of the tool's skip argument, used in the paging hint.</summary>
    public string SkipArgument { get; }

    /// <summary>
    ///     Validates the requested page. Returns the page, or an error message naming the limit; on an error the tool
    ///     must return it without querying the engine.
    /// </summary>
    /// <param name="server">MCP server, used to read <see cref="DynamicToolOptions" /> (defaults when not registered).</param>
    /// <param name="requestedTake">The limit/take argument of the tool, null when not given.</param>
    /// <param name="requestedSkip">The skip/offset argument of the tool, null when not given.</param>
    /// <param name="takeArgument">Name of the tool's limit/take argument, used in the error.</param>
    /// <param name="skipArgument">Name of the tool's skip/offset argument, used in the error and the hint.</param>
    public static (QueryPaging? Paging, string? Error) Resolve(McpServer server, int? requestedTake,
        int? requestedSkip, string takeArgument, string skipArgument)
    {
        var options = server.Services?.GetService<IOptions<DynamicToolOptions>>()?.Value ?? new DynamicToolOptions();
        var max = options.GetEffectiveMaxQueryResultLimit();

        if (requestedSkip is < 0)
        {
            return (null, $"{skipArgument} {requestedSkip} is negative; {skipArgument} must be 0 or greater.");
        }

        if (requestedTake is <= 0)
        {
            return (null, $"{takeArgument} {requestedTake} is invalid; {takeArgument} must be between 1 and {max}.");
        }

        if (requestedTake > max)
        {
            return (null,
                $"{takeArgument} {requestedTake} exceeds the maximum of {max}; page with {skipArgument} " +
                $"({takeArgument} <= {max}, {skipArgument} = 0, {max}, {2 * max}, ...).");
        }

        var take = requestedTake ?? options.GetEffectiveDefaultQueryLimit();
        return (new QueryPaging(requestedSkip ?? 0, take, requestedTake == null, max, skipArgument), null);
    }

    /// <summary>
    ///     Builds the paging block of a result page.
    /// </summary>
    /// <param name="returnedCount">Items the engine returned for this page.</param>
    /// <param name="totalCount">Total number of matching items if the engine reported it, else null.</param>
    public QueryPagingInfo ToInfo(int returnedCount, long? totalCount)
    {
        // Without a total count a full page means there may be more.
        var hasMore = totalCount.HasValue
            ? Skip + (long)returnedCount < totalCount.Value
            : returnedCount >= Take;
        var nextSkip = hasMore ? Skip + returnedCount : (int?)null;

        return new QueryPagingInfo
        {
            Skip = Skip,
            Take = Take,
            DefaultTakeApplied = DefaultTakeApplied,
            MaxTake = MaxTake,
            ReturnedCount = returnedCount,
            HasMore = hasMore,
            NextSkip = nextSkip,
            Hint = hasMore
                ? $"More items may exist. Call again with {SkipArgument}={nextSkip} to fetch the next page " +
                  $"(page size {Take}, maximum {MaxTake})."
                : null
        };
    }
}
