using System.ComponentModel;
using System.IdentityModel.Tokens.Jwt;
using System.Text.Json;
using System.Text.Json.Serialization;
using Meshmakers.Octo.Backend.McpServices.Models;
using Meshmakers.Octo.Backend.McpServices.Options;
using Meshmakers.Octo.Backend.McpServices.Services;
using ModelContextProtocol.Server;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Backend.McpServices.Tools;

/// <summary>
///     Authentication tools for the MCP server using OAuth2 Device Authorization Flow.
/// </summary>
[McpServerToolType]
public sealed class AuthenticationTools
{
    /// <summary>
    ///     Start the authentication process using the OAuth2 Device Authorization Flow.
    ///     Returns a user code and verification URI. The user must open the URI in a browser
    ///     and enter the code to authenticate. After that, call check_auth_status to complete.
    /// </summary>
    [McpServerTool(Name = "authenticate")]
    [Description(
        "Start authentication using OAuth2 Device Authorization Flow. Returns a verification URL and user code. " +
        "Open the URL in a browser, enter the code, and log in. Then call check_auth_status to complete authentication. " +
        "You must specify a tenantId to authenticate against a specific tenant.")]
    public static async Task<AuthenticateResponse> Authenticate(McpServer server, string tenantId)
    {
        try
        {
            var tokenStore = server.Services!.GetRequiredService<IMcpSessionTokenStore>();
            var httpClientFactory = server.Services!.GetRequiredService<IHttpClientFactory>();
            var mcpOptions = server.Services!.GetRequiredService<IOptions<McpServiceOptions>>().Value;

            var sessionId = McpSessionContext.TryGetSessionKey(server);
            if (sessionId == null)
            {
                return new AuthenticateResponse
                {
                    IsSuccess = false,
                    ErrorMessage = NoCallerError
                };
            }

            // Check if already authenticated
            var existingTokens = tokenStore.GetTokens(sessionId);
            if (existingTokens != null && !existingTokens.IsExpired)
            {
                return new AuthenticateResponse
                {
                    IsSuccess = true,
                    IsAlreadyAuthenticated = true,
                    Message = "Already authenticated. Use 'whoami' to see your identity."
                };
            }

            // Start Device Authorization Flow
            var client = httpClientFactory.CreateClient("identity");
            var deviceAuthEndpoint = $"{mcpOptions.AuthorityUrl.TrimEnd('/')}/connect/deviceauthorization";

            var requestParams = new Dictionary<string, string>
            {
                ["client_id"] = Constants.McpServicesDeviceClientId,
                ["scope"] = "openid profile email role octo_api offline_access",
                ["acr_values"] = $"tenant:{tenantId}"
            };
            var request = new FormUrlEncodedContent(requestParams);

            var response = await client.PostAsync(deviceAuthEndpoint, request);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync();
                return new AuthenticateResponse
                {
                    IsSuccess = false,
                    ErrorMessage = $"Device authorization request failed: {response.StatusCode}. {errorBody}"
                };
            }

            var json = await response.Content.ReadAsStringAsync();
            var deviceResponse = JsonSerializer.Deserialize<DeviceAuthorizationResponse>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (deviceResponse == null)
            {
                return new AuthenticateResponse
                {
                    IsSuccess = false,
                    ErrorMessage = "Failed to parse device authorization response."
                };
            }

            // Store pending authorization
            tokenStore.SetDeviceAuthorization(sessionId, new DeviceAuthorizationState
            {
                DeviceCode = deviceResponse.DeviceCode,
                UserCode = deviceResponse.UserCode,
                VerificationUri = deviceResponse.VerificationUri,
                VerificationUriComplete = deviceResponse.VerificationUriComplete,
                ExpiresAtUtc = DateTime.UtcNow.AddSeconds(deviceResponse.ExpiresIn),
                IntervalSeconds = deviceResponse.Interval > 0 ? deviceResponse.Interval : 5
            });

            return new AuthenticateResponse
            {
                IsSuccess = true,
                UserCode = deviceResponse.UserCode,
                VerificationUri = deviceResponse.VerificationUri,
                VerificationUriComplete = deviceResponse.VerificationUriComplete,
                ExpiresInSeconds = deviceResponse.ExpiresIn,
                Message =
                    $"Please open {deviceResponse.VerificationUri} in your browser and enter code: {deviceResponse.UserCode}. Then call 'check_auth_status' to complete authentication."
            };
        }
        catch (Exception ex)
        {
            return new AuthenticateResponse
            {
                IsSuccess = false,
                ErrorMessage = ex.Message
            };
        }
    }

    /// <summary>
    ///     Check if the user has completed the authentication in the browser.
    ///     Call this after 'authenticate' once you have entered the code in the browser.
    /// </summary>
    [McpServerTool(Name = "check_auth_status")]
    [Description(
        "Check if the user has completed authentication in the browser. Call this after 'authenticate' " +
        "once the user has entered the code. Returns authentication status and user information on success.")]
    public static async Task<CheckAuthStatusResponse> CheckAuthStatus(McpServer server)
    {
        try
        {
            var tokenStore = server.Services!.GetRequiredService<IMcpSessionTokenStore>();
            var httpClientFactory = server.Services!.GetRequiredService<IHttpClientFactory>();
            var mcpOptions = server.Services!.GetRequiredService<IOptions<McpServiceOptions>>().Value;

            var sessionId = McpSessionContext.TryGetSessionKey(server);
            if (sessionId == null)
            {
                return new CheckAuthStatusResponse
                {
                    IsSuccess = false,
                    IsAuthenticated = false,
                    ErrorMessage = NoCallerError
                };
            }

            // Check if already authenticated
            var existingTokens = tokenStore.GetTokens(sessionId);
            if (existingTokens != null && !existingTokens.IsExpired)
            {
                return new CheckAuthStatusResponse
                {
                    IsSuccess = true,
                    IsAuthenticated = true,
                    Message = "Already authenticated."
                };
            }

            // Get pending device authorization
            var deviceAuth = tokenStore.GetDeviceAuthorization(sessionId);
            if (deviceAuth == null)
            {
                return new CheckAuthStatusResponse
                {
                    IsSuccess = false,
                    IsAuthenticated = false,
                    ErrorMessage = "No pending authentication. Call 'authenticate' first."
                };
            }

            // Check if expired
            if (DateTime.UtcNow >= deviceAuth.ExpiresAtUtc)
            {
                tokenStore.RemoveDeviceAuthorization(sessionId);
                return new CheckAuthStatusResponse
                {
                    IsSuccess = false,
                    IsAuthenticated = false,
                    ErrorMessage = "Authentication request has expired. Call 'authenticate' to start a new request."
                };
            }

            // Poll token endpoint
            var client = httpClientFactory.CreateClient("identity");
            var tokenEndpoint = $"{mcpOptions.AuthorityUrl.TrimEnd('/')}/connect/token";

            var request = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = Constants.McpServicesDeviceClientId,
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                ["device_code"] = deviceAuth.DeviceCode
            });

            var response = await client.PostAsync(tokenEndpoint, request);
            var json = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                var errorResponse = JsonSerializer.Deserialize<TokenErrorResponse>(json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (errorResponse?.Error == "authorization_pending")
                {
                    return new CheckAuthStatusResponse
                    {
                        IsSuccess = true,
                        IsAuthenticated = false,
                        IsPending = true,
                        Message =
                            $"Waiting for user to authenticate. Please open {deviceAuth.VerificationUri} and enter code: {deviceAuth.UserCode}",
                        RetryAfterSeconds = deviceAuth.IntervalSeconds
                    };
                }

                if (errorResponse?.Error == "slow_down")
                {
                    return new CheckAuthStatusResponse
                    {
                        IsSuccess = true,
                        IsAuthenticated = false,
                        IsPending = true,
                        Message = "Waiting for user to authenticate (polling too fast, slowing down).",
                        RetryAfterSeconds = deviceAuth.IntervalSeconds + 5
                    };
                }

                tokenStore.RemoveDeviceAuthorization(sessionId);
                return new CheckAuthStatusResponse
                {
                    IsSuccess = false,
                    IsAuthenticated = false,
                    ErrorMessage =
                        $"Authentication failed: {errorResponse?.Error} - {errorResponse?.ErrorDescription}"
                };
            }

            // Success - parse tokens
            var tokenResponse = JsonSerializer.Deserialize<TokenResponse>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (tokenResponse == null)
            {
                return new CheckAuthStatusResponse
                {
                    IsSuccess = false,
                    IsAuthenticated = false,
                    ErrorMessage = "Failed to parse token response."
                };
            }

            // Store tokens
            tokenStore.SetTokens(sessionId, new McpSessionTokens
            {
                AccessToken = tokenResponse.AccessToken,
                RefreshToken = tokenResponse.RefreshToken,
                ExpiresAtUtc = DateTime.UtcNow.AddSeconds(tokenResponse.ExpiresIn)
            });

            // Clean up device authorization
            tokenStore.RemoveDeviceAuthorization(sessionId);

            // The stored token is only usable when it belongs to the principal that presented the
            // transport bearer (AB#5036) — a device login against a DIFFERENT tenant produces a token for
            // that tenant's shadow user, which the tools will refuse. Say so here rather than letting the
            // next tool call fail with a message that looks unrelated. The sanctioned way to reach another
            // tenant is 'switch_tenant' / the transparent RFC 8693 exchange (AB#4338).
            var caller = McpCallerPrincipal.FromServer(server);
            var boundToCaller = caller is not { IsUserPrincipal: true }
                                || (string.Equals(JwtClaimReader.TryReadSubjectId(tokenResponse.AccessToken),
                                        caller.UserSubjectId, StringComparison.OrdinalIgnoreCase)
                                    && string.Equals(JwtClaimReader.TryReadTenantId(tokenResponse.AccessToken),
                                        caller.TenantId, StringComparison.OrdinalIgnoreCase));

            return new CheckAuthStatusResponse
            {
                IsSuccess = true,
                IsAuthenticated = true,
                Message = boundToCaller
                    ? "Authentication successful! You can now use all tools. Use 'whoami' to see your identity and 'list_tenants' to see available tenants."
                    : "Authentication completed, but the resulting token belongs to a different identity than "
                      + "the bearer this MCP connection was opened with, so tools will not use it. Re-run "
                      + "'authenticate' for your own tenant, or use 'switch_tenant' to reach another tenant."
            };
        }
        catch (Exception ex)
        {
            return new CheckAuthStatusResponse
            {
                IsSuccess = false,
                IsAuthenticated = false,
                ErrorMessage = ex.Message
            };
        }
    }

    /// <summary>
    ///     Report the current authentication status of the MCP session. Forces a lazy
    ///     <c>refresh_token</c> grant if the stored access token is expired but a refresh
    ///     token is available. Mirrors octo-cli's <c>AuthStatus</c> command.
    /// </summary>
    [McpServerTool(Name = "auth_status")]
    [Description(
        "Show authentication status for the current MCP session. Forces a token refresh if the access " +
        "token is expired but a refresh token is available — equivalent to octo-cli's AuthStatus command. " +
        "Returns identity claims (sub / name / tenant) when the token is a parseable JWT, plus the UTC " +
        "expiry of the stored access token.")]
    public static async Task<AuthStatusResponse> AuthStatus(McpServer server)
    {
        try
        {
            var sessionId = McpSessionContext.TryGetSessionKey(server);
            var tokenStore = server.Services!.GetRequiredService<IMcpSessionTokenStore>();

            var before = sessionId == null ? null : tokenStore.GetTokens(sessionId);
            var wasExpired = before != null && before.IsExpired;

            var resolved = await McpSessionContext.ResolveAccessTokenAsync(server);
            if (resolved.Error != null)
            {
                return new AuthStatusResponse
                {
                    IsSuccess = false,
                    IsAuthenticated = false,
                    ErrorMessage = resolved.Error
                };
            }

            var accessToken = resolved.AccessToken;
            if (accessToken == null)
            {
                return new AuthStatusResponse
                {
                    IsSuccess = true,
                    IsAuthenticated = false,
                    Message = "Not authenticated. Call 'authenticate' first."
                };
            }

            var after = sessionId == null ? null : tokenStore.GetTokens(sessionId);
            var wasRefreshed = wasExpired && after != null && !after.IsExpired;

            string? sub = null;
            string? name = null;
            string? tenant = null;
            try
            {
                var handler = new JwtSecurityTokenHandler();
                var jwt = handler.ReadJwtToken(accessToken);
                foreach (var claim in jwt.Claims)
                {
                    switch (claim.Type)
                    {
                        case "sub":
                            sub ??= claim.Value;
                            break;
                        case "name":
                            name ??= claim.Value;
                            break;
                        case "tenant":
                        case "tenant_id":
                            tenant ??= claim.Value;
                            break;
                    }
                }
            }
            catch
            {
                // Opaque (non-JWT) bearer — leave claim fields null; IsAuthenticated still true.
            }

            return new AuthStatusResponse
            {
                IsSuccess = true,
                IsAuthenticated = true,
                WasRefreshed = wasRefreshed,
                ExpiresAtUtc = after?.ExpiresAtUtc,
                SubjectId = sub,
                UserName = name,
                TenantId = tenant,
                Message = wasRefreshed
                    ? "Access token was refreshed."
                    : "Authenticated."
            };
        }
        catch (Exception ex)
        {
            return new AuthStatusResponse
            {
                IsSuccess = false,
                IsAuthenticated = false,
                ErrorMessage = ex.Message
            };
        }
    }

    /// <summary>
    ///     Error for a device-flow call that cannot be attributed to a caller. Since AB#4315 both MCP
    ///     endpoints require a validated bearer, so a request always carries a principal in production —
    ///     and since AB#5036 that principal, not the client-supplied <c>Mcp-Session-Id</c> header, is what
    ///     the token store is keyed by. Without one there is no slot to put the device code in that would
    ///     not be shared with every other caller on the pod.
    /// </summary>
    private const string NoCallerError =
        "Cannot start a device-flow session: this request carries no authenticated principal. Connect your "
        + "MCP client with a bearer token first.";

    #region Response Models

    // ReSharper disable once ClassNeverInstantiated.Global
    internal class DeviceAuthorizationResponse
    {
        [JsonPropertyName("device_code")] public string DeviceCode { get; set; } = null!;
        [JsonPropertyName("user_code")] public string UserCode { get; set; } = null!;
        [JsonPropertyName("verification_uri")] public string VerificationUri { get; set; } = null!;
        [JsonPropertyName("verification_uri_complete")] public string? VerificationUriComplete { get; set; }
        [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; }
        [JsonPropertyName("interval")] public int Interval { get; set; }
    }

    // ReSharper disable once ClassNeverInstantiated.Global
    internal class TokenResponse
    {
        [JsonPropertyName("access_token")] public string AccessToken { get; set; } = null!;
        [JsonPropertyName("refresh_token")] public string? RefreshToken { get; set; }
        [JsonPropertyName("token_type")] public string TokenType { get; set; } = null!;
        [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; }
    }

    // ReSharper disable once ClassNeverInstantiated.Global
    internal class TokenErrorResponse
    {
        [JsonPropertyName("error")] public string Error { get; set; } = null!;
        [JsonPropertyName("error_description")] public string? ErrorDescription { get; set; }
    }

    #endregion
}
