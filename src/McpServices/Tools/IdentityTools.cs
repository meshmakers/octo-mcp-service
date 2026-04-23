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
    public static Task<WhoAmIResponse> WhoAmI(McpServer server)
    {
        try
        {
            var tokenStore = server.Services!.GetRequiredService<IMcpSessionTokenStore>();
            var sessionId = GetSessionId(server);

            var tokens = tokenStore.GetTokens(sessionId);
            if (tokens == null || tokens.IsExpired)
            {
                return Task.FromResult(new WhoAmIResponse
                {
                    IsSuccess = false,
                    IsAuthenticated = false,
                    ErrorMessage = "Not authenticated. Call 'authenticate' first."
                });
            }

            var claims = ParseAccessToken(tokens.AccessToken);

            return Task.FromResult(new WhoAmIResponse
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
                TokenExpiresAtUtc = tokens.ExpiresAtUtc
            });
        }
        catch (Exception ex)
        {
            return Task.FromResult(new WhoAmIResponse
            {
                IsSuccess = false,
                IsAuthenticated = false,
                ErrorMessage = ex.Message
            });
        }
    }

    /// <summary>
    ///     List all tenants the authenticated user has access to.
    /// </summary>
    [McpServerTool(Name = "list_tenants")]
    [Description("List all tenants the authenticated user has access to. Use the returned tenant IDs as the 'tenantId' parameter in other tools.")]
    public static Task<ListTenantsResponse> ListTenants(McpServer server)
    {
        try
        {
            var tokenStore = server.Services!.GetRequiredService<IMcpSessionTokenStore>();
            var sessionId = GetSessionId(server);

            var tokens = tokenStore.GetTokens(sessionId);
            if (tokens == null || tokens.IsExpired)
            {
                return Task.FromResult(new ListTenantsResponse
                {
                    IsSuccess = false,
                    ErrorMessage = "Not authenticated. Call 'authenticate' first."
                });
            }

            var claims = ParseAccessToken(tokens.AccessToken);
            var allowedTenants = GetListClaim(claims, "allowed_tenants");
            var currentTenantId = claims.GetValueOrDefault("tenant_id");

            return Task.FromResult(new ListTenantsResponse
            {
                IsSuccess = true,
                CurrentTenantId = currentTenantId,
                AllowedTenants = allowedTenants,
                TotalCount = allowedTenants.Count,
                Message = allowedTenants.Count > 0
                    ? $"You have access to {allowedTenants.Count} tenant(s). Pass the tenant ID as 'tenantId' parameter to other tools."
                    : "No tenants found in your token. You may need to request the 'allowed_tenants' scope."
            });
        }
        catch (Exception ex)
        {
            return Task.FromResult(new ListTenantsResponse
            {
                IsSuccess = false,
                ErrorMessage = ex.Message
            });
        }
    }

    private static string GetSessionId(McpServer server)
    {
        var httpContextAccessor = server.Services?.GetService<IHttpContextAccessor>();
        var sessionId = httpContextAccessor?.HttpContext?.Request.Headers["Mcp-Session-Id"].FirstOrDefault();
        return sessionId ?? server.ServerOptions?.ServerInfo?.Name ?? "default-session";
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
