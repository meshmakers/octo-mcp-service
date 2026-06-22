using System.ComponentModel;
using Meshmakers.Octo.Backend.McpServices.Models;
using Meshmakers.Octo.Backend.McpServices.Services;
using ModelContextProtocol.Server;

// ReSharper disable UnusedMember.Global

namespace Meshmakers.Octo.Backend.McpServices.Tools;

/// <summary>
///     Enable / disable the communication controller for a tenant. Mirrors octo-cli EnableCommunication /
///     DisableCommunication.
/// </summary>
[McpServerToolType]
public sealed class CommunicationLifecycleTools
{
    /// <summary>Enable the communication controller for the tenant.</summary>
    [McpServerTool(Name = "enable_communication")]
    [McpRisk(McpRiskLevel.High)]
    [Description(
        "Enable the communication controller for the resolved tenant. Equivalent to octo-cli " +
        "EnableCommunication.")]
    public static async Task<CommunicationLifecycleResponse> EnableCommunication(
        McpServer server,
        [Description("Tenant to enable communication for. Falls back to URL route.")] string? tenantId = null)
    {
        var ctx = await CommunicationClientContext.TryBuildAsync(server, tenantId);
        if (ctx.Error != null)
        {
            return new CommunicationLifecycleResponse { IsSuccess = false, ErrorMessage = ctx.Error };
        }

        try
        {
            await ctx.Client!.EnableAsync(ctx.TenantId!);
            return new CommunicationLifecycleResponse
            {
                IsSuccess = true,
                TenantId = ctx.TenantId,
                TargetTenantId = ctx.TenantId,
                Message = $"Communication controller enabled for tenant '{ctx.TenantId}'."
            };
        }
        catch (Exception ex)
        {
            return new CommunicationLifecycleResponse { IsSuccess = false, ErrorMessage = ex.Message };
        }
    }

    /// <summary>Disable the communication controller for the tenant. Destructive: requires confirm.</summary>
    [McpServerTool(Name = "disable_communication")]
    [McpRisk(McpRiskLevel.High)]
    [Description(
        "Disable the communication controller for the resolved tenant. DESTRUCTIVE — stops all pipeline / data " +
        "flow execution for the tenant until re-enabled. Requires confirm=true. Equivalent to octo-cli " +
        "DisableCommunication.")]
    public static async Task<CommunicationLifecycleResponse> DisableCommunication(
        McpServer server,
        [Description("Must be true to actually disable.")] bool confirm = false,
        [Description("Tenant to disable communication for. Falls back to URL route.")] string? tenantId = null)
    {
        if (!confirm)
        {
            return new CommunicationLifecycleResponse
            {
                IsSuccess = false,
                ErrorMessage = "Refusing to disable communication controller without confirm=true."
            };
        }

        var ctx = await CommunicationClientContext.TryBuildAsync(server, tenantId);
        if (ctx.Error != null)
        {
            return new CommunicationLifecycleResponse { IsSuccess = false, ErrorMessage = ctx.Error };
        }

        try
        {
            await ctx.Client!.DisableAsync(ctx.TenantId!);
            return new CommunicationLifecycleResponse
            {
                IsSuccess = true,
                TenantId = ctx.TenantId,
                TargetTenantId = ctx.TenantId,
                Message = $"Communication controller disabled for tenant '{ctx.TenantId}'."
            };
        }
        catch (Exception ex)
        {
            return new CommunicationLifecycleResponse { IsSuccess = false, ErrorMessage = ex.Message };
        }
    }
}
