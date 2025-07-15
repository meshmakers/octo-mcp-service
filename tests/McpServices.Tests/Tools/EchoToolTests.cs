using FluentAssertions;
using Meshmakers.Octo.Backend.McpServices.Tools;
using Meshmakers.Octo.Services.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace McpServices.Tests.Tools;

public class EchoToolTests : TestBase
{
    [Fact]
    public async Task Echo_WithValidMessage_ReturnsExpectedFormat()
    {
        // Arrange
        SetupMockServices();
        const string inputMessage = "Hello World";
        const string expectedTenantId = "test-tenant";
        
        // Act
        var result = await EchoTool.Echo(MockServer.Object, inputMessage);
        
        // Assert
        result.Should().NotBeNull();
        result.Should().BeOfType<string>();
        result.Should().Contain("hello " + inputMessage);
        result.Should().Contain("from tenant " + expectedTenantId);
        result.Should().Be($"hello {inputMessage}, from tenant {expectedTenantId}");
    }
    
    [Fact]
    public async Task Echo_WithEmptyMessage_ReturnsCorrectFormat()
    {
        // Arrange
        SetupMockServices();
        const string inputMessage = "";
        const string expectedTenantId = "test-tenant";
        
        // Act
        var result = await EchoTool.Echo(MockServer.Object, inputMessage);
        
        // Assert
        result.Should().NotBeNull();
        result.Should().Be($"hello , from tenant {expectedTenantId}");
    }
    
    [Fact]
    public async Task Echo_WithNullMessage_ReturnsCorrectFormat()
    {
        // Arrange
        SetupMockServices();
        const string inputMessage = "test";
        const string expectedTenantId = "test-tenant";
        
        // Act
        var result = await EchoTool.Echo(MockServer.Object, inputMessage);
        
        // Assert
        result.Should().NotBeNull();
        result.Should().Be($"hello test, from tenant {expectedTenantId}");
    }
    
    [Fact]
    public async Task Echo_WithSpecialCharacters_PreservesMessage()
    {
        // Arrange
        SetupMockServices();
        const string inputMessage = "Test with @#$%^&*()_+{}[]|\\:;\"'<>,.?/~`";
        const string expectedTenantId = "test-tenant";
        
        // Act
        var result = await EchoTool.Echo(MockServer.Object, inputMessage);
        
        // Assert
        result.Should().NotBeNull();
        result.Should().Contain(inputMessage);
        result.Should().Be($"hello {inputMessage}, from tenant {expectedTenantId}");
    }
    
    [Fact]
    public async Task Echo_WithUnicodeCharacters_PreservesMessage()
    {
        // Arrange
        SetupMockServices();
        const string inputMessage = "Hello 世界 🌍 café naïve résumé";
        const string expectedTenantId = "test-tenant";
        
        // Act
        var result = await EchoTool.Echo(MockServer.Object, inputMessage);
        
        // Assert
        result.Should().NotBeNull();
        result.Should().Contain(inputMessage);
        result.Should().Be($"hello {inputMessage}, from tenant {expectedTenantId}");
    }
    
    [Fact]
    public async Task Echo_WithLongMessage_HandlesCorrectly()
    {
        // Arrange
        SetupMockServices();
        var inputMessage = new string('A', 1000); // 1000 character message
        const string expectedTenantId = "test-tenant";
        
        // Act
        var result = await EchoTool.Echo(MockServer.Object, inputMessage);
        
        // Assert
        result.Should().NotBeNull();
        result.Should().Contain(inputMessage);
        result.Should().Be($"hello {inputMessage}, from tenant {expectedTenantId}");
        result.Length.Should().Be(inputMessage.Length + "hello ".Length + ", from tenant ".Length + expectedTenantId.Length);
    }
    
    [Fact]
    public async Task Echo_WithDifferentTenantId_ReturnsCorrectTenantId()
    {
        // Arrange
        SetupMockServices();
        const string inputMessage = "Test Message";
        const string customTenantId = "custom-tenant-123";
        
        // Override the tenant ID for this test
        MockHttpContextAccessor.Setup(h => h.GetTenantId()).Returns(customTenantId);
        MockTenantRepository.Setup(tr => tr.TenantId).Returns(customTenantId);
        
        // Act
        var result = await EchoTool.Echo(MockServer.Object, inputMessage);
        
        // Assert
        result.Should().NotBeNull();
        result.Should().Contain(customTenantId);
        result.Should().Be($"hello {inputMessage}, from tenant {customTenantId}");
    }
    
    [Fact]
    public async Task Echo_AccessesCorrectServices()
    {
        // Arrange
        SetupMockServices();
        const string inputMessage = "Service Test";
        
        // Act
        var result = await EchoTool.Echo(MockServer.Object, inputMessage);
        
        // Assert
        result.Should().NotBeNull();
        
        // Verify that the correct services were accessed
        MockServer.Verify(s => s.Services, Times.Once);
        MockHttpContextAccessor.Verify(h => h.GetTenantRepositoryAsync(), Times.Once);
        MockTenantRepository.Verify(tr => tr.TenantId, Times.Once);
    }
    
    [Theory]
    [InlineData("simple")]
    [InlineData("123")]
    [InlineData("test-message")]
    [InlineData("Multiple Words Here")]
    [InlineData("With.Dots.And-Dashes")]
    public async Task Echo_WithVariousMessages_ReturnsCorrectFormat(string inputMessage)
    {
        // Arrange
        SetupMockServices();
        const string expectedTenantId = "test-tenant";
        
        // Act
        var result = await EchoTool.Echo(MockServer.Object, inputMessage);
        
        // Assert
        result.Should().NotBeNull();
        result.Should().Be($"hello {inputMessage}, from tenant {expectedTenantId}");
    }
}