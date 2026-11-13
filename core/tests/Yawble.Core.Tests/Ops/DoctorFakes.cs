using Yawble.Core.Credentials;
using Yawble.Core.Ops;

namespace Yawble.Core.Tests.Ops;

/// <summary>An in-memory credential store that can list its keys. Tests only: the app never uses it.</summary>
internal sealed class MemoryCredentialStore : ICredentialStore, ICredentialIndex
{
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

    /// <summary>Behave like the macOS and Linux stores: no listing, only exact lookups.</summary>
    public bool CannotList { get; init; }

    public string Kind => "memory";

    public string? Get(string key) => _values.TryGetValue(key, out var v) ? v : null;

    public void Set(string key, string value) => _values[key] = value;

    public bool Delete(string key) => _values.Remove(key);

    public bool Has(string key) => _values.ContainsKey(key);

    public IReadOnlyList<string> KeysWithPrefix(string prefix) => CannotList
        ? throw new NotSupportedException()
        : _values.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToArray();
}

internal sealed class MemoryLedger : IIssuedKeyLedger
{
    private readonly SortedSet<string> _keys = new(StringComparer.Ordinal);

    public void Record(string key) => _keys.Add(key);

    public void Forget(string key) => _keys.Remove(key);

    public IReadOnlyList<string> Keys() => _keys.ToList();
}
