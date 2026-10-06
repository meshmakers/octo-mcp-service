using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Meshmakers.Octo.Backend.McpServices.Models;
using Meshmakers.Octo.Backend.McpServices.Options;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Backend.McpServices.Services;

/// <inheritdoc />
/// <remarks>
///     Same transport as <see cref="RuntimeGraphqlIntrospectionClient" />: a POST to
///     <c>{AssetServiceUrl}/tenants/{tenantId}/graphQl</c> on the "identity" named HttpClient. The SDK's
///     <c>TenantClient.SendQueryAsync</c> only reads single-connection responses, and the overview needs the
///     connection plus the sibling <c>summary</c> field, so the query is sent here with typed variables.
/// </remarks>
public sealed class RuntimeSecretInventoryClient(
    IHttpClientFactory httpClientFactory,
    IOptions<OctoServiceUrlOptions> urlOptions,
    ILogger<RuntimeSecretInventoryClient> logger) : IRuntimeSecretInventoryClient
{
    /// <summary>GraphQL error code of the asset repository when the AdminPanelManagement role is missing.</summary>
    internal const string ForbiddenCode = "Forbidden";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <inheritdoc />
    public async Task<SecretInventoryQueryResult> QueryAsync(
        string accessToken,
        string tenantId,
        SecretInventoryRequest query,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentNullException.ThrowIfNull(query);

        var assetBase = urlOptions.Value.AssetServiceUrl;
        if (string.IsNullOrWhiteSpace(assetBase))
        {
            return Fail(SecretInventoryQueryOutcome.NotReachable,
                "OctoServiceUrls.AssetServiceUrl is not configured on the MCP service.");
        }

        var url = $"{assetBase.TrimEnd('/')}/tenants/{Uri.EscapeDataString(tenantId)}/graphQl";
        var client = httpClientFactory.CreateClient("identity");
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Content = JsonContent.Create(new GraphQlBody
        {
            Query = BuildQuery(query.IncludeSummary, query.IncludeUsedBy),
            OperationName = "SecretInventory",
            Variables = new Dictionary<string, object?>
            {
                ["first"] = query.First,
                ["after"] = query.After,
                ["ckTypeId"] = query.CkTypeId,
                ["forms"] = query.Forms,
                ["needsReEntry"] = query.NeedsReEntry,
                ["search"] = query.Search
            }
        }, options: JsonOptions);

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Secret inventory query unreachable for tenant {Tenant} at {Url}.", tenantId, url);
            return Fail(SecretInventoryQueryOutcome.NotReachable,
                $"asset-services GraphQL endpoint unreachable: {ex.Message}");
        }

        using (response)
        {
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return Fail(SecretInventoryQueryOutcome.Unauthorised,
                    $"asset-services rejected the secret inventory request ({(int)response.StatusCode}). " +
                    "The session's access token may be expired or missing the tenant scope.");
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode && !body.TrimStart().StartsWith('{'))
            {
                return Fail(SecretInventoryQueryOutcome.NotReachable,
                    $"asset-services returned HTTP {(int)response.StatusCode}: {Preview(body)}");
            }

            GraphQlResponse? parsed;
            try
            {
                parsed = JsonSerializer.Deserialize<GraphQlResponse>(body, JsonOptions);
            }
            catch (JsonException ex)
            {
                return Fail(SecretInventoryQueryOutcome.UnexpectedError,
                    $"asset-services returned an unreadable response ({ex.Message}): {Preview(body)}");
            }

            var errors = parsed?.Errors ?? [];
            if (errors.Any(e => string.Equals(e.Extensions?.Code, ForbiddenCode, StringComparison.Ordinal)))
            {
                return Fail(SecretInventoryQueryOutcome.Forbidden,
                    "The secrets overview requires the 'AdminPanelManagement' role in tenant " +
                    $"'{tenantId}'. Ask a tenant administrator to grant it, or use get_secret_status for the " +
                    "sweep report.");
            }

            var secrets = parsed?.Data?.Secrets;
            if (errors.Count > 0 || secrets?.Inventory == null)
            {
                var message = errors.Count > 0
                    ? string.Join("; ", errors.Select(e => e.Message))
                    : Preview(body);
                return Fail(SecretInventoryQueryOutcome.UnexpectedError,
                    $"asset-services returned GraphQL errors: {message}");
            }

            return new SecretInventoryQueryResult
            {
                Outcome = SecretInventoryQueryOutcome.Succeeded,
                Inventory = secrets.Inventory,
                Summary = secrets.Summary
            };
        }
    }

    /// <summary>Builds the GraphQL document; <c>summary</c> and <c>usedBy</c> are selected only on request.</summary>
    internal static string BuildQuery(bool includeSummary, bool includeUsedBy)
    {
        var usedBy = includeUsedBy
            ? " usedBy { dataFlowRtId dataFlowName pipelineRtId pipelineName nodePath match }"
            : string.Empty;
        var summary = includeSummary
            ? " summary { total notSet plaintext encV1 encV2 keyMissing corrupt needsReEntry " +
              "encV2ByKeyId { keyId count } }"
            : string.Empty;
        return "query SecretInventory($first: Int, $after: String, $ckTypeId: String, " +
               "$forms: [SecretStorageForm!], $needsReEntry: Boolean, $search: String) { " +
               "secrets { inventory(first: $first, after: $after, ckTypeId: $ckTypeId, forms: $forms, " +
               "needsReEntry: $needsReEntry, search: $search) { totalCount pageInfo { hasNextPage endCursor } " +
               "items { ckTypeId rtId rtWellKnownName displayName attributePath attributeName required form " +
               $"keyId setAt needsReEntry{usedBy} }} }}{summary} }} }}";
    }

    private static SecretInventoryQueryResult Fail(SecretInventoryQueryOutcome outcome, string message)
    {
        return new SecretInventoryQueryResult { Outcome = outcome, ErrorMessage = message };
    }

    private static string Preview(string body)
    {
        return body.Length > 400 ? body[..400] + "…" : body;
    }

    private sealed class GraphQlBody
    {
        public required string Query { get; init; }
        public required string OperationName { get; init; }
        public required Dictionary<string, object?> Variables { get; init; }
    }

    private sealed class GraphQlResponse
    {
        public GraphQlData? Data { get; init; }
        public List<GraphQlError>? Errors { get; init; }
    }

    private sealed class GraphQlData
    {
        public SecretsData? Secrets { get; init; }
    }

    private sealed class SecretsData
    {
        public SecretInventoryConnectionInfo? Inventory { get; init; }
        public SecretInventorySummaryInfo? Summary { get; init; }
    }

    private sealed class GraphQlError
    {
        public string? Message { get; init; }
        public GraphQlErrorExtensions? Extensions { get; init; }
    }

    private sealed class GraphQlErrorExtensions
    {
        [JsonPropertyName("code")] public string? Code { get; init; }
    }
}
