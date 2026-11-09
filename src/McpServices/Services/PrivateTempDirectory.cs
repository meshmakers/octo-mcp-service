namespace Meshmakers.Octo.Backend.McpServices.Services;

/// <summary>
///     AB#8568: a fresh directory in the temp path that only the service user can list, open or write. The name is
///     unpredictable and the directory is created atomically (<see cref="Directory.CreateTempSubdirectory" />,
///     <c>mkdtemp</c> semantics on Unix: mode 0700, fails if the name exists), so it is by construction created and
///     owned by the current process user and a directory another user prepared in a shared <c>/tmp</c> is never reused.
///     On Unix the result is verified with managed APIs only: a real directory (no symbolic link) without any group or
///     other permission bit. Anything else throws, fail closed. No native ownership check (no <c>lstat</c> P/Invoke):
///     it would be redundant and depends on per-platform <c>struct stat</c> layouts and glibc versions.
/// </summary>
internal static class PrivateTempDirectory
{
    private const UnixFileMode GroupOrOther =
        UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
        UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

    /// <summary>Creates and verifies the directory; deletes it again and throws when the verification fails.</summary>
    public static DirectoryInfo Create(string prefix)
    {
        var directory = Directory.CreateTempSubdirectory(prefix);
        try
        {
            EnsurePrivate(directory.FullName);
            return directory;
        }
        catch
        {
            TryDelete(directory.FullName);
            throw;
        }
    }

    /// <summary>
    ///     Throws <see cref="UnauthorizedAccessException" /> unless <paramref name="path" /> is a real directory (not a
    ///     symbolic link) without group or other permission bits. No-op on Windows, where the temp path is the user's own
    ///     profile directory.
    /// </summary>
    public static void EnsurePrivate(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var info = new DirectoryInfo(path);
        if (!info.Exists || info.LinkTarget != null || (info.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new UnauthorizedAccessException("The snapshot directory could not be verified.");
        }

        if ((File.GetUnixFileMode(path) & GroupOrOther) != 0)
        {
            throw new UnauthorizedAccessException("The snapshot directory is open to other users.");
        }
    }

    /// <summary>Deletes the directory with everything in it; best effort, never throws.</summary>
    public static void TryDelete(string path)
    {
        try
        {
            Directory.Delete(path, true);
        }
        catch
        {
            // Best effort: the directory is private and holds nothing the caller did not upload.
        }
    }
}
