using System.Text.Json;
using System.Text.Json.Serialization;
using Meshmakers.Octo.Backend.McpServices.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Backend.McpServices.Services;

/// <summary>
///     Default <see cref="ISessionTokenRefresher" /> — POSTs to <c>/connect/token</c> with
///     <c>grant_type=refresh_token</c>. Mirrors the SDK's <c>AuthenticatorClient.RefreshTokenAsync</c>
///     wire format. The MCP device client is public (no client secret) so only
///     <c>client_id</c> + <c>refresh_token</c> are sent — same as the device-flow polling call
///     in <see cref="Tools.AuthenticationTools" />.
/// </summary>
internal sealed class SessionTokenRefresher : ISessionTokenRefresher
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<SessionTokenRefresher> _logger;
    private readonly IOptions<McpServiceOptions> _options;

    /// <summary>Constructor.</summary>
    public SessionTokenRefresher(
        IHttpClientFactory httpClientFactory,
        IOptions<McpServiceOptions> options,
        ILogger<SessionTokenRefresher> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<McpSessionTokens?> RefreshAsync(string refreshToken, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            return null;
        }

        var client = _httpClientFactory.CreateClient("identity");
        var tokenEndpoint = $"{_options.Value.AuthorityUrl.TrimEnd('/')}/connect/token";

        var request = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = Constants.McpServicesDeviceClientId,
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken
        });

        HttpResponseMessage response;
        try
        {
            response = await client.PostAsync(tokenEndpoint, request, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Refresh-token request to {Endpoint} threw", tokenEndpoint);
            return null;
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogInformation(
                "Refresh-token request returned {StatusCode}: {Body}",
                response.StatusCode, json);
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
            _logger.LogWarning(ex, "Refresh-token response was not valid JSON");
            return null;
        }

        if (tokenResponse?.AccessToken == null)
        {
            _logger.LogWarning("Refresh-token response missing access_token");
            return null;
        }

        return new McpSessionTokens
        {
            AccessToken = tokenResponse.AccessToken,
            // IdentityServer may rotate refresh tokens — use the new one if present,
            // otherwise keep the one the caller passed in.
            RefreshToken = string.IsNullOrEmpty(tokenResponse.RefreshToken)
                ? refreshToken
                : tokenResponse.RefreshToken,
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
