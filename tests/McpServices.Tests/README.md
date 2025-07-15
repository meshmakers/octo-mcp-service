# MCP Services Tests

This test suite validates the core functionality of the MCP (Model Context Protocol) server for CI/CD integration.

## Test Structure

### Core Test Classes

- **`TestBase`** - Base class providing common mock setup for all tests
- **`ToolManagementToolsTests`** - Tests for tool discovery and management functionality
- **`EchoToolTests`** - Tests for the Echo tool functionality
- **`McpServerIntegrationTests`** - Integration tests for overall MCP server behavior

### Test Categories

1. **Tool Discovery Tests**
   - `list_available_tools` functionality
   - Category filtering
   - Tool metadata validation

2. **Tool Execution Tests**
   - Echo tool with various inputs
   - Parameter validation
   - Error handling

3. **Integration Tests**
   - End-to-end workflow validation
   - Service interaction verification
   - CI/CD readiness checks

## Running Tests

### Local Development

```bash
# Run all tests
dotnet test

# Run specific test class
dotnet test --filter "ClassName~ToolManagementToolsTests"

# Run with verbose output
dotnet test --logger:console --verbosity normal

# Run tests with coverage (requires coverlet)
dotnet test --collect:"XPlat Code Coverage"
```

### CI/CD Pipeline

```bash
# Basic test execution for CI/CD
dotnet test --configuration Release --logger trx --results-directory TestResults

# With coverage for quality gates
dotnet test --configuration Release --collect:"XPlat Code Coverage" --results-directory TestResults
```

## Test Requirements

### Dependencies
- .NET 9.0 SDK
- xUnit test framework
- Moq for mocking
- FluentAssertions for assertions

### Mock Setup
Tests use mocked dependencies to ensure:
- Fast execution
- No external dependencies
- Consistent test environment
- Isolation from real services

### Key Test Scenarios

1. **Service Health**
   - Verify MCP server can start and list tools
   - Validate core tools are available
   - Check tool categories are properly assigned

2. **Tool Functionality**
   - Echo tool responds correctly to various inputs
   - Parameter validation works as expected
   - Error handling is appropriate

3. **Integration Readiness**
   - All expected tools are discoverable
   - Tool metadata is complete and accurate
   - Service interactions work correctly

## CI/CD Integration

These tests are designed to be run in CI/CD pipelines to ensure:

- **Quality Gates**: All tests must pass before deployment
- **Regression Prevention**: Catch breaking changes early
- **Documentation**: Verify tool descriptions and examples are accurate
- **Performance**: Tests complete quickly (< 30 seconds)

## Test Data

Tests use minimal mock data to focus on:
- Tool discovery and metadata
- Basic functionality validation
- Error handling scenarios
- Parameter validation

## Troubleshooting

### Common Issues

1. **Missing Dependencies**: Ensure all NuGet packages are restored
2. **Mock Setup**: Verify mock services are properly configured
3. **Test Isolation**: Each test should be independent and not affect others

### Debug Tips

- Use `dotnet test --logger:console --verbosity detailed` for detailed output
- Check mock setup in `TestBase` if services aren't working
- Verify test data matches expected format

## Adding New Tests

When adding new MCP tools:

1. Add tests to appropriate test class
2. Follow naming convention: `ToolName_Scenario_ExpectedResult`
3. Use appropriate test categories (Fact, Theory, InlineData)
4. Include both positive and negative test cases
5. Update integration tests if needed

## Test Coverage

The test suite covers:
- ✅ Tool discovery (`list_available_tools`)
- ✅ Basic tool execution (`Echo`)
- ✅ Parameter validation
- ✅ Error handling
- ✅ Integration scenarios
- ✅ Service interactions

Future additions should maintain this coverage level and add tests for new functionality.