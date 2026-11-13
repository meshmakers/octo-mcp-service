using System.ComponentModel;
using Meshmakers.Octo.Backend.McpServices.Models;
using Meshmakers.Octo.Backend.McpServices.MethodClients.DataTransferObjects.System.Identity.v3;
using Meshmakers.Octo.Backend.McpServices.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Sdk.ServiceClient.AssetRepositoryServices.Tenants.Methods;
using ModelContextProtocol.Server;

// ReSharper disable UnusedMember.Global

namespace Meshmakers.Octo.Backend.McpServices.Tools;

/// <summary>
///     User-management tools backed by the Identity service. Mirrors the octo-cli users commands.
/// </summary>
/// <remarks>
///     The role tools (<c>add_user_to_role</c>, <c>remove_user_from_role</c>) call the CK methods
///     <c>System.Identity/User.AddRole-1</c> / <c>User.RemoveRole-1</c> through the generated
///     <see cref="RtUserMethodClient" /> (AB#8440, generated from <c>System.Identity-[3.2,4.0)</c> in
///     <c>construction-kits.yaml</c>); the REST methods <c>AddRoleToUser</c> / <c>RemoveRoleFromUser</c> are obsolete
///     (AB#8416). <c>create_user</c> and <c>reset_user_password</c> call <c>User.CreateUser-1</c> /
///     <c>User.ResetPassword-1</c> the same way (AB#8418), without a REST fallback (AB#7039): a server without the method
///     gets a message naming the needed identity version. The password is a sensitive parameter: it goes only into the
///     method call, every text a tool returns is scrubbed of it (<see cref="Scrub" />), and the SDK's payload logging is
///     filtered (<see cref="Configuration.McpPayloadLogFilter" />).
/// </remarks>
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

    /// <summary>Create a new user. Optionally with a password. Runs <c>System.Identity/User.CreateUser-1</c>.</summary>
    [McpServerTool(Name = "create_user")]
    [McpRisk(McpRiskLevel.Medium)]
    [Description("Create a new user account. Equivalent to octo-cli CreateUser. Username and email are lower-cased. " +
                 "Runs the CK method System.Identity/User.CreateUser-1; refusals (USER_ALREADY_EXISTS, USER_NAME_RESERVED, " +
                 "PASSWORD_POLICY_VIOLATION with the policy description) come from the server as code and message.")]
    public static async Task<UserResponse> CreateUser(
        McpServer server,
        [Description("Username (will be lower-cased).")] string userName,
        [Description("E-mail address (will be lower-cased).")] string email,
        [Description(SensitivePasswordDescription + " Optional initial password; without it the user gets no password.")]
        string? password = null,
        [Description("Tenant to operate on. Falls back to URL route.")] string? tenantId = null)
    {
        if (string.IsNullOrWhiteSpace(userName) || string.IsNullOrWhiteSpace(email))
        {
            return new UserResponse { IsSuccess = false, ErrorMessage = "userName and email are required." };
        }

        var ctx = await IdentityClientContext.TryBuildAsync(server, tenantId, withTenantClient: true);
        if (ctx.Error != null)
        {
            return new UserResponse { IsSuccess = false, ErrorMessage = ctx.Error };
        }

        var normalizedName = userName.ToLowerInvariant();
        try
        {
            var normalizedEmail = email.ToLowerInvariant();
            var result = await new RtUserMethodClient(ctx.TenantClient!).CreateUserAsync(normalizedName,
                normalizedEmail, password: password);
            if (!result.IsSuccess)
            {
                return Failure(ctx, normalizedName, WithCode(result.Error!), password);
            }

            return new UserResponse
            {
                IsSuccess = true,
                TenantId = ctx.TenantId,
                User = normalizedName,
                Message = $"User '{normalizedName}' created."
            };
        }
        catch (CkMethodCallException ex)
        {
            return Failure(ctx, normalizedName,
                DescribePasswordMethodCallError(ex, RtUserMethodClient.CreateUserMethodId,
                    CreateUserMinimumIdentityVersion, "The user was not created."), password);
        }
        catch (Exception ex)
        {
            return Failure(ctx, normalizedName, ex.Message, password);
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

    /// <summary>
    ///     Reset the password of a user. Destructive: requires confirm=true. Runs <c>System.Identity/User.ResetPassword-1</c>.
    /// </summary>
    [McpServerTool(Name = "reset_user_password")]
    [McpRisk(McpRiskLevel.High)]
    [Description(
        "Reset the password of a user. DESTRUCTIVE — requires confirm=true. Equivalent to octo-cli ResetPassword. " +
        "Runs the CK method System.Identity/User.ResetPassword-1; refusals (PASSWORD_POLICY_VIOLATION with the policy " +
        "description, SHADOW_USER_PASSWORD_NOT_ALLOWED, METHOD_FORBIDDEN) come from the server as code and message.")]
    public static async Task<UserResponse> ResetPassword(
        McpServer server,
        [Description("Username or e-mail of the user.")] string userNameOrEmail,
        [Description(SensitivePasswordDescription + " The new password; must satisfy the tenant's password policy.")]
        string newPassword,
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

        var ctx = await IdentityClientContext.TryBuildAsync(server, tenantId, withTenantClient: true);
        if (ctx.Error != null)
        {
            return new UserResponse { IsSuccess = false, ErrorMessage = ctx.Error };
        }

        var identifier = userNameOrEmail.ToLowerInvariant();
        try
        {
            var userRtId = await ResolveUserRtIdAsync(ctx, identifier);
            var result = await new RtUserMethodClient(ctx.TenantClient!).ResetPasswordAsync(userRtId, newPassword);
            if (!result.IsSuccess)
            {
                return Failure(ctx, identifier, WithCode(result.Error!), newPassword);
            }

            return new UserResponse
            {
                IsSuccess = true,
                TenantId = ctx.TenantId,
                User = identifier,
                Message = $"Password reset for user '{identifier}'."
            };
        }
        catch (CkMethodCallException ex)
        {
            return Failure(ctx, identifier,
                DescribePasswordMethodCallError(ex, RtUserMethodClient.ResetPasswordMethodId,
                    ResetPasswordMinimumIdentityVersion, "The password was not changed."), newPassword);
        }
        catch (Exception ex)
        {
            return Failure(ctx, identifier, ex.Message, newPassword);
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

        var ctx = await IdentityClientContext.TryBuildAsync(server, tenantId, withTenantClient: true);
        if (ctx.Error != null)
        {
            return new UserResponse { IsSuccess = false, ErrorMessage = ctx.Error };
        }

        try
        {
            var identifier = userNameOrEmail.ToLowerInvariant();
            var userRtId = await ResolveUserRtIdAsync(ctx, identifier);
            var result = await new RtUserMethodClient(ctx.TenantClient!).AddRoleAsync(userRtId, roleName);
            if (!result.IsSuccess)
            {
                return new UserResponse
                {
                    IsSuccess = false,
                    TenantId = ctx.TenantId,
                    User = identifier,
                    ErrorMessage = DescribeRoleError(result.Error!, identifier, roleName)
                };
            }

            return new UserResponse
            {
                IsSuccess = true,
                TenantId = ctx.TenantId,
                User = identifier,
                Message = $"User '{identifier}' assigned to role '{roleName}'."
            };
        }
        catch (CkMethodCallException ex)
        {
            return new UserResponse { IsSuccess = false, ErrorMessage = DescribeMethodCallError(ex) };
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

        var ctx = await IdentityClientContext.TryBuildAsync(server, tenantId, withTenantClient: true);
        if (ctx.Error != null)
        {
            return new UserResponse { IsSuccess = false, ErrorMessage = ctx.Error };
        }

        try
        {
            var identifier = userNameOrEmail.ToLowerInvariant();
            var userRtId = await ResolveUserRtIdAsync(ctx, identifier);
            var result = await new RtUserMethodClient(ctx.TenantClient!).RemoveRoleAsync(userRtId, roleName);
            if (!result.IsSuccess)
            {
                return new UserResponse
                {
                    IsSuccess = false,
                    TenantId = ctx.TenantId,
                    User = identifier,
                    ErrorMessage = DescribeRoleError(result.Error!, identifier, roleName)
                };
            }

            return new UserResponse
            {
                IsSuccess = true,
                TenantId = ctx.TenantId,
                User = identifier,
                Message = $"User '{identifier}' removed from role '{roleName}'."
            };
        }
        catch (CkMethodCallException ex)
        {
            return new UserResponse { IsSuccess = false, ErrorMessage = DescribeMethodCallError(ex) };
        }
        catch (Exception ex)
        {
            return new UserResponse { IsSuccess = false, ErrorMessage = ex.Message };
        }
    }

    /// <summary>
    ///     The CK methods address the user by runtime id; the tools take a user name or e-mail address. The Identity
    ///     REST lookup accepts both and returns the runtime id in <see cref="UserDto.UserId" />.
    /// </summary>
    private static async Task<string> ResolveUserRtIdAsync(IdentityClientContext ctx, string identifier)
    {
        var user = await ctx.Client!.GetUserByNameEmailOrId(identifier);
        if (string.IsNullOrWhiteSpace(user?.UserId))
        {
            throw new InvalidOperationException($"User '{identifier}' not found.");
        }

        return user.UserId;
    }

    /// <summary>
    ///     Text of a declared error of <c>User.AddRole-1</c> / <c>User.RemoveRole-1</c>. The texts follow the former REST
    ///     answers; platform errors (<c>METHOD_FORBIDDEN</c>, <c>METHOD_TARGET_NOT_FOUND</c>, …) are thrown by the client
    ///     and reported with the exception message.
    /// </summary>
    internal static string DescribeRoleError(CkMethodError error, string user, string roleName) => error.Code switch
    {
        RtUserMethodClient.AddRoleErrors.RoleNotFound => $"Role with name '{roleName}' not found.",
        RtUserMethodClient.AddRoleErrors.RoleAlreadyAssigned => $"User '{user}' already has role '{roleName}'.",
        RtUserMethodClient.RemoveRoleErrors.RoleNotAssigned => $"User '{user}' doesn't have role '{roleName}' as a direct role.",
        _ => WithCode(error)
    };

    /// <summary>
    ///     Text of a platform error the method client throws (<c>METHOD_FORBIDDEN</c>, <c>METHOD_TARGET_NOT_FOUND</c>,
    ///     <c>METHOD_VALIDATION_FAILED</c>, …): the code and the service's message, e.g.
    ///     <c>METHOD_FORBIDDEN: The caller needs the role UserManagement.</c>
    /// </summary>
    internal static string DescribeMethodCallError(CkMethodCallException exception) => WithCode(exception.Error);

    /// <summary>The first identity release that hosts <c>User.CreateUser-1</c>.</summary>
    internal const string CreateUserMinimumIdentityVersion = "3.5.57";

    /// <summary>The first identity release that hosts <c>User.ResetPassword-1</c>.</summary>
    internal const string ResetPasswordMinimumIdentityVersion = "3.5.52";

    /// <summary>Prefix of the description of every password parameter (AB#8418).</summary>
    internal const string SensitivePasswordDescription =
        "SENSITIVE: a password. It is sent only in the CK method call, never echoed in the response and never logged.";

    internal const string Redacted = "[REDACTED]";

    /// <summary>
    ///     Text of a <see cref="CkMethodCallException" /> of a password method. A server without the method - the asset
    ///     repository rejects the document in GraphQL validation, or no handler is live (<c>METHOD_HANDLER_NOT_AVAILABLE</c>:
    ///     identity too old or <c>Methods:Identity:EnablePasswordMethods=false</c>) - gets a message naming the needed identity
    ///     version; there is no REST fallback (AB#7039). Any other refusal is reported with code and message.
    /// </summary>
    internal static string DescribePasswordMethodCallError(CkMethodCallException exception, string methodId,
        string minimumIdentityVersion, string outcome) =>
        IsMethodUnavailable(exception)
            ? $"The service does not offer the CK method {methodId}. It needs identity services {minimumIdentityVersion} " +
              "or newer with the password methods switched on (Methods:Identity:EnablePasswordMethods). " +
              $"{outcome} The tool sends passwords only through the CK method and does not fall back to the REST endpoint."
            : DescribeMethodCallError(exception);

    /// <summary>
    ///     The call was certainly not executed: no live handler, or a document the schema rejects (every GraphQL error carries
    ///     the spec section <c>extensions.number</c>).
    /// </summary>
    private static bool IsMethodUnavailable(CkMethodCallException exception) =>
        string.Equals(exception.Code, "METHOD_HANDLER_NOT_AVAILABLE", StringComparison.Ordinal) ||
        exception.Errors is { Count: > 0 } errors &&
        errors.All(e => e.Extensions != null && e.Extensions.ContainsKey("number"));

    /// <summary>Replaces every occurrence of <paramref name="secret" /> in <paramref name="text" /> with <see cref="Redacted" />.</summary>
    internal static string Scrub(string text, string? secret) =>
        string.IsNullOrEmpty(secret) ? text : text.Replace(secret, Redacted, StringComparison.Ordinal);

    private static UserResponse Failure(IdentityClientContext ctx, string user, string message, string? password) =>
        new()
        {
            IsSuccess = false,
            TenantId = ctx.TenantId,
            User = user,
            ErrorMessage = Scrub(message, password)
        };

    private static string WithCode(CkMethodError error) =>
        string.IsNullOrWhiteSpace(error.Message) ? error.Code : $"{error.Code}: {error.Message}";
}
