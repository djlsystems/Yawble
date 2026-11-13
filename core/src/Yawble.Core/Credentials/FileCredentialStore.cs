using System.Text.Json;
using System.Text.Json.Nodes;

namespace Yawble.Core.Credentials;

/// <summary>
/// The fallback when no Secret Service runs on Linux: one JSON file readable and writable only by
/// the person (mode 0600) in the app's data folder, for every stored credential. The app warns once that the
/// credential is protected only by the account's file permissions; it never refuses. No sandbox policy may grant this
/// file.
///
/// Every write goes to a fresh 0600 temp file in the same folder and is renamed over the old one, so the file is
/// never briefly readable by others and never half-written. A read that finds wider permissions (someone ran
/// chmod) narrows them back to 0600 and reports it through <see cref="PermissionsRepaired"/>.
/// </summary>
public sealed class FileCredentialStore : ICredentialStore, Ops.ICredentialIndex
{
    public const string FileName = "credentials.json";
    public const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private readonly Lock _lock = new();

    public FileCredentialStore(string directory)
    {
        Directory.CreateDirectory(directory);
        FilePath = Path.Combine(directory, FileName);
    }

    public string Kind => "file-0600";

    public string FilePath { get; }

    /// <summary>Raised (with the mode found) when a read had to narrow the file's permissions back to 0600.</summary>
    public event Action<UnixFileMode>? PermissionsRepaired;

    public string? Get(string key)
    {
        lock (_lock)
        {
            var all = Load();
            return all[key]?.GetValue<string>();
        }
    }

    public void Set(string key, string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(value);
        lock (_lock)
        {
            var all = Load();
            all[key] = value;
            Save(all);
        }
    }

    public bool Delete(string key)
    {
        lock (_lock)
        {
            var all = Load();
            if (!all.Remove(key)) return false;
            Save(all);
            return true;
        }
    }

    public IReadOnlyList<string> KeysWithPrefix(string prefix)
    {
        lock (_lock) return Load().Select(p => p.Key).Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToList();
    }

    public bool Has(string key)
    {
        lock (_lock) return Load().ContainsKey(key);
    }

    private JsonObject Load()
    {
        if (!File.Exists(FilePath)) return [];
        if (!OperatingSystem.IsWindows())
        {
            var mode = File.GetUnixFileMode(FilePath);
            if ((mode & ~OwnerOnly) != 0)
            {
                File.SetUnixFileMode(FilePath, OwnerOnly);
                PermissionsRepaired?.Invoke(mode);
            }
        }
        try
        {
            return JsonNode.Parse(File.ReadAllText(FilePath)) as JsonObject ?? [];
        }
        catch (JsonException)
        {
            throw new CredentialStoreException($"the credential file {FilePath} is not valid JSON; fix or delete it and reconnect");
        }
    }

    private void Save(JsonObject all)
    {
        var dir = Path.GetDirectoryName(FilePath)!;
        var temp = Path.Combine(dir, $".{FileName}.{Guid.NewGuid():N}.tmp");
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = OwnerOnly; // created 0600: never readable by others
        try
        {
            using (var stream = new FileStream(temp, options))
            {
                JsonSerializer.Serialize(stream, all);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, FilePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }
}
