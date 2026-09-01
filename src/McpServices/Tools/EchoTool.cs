using System.ComponentModel;
using Meshmakers.Octo.Backend.McpServices.Services;
using Meshmakers.Octo.Services.Infrastructure.Services;
using ModelContextProtocol.Server;

namespace Meshmakers.Octo.Backend.McpServices.Tools;

/// <summary>
///     Connectivity smoke test. Echoes a message back together with the tenant the call resolved to.
/// </summary>
[McpServerToolType]
public sealed class EchoTool
{
    /// <summary>
    ///     Echoes <paramref name="message" /> back, naming the resolved tenant.
    /// </summary>
    /// <param name="thisServer">MCP server instance.</param>
    /// <param name="message">The message to echo back.</param>
    /// <param name="tenantId">Optional tenant ID. If not specified, the tenant is resolved from the URL route.</param>
    /// <returns>The echoed message, or an error string when the caller may not act on the tenant.</returns>
    [McpServerTool(Name = "Echo")]
    [Description("Echoes the input back to the client.")]
    public static async Task<string> Echo(
        McpServer thisServer,
        string message,
        string? tenantId = null)
    {
        var tenantResolution = thisServer.Services!.GetRequiredService<ITenantResolutionService>();

        // Tenant gate (AB#5030). Ungated, this tool was a tenant-existence oracle: any authenticated
        // caller could probe arbitrary tenant ids and tell "exists" from "does not exist" by the shape
        // of the answer.
        var access = await RuntimeSecurityContextResolver.ResolveTenantAccessAsync(
            thisServer, tenantResolution, tenantId);
        if (access.Error != null)
        {
            return $"Echo failed: {access.Error}";
        }

        try
        {
            var tenantRepository = await tenantResolution.GetTenantRepositoryAsync(tenantId);
            return "hello " + message + ", from tenant " + tenantRepository.TenantId;
        }
        catch (Exception ex)
        {
            // "Never throw out of a tool" — an unknown/unreachable tenant comes back as a message.
            return $"Echo failed: {ex.Message}";
        }
    }
}
