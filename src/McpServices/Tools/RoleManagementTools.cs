using System.ComponentModel;
using Meshmakers.Octo.Backend.McpServices.Models;
using Meshmakers.Octo.Backend.McpServices.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using ModelContextProtocol.Server;

// ReSharper disable UnusedMember.Global

namespace Meshmakers.Octo.Backend.McpServices.Tools;

/// <summary>
///     Role management tools (Identity service). Mirrors octo-cli GetRoles/CreateRole/UpdateRole/DeleteRole.
/// </summary>
[McpServerToolType]
public sealed class RoleManagementTools
{
    /// <summary>List all roles in the tenant.</summary>
    [McpServerTool(Name = "get_roles")]
    [Description("List all roles in the tenant. Equivalent to octo-cli GetRoles.")]
    public static async Task<GetRolesResponse> GetRoles(
        McpServer server,
        [Description("Tenant to operate on. Falls back to URL route.")] string? tenantId = null)
    {
        var ctx = await IdentityClientContext.TryBuildAsync(server, tenantId);
        if (ctx.Error != null)
        {
            return new GetRolesResponse { IsSuccess = false, ErrorMessage = ctx.Error };
        }

        try
        {
            var roles = (await ctx.Client!.GetRoles()).ToList();
            return new GetRolesResponse
            {
                IsSuccess = true,
                TenantId = ctx.TenantId,
                Roles = roles,
                TotalCount = roles.Count,
                Message = roles.Count == 0 ? "No roles found." : $"Found {roles.Count} role(s)."
            };
        }
        catch (Exception ex)
        {
            return new GetRolesResponse { IsSuccess = false, ErrorMessage = ex.Message };
        }
    }

    /// <summary>Create a new role.</summary>
    [McpServerTool(Name = "create_role")]
    [McpRisk(McpRiskLevel.Medium)]
    [Description("Create a new role. Equivalent to octo-cli CreateRole.")]
    public static async Task<RoleResponse> CreateRole(
        McpServer server,
        [Description("Role name.")] string roleName,
        [Description("Tenant to operate on. Falls back to URL route.")] string? tenantId = null)
    {
        if (string.IsNullOrWhiteSpace(roleName))
        {
            return new RoleResponse { IsSuccess = false, ErrorMessage = "roleName is required." };
        }

        var ctx = await IdentityClientContext.TryBuildAsync(server, tenantId);
        if (ctx.Error != null)
        {
            return new RoleResponse { IsSuccess = false, ErrorMessage = ctx.Error };
        }

        try
        {
            await ctx.Client!.CreateRole(new RoleDto { Name = roleName });
            return new RoleResponse
            {
                IsSuccess = true,
                TenantId = ctx.TenantId,
                RoleName = roleName,
                Message = $"Role '{roleName}' created."
            };
        }
        catch (Exception ex)
        {
            return new RoleResponse { IsSuccess = false, ErrorMessage = ex.Message };
        }
    }

    /// <summary>Rename a role.</summary>
    [McpServerTool(Name = "update_role")]
    [McpRisk(McpRiskLevel.Medium)]
    [Description("Rename an existing role. Equivalent to octo-cli UpdateRole.")]
    public static async Task<RoleResponse> UpdateRole(
        McpServer server,
        [Description("Existing role name.")] string roleName,
        [Description("New role name.")] string newRoleName,
        [Description("Tenant to operate on. Falls back to URL route.")] string? tenantId = null)
    {
        if (string.IsNullOrWhiteSpace(roleName) || string.IsNullOrWhiteSpace(newRoleName))
        {
            return new RoleResponse { IsSuccess = false, ErrorMessage = "roleName and newRoleName are required." };
        }

        var ctx = await IdentityClientContext.TryBuildAsync(server, tenantId);
        if (ctx.Error != null)
        {
            return new RoleResponse { IsSuccess = false, ErrorMessage = ctx.Error };
        }

        try
        {
            await ctx.Client!.UpdateRole(roleName, new RoleDto { Name = newRoleName });
            return new RoleResponse
            {
                IsSuccess = true,
                TenantId = ctx.TenantId,
                RoleName = newRoleName,
                Message = $"Role '{roleName}' renamed to '{newRoleName}'."
            };
        }
        catch (Exception ex)
        {
            return new RoleResponse { IsSuccess = false, ErrorMessage = ex.Message };
        }
    }

    /// <summary>Delete a role. Destructive: requires confirm=true.</summary>
    [McpServerTool(Name = "delete_role")]
    [McpRisk(McpRiskLevel.Medium)]
    [Description("Delete a role. DESTRUCTIVE — requires confirm=true. Equivalent to octo-cli DeleteRole.")]
    public static async Task<RoleResponse> DeleteRole(
        McpServer server,
        [Description("Role name to delete.")] string roleName,
        [Description("Must be true to actually delete.")] bool confirm = false,
        [Description("Tenant to operate on. Falls back to URL route.")] string? tenantId = null)
    {
        if (string.IsNullOrWhiteSpace(roleName))
        {
            return new RoleResponse { IsSuccess = false, ErrorMessage = "roleName is required." };
        }

        if (!confirm)
        {
            return new RoleResponse
            {
                IsSuccess = false,
                ErrorMessage = $"Refusing to delete role '{roleName}' without confirm=true."
            };
        }

        var ctx = await IdentityClientContext.TryBuildAsync(server, tenantId);
        if (ctx.Error != null)
        {
            return new RoleResponse { IsSuccess = false, ErrorMessage = ctx.Error };
        }

        try
        {
            await ctx.Client!.DeleteRole(roleName);
            return new RoleResponse
            {
                IsSuccess = true,
                TenantId = ctx.TenantId,
                RoleName = roleName,
                Message = $"Role '{roleName}' deleted."
            };
        }
        catch (Exception ex)
        {
            return new RoleResponse { IsSuccess = false, ErrorMessage = ex.Message };
        }
    }
}
