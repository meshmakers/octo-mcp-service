using System.ComponentModel;
using Meshmakers.Octo.Services.Infrastructure;
using ModelContextProtocol.Server;

namespace Meshmakers.Octo.Backend.McpServices.Tools;

/// <summary>
/// Tests
/// </summary>
[McpServerToolType]
public sealed class EchoTool
{
    /// <summary>
    ///
    /// </summary>
    /// <param name="thisServer"></param>
    /// <param name="message"></param>
    /// <returns></returns>
    [McpServerTool(Name = "Echo"), Description("Echoes the input back to the client.")]
    public static async Task<string> Echo(
        IMcpServer thisServer,
        string message)
    {
        var httpContextAccessor = thisServer.Services!.GetRequiredService<IHttpContextAccessor>();
        var tenantRepository = await httpContextAccessor.GetTenantRepositoryAsync();

        return "hello " + message + ", from tenant " + tenantRepository.TenantId;
    }
}