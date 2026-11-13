using System.Text.RegularExpressions;

namespace Yawble.Core.Integrations;

public static class Decisions
{
    public const string Allow = "allow", Ask = "ask", Deny = "deny";
    public static readonly IReadOnlyList<string> All = [Allow, Ask, Deny];
}

/// <summary>A rule: an argument pattern and what happens. Layer: floor, team, global, preset or default.</summary>
public sealed record CliRule(string Pattern, string Decision, string Layer, long? Id = null);

/// <summary>A known CLI: how to find and check it, what is never allowed, and its preset.</summary>
/// <param name="Floors">Never run, whatever any rule says: commands that reveal or change the sign-in.</param>
/// <param name="Preset">The default rules the person starts from (read-only commands allowed, changes asking).</param>
/// <param name="LoginArgs">The arguments of the CLI's own sign-in, run in a terminal window the person opens (never by an agent); null: none known.</param>
/// <param name="HelpFlag">How the CLI prints its help (never one that opens a browser: git --help does, on Windows).</param>
public sealed record CliEntry(string Name, string Label, string Description, string[] StatusArgs, string[] Floors, (string Pattern, string Decision)[] Preset,
    IReadOnlyDictionary<string, string>? Env = null, string HelpFlag = "--help", string[]? LoginArgs = null);

/// <summary>
/// The rules of a call: floors first (deny, never loosened), then the team's
/// rules, then the global ones, then the CLI's preset, else ask. Within a layer the most specific pattern wins (the
/// most literal characters); on a tie, deny over ask over allow. A pattern is matched against the arguments joined
/// by spaces, case-insensitively; * matches anything (also nothing). "pr merge*" matches "pr merge 12 --squash".
/// </summary>
public static class CliRules
{
    public static (string Decision, CliRule? Rule) Decide(IReadOnlyList<string> args, CliEntry? entry, IReadOnlyList<CliRule> team, IReadOnlyList<CliRule> global)
    {
        var line = Join(args);
        // The universal credential floor: every CLI, before anything else; no rule, tick or model answer loosens it.
        if (CredentialFloor.Check(args) is { } word) return (Decisions.Deny, new CliRule(CredentialFloor.Reason(word), Decisions.Deny, "floor"));
        if (entry is not null)
        {
            foreach (var floor in entry.Floors)
                if (Matches(floor, line)) return (Decisions.Deny, new CliRule(floor, Decisions.Deny, "floor"));
        }
        foreach (var layer in new[] { team, global, (IReadOnlyList<CliRule>)(entry?.Preset.Select(p => new CliRule(p.Pattern, p.Decision, "preset")).ToList() ?? []) })
        {
            var best = layer.Where(r => Matches(r.Pattern, line))
                .OrderByDescending(r => r.Pattern.Count(c => c != '*'))
                .ThenBy(r => r.Decision switch { Decisions.Deny => 0, Decisions.Ask => 1, _ => 2 })
                .FirstOrDefault();
            if (best is not null) return (best.Decision, best);
        }
        return (Decisions.Ask, null);
    }

    /// <summary>
    /// The decision for an API action: the team's rules, then the global ones, else ask. A pattern is the exact action id ("GET /pets/{petId}"),
    /// compared whole and case-sensitively: no wildcards, no word matching, so "GET /pets" never covers "GET /pets/{petId}". The credential
    /// floor is not applied here (an action id holds spaces and so reads as free text); a credential row is locked by its class instead.
    /// </summary>
    public static (string Decision, CliRule? Rule) DecideExact(string actionId, IReadOnlyList<CliRule> team, IReadOnlyList<CliRule> global)
    {
        foreach (var layer in new[] { team, global })
        {
            var hits = layer.Where(r => string.Equals(r.Pattern, actionId, StringComparison.Ordinal))
                .OrderBy(r => r.Decision switch { Decisions.Deny => 0, Decisions.Ask => 1, _ => 2 }).FirstOrDefault();
            if (hits is not null) return (hits.Decision, hits);
        }
        return (Decisions.Ask, null);
    }

    public static string Join(IReadOnlyList<string> args) => string.Join(' ', args.Select(a => a.Trim()).Where(a => a.Length > 0));

    /// <summary>
    /// Whether a pattern matches a line. A trailing " *" means "these words, alone or followed by more": "pr lock *" matches "pr lock" and
    /// "pr lock 4", never "pr locker" (a checklist row's rule, so it covers only its own command).
    /// </summary>
    public static bool Matches(string pattern, string line)
    {
        var p = pattern.Trim();
        var words = p.Length > 2 && p.EndsWith(" *", StringComparison.Ordinal);
        var body = words ? p[..^2] : p;
        var regex = "^" + string.Join(".*", body.Split('*').Select(Regex.Escape)) + (words ? "( .*)?" : "") + "$";
        return Regex.IsMatch(line, regex, RegexOptions.IgnoreCase | RegexOptions.Singleline, TimeSpan.FromMilliseconds(200));
    }

    /// <summary>A rule the person may save: the pattern as below; an allow rule may not name a credential word (the floor would refuse the call anyway).</summary>
    public static string? Problem(string pattern, string decision) =>
        Problem(pattern) ?? (decision == Decisions.Allow && CredentialFloor.CheckPattern(pattern) is { } word
            ? $"the word \"{word}\" reveals or changes a sign-in: such a command is never allowed" : null);

    /// <summary>A pattern the person may save: not empty, no line breaks, at most 200 characters.</summary>
    public static string? Problem(string pattern) =>
        string.IsNullOrWhiteSpace(pattern) ? "a rule needs a pattern, e.g. pr merge*"
        : pattern.Length > 200 ? "a pattern is at most 200 characters"
        : pattern.IndexOfAny(['\r', '\n']) >= 0 ? "a pattern is one line" : null;
}

/// <summary>The CLIs the app knows well. Others can be enabled by name; they start from "ask for everything".</summary>
public static class CliCatalog
{
    public static readonly IReadOnlyList<CliEntry> Known =
    [
        new("gh", "GitHub CLI", "Pull requests, issues, checks and releases on GitHub, with your own GitHub sign-in.",
            ["auth", "status"],
            Floors: ["auth token*", "auth status *--show-token*", "auth status *-t*", "auth login*", "auth logout*", "auth refresh*", "auth setup-git*",
                     "auth switch*", "secret *", "ssh-key *", "gpg-key *", "config set*", "extension install*", "alias set*",
                     // It runs in the team's repository on the person's disk: nothing that opens a browser on their screen or
                     // changes that checkout (the app's Git dialog does that).
                     "*--web*", "* -w", "* -w *", "browse*", "pr checkout*", "repo clone*", "repo sync*", "repo set-default*"],
            Preset:
            [
                ("pr list*", Decisions.Allow), ("pr view*", Decisions.Allow), ("pr status*", Decisions.Allow), ("pr checks*", Decisions.Allow),
                ("pr diff*", Decisions.Allow), ("issue list*", Decisions.Allow), ("issue view*", Decisions.Allow), ("issue status*", Decisions.Allow),
                ("repo view*", Decisions.Allow), ("run list*", Decisions.Allow), ("run view*", Decisions.Allow), ("release list*", Decisions.Allow),
                ("release view*", Decisions.Allow), ("search *", Decisions.Allow), ("status*", Decisions.Allow), ("workflow list*", Decisions.Allow),
                ("workflow view*", Decisions.Allow), ("label list*", Decisions.Allow), ("--version", Decisions.Allow),
                ("pr create*", Decisions.Ask), ("pr comment*", Decisions.Ask), ("issue create*", Decisions.Ask), ("issue comment*", Decisions.Ask),
                ("pr merge*", Decisions.Ask), ("api *", Decisions.Ask),
                ("repo delete*", Decisions.Deny), ("release delete*", Decisions.Deny), ("repo archive*", Decisions.Deny),
                // the built-in tools, decided by their own rows (BuiltinRows): reading the team branch's PRs runs, opening one asks
                ("pr_status*", Decisions.Allow), ("pr_open*", Decisions.Ask),
            ],
            Env: new Dictionary<string, string> { ["GH_PROMPT_DISABLED"] = "1", ["GH_NO_UPDATE_NOTIFIER"] = "1", ["NO_COLOR"] = "1", ["GH_PAGER"] = "" },
            LoginArgs: ["auth", "login"]),
        // git on the host runs in the PERSON's checkout of a team repository (its base branch): reading it is useful (which
        // commit main is at, what changed), changing it is never the team's: the Git dialog lands work on main, and every
        // agent has git in its own clone for its own work. So reads run, every write is refused, and nothing that reaches
        // for a credential or a config can run at all.
        new("git", "Git", "Read the person's own checkout of a team repository: its commit, log, status and diffs. It never changes it.",
            ["--version"],
            Floors: ["credential*", "config*", "*credential.helper*", "-c *", "* -c *", "--config-env*", "*--exec-path*", "*core.sshCommand*",
                     "*core.hooksPath*", "*-C *", "--git-dir*", "--work-tree*", "*askpass*"],
            Preset:
            [
                ("rev-parse*", Decisions.Allow), ("log*", Decisions.Allow), ("status*", Decisions.Allow), ("show*", Decisions.Allow),
                ("diff*", Decisions.Allow), ("ls-files*", Decisions.Allow), ("describe*", Decisions.Allow), ("rev-list*", Decisions.Allow),
                ("merge-base*", Decisions.Allow), ("cat-file*", Decisions.Allow), ("blame*", Decisions.Allow), ("shortlog*", Decisions.Allow),
                ("for-each-ref*", Decisions.Allow), ("branch --list*", Decisions.Allow), ("branch -a*", Decisions.Allow), ("branch -v*", Decisions.Allow),
                ("branch", Decisions.Allow), ("grep*", Decisions.Allow), ("version*", Decisions.Allow), ("--version", Decisions.Allow),
                // Everything that changes the checkout, its refs or a remote: the person's (the Git dialog), never the team's.
                ("push*", Decisions.Deny), ("pull*", Decisions.Deny), ("fetch*", Decisions.Deny), ("commit*", Decisions.Deny),
                ("merge*", Decisions.Deny), ("rebase*", Decisions.Deny), ("reset*", Decisions.Deny), ("checkout*", Decisions.Deny),
                ("switch*", Decisions.Deny), ("restore*", Decisions.Deny), ("clean*", Decisions.Deny), ("stash*", Decisions.Deny),
                ("tag*", Decisions.Deny), ("remote*", Decisions.Deny), ("add*", Decisions.Deny), ("rm*", Decisions.Deny), ("mv*", Decisions.Deny),
                ("cherry-pick*", Decisions.Deny), ("revert*", Decisions.Deny), ("am*", Decisions.Deny), ("apply*", Decisions.Deny),
                ("update-ref*", Decisions.Deny), ("worktree*", Decisions.Deny), ("submodule*", Decisions.Deny), ("gc*", Decisions.Deny),
                ("branch -d*", Decisions.Deny), ("branch -D*", Decisions.Deny), ("branch -m*", Decisions.Deny), ("branch -M*", Decisions.Deny),
                ("branch -f*", Decisions.Deny), ("filter-branch*", Decisions.Deny), ("replace*", Decisions.Deny), ("notes*", Decisions.Deny),
                ("bisect*", Decisions.Deny), ("init*", Decisions.Deny), ("clone*", Decisions.Deny), ("archive*", Decisions.Deny),
            ],
            Env: new Dictionary<string, string> { ["GIT_TERMINAL_PROMPT"] = "0", ["GIT_PAGER"] = "cat", ["PAGER"] = "cat", ["GCM_INTERACTIVE"] = "never" },
            HelpFlag: "-h"),
        new("az", "Azure CLI", "Azure resources with your own Azure sign-in: list and show, deployments and logs.",
            ["account", "show", "--output", "json"],
            Floors: ["account get-access-token*", "*keys list*", "*list-keys*", "*keyvault secret show*", "*keyvault secret download*", "*credential*",
                     "*show-connection-string*", "*list-publishing-*", "login*", "logout*", "account clear*", "*secret list*"],
            Preset:
            [
                ("* list*", Decisions.Allow), ("* show*", Decisions.Allow), ("account show*", Decisions.Allow), ("version*", Decisions.Allow),
                ("* logs*", Decisions.Allow), ("* delete*", Decisions.Deny),
            ],
            // (On Windows az runs in Python's UTF-8 mode: CliRunner.AzureCliDirect. Env vars would not reach it: az.cmd
            // starts Python isolated, -I, which ignores PYTHON* variables.)
            Env: new Dictionary<string, string> { ["AZURE_CORE_ONLY_SHOW_ERRORS"] = "1", ["AZURE_CORE_NO_COLOR"] = "1", ["AZURE_CORE_COLLECT_TELEMETRY"] = "no" },
            LoginArgs: ["login"]),
    ];

    public static CliEntry? Of(string name) => Known.FirstOrDefault(k => string.Equals(k.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The checklist rows the app ships for a CLI's built-in tools (gh: pr_open, pr_status), shown with origin "builtin".</summary>
    public static IReadOnlyList<(string Path, string Summary, string Class)> BuiltinRows(string cli) =>
        string.Equals(cli, "gh", StringComparison.OrdinalIgnoreCase)
            ? [("pr_open", "open a PR from the team branch", CommandClass.Change), ("pr_status", "the team branch's PRs", CommandClass.ReadOnly)]
            : [];

    /// <summary>
    /// A CLI the app does not know: help and version allowed, everything else asks. Its help is asked with --help only, never -h
    /// (Ruling 11: "shutdown -h" hibernates the computer; an unknown program's short flags mean anything).
    /// </summary>
    public static CliEntry Generic(string name) => new(name, name, $"The {name} command on this computer.", ["--version"], [],
        [("--version", Decisions.Allow), ("version", Decisions.Allow), ("--help", Decisions.Allow), ("* --help", Decisions.Allow)], HelpFlag: "--help");

    /// <summary>System and destructive programs that can never be added as a CLI integration (Ruling 11), compared without .exe/.cmd/.bat.</summary>
    private static readonly HashSet<string> Denied = new(StringComparer.OrdinalIgnoreCase)
    {
        "shutdown", "restart", "format", "diskpart", "bcdedit", "reg", "del", "erase", "rd", "rmdir", "rm", "taskkill", "sc", "net", "vssadmin",
        "wmic", "cipher", "cmd", "powershell", "pwsh", "msiexec", "schtasks", "takeown", "icacls",
    };

    /// <summary>Why a name cannot be added as a CLI integration (a system or destructive program), or null.</summary>
    public static string? Refused(string name)
    {
        var n = (name ?? "").Trim();
        foreach (var ext in new[] { ".exe", ".cmd", ".bat" })
            if (n.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) { n = n[..^ext.Length]; break; }
        return Denied.Contains(n)
            ? $"'{name}' is a system program that can shut down, erase or reconfigure this computer: it cannot be added as an integration"
            : null;
    }

    /// <summary>Commands that pass anything through to an API (gh api, az rest, any "api", "rest" or "graphql"): remembered as the exact line only.</summary>
    private static readonly HashSet<string> PassthroughWords = new(StringComparer.OrdinalIgnoreCase) { "api", "rest", "graphql" };

    /// <summary>Whether a row path is (or is under) a passthrough command, for any CLI (gh's api and az's rest included).</summary>
    public static bool IsPassthrough(string path) => path.Split(' ', StringSplitOptions.RemoveEmptyEntries).Any(PassthroughWords.Contains);
}
