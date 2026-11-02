using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace McpServices.Tests.Architecture;

/// <summary>
///     AB#8043: every entity a tool returns is mapped by <c>AccessAwareRtEntityToDtoMapper</c>, which removes CK v2
///     <c>Hidden</c> attributes (password hashes, security stamps, authenticator tokens). The SDK
///     <c>RtEntityToDtoMapper</c> does not know <c>access</c>, so registering or constructing it anywhere else would put
///     Hidden values back on the wire. The tool tests use the same mapper as production (TestBase), so this test pins the
///     composition root and the source tree.
/// </summary>
public class HiddenAttributeOutputArchitectureTests
{
    private static readonly string SourceRoot = FindSourceRoot();

    [Fact]
    public void Program_RegistersTheAccessAwareMapper_AsTheOnlyRtEntityToDtoMapper()
    {
        var program = File.ReadAllText(Path.Combine(SourceRoot, "Program.cs"));

        program.Should().Contain("AddTransient<IRtEntityToDtoMapper, AccessAwareRtEntityToDtoMapper>()");
        Regex.IsMatch(program, @"IRtEntityToDtoMapper\s*,\s*RtEntityToDtoMapper\s*>").Should().BeFalse();
        // No concrete registration of the SDK mapper and no factory registration of the interface either.
        Regex.IsMatch(program, @"\bRtEntityToDtoMapper\b").Should().BeFalse();
        Regex.IsMatch(program, @"IRtEntityToDtoMapper\s*>").Should().BeFalse();
    }

    [Fact]
    public void OnlyTheAccessAwareMapper_ConstructsTheSdkMapper()
    {
        var offenders = Directory.EnumerateFiles(SourceRoot, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") &&
                        !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Where(f => Path.GetFileName(f) != "AccessAwareRtEntityToDtoMapper.cs")
            .Where(f => Regex.IsMatch(File.ReadAllText(f), @"new\s+RtEntityToDtoMapper\s*\("))
            .Select(Path.GetFileName)
            .ToList();

        offenders.Should().BeEmpty("the SDK mapper returns Hidden attribute values (AB#8043)");
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
