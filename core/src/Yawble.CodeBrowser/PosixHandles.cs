using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Yawble.CodeBrowser;

/// <summary>
/// The POSIX side of SafeTree's handle checks (macOS and Linux, x64 and arm64): open without following a link, the
/// final path of an open descriptor, and what <c>fstat</c> says about it (type, link count, device and inode).
/// Anything not known (another OS or CPU, a call that fails) comes back as null / an invalid handle, which SafeTree
/// refuses, the same as on Windows.
/// </summary>
internal static class PosixHandles
{
    /// <summary>Whether this OS and CPU are ones the calls and the <c>struct stat</c> layouts below cover.</summary>
    [SupportedOSPlatformGuard("linux")]
    [SupportedOSPlatformGuard("macos")]
    public static bool Supported =>
        (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        && RuntimeInformation.ProcessArchitecture is Architecture.X64 or Architecture.Arm64;

    /// <summary>What fstat says about an open descriptor (only the fields the checks use).</summary>
    public readonly record struct Stat(ulong Device, ulong Inode, uint Mode, ulong Links)
    {
        public bool IsRegular => (Mode & S_IFMT) == S_IFREG;
        public bool IsDirectory => (Mode & S_IFMT) == S_IFDIR;
    }

    private const uint S_IFMT = 0xF000, S_IFREG = 0x8000, S_IFDIR = 0x4000;

    // open(2) flags. Linux's O_DIRECTORY / O_NOFOLLOW differ between x64 and arm64; macOS's are the same on both.
    private const int O_RDONLY = 0;
    private static int O_NOFOLLOW => OperatingSystem.IsMacOS() ? 0x100 : IsArm64 ? 0x8000 : 0x20000;
    private static int O_DIRECTORY => OperatingSystem.IsMacOS() ? 0x100000 : IsArm64 ? 0x4000 : 0x10000;
    private static int O_CLOEXEC => OperatingSystem.IsMacOS() ? 0x1000000 : 0x80000;
    private static int O_NONBLOCK => OperatingSystem.IsMacOS() ? 0x4 : 0x800;
    private static bool IsArm64 => RuntimeInformation.ProcessArchitecture == Architecture.Arm64;

    /// <summary>
    /// Opens a file for reading, never through a link: <c>open(path, O_RDONLY|O_NOFOLLOW|O_CLOEXEC|O_NONBLOCK)</c>
    /// (a link as the last name fails with ELOOP; O_NONBLOCK so a FIFO swapped in does not hang the open - it is then
    /// refused as not a regular file). Throws IOException when it cannot be opened.
    /// </summary>
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    public static FileStream OpenFileNoFollow(string path)
    {
        var handle = Open(path, O_RDONLY | O_NOFOLLOW | O_CLOEXEC | O_NONBLOCK);
        if (handle.IsInvalid)
        {
            var errno = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw new IOException(errno == Eloop ? "it is a link" : $"it cannot be opened (errno {errno})");
        }
        try
        {
            return new FileStream(handle, FileAccess.Read, bufferSize: 4096);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    /// <summary>Opens a folder, never through a link: <c>open(path, O_RDONLY|O_DIRECTORY|O_NOFOLLOW|O_CLOEXEC)</c>. Invalid when it cannot.</summary>
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    public static SafeFileHandle OpenFolderNoFollow(string path) => Open(path, O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC);

    private static int Eloop => OperatingSystem.IsMacOS() ? 62 : 40;

    private static SafeFileHandle Open(string path, int flags)
    {
        if (!Supported) return new SafeFileHandle(-1, ownsHandle: false);
        var fd = open(path, flags);
        return new SafeFileHandle(fd, ownsHandle: fd >= 0);
    }

    /// <summary>
    /// The path the open descriptor names now. macOS: <c>fcntl(fd, F_GETPATH, buf[MAXPATHLEN])</c>; Linux:
    /// <c>readlink("/proc/self/fd/N")</c>. Null when not known (or, on Linux, when the entry was deleted).
    /// </summary>
    public static string? FinalPath(SafeFileHandle handle)
    {
        if (!Supported || handle.IsInvalid || handle.IsClosed) return null;
        var added = false;
        try
        {
            handle.DangerousAddRef(ref added);
            var fd = (int)handle.DangerousGetHandle();
            if (OperatingSystem.IsMacOS())
            {
                var buf = new byte[MaxPathLen];
                var r = IsArm64 ? fcntl_arm64(fd, F_GETPATH, 0, 0, 0, 0, 0, 0, buf) : fcntl_x64(fd, F_GETPATH, buf);
                if (r == -1) return null;
                var n = Array.IndexOf(buf, (byte)0);
                return n <= 0 ? null : Encoding.UTF8.GetString(buf, 0, n);
            }
            var link = new byte[4096];
            var len = readlink("/proc/self/fd/" + fd.ToString(System.Globalization.CultureInfo.InvariantCulture), link, link.Length);
            if (len <= 0 || len >= link.Length) return null;
            var path = Encoding.UTF8.GetString(link, 0, (int)len);
            // An entry deleted since it was opened reads "<path> (deleted)"; a pipe or socket "pipe:[N]": neither is a path.
            if (!path.StartsWith('/') || path.EndsWith(" (deleted)", StringComparison.Ordinal)) return null;
            return path;
        }
        catch (Exception e) when (e is EntryPointNotFoundException or DllNotFoundException or ObjectDisposedException)
        {
            return null;
        }
        finally
        {
            if (added) handle.DangerousRelease();
        }
    }

    /// <summary>
    /// fstat of an open descriptor, read at the offsets of each covered <c>struct stat</c> layout:
    /// <list type="bullet">
    /// <item>Linux x64 (glibc and musl, = the kernel's): st_dev u64 @0, st_ino u64 @8, st_nlink u64 @16, st_mode u32 @24;</item>
    /// <item>Linux arm64 (asm-generic): st_dev u64 @0, st_ino u64 @8, st_mode u32 @16, st_nlink u32 @20;</item>
    /// <item>macOS x64 and arm64 (64-bit inodes; x64 calls fstat$INODE64): st_dev i32 @0, st_mode u16 @4, st_nlink u16 @6, st_ino u64 @8.</item>
    /// </list>
    /// glibc before 2.33 has no fstat symbol: __fxstat(_STAT_VER, ...) is called instead (1 on x64, 0 on arm64).
    /// </summary>
    public static Stat? FStat(SafeFileHandle handle)
    {
        if (!Supported || handle.IsInvalid || handle.IsClosed) return null;
        var added = false;
        try
        {
            handle.DangerousAddRef(ref added);
            var fd = (int)handle.DangerousGetHandle();
            var buf = new byte[256]; // larger than every covered struct stat (Linux x64 144, arm64 128, macOS 144)
            int r;
            if (OperatingSystem.IsMacOS())
                r = IsArm64 ? fstat(fd, buf) : fstat_inode64(fd, buf);
            else
            {
                try { r = fstat(fd, buf); }
                catch (EntryPointNotFoundException) { r = __fxstat(IsArm64 ? 0 : 1, fd, buf); }
            }
            if (r != 0) return null;
            if (OperatingSystem.IsMacOS())
                return new Stat((uint)BitConverter.ToInt32(buf, 0), BitConverter.ToUInt64(buf, 8), BitConverter.ToUInt16(buf, 4), BitConverter.ToUInt16(buf, 6));
            return IsArm64
                ? new Stat(BitConverter.ToUInt64(buf, 0), BitConverter.ToUInt64(buf, 8), BitConverter.ToUInt32(buf, 16), BitConverter.ToUInt32(buf, 20))
                : new Stat(BitConverter.ToUInt64(buf, 0), BitConverter.ToUInt64(buf, 8), BitConverter.ToUInt32(buf, 24), BitConverter.ToUInt64(buf, 16));
        }
        catch (Exception e) when (e is EntryPointNotFoundException or DllNotFoundException or ObjectDisposedException)
        {
            return null;
        }
        finally
        {
            if (added) handle.DangerousRelease();
        }
    }

    private const int F_GETPATH = 50, MaxPathLen = 1024;

    // open(2) is variadic (the mode); no O_CREAT is passed, so the two named arguments are all that is read.
    [DllImport("libc", SetLastError = true)]
    private static extern int open(string path, int flags);

    [DllImport("libc", SetLastError = true)]
    private static extern nint readlink(string path, byte[] buf, nint size);

    // fcntl(2) is variadic. On x64 a variadic argument is passed like a named one. On Apple arm64 every variadic
    // argument goes on the stack: six dummy register arguments fill x2..x7 so the buffer is the first stack slot.
    [DllImport("libc", EntryPoint = "fcntl", SetLastError = true)]
    private static extern int fcntl_x64(int fd, int cmd, byte[] buf);

    [DllImport("libc", EntryPoint = "fcntl", SetLastError = true)]
    private static extern int fcntl_arm64(int fd, int cmd, nint x2, nint x3, nint x4, nint x5, nint x6, nint x7, byte[] buf);

    [DllImport("libc", SetLastError = true)]
    private static extern int fstat(int fd, byte[] buf);

    [DllImport("libc", EntryPoint = "fstat$INODE64", SetLastError = true)]
    private static extern int fstat_inode64(int fd, byte[] buf);

    [DllImport("libc", SetLastError = true)]
    private static extern int __fxstat(int ver, int fd, byte[] buf);
}
