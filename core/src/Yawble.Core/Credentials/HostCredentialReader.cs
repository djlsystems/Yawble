using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Yawble.Core.Credentials;

/// <summary>
/// Reads an agent CLI's OWN sign-in from the host's credential store (the person signed in to that CLI in their own
/// terminal; the app never asks for it). Only for CLIs whose sign-in lives there and that accept it another way inside
/// the sandbox (Copilot's token as COPILOT_GITHUB_TOKEN): an AppContainer cannot read Credential Manager (measured:
/// CredReadW returns ERROR_ACCESS_DENIED for every target inside a ProcessContainer). On macOS, Claude Code's sign-in
/// in the login Keychain, read by the gateway (<see cref="ClaudeHostSession"/>) through macOS's <c>security</c> tool.
/// Values are never logged.
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
/// Read through macOS's own tool, <c>/usr/bin/security find-generic-password -s &lt;service&gt; [-w]</c>, not the Security
/// framework: Claude Code writes its item through that tool, and the item's access list always trusts it, so reading
/// shows no Keychain prompt (a rewrite of the item by Claude Code's token refresh resets the list and dropped the app
/// from it). <see cref="Targets"/> takes an exact service name (no wildcard) and runs the command without <c>-w</c>,
/// so the password is not requested. <see cref="Read"/> adds <c>-w</c> and takes the password from stdout in memory.
/// Exit 44 (errSecItemNotFound) is "absent"; any other failure, or no answer within <see cref="Timeout"/>, is
/// "unreadable" and the reason gives the exit code only. This reader never writes, modifies or deletes the item.
/// </summary>
public sealed class MacHostCredentialReader(ISecurityTool? tool = null) : IHostCredentialReader
{
    /// <summary>errSecItemNotFound, as <c>security</c> reports it in its exit code.</summary>
    public const int ExitItemNotFound = 44;

    /// <summary>How long one run of the tool may take before it is killed.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private readonly ISecurityTool _tool = tool ?? new ProcessSecurityTool();

    /// <summary>The argv of the existence check (<paramref name="password"/> false) or the read.</summary>
    public static IReadOnlyList<string> Arguments(string service, bool password) =>
        password ? ["find-generic-password", "-s", service, "-w"] : ["find-generic-password", "-s", service];

    public IReadOnlyList<string> Targets(string filter)
    {
        if (filter.Length == 0 || filter.EndsWith('*')) return [];
        try
        {
            return _tool.Run(Arguments(filter, password: false), Timeout) is { TimedOut: false, ExitCode: 0 } ? [filter] : [];
        }
        catch (CredentialStoreException)
        {
            return [];
        }
    }

    /// <summary>The password, trailing newline trimmed; null when the item is absent (exit 44).</summary>
    /// <exception cref="CredentialStoreException">The tool failed or timed out; the message gives the exit code only.</exception>
    public string? Read(string target)
    {
        var result = _tool.Run(Arguments(target, password: true), Timeout);
        if (result.TimedOut)
            throw new CredentialStoreException($"the Keychain item could not be read: {ProcessSecurityTool.Path} did not answer within {Timeout.TotalSeconds:0} s");
        if (result.ExitCode == ExitItemNotFound) return null;
        if (result.ExitCode != 0)
            throw new CredentialStoreException($"the Keychain item could not be read: {ProcessSecurityTool.Path} exited with code {result.ExitCode}");
        var value = result.StandardOutput.TrimEnd('\n', '\r');
        return value.Length > 0 ? FromHex(value) ?? value : null;
    }

    /// <summary>
    /// <c>security -w</c> prints a password that is not printable text as hex; Claude Code's JSON is text, so this is only
    /// a fallback: an even-length all-hex value that decodes to UTF-8 starting with '{'.
    /// </summary>
    private static string? FromHex(string value)
    {
        if (value.Length % 2 != 0 || !value.All(Uri.IsHexDigit)) return null;
        try
        {
            var bytes = Convert.FromHexString(value);
            try
            {
                var text = new UTF8Encoding(false, true).GetString(bytes);
                return text.StartsWith('{') ? text : null;
            }
            finally
            {
                Array.Clear(bytes);
            }
        }
        catch (Exception e) when (e is FormatException or DecoderFallbackException)
        {
            return null;
        }
    }
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
