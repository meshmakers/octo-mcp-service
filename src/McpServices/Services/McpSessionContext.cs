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
    /// <remarks>
    ///     Two sources, in this order:
    ///     <list type="number">
    ///         <item>The per-session token store populated by the device-flow <c>authenticate</c> tool —
    ///               the interactive path for human developers running <c>claude</c> locally.</item>
    ///         <item>The HTTP <c>Authorization: Bearer</c> header. The OIDC middleware has already
    ///               validated this token before the request reached this method, so forwarding it to
    ///               downstream Octo API clients (Asset, Identity, Communication, etc.) reuses the same
    ///               authentication that gated the MCP request. This is the path the OctoMesh AI worker
    ///               pod takes — its <c>.mcp.json</c> carries an adapter-minted Bearer token in the
    ///               <c>headers</c> block (chart 0.8.0 Bug C), the worker never calls <c>authenticate</c>,
    ///               and without this fallback every <see cref="AssetClientContext.TryBuild" />-based
    ///               tool (BlueprintTools, IdentityTools, etc.) would refuse with
    ///               "Not authenticated. Call 'authenticate' first." even though the caller IS
    ///               authenticated.</item>
    ///     </list>
    /// </remarks>
    public static string? TryGetAccessToken(McpServer server)
    {
        var tokenStore = server.Services!.GetRequiredService<IMcpSessionTokenStore>();
        var tokens = tokenStore.GetTokens(GetSessionId(server));
        if (tokens != null && !tokens.IsExpired)
        {
            return tokens.AccessToken;
        }

        var httpContextAccessor = server.Services!.GetService<IHttpContextAccessor>();
        var authHeader = httpContextAccessor?.HttpContext?.Request.Headers.Authorization.FirstOrDefault();
        if (!string.IsNullOrEmpty(authHeader)
            && authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            var bearer = authHeader["Bearer ".Length..].Trim();
            if (!string.IsNullOrEmpty(bearer))
            {
                return bearer;
            }
        }

        return null;
    }
}
