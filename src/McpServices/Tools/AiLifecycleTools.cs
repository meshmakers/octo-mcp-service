using System.ComponentModel;
using Meshmakers.Octo.Backend.McpServices.Models;
using Meshmakers.Octo.Backend.McpServices.Services;
using ModelContextProtocol.Server;

// ReSharper disable UnusedMember.Global

namespace Meshmakers.Octo.Backend.McpServices.Tools;

/// <summary>
///     AI Services enable / disable for a tenant (AB#7192). Mirrors octo-cli EnableAi / DisableAi, the one of the four
///     tenant capabilities that had no MCP tool: a tenant with AI Services enabled could not be prepared for
///     <c>delete_tenant</c> from MCP.
/// </summary>
[McpServerToolType]
public sealed class AiLifecycleTools
{
    /// <summary>Enable AI Services for the tenant.</summary>
    [McpServerTool(Name = "enable_ai")]
    [McpRisk(McpRiskLevel.High)]
    [Description(
        "Enable AI Services (the AI adapter) for the resolved tenant. The Communication controller must already be " +
        "enabled for the tenant. Idempotent. Equivalent to octo-cli EnableAi. Requires the tenant role of the " +
        "tenant's own administrator.")]
    public static async Task<AiLifecycleResponse> EnableAi(
        McpServer server,
        [Description("Tenant to enable AI Services for. Falls back to URL route.")] string? tenantId = null)
    {
        var ctx = await AiClientContext.TryBuildAsync(server, tenantId);
        if (ctx.Error != null)
        {
            return new AiLifecycleResponse { IsSuccess = false, ErrorMessage = ctx.Error };
        }

        try
        {
            await ctx.Client!.EnableAsync(ctx.TenantId!);
            return new AiLifecycleResponse
            {
                IsSuccess = true,
                TenantId = ctx.TenantId,
                Message = $"AI Services enabled for tenant '{ctx.TenantId}'."
            };
        }
        catch (Exception ex)
        {
            return new AiLifecycleResponse { IsSuccess = false, ErrorMessage = ex.Message };
        }
    }

    /// <summary>Disable AI Services for the tenant. Reversible flag flip; requires confirm.</summary>
    [McpServerTool(Name = "disable_ai")]
    [McpRisk(McpRiskLevel.High)]
    [Description(
        "Disable AI Services for the resolved tenant. New AI sessions are refused until re-enabled with enable_ai; the " +
        "seeded AgentConfig / QuotaLimit and all session, lease and audit data stay in the tenant, running sessions are " +
        "not interrupted. Required before delete_tenant / detach_tenant (AB#4255) and before disable_communication " +
        "(AI depends on Communication); no precondition of its own. Reversible with enable_ai. Requires confirm=true. " +
        "Equivalent to octo-cli DisableAi.")]
    public static async Task<AiLifecycleResponse> DisableAi(
        McpServer server,
        [Description("Must be true to actually disable.")] bool confirm = false,
        [Description("Tenant to disable AI Services for. Falls back to URL route.")] string? tenantId = null)
    {
        if (!confirm)
        {
            return new AiLifecycleResponse
            {
                IsSuccess = false,
                ErrorMessage = "Refusing to disable AI Services without confirm=true."
            };
        }

        var ctx = await AiClientContext.TryBuildAsync(server, tenantId);
        if (ctx.Error != null)
        {
            return new AiLifecycleResponse { IsSuccess = false, ErrorMessage = ctx.Error };
        }

        try
        {
            await ctx.Client!.DisableAsync(ctx.TenantId!);
            return new AiLifecycleResponse
            {
                IsSuccess = true,
                TenantId = ctx.TenantId,
                Message = $"AI Services disabled for tenant '{ctx.TenantId}'."
            };
        }
        catch (Exception ex)
        {
            return new AiLifecycleResponse { IsSuccess = false, ErrorMessage = ex.Message };
        }
    }
}
