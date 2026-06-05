using System.ComponentModel;
using Meshmakers.Octo.Backend.McpServices.Models;
using Meshmakers.Octo.Backend.McpServices.Services;
using ModelContextProtocol.Server;

// ReSharper disable UnusedMember.Global

namespace Meshmakers.Octo.Backend.McpServices.Tools;

/// <summary>Reporting service enable / disable. Mirrors octo-cli EnableReporting / DisableReporting.</summary>
[McpServerToolType]
public sealed class ReportingTools
{
    /// <summary>Enable reporting for the tenant.</summary>
    [McpServerTool(Name = "enable_reporting")]
    [Description("Enable the reporting service for the resolved tenant. Equivalent to octo-cli EnableReporting.")]
    public static async Task<TimeSeriesResponse> EnableReporting(
        McpServer server,
        [Description("Tenant to operate on. Falls back to URL route.")] string? tenantId = null)
    {
        var ctx = ReportingClientContext.TryBuild(server, tenantId);
        if (ctx.Error != null)
        {
            return new TimeSeriesResponse { IsSuccess = false, ErrorMessage = ctx.Error };
        }

        try
        {
            await ctx.Client!.EnableAsync(ctx.TenantId!);
            return new TimeSeriesResponse
            {
                IsSuccess = true,
                TenantId = ctx.TenantId,
                Message = $"Reporting enabled for tenant '{ctx.TenantId}'."
            };
        }
        catch (Exception ex)
        {
            return new TimeSeriesResponse { IsSuccess = false, ErrorMessage = ex.Message };
        }
    }

    /// <summary>Disable reporting for the tenant. Destructive: requires confirm.</summary>
    [McpServerTool(Name = "disable_reporting")]
    [Description(
        "Disable the reporting service for the resolved tenant. DESTRUCTIVE — reports stop being generated " +
        "until re-enabled. Requires confirm=true. Equivalent to octo-cli DisableReporting.")]
    public static async Task<TimeSeriesResponse> DisableReporting(
        McpServer server,
        [Description("Must be true to actually disable.")] bool confirm = false,
        [Description("Tenant to operate on. Falls back to URL route.")] string? tenantId = null)
    {
        if (!confirm)
        {
            return new TimeSeriesResponse
            {
                IsSuccess = false,
                ErrorMessage = "Refusing to disable reporting without confirm=true."
            };
        }

        var ctx = ReportingClientContext.TryBuild(server, tenantId);
        if (ctx.Error != null)
        {
            return new TimeSeriesResponse { IsSuccess = false, ErrorMessage = ctx.Error };
        }

        try
        {
            await ctx.Client!.DisableAsync(ctx.TenantId!);
            return new TimeSeriesResponse
            {
                IsSuccess = true,
                TenantId = ctx.TenantId,
                Message = $"Reporting disabled for tenant '{ctx.TenantId}'."
            };
        }
        catch (Exception ex)
        {
            return new TimeSeriesResponse { IsSuccess = false, ErrorMessage = ex.Message };
        }
    }
}
