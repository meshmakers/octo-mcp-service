using System.ComponentModel;
using Meshmakers.Octo.Backend.McpServices.Models;
using Meshmakers.Octo.Backend.McpServices.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using ModelContextProtocol.Server;

// ReSharper disable UnusedMember.Global

namespace Meshmakers.Octo.Backend.McpServices.Tools;

/// <summary>
///     Group management tools (Identity service). Mirrors octo-cli Groups commands.
/// </summary>
[McpServerToolType]
public sealed class GroupManagementTools
{
    /// <summary>List all groups in the tenant.</summary>
    [McpServerTool(Name = "get_groups")]
    [Description("List all groups in the tenant. Equivalent to octo-cli GetGroups.")]
    public static async Task<GetGroupsResponse> GetGroups(
        McpServer server,
        [Description("Tenant to operate on. Falls back to URL route.")] string? tenantId = null)
    {
        var ctx = IdentityClientContext.TryBuild(server, tenantId);
        if (ctx.Error != null)
        {
            return new GetGroupsResponse { IsSuccess = false, ErrorMessage = ctx.Error };
        }

        try
        {
            var groups = (await ctx.Client!.GetGroups()).ToList();
            return new GetGroupsResponse
            {
                IsSuccess = true,
                TenantId = ctx.TenantId,
                Groups = groups,
                TotalCount = groups.Count,
                Message = groups.Count == 0 ? "No groups found." : $"Found {groups.Count} group(s)."
            };
        }
        catch (Exception ex)
        {
            return new GetGroupsResponse { IsSuccess = false, ErrorMessage = ex.Message };
        }
    }

    /// <summary>Get a single group by its runtime ID.</summary>
    [McpServerTool(Name = "get_group")]
    [Description("Get a single group by its runtime ID. Equivalent to octo-cli GetGroup.")]
    public static async Task<GroupResponse> GetGroup(
        McpServer server,
        [Description("Runtime ID of the group.")] string groupId,
        [Description("Tenant to operate on. Falls back to URL route.")] string? tenantId = null)
    {
        if (string.IsNullOrWhiteSpace(groupId))
        {
            return new GroupResponse { IsSuccess = false, ErrorMessage = "groupId is required." };
        }

        var ctx = IdentityClientContext.TryBuild(server, tenantId);
        if (ctx.Error != null)
        {
            return new GroupResponse { IsSuccess = false, ErrorMessage = ctx.Error };
        }

        try
        {
            var group = await ctx.Client!.GetGroup(new OctoObjectId(groupId));
            return new GroupResponse
            {
                IsSuccess = true,
                TenantId = ctx.TenantId,
                GroupId = groupId,
                Group = group
            };
        }
        catch (Exception ex)
        {
            return new GroupResponse { IsSuccess = false, ErrorMessage = ex.Message };
        }
    }

    /// <summary>Create a new group.</summary>
    [McpServerTool(Name = "create_group")]
    [McpRisk(McpRiskLevel.Medium)]
    [Description(
        "Create a new group with optional description and initial role assignments. Equivalent to octo-cli " +
        "CreateGroup.")]
    public static async Task<GroupResponse> CreateGroup(
        McpServer server,
        [Description("Group name (required).")] string groupName,
        [Description("Optional description.")] string? description = null,
        [Description("Optional list of role IDs (or names) to attach to the group.")] List<string>? roleIds = null,
        [Description("Tenant to operate on. Falls back to URL route.")] string? tenantId = null)
    {
        if (string.IsNullOrWhiteSpace(groupName))
        {
            return new GroupResponse { IsSuccess = false, ErrorMessage = "groupName is required." };
        }

        var ctx = IdentityClientContext.TryBuild(server, tenantId);
        if (ctx.Error != null)
        {
            return new GroupResponse { IsSuccess = false, ErrorMessage = ctx.Error };
        }

        try
        {
            await ctx.Client!.CreateGroup(new CreateGroupDto
            {
                GroupName = groupName,
                GroupDescription = description,
                RoleIds = roleIds
            });

            return new GroupResponse
            {
                IsSuccess = true,
                TenantId = ctx.TenantId,
                Message = $"Group '{groupName}' created."
            };
        }
        catch (Exception ex)
        {
            return new GroupResponse { IsSuccess = false, ErrorMessage = ex.Message };
        }
    }

    /// <summary>Update group metadata (name, description).</summary>
    [McpServerTool(Name = "update_group")]
    [McpRisk(McpRiskLevel.Medium)]
    [Description("Update group metadata (name, description). Equivalent to octo-cli UpdateGroup.")]
    public static async Task<GroupResponse> UpdateGroup(
        McpServer server,
        [Description("Runtime ID of the group.")] string groupId,
        [Description("New group name.")] string groupName,
        [Description("Optional new description.")] string? description = null,
        [Description("Tenant to operate on. Falls back to URL route.")] string? tenantId = null)
    {
        if (string.IsNullOrWhiteSpace(groupId) || string.IsNullOrWhiteSpace(groupName))
        {
            return new GroupResponse { IsSuccess = false, ErrorMessage = "groupId and groupName are required." };
        }

        var ctx = IdentityClientContext.TryBuild(server, tenantId);
        if (ctx.Error != null)
        {
            return new GroupResponse { IsSuccess = false, ErrorMessage = ctx.Error };
        }

        try
        {
            await ctx.Client!.UpdateGroup(new OctoObjectId(groupId), new UpdateGroupDto
            {
                GroupName = groupName,
                GroupDescription = description
            });
            return new GroupResponse
            {
                IsSuccess = true,
                TenantId = ctx.TenantId,
                GroupId = groupId,
                Message = $"Group '{groupId}' updated."
            };
        }
        catch (Exception ex)
        {
            return new GroupResponse { IsSuccess = false, ErrorMessage = ex.Message };
        }
    }

    /// <summary>Delete a group. Destructive: requires confirm=true.</summary>
    [McpServerTool(Name = "delete_group")]
    [McpRisk(McpRiskLevel.Medium)]
    [Description("Delete a group. DESTRUCTIVE — requires confirm=true. Equivalent to octo-cli DeleteGroup.")]
    public static async Task<GroupResponse> DeleteGroup(
        McpServer server,
        [Description("Runtime ID of the group.")] string groupId,
        [Description("Must be true to actually delete.")] bool confirm = false,
        [Description("Tenant to operate on. Falls back to URL route.")] string? tenantId = null)
    {
        if (string.IsNullOrWhiteSpace(groupId))
        {
            return new GroupResponse { IsSuccess = false, ErrorMessage = "groupId is required." };
        }

        if (!confirm)
        {
            return new GroupResponse
            {
                IsSuccess = false,
                ErrorMessage = $"Refusing to delete group '{groupId}' without confirm=true."
            };
        }

        var ctx = IdentityClientContext.TryBuild(server, tenantId);
        if (ctx.Error != null)
        {
            return new GroupResponse { IsSuccess = false, ErrorMessage = ctx.Error };
        }

        try
        {
            await ctx.Client!.DeleteGroup(new OctoObjectId(groupId));
            return new GroupResponse
            {
                IsSuccess = true,
                TenantId = ctx.TenantId,
                GroupId = groupId,
                Message = $"Group '{groupId}' deleted."
            };
        }
        catch (Exception ex)
        {
            return new GroupResponse { IsSuccess = false, ErrorMessage = ex.Message };
        }
    }

    /// <summary>Replace the roles attached to a group.</summary>
    [McpServerTool(Name = "update_group_roles")]
    [McpRisk(McpRiskLevel.Medium)]
    [Description(
        "Replace the role assignments of a group. Equivalent to octo-cli UpdateGroupRoles. Pass the full target " +
        "list — semantics are replace-all, not merge.")]
    public static async Task<GroupResponse> UpdateGroupRoles(
        McpServer server,
        [Description("Runtime ID of the group.")] string groupId,
        [Description("Full target list of role IDs.")] List<string> roleIds,
        [Description("Tenant to operate on. Falls back to URL route.")] string? tenantId = null)
    {
        if (string.IsNullOrWhiteSpace(groupId))
        {
            return new GroupResponse { IsSuccess = false, ErrorMessage = "groupId is required." };
        }

        var ctx = IdentityClientContext.TryBuild(server, tenantId);
        if (ctx.Error != null)
        {
            return new GroupResponse { IsSuccess = false, ErrorMessage = ctx.Error };
        }

        try
        {
            await ctx.Client!.UpdateGroupRoles(new OctoObjectId(groupId), roleIds ?? []);
            return new GroupResponse
            {
                IsSuccess = true,
                TenantId = ctx.TenantId,
                GroupId = groupId,
                Message = $"Roles for group '{groupId}' updated ({roleIds?.Count ?? 0} role(s))."
            };
        }
        catch (Exception ex)
        {
            return new GroupResponse { IsSuccess = false, ErrorMessage = ex.Message };
        }
    }

    /// <summary>Add a user to a group.</summary>
    [McpServerTool(Name = "add_user_to_group")]
    [McpRisk(McpRiskLevel.Medium)]
    [Description("Add a user to a group. Equivalent to octo-cli AddUserToGroup.")]
    public static async Task<GroupResponse> AddUserToGroup(
        McpServer server,
        [Description("Runtime ID of the group.")] string groupId,
        [Description("Runtime ID of the user.")] string userId,
        [Description("Tenant to operate on. Falls back to URL route.")] string? tenantId = null)
    {
        if (string.IsNullOrWhiteSpace(groupId) || string.IsNullOrWhiteSpace(userId))
        {
            return new GroupResponse { IsSuccess = false, ErrorMessage = "groupId and userId are required." };
        }

        var ctx = IdentityClientContext.TryBuild(server, tenantId);
        if (ctx.Error != null)
        {
            return new GroupResponse { IsSuccess = false, ErrorMessage = ctx.Error };
        }

        try
        {
            await ctx.Client!.AddUserToGroup(new OctoObjectId(groupId), userId);
            return new GroupResponse
            {
                IsSuccess = true,
                TenantId = ctx.TenantId,
                GroupId = groupId,
                Message = $"User '{userId}' added to group '{groupId}'."
            };
        }
        catch (Exception ex)
        {
            return new GroupResponse { IsSuccess = false, ErrorMessage = ex.Message };
        }
    }

    /// <summary>Remove a user from a group. Destructive: requires confirm=true.</summary>
    [McpServerTool(Name = "remove_user_from_group")]
    [McpRisk(McpRiskLevel.Medium)]
    [Description(
        "Remove a user from a group. DESTRUCTIVE — requires confirm=true. Equivalent to octo-cli " +
        "RemoveUserFromGroup.")]
    public static async Task<GroupResponse> RemoveUserFromGroup(
        McpServer server,
        [Description("Runtime ID of the group.")] string groupId,
        [Description("Runtime ID of the user.")] string userId,
        [Description("Must be true to actually remove.")] bool confirm = false,
        [Description("Tenant to operate on. Falls back to URL route.")] string? tenantId = null)
    {
        if (string.IsNullOrWhiteSpace(groupId) || string.IsNullOrWhiteSpace(userId))
        {
            return new GroupResponse { IsSuccess = false, ErrorMessage = "groupId and userId are required." };
        }

        if (!confirm)
        {
            return new GroupResponse
            {
                IsSuccess = false,
                ErrorMessage = $"Refusing to remove user '{userId}' from group '{groupId}' without confirm=true."
            };
        }

        var ctx = IdentityClientContext.TryBuild(server, tenantId);
        if (ctx.Error != null)
        {
            return new GroupResponse { IsSuccess = false, ErrorMessage = ctx.Error };
        }

        try
        {
            await ctx.Client!.RemoveUserFromGroup(new OctoObjectId(groupId), userId);
            return new GroupResponse
            {
                IsSuccess = true,
                TenantId = ctx.TenantId,
                GroupId = groupId,
                Message = $"User '{userId}' removed from group '{groupId}'."
            };
        }
        catch (Exception ex)
        {
            return new GroupResponse { IsSuccess = false, ErrorMessage = ex.Message };
        }
    }

    /// <summary>Add a child group to a parent group.</summary>
    [McpServerTool(Name = "add_group_to_group")]
    [McpRisk(McpRiskLevel.Medium)]
    [Description("Nest one group inside another. Equivalent to octo-cli AddGroupToGroup.")]
    public static async Task<GroupResponse> AddGroupToGroup(
        McpServer server,
        [Description("Runtime ID of the parent group.")] string parentGroupId,
        [Description("Runtime ID of the child group to nest inside the parent.")] string childGroupId,
        [Description("Tenant to operate on. Falls back to URL route.")] string? tenantId = null)
    {
        if (string.IsNullOrWhiteSpace(parentGroupId) || string.IsNullOrWhiteSpace(childGroupId))
        {
            return new GroupResponse
            {
                IsSuccess = false,
                ErrorMessage = "parentGroupId and childGroupId are required."
            };
        }

        var ctx = IdentityClientContext.TryBuild(server, tenantId);
        if (ctx.Error != null)
        {
            return new GroupResponse { IsSuccess = false, ErrorMessage = ctx.Error };
        }

        try
        {
            await ctx.Client!.AddGroupToGroup(new OctoObjectId(parentGroupId), childGroupId);
            return new GroupResponse
            {
                IsSuccess = true,
                TenantId = ctx.TenantId,
                GroupId = parentGroupId,
                Message = $"Child group '{childGroupId}' added to parent '{parentGroupId}'."
            };
        }
        catch (Exception ex)
        {
            return new GroupResponse { IsSuccess = false, ErrorMessage = ex.Message };
        }
    }

    /// <summary>Remove a child group from a parent group. Destructive: requires confirm=true.</summary>
    [McpServerTool(Name = "remove_group_from_group")]
    [McpRisk(McpRiskLevel.Medium)]
    [Description(
        "Un-nest a child group from a parent. DESTRUCTIVE — requires confirm=true. Equivalent to octo-cli " +
        "RemoveGroupFromGroup.")]
    public static async Task<GroupResponse> RemoveGroupFromGroup(
        McpServer server,
        [Description("Runtime ID of the parent group.")] string parentGroupId,
        [Description("Runtime ID of the child group.")] string childGroupId,
        [Description("Must be true to actually remove.")] bool confirm = false,
        [Description("Tenant to operate on. Falls back to URL route.")] string? tenantId = null)
    {
        if (string.IsNullOrWhiteSpace(parentGroupId) || string.IsNullOrWhiteSpace(childGroupId))
        {
            return new GroupResponse
            {
                IsSuccess = false,
                ErrorMessage = "parentGroupId and childGroupId are required."
            };
        }

        if (!confirm)
        {
            return new GroupResponse
            {
                IsSuccess = false,
                ErrorMessage =
                    $"Refusing to remove child '{childGroupId}' from parent '{parentGroupId}' without confirm=true."
            };
        }

        var ctx = IdentityClientContext.TryBuild(server, tenantId);
        if (ctx.Error != null)
        {
            return new GroupResponse { IsSuccess = false, ErrorMessage = ctx.Error };
        }

        try
        {
            await ctx.Client!.RemoveGroupFromGroup(new OctoObjectId(parentGroupId), childGroupId);
            return new GroupResponse
            {
                IsSuccess = true,
                TenantId = ctx.TenantId,
                GroupId = parentGroupId,
                Message = $"Child group '{childGroupId}' removed from parent '{parentGroupId}'."
            };
        }
        catch (Exception ex)
        {
            return new GroupResponse { IsSuccess = false, ErrorMessage = ex.Message };
        }
    }
}
