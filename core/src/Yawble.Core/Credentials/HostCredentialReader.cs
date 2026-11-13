using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Yawble.Core.Credentials;

/// <summary>
/// Reads an agent CLI's OWN sign-in from the host's credential store (the person signed in to that CLI in their own
/// terminal; the app never asks for it). Only for CLIs whose sign-in lives there and that accept it another way inside
/// the sandbox (Copilot's token as COPILOT_GITHUB_TOKEN): an AppContainer cannot read Credential Manager (measured:
/// CredReadW returns ERROR_ACCESS_DENIED for every target inside a ProcessContainer). Values are never logged.
/// </summary>
public interface IHostCredentialReader
{
    /// <summary>The targets that exist and match <paramref name="filter"/> (CredEnumerate's "prefix*" form); no value is read.</summary>
    IReadOnlyList<string> Targets(string filter);

    /// <summary>The secret of a generic credential, decoded as the CLI stored it (UTF-16 or UTF-8); null when absent.</summary>
    string? Read(string target);
}

public static class HostCredentials
{
    public static IHostCredentialReader ForThisHost() =>
        OperatingSystem.IsWindows() ? new WindowsHostCredentialReader() : new NoHostCredentialReader();
}

public sealed class NoHostCredentialReader : IHostCredentialReader
{
    public IReadOnlyList<string> Targets(string filter) => [];

    public string? Read(string target) => null;
}

[SupportedOSPlatform("windows")]
public sealed partial class WindowsHostCredentialReader : IHostCredentialReader
{
    private const uint CredTypeGeneric = 1;

    public IReadOnlyList<string> Targets(string filter)
    {
        if (!CredEnumerateW(filter, 0, out var count, out var list)) return [];
        try
        {
            var targets = new List<string>();
            for (var i = 0; i < count; i++)
            {
                var ptr = Marshal.ReadIntPtr(list, i * IntPtr.Size);
                var cred = Marshal.PtrToStructure<Credential>(ptr);
                if (cred.Type == CredTypeGeneric && Marshal.PtrToStringUni(cred.TargetName) is { } t) targets.Add(t);
            }
            return targets;
        }
        finally
        {
            CredFree(list);
        }
    }

    public string? Read(string target)
    {
        if (!CredReadW(target, CredTypeGeneric, 0, out var ptr)) return null;
        try
        {
            var cred = Marshal.PtrToStructure<Credential>(ptr);
            if (cred.CredentialBlobSize == 0 || cred.CredentialBlob == IntPtr.Zero) return null;
            var bytes = new byte[cred.CredentialBlobSize];
            Marshal.Copy(cred.CredentialBlob, bytes, 0, bytes.Length);
            // keytar (Node) stores UTF-16; go-keyring and others UTF-8. Printable ASCII in UTF-16 decides it.
            var utf16 = bytes.Length % 2 == 0 ? Encoding.Unicode.GetString(bytes) : null;
            return utf16 is not null && utf16.Length > 0 && utf16.All(c => c is >= ' ' and <= '~') ? utf16 : Encoding.UTF8.GetString(bytes);
        }
        finally
        {
            CredFree(ptr);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Credential
    {
        public uint Flags;
        public uint Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public long LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }

    [LibraryImport("advapi32.dll", EntryPoint = "CredReadW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredReadW(string target, uint type, uint flags, out IntPtr credential);

    [LibraryImport("advapi32.dll", EntryPoint = "CredEnumerateW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredEnumerateW(string? filter, uint flags, out int count, out IntPtr credentials);

    [LibraryImport("advapi32.dll", EntryPoint = "CredFree")]
    private static partial void CredFree(IntPtr buffer);
}
