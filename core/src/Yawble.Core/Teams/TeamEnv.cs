using System.Text.Json;
using System.Text.RegularExpressions;

namespace Yawble.Core.Teams;

/// <summary>
/// A team's environment variables: names and values given to every run of the team's members. The app's
/// own variables always win, and the names the app sets for a run (HOME, PATH, the sandbox's folders, the gateway and
/// sign-in variables) cannot be given at all. Plain text, so for coordination, not confinement: never a production key.
/// </summary>
public static partial class TeamEnv
{
    public const int MaxEntries = 64;
    public const int MaxValueLength = 4096;

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex NamePattern();

    private static readonly string[] ReservedPrefixes = ["YAWBLE_", "ANTHROPIC_", "CLAUDE_", "CODEX_", "OPENAI_", "GROK_", "XAI_", "COPILOT_", "GITHUB_", "GH_", "GIT_", "DOTNET_", "NUGET_", "XDG_", "MSBUILD"];

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "HOME", "PATH", "PATHEXT", "USERPROFILE", "APPDATA", "LOCALAPPDATA", "TEMP", "TMP", "TMPDIR", "SYSTEMROOT", "WINDIR",
        "SYSTEMDRIVE", "PROGRAMDATA", "ALLUSERSPROFILE", "PROGRAMFILES", "PROGRAMFILES(X86)", "COMMONPROGRAMFILES", "LANG",
        "HTTP_PROXY", "HTTPS_PROXY", "NO_PROXY", "ALL_PROXY", "SSL_CERT_FILE", "NODE_EXTRA_CA_CERTS", "UseSharedCompilation",
    };

    public static bool Reserved(string name) =>
        ReservedNames.Contains(name) || ReservedPrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    /// <summary>What is wrong with a set of variables, one line each; empty when it may be saved. Nothing is dropped silently.</summary>
    public static IReadOnlyList<string> Problems(IReadOnlyList<KeyValuePair<string, string>> entries)
    {
        var problems = new List<string>();
        if (entries.Count > MaxEntries) problems.Add($"at most {MaxEntries} variables");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in entries)
        {
            if (!NamePattern().IsMatch(name)) problems.Add($"'{name}' is not a variable name (letters, digits and _, not starting with a digit)");
            else if (Reserved(name)) problems.Add($"'{name}' is set by the app for every run and cannot be given here");
            else if (!seen.Add(name)) problems.Add($"'{name}' is given twice");
            if (value.Length > MaxValueLength) problems.Add($"'{name}' is longer than {MaxValueLength} characters");
        }
        return problems;
    }

    /// <summary>
    /// What a viewer through the Cloud Link gets in place of every value: the names are shown, the values stay on the
    /// computer. Sent back unchanged on a save, it means "keep the value this variable has".
    /// </summary>
    public const string HiddenValue = "(set on the computer)";

    /// <summary>The variables as a Cloud Link viewer sees them: every name, each value replaced by <see cref="HiddenValue"/>.</summary>
    public static IReadOnlyDictionary<string, string> Hidden(IReadOnlyDictionary<string, string> env) =>
        env.ToDictionary(e => e.Key, _ => HiddenValue, StringComparer.Ordinal);

    /// <summary>
    /// A Cloud Link viewer's save, with the values it never saw put back: an entry whose value is <see cref="HiddenValue"/>
    /// keeps the stored value of the same name. One with no stored value under that name (a new or renamed variable) is a
    /// problem: the hidden text is never saved as a value, and a hidden value is never copied to another name.
    /// </summary>
    public static (List<KeyValuePair<string, string>> Entries, List<string> Problems) KeepHidden(
        IReadOnlyList<KeyValuePair<string, string>> entries, IReadOnlyDictionary<string, string> stored)
    {
        var kept = new List<KeyValuePair<string, string>>();
        var problems = new List<string>();
        foreach (var (name, value) in entries)
        {
            if (value != HiddenValue) kept.Add(new(name, value));
            else if (stored.TryGetValue(name, out var real)) kept.Add(new(name, real));
            else problems.Add($"'{name}' has no value yet: its value is hidden here, so give it a new one");
        }
        return (kept, problems);
    }

    public static IReadOnlyDictionary<string, string> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new Dictionary<string, string>();
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? [];
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>();
        }
    }

    public static string Serialize(IEnumerable<KeyValuePair<string, string>> entries) =>
        JsonSerializer.Serialize(entries.ToDictionary(e => e.Key, e => e.Value, StringComparer.Ordinal));
}
