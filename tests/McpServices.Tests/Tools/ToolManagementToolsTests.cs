using FluentAssertions;
using Meshmakers.Octo.Backend.McpServices.Models;
using Meshmakers.Octo.Backend.McpServices.Tools;
using Xunit;

namespace McpServices.Tests.Tools;

public class ToolManagementToolsTests : TestBase
{
    [Fact]
    public async Task ListAvailableTools_WithoutFilter_ReturnsAllTools()
    {
        // Arrange
        SetupMockServices();
        
        // Act
        var result = await ToolManagementTools.ListAvailableTools(MockServer.Object);
        
        // Assert
        result.Should().NotBeNull();
        result.Should().BeOfType<ListAvailableToolsResponse>();
        
        var response = result;
        response.Should().NotBeNull();
        response.TotalTools.Should().BeGreaterThan(0);
        response.Tools.Should().NotBeEmpty();
        response.CategoryFilter.Should().BeNull();
        response.Categories.Should().NotBeEmpty();
        
        // Verify specific expected tools exist
        var toolNames = response.Tools.Select(t => t.Name).ToList();
        toolNames.Should().Contain("list_available_tools");
        toolNames.Should().Contain("get_tool_details");
        toolNames.Should().Contain("Echo");
        toolNames.Should().Contain("query_entities");
        toolNames.Should().Contain("query_entities_simple");
        toolNames.Should().Contain("get_available_types");
        
        // Verify categories are properly assigned
        response.Categories.Should().ContainKey("Tool Management");
        response.Categories.Should().ContainKey("CRUD Operations");
        response.Categories.Should().ContainKey("Schema Discovery");
        response.Categories.Should().ContainKey("Testing");
        
        // Verify tool details are populated
        var listToolsInfo = response.Tools.FirstOrDefault(t => t.Name == "list_available_tools");
        listToolsInfo.Should().NotBeNull();
        listToolsInfo!.Category.Should().Be("Tool Management");
        listToolsInfo.Description.Should().NotBeNullOrEmpty();
        listToolsInfo.ClassName.Should().Be("ToolManagementTools");
        listToolsInfo.MethodName.Should().Be("ListAvailableTools");
        listToolsInfo.Parameters.Should().NotBeNull();
        listToolsInfo.Parameters.Should().Contain(p => p.Name == "category");
        
        // Verify parameter information
        var categoryParam = listToolsInfo.Parameters.First(p => p.Name == "category");
        categoryParam.IsOptional.Should().BeTrue();
        categoryParam.Type.Should().Be("string");
        categoryParam.DefaultValue.Should().BeNull();
    }
    
    [Fact]
    public async Task ListAvailableTools_WithCategoryFilter_ReturnsFilteredTools()
    {
        // Arrange
        SetupMockServices();
        const string categoryFilter = "Testing";
        
        // Act
        var result = await ToolManagementTools.ListAvailableTools(MockServer.Object, categoryFilter);
        
        // Assert
        result.Should().NotBeNull();
        result.Should().BeOfType<ListAvailableToolsResponse>();
        
        var response = (ListAvailableToolsResponse)result;
        response.CategoryFilter.Should().Be(categoryFilter);
        response.Tools.Should().NotBeEmpty();
        response.Tools.Should().OnlyContain(t => t.Category == categoryFilter);
        
        // Verify Echo tool is included in Testing category
        var echoTool = response.Tools.FirstOrDefault(t => t.Name == "Echo");
        echoTool.Should().NotBeNull();
        echoTool!.Category.Should().Be("Testing");
    }
    
    [Fact]
    public async Task ListAvailableTools_WithInvalidCategory_ReturnsEmptyList()
    {
        // Arrange
        SetupMockServices();
        const string invalidCategory = "NonExistentCategory";
        
        // Act
        var result = await ToolManagementTools.ListAvailableTools(MockServer.Object, invalidCategory);
        
        // Assert
        result.Should().NotBeNull();
        result.Should().BeOfType<ListAvailableToolsResponse>();
        
        var response = (ListAvailableToolsResponse)result;
        response.CategoryFilter.Should().Be(invalidCategory);
        response.Tools.Should().BeEmpty();
        response.TotalTools.Should().Be(0);
    }
    
    [Fact]
    public async Task ListAvailableTools_ToolsHaveCorrectStructure()
    {
        // Arrange
        SetupMockServices();
        
        // Act
        var result = await ToolManagementTools.ListAvailableTools(MockServer.Object);
        
        // Assert
        var response = (ListAvailableToolsResponse)result;
        
        foreach (var tool in response.Tools)
        {
            // Verify required fields
            tool.Name.Should().NotBeNullOrEmpty();
            tool.Category.Should().NotBeNullOrEmpty();
            tool.Description.Should().NotBeNullOrEmpty();
            tool.ClassName.Should().NotBeNullOrEmpty();
            tool.MethodName.Should().NotBeNullOrEmpty();
            tool.Parameters.Should().NotBeNull();
            tool.ParameterCount.Should().Be(tool.Parameters.Count);
            
            // Verify parameter structure
            foreach (var param in tool.Parameters)
            {
                param.Name.Should().NotBeNullOrEmpty();
                param.Type.Should().NotBeNullOrEmpty();
                // IsOptional and DefaultValue are optional checks
            }
        }
    }
    
    [Fact]
    public async Task ListAvailableTools_ReturnsToolsInCorrectOrder()
    {
        // Arrange
        SetupMockServices();
        
        // Act
        var result = await ToolManagementTools.ListAvailableTools(MockServer.Object);
        
        // Assert
        var response = (ListAvailableToolsResponse)result;
        
        // Verify tools are ordered by category then by name
        var orderedTools = response.Tools
            .OrderBy(t => t.Category)
            .ThenBy(t => t.Name)
            .ToList();
            
        response.Tools.Should().BeEquivalentTo(orderedTools, options => options.WithStrictOrdering());
    }
    
    [Fact]
    public async Task ListAvailableTools_CategoriesCountMatchesTools()
    {
        // Arrange
        SetupMockServices();
        
        // Act
        var result = await ToolManagementTools.ListAvailableTools(MockServer.Object);
        
        // Assert
        var response = (ListAvailableToolsResponse)result;
        
        // Verify category counts match actual tool counts
        var actualCategoryCounts = response.Tools
            .GroupBy(t => t.Category)
            .ToDictionary(g => g.Key, g => g.Count());
            
        response.Categories.Should().BeEquivalentTo(actualCategoryCounts);
    }
}