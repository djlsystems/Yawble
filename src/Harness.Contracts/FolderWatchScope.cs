using System.Text.Json;
using System.Text.RegularExpressions;

namespace Harness.Contracts;

/// <summary>
/// What a folder trigger watches, as pure string rules: which names are never looked at, what a
/// glob matches, and whether a `file.changed` row is this trigger's to deliver. Here rather than in
/// the Host because the delivery pump in Harness.Containers asks the last question, and the watcher
/// in the Host must answer the first two identically.
/// </summary>
public static class FolderWatchScope
{
    public const string DocumentsRoot = "documents";
    public const string RootPrefix = "root:";

    /// <summary>`file.changed`'s `changed` list is cut here; `count` still carries the whole number.</summary>
    public const int MaximumChangedListed = 100;

    /// <summary>The prefix of a poll's source, `trigger:&lt;id&gt;` - the same the event path uses.</summary>
    public const string TriggerSourcePrefix = "trigger:";

    private static readonly HashSet<string> IgnoredNames = new(StringComparer.Ordinal)
    {
        ".harness-team", ".git", ".worktrees", "node_modules",
    };

    /// <summary>
    /// A name never listed, at any depth: the team marker, git's own folders, dependency trees and
    /// the temp files editors leave while a document is open. A folder by one of these names is not
    /// descended into.
    /// </summary>
    public static bool IsIgnored(string name) =>
        IgnoredNames.Contains(name)
        || name.EndsWith('~')
        || name.StartsWith(".#", StringComparison.Ordinal)
        || name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".swp", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether <paramref name="relative"/> (a file's path relative to the WATCHED FOLDER, forward
    /// slashes) passes <paramref name="glob"/>. No `/` in the glob matches the file name alone, so
    /// `*.pdf` means every pdf at any depth; a `/` matches the whole relative path. `*` and `?` stay
    /// inside one segment, `**` crosses them. A null or blank glob passes everything.
    /// </summary>
    public static bool GlobMatches(string? glob, string relative)
    {
        if (string.IsNullOrWhiteSpace(glob)) return true;

        var pattern = glob.Trim().Replace('\\', '/');
        var subject = pattern.Contains('/')
            ? relative
            : relative[(relative.LastIndexOf('/') + 1)..];

        return Regex.IsMatch(subject, GlobToRegex(pattern), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    /// <summary>Forward slashes, no leading or trailing slash. Empty is the root itself.</summary>
    public static string Normalise(string? path) =>
        (path ?? "").Replace('\\', '/').Trim().Trim('/');

    /// <summary>
    /// Whether this `file.changed` row is <paramref name="trigger"/>'s to deliver - asked by the
    /// delivery pump for a folder trigger only, before its filter.
    ///
    /// A POLL'S ROW BELONGS TO THE TRIGGER THAT POLLED. Two members watching one folder each poll it
    /// and each publish; if either row could wake both, every change would wake each of them twice.
    /// Any other row - the Documents dialog's - wakes every folder trigger whose root, folder and
    /// glob cover at least one changed file.
    ///
    /// Never throws: this runs inside the pump, where an exception stops every container's
    /// deliveries rather than one trigger's.
    /// </summary>
    public static bool Covers(TriggerRow trigger, string? payload, string? source)
    {
        if (source is not null && source.StartsWith(TriggerSourcePrefix, StringComparison.Ordinal))
        {
            return string.Equals(source[TriggerSourcePrefix.Length..], trigger.Id, StringComparison.Ordinal);
        }

        if (payload is null || trigger.WatchRoot is null) return false;

        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object) return false;

            if (!root.TryGetProperty(PayloadFields.Root, out var area)
                || area.ValueKind != JsonValueKind.String
                || !string.Equals(area.GetString(), trigger.WatchRoot, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (!root.TryGetProperty(PayloadFields.Changed, out var changed)
                || changed.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            var folder = Normalise(trigger.WatchPath);

            foreach (var item in changed.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String) continue;

                var path = Normalise(item.GetString());
                string inside;

                if (folder.Length == 0) inside = path;
                else if (path.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase)) inside = path[(folder.Length + 1)..];
                else continue;

                if (inside.Split('/').Any(IsIgnored)) continue;
                if (GlobMatches(trigger.WatchGlob, inside)) return true;
            }

            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string GlobToRegex(string glob)
    {
        var builder = new System.Text.StringBuilder("^");

        for (var i = 0; i < glob.Length; i++)
        {
            var c = glob[i];

            if (c == '*' && i + 1 < glob.Length && glob[i + 1] == '*')
            {
                // `**/` also matches no folder at all, so `in/**/*.csv` takes `in/a.csv`.
                if (i + 2 < glob.Length && glob[i + 2] == '/')
                {
                    builder.Append("(?:.*/)?");
                    i += 2;
                }
                else
                {
                    builder.Append(".*");
                    i++;
                }
            }
            else if (c == '*') builder.Append("[^/]*");
            else if (c == '?') builder.Append("[^/]");
            else builder.Append(Regex.Escape(c.ToString()));
        }

        return builder.Append('$').ToString();
    }
}
