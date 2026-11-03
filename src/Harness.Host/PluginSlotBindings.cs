namespace Harness.Host;

/// <summary>
/// A SAVE NEVER DROPS A CONNECTION OF A MULTIPLE SLOT THE PERSON DID NOT REMOVE. A slot declared
/// <c>"multiple": true</c> stores its connections under <c>&lt;slot&gt;</c>, <c>&lt;slot&gt;#2</c>,
/// <c>&lt;slot&gt;#3</c>... A body naming only some of them - a form that showed one picker for the slot,
/// a CLI call naming the one just chosen - used to replace the rest with nothing, and nobody was told. Now
/// a multiple slot's bindings are a set: every stored connection stays unless the body names it in
/// <c>removedConnections</c> for that slot, and every other connection the body names for the slot is added. The
/// result is re-keyed <c>&lt;slot&gt;</c>, <c>&lt;slot&gt;#2</c>... in the order the person left them: the body's order when it names every connection still kept, else stored first and the new ones after. A slot that
/// takes one connection, and a slot the active version does not declare, are as the body says, as before.
/// </summary>
public static class PluginSlotBindings
{
    /// <summary>The bindings a save stores: <paramref name="sent"/> with each multiple slot of
    /// <paramref name="manifest"/> merged with <paramref name="stored"/>, less what
    /// <paramref name="removed"/> names (slot to connection ids).</summary>
    public static Dictionary<string, string> Merge(
        PluginManifest manifest,
        IReadOnlyDictionary<string, string> stored,
        IReadOnlyDictionary<string, string> sent,
        IReadOnlyDictionary<string, string[]>? removed)
    {
        static string SlotOf(string key) => PluginConnectionSlot.BindingKey(key)?.Slot ?? key;

        static IEnumerable<KeyValuePair<string, string>> InKeyOrder(IEnumerable<KeyValuePair<string, string>> bindings) =>
            bindings.OrderBy(b => PluginConnectionSlot.BindingKey(b.Key)?.Index ?? 1).ThenBy(b => b.Key, StringComparer.Ordinal);

        bool IsMultiple(string slot) => manifest.Connections.TryGetValue(slot, out var declared) && declared.Multiple;

        var merged = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, id) in sent)
        {
            if (!IsMultiple(SlotOf(key))) merged[key] = id;
        }

        var slots = stored.Keys.Concat(sent.Keys).Select(SlotOf).Where(IsMultiple).Distinct(StringComparer.Ordinal);
        foreach (var slot in slots)
        {
            var gone = removed?.GetValueOrDefault(slot) is { } ids ? ids.ToHashSet(StringComparer.Ordinal) : [];
            var kept = InKeyOrder(stored.Where(b => SlotOf(b.Key) == slot)).Select(b => b.Value);
            var added = InKeyOrder(sent.Where(b => SlotOf(b.Key) == slot)).Select(b => b.Value);

            // In the order the person left them: a body naming every connection still kept is the whole list,
            // as the dialog sends it, so its order wins. A body naming only some (the one just chosen) keeps
            // the stored ones where they were and adds the rest after them.
            bool Live(string id) => !string.IsNullOrEmpty(id) && !gone.Contains(id);
            var keptLive = kept.Where(Live).ToHashSet(StringComparer.Ordinal);
            var whole = keptLive.IsSubsetOf(added);
            var ordered = whole ? added.Concat(kept) : kept.Concat(added);

            var index = 1;
            foreach (var id in ordered.Where(Live).Distinct(StringComparer.Ordinal))
            {
                merged[index == 1 ? slot : $"{slot}#{index}"] = id;
                index++;
            }
        }

        return merged;
    }
}
