using System.ComponentModel;
using System.IdentityModel.Tokens.Jwt;
using Meshmakers.Octo.Backend.McpServices.Models;
using Meshmakers.Octo.Backend.McpServices.Services;
using ModelContextProtocol.Server;

namespace Meshmakers.Octo.Backend.McpServices.Tools;

/// <summary>
///     Identity tools for displaying user information and available tenants.
/// </summary>
[McpServerToolType]
public sealed class IdentityTools
{
    /// <summary>
    ///     Get information about the currently authenticated user.
    /// </summary>
    [McpServerTool(Name = "whoami")]
    [Description("Get information about the currently authenticated user, including name, email, roles, and available tenants.")]
    public static async Task<WhoAmIResponse> WhoAmI(McpServer server)
    {
        try
        {
            // McpSessionContext resolves the token from the device-flow session store first and
            // falls back to the inbound Authorization: Bearer header — the path interactive MCP
            // OAuth clients (Claude Code) and the AI worker take. Reading the store directly here
            // predated that fallback and wrongly reported "Not authenticated" for callers whose
            // bearer already passed the transport gate.
            var accessToken = await McpSessionContext.TryGetAccessTokenAsync(server);
            if (accessToken == null)
            {
                return new WhoAmIResponse
                {
                    IsSuccess = false,
                    IsAuthenticated = false,
                    ErrorMessage = Constants.NotAuthenticatedError
                };
            }

            var claims = ParseAccessToken(accessToken);

            return new WhoAmIResponse
            {
                IsSuccess = true,
                IsAuthenticated = true,
                UserId = claims.GetValueOrDefault("sub"),
                UserName = claims.GetValueOrDefault("preferred_username") ?? claims.GetValueOrDefault("name"),
                Email = claims.GetValueOrDefault("email"),
                TenantId = claims.GetValueOrDefault("tenant_id"),
                AllowedTenants = GetListClaim(claims, "allowed_tenants"),
                Roles = GetListClaim(claims, "role"),
                Scopes = GetListClaim(claims, "scope"),
                TokenExpiresAtUtc = GetTokenExpiry(server, accessToken)
            };
        }
        catch (Exception ex)
        {
            return new WhoAmIResponse
            {
                IsSuccess = false,
                IsAuthenticated = false,
                ErrorMessage = ex.Message
            };
        }
    }

    /// <summary>
    ///     List all tenants the authenticated user has access to.
    /// </summary>
    [McpServerTool(Name = "list_tenants")]
    [Description("List all tenants the authenticated user has access to. Use the returned tenant IDs as the 'tenantId' parameter in other tools.")]
    public static async Task<ListTenantsResponse> ListTenants(McpServer server)
    {
        try
        {
            // Same session-store-then-bearer-header resolution as WhoAmI — see the comment there.
            var accessToken = await McpSessionContext.TryGetAccessTokenAsync(server);
            if (accessToken == null)
            {
                return new ListTenantsResponse
                {
                    IsSuccess = false,
                    ErrorMessage = Constants.NotAuthenticatedError
                };
            }

            var claims = ParseAccessToken(accessToken);
            var allowedTenants = GetListClaim(claims, "allowed_tenants");
            var currentTenantId = claims.GetValueOrDefault("tenant_id");

            return new ListTenantsResponse
            {
                IsSuccess = true,
                CurrentTenantId = currentTenantId,
                AllowedTenants = allowedTenants,
                TotalCount = allowedTenants.Count,
                Message = allowedTenants.Count > 0
                    ? $"You have access to {allowedTenants.Count} tenant(s). Pass the tenant ID as 'tenantId' parameter to other tools."
                    : "No tenants found in your token. You may need to request the 'allowed_tenants' scope."
            };
        }
        catch (Exception ex)
        {
            return new ListTenantsResponse
            {
                IsSuccess = false,
                ErrorMessage = ex.Message
            };
        }
    }

    /// <summary>
    ///     Expiry for the response: the session store's value when the token came from the
    ///     device-flow store, otherwise the JWT's own <c>exp</c> (bearer-header path).
    /// </summary>
    private static DateTime? GetTokenExpiry(McpServer server, string accessToken)
    {
        var tokenStore = server.Services!.GetRequiredService<IMcpSessionTokenStore>();
        var stored = tokenStore.GetTokens(McpSessionContext.GetSessionId(server));
        if (stored != null && stored.AccessToken == accessToken)
        {
            return stored.ExpiresAtUtc;
        }

        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(accessToken);
        return jwt.ValidTo == DateTime.MinValue ? null : jwt.ValidTo;
    }

    private static Dictionary<string, string> ParseAccessToken(string accessToken)
    {
        var handler = new JwtSecurityTokenHandler();
        var token = handler.ReadJwtToken(accessToken);

        var claims = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var claim in token.Claims)
        {
            // For duplicate claim types (e.g., role, scope), concatenate with space
            if (claims.ContainsKey(claim.Type))
            {
                claims[claim.Type] += " " + claim.Value;
            }
            else
            {
                claims[claim.Type] = claim.Value;
            }
        }

        return claims;
    }

    private static List<string> GetListClaim(Dictionary<string, string> claims, string claimType)
    {
        if (!claims.TryGetValue(claimType, out var value))
        {
            return [];
        }

        return value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Distinct().ToList();
    }
}
