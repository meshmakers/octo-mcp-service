using Meshmakers.Octo.Sdk.ServiceClient.CommunicationControllerServices;
using ModelContextProtocol.Server;

namespace Meshmakers.Octo.Backend.McpServices.Services;

/// <summary>
///     Shared bootstrapping for tools that talk to the Communication Controller. Counterpart to
///     <see cref="IdentityClientContext"/> and <see cref="AssetClientContext"/>.
/// </summary>
internal sealed record CommunicationClientContext(
    ICommunicationServicesClient? Client,
    string? TenantId,
    string? Error)
{
    public static CommunicationClientContext TryBuild(McpServer server, string? tenantIdParam)
    {
        var accessToken = McpSessionContext.TryGetAccessToken(server);
        if (accessToken == null)
        {
            return new CommunicationClientContext(null, null, "Not authenticated. Call 'authenticate' first.");
        }

        try
        {
            var tenantResolver = server.Services!.GetRequiredService<ITenantResolutionService>();
            var tenantId = tenantResolver.ResolveTenantId(tenantIdParam);

            var factory = server.Services!.GetRequiredService<IOctoServiceClientFactory>();
            return new CommunicationClientContext(
                factory.CreateCommunicationClient(tenantId, accessToken),
                tenantId,
                null);
        }
        catch (Exception ex)
        {
            return new CommunicationClientContext(null, null, ex.Message);
        }
    }
}
