using System.ComponentModel;
using Meshmakers.Octo.Backend.McpServices.Models;
using Meshmakers.Octo.Backend.McpServices.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using ModelContextProtocol.Server;

// ReSharper disable UnusedMember.Global

namespace Meshmakers.Octo.Backend.McpServices.Tools;

/// <summary>OAuth API scope management tools. Mirrors octo-cli ApiScopes commands.</summary>
[McpServerToolType]
public sealed class ApiScopeTools
{
    /// <summary>List all API scopes in the tenant.</summary>
    [McpServerTool(Name = "get_api_scopes")]
    [Description("List all OAuth API scopes in the tenant. Equivalent to octo-cli GetApiScopes.")]
    public static async Task<GetApiScopesResponse> GetApiScopes(
        McpServer server,
        [Description("Tenant to operate on. Falls back to URL route.")] string? tenantId = null)
    {
        var ctx = IdentityClientContext.TryBuild(server, tenantId);
        if (ctx.Error != null)
        {
            return new GetApiScopesResponse { IsSuccess = false, ErrorMessage = ctx.Error };
        }

        try
        {
            var scopes = (await ctx.Client!.GetApiScopes()).ToList();
            return new GetApiScopesResponse
            {
                IsSuccess = true,
                TenantId = ctx.TenantId,
                ApiScopes = scopes,
                TotalCount = scopes.Count,
                Message = scopes.Count == 0 ? "No API scopes." : $"{scopes.Count} API scope(s)."
            };
        }
        catch (Exception ex)
        {
            return new GetApiScopesResponse { IsSuccess = false, ErrorMessage = ex.Message };
        }
    }

    /// <summary>Create a new API scope.</summary>
    [McpServerTool(Name = "create_api_scope")]
    [McpRisk(McpRiskLevel.High)]
    [Description("Create a new OAuth API scope. Equivalent to octo-cli CreateApiScope.")]
    public static async Task<ApiScopeResponse> CreateApiScope(
        McpServer server,
        [Description("Unique name.")] string name,
        [Description("Optional display name.")] string? displayName = null,
        [Description("Optional description.")] string? description = null,
        [Description("Enabled flag (default true).")] bool isEnabled = true,
        [Description("Tenant to operate on. Falls back to URL route.")] string? tenantId = null)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return new ApiScopeResponse { IsSuccess = false, ErrorMessage = "name is required." };
        }

        var ctx = IdentityClientContext.TryBuild(server, tenantId);
        if (ctx.Error != null)
        {
            return new ApiScopeResponse { IsSuccess = false, ErrorMessage = ctx.Error };
        }

        try
        {
            await ctx.Client!.CreateApiScope(new ApiScopeDto
            {
                IsEnabled = isEnabled,
                Name = name,
                DisplayName = displayName,
                Description = description
            });
            return new ApiScopeResponse
            {
                IsSuccess = true,
                TenantId = ctx.TenantId,
                Name = name,
                Message = $"API scope '{name}' created."
            };
        }
        catch (Exception ex)
        {
            return new ApiScopeResponse { IsSuccess = false, ErrorMessage = ex.Message };
        }
    }

    /// <summary>Update an existing API scope.</summary>
    [McpServerTool(Name = "update_api_scope")]
    [McpRisk(McpRiskLevel.High)]
    [Description("Update an existing OAuth API scope. Equivalent to octo-cli UpdateApiScope.")]
    public static async Task<ApiScopeResponse> UpdateApiScope(
        McpServer server,
        [Description("Existing scope name.")] string name,
        [Description("Optional new display name.")] string? displayName = null,
        [Description("Optional new description.")] string? description = null,
        [Description("Optional new enabled flag.")] bool? isEnabled = null,
        [Description("Tenant to operate on. Falls back to URL route.")] string? tenantId = null)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return new ApiScopeResponse { IsSuccess = false, ErrorMessage = "name is required." };
        }

        var ctx = IdentityClientContext.TryBuild(server, tenantId);
        if (ctx.Error != null)
        {
            return new ApiScopeResponse { IsSuccess = false, ErrorMessage = ctx.Error };
        }

        try
        {
            var dto = new ApiScopeDto
            {
                Name = name,
                DisplayName = displayName,
                Description = description,
                IsEnabled = isEnabled ?? true
            };
            await ctx.Client!.UpdateApiScope(name, dto);
            return new ApiScopeResponse
            {
                IsSuccess = true,
                TenantId = ctx.TenantId,
                Name = name,
                Message = $"API scope '{name}' updated."
            };
        }
        catch (Exception ex)
        {
            return new ApiScopeResponse { IsSuccess = false, ErrorMessage = ex.Message };
        }
    }

    /// <summary>Delete an API scope. Destructive: requires confirm=true.</summary>
    [McpServerTool(Name = "delete_api_scope")]
    [McpRisk(McpRiskLevel.High)]
    [Description(
        "Delete an OAuth API scope. DESTRUCTIVE — requires confirm=true. Equivalent to octo-cli DeleteApiScope.")]
    public static async Task<ApiScopeResponse> DeleteApiScope(
        McpServer server,
        [Description("Scope name.")] string name,
        [Description("Must be true to actually delete.")] bool confirm = false,
        [Description("Tenant to operate on. Falls back to URL route.")] string? tenantId = null)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return new ApiScopeResponse { IsSuccess = false, ErrorMessage = "name is required." };
        }

        if (!confirm)
        {
            return new ApiScopeResponse
            {
                IsSuccess = false,
                ErrorMessage = $"Refusing to delete API scope '{name}' without confirm=true."
            };
        }

        var ctx = IdentityClientContext.TryBuild(server, tenantId);
        if (ctx.Error != null)
        {
            return new ApiScopeResponse { IsSuccess = false, ErrorMessage = ctx.Error };
        }

        try
        {
            await ctx.Client!.DeleteScope(name);
            return new ApiScopeResponse
            {
                IsSuccess = true,
                TenantId = ctx.TenantId,
                Name = name,
                Message = $"API scope '{name}' deleted."
            };
        }
        catch (Exception ex)
        {
            return new ApiScopeResponse { IsSuccess = false, ErrorMessage = ex.Message };
        }
    }
}
