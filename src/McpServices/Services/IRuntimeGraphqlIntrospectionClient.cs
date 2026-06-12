namespace Meshmakers.Octo.Backend.McpServices.Services;

/// <summary>
///     Performs a GraphQL introspection query against the tenant's runtime GraphQL endpoint
///     (asset-services) and streams the raw introspection JSON into a caller-provided
///     <see cref="Stream" />. Used by the <c>export_runtime_graphql_sdl</c> MCP tool
///     (M3 B-2c-schema-availability). Streams instead of returning a string because a
///     tenant with many CK types easily produces 3-5 MB of introspection JSON — embedding
///     that in the JSON-RPC response would OOM the MCP pod on the serializer's escape pass.
/// </summary>
public interface IRuntimeGraphqlIntrospectionClient
{
    /// <summary>
    ///     Fetch the introspection JSON for the tenant and write the response body to
    ///     <paramref name="output" /> as bytes arrive. Returns a typed outcome — the caller
    ///     never sees an exception.
    /// </summary>
    /// <param name="accessToken">Bearer token to authenticate against asset-services.</param>
    /// <param name="tenantId">Tenant whose GraphQL surface to introspect.</param>
    /// <param name="output">
    ///     Stream to receive the response body. Caller owns it (open before, close after).
    ///     Position is NOT rewound; pass a fresh / truncated stream.
    /// </param>
    /// <param name="cancellationToken">Cancellation.</param>
    Task<RuntimeGraphqlIntrospectionResult> FetchToStreamAsync(
        string accessToken,
        string tenantId,
        Stream output,
        CancellationToken cancellationToken = default);
}

/// <summary>Typed outcome of <see cref="IRuntimeGraphqlIntrospectionClient.FetchToStreamAsync" />.</summary>
public enum RuntimeGraphqlIntrospectionOutcome
{
    /// <summary>HTTP 200 with a streamed body that started with the expected GraphQL envelope.</summary>
    Succeeded,

    /// <summary>HTTP 401 or 403 — token rejected.</summary>
    Unauthorised,

    /// <summary>Network failure, timeout, or HTTP 5xx from asset-services.</summary>
    NotReachable,

    /// <summary>Response body wasn't a recognisable introspection payload (sniffed prefix mismatch).</summary>
    UnexpectedError,
}

/// <summary>Result envelope of <see cref="IRuntimeGraphqlIntrospectionClient.FetchToStreamAsync" />.</summary>
public sealed class RuntimeGraphqlIntrospectionResult
{
    /// <summary>What happened.</summary>
    public required RuntimeGraphqlIntrospectionOutcome Outcome { get; init; }

    /// <summary>
    ///     On <see cref="RuntimeGraphqlIntrospectionOutcome.Succeeded" />, the number of
    ///     bytes streamed to the output. Null on every other outcome.
    /// </summary>
    public long? ByteCount { get; init; }

    /// <summary>Human-readable error message when <see cref="Outcome" /> != Succeeded.</summary>
    public string? ErrorMessage { get; init; }
}
