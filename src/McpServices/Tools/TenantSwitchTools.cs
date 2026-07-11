using System.ComponentModel;
using Meshmakers.Octo.Backend.McpServices.Models;
using Meshmakers.Octo.Backend.McpServices.Services;
using ModelContextProtocol.Server;

namespace Meshmakers.Octo.Backend.McpServices.Tools;

/// <summary>
///     Cross-tenant switching tool (AB#4338). Lets an already-authenticated user obtain a target-tenant
///     access token — with roles re-resolved in the target tenant — without a browser / credential
///     prompt, via RFC 8693 token exchange from the current home-tenant token.
/// </summary>
[McpServerToolType]
public sealed class TenantSwitchTools
{
    /// <summary>
    ///     Switch the current MCP session to operate on a different tenant. Exchanges the caller's
    ///     current (home) access token for a token scoped to <paramref name="tenantId"/> and caches it,
    ///     so subsequent tenant-scoped tool calls against that tenant reuse it transparently.
    /// </summary>
    [McpServerTool(Name = "switch_tenant")]
    [McpRisk(McpRiskLevel.Low)]
    [Description(
        "Switch to a different tenant without re-authenticating. Exchanges your current access token for " +
        "one scoped to the target tenant, with your roles re-resolved in that tenant (no privilege leak). " +
        "Grants no new authority — access to the target tenant is enforced server-side. On success returns " +
        "your roles in the target tenant. If the exchange fails (e.g. you may not access that tenant), use " +
        "the 'authenticate' tool to log in against the target tenant directly.")]
    public static async Task<SwitchTenantResponse> SwitchTenant(
        McpServer server,
        [Description("The tenant ID to switch to.")] string tenantId)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(tenantId))
            {
                return new SwitchTenantResponse
                {
                    IsSuccess = false,
                    ErrorMessage = "A tenantId is required."
                };
            }

            // Resolve the home token — proof of identity for the exchange.
            var homeToken = await McpSessionContext.TryGetAccessTokenAsync(server);
            if (homeToken == null)
            {
                return new SwitchTenantResponse
                {
                    IsSuccess = false,
                    TenantId = tenantId,
                    ErrorMessage = Constants.NotAuthenticatedError
                };
            }

            // If the caller is already on the requested tenant, no exchange is needed.
            var homeTenantId = JwtClaimReader.TryReadTenantId(homeToken);
            if (homeTenantId != null && string.Equals(homeTenantId, tenantId, StringComparison.OrdinalIgnoreCase))
            {
                return new SwitchTenantResponse
                {
                    IsSuccess = true,
                    TenantId = tenantId,
                    Roles = JwtClaimReader.ReadRoles(homeToken),
                    Message = $"Already on tenant '{tenantId}'."
                };
            }

            var exchanger = server.Services!.GetRequiredService<ITenantTokenExchanger>();
            var exchanged = await exchanger.ExchangeForTenantAsync(homeToken, tenantId);
            if (exchanged == null)
            {
                return new SwitchTenantResponse
                {
                    IsSuccess = false,
                    TenantId = tenantId,
                    ErrorMessage =
                        $"Could not switch to tenant '{tenantId}'. You may not have access to it, or the " +
                        "identity service is unreachable. Use the 'authenticate' tool to log in against " +
                        "that tenant directly."
                };
            }

            // Cache the B token so subsequent tenant-scoped tool calls reuse it.
            var sessionId = McpSessionContext.GetSessionId(server);
            var tokenStore = server.Services!.GetRequiredService<IMcpSessionTokenStore>();
            tokenStore.SetTenantTokens(sessionId, tenantId, exchanged);

            return new SwitchTenantResponse
            {
                IsSuccess = true,
                TenantId = tenantId,
                Roles = JwtClaimReader.ReadRoles(exchanged.AccessToken),
                Message = $"Switched to tenant '{tenantId}'. Subsequent tools will operate on this tenant " +
                          "when you pass it as 'tenantId'."
            };
        }
        catch (Exception ex)
        {
            return new SwitchTenantResponse
            {
                IsSuccess = false,
                TenantId = tenantId,
                ErrorMessage = ex.Message
            };
        }
    }
}
