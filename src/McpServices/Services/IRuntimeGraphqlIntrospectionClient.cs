namespace Meshmakers.Octo.Backend.McpServices.Services;

/// <summary>
///     Performs a GraphQL introspection query against the tenant's runtime GraphQL endpoint
///     (asset-services) and returns the raw introspection JSON. Used by the
///     <c>export_runtime_graphql_sdl</c> MCP tool (M3 B-2c-schema-availability).
/// </summary>
public interface IRuntimeGraphqlIntrospectionClient
{
    /// <summary>
    ///     Fetch the introspection JSON for the tenant. Returns a typed outcome — the caller
    ///     never sees an exception.
    /// </summary>
    /// <param name="accessToken">Bearer token to authenticate against asset-services.</param>
    /// <param name="tenantId">Tenant whose GraphQL surface to introspect.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>
    ///     Outcome carries one of: <see cref="RuntimeGraphqlIntrospectionOutcome.Succeeded" />
    ///     with the JSON body in <see cref="RuntimeGraphqlIntrospectionResult.Json" />;
    ///     <see cref="RuntimeGraphqlIntrospectionOutcome.Unauthorised" /> for 401/403 from
    ///     asset-services; <see cref="RuntimeGraphqlIntrospectionOutcome.NotReachable" /> for
    ///     network failure or 5xx; <see cref="RuntimeGraphqlIntrospectionOutcome.UnexpectedError" />
    ///     for shape-mismatch on the response.
    /// </returns>
    Task<RuntimeGraphqlIntrospectionResult> FetchAsync(
        string accessToken,
        string tenantId,
        CancellationToken cancellationToken = default);
}

/// <summary>Typed outcome of <see cref="IRuntimeGraphqlIntrospectionClient.FetchAsync" />.</summary>
public enum RuntimeGraphqlIntrospectionOutcome
{
    /// <summary>HTTP 200 with a parseable introspection-query JSON body.</summary>
    Succeeded,

    /// <summary>HTTP 401 or 403 — token rejected.</summary>
    Unauthorised,

    /// <summary>Network failure, timeout, or HTTP 5xx from asset-services.</summary>
    NotReachable,

    /// <summary>Response body wasn't a recognisable introspection payload.</summary>
    UnexpectedError,
}

/// <summary>Result envelope of <see cref="IRuntimeGraphqlIntrospectionClient.FetchAsync" />.</summary>
public sealed class RuntimeGraphqlIntrospectionResult
{
    /// <summary>What happened.</summary>
    public required RuntimeGraphqlIntrospectionOutcome Outcome { get; init; }

    /// <summary>
    ///     On <see cref="RuntimeGraphqlIntrospectionOutcome.Succeeded" />, the raw
    ///     introspection JSON ready for the agent to write into <c>schema.json</c>.
    ///     Null on every other outcome.
    /// </summary>
    public string? Json { get; init; }

    /// <summary>
    ///     On <see cref="RuntimeGraphqlIntrospectionOutcome.Succeeded" />, the number of
    ///     types the introspection result enumerated — a sanity gauge for the agent's
    ///     trace + the tool's response message. Null on every other outcome.
    /// </summary>
    public int? TypeCount { get; init; }

    /// <summary>Human-readable error message when <see cref="Outcome" /> != Succeeded.</summary>
    public string? ErrorMessage { get; init; }
}
