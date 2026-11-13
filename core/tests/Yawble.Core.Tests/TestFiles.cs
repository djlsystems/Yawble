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
}
