using Meshmakers.Octo.Sdk.ServiceClient.IdentityServices;
using ModelContextProtocol.Server;

namespace Meshmakers.Octo.Backend.McpServices.Services;

/// <summary>
///     Shared bootstrapping for tools that talk to the Identity service: extract session token, resolve the
///     tenant, build an <see cref="IIdentityServicesClient"/>. Tools that share this pattern call
///     <see cref="TryBuild"/> and either propagate <see cref="Error"/> or use <see cref="Client"/>.
/// </summary>
internal sealed record IdentityClientContext(
    IIdentityServicesClient? Client,
    string? TenantId,
    string? Error)
{
    public static IdentityClientContext TryBuild(McpServer server, string? tenantIdParam)
    {
        var accessToken = McpSessionContext.TryGetAccessToken(server);
        if (accessToken == null)
        {
            return new IdentityClientContext(null, null, "Not authenticated. Call 'authenticate' first.");
        }

        try
        {
            var tenantResolver = server.Services!.GetRequiredService<ITenantResolutionService>();
            var tenantId = tenantResolver.ResolveTenantId(tenantIdParam);

            var factory = server.Services!.GetRequiredService<IOctoServiceClientFactory>();
            return new IdentityClientContext(factory.CreateIdentityClient(tenantId, accessToken), tenantId, null);
        }
        catch (Exception ex)
        {
            return new IdentityClientContext(null, null, ex.Message);
        }
    }
}
