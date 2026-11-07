using System.ComponentModel;
using Meshmakers.Octo.Backend.McpServices.Models;
using Meshmakers.Octo.Backend.McpServices.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using ModelContextProtocol.Server;

// ReSharper disable UnusedMember.Global

namespace Meshmakers.Octo.Backend.McpServices.Tools;

/// <summary>
///     The allow-list of a tenant as a <b>source</b> of sibling-tenant grants (AB#8346, identity AB#8345): which target
///     tenants may look up this tenant's users by exact user name or e-mail and grant them access. Mirrors octo-cli
///     GetCrossTenantGrantPermissions / SetCrossTenantGrantPermissions. Only administrators of the tenant itself may read
///     or change it.
/// </summary>
[McpServerToolType]
public sealed class CrossTenantGrantPermissionTools
{
    /// <summary>Read the allow-list of the tenant.</summary>
    [McpServerTool(Name = "get_cross_tenant_grant_permissions")]
    [Description("Show which tenants may look up users of this tenant by exact user name or e-mail and grant them access " +
                 "(the tenant's allow-list as a source of sibling-tenant grants). Empty = nobody (default). Only " +
                 "administrators of this tenant (UserManagement + TenantManagement, session for this tenant) may read it; " +
                 "others get HTTP 403. Equivalent to octo-cli GetCrossTenantGrantPermissions.")]
    public static async Task<CrossTenantGrantPermissionsResponse> GetPermissions(
        McpServer server,
        [Description("Tenant to operate on (the source tenant). Falls back to URL route.")] string? tenantId = null)
    {
        var ctx = await IdentityClientContext.TryBuildAsync(server, tenantId);
        if (ctx.Error != null)
        {
            return new CrossTenantGrantPermissionsResponse { IsSuccess = false, ErrorMessage = ctx.Error };
        }

        try
        {
            var permissions = await ctx.Client!.GetCrossTenantGrantPermissions();
            return new CrossTenantGrantPermissionsResponse
            {
                IsSuccess = true,
                TenantId = ctx.TenantId,
                TargetTenantIds = permissions.TargetTenantIds
            };
        }
        catch (Exception ex)
        {
            return new CrossTenantGrantPermissionsResponse
            {
                IsSuccess = false,
                TenantId = ctx.TenantId,
                ErrorMessage = ServiceProblemMessage.From(ex).Message
            };
        }
    }

    /// <summary>Replace the allow-list of the tenant. Requires confirm=true.</summary>
    [McpServerTool(Name = "set_cross_tenant_grant_permissions")]
    [McpRisk(McpRiskLevel.High)]
    [Description("Replace the list of tenants that may look up users of this tenant by exact user name or e-mail and grant " +
                 "them access. Pass the complete list; [] permits nobody. A removed tenant can no longer look up or grant at " +
                 "once and its existing grants are refused at their next token exchange. Invalid entries (format, more than " +
                 "200, this tenant itself) answer HTTP 400 and nothing changes. Only administrators of this tenant may " +
                 "change it (others get HTTP 403). Requires confirm=true. Equivalent to octo-cli SetCrossTenantGrantPermissions.")]
    public static async Task<CrossTenantGrantPermissionsResponse> SetPermissions(
        McpServer server,
        [Description("The complete new list of permitted target tenant IDs; [] permits nobody.")]
        List<string>? targetTenantIds,
        [Description("Must be true to actually change the list.")] bool confirm = false,
        [Description("Tenant to operate on (the source tenant). Falls back to URL route.")] string? tenantId = null)
    {
        if (targetTenantIds == null)
        {
            return new CrossTenantGrantPermissionsResponse
            {
                IsSuccess = false,
                ErrorMessage = "targetTenantIds is required; pass [] to permit nobody."
            };
        }

        if (targetTenantIds.Any(string.IsNullOrWhiteSpace))
        {
            return new CrossTenantGrantPermissionsResponse
            {
                IsSuccess = false,
                ErrorMessage = "targetTenantIds must not contain empty entries."
            };
        }

        if (!confirm)
        {
            return new CrossTenantGrantPermissionsResponse
            {
                IsSuccess = false,
                ErrorMessage = "Refusing to replace the cross-tenant grant permissions without confirm=true."
            };
        }

        var ctx = await IdentityClientContext.TryBuildAsync(server, tenantId);
        if (ctx.Error != null)
        {
            return new CrossTenantGrantPermissionsResponse { IsSuccess = false, ErrorMessage = ctx.Error };
        }

        try
        {
            var before = await ctx.Client!.GetCrossTenantGrantPermissions();
            var stored = await ctx.Client.UpdateCrossTenantGrantPermissions(
                new CrossTenantGrantPermissionsDto { TargetTenantIds = targetTenantIds });
            var added = stored.TargetTenantIds.Except(before.TargetTenantIds, StringComparer.OrdinalIgnoreCase).ToList();
            var removed = before.TargetTenantIds.Except(stored.TargetTenantIds, StringComparer.OrdinalIgnoreCase).ToList();
            return new CrossTenantGrantPermissionsResponse
            {
                IsSuccess = true,
                TenantId = ctx.TenantId,
                TargetTenantIds = stored.TargetTenantIds,
                Added = added,
                Removed = removed,
                Message = $"Cross-tenant grant permissions stored: {added.Count} added, {removed.Count} removed."
            };
        }
        catch (Exception ex)
        {
            return new CrossTenantGrantPermissionsResponse
            {
                IsSuccess = false,
                TenantId = ctx.TenantId,
                ErrorMessage = ServiceProblemMessage.From(ex).Message
            };
        }
    }
}
