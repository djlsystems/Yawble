using System.Collections.Concurrent;
using Yawble.Core.Credentials;

namespace Yawble.Core.Tests.Connections;

/// <summary>An in-memory credential store for tests. Never touches the OS store.</summary>
internal sealed class MemoryCredentialStore : ICredentialStore
{
    private readonly ConcurrentDictionary<string, string> _items = new(StringComparer.Ordinal);

    /// <summary>When set, every write throws as a failing OS store would.</summary>
    public bool FailWrites { get; set; }

    public string Kind => "memory";
    public IReadOnlyCollection<string> Keys => _items.Keys.ToArray();
    public string? Get(string key) => _items.TryGetValue(key, out var v) ? v : null;
    public void Set(string key, string value)
    {
        if (FailWrites) throw new CredentialStoreException("the store refused the write");
        _items[key] = value;
    }
    public bool Delete(string key) => _items.TryRemove(key, out _);
}
