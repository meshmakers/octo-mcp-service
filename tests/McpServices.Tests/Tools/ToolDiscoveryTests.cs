using System.Reflection;
using FluentAssertions;
using Meshmakers.Octo.Backend.McpServices.Models;
using Meshmakers.Octo.Backend.McpServices.Tools;
using ModelContextProtocol.Server;
using Xunit;

namespace McpServices.Tests.Tools;

/// <summary>
/// Unit tests for tool discovery functionality that don't depend on external services
/// </summary>
public class ToolDiscoveryTests
{
    [Fact]
    public void ToolManagementTools_HasCorrectToolAttributes()
    {
        // Arrange
        var toolType = typeof(ToolManagementTools);
        
        // Act
        var toolTypeAttribute = toolType.GetCustomAttribute<McpServerToolTypeAttribute>();
        var methods = toolType.GetMethods(BindingFlags.Public | BindingFlags.Static);
        var toolMethods = methods.Where(m => m.GetCustomAttribute<McpServerToolAttribute>() != null).ToList();
        
        // Assert
        toolTypeAttribute.Should().NotBeNull("ToolManagementTools should have McpServerToolType attribute");
        toolMethods.Should().NotBeEmpty("ToolManagementTools should have at least one tool method");
        
        // Verify list_available_tools method exists
        var listToolsMethod = toolMethods.FirstOrDefault(m => 
            m.GetCustomAttribute<McpServerToolAttribute>()?.Name == "list_available_tools");
        listToolsMethod.Should().NotBeNull("list_available_tools method should exist");
    }
    
    [Fact]
    public void EchoTool_HasCorrectToolAttributes()
    {
        // Arrange
        var toolType = typeof(EchoTool);
        
        // Act
        var toolTypeAttribute = toolType.GetCustomAttribute<McpServerToolTypeAttribute>();
        var methods = toolType.GetMethods(BindingFlags.Public | BindingFlags.Static);
        var toolMethods = methods.Where(m => m.GetCustomAttribute<McpServerToolAttribute>() != null).ToList();
        
        // Assert
        toolTypeAttribute.Should().NotBeNull("EchoTool should have McpServerToolType attribute");
        toolMethods.Should().NotBeEmpty("EchoTool should have at least one tool method");
        
        // Verify Echo method exists
        var echoMethod = toolMethods.FirstOrDefault(m => 
            m.GetCustomAttribute<McpServerToolAttribute>()?.Name == "Echo");
        echoMethod.Should().NotBeNull("Echo method should exist");
        
        // Verify method signature
        var parameters = echoMethod!.GetParameters();
        parameters.Should().HaveCount(2, "Echo method should have 2 parameters");
        parameters[0].ParameterType.Should().Be(typeof(McpServer), "First parameter should be IMcpServer");
        parameters[1].ParameterType.Should().Be(typeof(string), "Second parameter should be string");
    }
    
    [Fact]
    public void SchemaDiscoveryTools_HasCorrectToolAttributes()
    {
        // Arrange
        var toolType = typeof(SchemaDiscoveryTools);
        
        // Act
        var toolTypeAttribute = toolType.GetCustomAttribute<McpServerToolTypeAttribute>();
        var methods = toolType.GetMethods(BindingFlags.Public | BindingFlags.Static);
        var toolMethods = methods.Where(m => m.GetCustomAttribute<McpServerToolAttribute>() != null).ToList();
        
        // Assert
        toolTypeAttribute.Should().NotBeNull("SchemaDiscoveryTools should have McpServerToolType attribute");
        toolMethods.Should().NotBeEmpty("SchemaDiscoveryTools should have at least one tool method");
        
        // Verify expected methods exist
        var expectedMethods = new[] { "get_available_models", "get_available_types", "get_type_schema", "search_types" };
        foreach (var expectedMethod in expectedMethods)
        {
            var method = toolMethods.FirstOrDefault(m => 
                m.GetCustomAttribute<McpServerToolAttribute>()?.Name == expectedMethod);
            method.Should().NotBeNull($"{expectedMethod} method should exist");
        }
    }
    
    [Fact]
    public void DynamicCrudTools_HasCorrectToolAttributes()
    {
        // Arrange
        var toolType = typeof(RuntimeEntityCrudTools);
        
        // Act
        var toolTypeAttribute = toolType.GetCustomAttribute<McpServerToolTypeAttribute>();
        var methods = toolType.GetMethods(BindingFlags.Public | BindingFlags.Static);
        var toolMethods = methods.Where(m => m.GetCustomAttribute<McpServerToolAttribute>() != null).ToList();
        
        // Assert
        toolTypeAttribute.Should().NotBeNull("DynamicCrudTools should have McpServerToolType attribute");
        toolMethods.Should().NotBeEmpty("DynamicCrudTools should have at least one tool method");
        
        // Verify expected CRUD methods exist
        var expectedMethods = new[] { "query_entities", "query_entities_simple", "get_entity_by_id", "create_entity", "update_entity", "delete_entity" };
        foreach (var expectedMethod in expectedMethods)
        {
            var method = toolMethods.FirstOrDefault(m => 
                m.GetCustomAttribute<McpServerToolAttribute>()?.Name == expectedMethod);
            method.Should().NotBeNull($"{expectedMethod} method should exist");
        }
    }
    
    [Fact]
    public void AllToolClasses_AreProperlyDecorated()
    {
        // Arrange
        var assembly = typeof(ToolManagementTools).Assembly;
        
        // Act
        var toolTypes = assembly.GetTypes()
            .Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() != null)
            .ToList();
        
        // Assert
        toolTypes.Should().NotBeEmpty("Assembly should contain tool types");
        
        // Verify each tool type has at least one tool method
        foreach (var toolType in toolTypes)
        {
            var toolMethods = toolType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(m => m.GetCustomAttribute<McpServerToolAttribute>() != null)
                .ToList();
            
            toolMethods.Should().NotBeEmpty($"{toolType.Name} should have at least one tool method");
            
            // Verify each tool method has proper signature
            foreach (var method in toolMethods)
            {
                var parameters = method.GetParameters();
                parameters.Should().NotBeEmpty($"{method.Name} should have parameters");
                parameters[0].ParameterType.Should().Be(typeof(McpServer), 
                    $"First parameter of {method.Name} should be IMcpServer");
            }
        }
    }
    
    [Fact]
    public void ToolNames_AreUnique()
    {
        // Arrange
        var assembly = typeof(ToolManagementTools).Assembly;
        
        // Act
        var toolNames = assembly.GetTypes()
            .Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() != null)
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(m => m.GetCustomAttribute<McpServerToolAttribute>() != null)
                .Select(m => m.GetCustomAttribute<McpServerToolAttribute>()!.Name))
            .Where(name => !string.IsNullOrEmpty(name))
            .ToList();
        
        // Assert
        toolNames.Should().NotBeEmpty("Should have tool names");
        toolNames.Should().OnlyHaveUniqueItems("All tool names should be unique");
        
        // Verify expected core tools are present
        toolNames.Should().Contain("list_available_tools");
        toolNames.Should().Contain("Echo");
        toolNames.Should().Contain("query_entities");
        toolNames.Should().Contain("get_available_types");
    }
    
    [Theory]
    [InlineData("list_available_tools", typeof(Task<ListAvailableToolsResponse>))]
    [InlineData("get_tool_details", typeof(Task<ToolDetailsResponse>))]
    [InlineData("get_tool_statistics", typeof(Task<ToolStatistics>))]
    [InlineData("validate_tool_parameters", typeof(Task<ValidateParametersResponse>))]
    [InlineData("Echo", typeof(Task<string>))]
    [InlineData("query_entities", typeof(Task<QueryEntitiesResponse>))]
    [InlineData("query_entities_simple", typeof(Task<QueryEntitiesResponse>))]
    [InlineData("get_entity_by_id", typeof(Task<GetEntityResponse>))]
    [InlineData("create_entity", typeof(Task<CreateEntityResponse>))]
    [InlineData("update_entity", typeof(Task<UpdateEntityResponse>))]
    [InlineData("delete_entity", typeof(Task<DeleteEntityResponse>))]
    [InlineData("navigate_associations", typeof(Task<NavigateAssociationsResponse>))]
    public void CoreTool_ExistsAndHasCorrectSignature(string toolName, Type expectedReturnType)
    {
        // Arrange
        var assembly = typeof(ToolManagementTools).Assembly;
        
        // Act
        var toolMethod = assembly.GetTypes()
            .Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() != null)
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static))
            .FirstOrDefault(m => m.GetCustomAttribute<McpServerToolAttribute>()?.Name == toolName);
        
        // Assert
        toolMethod.Should().NotBeNull($"Tool '{toolName}' should exist");
        
        var parameters = toolMethod!.GetParameters();
        parameters.Should().NotBeEmpty($"Tool '{toolName}' should have parameters");
        parameters[0].ParameterType.Should().Be(typeof(McpServer), 
            $"First parameter of '{toolName}' should be IMcpServer");
        
        // Verify return type is Task<T>
        toolMethod.ReturnType.Should().BeAssignableTo(typeof(Task), 
            $"Tool '{toolName}' should return Task<T>");
        
        // Verify specific return type
        toolMethod.ReturnType.Should().Be(expectedReturnType, 
            $"Tool '{toolName}' should return {expectedReturnType.Name}");
    }
}