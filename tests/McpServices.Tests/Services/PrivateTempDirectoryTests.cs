using System.Runtime.Versioning;
using FluentAssertions;
using Meshmakers.Octo.Backend.McpServices.Services;
using Xunit;

namespace McpServices.Tests.Services;

/// <summary>
///     AB#8568: the import snapshot directory is fresh, unpredictable, owner-only and verified on Unix (owner, mode, no
///     symbolic link); anything else is refused.
/// </summary>
public class PrivateTempDirectoryTests
{
    private const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    [Fact]
    public void Create_ReturnsAFreshOwnerOnlyDirectoryWithAnUnpredictableName()
    {
        var first = PrivateTempDirectory.Create("ab8568-test-");
        var second = PrivateTempDirectory.Create("ab8568-test-");
        try
        {
            first.FullName.Should().NotBe(second.FullName);
            first.Name.Should().StartWith("ab8568-test-").And.NotBe("ab8568-test-");
            Path.GetDirectoryName(first.FullName.TrimEnd(Path.DirectorySeparatorChar))
                .Should().Be(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar));
            Directory.GetFileSystemEntries(first.FullName).Should().BeEmpty();
            if (!OperatingSystem.IsWindows())
            {
                File.GetUnixFileMode(first.FullName).Should().Be(OwnerOnly);
            }
        }
        finally
        {
            PrivateTempDirectory.TryDelete(first.FullName);
            PrivateTempDirectory.TryDelete(second.FullName);
        }
    }

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public void TryGetOwner_ReadsTheOwnerOfAFreshDirectory_AndOfTheRootDirectory()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var directory = PrivateTempDirectory.Create("ab8568-test-");
        try
        {
            // The platform's struct stat layout is known and self-checked against the managed mode.
            PrivateTempDirectory.TryGetOwner(directory.FullName, out var owner, out var isDirectory).Should().BeTrue();
            isDirectory.Should().BeTrue();
            PrivateTempDirectory.TryGetOwner("/", out var rootOwner, out _).Should().BeTrue();
            rootOwner.Should().Be(0u);
            var act = () => PrivateTempDirectory.EnsurePrivate(directory.FullName, owner);
            act.Should().NotThrow();
        }
        finally
        {
            PrivateTempDirectory.TryDelete(directory.FullName);
        }
    }

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public void EnsurePrivate_DirectoryOwnedBySomeoneElse_Throws()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var directory = PrivateTempDirectory.Create("ab8568-test-");
        try
        {
            PrivateTempDirectory.TryGetOwner(directory.FullName, out var owner, out _).Should().BeTrue();

            var act = () => PrivateTempDirectory.EnsurePrivate(directory.FullName, owner + 1);

            act.Should().Throw<UnauthorizedAccessException>();
        }
        finally
        {
            PrivateTempDirectory.TryDelete(directory.FullName);
        }
    }

    [Theory]
    [InlineData(UnixFileMode.GroupWrite)]
    [InlineData(UnixFileMode.OtherWrite)]
    [InlineData(UnixFileMode.GroupRead | UnixFileMode.GroupExecute)]
    [InlineData(UnixFileMode.OtherRead | UnixFileMode.OtherExecute)]
    public void EnsurePrivate_DirectoryOpenToGroupOrOthers_Throws(UnixFileMode extra)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var directory = PrivateTempDirectory.Create("ab8568-test-");
        try
        {
            File.SetUnixFileMode(directory.FullName, OwnerOnly | extra);

            var act = () => PrivateTempDirectory.EnsurePrivate(directory.FullName);

            act.Should().Throw<UnauthorizedAccessException>();
        }
        finally
        {
            PrivateTempDirectory.TryDelete(directory.FullName);
        }
    }

    [Fact]
    public void EnsurePrivate_SymbolicLinkToAPrivateDirectory_Throws()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var target = PrivateTempDirectory.Create("ab8568-test-");
        var link = Path.Combine(Path.GetTempPath(), $"ab8568-link-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateSymbolicLink(link, target.FullName);

            var act = () => PrivateTempDirectory.EnsurePrivate(link);

            act.Should().Throw<UnauthorizedAccessException>();
        }
        finally
        {
            File.Delete(link);
            PrivateTempDirectory.TryDelete(target.FullName);
        }
    }

    [Fact]
    public void EnsurePrivate_RegularFile_Throws()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var file = Path.Combine(Path.GetTempPath(), $"ab8568-file-{Guid.NewGuid():N}");
        File.WriteAllText(file, "x");
        try
        {
            File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);

            var act = () => PrivateTempDirectory.EnsurePrivate(file);

            act.Should().Throw<UnauthorizedAccessException>();
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void TryDelete_RemovesTheDirectoryWithItsContent_AndNeverThrows()
    {
        var directory = PrivateTempDirectory.Create("ab8568-test-");
        File.WriteAllText(Path.Combine(directory.FullName, "a.yaml"), "x");
        Directory.CreateDirectory(Path.Combine(directory.FullName, "sub"));

        PrivateTempDirectory.TryDelete(directory.FullName);
        PrivateTempDirectory.TryDelete(directory.FullName);

        Directory.Exists(directory.FullName).Should().BeFalse();
    }
}
