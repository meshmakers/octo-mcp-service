#!/bin/bash

# MCP Services Test Runner
# This script runs the MCP server tests locally

set -e

echo "🔧 MCP Server Test Runner"
echo "=========================="

# Colors for output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
NC='\033[0m' # No Color

# Function to print colored output
print_status() {
    echo -e "${GREEN}[INFO]${NC} $1"
}

print_warning() {
    echo -e "${YELLOW}[WARN]${NC} $1"
}

print_error() {
    echo -e "${RED}[ERROR]${NC} $1"
}

# Check if .NET is available
if ! command -v dotnet &> /dev/null; then
    print_error "dotnet CLI is not installed or not in PATH"
    exit 1
fi

# Check .NET version
DOTNET_VERSION=$(dotnet --version)
print_status "Using .NET version: $DOTNET_VERSION"

# Change to the correct directory
cd "$(dirname "$0")"

# Test project path
TEST_PROJECT="tests/McpServices.Tests/McpServices.Tests.csproj"

if [ ! -f "$TEST_PROJECT" ]; then
    print_error "Test project not found at $TEST_PROJECT"
    exit 1
fi

# Parse command line arguments
RUN_COVERAGE=false
RUN_INTEGRATION_ONLY=false
VERBOSE=false
FILTER=""

while [[ $# -gt 0 ]]; do
    case $1 in
        --coverage)
            RUN_COVERAGE=true
            shift
            ;;
        --integration-only)
            RUN_INTEGRATION_ONLY=true
            shift
            ;;
        --verbose)
            VERBOSE=true
            shift
            ;;
        --filter)
            FILTER="$2"
            shift
            shift
            ;;
        --help|-h)
            echo "Usage: $0 [options]"
            echo "Options:"
            echo "  --coverage          Run tests with code coverage"
            echo "  --integration-only  Run only integration tests"
            echo "  --verbose           Enable verbose output"
            echo "  --filter FILTER     Filter tests by name/category"
            echo "  --help, -h          Show this help"
            exit 0
            ;;
        *)
            print_error "Unknown option: $1"
            exit 1
            ;;
    esac
done

# Step 1: Restore dependencies
print_status "Restoring dependencies..."
dotnet restore "$TEST_PROJECT"

# Step 2: Build the test project
print_status "Building test project..."
dotnet build "$TEST_PROJECT" --no-restore --configuration DebugL

# Step 3: Run tests
print_status "Running tests..."

TEST_CMD="dotnet test $TEST_PROJECT --no-build --configuration DebugL"

# Add coverage if requested
if [ "$RUN_COVERAGE" = true ]; then
    print_status "Running with code coverage..."
    TEST_CMD="$TEST_CMD --collect:\"XPlat Code Coverage\" --results-directory TestResults"
fi

# Add integration filter if requested
if [ "$RUN_INTEGRATION_ONLY" = true ]; then
    print_status "Running integration tests only..."
    TEST_CMD="$TEST_CMD --filter ClassName~IntegrationTests"
elif [ -n "$FILTER" ]; then
    print_status "Running tests with filter: $FILTER"
    TEST_CMD="$TEST_CMD --filter $FILTER"
fi

# Add verbosity if requested
if [ "$VERBOSE" = true ]; then
    TEST_CMD="$TEST_CMD --logger:console --verbosity normal"
else
    TEST_CMD="$TEST_CMD --logger:console --verbosity minimal"
fi

# Execute the test command
eval $TEST_CMD

TEST_EXIT_CODE=$?

# Step 4: Results
if [ $TEST_EXIT_CODE -eq 0 ]; then
    print_status "✅ All tests passed!"
    
    # Show coverage results if available
    if [ "$RUN_COVERAGE" = true ] && [ -d "TestResults" ]; then
        print_status "Coverage reports generated in TestResults/"
        find TestResults -name "*.xml" -type f | head -1 | xargs -I {} echo "  Coverage report: {}"
    fi
else
    print_error "❌ Tests failed with exit code $TEST_EXIT_CODE"
fi

# Step 5: Quick health check
print_status "Running quick health check..."
dotnet test "$TEST_PROJECT" --no-build --configuration DebugL --filter "FullyQualifiedName~ToolDiscoveryTests" --logger:console --verbosity quiet

if [ $? -eq 0 ]; then
    print_status "✅ Tool discovery health check passed"
else
    print_warning "⚠️  Tool discovery health check failed"
fi

# Cleanup
print_status "Cleaning up..."
if [ -d "TestResults" ] && [ "$RUN_COVERAGE" = false ]; then
    rm -rf TestResults
fi

print_status "Test run completed!"
exit $TEST_EXIT_CODE