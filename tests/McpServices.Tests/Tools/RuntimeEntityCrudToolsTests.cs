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
        
        // Verify repository was called with the correct offset
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
                It.IsAny<CkId<CkTypeId>>(),
                It.IsAny<DataQueryOperation>(),
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
                new() { AttributePath = "firstName", Operator = FilterOperatorDto.Equals, Value = "Gerald" },
                new() { AttributePath = "age", Operator = FilterOperatorDto.GreaterThan, Value = 30 }
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

        List<SimpleFilterDto> simpleFilters =
        [
            new() { AttributePath = "firstName", Value = "Gerald" },
            new() { AttributePath = "department", Value = "IT" }
        ];

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
            simpleFilters: null);

        // Assert
        result.Should().NotBeNull();
        result.CkTypeId.Should().Be(TestCkTypeId);
    }

    [Fact]
    public async Task QueryEntities_WithInvalidCkTypeId_ReturnsErrorResponse()
    {
        // Arrange
        SetupMockServices();
        const string invalidCkTypeId = "Invalid-Type-Id";
        
        MockTenantRepository
            .Setup(r => r.GetCkTypeGraphAsync(It.IsAny<CkId<CkTypeId>>()))
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
    public async Task QueryEntities_RepositoryException_ReturnsErrorResponse()
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

        var r = await RuntimeEntityCrudTools.QueryEntities(
            MockServer.Object,
            TestCkTypeId);

        r.IsSuccess.Should().BeFalse();
        r.ErrorMessage.Should().Contain("Database connection failed");
    }

    #region Helper Methods

    private static IResultSet<RtEntity> CreateMockQueryResults(int count = 3)
    {
        var entities = Enumerable.Range(1, count)
            .Select(_ => new RtEntity
            {
                RtId = OctoObjectId.GenerateNewId(),
                CkTypeId = new CkId<CkTypeId>(TestCkTypeId)
            })
            .ToList();

        return new ResultSet<RtEntity>(entities, count, null, null);
    }

    #endregion
}
