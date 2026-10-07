using Meshmakers.Octo.Runtime.Contracts.Secrets;

namespace Meshmakers.Octo.Backend.McpServices.Services;

/// <summary>
///     Turns the engine's Secret-attribute exceptions (AB#5528) into stable, actionable tool messages. The
///     codes mirror the asset repository GraphQL error codes so an AI client sees the same vocabulary on
///     both APIs. Messages never contain secret values — the engine exceptions do not carry them.
/// </summary>
internal static class SecretErrors
{
    /// <summary>Error code for a query/filter/sort/aggregation/group-by on a Secret attribute.</summary>
    public const string NotQueryableCode = "SecretAttributeNotQueryable";

    /// <summary>Error code when the host has no secret key ring configured.</summary>
    public const string EncryptionNotConfiguredCode = "SecretEncryptionNotConfigured";

    /// <summary>
    ///     Returns a tool error message for a Secret-related exception anywhere in the inner-exception
    ///     chain, or <c>null</c> when the exception is unrelated.
    /// </summary>
    public static string? TryDescribe(Exception ex)
    {
        for (var current = ex; current != null; current = current.InnerException)
        {
            switch (current)
            {
                case SecretAttributeNotQueryableException notQueryable:
                    return $"{NotQueryableCode}: {notQueryable.Message} " +
                           "Use only the IsNull / IsNotNull filter operators on Secret attributes; they cannot be " +
                           "sorted, searched, aggregated, grouped or used as query columns.";
                case SecretEncryptionNotConfiguredException:
                    return $"{EncryptionNotConfiguredCode}: {current.Message}";
            }

            if (current is AggregateException { InnerExceptions.Count: > 0 } aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions)
                {
                    var described = TryDescribe(inner);
                    if (described != null)
                    {
                        return described;
                    }
                }
            }
        }

        return null;
    }

    /// <summary>
    ///     Returns the Secret-specific description when available, otherwise <see cref="Exception.Message" />.
    /// </summary>
    public static string Describe(Exception ex)
    {
        return TryDescribe(ex) ?? ex.Message;
    }
}
