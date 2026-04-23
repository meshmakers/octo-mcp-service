using System.ComponentModel;
using Meshmakers.Octo.Backend.McpServices.Services;
using Meshmakers.Octo.Services.Infrastructure.Services;
using ModelContextProtocol.Server;

namespace Meshmakers.Octo.Backend.McpServices.Tools;

/// <summary>
///     Tests
/// </summary>
[McpServerToolType]
public sealed class EchoTool
{
    /// <summary>
    /// </summary>
    /// <param name="thisServer"></param>
    /// <param name="message"></param>
    /// <param name="tenantId">Optional tenant ID. If not specified, the tenant is resolved from the URL route.</param>
    /// <returns></returns>
    [McpServerTool(Name = "Echo")]
    [Description("Echoes the input back to the client.")]
    public static async Task<string> Echo(
        McpServer thisServer,
        string message,
        string? tenantId = null)
    {
        var tenantResolution = thisServer.Services!.GetRequiredService<ITenantResolutionService>();
        var tenantRepository = await tenantResolution.GetTenantRepositoryAsync(tenantId);

        return "hello " + message + ", from tenant " + tenantRepository.TenantId;
    }
}
