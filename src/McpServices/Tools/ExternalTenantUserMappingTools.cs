using System.ComponentModel;
using Meshmakers.Octo.Backend.McpServices.Models;
using Meshmakers.Octo.Backend.McpServices.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects.ApiErrors;
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
        var ctx = await IdentityClientContext.TryBuildAsync(server, tenantId);
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

        var ctx = await IdentityClientContext.TryBuildAsync(server, tenantId);
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

    /// <summary>
    ///     Look up exactly one user of a source tenant by exact user name or e-mail (AB#8346, identity AB#8341), with the
    ///     resolved tenant as target tenant.
    /// </summary>
    [McpServerTool(Name = "lookup_source_tenant_user")]
    [McpRisk(McpRiskLevel.Medium)]
    [Description("Find exactly one user of a source tenant by exact user name or exact e-mail, to grant that user access to " +
                 "the tenant (create_external_tenant_user_mapping with the returned userId and userName). The source tenant " +
                 "must be a registry ancestor or permit this tenant (its allow-list). No listing, prefix or wildcard search. " +
                 "Every negative case answers the same errorCode SourceUserNotFound without a reason; lookups are rate-limited " +
                 "(errorCode SourceUserLookupRateLimited with retryAfterSeconds). The result has no e-mail address. " +
                 "Equivalent to octo-cli LookupSourceTenantUser.")]
    public static async Task<SourceUserLookupResponse> LookupSourceUser(
        McpServer server,
        [Description("Source tenant ID the user belongs to.")] string sourceTenantId,
        [Description("Exact user name or exact e-mail of the user in the source tenant (case-insensitive).")]
        string identifier,
        [Description("Tenant to operate on (the target tenant). Falls back to URL route.")] string? tenantId = null)
    {
        if (string.IsNullOrWhiteSpace(sourceTenantId) || string.IsNullOrWhiteSpace(identifier))
        {
            return new SourceUserLookupResponse
            {
                IsSuccess = false,
                ErrorMessage = "sourceTenantId and identifier are required."
            };
        }

        var ctx = await IdentityClientContext.TryBuildAsync(server, tenantId);
        if (ctx.Error != null)
        {
            return new SourceUserLookupResponse { IsSuccess = false, ErrorMessage = ctx.Error };
        }

        try
        {
            // The identifier (often an e-mail address) is passed to the service only; it is never echoed or logged.
            var user = await ctx.Client!.LookupSourceUser(new SourceUserLookupRequestDto
            {
                SourceTenantId = sourceTenantId,
                Identifier = identifier
            });
            if (user == null)
            {
                return new SourceUserLookupResponse
                {
                    IsSuccess = false,
                    TenantId = ctx.TenantId,
                    ErrorCode = CrossTenantGrantProblemCodes.SourceUserNotFound,
                    ErrorMessage = "No user found that can be granted access."
                };
            }

            return new SourceUserLookupResponse { IsSuccess = true, TenantId = ctx.TenantId, User = user };
        }
        catch (Exception ex)
        {
            var problem = ServiceProblemMessage.From(ex);
            return new SourceUserLookupResponse
            {
                IsSuccess = false,
                TenantId = ctx.TenantId,
                ErrorMessage = problem.Message,
                ErrorCode = problem.Code,
                RetryAfterSeconds = problem.RetryAfterSeconds
            };
        }
    }

    /// <summary>Create a new mapping (grant a user of another tenant access, optionally with groups).</summary>
    [McpServerTool(Name = "create_external_tenant_user_mapping")]
    [McpRisk(McpRiskLevel.High)]
    [Description("Grant a user of another tenant access to this tenant: creates an external tenant user mapping with roles " +
                 "and/or groups of this tenant. The source tenant must be a registry ancestor or permit this tenant; " +
                 "take sourceUserId and sourceUserName from lookup_source_tenant_user. Returns the mapping as stored " +
                 "(user name from the source tenant, group names). Refusals carry errorCode: SourceTenantNotPermitted (403), " +
                 "UnknownGroup (400, nothing stored), ExternalTenantUserMappingAlreadyExists (409). " +
                 "Equivalent to octo-cli CreateExternalTenantUserMapping.")]
    public static async Task<ExternalTenantUserMappingResponse> CreateMapping(
        McpServer server,
        [Description("Source tenant ID.")] string sourceTenantId,
        [Description("Source user ID (userId of lookup_source_tenant_user).")] string sourceUserId,
        [Description("Source user name (userName of lookup_source_tenant_user); required, the service stores the name of the source user record.")]
        string sourceUserName,
        [Description("Optional list of role IDs to assign.")] List<string>? roleIds = null,
        [Description("Optional list of group IDs of this tenant the user becomes a member of (gets exactly their roles).")]
        List<string>? groupIds = null,
        [Description("Tenant to operate on (the target tenant). Falls back to URL route.")] string? tenantId = null)
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

        if (groupIds != null && groupIds.Any(string.IsNullOrWhiteSpace))
        {
            return new ExternalTenantUserMappingResponse
            {
                IsSuccess = false,
                ErrorMessage = "groupIds must not contain empty entries."
            };
        }

        var ctx = await IdentityClientContext.TryBuildAsync(server, tenantId);
        if (ctx.Error != null)
        {
            return new ExternalTenantUserMappingResponse { IsSuccess = false, ErrorMessage = ctx.Error };
        }

        try
        {
            var mapping = await ctx.Client!.GrantExternalTenantUser(new CreateExternalTenantUserMappingDto
            {
                SourceTenantId = sourceTenantId,
                SourceUserId = sourceUserId,
                SourceUserName = sourceUserName,
                RoleIds = roleIds,
                GroupIds = groupIds is { Count: > 0 } ? groupIds : null
            });
            return new ExternalTenantUserMappingResponse
            {
                IsSuccess = true,
                TenantId = ctx.TenantId,
                RtId = mapping.Id?.ToString(),
                Mapping = mapping,
                Message = $"Mapping created for user '{mapping.SourceUserName}' from tenant '{mapping.SourceTenantId}'."
            };
        }
        catch (Exception ex)
        {
            var problem = ServiceProblemMessage.From(ex);
            return new ExternalTenantUserMappingResponse
            {
                IsSuccess = false,
                TenantId = ctx.TenantId,
                ErrorMessage = problem.Message,
                ErrorCode = problem.Code
            };
        }
    }

    /// <summary>Update the role assignments on a mapping.</summary>
    [McpServerTool(Name = "update_external_tenant_user_mapping")]
    [McpRisk(McpRiskLevel.Medium)]
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

        var ctx = await IdentityClientContext.TryBuildAsync(server, tenantId);
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
    [McpRisk(McpRiskLevel.High)]
    [Description("Delete an external tenant user mapping (remove a grant): the user's next token exchange into this tenant is refused; an issued token stays valid until it expires. DESTRUCTIVE — requires confirm=true. Equivalent to octo-cli DeleteExternalTenantUserMapping.")]
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

        var ctx = await IdentityClientContext.TryBuildAsync(server, tenantId);
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
