using Meshmakers.Octo.Sdk.ServiceClient.ReportingServices;
using ModelContextProtocol.Server;

namespace Meshmakers.Octo.Backend.McpServices.Services;

/// <summary>
///     Shared bootstrapping for tools that talk to the Reporting service.
/// </summary>
internal sealed record ReportingClientContext(
    IReportingServicesClient? Client,
    string? TenantId,
    string? Error)
{
    public static ReportingClientContext TryBuild(McpServer server, string? tenantIdParam)
    {
        var accessToken = McpSessionContext.TryGetAccessToken(server);
        if (accessToken == null)
        {
            return new ReportingClientContext(null, null, "Not authenticated. Call 'authenticate' first.");
        }

        try
        {
            var tenantResolver = server.Services!.GetRequiredService<ITenantResolutionService>();
            var tenantId = tenantResolver.ResolveTenantId(tenantIdParam);

            var factory = server.Services!.GetRequiredService<IOctoServiceClientFactory>();
            return new ReportingClientContext(factory.CreateReportingClient(tenantId, accessToken), tenantId, null);
        }
        catch (Exception ex)
        {
            return new ReportingClientContext(null, null, ex.Message);
        }
    }
}
