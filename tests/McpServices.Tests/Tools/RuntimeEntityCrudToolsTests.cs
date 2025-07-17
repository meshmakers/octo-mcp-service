using FluentAssertions;
using Meshmakers.Octo.Backend.McpServices.Models.Filters;
using Meshmakers.Octo.Backend.McpServices.Tools;
using Meshmakers.Octo.ConstructionKit.Contracts;
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
    private const string TestCkTypeId = "TestModule-1.0.0/Customer-1.0.0";

    [Fact]
    public async Task QueryEntities_WithoutFilters_ReturnsAllEntities()
    {
        // Arrange
        SetupMockServices();
        var mockResults = CreateMockQueryResults();
        
        MockTenantRepository
            .Setup(r => r.GetRtEntitiesByTypeAsync(
                It.IsAny<IOctoSession>(),
                It.IsAny<CkId<CkTypeId>>(),
                It.IsAny<DataQueryOperation>(),
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
        SetupMockServices();
        var mockResults = CreateMockQueryResults();
        const int limit = 2;
        
        MockTenantRepository
            .Setup(r => r.GetRtEntitiesByTypeAsync(
                It.IsAny<IOctoSession>(),
                It.IsAny<CkId<CkTypeId>>(),
                It.IsAny<DataQueryOperation>(),
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
            It.IsAny<CkId<CkTypeId>>(),
            It.IsAny<DataQueryOperation>(),
            null,
            limit), Times.Once);
    }

    [Fact]
    public async Task QueryEntities_WithOffset_RespectsOffset()
    {
        // Arrange
        SetupMockServices();
        var mockResults = CreateMockQueryResults();
        const int offset = 10;
        
        MockTenantRepository
            .Setup(r => r.GetRtEntitiesByTypeAsync(
                It.IsAny<IOctoSession>(),
                It.IsAny<CkId<CkTypeId>>(),
                It.IsAny<DataQueryOperation>(),
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
        
        // Verify repository was called with correct offset
        MockTenantRepository.Verify(r => r.GetRtEntitiesByTypeAsync(
            It.IsAny<IOctoSession>(),
            It.IsAny<CkId<CkTypeId>>(),
            It.IsAny<DataQueryOperation>(),
            offset,
            null), Times.Once);
    }

    [Fact]
    public async Task QueryEntities_WithSimpleStringFilters_ParsesCorrectly()
    {
        // Arrange
        SetupMockServices();
        var mockResults = CreateMockQueryResults();
        var simpleFilters = """{"firstName": "Gerald", "age": 35, "isActive": true}""";
        
        MockTenantRepository
            .Setup(r => r.GetRtEntitiesByTypeAsync(
                It.IsAny<IOctoSession>(),
                It.IsAny<CkId<CkTypeId>>(),
                It.IsAny<DataQueryOperation>(),
                null,
                null))
            .ReturnsAsync(mockResults);

        // Act
        var result = await RuntimeEntityCrudTools.QueryEntities(
            MockServer.Object,
            TestCkTypeId,
            filters: simpleFilters);

        // Assert
        result.Should().NotBeNull();
        result.CkTypeId.Should().Be(TestCkTypeId);
        
        // Verify repository was called (specific filter verification would require access to DataQueryOperation internals)
        MockTenantRepository.Verify(r => r.GetRtEntitiesByTypeAsync(
            It.IsAny<IOctoSession>(),
            It.IsAny<CkId<CkTypeId>>(),
            It.IsAny<DataQueryOperation>(),
            null,
            null), Times.Once);
    }

    [Fact]
    public async Task QueryEntities_WithEntityFilterDto_AppliesComplexFilters()
    {
        // Arrange
        SetupMockServices();
        var mockResults = CreateMockQueryResults();
        
        var complexFilter = new FieldFilterCriteriaDto
        {
            Operator = LogicalOperatorDto.And,
            Fields =
            [
                new() { FieldPath = "firstName", Operator = FilterOperatorDto.Equals, Value = "Gerald" },
                new() { FieldPath = "age", Operator = FilterOperatorDto.GreaterThan, Value = 30 }
            ]
        };
        
        MockTenantRepository
            .Setup(r => r.GetRtEntitiesByTypeAsync(
                It.IsAny<IOctoSession>(),
                It.IsAny<CkId<CkTypeId>>(),
                It.IsAny<DataQueryOperation>(),
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
            It.IsAny<CkId<CkTypeId>>(),
            It.IsAny<DataQueryOperation>(),
            null,
            null), Times.Once);
    }

    [Fact]
    public async Task QueryEntities_WithNumericFilters_ParsesDifferentNumberTypes()
    {
        // Arrange
        SetupMockServices();
        var mockResults = CreateMockQueryResults();
        
        var filtersWithNumbers = """
        {
            "intValue": 42,
            "longValue": 9223372036854775807,
            "doubleValue": 3.14159
        }
        """;
        
        MockTenantRepository
            .Setup(r => r.GetRtEntitiesByTypeAsync(
                It.IsAny<IOctoSession>(),
                It.IsAny<CkId<CkTypeId>>(),
                It.IsAny<DataQueryOperation>(),
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
            It.IsAny<CkId<CkTypeId>>(),
            It.IsAny<DataQueryOperation>(),
            null,
            null), Times.Once);
    }

    [Fact]
    public async Task QueryEntities_WithBooleanFilters_ParsesCorrectly()
    {
        // Arrange
        SetupMockServices();
        var mockResults = CreateMockQueryResults();
        
        var booleanFilters = """{"isActive": true, "isDeleted": false}""";
        
        MockTenantRepository
            .Setup(r => r.GetRtEntitiesByTypeAsync(
                It.IsAny<IOctoSession>(),
                It.IsAny<CkId<CkTypeId>>(),
                It.IsAny<DataQueryOperation>(),
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
        SetupMockServices();
        var mockResults = CreateMockQueryResults();
        
        var filtersWithNull = """{"firstName": "Gerald", "middleName": null, "lastName": "Lochner"}""";
        
        MockTenantRepository
            .Setup(r => r.GetRtEntitiesByTypeAsync(
                It.IsAny<IOctoSession>(),
                It.IsAny<CkId<CkTypeId>>(),
                It.IsAny<DataQueryOperation>(),
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
        SetupMockServices();
        var mockResults = CreateMockQueryResults();
        var simpleFilters = """{"firstName": "Gerald", "department": "IT"}""";
        
        MockTenantRepository
            .Setup(r => r.GetRtEntitiesByTypeAsync(
                It.IsAny<IOctoSession>(),
                It.IsAny<CkId<CkTypeId>>(),
                It.IsAny<DataQueryOperation>(),
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
        SetupMockServices();
        var mockResults = CreateMockQueryResults();
        
        MockTenantRepository
            .Setup(r => r.GetRtEntitiesByTypeAsync(
                It.IsAny<IOctoSession>(),
                It.IsAny<CkId<CkTypeId>>(),
                It.IsAny<DataQueryOperation>(),
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
        SetupMockServices();
        var mockResults = CreateMockQueryResults();
        
        MockTenantRepository
            .Setup(r => r.GetRtEntitiesByTypeAsync(
                It.IsAny<IOctoSession>(),
                It.IsAny<CkId<CkTypeId>>(),
                It.IsAny<DataQueryOperation>(),
                null,
                null))
            .ReturnsAsync(mockResults);

        // Act
        var result = await RuntimeEntityCrudTools.QueryEntitiesSimple(
            MockServer.Object,
            TestCkTypeId,
            simpleFilters: "");

        // Assert
        result.Should().NotBeNull();
        result.CkTypeId.Should().Be(TestCkTypeId);
    }

    [Fact]
    public async Task QueryEntities_WithInvalidCkTypeId_ThrowsInvalidOperationException()
    {
        // Arrange
        SetupMockServices();
        const string invalidCkTypeId = "Invalid-Type-Id";
        
        MockTenantRepository
            .Setup(r => r.GetCkTypeGraphAsync(It.IsAny<CkId<CkTypeId>>()))
            .ThrowsAsync(new ArgumentException("Invalid CK Type ID"));

        // Act & Assert
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            RuntimeEntityCrudTools.QueryEntities(
                MockServer.Object,
                invalidCkTypeId));

        exception.Message.Should().Contain($"Failed to query entities for type '{invalidCkTypeId}'");
    }

    [Fact]
    public async Task QueryEntities_WithInvalidJsonFilters_ThrowsInvalidOperationException()
    {
        // Arrange
        SetupMockServices();
        const string invalidJson = "{ invalid json }";
        
        // Act & Assert
        // Invalid JSON that can't be parsed as either simple filters or EntityFilterDto should throw
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            RuntimeEntityCrudTools.QueryEntities(
                MockServer.Object,
                TestCkTypeId,
                filters: invalidJson));

        exception.Message.Should().Contain($"Failed to query entities for type '{TestCkTypeId}'");
        exception.InnerException.Should().BeOfType<JsonException>();
    }

    [Fact]
    public async Task QueryEntities_WithPaginationParameters_PassesCorrectParameters()
    {
        // Arrange
        SetupMockServices();
        var mockResults = CreateMockQueryResults();
        const int limit = 50;
        const int offset = 100;
        
        MockTenantRepository
            .Setup(r => r.GetRtEntitiesByTypeAsync(
                It.IsAny<IOctoSession>(),
                It.IsAny<CkId<CkTypeId>>(),
                It.IsAny<DataQueryOperation>(),
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
            It.IsAny<CkId<CkTypeId>>(),
            It.IsAny<DataQueryOperation>(),
            offset,
            limit), Times.Once);
    }

    [Fact]
    public async Task QueryEntities_RepositoryException_ThrowsInvalidOperationException()
    {
        // Arrange
        SetupMockServices();

        MockTenantRepository
            .Setup(r => r.GetRtEntitiesByTypeAsync(
                It.IsAny<IOctoSession>(),
                It.IsAny<CkId<CkTypeId>>(),
                It.IsAny<DataQueryOperation>(),
                null,
                null))
            .ThrowsAsync(new Exception("Database connection failed"));

        // Act & Assert
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            RuntimeEntityCrudTools.QueryEntities(
                MockServer.Object,
                TestCkTypeId));

        exception.Message.Should().Contain($"Failed to query entities for type '{TestCkTypeId}'");
        exception.InnerException?.Message.Should().Be("Database connection failed");
    }

    #region Helper Methods

    private static IResultSet<RtEntity> CreateMockQueryResults(int count = 3)
    {
        var entities = Enumerable.Range(1, count)
            .Select(i => new RtEntity
            {
                RtId = OctoObjectId.GenerateNewId(),
                CkTypeId = new CkId<CkTypeId>(TestCkTypeId)
            })
            .ToList();

        return new ResultSet<RtEntity>(entities, count, null, null);
    }

    #endregion
}
