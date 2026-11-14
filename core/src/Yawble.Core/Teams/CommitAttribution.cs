using System.Text.RegularExpressions;

namespace Yawble.Core.Teams;

/// <summary>One commit whose message carries AI attribution: its SHA and the offending lines.</summary>
public sealed record AttributedCommit(string Sha, IReadOnlyList<string> Lines);

/// <summary>
/// Finds AI attribution lines in commit messages: a <c>Co-Authored-By:</c> trailer naming an AI vendor, model or tool,
/// a <c>Claude-Session:</c> trailer, and a <c>Generated with ...</c> line naming one. A human co-author
/// (<c>Co-Authored-By: Jane &lt;jane@x&gt;</c>) is left alone. Pure text: the caller reads the messages from git.
/// The app refuses work that carries these and never rewrites it (see <see cref="RefusalText"/>).
/// </summary>
public static partial class CommitAttribution
{
    // Vendors, models, tools and their domains, caught on the word. Word-bounded, so "Ivan Grokhov" or "Codexia" are not
    // caught. Claude is here by the spec (card 134, item 3) although it is also a first name: "Claude Dupont" is caught.
    private const string Ai = @"(?:claude|anthropic|openai|chatgpt|gpt-?\d[\w.-]*|codex|copilot|github\s+copilot|grok|xai|x\.ai|gemini|"
        + @"antigravity|deepmind|mistral|cursor|aider|windsurf|codeium|tabnine|amazon\s+q|codewhisperer|llama|"
        + @"opus|sonnet|haiku|claude\.(?:ai|com)|cognition\.ai|cognition-labs\.com)";

    // Tools named like people ("Devin Smith", "Cody Jones", "Bard Olsen" are humans): caught only with a clear AI
    // signal right after the name - "AI", "bot", "Code", a [bot] account, a model name or a version number
    // ("Devin AI", "devin-ai-integration[bot]", "Cody Bot"). Their vendor domains are in Ai above.
    private const string Named = @"(?:devin|cody|bard)"
        + @"(?:[\s_-]*(?:ai|bot|code)(?![a-z])|\[bot\]|\s+(?:opus|sonnet|haiku|fable)(?![\w])|\s+\d)";

    [GeneratedRegex(@"^\s*Co-Authored-By\s*:(?<who>.*)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CoAuthor();

    [GeneratedRegex(@"(?<![\w])(?:" + Ai + @"(?![\w])|" + Named + ")", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AiName();

    [GeneratedRegex(@"^\s*Claude-Session\s*:", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Session();

    // "🤖 Generated with [Claude Code](https://...)": only an optional 🤖 before it (a "- Generated with" bullet in prose
    // is not a trailer), then an AI name after it.
    [GeneratedRegex(@"^(?:🤖\s*)?Generated\s+with\b(?<what>.*)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex GeneratedWith();

    /// <summary>The lines of <paramref name="message"/> that are AI attribution (trimmed), in order; empty when none.</summary>
    public static IReadOnlyList<string> Find(string message)
    {
        var found = new List<string>();
        foreach (var raw in message.ReplaceLineEndings("\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            var co = CoAuthor().Match(line);
            var gen = GeneratedWith().Match(line);
            if ((co.Success && AiName().IsMatch(co.Groups["who"].Value))
                || Session().IsMatch(line)
                || (gen.Success && AiName().IsMatch(gen.Groups["what"].Value)))
                found.Add(line);
        }
        return found;
    }

    /// <summary>Whether <paramref name="message"/> carries any AI attribution line.</summary>
    public static bool Has(string message) => Find(message).Count > 0;

    /// <summary>The record separator <see cref="ParseLog"/> expects between commits (<c>git log --format=%H%x00%B%x1e</c>).</summary>
    public const string LogFormat = "--format=%H%x00%B%x1e";

    /// <summary>The commits in a <see cref="LogFormat"/> log that carry AI attribution, in log order.</summary>
    public static IReadOnlyList<AttributedCommit> ParseLog(string log)
    {
        var list = new List<AttributedCommit>();
        foreach (var record in log.Split('\x1e'))
        {
            var parts = record.Split('\0', 2);
            if (parts.Length < 2) continue;
            var lines = Find(parts[1]);
            if (lines.Count > 0) list.Add(new AttributedCommit(parts[0].Trim(), lines));
        }
        return list;
    }

    /// <summary>
    /// What the member (and through the run's failure, its Manager) is told when its work is refused: which commits,
    /// which lines, and that it must amend its own unpushed commits. The app never strips or rewrites them.
    /// </summary>
    public static string RefusalText(string memberName, string branch, IReadOnlyList<AttributedCommit> commits)
    {
        var shown = string.Join("; ", commits.Take(5).Select(c => $"{c.Sha[..Math.Min(12, c.Sha.Length)]}: \"{string.Join("\", \"", c.Lines)}\""));
        var more = commits.Count > 5 ? $" and {commits.Count - 5} more" : "";
        return $"{memberName}'s new work on {branch} was not brought into the team repository: commit messages must carry no AI "
            + $"attribution lines (Co-Authored-By naming an AI, Claude-Session, Generated with), and these do: {shown}{more}. "
            + $"{memberName} must amend its own unpushed commits to remove those lines (git commit --amend for the last one; for older "
            + "ones, git reset --soft to the commit before them and commit again), then end its run so the work is fetched again. "
            + "The app never rewrites commits: nothing was changed.";
    }
}
