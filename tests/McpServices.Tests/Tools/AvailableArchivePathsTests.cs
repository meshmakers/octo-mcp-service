using System.Collections.ObjectModel;
using FluentAssertions;
using Meshmakers.Octo.Backend.McpServices.Services;
using Meshmakers.Octo.Backend.McpServices.Tools;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Moq;
using Xunit;

namespace McpServices.Tests.Tools;

/// <summary>
///     Unit tests for <see cref="AvailableArchivePathsResolver"/> and the <c>get_available_archive_paths</c>
///     tool (ROADMAP #4). The resolver walks the CK type/record graph; tests pin down the recursion cap,
///     the array-flag propagation, the record cycle guard, and the missing-record fallback.
/// </summary>
public class AvailableArchivePathsTests : TestBase
{
    private const string TenantId = "test-tenant";
    private static readonly RtCkId<CkTypeId> SensorCkType = new("EnergyCommunity-1/Sensor-1");

    // ── Resolver: focused walks ─────────────────────────────────────────────

    [Fact]
    public void Resolve_PrimitiveAttributes_EmittedAsLeafRows()
    {
        var typeGraph = BuildType("EnergyCommunity-1/Sensor-1",
            ("Name", AttributeValueTypesDto.String, null),
            ("Power", AttributeValueTypesDto.Double, null));

        var ckCache = new Mock<ICkCacheService>();
        ckCache.Setup(c => c.GetRtCkType(TenantId, SensorCkType)).Returns(typeGraph);

        var result = AvailableArchivePathsResolver.Resolve(
            ckCache.Object, TenantId, SensorCkType, maxDepth: 5);

        result.Should().HaveCount(2);
        result.Should().Contain(p =>
            p.Path == "Name" && p.PrimitiveType == "String" && !p.IsRecord && !p.IsArray);
        result.Should().Contain(p =>
            p.Path == "Power" && p.PrimitiveType == "Double" && !p.IsRecord && !p.IsArray);
    }

    [Fact]
    public void Resolve_RecordAttribute_EmitsRecordRowPlusChildren()
    {
        var addressRecordId = new CkId<CkRecordId>("EnergyCommunity-1/Address-1");

        var typeGraph = BuildType("EnergyCommunity-1/Sensor-1",
            ("Location", AttributeValueTypesDto.Record, addressRecordId));

        var addressRecord = BuildRecord(addressRecordId,
            ("Street", AttributeValueTypesDto.String, null),
            ("Zip", AttributeValueTypesDto.String, null));

        var ckCache = new Mock<ICkCacheService>();
        ckCache.Setup(c => c.GetRtCkType(TenantId, SensorCkType)).Returns(typeGraph);
        SetupTryGetCkRecord(ckCache, addressRecordId, addressRecord);

        var result = AvailableArchivePathsResolver.Resolve(
            ckCache.Object, TenantId, SensorCkType, maxDepth: 5);

        result.Should().HaveCount(3);
        result.Should().Contain(p =>
            p.Path == "Location" && p.IsRecord && p.RecordTypeId!.Contains("Address-1"));
        result.Should().Contain(p => p.Path == "Location.Street" && p.PrimitiveType == "String");
        result.Should().Contain(p => p.Path == "Location.Zip" && p.PrimitiveType == "String");
    }

    [Fact]
    public void Resolve_RecordArray_FlagsChildrenAsArrayElements()
    {
        var contactRecordId = new CkId<CkRecordId>("EnergyCommunity-1/Contact-1");

        var typeGraph = BuildType("EnergyCommunity-1/Sensor-1",
            ("Contacts", AttributeValueTypesDto.RecordArray, contactRecordId));

        var contactRecord = BuildRecord(contactRecordId,
            ("Email", AttributeValueTypesDto.String, null));

        var ckCache = new Mock<ICkCacheService>();
        ckCache.Setup(c => c.GetRtCkType(TenantId, SensorCkType)).Returns(typeGraph);
        SetupTryGetCkRecord(ckCache, contactRecordId, contactRecord);

        var result = AvailableArchivePathsResolver.Resolve(
            ckCache.Object, TenantId, SensorCkType, maxDepth: 5);

        result.Should().Contain(p => p.Path == "Contacts" && p.IsRecord && p.IsArray);
        result.Should().Contain(p =>
            p.Path == "Contacts.Email" && !p.IsRecord && p.IsArray);
    }

    [Fact]
    public void Resolve_DepthCap_TruncatesNestedRecords()
    {
        // Level 0 (root) Sensor → Level 1 record A → Level 2 record B.
        // With maxDepth=1, we only emit "Outer" (record row at depth 1) and skip B's children.
        var innerRecordId = new CkId<CkRecordId>("EnergyCommunity-1/Inner-1");
        var outerRecordId = new CkId<CkRecordId>("EnergyCommunity-1/Outer-1");

        var typeGraph = BuildType("EnergyCommunity-1/Sensor-1",
            ("Outer", AttributeValueTypesDto.Record, outerRecordId));

        var outerRecord = BuildRecord(outerRecordId,
            ("Inner", AttributeValueTypesDto.Record, innerRecordId));

        var innerRecord = BuildRecord(innerRecordId,
            ("Leaf", AttributeValueTypesDto.String, null));

        var ckCache = new Mock<ICkCacheService>();
        ckCache.Setup(c => c.GetRtCkType(TenantId, SensorCkType)).Returns(typeGraph);
        SetupTryGetCkRecord(ckCache, outerRecordId, outerRecord);
        SetupTryGetCkRecord(ckCache, innerRecordId, innerRecord);

        var result = AvailableArchivePathsResolver.Resolve(
            ckCache.Object, TenantId, SensorCkType, maxDepth: 1);

        // Only the "Outer" record row is emitted; depth cap stops recursion at exactly depth 1.
        result.Should().HaveCount(1);
        result[0].Path.Should().Be("Outer");
        result[0].IsRecord.Should().BeTrue();
    }

    [Fact]
    public void Resolve_CycleGuard_DoesNotInfiniteLoopOnSelfReferentialRecord()
    {
        // A "tree node" record whose Child slot points back at itself. The visited-record set
        // prevents the walker from descending forever.
        var nodeRecordId = new CkId<CkRecordId>("EnergyCommunity-1/TreeNode-1");

        var typeGraph = BuildType("EnergyCommunity-1/Sensor-1",
            ("Root", AttributeValueTypesDto.Record, nodeRecordId));

        var nodeRecord = BuildRecord(nodeRecordId,
            ("Name", AttributeValueTypesDto.String, null),
            ("Child", AttributeValueTypesDto.Record, nodeRecordId));

        var ckCache = new Mock<ICkCacheService>();
        ckCache.Setup(c => c.GetRtCkType(TenantId, SensorCkType)).Returns(typeGraph);
        SetupTryGetCkRecord(ckCache, nodeRecordId, nodeRecord);

        var result = AvailableArchivePathsResolver.Resolve(
            ckCache.Object, TenantId, SensorCkType, maxDepth: 5);

        // Root record row + its Name leaf + the Child record row (re-entrance blocked).
        result.Select(p => p.Path).Should().BeEquivalentTo(
            ["Root", "Root.Name", "Root.Child"]);
    }

    [Fact]
    public void Resolve_MissingRecordInCache_OmitsChildren()
    {
        // ValueCkRecordId references a record that isn't in the cache (e.g. cache stale, model
        // partially loaded). The record row itself is emitted but children are skipped.
        var unknownRecordId = new CkId<CkRecordId>("EnergyCommunity-1/Unknown-1");

        var typeGraph = BuildType("EnergyCommunity-1/Sensor-1",
            ("MysteryBox", AttributeValueTypesDto.Record, unknownRecordId));

        var ckCache = new Mock<ICkCacheService>();
        ckCache.Setup(c => c.GetRtCkType(TenantId, SensorCkType)).Returns(typeGraph);
        SetupTryGetCkRecord(ckCache, unknownRecordId, null);

        var result = AvailableArchivePathsResolver.Resolve(
            ckCache.Object, TenantId, SensorCkType, maxDepth: 5);

        result.Should().HaveCount(1);
        result[0].Path.Should().Be("MysteryBox");
        result[0].IsRecord.Should().BeTrue();
    }

    [Fact]
    public void Resolve_MaxDepthZero_ClampsToOne()
    {
        var typeGraph = BuildType("EnergyCommunity-1/Sensor-1",
            ("Name", AttributeValueTypesDto.String, null));

        var ckCache = new Mock<ICkCacheService>();
        ckCache.Setup(c => c.GetRtCkType(TenantId, SensorCkType)).Returns(typeGraph);

        var result = AvailableArchivePathsResolver.Resolve(
            ckCache.Object, TenantId, SensorCkType, maxDepth: 0);

        // maxDepth=0 → clamped to 1 → root attributes still emitted.
        result.Should().HaveCount(1);
        result[0].Path.Should().Be("Name");
    }

    // ── Tool: end-to-end with the SchemaDiscoveryTools entry point ───────────

    [Fact]
    public async Task GetAvailableArchivePaths_HappyPath_ReturnsRowsWithMetadata()
    {
        var typeGraph = BuildType("EnergyCommunity-1/Sensor-1",
            ("Name", AttributeValueTypesDto.String, null),
            ("Power", AttributeValueTypesDto.Double, null));

        MockCkCacheService
            .Setup(c => c.GetRtCkType(It.IsAny<string>(), It.IsAny<RtCkId<CkTypeId>>()))
            .Returns(typeGraph);

        var result = await SchemaDiscoveryTools.GetAvailableArchivePaths(
            MockServer.Object, "EnergyCommunity-1/Sensor-1");

        result.IsSuccess.Should().BeTrue(result.ErrorMessage ?? "");
        result.CkTypeId.Should().Be("EnergyCommunity-1/Sensor-1");
        result.MaxDepth.Should().Be(5);
        result.PathCount.Should().Be(2);
        result.Paths.Should().NotBeNull().And.HaveCount(2);
    }

    [Fact]
    public async Task GetAvailableArchivePaths_MissingCkTypeId_ReturnsValidationError()
    {
        var result = await SchemaDiscoveryTools.GetAvailableArchivePaths(
            MockServer.Object, ckTypeId: "");

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("required");
    }

    // ── Test-fixture builders for CkTypeGraph / CkRecordGraph ─────────────────

    private static CkTypeGraph BuildType(string typeId,
        params (string Name, AttributeValueTypesDto ValueType, CkId<CkRecordId>? RecordId)[] attrs)
    {
        var allAttrs = BuildAttributes(attrs);
        var defined = allAttrs.Values
            .Select(g => new CkTypeAttributeDto
            {
                CkAttributeId = g.CkAttributeId,
                AttributeName = g.AttributeName
            })
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

    private static CkRecordGraph BuildRecord(CkId<CkRecordId> recordId,
        params (string Name, AttributeValueTypesDto ValueType, CkId<CkRecordId>? RecordId)[] attrs)
    {
        var allAttrs = BuildAttributes(attrs);
        var defined = allAttrs.Values
            .Select(g => new CkTypeAttributeDto
            {
                CkAttributeId = g.CkAttributeId,
                AttributeName = g.AttributeName
            })
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

    private static Dictionary<CkId<CkAttributeId>, CkTypeAttributeGraph> BuildAttributes(
        (string Name, AttributeValueTypesDto ValueType, CkId<CkRecordId>? RecordId)[] attrs)
    {
        var allAttrs = new Dictionary<CkId<CkAttributeId>, CkTypeAttributeGraph>();
        foreach (var (name, valueType, recordId) in attrs)
        {
            // CkId<T> format is "{ModelId}/{ElementId}-{Version}" (full qualified). The DTO's
            // AttributeId field, however, is the bare element id (e.g. "Name-1") — assigning the full
            // CkId string back to it fails the inner format check.
            var attrId = new CkId<CkAttributeId>($"EnergyCommunity-1/{name}-1");
            var ckAttrDto = new CkAttributeDto
            {
                AttributeId = $"{name}-1",
                ValueType = valueType,
                ValueCkRecordId = recordId
            };
            var ckAttrGraph = new CkAttributeGraph(attrId, ckAttrDto);
            var ckTypeAttrDto = new CkTypeAttributeDto
            {
                CkAttributeId = attrId,
                AttributeName = name
            };
            allAttrs[attrId] = new CkTypeAttributeGraph(attrId, ckTypeAttrDto, ckAttrGraph);
        }
        return allAttrs;
    }

    private static void SetupTryGetCkRecord(Mock<ICkCacheService> ckCache,
        CkId<CkRecordId> recordId, CkRecordGraph? recordGraph)
    {
        // Two overloads exist (one with NotNullWhen attribute). Set both up so callers reach the
        // same canned answer.
        ckCache
            .Setup(c => c.TryGetCkRecord(It.IsAny<string>(), recordId,
                out It.Ref<CkRecordGraph?>.IsAny))
            .Returns(new TryGetCkRecordCallback((string _, CkId<CkRecordId> _,
                out CkRecordGraph? rg) =>
            {
                rg = recordGraph;
                return recordGraph != null;
            }));
    }

    private delegate bool TryGetCkRecordCallback(string tenantId, CkId<CkRecordId> recordId,
        out CkRecordGraph? recordGraph);
}
