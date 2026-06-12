using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Meshmakers.Octo.Backend.McpServices.Options;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Backend.McpServices.Services;

/// <inheritdoc />
public sealed class RuntimeGraphqlIntrospectionClient(
    IHttpClientFactory httpClientFactory,
    IOptions<OctoServiceUrlOptions> urlOptions,
    ILogger<RuntimeGraphqlIntrospectionClient> logger) : IRuntimeGraphqlIntrospectionClient
{
    // Full introspection query — same shape the JS reference client uses
    // (graphql-js getIntrospectionQuery() with descriptions + directives).
    // Pinned here so the response includes every field graphql-codegen needs to
    // build typed client code: type kinds, field args, input field defaults,
    // enum values, interfaces, possible types, directives.
    private const string IntrospectionQuery = """
        query IntrospectionQuery {
          __schema {
            queryType { name }
            mutationType { name }
            subscriptionType { name }
            types { ...FullType }
            directives {
              name description
              locations
              args { ...InputValue }
            }
          }
        }
        fragment FullType on __Type {
          kind name description
          fields(includeDeprecated: true) {
            name description
            args { ...InputValue }
            type { ...TypeRef }
            isDeprecated deprecationReason
          }
          inputFields { ...InputValue }
          interfaces { ...TypeRef }
          enumValues(includeDeprecated: true) { name description isDeprecated deprecationReason }
          possibleTypes { ...TypeRef }
        }
        fragment InputValue on __InputValue {
          name description type { ...TypeRef } defaultValue
        }
        fragment TypeRef on __Type {
          kind name
          ofType { kind name ofType { kind name ofType { kind name ofType { kind name ofType { kind name ofType { kind name ofType { kind name } } } } } } }
        }
        """;

    /// <inheritdoc />
    public async Task<RuntimeGraphqlIntrospectionResult> FetchAsync(
        string accessToken,
        string tenantId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        var assetBase = urlOptions.Value.AssetServiceUrl;
        if (string.IsNullOrWhiteSpace(assetBase))
        {
            return new RuntimeGraphqlIntrospectionResult
            {
                Outcome = RuntimeGraphqlIntrospectionOutcome.NotReachable,
                ErrorMessage = "OctoServiceUrls.AssetServiceUrl is not configured on the MCP service.",
            };
        }

        // The runtime GraphQL endpoint is mounted at /tenants/{tenantId}/graphQl on
        // asset-services — confirmed against octo-asset-repo-services/Configuration/
        // OctoApplicationBuilderExtensions.cs.
        var url = $"{assetBase.TrimEnd('/')}/tenants/{Uri.EscapeDataString(tenantId)}/graphQl";

        var client = httpClientFactory.CreateClient("identity");
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Content = JsonContent.Create(new IntrospectionQueryBody
        {
            Query = IntrospectionQuery,
            OperationName = "IntrospectionQuery",
        });

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(ex,
                "Runtime GraphQL introspection unreachable for tenant {Tenant} at {Url}.",
                tenantId, url);
            return new RuntimeGraphqlIntrospectionResult
            {
                Outcome = RuntimeGraphqlIntrospectionOutcome.NotReachable,
                ErrorMessage = $"asset-services GraphQL endpoint unreachable: {ex.Message}",
            };
        }

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return new RuntimeGraphqlIntrospectionResult
            {
                Outcome = RuntimeGraphqlIntrospectionOutcome.Unauthorised,
                ErrorMessage =
                    $"asset-services rejected the introspection request ({(int)response.StatusCode}). " +
                    "The session's access token may be expired or missing the tenant scope.",
            };
        }

        if (!response.IsSuccessStatusCode)
        {
            var preview = await ReadBodyPreviewAsync(response, cancellationToken);
            return new RuntimeGraphqlIntrospectionResult
            {
                Outcome = RuntimeGraphqlIntrospectionOutcome.NotReachable,
                ErrorMessage =
                    $"asset-services returned HTTP {(int)response.StatusCode}: {preview}",
            };
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        int? typeCount = null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            // Standard GraphQL introspection envelope: { "data": { "__schema": { "types": [...] } } }.
            if (doc.RootElement.TryGetProperty("data", out var data) &&
                data.TryGetProperty("__schema", out var schema) &&
                schema.TryGetProperty("types", out var types) &&
                types.ValueKind == JsonValueKind.Array)
            {
                typeCount = types.GetArrayLength();
            }
            else if (doc.RootElement.TryGetProperty("errors", out var errors))
            {
                return new RuntimeGraphqlIntrospectionResult
                {
                    Outcome = RuntimeGraphqlIntrospectionOutcome.UnexpectedError,
                    ErrorMessage = $"asset-services returned GraphQL errors: {errors.GetRawText()}",
                };
            }
            else
            {
                return new RuntimeGraphqlIntrospectionResult
                {
                    Outcome = RuntimeGraphqlIntrospectionOutcome.UnexpectedError,
                    ErrorMessage = "asset-services response did not contain data.__schema.types[].",
                };
            }
        }
        catch (JsonException ex)
        {
            return new RuntimeGraphqlIntrospectionResult
            {
                Outcome = RuntimeGraphqlIntrospectionOutcome.UnexpectedError,
                ErrorMessage = $"asset-services response was not valid JSON: {ex.Message}",
            };
        }

        return new RuntimeGraphqlIntrospectionResult
        {
            Outcome = RuntimeGraphqlIntrospectionOutcome.Succeeded,
            Json = json,
            TypeCount = typeCount,
        };
    }

    private static async Task<string> ReadBodyPreviewAsync(
        HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            return body.Length > 400 ? body[..400] + "…" : body;
        }
        catch
        {
            return "<unable to read body>";
        }
    }

    private sealed class IntrospectionQueryBody
    {
        public required string Query { get; init; }
        public required string OperationName { get; init; }
    }
}
