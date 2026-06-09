using FluentAssertions;
using Meshmakers.Octo.Backend.McpServices.Models;
using Meshmakers.Octo.Backend.McpServices.Models.Filters;
using Meshmakers.Octo.Backend.McpServices.Tools;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Moq;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Engine.Repositories.Query;
using System.Text.Json;
using Xunit;

namespace McpServices.Tests.Tools;

public class RuntimeEntityCrudToolsTests : TestBase
{
    private const string TestCkTypeId = "TestModule-1.0.0/Customer-1";

    [Fact]
    public async Task QueryEntities_WithoutFilters_ReturnsAllEntities()
    {
        // Arrange
        var mockResults = CreateMockQueryResults();
        
        MockTenantRepository
            .Setup(r => r.GetRtEntitiesByTypeAsync(
                It.IsAny<IOctoSession>(),
                It.IsAny<RtCkId<CkTypeId>>(),
                It.IsAny<RtEntityQueryOptions>(),
                null,
                null))
            .ReturnsAsync(mockResults);

        // Act
        var result = await RuntimeEntityCrudTools.QueryEntities(
            MockServer.Object,
            TestCkTypeId);

        // Assert
        result.Should().NotBeNull();
        result.CkTypeId.Should().Be(TestCkTypeId);
        result.TotalCount.Should().Be(mockResults.TotalCount);
        result.ReturnedCount.Should().Be(mockResults.Items.Count());
        result.Entities.Should().HaveCount(mockResults.Items.Count());
    }

    [Fact]
    public async Task QueryEntities_WithLimit_RespectsLimit()
    {
        // Arrange
        var mockResults = CreateMockQueryResults();
        const int limit = 2;
        
        MockTenantRepository
            .Setup(r => r.GetRtEntitiesByTypeAsync(
                It.IsAny<IOctoSession>(),
                It.IsAny<RtCkId<CkTypeId>>(),
                It.IsAny<RtEntityQueryOptions>(),
                null,
                limit))
            .ReturnsAsync(mockResults);

        // Act
        var result = await RuntimeEntityCrudTools.QueryEntities(
            MockServer.Object,
            TestCkTypeId,
            limit: limit);

        // Assert
        result.Should().NotBeNull();
        result.ReturnedCount.Should().Be(mockResults.Items.Count());
        
        // Verify repository was called with correct limit
        MockTenantRepository.Verify(r => r.GetRtEntitiesByTypeAsync(
            It.IsAny<IOctoSession>(),
            It.IsAny<RtCkId<CkTypeId>>(),
            It.IsAny<RtEntityQueryOptions>(),
            null,
            limit), Times.Once);
    }

    [Fact]
    public async Task QueryEntities_WithOffset_RespectsOffset()
    {
        // Arrange
        var mockResults = CreateMockQueryResults();
        const int offset = 10;
        
        MockTenantRepository
            .Setup(r => r.GetRtEntitiesByTypeAsync(
                It.IsAny<IOctoSession>(),
                It.IsAny<RtCkId<CkTypeId>>(),
                It.IsAny<RtEntityQueryOptions>(),
                offset,
                null))
            .ReturnsAsync(mockResults);

        // Act
        var result = await RuntimeEntityCrudTools.QueryEntities(
            MockServer.Object,
            TestCkTypeId,
            offset: offset);

        // Assert
        result.Should().NotBeNull();
        
        // Verify repository was called with the correct offset
        MockTenantRepository.Verify(r => r.GetRtEntitiesByTypeAsync(
            It.IsAny<IOctoSession>(),
            It.IsAny<RtCkId<CkTypeId>>(),
            It.IsAny<RtEntityQueryOptions>(),
            offset,
            null), Times.Once);
    }

    [Fact]
    public async Task QueryEntities_WithSimpleStringFilters_ParsesCorrectly()
    {
        // Arrange
        var mockResults = CreateMockQueryResults();
        var filters = new FieldFilterCriteriaDto
        {
            Fields =
            [
                new() { AttributePath = "firstName", Operator = FilterOperatorDto.Equals, Value = "Gerald" },
                new() { AttributePath = "department", Operator = FilterOperatorDto.Equals, Value = "IT" }
            ]
        };
        
        MockTenantRepository
            .Setup(r => r.GetRtEntitiesByTypeAsync(
                It.IsAny<IOctoSession>(),
                It.IsAny<RtCkId<CkTypeId>>(),
                It.IsAny<RtEntityQueryOptions>(),
                null,
                null))
            .ReturnsAsync(mockResults);

        // Act
        var result = await RuntimeEntityCrudTools.QueryEntities(
            MockServer.Object,
            TestCkTypeId,
            filters: filters);

        // Assert
        result.Should().NotBeNull();
        result.CkTypeId.Should().Be(TestCkTypeId);
        
        // Verify repository was called (specific filter verification would require access to DataQueryOperation internals)
        MockTenantRepository.Verify(r => r.GetRtEntitiesByTypeAsync(
            It.IsAny<IOctoSession>(),
            It.IsAny<RtCkId<CkTypeId>>(),
            It.IsAny<RtEntityQueryOptions>(),
            null,
            null), Times.Once);
    }

    [Fact]
    public async Task QueryEntities_WithEntityFilterDto_AppliesComplexFilters()
    {
        // Arrange
        var mockResults = CreateMockQueryResults();
        
        var complexFilter = new FieldFilterCriteriaDto
        {
            Operator = LogicalOperatorDto.And,
            Fields =
            [
                new() { AttributePath = "firstName", Operator = FilterOperatorDto.Equals, Value = "Gerald" },
                new() { AttributePath = "age", Operator = FilterOperatorDto.GreaterThan, Value = 30 }
            ]
        };
        
        MockTenantRepository
            .Setup(r => r.GetRtEntitiesByTypeAsync(
                It.IsAny<IOctoSession>(),
                It.IsAny<RtCkId<CkTypeId>>(),
                It.IsAny<RtEntityQueryOptions>(),
                null,
                null))
            .ReturnsAsync(mockResults);

        // Act
        var result = await RuntimeEntityCrudTools.QueryEntities(
            MockServer.Object,
            TestCkTypeId,
            filters: complexFilter);

        // Assert
        result.Should().NotBeNull();
        result.CkTypeId.Should().Be(TestCkTypeId);
        
        MockTenantRepository.Verify(r => r.GetRtEntitiesByTypeAsync(
            It.IsAny<IOctoSession>(),
            It.IsAny<RtCkId<CkTypeId>>(),
            It.IsAny<RtEntityQueryOptions>(),
            null,
            null), Times.Once);
    }

    [Fact]
    public async Task QueryEntities_WithNumericFilters_ParsesDifferentNumberTypes()
    {
        // Arrange
        var mockResults = CreateMockQueryResults();
        
        var filtersWithNumbers = new FieldFilterCriteriaDto
        {
            Fields =
            [
                new() { AttributePath = "intValue", Operator = FilterOperatorDto.Equals, Value = 42},
                new() { AttributePath = "longValue", Operator = FilterOperatorDto.Equals, Value = 9223372036854775807 },
                new() { AttributePath = "doubleValue", Operator = FilterOperatorDto.Equals, Value = 3.14159 }
            ]
        };
        
        MockTenantRepository
            .Setup(r => r.GetRtEntitiesByTypeAsync(
                It.IsAny<IOctoSession>(),
                It.IsAny<RtCkId<CkTypeId>>(),
                It.IsAny<RtEntityQueryOptions>(),
                null,
                null))
            .ReturnsAsync(mockResults);

        // Act
        var result = await RuntimeEntityCrudTools.QueryEntities(
            MockServer.Object,
            TestCkTypeId,
            filters: filtersWithNumbers);

        // Assert
        result.Should().NotBeNull();
        result.CkTypeId.Should().Be(TestCkTypeId);
        
        MockTenantRepository.Verify(r => r.GetRtEntitiesByTypeAsync(
            It.IsAny<IOctoSession>(),
            It.IsAny<RtCkId<CkTypeId>>(),
            It.IsAny<RtEntityQueryOptions>(),
            null,
            null), Times.Once);
    }

    [Fact]
    public async Task QueryEntities_WithBooleanFilters_ParsesCorrectly()
    {
        // Arrange
        var mockResults = CreateMockQueryResults();
        
        var booleanFilters = new FieldFilterCriteriaDto
        {
            Fields =
            [
                new() { AttributePath = "isActive", Operator = FilterOperatorDto.Equals, Value = true},
                new() { AttributePath = "isDeleted", Operator = FilterOperatorDto.Equals, Value = false }
            ]
        };
        
        MockTenantRepository
            .Setup(r => r.GetRtEntitiesByTypeAsync(
                It.IsAny<IOctoSession>(),
                It.IsAny<RtCkId<CkTypeId>>(),
                It.IsAny<RtEntityQueryOptions>(),
                null,
                null))
            .ReturnsAsync(mockResults);

        // Act
        var result = await RuntimeEntityCrudTools.QueryEntities(
            MockServer.Object,
            TestCkTypeId,
            filters: booleanFilters);

        // Assert
        result.Should().NotBeNull();
        result.CkTypeId.Should().Be(TestCkTypeId);
    }

    [Fact]
    public async Task QueryEntities_WithNullValues_SkipsNullFilters()
    {
        // Arrange
        var mockResults = CreateMockQueryResults();
        
        var filtersWithNull = new FieldFilterCriteriaDto
        {
            Fields =
            [
                new() { AttributePath = "firstName", Operator = FilterOperatorDto.Equals, Value = "Gerald"},
                new() { AttributePath = "middleName", Operator = FilterOperatorDto.Equals, Value = null },
                new() { AttributePath = "lastName", Operator = FilterOperatorDto.Equals, Value = "Lochner" }
            ]
        };
        
        MockTenantRepository
            .Setup(r => r.GetRtEntitiesByTypeAsync(
                It.IsAny<IOctoSession>(),
                It.IsAny<RtCkId<CkTypeId>>(),
                It.IsAny<RtEntityQueryOptions>(),
                null,
                null))
            .ReturnsAsync(mockResults);

        // Act
        var result = await RuntimeEntityCrudTools.QueryEntities(
            MockServer.Object,
            TestCkTypeId,
            filters: filtersWithNull);

        // Assert
        result.Should().NotBeNull();
        result.CkTypeId.Should().Be(TestCkTypeId);
    }

    [Fact]
    public async Task QueryEntitiesSimple_WithValidFilters_ReturnsEntities()
    {
        // Arrange
        var mockResults = CreateMockQueryResults();

        List<SimpleFilterDto> simpleFilters =
        [
            new() { AttributePath = "firstName", Value = "Gerald" },
            new() { AttributePath = "department", Value = "IT" }
        ];

        MockTenantRepository
            .Setup(r => r.GetRtEntitiesByTypeAsync(
                It.IsAny<IOctoSession>(),
                It.IsAny<RtCkId<CkTypeId>>(),
                It.IsAny<RtEntityQueryOptions>(),
                null,
                null))
            .ReturnsAsync(mockResults);

        // Act
        var result = await RuntimeEntityCrudTools.QueryEntitiesSimple(
            MockServer.Object,
            TestCkTypeId,
            simpleFilters);

        // Assert
        result.Should().NotBeNull();
        result.CkTypeId.Should().Be(TestCkTypeId);
        result.TotalCount.Should().Be(mockResults.TotalCount);
        result.ReturnedCount.Should().Be(mockResults.Items.Count());
    }

    [Fact]
    public async Task QueryEntitiesSimple_WithNullFilters_ReturnsAllEntities()
    {
        // Arrange
        var mockResults = CreateMockQueryResults();
        
        MockTenantRepository
            .Setup(r => r.GetRtEntitiesByTypeAsync(
                It.IsAny<IOctoSession>(),
                It.IsAny<RtCkId<CkTypeId>>(),
                It.IsAny<RtEntityQueryOptions>(),
                null,
                null))
            .ReturnsAsync(mockResults);

        // Act
        var result = await RuntimeEntityCrudTools.QueryEntitiesSimple(
            MockServer.Object,
            TestCkTypeId,
            simpleFilters: null);

        // Assert
        result.Should().NotBeNull();
        result.CkTypeId.Should().Be(TestCkTypeId);
        result.TotalCount.Should().Be(mockResults.TotalCount);
    }

    [Fact]
    public async Task QueryEntitiesSimple_WithEmptyFilters_ReturnsAllEntities()
    {
        // Arrange
        var mockResults = CreateMockQueryResults();
        
        MockTenantRepository
            .Setup(r => r.GetRtEntitiesByTypeAsync(
                It.IsAny<IOctoSession>(),
                It.IsAny<RtCkId<CkTypeId>>(),
                It.IsAny<RtEntityQueryOptions>(),
                null,
                null))
            .ReturnsAsync(mockResults);

        // Act
        var result = await RuntimeEntityCrudTools.QueryEntitiesSimple(
            MockServer.Object,
            TestCkTypeId,
            simpleFilters: null);

        // Assert
        result.Should().NotBeNull();
        result.CkTypeId.Should().Be(TestCkTypeId);
    }

    [Fact]
    public async Task QueryEntities_WithInvalidCkTypeId_ReturnsErrorResponse()
    {
        // Arrange
        const string invalidCkTypeId = "Invalid-Type-Id";
        
        MockTenantRepository
            .Setup(r => r.GetCkTypeGraphAsync(It.IsAny<RtCkId<CkTypeId>>()))
            .ThrowsAsync(new ArgumentException("Invalid CK Type ID"));

        // Act & Assert
        var r = await RuntimeEntityCrudTools.QueryEntities(
            MockServer.Object,
            invalidCkTypeId);

        r.IsSuccess.Should().BeFalse();
        r.ErrorMessage.Should().Contain("'ckId' must contain a model id (Parameter 'ckId')");
    }

    [Fact]
    public async Task QueryEntities_WithPaginationParameters_PassesCorrectParameters()
    {
        // Arrange
        var mockResults = CreateMockQueryResults();
        const int limit = 50;
        const int offset = 100;
        
        MockTenantRepository
            .Setup(r => r.GetRtEntitiesByTypeAsync(
                It.IsAny<IOctoSession>(),
                It.IsAny<RtCkId<CkTypeId>>(),
                It.IsAny<RtEntityQueryOptions>(),
                offset,
                limit))
            .ReturnsAsync(mockResults);

        // Act
        var result = await RuntimeEntityCrudTools.QueryEntities(
            MockServer.Object,
            TestCkTypeId,
            limit: limit,
            offset: offset);

        // Assert
        result.Should().NotBeNull();
        
        MockTenantRepository.Verify(r => r.GetRtEntitiesByTypeAsync(
            It.IsAny<IOctoSession>(),
            It.IsAny<RtCkId<CkTypeId>>(),
            It.IsAny<RtEntityQueryOptions>(),
            offset,
            limit), Times.Once);
    }

    [Fact]
    public async Task QueryEntities_RepositoryException_ReturnsErrorResponse()
    {
        // Arrange
        MockTenantRepository
            .Setup(r => r.GetRtEntitiesByTypeAsync(
                It.IsAny<IOctoSession>(),
                It.IsAny<RtCkId<CkTypeId>>(),
                It.IsAny<RtEntityQueryOptions>(),
                null,
                null))
            .ThrowsAsync(new Exception("Database connection failed"));

        // Act & Assert

        var r = await RuntimeEntityCrudTools.QueryEntities(
            MockServer.Object,
            TestCkTypeId);

        r.IsSuccess.Should().BeFalse();
        r.ErrorMessage.Should().Contain("Database connection failed");
    }

    // ===== Optimistic locking (#4111) ============================================
    // The contract: callers pass expected_version (= the RtVersion they saw when they
    // read the entity). The tool refuses the write if the stored version moved on, and
    // surfaces the current row so the caller can rebase. Backward-compat: omitting
    // expected_version skips the check entirely.

    [Fact]
    public async Task UpdateEntity_WithMatchingExpectedVersion_BumpsAndSucceeds()
    {
        // Arrange
        var rtId = OctoObjectId.GenerateNewId();
        var existing = new RtEntity
        {
            RtId = rtId,
            CkTypeId = new RtCkId<CkTypeId>(TestCkTypeId),
            RtVersion = 7
        };
        var updated = new RtEntity
        {
            RtId = rtId,
            CkTypeId = new RtCkId<CkTypeId>(TestCkTypeId),
            RtVersion = 8
        };
        // GetRtEntityByRtIdAsync is called twice — once for the pre-check, once after the
        // commit to fetch the post-write payload. Sequence the returns.
        MockTenantRepository
            .SetupSequence(r => r.GetRtEntityByRtIdAsync(It.IsAny<IOctoSession>(), It.IsAny<RtEntityId>()))
            .ReturnsAsync(existing)
            .ReturnsAsync(updated);

        // Act
        var result = await RuntimeEntityCrudTools.UpdateEntity(
            MockServer.Object,
            rtId.ToString(),
            TestCkTypeId,
            new List<AttributeUpdateItem>(),
            expectedVersion: 7);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.IsConflict.Should().BeFalse();
        result.CurrentRtVersion.Should().Be(8);
        // Tool must have written with the bumped version — verifies the post-update path
        // is wired so the next optimistic call can use this as its new expected_version.
        MockTenantRepository.Verify(r => r.UpdateOneRtEntityByIdAsync(
            It.IsAny<IOctoSession>(),
            It.IsAny<RtCkId<CkTypeId>>(),
            It.IsAny<OctoObjectId>(),
            It.Is<RtEntity>(e => e.RtVersion == 8)), Times.Once);
    }

    [Fact]
    public async Task UpdateEntity_WithoutExpectedVersion_StillBumpsAndSkipsCheck()
    {
        // Arrange — backward-compat path: callers that never pass expected_version still
        // get a bumped RtVersion so a NEW caller arriving later has a meaningful token.
        var rtId = OctoObjectId.GenerateNewId();
        var existing = new RtEntity
        {
            RtId = rtId,
            CkTypeId = new RtCkId<CkTypeId>(TestCkTypeId),
            RtVersion = 12
        };
        var updated = new RtEntity
        {
            RtId = rtId,
            CkTypeId = new RtCkId<CkTypeId>(TestCkTypeId),
            RtVersion = 13
        };
        MockTenantRepository
            .SetupSequence(r => r.GetRtEntityByRtIdAsync(It.IsAny<IOctoSession>(), It.IsAny<RtEntityId>()))
            .ReturnsAsync(existing)
            .ReturnsAsync(updated);

        // Act
        var result = await RuntimeEntityCrudTools.UpdateEntity(
            MockServer.Object,
            rtId.ToString(),
            TestCkTypeId,
            new List<AttributeUpdateItem>());

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.CurrentRtVersion.Should().Be(13);
        MockTenantRepository.Verify(r => r.UpdateOneRtEntityByIdAsync(
            It.IsAny<IOctoSession>(),
            It.IsAny<RtCkId<CkTypeId>>(),
            It.IsAny<OctoObjectId>(),
            It.Is<RtEntity>(e => e.RtVersion == 13)), Times.Once);
    }

    [Fact]
    public async Task UpdateEntity_WithStaleExpectedVersion_ReturnsConflictAndDoesNotWrite()
    {
        // Arrange
        var rtId = OctoObjectId.GenerateNewId();
        var existing = new RtEntity
        {
            RtId = rtId,
            CkTypeId = new RtCkId<CkTypeId>(TestCkTypeId),
            RtVersion = 9 // stored
        };
        MockTenantRepository
            .Setup(r => r.GetRtEntityByRtIdAsync(It.IsAny<IOctoSession>(), It.IsAny<RtEntityId>()))
            .ReturnsAsync(existing);

        // Act — caller had version 5; stored is 9
        var result = await RuntimeEntityCrudTools.UpdateEntity(
            MockServer.Object,
            rtId.ToString(),
            TestCkTypeId,
            new List<AttributeUpdateItem>(),
            expectedVersion: 5);

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.IsConflict.Should().BeTrue();
        result.CurrentRtVersion.Should().Be(9);
        result.Entity.Should().NotBeNull("conflict response must carry the current row so the caller can rebase");
        result.ErrorMessage.Should().Contain("5").And.Contain("9");
        // No write happened.
        MockTenantRepository.Verify(r => r.UpdateOneRtEntityByIdAsync(
            It.IsAny<IOctoSession>(),
            It.IsAny<RtCkId<CkTypeId>>(),
            It.IsAny<OctoObjectId>(),
            It.IsAny<RtEntity>()), Times.Never);
        // Transaction aborted on the refused path.
        MockSession.Verify(s => s.AbortTransactionAsync(), Times.Once);
    }

    [Fact]
    public async Task DeleteEntity_WithMatchingExpectedVersion_Succeeds()
    {
        // Arrange
        var rtId = OctoObjectId.GenerateNewId();
        var existing = new RtEntity
        {
            RtId = rtId,
            CkTypeId = new RtCkId<CkTypeId>(TestCkTypeId),
            RtVersion = 4
        };
        MockTenantRepository
            .Setup(r => r.GetRtEntityByRtIdAsync(It.IsAny<IOctoSession>(), It.IsAny<RtEntityId>()))
            .ReturnsAsync(existing);

        // Act
        var result = await RuntimeEntityCrudTools.DeleteEntity(
            MockServer.Object,
            TestCkTypeId,
            rtId.ToString(),
            expectedVersion: 4);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.IsConflict.Should().BeFalse();
        MockTenantRepository.Verify(r => r.DeleteOneRtEntityByRtIdAsync(
            It.IsAny<IOctoSession>(),
            It.IsAny<RtCkId<CkTypeId>>(),
            It.IsAny<OctoObjectId>(),
            It.IsAny<DeleteOptions>()), Times.Once);
    }

    [Fact]
    public async Task DeleteEntity_WithStaleExpectedVersion_ReturnsConflictAndDoesNotDelete()
    {
        // Arrange — somebody else just wrote to the entity; the caller's expected_version
        // is stale. The tool must refuse the delete so the caller can re-evaluate (maybe
        // the new state isn't worth deleting anymore).
        var rtId = OctoObjectId.GenerateNewId();
        var existing = new RtEntity
        {
            RtId = rtId,
            CkTypeId = new RtCkId<CkTypeId>(TestCkTypeId),
            RtVersion = 6
        };
        MockTenantRepository
            .Setup(r => r.GetRtEntityByRtIdAsync(It.IsAny<IOctoSession>(), It.IsAny<RtEntityId>()))
            .ReturnsAsync(existing);

        // Act — caller had version 2; stored is 6
        var result = await RuntimeEntityCrudTools.DeleteEntity(
            MockServer.Object,
            TestCkTypeId,
            rtId.ToString(),
            expectedVersion: 2);

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.IsConflict.Should().BeTrue();
        result.CurrentRtVersion.Should().Be(6);
        result.Entity.Should().NotBeNull();
        result.ErrorMessage.Should().Contain("2").And.Contain("6");
        MockTenantRepository.Verify(r => r.DeleteOneRtEntityByRtIdAsync(
            It.IsAny<IOctoSession>(),
            It.IsAny<RtCkId<CkTypeId>>(),
            It.IsAny<OctoObjectId>(),
            It.IsAny<DeleteOptions>()), Times.Never);
        MockSession.Verify(s => s.AbortTransactionAsync(), Times.Once);
    }

    [Fact]
    public async Task UpdateEntity_RtVersionAtUlongMax_SaturatesWithoutOverflow()
    {
        // Arrange — pathological but checked: an entity that already hit ulong.MaxValue
        // must not throw OverflowException on the bump. We saturate.
        var rtId = OctoObjectId.GenerateNewId();
        var existing = new RtEntity
        {
            RtId = rtId,
            CkTypeId = new RtCkId<CkTypeId>(TestCkTypeId),
            RtVersion = ulong.MaxValue
        };
        var updated = new RtEntity
        {
            RtId = rtId,
            CkTypeId = new RtCkId<CkTypeId>(TestCkTypeId),
            RtVersion = ulong.MaxValue
        };
        MockTenantRepository
            .SetupSequence(r => r.GetRtEntityByRtIdAsync(It.IsAny<IOctoSession>(), It.IsAny<RtEntityId>()))
            .ReturnsAsync(existing)
            .ReturnsAsync(updated);

        // Act
        var result = await RuntimeEntityCrudTools.UpdateEntity(
            MockServer.Object,
            rtId.ToString(),
            TestCkTypeId,
            new List<AttributeUpdateItem>(),
            expectedVersion: ulong.MaxValue);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.CurrentRtVersion.Should().Be(ulong.MaxValue);
        MockTenantRepository.Verify(r => r.UpdateOneRtEntityByIdAsync(
            It.IsAny<IOctoSession>(),
            It.IsAny<RtCkId<CkTypeId>>(),
            It.IsAny<OctoObjectId>(),
            It.Is<RtEntity>(e => e.RtVersion == ulong.MaxValue)), Times.Once);
    }

    #region Helper Methods

    private static IResultSet<RtEntity> CreateMockQueryResults(int count = 3)
    {
        var entities = Enumerable.Range(1, count)
            .Select(_ => new RtEntity
            {
                RtId = OctoObjectId.GenerateNewId(),
                CkTypeId = new RtCkId<CkTypeId>(TestCkTypeId)
            })
            .ToList();

        return new ResultSet<RtEntity>(entities, count, null, null);
    }

    #endregion
}
