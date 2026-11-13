namespace Yawble.Core.Integrations;

/// <summary>What a command does, as far as it can be told from its name.</summary>
public static class CommandClass
{
    public const string ReadOnly = "readonly", Change = "change", Destructive = "destructive", Credential = "credential", Group = "group", Unknown = "unknown";
    public static readonly IReadOnlyList<string> All = [ReadOnly, Change, Destructive, Credential, Group, Unknown];
}

/// <summary>Where a classification came from. Only floor, preset and verb may pre-tick a node.</summary>
public static class ClassSource
{
    public const string Floor = "floor", Preset = "preset", Verb = "verb", Annotation = "annotation", Method = "method", Model = "model", None = "none";
}

public sealed record Classification(string Class, string Source, string Reason);

/// <summary>
/// The fixed order, first hit wins: 1 the universal credential floor, 2 the known CLI's own floors and preset, 3 the verb of
/// the last word. A group (a node with sub-commands) carries no class of its own, it is a header, unless a floor hits it.
/// What is left is unknown, for the model fallback (ModelClassifier) or the person.
/// </summary>
public static class CommandClassifier
{
    internal static readonly HashSet<string> ReadOnlyVerbs = new(StringComparer.Ordinal)
    {
        "list", "ls", "show", "get", "describe", "view", "status", "info", "version", "help", "diff", "log", "logs", "search", "find", "query",
        "check", "validate", "lint", "inspect", "history", "explain", "tree", "top", "cat", "blame", "grep", "whoami", "wait", "watch",
    };

    // export and download are NOT read-only: they write files.
    internal static readonly HashSet<string> ChangeVerbs = new(StringComparer.Ordinal)
    {
        "create", "add", "new", "set", "put", "update", "edit", "patch", "apply", "merge", "push", "pull", "deploy", "start", "stop", "restart",
        "run", "exec", "invoke", "send", "publish", "upload", "import", "export", "download", "install", "enable", "disable", "rename", "move", "mv",
        "copy", "cp", "attach", "detach", "scale", "rollout", "sync", "login", "commit", "checkout", "clone", "init", "tag", "write", "rebase",
        "restore", "stash", "switch", "fetch", "cherry", "revert", "build", "cancel", "approve", "close", "reopen", "lock", "unlock", "label",
        "assign", "tag", "annotate", "taint", "cordon", "drain", "mount", "unmount", "reboot", "resize", "backup", "grant",
    };

    internal static readonly HashSet<string> DestructiveVerbs = new(StringComparer.Ordinal)
    {
        "delete", "rm", "remove", "destroy", "purge", "drop", "terminate", "kill", "prune", "reset", "wipe", "uninstall", "revoke", "deregister",
        "clean", "erase", "truncate", "discard",
    };

    public static Classification Classify(string cliName, CliEntry? entry, string path, bool hasChildren)
    {
        var words = path.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        // 1. the universal credential floor, the same code the agent's call goes through
        if (CredentialFloor.Check(words) is { } word)
            return new Classification(CommandClass.Credential, ClassSource.Floor, $"word \"{word}\" reveals or changes a sign-in: never allowed (universal credential floor)");
        // 2. the known CLI's own floors and preset
        if (entry is not null)
        {
            foreach (var floor in entry.Floors)
            {
                if (!CliRules.Matches(floor, path) && !CliRules.Matches(floor, path + " x")) continue;
                var refusedOnly = floor.Contains("web", StringComparison.Ordinal) || floor.Contains("-w", StringComparison.Ordinal) || floor.Contains("browse")
                    || floor.Contains("checkout") || floor.Contains("clone") || floor.Contains("sync") || floor.Contains("set-default");
                return new Classification(refusedOnly ? CommandClass.Destructive : CommandClass.Credential, ClassSource.Floor,
                    $"the built-in rule \"{floor}\" of {entry.Name} refuses it");
            }
        }
        if (hasChildren) return new Classification(CommandClass.Group, ClassSource.None, "a group of commands: only its commands are classified");
        if (entry is not null)
        {
            foreach (var (pattern, decision) in entry.Preset.OrderByDescending(p => p.Pattern.Count(c => c != '*')))
            {
                if (!CliRules.Matches(pattern, path) && !CliRules.Matches(pattern, path + " x")) continue;
                var cls = decision switch { Decisions.Allow => CommandClass.ReadOnly, Decisions.Deny => CommandClass.Destructive, _ => CommandClass.Change };
                return new Classification(cls, ClassSource.Preset, $"from the built-in rules of {entry.Name}: \"{pattern}\" is {decision}");
            }
        }
        // 3. the verb of the last word (first word of a hyphenated one: get-object -> get)
        var last = words.Length == 0 ? "" : words[^1];
        var parts = last.Split('-', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return new Classification(CommandClass.Unknown, ClassSource.None, "no command word");
        if (last.StartsWith("force-", StringComparison.Ordinal) || parts.Any(p => DestructiveVerbs.Contains(p)))
        {
            var v = last.StartsWith("force-", StringComparison.Ordinal) ? "force-*" : parts.First(p => DestructiveVerbs.Contains(p));
            return new Classification(CommandClass.Destructive, ClassSource.Verb, $"verb \"{v}\" is destructive");
        }
        if (ReadOnlyVerbs.Contains(parts[0])) return new Classification(CommandClass.ReadOnly, ClassSource.Verb, $"verb \"{parts[0]}\" matches read-only");
        if (ChangeVerbs.Contains(parts[0])) return new Classification(CommandClass.Change, ClassSource.Verb, $"verb \"{parts[0]}\" changes something");
        return new Classification(CommandClass.Unknown, ClassSource.None, "the name does not say what it does");
    }
}
