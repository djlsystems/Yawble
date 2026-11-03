namespace Harness.Host;

/// <summary>One stored connection binding the active version cannot use, with the sentence saying why.
/// Never a value: the binding key, the slot, the connection's id and name.</summary>
public sealed record UnusableBinding(string Key, string Slot, string ConnectionId, string ConnectionName, string Reason);

/// <summary>
/// A STORED BINDING THE ACTIVE VERSION CANNOT USE. A plugin member is hired as <c>plugin:&lt;id&gt;</c>
/// with no version, so a newer version moves it on at once with its bindings kept as they are stored -
/// nothing on an upgrade removes one. A binding the new manifest still takes is used as before. One it
/// no longer takes is KEPT and IGNORED (as <see cref="Connections.BindingRefusalAsync"/> keeps it on a
/// save): its slot is gone, its slot now takes one connection and this is a second
/// (<c>&lt;slot&gt;#2</c>...), or its slot no longer takes that kind of connection. Each such binding is
/// said in words - on the upgrade's answer (<see cref="PluginUpgradeAttention"/>) and as a connection
/// that holds the member on Needs you (<see cref="PluginSetup"/>) - so a later refused command is never
/// the first a person hears of it. A binding whose connection was removed is not this: removing one
/// unbinds it, with its own row.
/// </summary>
public static class PluginBindingFit
{
    /// <summary>Each binding of <paramref name="bindings"/> that <paramref name="manifest"/> cannot use, in
    /// key order.</summary>
    public static async Task<IReadOnlyList<UnusableBinding>> UnusableAsync(
        PluginManifest manifest, IReadOnlyDictionary<string, string> bindings, IConnectionStore store, CancellationToken ct = default)
    {
        var named = $"{manifest.Id} {manifest.Version}";
        var found = new List<UnusableBinding>();

        foreach (var (key, connectionId) in bindings.OrderBy(b => b.Key, StringComparer.Ordinal))
        {
            if (await store.GetAsync(connectionId, ct) is not { } connection) continue;

            var slot = PluginConnectionSlot.BindingKey(key)?.Slot ?? key;
            var bound = $"Connection {connection.Named} is bound to `{key}` of this member, and";

            string? reason = !manifest.Connections.TryGetValue(slot, out var declared)
                ? $"{bound} {named} no longer has the connection slot `{slot}`: the binding is kept and not used."
                : slot != key && !declared.Multiple
                    ? $"{bound} slot `{slot}` of {named} now takes one connection: this second binding is kept and not used."
                    : !declared.Admits(connection.Provider)
                        ? $"{bound} slot `{slot}` of {named} no longer takes a {ConnectionProviders.Display(connection.Provider)} connection: "
                            + "the binding is kept and not used."
                        : null;

            if (reason is not null)
            {
                found.Add(new UnusableBinding(key, slot, connection.Id, connection.Name,
                    reason + " Bind it to a slot that takes it, or unbind it, in the member's settings."));
            }
        }

        return found;
    }
}
