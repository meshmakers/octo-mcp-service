using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;

namespace Meshmakers.Octo.Backend.McpServices.Services;

/// <summary>
///     Resolves the tenant ID from multiple sources (tool parameter, route parameter)
///     and validates access against the user's allowed tenants.
/// </summary>
public interface ITenantResolutionService
{
    /// <summary>
    ///     Resolves the tenant ID using the following priority:
    ///     1. Explicit tool parameter (tenantId)
    ///     2. Route parameter ({tenantId})
    ///     Throws if no tenant can be resolved or the user is not authorized for the tenant.
    /// </summary>
    /// <param name="toolTenantId">Optional tenant ID passed as tool parameter.</param>
    /// <returns>The resolved tenant ID.</returns>
    string ResolveTenantId(string? toolTenantId);

    /// <summary>
    ///     Resolves the tenant and returns the corresponding tenant repository.
    /// </summary>
    /// <param name="toolTenantId">Optional tenant ID passed as tool parameter.</param>
    /// <returns>The tenant repository for the resolved tenant.</returns>
    Task<ITenantRepository> GetTenantRepositoryAsync(string? toolTenantId);
}
