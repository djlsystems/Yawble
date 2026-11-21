using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Yawble.Core.Credentials;

/// <summary>
/// Reads an agent CLI's OWN sign-in from the host's credential store (the person signed in to that CLI in their own
/// terminal; the app never asks for it). Only for CLIs whose sign-in lives there and that accept it another way inside
/// the sandbox (Copilot's token as COPILOT_GITHUB_TOKEN): an AppContainer cannot read Credential Manager (measured:
/// CredReadW returns ERROR_ACCESS_DENIED for every target inside a ProcessContainer). On macOS, Claude Code's sign-in
/// in the login Keychain, read by the gateway (<see cref="ClaudeHostSession"/>). Values are never logged.
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
        OperatingSystem.IsWindows() ? new WindowsHostCredentialReader()
        : OperatingSystem.IsMacOS() ? new MacHostCredentialReader()
        : new NoHostCredentialReader();
}

/// <summary>
/// macOS: a target is the SERVICE of a generic password in the login Keychain (Claude Code's "Claude Code-credentials").
/// <see cref="Targets"/> takes an exact service name (no wildcard) and checks the item exists without reading its
/// password, so it shows no prompt. <see cref="Read"/> reads the password of the item whose account is the user name
/// (Claude Code's own), else of any account of that service; the first read makes macOS ask the person once.
/// Through <see cref="MacKeychainStore"/>'s Keychain calls. NOT exercised off macOS; tests use a fake reader.
/// </summary>
[SupportedOSPlatform("macos")]
public sealed class MacHostCredentialReader(Func<string>? userName = null) : IHostCredentialReader
{
    private readonly Func<string> _userName = userName ?? (() => Environment.UserName);

    public IReadOnlyList<string> Targets(string filter) =>
        filter.Length > 0 && !filter.EndsWith('*') && MacKeychainStore.ItemExists(filter, null) ? [filter] : [];

    public string? Read(string target) =>
        MacKeychainStore.ReadItem(target, _userName()) is { Length: > 0 } own ? own
        : MacKeychainStore.ReadItem(target, null) is { Length: > 0 } any ? any : null;
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
