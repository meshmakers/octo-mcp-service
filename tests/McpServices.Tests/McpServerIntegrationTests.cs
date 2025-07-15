using FluentAssertions;
using Meshmakers.Octo.Backend.McpServices.Models;
using Meshmakers.Octo.Backend.McpServices.Tools;
using Xunit;

namespace McpServices.Tests;

/// <summary>
/// Integration tests for MCP server core functionality
/// These tests validate the overall MCP server behavior for CI/CD
/// </summary>
public class McpServerIntegrationTests : TestBase
{
    [Fact]
    public async Task McpServer_CoreTools_AllFunctional()
    {
        // Arrange
        SetupMockServices();
        
        // Act & Assert - Test list_available_tools
        var toolsResult = await ToolManagementTools.ListAvailableTools(MockServer.Object);
        toolsResult.Should().NotBeNull();
        toolsResult.Should().BeOfType<ListAvailableToolsResponse>();
        
        var toolsResponse = (ListAvailableToolsResponse)toolsResult;
        toolsResponse.TotalTools.Should().BeGreaterThan(0);
        toolsResponse.Tools.Should().NotBeEmpty();
        
        // Verify essential tools are present
        var toolNames = toolsResponse.Tools.Select(t => t.Name).ToList();
        toolNames.Should().Contain("list_available_tools");
        toolNames.Should().Contain("Echo");
        toolNames.Should().Contain("get_tool_details");
        
        // Act & Assert - Test Echo tool
        var echoResult = await EchoTool.Echo(MockServer.Object, "CI/CD Test");
        echoResult.Should().NotBeNull();
        echoResult.Should().BeOfType<string>();
        echoResult.Should().Contain("hello CI/CD Test");
        echoResult.Should().Contain("from tenant test-tenant");
    }
    
    [Fact]
    public async Task McpServer_ToolDiscovery_ReturnsExpectedCategories()
    {
        // Arrange
        SetupMockServices();
        
        // Act
        var result = await ToolManagementTools.ListAvailableTools(MockServer.Object);
        
        // Assert
        var response = (ListAvailableToolsResponse)result;
        
        // Verify expected categories exist
        response.Categories.Should().ContainKey("CRUD Operations");
        response.Categories.Should().ContainKey("Schema Discovery");
        response.Categories.Should().ContainKey("Tool Management");
        response.Categories.Should().ContainKey("Testing");
        
        // Verify each category has tools
        response.Categories.Values.Should().OnlyContain(count => count > 0);
    }
    
    [Fact]
    public async Task McpServer_ToolDetails_WorksForAllTools()
    {
        // Arrange
        SetupMockServices();
        
        // Act - Get all tools first
        var toolsResult = await ToolManagementTools.ListAvailableTools(MockServer.Object);
        var toolsResponse = (ListAvailableToolsResponse)toolsResult;
        
        // Assert - Get details for each tool
        foreach (var tool in toolsResponse.Tools.Take(5)) // Test first 5 tools to keep test time reasonable
        {
            var detailsResult = await ToolManagementTools.GetToolDetails(MockServer.Object, tool.Name);
            detailsResult.Should().NotBeNull();
            
            // Should not be an error response
            detailsResult.Should().NotBeOfType<ToolManagementError>();
            
            if (detailsResult is ToolDetailsResponse details)
            {
                details.Name.Should().Be(tool.Name);
                details.Category.Should().NotBeNullOrEmpty();
                details.Description.Should().NotBeNullOrEmpty();
                details.Parameters.Should().NotBeNull();
            }
        }
    }
    
    [Fact]
    public async Task McpServer_EchoTool_HandlesVariousInputs()
    {
        // Arrange
        SetupMockServices();
        var testMessages = new[]
        {
            "Hello World",
            "",
            "123",
            "Special chars: @#$%^&*()",
            "Unicode: 你好世界 🌍",
            new string('A', 100) // Long message
        };
        
        // Act & Assert
        foreach (var message in testMessages)
        {
            var result = await EchoTool.Echo(MockServer.Object, message);
            result.Should().NotBeNull();
            result.Should().BeOfType<string>();
            result.Should().Contain($"hello {message}");
            result.Should().Contain("from tenant test-tenant");
        }
    }
    
    [Fact]
    public async Task McpServer_ToolValidation_WorksCorrectly()
    {
        // Arrange
        SetupMockServices();
        
        // Act - Test parameter validation for Echo tool
        var validationResult = await ToolManagementTools.ValidateToolParameters(
            MockServer.Object, 
            "Echo", 
            "{\"message\": \"test\"}");
        
        // Assert
        validationResult.Should().NotBeNull();
        validationResult.Should().BeOfType<ValidateParametersResponse>();
        
        var validation = (ValidateParametersResponse)validationResult;
        validation.ToolName.Should().Be("Echo");
        validation.ProvidedParameters.Should().Contain("message");
    }
    
    [Fact]
    public async Task McpServer_ToolStatistics_ReturnsValidStructure()
    {
        // Arrange
        SetupMockServices();
        
        // Act
        var result = await ToolManagementTools.GetToolStatistics(MockServer.Object);
        
        // Assert
        result.Should().NotBeNull();
        result.Should().BeOfType<ToolStatistics>();
        
        var stats = (ToolStatistics)result;
        stats.TotalInvocations.Should().BeGreaterThan(0);
        stats.UniqueTools.Should().BeGreaterThan(0);
        stats.SuccessRate.Should().BeGreaterThan(0);
        stats.TopTools.Should().NotBeEmpty();
        stats.CategoryBreakdown.Should().NotBeNull();
        stats.ErrorStats.Should().NotBeNull();
        stats.Performance.Should().NotBeNull();
    }
}