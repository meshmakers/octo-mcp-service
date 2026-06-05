using Microsoft.AspNetCore.Http;
using ModelContextProtocol.Server;

namespace Meshmakers.Octo.Backend.McpServices.Services;

/// <summary>
///     Helpers for resolving the MCP session id and the access token stored for it.
///     Centralised so individual tool classes do not duplicate the header lookup + token-store dance.
/// </summary>
internal static class McpSessionContext
{
    public static string GetSessionId(McpServer server)
    {
        var httpContextAccessor = server.Services?.GetService<IHttpContextAccessor>();
        var sessionId = httpContextAccessor?.HttpContext?.Request.Headers["Mcp-Session-Id"].FirstOrDefault();
        return sessionId ?? server.ServerOptions?.ServerInfo?.Name ?? "default-session";
    }

    /// <summary>
    ///     Returns the access token for the current session, or null if the caller is not authenticated.
    /// </summary>
    public static string? TryGetAccessToken(McpServer server)
    {
        var tokenStore = server.Services!.GetRequiredService<IMcpSessionTokenStore>();
        var tokens = tokenStore.GetTokens(GetSessionId(server));
        if (tokens == null || tokens.IsExpired)
        {
            return null;
        }

        return tokens.AccessToken;
    }
}
