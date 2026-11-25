using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace McpServices.Tests.Architecture;

public class PipelineExecutionArchitectureTests
{
    private static readonly string SourceRoot = FindSourceRoot();

    [Fact]
    public void NoSourceFile_RegistersOrInjectsARoutedExecutePipelineRequestClient()
    {
        var routedExecuteClientPattern = new Regex(@"RoutedCommandClient\s*<\s*ExecutePipelineRequest\s*>");

        var offendingFiles = Directory.EnumerateFiles(SourceRoot, "*.cs", SearchOption.AllDirectories)
            .Where(filePath => !IsBuildOutput(filePath))
            .Where(filePath => routedExecuteClientPattern.IsMatch(File.ReadAllText(filePath)))
            .Select(Path.GetFileName)
            .ToList();

        offendingFiles.Should().BeEmpty(
            "only the communication controller publishes ExecutePipelineRequest; the ExecutePipeline tool uses the controller REST route");
    }

    private static bool IsBuildOutput(string filePath)
    {
        var separator = Path.DirectorySeparatorChar;
        return filePath.Contains($"{separator}obj{separator}") || filePath.Contains($"{separator}bin{separator}");
    }

    private static string FindSourceRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            var candidate = Path.Combine(directory.FullName, "src", "McpServices");
            if (File.Exists(Path.Combine(candidate, "Program.cs")))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("src/McpServices not found above the test output directory.");
    }
}
