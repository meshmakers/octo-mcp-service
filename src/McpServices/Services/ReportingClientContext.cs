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
    /// <summary>
    ///     Resolves the access token (including lazy refresh) and builds a Reporting client for
    ///     <paramref name="tenantIdParam"/>.
    /// </summary>
    public static async Task<ReportingClientContext> TryBuildAsync(McpServer server, string? tenantIdParam)
    {
        try
        {
            var tenantResolver = server.Services!.GetRequiredService<ITenantResolutionService>();
            var tenantId = tenantResolver.ResolveTenantId(tenantIdParam);

            // Tenant-aware token (AB#4338): home tenant → session token; other tenant → exchanged B token.
            var accessToken = await McpSessionContext.TryGetAccessTokenAsync(server, tenantId);
            if (accessToken == null)
            {
                return new ReportingClientContext(null, null, Constants.NotAuthenticatedError);
            }

            var factory = server.Services!.GetRequiredService<IOctoServiceClientFactory>();
            return new ReportingClientContext(factory.CreateReportingClient(tenantId, accessToken), tenantId, null);
        }
        catch (Exception ex)
        {
            return new ReportingClientContext(null, null, ex.Message);
        }
    }
}
