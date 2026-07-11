using System.Text.Json;
using System.Text.Json.Serialization;
using Meshmakers.Octo.Backend.McpServices.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Backend.McpServices.Services;

/// <summary>
///     Default <see cref="ITenantTokenExchanger" /> — POSTs to <c>/connect/token</c> with the RFC 8693
///     token-exchange grant (AB#4338). Sibling of <see cref="SessionTokenRefresher" />: same
///     <c>"identity"</c> named <see cref="IHttpClientFactory" /> client, same
///     <see cref="McpServiceOptions.AuthorityUrl" />, same token-response parsing. The MCP device
///     client is public (no client secret) so only <c>client_id</c> + the exchange parameters are sent.
///     The identity-side <c>TenantExchangeGrantValidator</c> resolves the target tenant from
///     <c>acr_values=tenant:B</c> and returns a B-scoped access token whose roles are re-resolved in B.
///     v1 issues no refresh token — re-exchange from the still-valid home token on expiry.
/// </summary>
internal sealed class TenantTokenExchanger : ITenantTokenExchanger
{
    private const string TokenExchangeGrantType = "urn:ietf:params:oauth:grant-type:token-exchange";
    private const string AccessTokenType = "urn:ietf:params:oauth:token-type:access_token";
    private const string ExchangeScope = "openid profile email role octo_api";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<TenantTokenExchanger> _logger;
    private readonly IOptions<McpServiceOptions> _options;

    /// <summary>Constructor.</summary>
    public TenantTokenExchanger(
        IHttpClientFactory httpClientFactory,
        IOptions<McpServiceOptions> options,
        ILogger<TenantTokenExchanger> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<McpSessionTokens?> ExchangeForTenantAsync(
        string homeAccessToken,
        string targetTenantId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(homeAccessToken) || string.IsNullOrWhiteSpace(targetTenantId))
        {
            return null;
        }

        var client = _httpClientFactory.CreateClient("identity");
        var tokenEndpoint = $"{_options.Value.AuthorityUrl.TrimEnd('/')}/connect/token";

        var request = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = Constants.McpServicesDeviceClientId,
            ["grant_type"] = TokenExchangeGrantType,
            ["subject_token"] = homeAccessToken,
            ["subject_token_type"] = AccessTokenType,
            ["acr_values"] = $"tenant:{targetTenantId}",
            ["scope"] = ExchangeScope
        });

        HttpResponseMessage response;
        try
        {
            response = await client.PostAsync(tokenEndpoint, request, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Token-exchange request to {Endpoint} for tenant {TargetTenant} threw",
                tokenEndpoint, targetTenantId);
            return null;
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            // OAuth error (e.g. unauthorized_client when the user may not access the target tenant).
            _logger.LogInformation(
                "Token-exchange request for tenant {TargetTenant} returned {StatusCode}: {Body}",
                targetTenantId, response.StatusCode, json);
            return null;
        }

        TokenResponse? tokenResponse;
        try
        {
            tokenResponse = JsonSerializer.Deserialize<TokenResponse>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Token-exchange response was not valid JSON");
            return null;
        }

        if (tokenResponse?.AccessToken == null)
        {
            _logger.LogWarning("Token-exchange response missing access_token");
            return null;
        }

        return new McpSessionTokens
        {
            AccessToken = tokenResponse.AccessToken,
            // v1 issues no exchanged refresh token — re-exchange from the home token on expiry.
            RefreshToken = null,
            ExpiresAtUtc = DateTime.UtcNow.AddSeconds(tokenResponse.ExpiresIn)
        };
    }

    private sealed class TokenResponse
    {
        [JsonPropertyName("access_token")] public string? AccessToken { get; set; }
        [JsonPropertyName("refresh_token")] public string? RefreshToken { get; set; }
        [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; }
    }
}
