using System.ComponentModel;
using Meshmakers.Octo.Backend.McpServices.Models;
using Meshmakers.Octo.Backend.McpServices.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using ModelContextProtocol.Server;

// ReSharper disable UnusedMember.Global

namespace Meshmakers.Octo.Backend.McpServices.Tools;

/// <summary>OAuth API resource management tools. Mirrors octo-cli ApiResources commands.</summary>
[McpServerToolType]
public sealed class ApiResourceTools
{
    /// <summary>List all API resources in the tenant.</summary>
    [McpServerTool(Name = "get_api_resources")]
    [Description("List all OAuth API resources in the tenant. Equivalent to octo-cli GetApiResources.")]
    public static async Task<GetApiResourcesResponse> GetApiResources(
        McpServer server,
        [Description("Tenant to operate on. Falls back to URL route.")] string? tenantId = null)
    {
        var ctx = IdentityClientContext.TryBuild(server, tenantId);
        if (ctx.Error != null)
        {
            return new GetApiResourcesResponse { IsSuccess = false, ErrorMessage = ctx.Error };
        }

        try
        {
            var resources = (await ctx.Client!.GetApiResources()).ToList();
            return new GetApiResourcesResponse
            {
                IsSuccess = true,
                TenantId = ctx.TenantId,
                ApiResources = resources,
                TotalCount = resources.Count,
                Message = resources.Count == 0 ? "No API resources." : $"{resources.Count} API resource(s)."
            };
        }
        catch (Exception ex)
        {
            return new GetApiResourcesResponse { IsSuccess = false, ErrorMessage = ex.Message };
        }
    }

    /// <summary>Create a new API resource.</summary>
    [McpServerTool(Name = "create_api_resource")]
    [McpRisk(McpRiskLevel.High)]
    [Description("Create a new OAuth API resource. Equivalent to octo-cli CreateApiResource.")]
    public static async Task<ApiResourceResponse> CreateApiResource(
        McpServer server,
        [Description("Unique name.")] string name,
        [Description("Optional display name.")] string? displayName = null,
        [Description("Optional description.")] string? description = null,
        [Description("Optional list of scope names to attach.")] List<string>? scopes = null,
        [Description("Enabled flag (default true).")] bool isEnabled = true,
        [Description("Tenant to operate on. Falls back to URL route.")] string? tenantId = null)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return new ApiResourceResponse { IsSuccess = false, ErrorMessage = "name is required." };
        }

        var ctx = IdentityClientContext.TryBuild(server, tenantId);
        if (ctx.Error != null)
        {
            return new ApiResourceResponse { IsSuccess = false, ErrorMessage = ctx.Error };
        }

        try
        {
            await ctx.Client!.CreateApiResource(new ApiResourceDto
            {
                IsEnabled = isEnabled,
                Name = name,
                DisplayName = displayName,
                Description = description,
                Scopes = scopes
            });
            return new ApiResourceResponse
            {
                IsSuccess = true,
                TenantId = ctx.TenantId,
                Name = name,
                Message = $"API resource '{name}' created."
            };
        }
        catch (Exception ex)
        {
            return new ApiResourceResponse { IsSuccess = false, ErrorMessage = ex.Message };
        }
    }

    /// <summary>Update an existing API resource.</summary>
    [McpServerTool(Name = "update_api_resource")]
    [McpRisk(McpRiskLevel.High)]
    [Description(
        "Update an existing OAuth API resource. Pass only the fields you want to change. Equivalent to octo-cli " +
        "UpdateApiResource.")]
    public static async Task<ApiResourceResponse> UpdateApiResource(
        McpServer server,
        [Description("Existing name (lookup key).")] string name,
        [Description("Optional new display name.")] string? displayName = null,
        [Description("Optional new description.")] string? description = null,
        [Description("Optional new list of scope names (replace-all).")] List<string>? scopes = null,
        [Description("Optional new enabled flag.")] bool? isEnabled = null,
        [Description("Tenant to operate on. Falls back to URL route.")] string? tenantId = null)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return new ApiResourceResponse { IsSuccess = false, ErrorMessage = "name is required." };
        }

        var ctx = IdentityClientContext.TryBuild(server, tenantId);
        if (ctx.Error != null)
        {
            return new ApiResourceResponse { IsSuccess = false, ErrorMessage = ctx.Error };
        }

        try
        {
            var dto = new ApiResourceDto
            {
                Name = name,
                DisplayName = displayName,
                Description = description,
                Scopes = scopes,
                IsEnabled = isEnabled ?? true
            };
            await ctx.Client!.UpdateApiResource(name, dto);
            return new ApiResourceResponse
            {
                IsSuccess = true,
                TenantId = ctx.TenantId,
                Name = name,
                Message = $"API resource '{name}' updated."
            };
        }
        catch (Exception ex)
        {
            return new ApiResourceResponse { IsSuccess = false, ErrorMessage = ex.Message };
        }
    }

    /// <summary>Delete an API resource. Destructive: requires confirm=true.</summary>
    [McpServerTool(Name = "delete_api_resource")]
    [McpRisk(McpRiskLevel.High)]
    [Description(
        "Delete an OAuth API resource. DESTRUCTIVE — requires confirm=true. Equivalent to octo-cli " +
        "DeleteApiResource.")]
    public static async Task<ApiResourceResponse> DeleteApiResource(
        McpServer server,
        [Description("Resource name.")] string name,
        [Description("Must be true to actually delete.")] bool confirm = false,
        [Description("Tenant to operate on. Falls back to URL route.")] string? tenantId = null)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return new ApiResourceResponse { IsSuccess = false, ErrorMessage = "name is required." };
        }

        if (!confirm)
        {
            return new ApiResourceResponse
            {
                IsSuccess = false,
                ErrorMessage = $"Refusing to delete API resource '{name}' without confirm=true."
            };
        }

        var ctx = IdentityClientContext.TryBuild(server, tenantId);
        if (ctx.Error != null)
        {
            return new ApiResourceResponse { IsSuccess = false, ErrorMessage = ctx.Error };
        }

        try
        {
            await ctx.Client!.DeleteApiResource(name);
            return new ApiResourceResponse
            {
                IsSuccess = true,
                TenantId = ctx.TenantId,
                Name = name,
                Message = $"API resource '{name}' deleted."
            };
        }
        catch (Exception ex)
        {
            return new ApiResourceResponse { IsSuccess = false, ErrorMessage = ex.Message };
        }
    }
}
