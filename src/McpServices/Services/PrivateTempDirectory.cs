using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Meshmakers.Octo.Backend.McpServices.Services;

/// <summary>
///     AB#8568: a fresh directory in the temp path that only the service user can list, open or write. The name is
///     unpredictable and the directory is created atomically (<see cref="Directory.CreateTempSubdirectory" />,
///     <c>mkdtemp</c> on Unix, mode 0700), so a directory another user prepared in a shared <c>/tmp</c> is never reused.
///     On Unix the result is verified after creation: a real directory (no symbolic link), owned by the effective user of
///     the process and without any group or other permission bit. Anything else throws, fail closed.
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

    /// <summary>Throws <see cref="UnauthorizedAccessException" /> unless the directory is private to the current user.</summary>
    public static void EnsurePrivate(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return; // the temp path is the user's own profile directory there
        }

        EnsurePrivate(path, NativeMethods.geteuid());
    }

    /// <summary>As <see cref="EnsurePrivate(string)" />, against the given owner; Unix only, tests call it directly.</summary>
    [UnsupportedOSPlatform("windows")]
    internal static void EnsurePrivate(string path, uint expectedOwner)
    {
        if (!TryGetOwner(path, out var owner, out var isDirectory) || !isDirectory)
        {
            throw new UnauthorizedAccessException("The snapshot directory could not be verified.");
        }

        if (owner != expectedOwner)
        {
            throw new UnauthorizedAccessException("The snapshot directory is not owned by the service user.");
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

    /// <summary>
    ///     The owner uid of <paramref name="path" /> by <c>lstat</c> (a symbolic link reports itself, not its target).
    ///     The <c>struct stat</c> layout differs per platform; the permission bits read at the platform's offset are
    ///     compared with <see cref="File.GetUnixFileMode(string)" />, so an unknown or wrong layout returns <c>false</c>
    ///     instead of a wrong owner.
    /// </summary>
    [UnsupportedOSPlatform("windows")]
    internal static bool TryGetOwner(string path, out uint owner, out bool isDirectory)
    {
        owner = 0;
        isDirectory = false;
        if (!TryGetStatLayout(out var modeOffset, out var modeIs16Bit, out var uidOffset))
        {
            return false;
        }

        var buffer = new byte[512];
        int result;
        try
        {
            result = OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.X64
                ? NativeMethods.lstat_inode64(path, buffer)
                : NativeMethods.lstat(path, buffer);
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            return false;
        }

        if (result != 0)
        {
            return false;
        }

        uint mode = modeIs16Bit
            ? BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(modeOffset))
            : BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(modeOffset));
        const uint typeMask = 0xF000, directoryType = 0x4000, symbolicLinkType = 0xA000;
        if ((mode & typeMask) == symbolicLinkType ||
            (mode & 0xFFF) != (uint)File.GetUnixFileMode(path))
        {
            return false;
        }

        owner = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(uidOffset));
        isDirectory = (mode & typeMask) == directoryType;
        return true;
    }

    private static bool TryGetStatLayout(out int modeOffset, out bool modeIs16Bit, out int uidOffset)
    {
        var architecture = RuntimeInformation.ProcessArchitecture;
        (modeOffset, modeIs16Bit, uidOffset) = (0, false, 0);
        if (OperatingSystem.IsLinux() && architecture == Architecture.X64)
        {
            // st_dev 8, st_ino 8, st_nlink 8, st_mode 4, st_uid 4
            (modeOffset, uidOffset) = (24, 28);
            return true;
        }

        if (OperatingSystem.IsLinux() && architecture == Architecture.Arm64)
        {
            // asm-generic: st_dev 8, st_ino 8, st_mode 4, st_nlink 4, st_uid 4
            (modeOffset, uidOffset) = (16, 24);
            return true;
        }

        if (OperatingSystem.IsMacOS() && architecture is Architecture.Arm64 or Architecture.X64)
        {
            // 64-bit inode layout: st_dev 4, st_mode 2, st_nlink 2, st_ino 8, st_uid 4
            (modeOffset, modeIs16Bit, uidOffset) = (4, true, 16);
            return true;
        }

        return false;
    }

    private static class NativeMethods
    {
        [DllImport("libc", EntryPoint = "geteuid")]
        public static extern uint geteuid();

        [DllImport("libc", EntryPoint = "lstat")]
        public static extern int lstat([MarshalAs(UnmanagedType.LPUTF8Str)] string path, byte[] buffer);

        [DllImport("libc", EntryPoint = "lstat$INODE64")]
        public static extern int lstat_inode64([MarshalAs(UnmanagedType.LPUTF8Str)] string path, byte[] buffer);
    }
}
