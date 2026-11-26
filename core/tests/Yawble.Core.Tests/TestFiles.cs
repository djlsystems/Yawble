using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Xunit;

namespace Yawble.Core.Tests;

/// <summary>File-system helpers that behave the same on Windows, macOS and Linux.</summary>
internal static class TestFiles
{
    /// <summary>
    /// Deletes a folder tree. Git marks its object and pack files read-only, and on Windows Directory.Delete refuses a
    /// read-only file (UnauthorizedAccessException), so every attribute is cleared first. Links are deleted, not followed.
    /// </summary>
    public static void DeleteTree(string path)
    {
        if (!Directory.Exists(path)) return;
        var root = new DirectoryInfo(path);
        if (root.LinkTarget is null)
        {
            foreach (var entry in root.EnumerateFileSystemInfos("*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 }))
            {
                if (entry.Attributes.HasFlag(FileAttributes.ReadOnly)) entry.Attributes &= ~FileAttributes.ReadOnly;
            }
        }
        Directory.Delete(path, recursive: true);
    }

    /// <summary>
    /// The number of hard links to a file, from the OS rather than from .NET (which has no API for it): GNU stat
    /// <c>-c %h</c> on Linux, BSD stat <c>-f %l</c> on macOS, GetFileInformationByHandle's nNumberOfLinks on Windows.
    /// A stat that fails (a wrong flag) fails the test instead of returning a parse of "".
    /// </summary>
    public static long LinkCount(string path)
    {
        if (OperatingSystem.IsWindows()) return WindowsLinkCount(path);
        string[] args = OperatingSystem.IsLinux() ? ["-c", "%h", path] : ["-f", "%l", path];
        var psi = new ProcessStartInfo("stat", args) { RedirectStandardOutput = true, RedirectStandardError = true };
        using var p = Process.Start(psi)!;
        var text = p.StandardOutput.ReadToEnd().Trim();
        var error = p.StandardError.ReadToEnd().Trim();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, $"stat {string.Join(' ', args)} exited {p.ExitCode}: {error}");
        return long.Parse(text, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static long WindowsLinkCount(string path)
    {
        using SafeFileHandle handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        Assert.True(GetFileInformationByHandle(handle, out var info), $"GetFileInformationByHandle({path}) failed: {Marshal.GetLastPInvokeError()}");
        return info.NumberOfLinks;
    }

#pragma warning disable CS0649 // filled in by the marshaller
    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        // FILETIMEs: two DWORDs each, 4-byte aligned (a long here would add padding and shift every later field).
        public uint CreationTimeLow, CreationTimeHigh;
        public uint LastAccessTimeLow, LastAccessTimeHigh;
        public uint LastWriteTimeLow, LastWriteTimeHigh;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }
#pragma warning restore CS0649

    // DllImport, not LibraryImport: the generated stub for an out struct needs unsafe code, which this project disallows.
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation info);

    /// <summary>
    /// Makes <paramref name="link"/> a Windows junction to the folder <paramref name="target"/>, with the file
    /// system's own call (no program is started, and unlike a symbolic link a junction needs no privilege).
    /// Returns false where the file system refuses one.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static bool TryCreateJunction(string link, string target)
    {
        const uint GenericWrite = 0x40000000, ShareAll = 7, OpenExisting = 3;
        const uint BackupSemantics = 0x02000000, OpenReparsePoint = 0x00200000; // a folder, and the link itself
        const uint SetReparsePoint = 0x000900A4, MountPointTag = 0xA0000003;

        // REPARSE_DATA_BUFFER for a mount point: tag, data length, reserved, then the offsets and lengths (in bytes)
        // of the two names, then the names in UTF-16, each ended by a zero character.
        var print = System.Text.Encoding.Unicode.GetBytes(Path.GetFullPath(target));
        var substitute = System.Text.Encoding.Unicode.GetBytes(@"\??\" + Path.GetFullPath(target));
        var buffer = new byte[16 + substitute.Length + 2 + print.Length + 2];
        BitConverter.TryWriteBytes(buffer.AsSpan(0), MountPointTag);
        BitConverter.TryWriteBytes(buffer.AsSpan(4), (ushort)(buffer.Length - 8));
        BitConverter.TryWriteBytes(buffer.AsSpan(8), (ushort)0);                        // substitute name offset
        BitConverter.TryWriteBytes(buffer.AsSpan(10), (ushort)substitute.Length);
        BitConverter.TryWriteBytes(buffer.AsSpan(12), (ushort)(substitute.Length + 2)); // print name offset
        BitConverter.TryWriteBytes(buffer.AsSpan(14), (ushort)print.Length);
        substitute.CopyTo(buffer, 16);
        print.CopyTo(buffer, 16 + substitute.Length + 2);

        Directory.CreateDirectory(link);
        using var handle = CreateFileW(link, GenericWrite, ShareAll, 0, OpenExisting, BackupSemantics | OpenReparsePoint, 0);
        if (!handle.IsInvalid && DeviceIoControl(handle, SetReparsePoint, buffer, (uint)buffer.Length, 0, 0, out _, 0)) return true;
        handle.Dispose();
        Directory.Delete(link);
        return false;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, byte[] input, uint inputSize, nint output, uint outputSize, out uint returned, nint overlapped);
}
