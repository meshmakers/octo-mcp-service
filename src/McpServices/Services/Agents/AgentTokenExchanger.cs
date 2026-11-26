using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Meshmakers.Octo.Backend.McpServices.Options;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Backend.McpServices.Services.Agents;

/// <summary>
///     Exchanges an agent token (scope <c>octo_agent</c>) for the short-lived <c>octo_api</c> token MCP uses for the
///     agent's downstream calls (AB#9729). Mockable seam of <see cref="McpSessionContext" />.
/// </summary>
public interface IAgentTokenExchanger
{
    /// <summary>
    ///     Returns the exchanged <c>octo_api</c> token, or <c>null</c> when no exchange secret is configured, identity
    ///     refused the exchange or could not be reached. Never returns the agent token itself.
    /// </summary>
    /// <param name="agentAccessToken">The agent's validated request token.</param>
    /// <param name="tenantId">The agent's own tenant (the token's <c>tenant_id</c>).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<string?> ExchangeAsync(string agentAccessToken, string tenantId, CancellationToken cancellationToken = default);
}

/// <summary>
///     Default <see cref="IAgentTokenExchanger" />: RFC 8693 token exchange at <c>/connect/token</c>, presented by the
///     confidential client <see cref="Constants.AgentExchangeClientId" /> with
///     <see cref="McpServiceOptions.AgentExchangeClientSecret" /> in the agent's own tenant (<c>acr_values=tenant:T</c>),
///     scope <c>octo_api</c>. Identity issues a five-minute token for the agent client with <c>act</c> = the agent client
///     id. Results are kept per agent token (keyed by its SHA-256, never by the token text) until 30 seconds before they
///     expire, so a run of tool calls costs one exchange per token lifetime; a failure is not cached.
/// </summary>
internal sealed class AgentTokenExchanger(
    IHttpClientFactory httpClientFactory,
    IOptions<McpServiceOptions> options,
    ILogger<AgentTokenExchanger> logger) : IAgentTokenExchanger
{
    private const string TokenExchangeGrantType = "urn:ietf:params:oauth:grant-type:token-exchange";
    private const string AccessTokenType = "urn:ietf:params:oauth:token-type:access_token";
    private static readonly TimeSpan ExpiryMargin = TimeSpan.FromSeconds(30);
    private const int MaxCacheEntries = 1000;

    private readonly ConcurrentDictionary<string, (string Token, DateTime ExpiresAtUtc)> _cache = new();

    /// <inheritdoc />
    public async Task<string?> ExchangeAsync(string agentAccessToken, string tenantId,
        CancellationToken cancellationToken = default)
    {
        var secret = options.Value.AgentExchangeClientSecret;
        if (string.IsNullOrWhiteSpace(secret))
        {
            logger.LogWarning(
                "Agent token of tenant {TenantId} not exchanged: no agent-exchange secret is configured " +
                "(Mcp:AgentExchangeClientSecret); the downstream call is refused", tenantId);
            return null;
        }

        if (string.IsNullOrWhiteSpace(agentAccessToken) || string.IsNullOrWhiteSpace(tenantId))
        {
            return null;
        }

        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(agentAccessToken)));
        var now = DateTime.UtcNow;
        if (_cache.TryGetValue(key, out var cached) && cached.ExpiresAtUtc > now)
        {
            return cached.Token;
        }

        var client = httpClientFactory.CreateClient("identity");
        var tokenEndpoint = $"{options.Value.AuthorityUrl.TrimEnd('/')}/connect/token";
        using var request = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = Constants.AgentExchangeClientId,
            ["client_secret"] = secret,
            ["grant_type"] = TokenExchangeGrantType,
            ["subject_token"] = agentAccessToken,
            ["subject_token_type"] = AccessTokenType,
            ["acr_values"] = $"tenant:{tenantId}",
            ["scope"] = AgentExchangeClientRegistration.Scope
        });

        HttpResponseMessage response;
        try
        {
            response = await client.PostAsync(tokenEndpoint, request, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("Agent token exchange for tenant {TenantId} failed: {Error}", tenantId, ex.GetType().Name);
            return null;
        }

        using (response)
        {
            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                // Only the OAuth error code is logged, never the body as a whole.
                logger.LogWarning("Agent token exchange for tenant {TenantId} refused: {StatusCode} {Error}", tenantId,
                    (int)response.StatusCode, ReadError(json));
                return null;
            }

            TokenResponse? tokenResponse;
            try
            {
                tokenResponse = JsonSerializer.Deserialize<TokenResponse>(json);
            }
            catch (JsonException)
            {
                logger.LogWarning("Agent token exchange for tenant {TenantId} returned no valid JSON", tenantId);
                return null;
            }

            if (string.IsNullOrEmpty(tokenResponse?.AccessToken))
            {
                logger.LogWarning("Agent token exchange for tenant {TenantId} returned no access token", tenantId);
                return null;
            }

            if (_cache.Count >= MaxCacheEntries)
            {
                foreach (var entry in _cache.Where(e => e.Value.ExpiresAtUtc <= now).ToList())
                {
                    _cache.TryRemove(entry.Key, out _);
                }
            }

            var expiresAt = now.AddSeconds(tokenResponse.ExpiresIn) - ExpiryMargin;
            if (expiresAt > now && _cache.Count < MaxCacheEntries)
            {
                _cache[key] = (tokenResponse.AccessToken, expiresAt);
            }

            return tokenResponse.AccessToken;
        }
    }

    private static string ReadError(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty("error", out var error) ? error.GetString() ?? "-" : "-";
        }
        catch (JsonException)
        {
            return "-";
        }
    }

    private sealed class TokenResponse
    {
        [JsonPropertyName("access_token")] public string? AccessToken { get; set; }
        [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; }
    }
}
