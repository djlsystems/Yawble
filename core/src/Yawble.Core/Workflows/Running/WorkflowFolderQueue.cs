using System.Text.Json;
using System.Text.Json.Nodes;

namespace Yawble.Core.Workflows.Running;

/// <summary>
/// A workflow member's folder fires that came while one of its runs was going or waiting on a tell: one entry per trigger
/// (its source, trigger:&lt;id&gt;), in the order they first came, each with the merged file list. A file seen in more than
/// one fire is listed once: first seen added it stays added (added then removed: dropped), seen again as added after changed or
/// removed it is changed, otherwise it has the kind of its latest fire (see <c>Then</c>). Kept as JSON in the store
/// (<see cref="WorkflowRunStore.FolderQueue"/>), so it outlives a restart.
/// </summary>
public static class WorkflowFolderQueue
{
    public const string Added = "added", Removed = "removed", Changed = "changed";
    private static readonly string[] Kinds = [Added, Removed, Changed];

    /// <summary>The most files one trigger's queued fire lists; files beyond it are dropped (counted in <see cref="Entry.Dropped"/>).</summary>
    public const int MaxFilesPerTrigger = 10_000;

    /// <summary>
    /// One trigger's queued change: the folder (the latest fire's root) and each file with its merged kind, in first-seen order;
    /// <paramref name="Dropped"/>: how many new files were left out because the entry was full.
    /// </summary>
    public sealed record Entry(string Source, string Root, IReadOnlyList<(string Path, string Kind)> Files, int Dropped = 0)
    {
        /// <summary>The run's input: FolderChange { root, added, removed, changed }.</summary>
        public JsonObject Payload() => WorkflowPayloads.FolderChange(Root,
            Files.Where(f => f.Kind == Added).Select(f => f.Path),
            Files.Where(f => f.Kind == Removed).Select(f => f.Path),
            Files.Where(f => f.Kind == Changed).Select(f => f.Path));

        /// <summary>The trigger's id from its source; null when the source is not trigger:&lt;id&gt;.</summary>
        public long? TriggerId =>
            Source.StartsWith(Yawble.Core.Bus.BusEngine.TriggerSourcePrefix, StringComparison.Ordinal)
            && long.TryParse(Source.AsSpan(Yawble.Core.Bus.BusEngine.TriggerSourcePrefix.Length), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var id) ? id : null;
    }

    /// <summary>
    /// A FolderChange payload added to the queue: merged into the trigger's entry, or a new entry at the end. An entry holds at most
    /// <paramref name="cap"/> files: new files beyond it are dropped and counted, and <paramref name="warn"/> is told once, when the
    /// entry first fills (later drops are only counted).
    /// </summary>
    public static List<Entry> Merge(IReadOnlyList<Entry> queue, string source, JsonObject payload, Action<string>? warn = null, int cap = MaxFilesPerTrigger)
    {
        var list = queue.ToList();
        var at = list.FindIndex(e => e.Source == source);
        var slots = new List<(string Path, string Kind)?>(at < 0 ? 0 : list[at].Files.Count);
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        if (at >= 0)
            foreach (var f in list[at].Files)
                if (index.TryAdd(f.Path, slots.Count)) slots.Add(f);
        var count = index.Count;
        var dropped = 0;
        foreach (var kind in Kinds)
        {
            if (payload[kind] is not JsonArray paths) continue;
            foreach (var node in paths)
            {
                if (node is not JsonValue v || !v.TryGetValue<string>(out var path)) continue;
                if (index.TryGetValue(path, out var i))
                {
                    if (Then(slots[i]!.Value.Kind, kind) is { } merged) slots[i] = (path, merged);
                    else
                    {
                        slots[i] = null;
                        index.Remove(path);
                        count--;
                    }
                }
                else if (count >= cap) dropped++;
                else
                {
                    index[path] = slots.Count;
                    slots.Add((path, kind));
                    count++;
                }
            }
        }
        var files = new List<(string Path, string Kind)>(count);
        foreach (var slot in slots)
            if (slot is { } f) files.Add(f);
        var before = at < 0 ? 0 : list[at].Dropped;
        if (dropped > 0 && before == 0)
            warn?.Invoke($"the queued folder fire of {source} reached its {cap} files: {dropped} more file(s) dropped; files past the limit are dropped until its run starts");
        var root = payload["root"] is JsonValue r && r.TryGetValue<string>(out var s) ? s : at < 0 ? "" : list[at].Root;
        var entry = new Entry(source, root, files, before + dropped);
        if (at < 0) list.Add(entry);
        else list[at] = entry;
        return list;
    }

    /// <summary>
    /// A queued file's kind once a later fire saw it as <paramref name="later"/>; null: the file leaves the fire. A file the queue
    /// has as added stays added (a run never saw it: added then changed is added), and added then removed leaves the fire. A file
    /// the queue has as changed or removed was known to a run, so added again is changed. Otherwise the later kind.
    /// </summary>
    private static string? Then(string queued, string later) => (queued, later) switch
    {
        (Added, Removed) => null,
        (Added, _) => Added,
        (_, Added) => Changed,
        _ => later,
    };

    /// <summary>
    /// An entry taken off the queue whose run could not start, put back at the head: what came for its trigger since is merged onto it
    /// (as a later fire), so nothing is lost and the order is kept.
    /// </summary>
    public static List<Entry> Requeue(IReadOnlyList<Entry> queue, Entry entry)
    {
        var list = queue.ToList();
        var at = list.FindIndex(e => e.Source == entry.Source);
        if (at >= 0)
        {
            var later = list[at];
            list.RemoveAt(at);
            var merged = Merge([entry], entry.Source, later.Payload())[0];
            entry = merged with { Dropped = merged.Dropped + later.Dropped };
        }
        list.Insert(0, entry);
        return list;
    }

    public static string ToJson(IReadOnlyList<Entry> queue) => new JsonArray(queue.Select(e =>
    {
        var o = new JsonObject
        {
            ["source"] = e.Source,
            ["root"] = e.Root,
            ["files"] = new JsonArray(e.Files.Select(f => (JsonNode?)new JsonObject { ["path"] = f.Path, ["kind"] = f.Kind }).ToArray()),
        };
        if (e.Dropped > 0) o["dropped"] = e.Dropped;
        return (JsonNode?)o;
    }).ToArray()).ToJsonString();

    /// <summary>The stored queue; empty when there is none or it cannot be read.</summary>
    public static List<Entry> FromJson(string? json)
    {
        var list = new List<Entry>();
        if (string.IsNullOrWhiteSpace(json)) return list;
        JsonNode? parsed;
        try { parsed = JsonNode.Parse(json); }
        catch (JsonException) { return list; }
        if (parsed is not JsonArray items) return list;
        foreach (var item in items.OfType<JsonObject>())
        {
            if (Text(item, "source") is not { } source) continue;
            var files = (item["files"] as JsonArray ?? []).OfType<JsonObject>()
                .Select(f => (Path: Text(f, "path"), Kind: Text(f, "kind")))
                .Where(f => f.Path is not null && f.Kind is not null && Kinds.Contains(f.Kind))
                .Select(f => (f.Path!, f.Kind!)).ToList();
            var dropped = item["dropped"] is JsonValue d && d.TryGetValue<int>(out var n) && n > 0 ? n : 0;
            list.Add(new Entry(source, Text(item, "root") ?? "", files, dropped));
        }
        return list;
    }

    private static string? Text(JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
