using Meshmakers.Octo.Backend.McpServices.ContentGuard;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.Secrets;

namespace Meshmakers.Octo.Backend.McpServices.Services.ContentGuard;

/// <summary>
///     Reads the fingerprints of the Secret attribute values a caller could reveal in a tenant, for detector
///     <see cref="ContentGuardDetector.TenantSecret" /> of the content guard (AB#8475).
/// </summary>
/// <remarks>
///     <list type="bullet">
///         <item>Reads with the <b>caller's session</b> (data permissions apply), never a system session: the guard
///         compares against what the caller could reveal.</item>
///         <item>Only fingerprints leave this class (<see cref="SecretFingerprint.Compute" />); plaintexts live only for
///         the duration of the hash. Nothing is cached.</item>
///         <item>Scope: top-level <c>Secret</c> attributes of every non-abstract CK type of the tenant. Secret members
///         inside records are not read yet.</item>
///         <item>Without a configured key ring (<see cref="ISecretAttributeProtector.IsConfigured" /> false, or no
///         protector) nothing can be revealed and the set is empty: the detector is off, the others still run.</item>
///     </list>
/// </remarks>
public static class TenantSecretFingerprints
{
    /// <summary>Upper bound of entities read per CK type.</summary>
    public const int MaxEntitiesPerType = 2000;

    /// <summary>Name of the service in the <see cref="SecretAccessContext" /> of the reveal (decrypt metric).</summary>
    public const string ServiceName = "mcp-content-guard";

    /// <summary>Reads the fingerprints.</summary>
    /// <param name="tenantRepository">Repository of the tenant.</param>
    /// <param name="callerSession">A session opened with the caller's security context.</param>
    /// <param name="ckCacheService">The CK cache (loaded for the tenant).</param>
    /// <param name="protector">The key ring; <c>null</c> when the host has none.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static async ValueTask<IReadOnlySet<string>> ReadAsync(ITenantRepository tenantRepository,
        IOctoSession callerSession, ICkCacheService ckCacheService, ISecretAttributeProtector? protector,
        CancellationToken cancellationToken)
    {
        var fingerprints = new HashSet<string>(StringComparer.Ordinal);
        if (protector is not { IsConfigured: true })
        {
            return fingerprints;
        }

        var tenantId = tenantRepository.TenantId;
        foreach (var type in ckCacheService.GetCkTypes(tenantId))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (type.IsAbstract)
            {
                continue;
            }

            var secretAttributes = type.AllAttributes.Values
                .Where(a => a.ValueType == AttributeValueTypesDto.Secret)
                .Select(a => a.AttributeName)
                .ToList();
            if (secretAttributes.Count == 0)
            {
                continue;
            }

            var rtCkTypeId = type.CkTypeId.ToRtCkId();
            var entities = await tenantRepository.GetRtEntitiesByTypeAsync(callerSession, rtCkTypeId,
                RtEntityQueryOptions.Create(), 0, MaxEntitiesPerType);
            foreach (var entity in entities.Items)
            {
                foreach (var attributeName in secretAttributes)
                {
                    var plaintext = protector.RevealOrNull(entity.GetAttributeSecretValueOrDefault(attributeName),
                        new SecretAccessContext(tenantId, rtCkTypeId.ToString(), attributeName, ServiceName));
                    if (!string.IsNullOrEmpty(plaintext) && plaintext.Length >= ContentDetectors.MinSecretLength)
                    {
                        fingerprints.Add(SecretFingerprint.Compute(plaintext));
                    }
                }
            }
        }

        return fingerprints;
    }
}
