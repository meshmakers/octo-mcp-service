using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Services.Infrastructure.Services;

namespace Meshmakers.Octo.Backend.McpServices.Services;

/// <summary>
///     Resolves tenant ID from tool parameter or route parameter and provides access to the tenant repository.
/// </summary>
internal class TenantResolutionService(
    IOctoHttpContextAccessor httpContextAccessor,
    ISystemContext systemContext) : ITenantResolutionService
{
    public string ResolveTenantId(string? toolTenantId)
    {
        // Priority 1: Explicit tool parameter
        if (!string.IsNullOrWhiteSpace(toolTenantId))
        {
            return toolTenantId;
        }

        // Priority 2: Route parameter (existing /{tenantId}/mcp endpoint)
        try
        {
            var routeTenantId = httpContextAccessor.GetTenantId();
            if (!string.IsNullOrWhiteSpace(routeTenantId))
            {
                return routeTenantId;
            }
        }
        catch
        {
            // Route parameter not available (tenantless /mcp endpoint)
        }

        throw new InvalidOperationException(
            "No tenant ID specified. Provide a 'tenantId' parameter or use the /{tenantId}/mcp endpoint.");
    }

    public async Task<ITenantRepository> GetTenantRepositoryAsync(string? toolTenantId)
    {
        var tenantId = ResolveTenantId(toolTenantId);
        return await systemContext.FindTenantRepositoryAsync(tenantId);
    }

    public async Task<ITenantContext> GetTenantContextAsync(string? toolTenantId)
    {
        var tenantId = ResolveTenantId(toolTenantId);
        return await systemContext.FindTenantContextAsync(tenantId);
    }
}
