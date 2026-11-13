using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Yawble.Core.Credentials;

/// <summary>
/// macOS Keychain: a generic password in the login keychain under the app's OWN service name (never
/// "Claude Code-credentials"), account = the slot key. Uses the SecKeychain generic-password calls, which take
/// plain buffers (no CFDictionary building) and are still shipped in macOS 15.
/// NOT exercised on the Linux build host; owed to a macOS run.
/// </summary>
[SupportedOSPlatform("macos")]
public sealed partial class MacKeychainStore(string service = CredentialService.ServiceName) : ICredentialStore, Ops.ICredentialIndex
{
    /// <summary>The legacy SecKeychain calls here look entries up by exact account only; the doctor asks <see cref="Has"/> for each issued key.</summary>
    public IReadOnlyList<string> KeysWithPrefix(string prefix) => throw new NotSupportedException("the macOS keychain store cannot list its keys");

    public bool Has(string key)
    {
        var s = Encoding.UTF8.GetBytes(service);
        var a = Encoding.UTF8.GetBytes(key);
        var status = SecKeychainFindGenericPassword(IntPtr.Zero, (uint)s.Length, s, (uint)a.Length, a, out _, out var data, out var item);
        if (status == ErrSecItemNotFound) return false;
        if (status != 0) throw new CredentialStoreException($"SecKeychainFindGenericPassword failed for {key} (OSStatus {status})");
        SecKeychainItemFreeContent(IntPtr.Zero, data);
        if (item != IntPtr.Zero) CFRelease(item);
        return true;
    }

    private const string Security = "/System/Library/Frameworks/Security.framework/Security";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const int ErrSecItemNotFound = -25300;

    public string Kind => "macos-keychain";

    public string? Get(string key)
    {
        var s = Encoding.UTF8.GetBytes(service);
        var a = Encoding.UTF8.GetBytes(key);
        var status = SecKeychainFindGenericPassword(IntPtr.Zero, (uint)s.Length, s, (uint)a.Length, a, out var length, out var data, out var item);
        if (status == ErrSecItemNotFound) return null;
        if (status != 0) throw new CredentialStoreException($"SecKeychainFindGenericPassword failed for {key} (OSStatus {status})");
        try
        {
            var bytes = new byte[length];
            Marshal.Copy(data, bytes, 0, (int)length);
            return Encoding.UTF8.GetString(bytes);
        }
        finally
        {
            SecKeychainItemFreeContent(IntPtr.Zero, data);
            if (item != IntPtr.Zero) CFRelease(item);
        }
    }

    public void Set(string key, string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(value);
        var s = Encoding.UTF8.GetBytes(service);
        var a = Encoding.UTF8.GetBytes(key);
        var v = Encoding.UTF8.GetBytes(value);
        try
        {
            var status = SecKeychainFindGenericPassword(IntPtr.Zero, (uint)s.Length, s, (uint)a.Length, a, out _, out var old, out var item);
            if (status == 0)
            {
                SecKeychainItemFreeContent(IntPtr.Zero, old);
                try
                {
                    status = SecKeychainItemModifyAttributesAndData(item, IntPtr.Zero, (uint)v.Length, v);
                }
                finally
                {
                    CFRelease(item);
                }
                if (status != 0) throw new CredentialStoreException($"SecKeychainItemModifyAttributesAndData failed for {key} (OSStatus {status})");
                return;
            }
            if (status != ErrSecItemNotFound) throw new CredentialStoreException($"SecKeychainFindGenericPassword failed for {key} (OSStatus {status})");
            status = SecKeychainAddGenericPassword(IntPtr.Zero, (uint)s.Length, s, (uint)a.Length, a, (uint)v.Length, v, IntPtr.Zero);
            if (status != 0) throw new CredentialStoreException($"SecKeychainAddGenericPassword failed for {key} (OSStatus {status})");
        }
        finally
        {
            Array.Clear(v);
        }
    }

    public bool Delete(string key)
    {
        var s = Encoding.UTF8.GetBytes(service);
        var a = Encoding.UTF8.GetBytes(key);
        var status = SecKeychainFindGenericPassword(IntPtr.Zero, (uint)s.Length, s, (uint)a.Length, a, out _, out var data, out var item);
        if (status == ErrSecItemNotFound) return false;
        if (status != 0) throw new CredentialStoreException($"SecKeychainFindGenericPassword failed for {key} (OSStatus {status})");
        SecKeychainItemFreeContent(IntPtr.Zero, data);
        try
        {
            status = SecKeychainItemDelete(item);
        }
        finally
        {
            CFRelease(item);
        }
        if (status != 0) throw new CredentialStoreException($"SecKeychainItemDelete failed for {key} (OSStatus {status})");
        return true;
    }

    [LibraryImport(Security)]
    private static partial int SecKeychainFindGenericPassword(IntPtr keychainOrArray, uint serviceNameLength, byte[] serviceName,
        uint accountNameLength, byte[] accountName, out uint passwordLength, out IntPtr passwordData, out IntPtr itemRef);

    [LibraryImport(Security)]
    private static partial int SecKeychainAddGenericPassword(IntPtr keychain, uint serviceNameLength, byte[] serviceName,
        uint accountNameLength, byte[] accountName, uint passwordLength, byte[] passwordData, IntPtr itemRef);

    [LibraryImport(Security)]
    private static partial int SecKeychainItemModifyAttributesAndData(IntPtr itemRef, IntPtr attrList, uint length, byte[] data);

    [LibraryImport(Security)]
    private static partial int SecKeychainItemFreeContent(IntPtr attrList, IntPtr data);

    [LibraryImport(Security)]
    private static partial int SecKeychainItemDelete(IntPtr itemRef);

    [LibraryImport(CoreFoundation)]
    private static partial void CFRelease(IntPtr cf);
}
