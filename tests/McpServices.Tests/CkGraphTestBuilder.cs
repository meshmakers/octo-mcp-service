using System.Collections.ObjectModel;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Moq;

namespace McpServices.Tests;

/// <summary>
///     Builds small in-memory CK type / record graphs for tests that need real attribute value types
///     (e.g. the Secret-attribute handling of AB#5543).
/// </summary>
internal static class CkGraphTestBuilder
{
    public static CkTypeGraph BuildType(string typeId,
        params (string Name, AttributeValueTypesDto ValueType, CkId<CkRecordId>? RecordId)[] attrs)
    {
        var allAttrs = BuildAttributes(attrs);
        var defined = allAttrs.Values
            .Select(g => new CkTypeAttributeDto { CkAttributeId = g.CkAttributeId, AttributeName = g.AttributeName })
            .ToList();

        return new CkTypeGraph(
            ckTypeId: new CkId<CkTypeId>(typeId),
            isAbstract: false,
            isFinal: false,
            isCollectionRoot: true,
            baseTypes: new ReadOnlyCollection<CkGraphTypeInheritance>([]),
            derivedFromCkTypeId: null,
            definingCollectionRootCkTypeId: null,
            derivedTypes: new ReadOnlyCollection<CkGraphTypeInheritance>([]),
            definedAttributes: defined,
            allAttributes: allAttrs,
            indexes: new ReadOnlyCollection<CkTypeIndexDto>([]),
            associations: new CkGraphDirectedAssociations(new List<CkTypeAssociationDto>()),
            description: string.Empty,
            enableChangeStreamPreAndPostImages: false);
    }

    public static CkRecordGraph BuildRecord(CkId<CkRecordId> recordId,
        params (string Name, AttributeValueTypesDto ValueType, CkId<CkRecordId>? RecordId)[] attrs)
    {
        var allAttrs = BuildAttributes(attrs);
        var defined = allAttrs.Values
            .Select(g => new CkTypeAttributeDto { CkAttributeId = g.CkAttributeId, AttributeName = g.AttributeName })
            .ToList();

        return new CkRecordGraph(
            ckRecordId: recordId,
            isAbstract: false,
            isFinal: false,
            baseRecords: new ReadOnlyCollection<CkGraphRecordInheritance>([]),
            derivedFromCkRecordId: null,
            derivedRecords: new ReadOnlyCollection<CkGraphRecordInheritance>([]),
            definedAttributes: defined,
            allAttributes: allAttrs,
            description: string.Empty);
    }

    public static void SetupTryGetCkRecord(Mock<ICkCacheService> ckCache, CkId<CkRecordId> recordId,
        CkRecordGraph? recordGraph)
    {
        ckCache
            .Setup(c => c.TryGetCkRecord(It.IsAny<string>(), recordId, out It.Ref<CkRecordGraph?>.IsAny))
            .Returns(new TryGetCkRecordCallback((string _, CkId<CkRecordId> _, out CkRecordGraph? rg) =>
            {
                rg = recordGraph;
                return recordGraph != null;
            }));
    }

    public static void SetupTryGetRtCkType(Mock<ICkCacheService> ckCache, CkTypeGraph typeGraph)
    {
        ckCache
            .Setup(c => c.TryGetRtCkType(It.IsAny<string>(), It.IsAny<RtCkId<CkTypeId>>(),
                out It.Ref<CkTypeGraph?>.IsAny))
            .Returns(new TryGetRtCkTypeCallback((string _, RtCkId<CkTypeId> _, out CkTypeGraph? tg) =>
            {
                tg = typeGraph;
                return true;
            }));
    }

    private delegate bool TryGetRtCkTypeCallback(string tenantId, RtCkId<CkTypeId> ckTypeId,
        out CkTypeGraph? typeGraph);

    private delegate bool TryGetCkRecordCallback(string tenantId, CkId<CkRecordId> recordId,
        out CkRecordGraph? recordGraph);

    private static Dictionary<CkId<CkAttributeId>, CkTypeAttributeGraph> BuildAttributes(
        (string Name, AttributeValueTypesDto ValueType, CkId<CkRecordId>? RecordId)[] attrs)
    {
        var allAttrs = new Dictionary<CkId<CkAttributeId>, CkTypeAttributeGraph>();
        foreach (var (name, valueType, recordId) in attrs)
        {
            var attrId = new CkId<CkAttributeId>($"TestModule-1/{name}-1");
            var ckAttrDto = new CkAttributeDto
            {
                AttributeId = $"{name}-1",
                ValueType = valueType,
                ValueCkRecordId = recordId
            };
            var ckAttrGraph = new CkAttributeGraph(attrId, ckAttrDto);
            var ckTypeAttrDto = new CkTypeAttributeDto { CkAttributeId = attrId, AttributeName = name };
            allAttrs[attrId] = new CkTypeAttributeGraph(attrId, ckTypeAttrDto, ckAttrGraph);
        }

        return allAttrs;
    }
}
