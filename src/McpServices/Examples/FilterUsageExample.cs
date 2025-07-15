using Meshmakers.Octo.Backend.McpServices.Extensions;
using Meshmakers.Octo.Backend.McpServices.Models.Filters;
using Meshmakers.Octo.Backend.McpServices.Utils;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;

namespace Meshmakers.Octo.Backend.McpServices.Examples;

/// <summary>
/// Example class demonstrating usage of the MCP filter system
/// </summary>
public static class FilterUsageExample
{
    /// <summary>
    /// Example demonstrating basic field filter creation and conversion
    /// </summary>
    public static void BasicFilterExample()
    {
        // Create MCP field filters
        var nameFilter = new FieldFilterDto
        {
            FieldPath = "name",
            Operator = FilterOperatorDto.Contains,
            Value = "John"
        };

        var ageFilter = new FieldFilterDto
        {
            FieldPath = "age",
            Operator = FilterOperatorDto.Between,
            Value = 18,
            SecondValue = 65
        };

        var statusFilter = new FieldFilterDto
        {
            FieldPath = "status",
            Operator = FilterOperatorDto.In,
            Value = new List<string> { "Active", "Pending" }
        };

        // Convert to database layer filters
        var dbNameFilter = nameFilter.ToFieldFilter();
        var dbAgeFilter = ageFilter.ToFieldFilter();
        var dbStatusFilter = statusFilter.ToFieldFilter();

        Console.WriteLine($"Name filter: {dbNameFilter}");
        Console.WriteLine($"Age filter: {dbAgeFilter}");
        Console.WriteLine($"Status filter: {dbStatusFilter}");
    }

    /// <summary>
    /// Example demonstrating entity filter with logical operators
    /// </summary>
    public static void EntityFilterExample()
    {
        // Create a complex entity filter using MCP DTOs
        var entityFilter = new EntityFilterDto
        {
            Operator = LogicalOperatorDto.And,
            Fields = new List<FieldFilterDto>
            {
                FilterExtensions.CreateEqualsFilter("department", "Engineering"),
                FilterExtensions.CreateBetweenFilter("salary", 50000, 150000)
            },
            NestedFilters = new List<EntityFilterDto>
            {
                new EntityFilterDto
                {
                    Operator = LogicalOperatorDto.Or,
                    Fields = new List<FieldFilterDto>
                    {
                        FilterExtensions.CreateContainsFilter("skills", "C#"),
                        FilterExtensions.CreateContainsFilter("skills", "MongoDB"),
                        FilterExtensions.CreateEqualsFilter("experience", "Senior")
                    }
                }
            }
        };

        // Convert to database layer
        var dbEntityFilter = entityFilter.ToEntityFilter();

        Console.WriteLine($"Complex entity filter: {dbEntityFilter}");
        Console.WriteLine($"Has conditions: {entityFilter.HasConditions()}");
    }

    /// <summary>
    /// Example demonstrating fluent filter building
    /// </summary>
    public static void FluentFilterExample()
    {
        // Create an entity filter using fluent syntax
        var entityFilter = new EntityFilterDto { Operator = LogicalOperatorDto.And }
            .AddField(FilterExtensions.CreateEqualsFilter("company", "Meshmakers"))
            .AddField(FilterExtensions.CreateNullFilter("deletedAt", isNull: true))
            .AddField(FilterExtensions.CreateContainsFilter("email", "@meshmakers.com"))
            .AddNestedFilter(new EntityFilterDto
            {
                Operator = LogicalOperatorDto.Or,
                Fields = new List<FieldFilterDto>
                {
                    FilterExtensions.CreateEqualsFilter("role", "Developer"),
                    FilterExtensions.CreateEqualsFilter("role", "Architect"),
                    FilterExtensions.CreateEqualsFilter("role", "Manager")
                }
            });

        // Convert to database layer
        var dbEntityFilter = entityFilter.ToEntityFilter();

        Console.WriteLine($"Fluent entity filter: {dbEntityFilter}");
    }

    /// <summary>
    /// Example demonstrating all supported operators
    /// </summary>
    public static void AllOperatorsExample()
    {
        var filters = new List<FieldFilterDto>
        {
            // Equality operators
            new() { FieldPath = "id", Operator = FilterOperatorDto.Equals, Value = 123 },
            new() { FieldPath = "status", Operator = FilterOperatorDto.NotEquals, Value = "Deleted" },

            // String operators
            new() { FieldPath = "name", Operator = FilterOperatorDto.Contains, Value = "test" },
            new() { FieldPath = "email", Operator = FilterOperatorDto.StartsWith, Value = "admin" },
            new() { FieldPath = "file", Operator = FilterOperatorDto.EndsWith, Value = ".pdf" },

            // Comparison operators
            new() { FieldPath = "age", Operator = FilterOperatorDto.GreaterThan, Value = 18 },
            new() { FieldPath = "score", Operator = FilterOperatorDto.GreaterThanOrEqual, Value = 80 },
            new() { FieldPath = "price", Operator = FilterOperatorDto.LessThan, Value = 100 },
            new() { FieldPath = "discount", Operator = FilterOperatorDto.LessThanOrEqual, Value = 50 },

            // Range operator
            new() { FieldPath = "date", Operator = FilterOperatorDto.Between, Value = DateTime.Today.AddDays(-30), SecondValue = DateTime.Today },

            // List operators
            new() { FieldPath = "category", Operator = FilterOperatorDto.In, Value = new List<string> { "A", "B", "C" } },
            new() { FieldPath = "type", Operator = FilterOperatorDto.NotIn, Value = new List<string> { "System", "Test" } },

            // Null operators
            new() { FieldPath = "deletedAt", Operator = FilterOperatorDto.IsNull },
            new() { FieldPath = "createdAt", Operator = FilterOperatorDto.IsNotNull },

            // Regex operator
            new() { FieldPath = "phone", Operator = FilterOperatorDto.Regex, Value = @"^\+\d{1,3}\s\d{3,14}$" }
        };

        Console.WriteLine("All supported operators:");
        foreach (var filter in filters)
        {
            var dbFilter = filter.ToFieldFilter();
            Console.WriteLine($"  {filter.Operator}: {dbFilter}");
        }
    }

    /// <summary>
    /// Example demonstrating conversion back to MCP DTOs
    /// </summary>
    public static void ConversionExample()
    {
        // Create database layer filters
        var dbFieldFilter = new FieldFilter("name", FieldFilterOperator.Contains, "test");
        var dbEntityFilter = new EntityFilter(LogicalOperator.And);
        dbEntityFilter.AddField(dbFieldFilter);
        dbEntityFilter.AddField(new FieldFilter("age", FieldFilterOperator.GreaterThan, 18));

        // Convert back to MCP DTOs
        var mcpFieldFilter = dbFieldFilter.ToMcpFieldFilter();
        var mcpEntityFilter = dbEntityFilter.ToMcpEntityFilter();

        Console.WriteLine($"Converted field filter: {mcpFieldFilter.FieldPath} {mcpFieldFilter.Operator} {mcpFieldFilter.Value}");
        Console.WriteLine($"Converted entity filter operator: {mcpEntityFilter.Operator}");
        Console.WriteLine($"Converted entity filter fields count: {mcpEntityFilter.Fields.Count}");
    }

    /// <summary>
    /// Example demonstrating builder pattern usage
    /// </summary>
    public static void BuilderPatternExample()
    {
        // Using the builder directly
        var mcpFilters = new List<FieldFilterDto>
        {
            new() { FieldPath = "department", Operator = FilterOperatorDto.Equals, Value = "IT" },
            new() { FieldPath = "salary", Operator = FilterOperatorDto.GreaterThanOrEqual, Value = 60000 }
        };

        // Convert using builder
        var entityFilter1 = McpFilterBuilder.CreateEntityFilterFromFields(mcpFilters);
        
        // Convert using extension method
        var entityFilter2 = mcpFilters.ToEntityFilter(LogicalOperatorDto.And);

        Console.WriteLine($"Builder result: {entityFilter1}");
        Console.WriteLine($"Extension result: {entityFilter2}");
        Console.WriteLine($"Results are equivalent: {entityFilter1.ToString() == entityFilter2.ToString()}");
    }

    /// <summary>
    /// Runs all examples
    /// </summary>
    public static void RunAllExamples()
    {
        Console.WriteLine("=== MCP Filter System Examples ===\n");

        Console.WriteLine("1. Basic Filter Example:");
        BasicFilterExample();
        Console.WriteLine();

        Console.WriteLine("2. Entity Filter Example:");
        EntityFilterExample();
        Console.WriteLine();

        Console.WriteLine("3. Fluent Filter Example:");
        FluentFilterExample();
        Console.WriteLine();

        Console.WriteLine("4. All Operators Example:");
        AllOperatorsExample();
        Console.WriteLine();

        Console.WriteLine("5. Conversion Example:");
        ConversionExample();
        Console.WriteLine();

        Console.WriteLine("6. Builder Pattern Example:");
        BuilderPatternExample();
        Console.WriteLine();

        Console.WriteLine("=== Examples Complete ===");
    }
}