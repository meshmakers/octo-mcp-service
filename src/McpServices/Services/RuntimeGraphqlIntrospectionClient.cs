using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
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

    // Sniff prefix used for the streaming-safe envelope check. The real introspection
    // response always starts with this exact byte sequence; if we don't see it in the
    // first 32 bytes, the body isn't an introspection result and we surface
    // UnexpectedError without trying to stream-parse it.
    private const string ExpectedJsonPrefix = "{\"data\":{\"__schema\":";

    /// <inheritdoc />
    public async Task<RuntimeGraphqlIntrospectionResult> FetchToStreamAsync(
        string accessToken,
        string tenantId,
        Stream output,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentNullException.ThrowIfNull(output);

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
            // Pass ResponseHeadersRead so we can start streaming the body before the full
            // payload lands in the HttpClient buffer. The full introspection response can be
            // 3-5 MB for a tenant with many CK types; not buffering it twice (HttpClient +
            // our String allocation) matters.
            response = await client.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
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

        using (response)
        {
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

            await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);

            // Sniff the first chunk for the expected envelope. If the body is a GraphQL
            // errors response (`{"errors":[…]}`), bail before we copy any of it into the
            // file the caller is going to register for download.
            var sniffBuffer = new byte[Math.Max(ExpectedJsonPrefix.Length * 2, 64)];
            var sniffRead = await responseStream.ReadAtLeastAsync(
                sniffBuffer, ExpectedJsonPrefix.Length, throwOnEndOfStream: false, cancellationToken);
            var sniffStr = System.Text.Encoding.UTF8.GetString(sniffBuffer, 0, sniffRead);
            if (!sniffStr.StartsWith(ExpectedJsonPrefix, StringComparison.Ordinal))
            {
                // Surface GraphQL errors verbatim if present so the caller can log them; otherwise
                // include the raw prefix.
                return new RuntimeGraphqlIntrospectionResult
                {
                    Outcome = RuntimeGraphqlIntrospectionOutcome.UnexpectedError,
                    ErrorMessage = sniffStr.Contains("\"errors\"", StringComparison.Ordinal)
                        ? $"asset-services returned GraphQL errors: {sniffStr.Trim()}"
                        : $"asset-services response did not start with the expected envelope: {sniffStr.Trim()}",
                };
            }

            // Write the sniff prefix + stream the rest.
            await output.WriteAsync(sniffBuffer.AsMemory(0, sniffRead), cancellationToken);
            await responseStream.CopyToAsync(output, cancellationToken);
            await output.FlushAsync(cancellationToken);

            return new RuntimeGraphqlIntrospectionResult
            {
                Outcome = RuntimeGraphqlIntrospectionOutcome.Succeeded,
                ByteCount = output.CanSeek ? output.Position : null,
            };
        }
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
