using System.ComponentModel;
using Meshmakers.Octo.Backend.McpServices.Models;
using Meshmakers.Octo.Backend.McpServices.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using ModelContextProtocol.Server;

// ReSharper disable UnusedMember.Global

namespace Meshmakers.Octo.Backend.McpServices.Tools;

/// <summary>
///     External tenant user mappings. Mirrors octo-cli ExternalTenantUserMappings commands. Lets a tenant grant
///     roles to users originating from a different tenant.
/// </summary>
[McpServerToolType]
public sealed class ExternalTenantUserMappingTools
{
    /// <summary>List mappings, optionally filtered by source tenant.</summary>
    [McpServerTool(Name = "get_external_tenant_user_mappings")]
    [Description("List external tenant user mappings, optionally filtered. Equivalent to octo-cli GetExternalTenantUserMappings.")]
    public static async Task<GetExternalTenantUserMappingsResponse> GetMappings(
        McpServer server,
        [Description("Optional source-tenant filter.")] string? sourceTenantId = null,
        [Description("Skip offset.")] int? skip = null,
        [Description("Page size.")] int? take = null,
        [Description("Tenant to operate on. Falls back to URL route.")] string? tenantId = null)
    {
        var ctx = IdentityClientContext.TryBuild(server, tenantId);
        if (ctx.Error != null)
        {
            return new GetExternalTenantUserMappingsResponse { IsSuccess = false, ErrorMessage = ctx.Error };
        }

        try
        {
            var mappings = (await ctx.Client!.GetExternalTenantUserMappings(skip, take, sourceTenantId)).ToList();
            return new GetExternalTenantUserMappingsResponse
            {
                IsSuccess = true,
                TenantId = ctx.TenantId,
                Mappings = mappings,
                TotalCount = mappings.Count
            };
        }
        catch (Exception ex)
        {
            return new GetExternalTenantUserMappingsResponse { IsSuccess = false, ErrorMessage = ex.Message };
        }
    }

    /// <summary>Get a single mapping by runtime id.</summary>
    [McpServerTool(Name = "get_external_tenant_user_mapping")]
    [Description("Get a single external tenant user mapping by runtime id. Equivalent to octo-cli GetExternalTenantUserMapping.")]
    public static async Task<ExternalTenantUserMappingResponse> GetMapping(
        McpServer server,
        [Description("Runtime id of the mapping.")] string rtId,
        [Description("Tenant to operate on. Falls back to URL route.")] string? tenantId = null)
    {
        if (string.IsNullOrWhiteSpace(rtId))
        {
            return new ExternalTenantUserMappingResponse { IsSuccess = false, ErrorMessage = "rtId is required." };
        }

        var ctx = IdentityClientContext.TryBuild(server, tenantId);
        if (ctx.Error != null)
        {
            return new ExternalTenantUserMappingResponse { IsSuccess = false, ErrorMessage = ctx.Error };
        }

        try
        {
            var mapping = await ctx.Client!.GetExternalTenantUserMapping(new OctoObjectId(rtId));
            return new ExternalTenantUserMappingResponse
            {
                IsSuccess = true,
                TenantId = ctx.TenantId,
                RtId = rtId,
                Mapping = mapping
            };
        }
        catch (Exception ex)
        {
            return new ExternalTenantUserMappingResponse { IsSuccess = false, ErrorMessage = ex.Message };
        }
    }

    /// <summary>Create a new mapping.</summary>
    [McpServerTool(Name = "create_external_tenant_user_mapping")]
    [Description("Create a new external tenant user mapping. Equivalent to octo-cli CreateExternalTenantUserMapping.")]
    public static async Task<ExternalTenantUserMappingResponse> CreateMapping(
        McpServer server,
        [Description("Source tenant ID.")] string sourceTenantId,
        [Description("Source user ID.")] string sourceUserId,
        [Description("Source user name.")] string sourceUserName,
        [Description("Optional list of role IDs to assign.")] List<string>? roleIds = null,
        [Description("Tenant to operate on. Falls back to URL route.")] string? tenantId = null)
    {
        if (string.IsNullOrWhiteSpace(sourceTenantId) || string.IsNullOrWhiteSpace(sourceUserId) ||
            string.IsNullOrWhiteSpace(sourceUserName))
        {
            return new ExternalTenantUserMappingResponse
            {
                IsSuccess = false,
                ErrorMessage = "sourceTenantId, sourceUserId and sourceUserName are required."
            };
        }

        var ctx = IdentityClientContext.TryBuild(server, tenantId);
        if (ctx.Error != null)
        {
            return new ExternalTenantUserMappingResponse { IsSuccess = false, ErrorMessage = ctx.Error };
        }

        try
        {
            await ctx.Client!.CreateExternalTenantUserMapping(new CreateExternalTenantUserMappingDto
            {
                SourceTenantId = sourceTenantId,
                SourceUserId = sourceUserId,
                SourceUserName = sourceUserName,
                RoleIds = roleIds
            });
            return new ExternalTenantUserMappingResponse
            {
                IsSuccess = true,
                TenantId = ctx.TenantId,
                Message = $"Mapping created for user '{sourceUserName}' from tenant '{sourceTenantId}'."
            };
        }
        catch (Exception ex)
        {
            return new ExternalTenantUserMappingResponse { IsSuccess = false, ErrorMessage = ex.Message };
        }
    }

    /// <summary>Update the role assignments on a mapping.</summary>
    [McpServerTool(Name = "update_external_tenant_user_mapping")]
    [Description("Update the role assignments on an external tenant user mapping. Equivalent to octo-cli UpdateExternalTenantUserMapping.")]
    public static async Task<ExternalTenantUserMappingResponse> UpdateMapping(
        McpServer server,
        [Description("Runtime id of the mapping.")] string rtId,
        [Description("New list of role IDs.")] List<string>? roleIds = null,
        [Description("Tenant to operate on. Falls back to URL route.")] string? tenantId = null)
    {
        if (string.IsNullOrWhiteSpace(rtId))
        {
            return new ExternalTenantUserMappingResponse { IsSuccess = false, ErrorMessage = "rtId is required." };
        }

        var ctx = IdentityClientContext.TryBuild(server, tenantId);
        if (ctx.Error != null)
        {
            return new ExternalTenantUserMappingResponse { IsSuccess = false, ErrorMessage = ctx.Error };
        }

        try
        {
            await ctx.Client!.UpdateExternalTenantUserMapping(new OctoObjectId(rtId),
                new UpdateExternalTenantUserMappingDto { RoleIds = roleIds });
            return new ExternalTenantUserMappingResponse
            {
                IsSuccess = true,
                TenantId = ctx.TenantId,
                RtId = rtId,
                Message = $"Mapping '{rtId}' updated."
            };
        }
        catch (Exception ex)
        {
            return new ExternalTenantUserMappingResponse { IsSuccess = false, ErrorMessage = ex.Message };
        }
    }

    /// <summary>Delete a mapping. Destructive: requires confirm=true.</summary>
    [McpServerTool(Name = "delete_external_tenant_user_mapping")]
    [Description("Delete an external tenant user mapping. DESTRUCTIVE — requires confirm=true. Equivalent to octo-cli DeleteExternalTenantUserMapping.")]
    public static async Task<ExternalTenantUserMappingResponse> DeleteMapping(
        McpServer server,
        [Description("Runtime id of the mapping.")] string rtId,
        [Description("Must be true to actually delete.")] bool confirm = false,
        [Description("Tenant to operate on. Falls back to URL route.")] string? tenantId = null)
    {
        if (string.IsNullOrWhiteSpace(rtId))
        {
            return new ExternalTenantUserMappingResponse { IsSuccess = false, ErrorMessage = "rtId is required." };
        }

        if (!confirm)
        {
            return new ExternalTenantUserMappingResponse
            {
                IsSuccess = false,
                ErrorMessage = $"Refusing to delete mapping '{rtId}' without confirm=true."
            };
        }

        var ctx = IdentityClientContext.TryBuild(server, tenantId);
        if (ctx.Error != null)
        {
            return new ExternalTenantUserMappingResponse { IsSuccess = false, ErrorMessage = ctx.Error };
        }

        try
        {
            await ctx.Client!.DeleteExternalTenantUserMapping(new OctoObjectId(rtId));
            return new ExternalTenantUserMappingResponse
            {
                IsSuccess = true,
                TenantId = ctx.TenantId,
                RtId = rtId,
                Message = $"Mapping '{rtId}' deleted."
            };
        }
        catch (Exception ex)
        {
            return new ExternalTenantUserMappingResponse { IsSuccess = false, ErrorMessage = ex.Message };
        }
    }
}
