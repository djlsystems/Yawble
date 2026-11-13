using System.Text.Json;

namespace Yawble.Core.Credentials;

/// <summary>
/// The credential-store keys THIS data folder created. The Windows Credential Manager (and the Keychain) use one service name
/// for every data folder on the computer, so "a key under connection/ that no Connection here uses" can belong to another
/// instance. Only a key recorded here may ever be offered for cleanup. Key names only, never a value.
/// </summary>
public interface IIssuedKeyLedger
{
    /// <summary>Called before the key is written to the store, so a crash in between leaves an entry for nothing, never the reverse.</summary>
    void Record(string key);

    /// <summary>Called once the entry is deleted from the store.</summary>
    void Forget(string key);

    IReadOnlyList<string> Keys();
}

/// <summary>The ledger as a JSON file of key names in the data folder, rewritten whole through a temp file.</summary>
public sealed class FileIssuedKeyLedger : IIssuedKeyLedger
{
    public const string FileName = "issued-credential-keys.json";

    private readonly string _path;
    private readonly Lock _lock = new();

    public FileIssuedKeyLedger(string directory)
    {
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, FileName);
    }

    public void Record(string key)
    {
        lock (_lock)
        {
            var keys = Load();
            if (keys.Add(key)) Save(keys);
        }
    }

    public void Forget(string key)
    {
        lock (_lock)
        {
            var keys = Load();
            if (keys.Remove(key)) Save(keys);
        }
    }

    public IReadOnlyList<string> Keys()
    {
        lock (_lock) return Load().Order(StringComparer.Ordinal).ToList();
    }

    private SortedSet<string> Load()
    {
        if (!File.Exists(_path)) return new SortedSet<string>(StringComparer.Ordinal);
        try
        {
            return new SortedSet<string>(JsonSerializer.Deserialize<string[]>(File.ReadAllText(_path)) ?? [], StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            // Saving a Connection must not fail because of this file. Keep the bad file aside and start empty: the only loss is
            // that older keys are no longer offered for cleanup (the safe direction; nothing is ever deleted for want of a record).
            try { File.Move(_path, _path + ".unreadable", overwrite: true); } catch (IOException) { }
            return new SortedSet<string>(StringComparer.Ordinal);
        }
    }

    private void Save(SortedSet<string> keys)
    {
        var temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(keys));
            File.Move(temp, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }
}
