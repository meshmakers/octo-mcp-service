using FluentAssertions;
using Meshmakers.Octo.Backend.McpServices.Services;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Services.Infrastructure.Services;
using Moq;
using Xunit;

namespace McpServices.Tests.Services;

/// <summary>
///     Tests for tenant resolution logic (tool parameter > route parameter > error).
/// </summary>
public class TenantResolutionServiceTests
{
    private readonly Mock<IOctoHttpContextAccessor> _mockHttpContextAccessor = new();
    private readonly Mock<ISystemContext> _mockSystemContext = new();
    private readonly Mock<ITenantRepository> _mockTenantRepository = new();

    private TenantResolutionService CreateService()
    {
        return new TenantResolutionService(
            _mockHttpContextAccessor.Object,
            _mockSystemContext.Object);
    }

    [Fact]
    public void ResolveTenantId_WithToolParameter_ReturnsThatTenantId()
    {
        // Arrange
        var service = CreateService();

        // Act
        var result = service.ResolveTenantId("explicit-tenant");

        // Assert
        result.Should().Be("explicit-tenant");
    }

    [Fact]
    public void ResolveTenantId_WithNullToolParameter_FallsBackToRoute()
    {
        // Arrange
        _mockHttpContextAccessor.Setup(h => h.GetTenantId()).Returns("route-tenant");
        var service = CreateService();

        // Act
        var result = service.ResolveTenantId(null);

        // Assert
        result.Should().Be("route-tenant");
    }

    [Fact]
    public void ResolveTenantId_WithEmptyToolParameter_FallsBackToRoute()
    {
        // Arrange
        _mockHttpContextAccessor.Setup(h => h.GetTenantId()).Returns("route-tenant");
        var service = CreateService();

        // Act
        var result = service.ResolveTenantId("");

        // Assert
        result.Should().Be("route-tenant");
    }

    [Fact]
    public void ResolveTenantId_WithNoTenantAvailable_ThrowsInvalidOperationException()
    {
        // Arrange
        _mockHttpContextAccessor.Setup(h => h.GetTenantId()).Throws(new InvalidOperationException("No tenant"));
        var service = CreateService();

        // Act & Assert
        var act = () => service.ResolveTenantId(null);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*No tenant ID specified*");
    }

    [Fact]
    public void ResolveTenantId_ToolParameterTakesPriority_OverRoute()
    {
        // Arrange
        _mockHttpContextAccessor.Setup(h => h.GetTenantId()).Returns("route-tenant");
        var service = CreateService();

        // Act
        var result = service.ResolveTenantId("tool-tenant");

        // Assert
        result.Should().Be("tool-tenant");
        // Route accessor should not have been called
        _mockHttpContextAccessor.Verify(h => h.GetTenantId(), Times.Never);
    }

    [Fact]
    public async Task GetTenantRepositoryAsync_WithToolParameter_UsesSystemContext()
    {
        // Arrange
        _mockTenantRepository.Setup(r => r.TenantId).Returns("my-tenant");
        _mockSystemContext.Setup(s => s.FindTenantRepositoryAsync("my-tenant"))
            .ReturnsAsync(_mockTenantRepository.Object);
        var service = CreateService();

        // Act
        var repo = await service.GetTenantRepositoryAsync("my-tenant");

        // Assert
        repo.Should().NotBeNull();
        repo.TenantId.Should().Be("my-tenant");
        _mockSystemContext.Verify(s => s.FindTenantRepositoryAsync("my-tenant"), Times.Once);
    }
}
