using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Yawble.Core.Credentials;

/// <summary>
/// Linux Secret Service (gnome-keyring, KWallet) through libsecret. Uses the non-variadic
/// secret_password_{storev,lookupv,clearv}_sync calls, with a schema "com.djlsystems.Yawble" whose attributes
/// are service and account. <see cref="Probe"/> tells whether a Secret Service answers; when none does (headless
/// or minimal Linux, or no libsecret) the credential service falls back to the 0600 file.
/// On the build host the probe runs for real (no D-Bus session, so no Secret Service); storing into a real
/// keyring is NOT exercised here and is owed to a desktop Linux run.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class LinuxSecretServiceStore : ICredentialStore, Ops.ICredentialIndex, IDisposable
{
    /// <summary>libsecret is used through exact-attribute lookups only here; the doctor asks <see cref="Has"/> for each issued key.</summary>
    public IReadOnlyList<string> KeysWithPrefix(string prefix) => throw new NotSupportedException("the Secret Service store cannot list its keys");

    public bool Has(string key) => Get(key) is not null;

    private const string LibSecret = "libsecret-1.so.0";
    private const string LibGlib = "libglib-2.0.so.0";
    public const string SchemaName = "com.djlsystems.Yawble";

    private readonly string _service;
    private readonly IntPtr _schema;
    private readonly IntPtr[] _schemaStrings;

    private LinuxSecretServiceStore(string service)
    {
        _service = service;
        // SecretSchema { const gchar *name; SecretSchemaFlags flags; SecretSchemaAttribute attributes[32];
        //                gint reserved; gpointer reserved1..7; } with SecretSchemaAttribute { const gchar *name; int type; }.
        // On LP64: name @0, flags @8, attributes @16 (16 bytes each), then reserved fields: 592 bytes in all.
        const int size = 8 + 8 + 32 * 16 + 8 + 7 * 8;
        _schema = Marshal.AllocHGlobal(size);
        unsafe { new Span<byte>((void*)_schema, size).Clear(); }
        _schemaStrings = [Marshal.StringToCoTaskMemUTF8(SchemaName), Marshal.StringToCoTaskMemUTF8("service"), Marshal.StringToCoTaskMemUTF8("account")];
        Marshal.WriteIntPtr(_schema, 0, _schemaStrings[0]);
        Marshal.WriteInt32(_schema, 8, 0); // SECRET_SCHEMA_NONE
        Marshal.WriteIntPtr(_schema, 16, _schemaStrings[1]);
        Marshal.WriteInt32(_schema, 24, 0); // SECRET_SCHEMA_ATTRIBUTE_STRING
        Marshal.WriteIntPtr(_schema, 32, _schemaStrings[2]);
        Marshal.WriteInt32(_schema, 40, 0);
    }

    public string Kind => "linux-secret-service";

    /// <summary>
    /// Opens the store if libsecret loads and a Secret Service answers a lookup; otherwise null with the reason.
    /// A lookup of a key that does not exist is harmless: it reads nothing and writes nothing.
    /// </summary>
    public static LinuxSecretServiceStore? Probe(out string reason, string service = CredentialService.ServiceName)
    {
        if (!NativeLibrary.TryLoad(LibSecret, out _) || !NativeLibrary.TryLoad(LibGlib, out _))
        {
            reason = "libsecret is not installed";
            return null;
        }
        var store = new LinuxSecretServiceStore(service);
        try
        {
            store.Get("probe");
            reason = "a Secret Service answered";
            return store;
        }
        catch (CredentialStoreException ex)
        {
            store.Dispose();
            reason = "no Secret Service: " + ex.Message;
            return null;
        }
    }

    public string? Get(string key) => WithAttributes(key, attrs =>
    {
        var error = IntPtr.Zero;
        var result = secret_password_lookupv_sync(_schema, attrs, IntPtr.Zero, ref error);
        Check(error, "lookup");
        if (result == IntPtr.Zero) return null;
        try
        {
            return Marshal.PtrToStringUTF8(result);
        }
        finally
        {
            secret_password_free(result); // clears the memory before freeing it
        }
    });

    public void Set(string key, string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(value);
        WithAttributes(key, attrs =>
        {
            var error = IntPtr.Zero;
            var label = Marshal.StringToCoTaskMemUTF8($"{_service}: {key}");
            var secret = Marshal.StringToCoTaskMemUTF8(value);
            try
            {
                var ok = secret_password_storev_sync(_schema, attrs, IntPtr.Zero, label, secret, IntPtr.Zero, ref error);
                Check(error, "store");
                if (ok == 0) throw new CredentialStoreException("secret_password_storev_sync returned false");
            }
            finally
            {
                Marshal.ZeroFreeCoTaskMemUTF8(secret);
                Marshal.FreeCoTaskMem(label);
            }
            return 0;
        });
    }

    public bool Delete(string key) => WithAttributes(key, attrs =>
    {
        var error = IntPtr.Zero;
        var removed = secret_password_clearv_sync(_schema, attrs, IntPtr.Zero, ref error);
        Check(error, "clear");
        return removed != 0;
    });

    private T WithAttributes<T>(string key, Func<IntPtr, T> work)
    {
        var glib = NativeLibrary.Load(LibGlib);
        var table = g_hash_table_new(NativeLibrary.GetExport(glib, "g_str_hash"), NativeLibrary.GetExport(glib, "g_str_equal"));
        IntPtr[] strings = [Marshal.StringToCoTaskMemUTF8("service"), Marshal.StringToCoTaskMemUTF8(_service),
            Marshal.StringToCoTaskMemUTF8("account"), Marshal.StringToCoTaskMemUTF8(key)];
        try
        {
            g_hash_table_insert(table, strings[0], strings[1]);
            g_hash_table_insert(table, strings[2], strings[3]);
            return work(table);
        }
        finally
        {
            g_hash_table_unref(table);
            foreach (var s in strings) Marshal.FreeCoTaskMem(s);
        }
    }

    private static void Check(IntPtr error, string operation)
    {
        if (error == IntPtr.Zero) return;
        // GError { GQuark domain; gint code; gchar *message; }
        var code = Marshal.ReadInt32(error, 4);
        var message = Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(error, 8)) ?? "";
        g_error_free(error);
        throw new CredentialStoreException($"Secret Service {operation} failed ({code}): {message}");
    }

    public void Dispose()
    {
        Marshal.FreeHGlobal(_schema);
        foreach (var s in _schemaStrings) Marshal.FreeCoTaskMem(s);
    }

    [DllImport(LibSecret)]
    private static extern int secret_password_storev_sync(IntPtr schema, IntPtr attributes, IntPtr collection, IntPtr label,
        IntPtr password, IntPtr cancellable, ref IntPtr error);

    [DllImport(LibSecret)]
    private static extern IntPtr secret_password_lookupv_sync(IntPtr schema, IntPtr attributes, IntPtr cancellable, ref IntPtr error);

    [DllImport(LibSecret)]
    private static extern int secret_password_clearv_sync(IntPtr schema, IntPtr attributes, IntPtr cancellable, ref IntPtr error);

    [DllImport(LibSecret)]
    private static extern void secret_password_free(IntPtr password);

    [DllImport(LibGlib)]
    private static extern IntPtr g_hash_table_new(IntPtr hashFunc, IntPtr equalFunc);

    [DllImport(LibGlib)]
    private static extern int g_hash_table_insert(IntPtr table, IntPtr key, IntPtr value);

    [DllImport(LibGlib)]
    private static extern void g_hash_table_unref(IntPtr table);

    [DllImport(LibGlib)]
    private static extern void g_error_free(IntPtr error);
}
