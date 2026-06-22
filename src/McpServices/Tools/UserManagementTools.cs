using System.ComponentModel;
using Meshmakers.Octo.Backend.McpServices.Models;
using Meshmakers.Octo.Backend.McpServices.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using ModelContextProtocol.Server;

// ReSharper disable UnusedMember.Global

namespace Meshmakers.Octo.Backend.McpServices.Tools;

/// <summary>
///     User-management tools backed by the Identity service. Mirrors the octo-cli users commands.
/// </summary>
[McpServerToolType]
public sealed class UserManagementTools
{
    /// <summary>List all users in the tenant.</summary>
    [McpServerTool(Name = "get_users")]
    [Description("List all users in the tenant. Equivalent to octo-cli GetUsers.")]
    public static async Task<GetUsersResponse> GetUsers(
        McpServer server,
        [Description("Tenant to operate on. Falls back to URL route.")] string? tenantId = null)
    {
        var ctx = await IdentityClientContext.TryBuildAsync(server, tenantId);
        if (ctx.Error != null)
        {
            return new GetUsersResponse { IsSuccess = false, ErrorMessage = ctx.Error };
        }

        try
        {
            var users = (await ctx.Client!.GetUsers()).ToList();
            return new GetUsersResponse
            {
                IsSuccess = true,
                TenantId = ctx.TenantId,
                Users = users,
                TotalCount = users.Count,
                Message = users.Count == 0 ? "No users found." : $"Found {users.Count} user(s)."
            };
        }
        catch (Exception ex)
        {
            return new GetUsersResponse { IsSuccess = false, ErrorMessage = ex.Message };
        }
    }

    /// <summary>Create a new user. Optionally with a password.</summary>
    [McpServerTool(Name = "create_user")]
    [McpRisk(McpRiskLevel.Medium)]
    [Description("Create a new user account. Equivalent to octo-cli CreateUser. Username and email are lower-cased.")]
    public static async Task<UserResponse> CreateUser(
        McpServer server,
        [Description("Username (will be lower-cased).")] string userName,
        [Description("E-mail address (will be lower-cased).")] string email,
        [Description("Optional initial password.")] string? password = null,
        [Description("Tenant to operate on. Falls back to URL route.")] string? tenantId = null)
    {
        if (string.IsNullOrWhiteSpace(userName) || string.IsNullOrWhiteSpace(email))
        {
            return new UserResponse { IsSuccess = false, ErrorMessage = "userName and email are required." };
        }

        var ctx = await IdentityClientContext.TryBuildAsync(server, tenantId);
        if (ctx.Error != null)
        {
            return new UserResponse { IsSuccess = false, ErrorMessage = ctx.Error };
        }

        try
        {
            var normalizedName = userName.ToLowerInvariant();
            var normalizedEmail = email.ToLowerInvariant();
            await ctx.Client!.CreateUser(new RegisterUserDto
            {
                Email = normalizedEmail,
                Name = normalizedName,
                Password = password
            });

            return new UserResponse
            {
                IsSuccess = true,
                TenantId = ctx.TenantId,
                User = normalizedName,
                Message = $"User '{normalizedName}' created."
            };
        }
        catch (Exception ex)
        {
            return new UserResponse { IsSuccess = false, ErrorMessage = ex.Message };
        }
    }

    /// <summary>Update an existing user's e-mail or rename.</summary>
    [McpServerTool(Name = "update_user")]
    [McpRisk(McpRiskLevel.Medium)]
    [Description(
        "Update an existing user (e-mail and/or username). Equivalent to octo-cli UpdateUser. Pass only the " +
        "fields you want to change.")]
    public static async Task<UserResponse> UpdateUser(
        McpServer server,
        [Description("Existing username or e-mail to identify the user.")] string userNameOrEmail,
        [Description("Optional new e-mail (lower-cased).")] string? newEmail = null,
        [Description("Optional new username (lower-cased).")] string? newUserName = null,
        [Description("Tenant to operate on. Falls back to URL route.")] string? tenantId = null)
    {
        if (string.IsNullOrWhiteSpace(userNameOrEmail))
        {
            return new UserResponse { IsSuccess = false, ErrorMessage = "userNameOrEmail is required." };
        }

        if (string.IsNullOrWhiteSpace(newEmail) && string.IsNullOrWhiteSpace(newUserName))
        {
            return new UserResponse
            {
                IsSuccess = false,
                ErrorMessage = "Provide at least one of newEmail or newUserName."
            };
        }

        var ctx = await IdentityClientContext.TryBuildAsync(server, tenantId);
        if (ctx.Error != null)
        {
            return new UserResponse { IsSuccess = false, ErrorMessage = ctx.Error };
        }

        try
        {
            var identifier = userNameOrEmail.ToLowerInvariant();
            await ctx.Client!.UpdateUser(identifier, new UserDto
            {
                Email = newEmail?.ToLowerInvariant(),
                Name = newUserName?.ToLowerInvariant()
            });

            return new UserResponse
            {
                IsSuccess = true,
                TenantId = ctx.TenantId,
                User = identifier,
                Message = $"User '{identifier}' updated."
            };
        }
        catch (Exception ex)
        {
            return new UserResponse { IsSuccess = false, ErrorMessage = ex.Message };
        }
    }

    /// <summary>Delete a user. Destructive: requires confirm=true.</summary>
    [McpServerTool(Name = "delete_user")]
    [McpRisk(McpRiskLevel.Medium)]
    [Description("Delete a user. DESTRUCTIVE — requires confirm=true. Equivalent to octo-cli DeleteUser.")]
    public static async Task<UserResponse> DeleteUser(
        McpServer server,
        [Description("Username or e-mail of the user to delete.")] string userNameOrEmail,
        [Description("Must be true to actually delete.")] bool confirm = false,
        [Description("Tenant to operate on. Falls back to URL route.")] string? tenantId = null)
    {
        if (string.IsNullOrWhiteSpace(userNameOrEmail))
        {
            return new UserResponse { IsSuccess = false, ErrorMessage = "userNameOrEmail is required." };
        }

        if (!confirm)
        {
            return new UserResponse
            {
                IsSuccess = false,
                ErrorMessage = $"Refusing to delete user '{userNameOrEmail}' without confirm=true."
            };
        }

        var ctx = await IdentityClientContext.TryBuildAsync(server, tenantId);
        if (ctx.Error != null)
        {
            return new UserResponse { IsSuccess = false, ErrorMessage = ctx.Error };
        }

        try
        {
            var identifier = userNameOrEmail.ToLowerInvariant();
            await ctx.Client!.DeleteUser(identifier);
            return new UserResponse
            {
                IsSuccess = true,
                TenantId = ctx.TenantId,
                User = identifier,
                Message = $"User '{identifier}' deleted."
            };
        }
        catch (Exception ex)
        {
            return new UserResponse { IsSuccess = false, ErrorMessage = ex.Message };
        }
    }

    /// <summary>Reset the password of a user. Destructive: requires confirm=true.</summary>
    [McpServerTool(Name = "reset_user_password")]
    [McpRisk(McpRiskLevel.High)]
    [Description(
        "Reset the password of a user. DESTRUCTIVE — requires confirm=true. Equivalent to octo-cli ResetPassword.")]
    public static async Task<UserResponse> ResetPassword(
        McpServer server,
        [Description("Username or e-mail of the user.")] string userNameOrEmail,
        [Description("New password.")] string newPassword,
        [Description("Must be true to actually reset.")] bool confirm = false,
        [Description("Tenant to operate on. Falls back to URL route.")] string? tenantId = null)
    {
        if (string.IsNullOrWhiteSpace(userNameOrEmail) || string.IsNullOrWhiteSpace(newPassword))
        {
            return new UserResponse
            {
                IsSuccess = false,
                ErrorMessage = "userNameOrEmail and newPassword are required."
            };
        }

        if (!confirm)
        {
            return new UserResponse
            {
                IsSuccess = false,
                ErrorMessage = $"Refusing to reset password for '{userNameOrEmail}' without confirm=true."
            };
        }

        var ctx = await IdentityClientContext.TryBuildAsync(server, tenantId);
        if (ctx.Error != null)
        {
            return new UserResponse { IsSuccess = false, ErrorMessage = ctx.Error };
        }

        try
        {
            var identifier = userNameOrEmail.ToLowerInvariant();
            await ctx.Client!.ResetPassword(identifier, newPassword);
            return new UserResponse
            {
                IsSuccess = true,
                TenantId = ctx.TenantId,
                User = identifier,
                Message = $"Password reset for user '{identifier}'."
            };
        }
        catch (Exception ex)
        {
            return new UserResponse { IsSuccess = false, ErrorMessage = ex.Message };
        }
    }

    /// <summary>Add a role to a user.</summary>
    [McpServerTool(Name = "add_user_to_role")]
    [McpRisk(McpRiskLevel.Medium)]
    [Description("Assign a role to a user. Equivalent to octo-cli AddUserToRole.")]
    public static async Task<UserResponse> AddUserToRole(
        McpServer server,
        [Description("Username or e-mail of the user.")] string userNameOrEmail,
        [Description("Existing role name.")] string roleName,
        [Description("Tenant to operate on. Falls back to URL route.")] string? tenantId = null)
    {
        if (string.IsNullOrWhiteSpace(userNameOrEmail) || string.IsNullOrWhiteSpace(roleName))
        {
            return new UserResponse
            {
                IsSuccess = false,
                ErrorMessage = "userNameOrEmail and roleName are required."
            };
        }

        var ctx = await IdentityClientContext.TryBuildAsync(server, tenantId);
        if (ctx.Error != null)
        {
            return new UserResponse { IsSuccess = false, ErrorMessage = ctx.Error };
        }

        try
        {
            var identifier = userNameOrEmail.ToLowerInvariant();
            await ctx.Client!.AddRoleToUser(identifier, roleName);
            return new UserResponse
            {
                IsSuccess = true,
                TenantId = ctx.TenantId,
                User = identifier,
                Message = $"User '{identifier}' assigned to role '{roleName}'."
            };
        }
        catch (Exception ex)
        {
            return new UserResponse { IsSuccess = false, ErrorMessage = ex.Message };
        }
    }

    /// <summary>Remove a role from a user. Destructive: requires confirm=true.</summary>
    [McpServerTool(Name = "remove_user_from_role")]
    [McpRisk(McpRiskLevel.Medium)]
    [Description(
        "Remove a role from a user. DESTRUCTIVE — requires confirm=true. Equivalent to octo-cli RemoveUserFromRole.")]
    public static async Task<UserResponse> RemoveUserFromRole(
        McpServer server,
        [Description("Username or e-mail of the user.")] string userNameOrEmail,
        [Description("Role name to remove.")] string roleName,
        [Description("Must be true to actually remove.")] bool confirm = false,
        [Description("Tenant to operate on. Falls back to URL route.")] string? tenantId = null)
    {
        if (string.IsNullOrWhiteSpace(userNameOrEmail) || string.IsNullOrWhiteSpace(roleName))
        {
            return new UserResponse
            {
                IsSuccess = false,
                ErrorMessage = "userNameOrEmail and roleName are required."
            };
        }

        if (!confirm)
        {
            return new UserResponse
            {
                IsSuccess = false,
                ErrorMessage = $"Refusing to remove role '{roleName}' from '{userNameOrEmail}' without confirm=true."
            };
        }

        var ctx = await IdentityClientContext.TryBuildAsync(server, tenantId);
        if (ctx.Error != null)
        {
            return new UserResponse { IsSuccess = false, ErrorMessage = ctx.Error };
        }

        try
        {
            var identifier = userNameOrEmail.ToLowerInvariant();
            await ctx.Client!.RemoveRoleFromUser(identifier, roleName);
            return new UserResponse
            {
                IsSuccess = true,
                TenantId = ctx.TenantId,
                User = identifier,
                Message = $"User '{identifier}' removed from role '{roleName}'."
            };
        }
        catch (Exception ex)
        {
            return new UserResponse { IsSuccess = false, ErrorMessage = ex.Message };
        }
    }
}
