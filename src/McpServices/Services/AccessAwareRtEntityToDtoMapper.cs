using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Contracts.Secrets;

namespace Meshmakers.Octo.Backend.McpServices.Services;

/// <summary>
///     AB#8043: the <see cref="IRtEntityToDtoMapper" /> of the MCP server. It maps with the SDK
///     <see cref="RtEntityToDtoMapper" /> (Secret attributes become their read state) and then removes every attribute whose
///     CK assignment is <c>access: Hidden</c> (<see cref="AttributeAccess.IsReadableOutsideMethod" />), on the entity and
///     inside records and record arrays. Hidden values (password hashes, security stamps, authenticator tokens) therefore
///     never reach an MCP client, whichever tool maps the entity (CRUD, query, association, persisted query, knowledge
///     resources) - the same answer the asset-repo GraphQL gives, where Hidden attributes are not part of the schema.
/// </summary>
/// <remarks>
///     Registered in <c>Program.cs</c> as the only <see cref="IRtEntityToDtoMapper" />; never register the SDK mapper
///     directly. The SDK mapper itself does not know <c>access</c>.
/// </remarks>
public sealed class AccessAwareRtEntityToDtoMapper : IRtEntityToDtoMapper
{
    private readonly ICkCacheService _ckCacheService;
    private readonly RtEntityToDtoMapper _inner;

    /// <summary>Creates the mapper.</summary>
    /// <param name="ckCacheService">Construction Kit cache</param>
    /// <param name="secretAttributeProtector">Optional key ring (passed to the SDK mapper, never decrypts)</param>
    public AccessAwareRtEntityToDtoMapper(ICkCacheService ckCacheService,
        ISecretAttributeProtector? secretAttributeProtector = null)
    {
        _ckCacheService = ckCacheService;
        _inner = new RtEntityToDtoMapper(ckCacheService, secretAttributeProtector);
    }

    /// <inheritdoc />
    public RtEntityDto ConvertToDto(string tenantId, RtEntity rtEntity,
        AttributeValueResolveFlags attributeValueResolveFlags = AttributeValueResolveFlags.Default)
    {
        var dto = _inner.ConvertToDto(tenantId, rtEntity, attributeValueResolveFlags);
        var typeGraph = _ckCacheService.GetRtCkType(tenantId, dto.CkTypeId);
        StripHidden(tenantId, typeGraph, dto);
        return dto;
    }

    private void StripHidden(string tenantId, CkTypeWithAttributesGraph graph, RtTypeWithAttributesDto dto)
    {
        if (dto.Attributes == null)
        {
            return;
        }

        var kept = new List<RtEntityAttributeDto>(dto.Attributes.Count());
        foreach (var attribute in dto.Attributes)
        {
            var ckAttribute = Find(graph, attribute.AttributeName);
            if (ckAttribute != null && !AttributeAccess.IsReadableOutsideMethod(ckAttribute.Access))
            {
                continue;
            }

            attribute.Value = StripHiddenInValue(tenantId, attribute.Value);
            kept.Add(attribute);
        }

        dto.Attributes = kept;
    }

    // The SDK mapper hands record arrays out as a lazy projection that builds new record DTOs on every enumeration, so
    // the list is materialised once and the stripped elements are what the caller sees.
    private object? StripHiddenInValue(string tenantId, object? value)
    {
        switch (value)
        {
            case RtRecordDto record:
                StripHidden(tenantId, _ckCacheService.GetRtCkRecord(tenantId, record.CkRecordId), record);
                return record;
            case IEnumerable<object> items:
                var list = items.ToList();
                foreach (var item in list)
                {
                    if (item is RtRecordDto itemRecord)
                    {
                        StripHidden(tenantId, _ckCacheService.GetRtCkRecord(tenantId, itemRecord.CkRecordId),
                            itemRecord);
                    }
                }

                return list;
            default:
                return value;
        }
    }

    private static CkTypeAttributeGraph? Find(CkTypeWithAttributesGraph graph, string attributeName) =>
        graph.AllAttributesByName.TryGetValue(attributeName, out var exact)
            ? exact
            : graph.AllAttributesByName
                .FirstOrDefault(kv => string.Equals(kv.Key, attributeName, StringComparison.OrdinalIgnoreCase)).Value;
}
