using System.ComponentModel;
using Meshmakers.Octo.Backend.McpServices.Models;
using Meshmakers.Octo.Backend.McpServices.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using ModelContextProtocol.Server;

// ReSharper disable UnusedMember.Global

namespace Meshmakers.Octo.Backend.McpServices.Tools;

/// <summary>
///     Admin provisioning tools — manage cross-tenant admin entitlement bootstrap. Mirrors octo-cli
///     AdminProvisioning commands. Must be called from the system tenant context (the resolved tenant must be
///     the parent / system tenant).
/// </summary>
[McpServerToolType]
public sealed class AdminProvisioningTools
{
    /// <summary>List admin provisioning mappings for a target tenant.</summary>
    [McpServerTool(Name = "get_admin_provisioning_mappings")]
    [Description(
        "List admin provisioning mappings for a target tenant — users from other tenants that may be promoted " +
        "to admin in this target. Equivalent to octo-cli GetAdminProvisioningMappings.")]
    public static async Task<GetAdminProvisioningMappingsResponse> GetMappings(
        McpServer server,
        [Description("Target tenant ID.")] string targetTenantId,
        [Description("Calling/system tenant. Falls back to URL route.")] string? tenantId = null)
    {
        if (string.IsNullOrWhiteSpace(targetTenantId))
        {
            return new GetAdminProvisioningMappingsResponse
            {
                IsSuccess = false,
                ErrorMessage = "targetTenantId is required."
            };
        }

        var ctx = await IdentityClientContext.TryBuildAsync(server, tenantId);
        if (ctx.Error != null)
        {
            return new GetAdminProvisioningMappingsResponse { IsSuccess = false, ErrorMessage = ctx.Error };
        }

        try
        {
            var mappings = (await ctx.Client!.GetAdminProvisioningMappings(targetTenantId)).ToList();
            return new GetAdminProvisioningMappingsResponse
            {
                IsSuccess = true,
                TenantId = ctx.TenantId,
                TargetTenantId = targetTenantId,
                Mappings = mappings,
                TotalCount = mappings.Count
            };
        }
        catch (Exception ex)
        {
            return new GetAdminProvisioningMappingsResponse { IsSuccess = false, ErrorMessage = ex.Message };
        }
    }

    /// <summary>Create a new admin provisioning mapping for a target tenant.</summary>
    [McpServerTool(Name = "create_admin_provisioning_mapping")]
    [McpRisk(McpRiskLevel.High)]
    [Description("Create a new admin provisioning mapping. Equivalent to octo-cli CreateAdminProvisioningMapping.")]
    public static async Task<AdminProvisioningResponse> CreateMapping(
        McpServer server,
        [Description("Target tenant ID.")] string targetTenantId,
        [Description("Source tenant ID.")] string sourceTenantId,
        [Description("Source user ID.")] string sourceUserId,
        [Description("Source user name.")] string sourceUserName,
        [Description("Optional list of role IDs to assign.")] List<string>? roleIds = null,
        [Description("Calling/system tenant. Falls back to URL route.")] string? tenantId = null)
    {
        if (string.IsNullOrWhiteSpace(targetTenantId) || string.IsNullOrWhiteSpace(sourceTenantId) ||
            string.IsNullOrWhiteSpace(sourceUserId) || string.IsNullOrWhiteSpace(sourceUserName))
        {
            return new AdminProvisioningResponse
            {
                IsSuccess = false,
                ErrorMessage = "targetTenantId, sourceTenantId, sourceUserId and sourceUserName are required."
            };
        }

        var ctx = await IdentityClientContext.TryBuildAsync(server, tenantId);
        if (ctx.Error != null)
        {
            return new AdminProvisioningResponse { IsSuccess = false, ErrorMessage = ctx.Error };
        }

        try
        {
            await ctx.Client!.CreateAdminProvisioningMapping(targetTenantId,
                new CreateExternalTenantUserMappingDto
                {
                    SourceTenantId = sourceTenantId,
                    SourceUserId = sourceUserId,
                    SourceUserName = sourceUserName,
                    RoleIds = roleIds
                });
            return new AdminProvisioningResponse
            {
                IsSuccess = true,
                TenantId = ctx.TenantId,
                TargetTenantId = targetTenantId,
                Message =
                    $"Admin provisioning mapping created in '{targetTenantId}' for '{sourceUserName}' " +
                    $"from '{sourceTenantId}'."
            };
        }
        catch (Exception ex)
        {
            return new AdminProvisioningResponse { IsSuccess = false, ErrorMessage = ex.Message };
        }
    }

    /// <summary>Provision the calling user as admin in a target tenant.</summary>
    [McpServerTool(Name = "provision_current_user_as_admin")]
    [McpRisk(McpRiskLevel.High)]
    [Description(
        "Provision the calling user as admin in a target tenant via the matching admin-provisioning mapping. " +
        "Equivalent to octo-cli ProvisionCurrentUser.")]
    public static async Task<AdminProvisioningResponse> ProvisionCurrentUser(
        McpServer server,
        [Description("Target tenant ID.")] string targetTenantId,
        [Description("Calling/system tenant. Falls back to URL route.")] string? tenantId = null)
    {
        if (string.IsNullOrWhiteSpace(targetTenantId))
        {
            return new AdminProvisioningResponse
            {
                IsSuccess = false,
                ErrorMessage = "targetTenantId is required."
            };
        }

        var ctx = await IdentityClientContext.TryBuildAsync(server, tenantId);
        if (ctx.Error != null)
        {
            return new AdminProvisioningResponse { IsSuccess = false, ErrorMessage = ctx.Error };
        }

        try
        {
            await ctx.Client!.ProvisionCurrentUser(targetTenantId);
            return new AdminProvisioningResponse
            {
                IsSuccess = true,
                TenantId = ctx.TenantId,
                TargetTenantId = targetTenantId,
                Message = $"Current user provisioned as admin in tenant '{targetTenantId}'."
            };
        }
        catch (Exception ex)
        {
            return new AdminProvisioningResponse { IsSuccess = false, ErrorMessage = ex.Message };
        }
    }

    /// <summary>Delete an admin provisioning mapping. Destructive: requires confirm=true.</summary>
    [McpServerTool(Name = "delete_admin_provisioning_mapping")]
    [McpRisk(McpRiskLevel.Medium)]
    [Description(
        "Delete an admin provisioning mapping. DESTRUCTIVE — requires confirm=true. Equivalent to octo-cli " +
        "DeleteAdminProvisioningMapping.")]
    public static async Task<AdminProvisioningResponse> DeleteMapping(
        McpServer server,
        [Description("Target tenant ID.")] string targetTenantId,
        [Description("Runtime id of the mapping.")] string mappingRtId,
        [Description("Must be true to actually delete.")] bool confirm = false,
        [Description("Calling/system tenant. Falls back to URL route.")] string? tenantId = null)
    {
        if (string.IsNullOrWhiteSpace(targetTenantId) || string.IsNullOrWhiteSpace(mappingRtId))
        {
            return new AdminProvisioningResponse
            {
                IsSuccess = false,
                ErrorMessage = "targetTenantId and mappingRtId are required."
            };
        }

        if (!confirm)
        {
            return new AdminProvisioningResponse
            {
                IsSuccess = false,
                ErrorMessage = $"Refusing to delete admin provisioning mapping '{mappingRtId}' without confirm=true."
            };
        }

        var ctx = await IdentityClientContext.TryBuildAsync(server, tenantId);
        if (ctx.Error != null)
        {
            return new AdminProvisioningResponse { IsSuccess = false, ErrorMessage = ctx.Error };
        }

        try
        {
            await ctx.Client!.DeleteAdminProvisioningMapping(targetTenantId, new OctoObjectId(mappingRtId));
            return new AdminProvisioningResponse
            {
                IsSuccess = true,
                TenantId = ctx.TenantId,
                TargetTenantId = targetTenantId,
                Message = $"Admin provisioning mapping '{mappingRtId}' deleted from tenant '{targetTenantId}'."
            };
        }
        catch (Exception ex)
        {
            return new AdminProvisioningResponse { IsSuccess = false, ErrorMessage = ex.Message };
        }
    }
}
