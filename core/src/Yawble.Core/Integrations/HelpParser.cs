using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Yawble.Core.Integrations;

public sealed record HelpEntry(string Name, string Description);

/// <summary>What a help text says: its first line, and the commands it lists.</summary>
public sealed record ParsedHelp(string Summary, IReadOnlyList<HelpEntry> Commands);

/// <summary>
/// A tolerant reader of a CLI's help. It finds the sections that list commands (gh "CORE COMMANDS", az
/// "Subgroups:" / "Commands:", aws "AVAILABLE COMMANDS" with "o name" bullets, kubectl "Basic Commands (Beginner):", git's
/// headings under "These are common Git commands") and reads their entry lines. Help it does not understand has no commands:
/// that is not an error, the node is a leaf.
/// </summary>
public static partial class HelpParser
{
    /// <summary>A word of a command path: what the walker may pass as an argument.</summary>
    [GeneratedRegex("^[a-z0-9][a-z0-9-]*$")]
    public static partial Regex WordRegex();

    // indent, optional bullet, name, optional (aliases), then either "  description", ": description" or "  : description"; a bare bullet "o name" has none.
    [GeneratedRegex(@"^(?<indent>\s+)(?:[*o+]\s+)?(?<name>(?!o\s)[a-z0-9][a-z0-9-]*)(?:\s*\([^)]*\))?(?:\s*:\s+|\s{2,}:?\s*)(?<desc>\S.*)$")]
    private static partial Regex EntryLine();

    [GeneratedRegex(@"^(?<indent>\s+)o (?<name>[a-z0-9][a-z0-9-]*)\s*$")]
    private static partial Regex BulletOnly();

    [GeneratedRegex(@"^(?<indent>\s*)(?<text>[A-Za-z][A-Za-z0-9 ()/,'.&:-]{1,68}?):?\s*$")]
    private static partial Regex HeadingLine();

    [GeneratedRegex(@"command|subgroup|service|^groups$", RegexOptions.IgnoreCase)]
    private static partial Regex CommandHeading();

    [GeneratedRegex(@"option|flag|argument|example|usage|environment|learn more|help topic|global|synopsis|description|see also|notes?$", RegexOptions.IgnoreCase)]
    private static partial Regex NotCommands();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    public const int MaxHelpChars = 200_000;

    public static ParsedHelp Parse(string? text)
    {
        text = (text ?? "").Replace("\r", "");
        if (text.Length > MaxHelpChars) text = text[..MaxHelpChars];
        var lines = text.Split('\n');
        var summary = FirstLine(lines);
        var found = new List<HelpEntry>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var inSection = false;       // inside a section that lists commands
        var afterCommands = false;   // a commands section was seen: later headings may continue it (git's groups)
        int? entryIndent = null;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd();
            if (line.Length == 0) continue;
            var heading = HeadingLine().Match(line);
            var indent = line.Length - line.TrimStart().Length;
            // A heading starts or ends a section. Entry lines are indented, so an unindented heading-like line is one.
            if (heading.Success && indent == 0 && !EntryLine().IsMatch(line) && !line.TrimEnd().EndsWith('.'))
            {
                var title = Regex.Replace(heading.Groups["text"].Value, @"\s*\([^)]*\)", "").Trim();
                if (CommandHeading().IsMatch(title) && !NotCommands().IsMatch(title)) { inSection = true; afterCommands = true; entryIndent = null; }
                else if (afterCommands && !NotCommands().IsMatch(title) && NextNonBlankIsEntry(lines, i)) { inSection = true; entryIndent = null; }
                else { inSection = false; if (NotCommands().IsMatch(title)) afterCommands = false; }
                continue;
            }
            if (!inSection) continue;
            if (indent == 0) { inSection = false; continue; } // free text ends it
            var bare = BareName().Match(line);
            if (bare.Success && i + 1 < lines.Length && NextNonBlank(lines, i) is { } desc && desc.Length - desc.TrimStart().Length > indent)
            {
                // gcloud: the name on a line of its own, its description on the next, deeper line.
                entryIndent ??= indent;
                if (indent == entryIndent && seen.Add(bare.Groups["name"].Value))
                    found.Add(new HelpEntry(bare.Groups["name"].Value, Clip(Spaces().Replace(desc.Trim(), " "), 160)));
                continue;
            }
            var m = EntryLine().Match(line);
            var bullet = !m.Success ? BulletOnly().Match(line) : null;
            var hit = m.Success ? m : bullet is { Success: true } ? bullet : null;
            if (hit is null) continue;
            var ind = hit.Groups["indent"].Length;
            entryIndent ??= ind;
            if (ind != entryIndent) continue; // a wrapped description line, not an entry
            var name = hit.Groups["name"].Value;
            if (!seen.Add(name)) continue;
            found.Add(new HelpEntry(name, hit.Groups["desc"].Success ? Clip(Spaces().Replace(hit.Groups["desc"].Value.Trim(), " "), 160) : ""));
        }
        return new ParsedHelp(summary, found);
    }

    [GeneratedRegex(@"^\s+(?<name>[a-z0-9][a-z0-9-]*)\s*$")]
    private static partial Regex BareName();

    private static string? NextNonBlank(string[] lines, int i)
    {
        for (var j = i + 1; j < lines.Length; j++) if (lines[j].Trim().Length > 0) return lines[j].TrimEnd();
        return null;
    }

    private static bool NextNonBlankIsEntry(string[] lines, int i)
    {
        for (var j = i + 1; j < lines.Length; j++)
        {
            if (lines[j].Trim().Length == 0) continue;
            return EntryLine().IsMatch(lines[j]) || BulletOnly().IsMatch(lines[j]);
        }
        return false;
    }

    private static string FirstLine(string[] lines)
    {
        foreach (var l in lines)
        {
            var t = l.Trim();
            if (t.Length == 0 || t.StartsWith("usage", StringComparison.OrdinalIgnoreCase)) continue;
            return Clip(Spaces().Replace(t, " "), 160);
        }
        return "";
    }

    /// <summary>The hash of help text with whitespace collapsed and version/date lines dropped, so a reflow is not a change.</summary>
    public static string Hash(string? text)
    {
        var kept = (text ?? "").Replace("\r", "").Split('\n')
            .Select(l => Spaces().Replace(l.Trim(), " "))
            .Where(l => l.Length > 0 && !Regex.IsMatch(l, @"\bv?\d+\.\d+(\.\d+)?\b|^(version|date|updated)\b", RegexOptions.IgnoreCase))
            .ToList();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', kept)))).ToLowerInvariant();
    }

    public static string Clip(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
}
