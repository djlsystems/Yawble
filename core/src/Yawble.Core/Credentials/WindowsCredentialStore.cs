using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Yawble.Core.Credentials;

/// <summary>
/// Windows Credential Manager: a generic credential per slot, target "Yawble/&lt;key&gt;", persisted for
/// the local machine and protected by DPAPI per user. The blob is the UTF-8 value.
/// NOT exercised on the Linux build host; owed to a Windows run.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class WindowsCredentialStore(string service = CredentialService.ServiceName) : ICredentialStore, Ops.ICredentialIndex
{
    /// <summary>
    /// The keys (without the service name) whose target starts with "{service}/{prefix}". Target names only: no credential blob is
    /// read. Every data folder on this computer shares the service name, so the caller must still narrow this to its own keys.
    /// </summary>
    public IReadOnlyList<string> KeysWithPrefix(string prefix)
    {
        var filter = Target(prefix) + "*";
        if (!CredEnumerateW(filter, 0, out var count, out var array))
        {
            var error = Marshal.GetLastPInvokeError();
            if (error == ErrorNotFound) return [];
            throw new CredentialStoreException($"CredEnumerateW failed (Win32 error {error})");
        }
        try
        {
            var head = Target("");
            var keys = new List<string>();
            for (var i = 0; i < count; i++)
            {
                var cred = Marshal.PtrToStructure<Credential>(Marshal.ReadIntPtr(array, i * IntPtr.Size));
                var name = Marshal.PtrToStringUni(cred.TargetName);
                if (cred.Type == CredTypeGeneric && name is not null && name.StartsWith(head + prefix, StringComparison.Ordinal))
                    keys.Add(name[head.Length..]);
            }
            return keys.Order(StringComparer.Ordinal).ToList();
        }
        finally
        {
            CredFree(array);
        }
    }

    public bool Has(string key)
    {
        if (!CredReadW(Target(key), CredTypeGeneric, 0, out var ptr))
        {
            var error = Marshal.GetLastPInvokeError();
            if (error == ErrorNotFound) return false;
            throw new CredentialStoreException($"CredReadW failed for {Target(key)} (Win32 error {error})");
        }
        CredFree(ptr);
        return true;
    }

    [LibraryImport("advapi32.dll", EntryPoint = "CredEnumerateW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredEnumerateW(string filter, uint flags, out uint count, out IntPtr credentials);

    private const uint CredTypeGeneric = 1;
    private const uint CredPersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;

    public string Kind => "windows-credential-manager";

    private string Target(string key) => $"{service}/{key}";

    public string? Get(string key)
    {
        if (!CredReadW(Target(key), CredTypeGeneric, 0, out var ptr))
        {
            var error = Marshal.GetLastPInvokeError();
            if (error == ErrorNotFound) return null;
            throw new CredentialStoreException($"CredReadW failed for {Target(key)} (Win32 error {error})");
        }
        try
        {
            var cred = Marshal.PtrToStructure<Credential>(ptr);
            if (cred.CredentialBlobSize == 0 || cred.CredentialBlob == IntPtr.Zero) return null;
            var bytes = new byte[cred.CredentialBlobSize];
            Marshal.Copy(cred.CredentialBlob, bytes, 0, bytes.Length);
            return System.Text.Encoding.UTF8.GetString(bytes);
        }
        finally
        {
            CredFree(ptr);
        }
    }

    public void Set(string key, string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(value);
        var bytes = System.Text.Encoding.UTF8.GetBytes(value);
        var blob = Marshal.AllocHGlobal(bytes.Length);
        var target = Marshal.StringToHGlobalUni(Target(key));
        var user = Marshal.StringToHGlobalUni(Environment.UserName);
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var cred = new Credential
            {
                Type = CredTypeGeneric,
                TargetName = target,
                CredentialBlobSize = (uint)bytes.Length,
                CredentialBlob = blob,
                Persist = CredPersistLocalMachine,
                UserName = user,
            };
            if (!CredWriteW(ref cred, 0))
            {
                throw new CredentialStoreException($"CredWriteW failed for {Target(key)} (Win32 error {Marshal.GetLastPInvokeError()})");
            }
        }
        finally
        {
            // Zero the copy of the secret before freeing it.
            Marshal.Copy(new byte[bytes.Length], 0, blob, bytes.Length);
            Array.Clear(bytes);
            Marshal.FreeHGlobal(blob);
            Marshal.FreeHGlobal(target);
            Marshal.FreeHGlobal(user);
        }
    }

    public bool Delete(string key)
    {
        if (CredDeleteW(Target(key), CredTypeGeneric, 0)) return true;
        var error = Marshal.GetLastPInvokeError();
        if (error == ErrorNotFound) return false;
        throw new CredentialStoreException($"CredDeleteW failed for {Target(key)} (Win32 error {error})");
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

    [LibraryImport("advapi32.dll", EntryPoint = "CredWriteW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredWriteW(ref Credential credential, uint flags);

    [LibraryImport("advapi32.dll", EntryPoint = "CredDeleteW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredDeleteW(string target, uint type, uint flags);

    [LibraryImport("advapi32.dll", EntryPoint = "CredFree")]
    private static partial void CredFree(IntPtr buffer);
}
