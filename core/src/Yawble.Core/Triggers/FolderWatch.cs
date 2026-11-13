using System.IO.Enumeration;
using System.Text.Json;

namespace Yawble.Core.Triggers;

/// <summary>What a folder-change trigger watches (0017): the team's documents or a folder on this computer.</summary>
/// <param name="Root">documents (Path inside the team's documents) or host (Path an absolute folder).</param>
/// <param name="Glob">File names to watch, e.g. *.pdf (null: every file).</param>
/// <param name="QuietSec">How long a change must stay still before it fires.</param>
public sealed record FolderWatch(string Root, string Path, string? Glob, int QuietSec)
{
    public const string Documents = "documents", Host = "host";
    public const int DefaultPollSec = 30, DefaultQuietSec = 10, MaxFiles = 5000;
}

/// <summary>A change the watcher reports: what was added, removed and changed size since the last fire.</summary>
public sealed record FolderChange(IReadOnlyList<string> Added, IReadOnlyList<string> Removed, IReadOnlyList<string> Changed)
{
    public bool Any => Added.Count + Removed.Count + Changed.Count > 0;

    /// <summary>The change in a few lines for the member's instruction (at most 50 names in all).</summary>
    public string Describe(string where)
    {
        var lines = new List<string> { $"Files changed in {where}:" };
        var budget = 50;
        void Add(string label, IReadOnlyList<string> names)
        {
            if (names.Count == 0) return;
            var shown = names.Take(Math.Max(0, budget)).ToList();
            budget -= shown.Count;
            lines.Add($"- {label} ({names.Count}): {string.Join(", ", shown)}{(names.Count > shown.Count ? ", ..." : "")}");
        }
        Add("added", Added);
        Add("removed", Removed);
        Add("changed", Changed);
        return string.Join("\n", lines);
    }
}

/// <summary>
/// The folder watcher: it polls, it does not use file-system notifications, which miss
/// changes on shares and from other machines. A change is a file added, removed or changed in size (a touched file
/// whose size is the same is not one). It fires once the folder has stayed the same for the quiet period, so a copy of
/// many files, or one large file still being written, fires once, after it finishes. The first look only remembers.
/// </summary>
public static class FolderWatcher
{
    private sealed record State(Dictionary<string, long>? Baseline, Dictionary<string, long>? Pending, DateTimeOffset? PendingSince);

    /// <summary>The files under <paramref name="folder"/> matching <paramref name="glob"/>: relative path to size.</summary>
    public static Dictionary<string, long> Listing(string folder, string? glob)
    {
        var listing = new Dictionary<string, long>(StringComparer.Ordinal);
        if (!Directory.Exists(folder)) return listing;
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (var file in new DirectoryInfo(folder).EnumerateFiles("*", options))
        {
            if (!string.IsNullOrWhiteSpace(glob) && !FileSystemName.MatchesSimpleExpression(glob.Trim(), file.Name, ignoreCase: true)) continue;
            listing[System.IO.Path.GetRelativePath(folder, file.FullName).Replace('\\', '/')] = file.Length;
            if (listing.Count >= FolderWatch.MaxFiles) break;
        }
        return listing;
    }

    /// <summary>
    /// One look at the folder: the change to fire (when one has settled), and the watcher's new state to keep.
    /// </summary>
    public static (FolderChange? Fire, string State) Look(Dictionary<string, long> now, string? stateJson, DateTimeOffset at, int quietSec)
    {
        var state = Read(stateJson);
        if (state.Baseline is null) return (null, Write(new State(now, null, null))); // the first look only remembers
        if (Same(now, state.Baseline)) return (null, Write(new State(state.Baseline, null, null)));
        if (state.Pending is null || state.PendingSince is null || !Same(now, state.Pending))
            return (null, Write(new State(state.Baseline, now, at))); // still moving: wait for it to settle
        if (at - state.PendingSince.Value < TimeSpan.FromSeconds(Math.Max(0, quietSec)))
            return (null, Write(state));
        var change = Diff(state.Baseline, now);
        return (change.Any ? change : null, Write(new State(now, null, null)));
    }

    public static FolderChange Diff(IReadOnlyDictionary<string, long> before, IReadOnlyDictionary<string, long> after) => new(
        after.Keys.Where(k => !before.ContainsKey(k)).Order(StringComparer.Ordinal).ToList(),
        before.Keys.Where(k => !after.ContainsKey(k)).Order(StringComparer.Ordinal).ToList(),
        after.Where(kv => before.TryGetValue(kv.Key, out var size) && size != kv.Value).Select(kv => kv.Key).Order(StringComparer.Ordinal).ToList());

    private static bool Same(IReadOnlyDictionary<string, long> a, IReadOnlyDictionary<string, long> b) =>
        a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var v) && v == kv.Value);

    private static State Read(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new State(null, null, null);
        try { return JsonSerializer.Deserialize<State>(json) ?? new State(null, null, null); }
        catch (JsonException) { return new State(null, null, null); }
    }

    private static string Write(State s) => JsonSerializer.Serialize(s);
}
