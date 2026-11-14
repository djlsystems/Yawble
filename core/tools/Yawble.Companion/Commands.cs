using System.Text.Json;
using System.Text.Json.Nodes;

namespace Yawble.Companion;

/// <summary>
/// The CLI's tool commands: each is one companion tool call with its arguments. Words after the team (tell), the question
/// (answer), the card (board comment) or the title's first word (backlog add) are the text, joined by spaces, taken as they
/// are: an option-looking word there is text. Elsewhere yawble's own options may stand, and `--` ends the options.
/// </summary>
internal static class Commands
{
    /// <summary>The tool commands, for the usage text.</summary>
    public const string Usage = """
          status                                   the whole system at a glance
          doctor                                   the app's health check
          teams                                    every team
          team [show] <name>                       one team's agents, queue and open decisions
          backlog [list] [--archived]              the backlog
          backlog show <B>                         one backlog item in full
          backlog add [--body T] [--team T] <title>   (options first: the rest is the title)
          backlog edit <B> [--title T] [--body T] [--team T]
          backlog ready|archive|restore <B>
          backlog dispatch <B> --team <team>
          board [--team T]                         board cards by lane
          board move <card> <lane> [--note T]      lanes: todo, in-progress, blocked, done
          board comment <card> <text>
          tell <team> <instruction>                give a team's Manager new work
          answer <team> <question> <answer>        answer an open decision
          pause|resume <team>
          git [<team>]                             git state per team
          git delete-branches <team> [--branches a,b] [--remote] [--repo R]   (approved in the window)
          git prune <team> [--repo R]                                         (approved in the window)
          git push|sync <team> <branch> [--repo R]                            (approved in the window)
          git merge <team> [--repo R]                                         (approved in the window)
          workflow close <team> <id> [--reason T]                             (approved in the window)
          team delete <name>                                                  (approved in the window)
          backup list
          backup restore <name>                                               (approved in the window)
          call <tool> [--arg k=v]... [--args-json '{...}']   any companion tool by name

        Free text (the instruction, answer, comment or title) is sent exactly as typed: an option-looking word or a `--`
        inside it is part of the text, so yawble's own options go before the command.
        """;

    public static bool TryMap(string[] args, out string tool, out JsonObject toolArgs, out string? error) =>
        TryMap(args, new GlobalOptions(), out tool, out toolArgs, out error);

    /// <summary>
    /// As <see cref="TryMap(string[], out string, out JsonObject, out string?)"/>, taking yawble's own options
    /// (<paramref name="globals"/>) wherever a command's option may stand, never inside its free text or after `--`.
    /// </summary>
    public static bool TryMap(string[] args, GlobalOptions globals, out string tool, out JsonObject toolArgs, out string? error)
    {
        tool = "";
        toolArgs = [];
        error = null;
        try
        {
            (tool, toolArgs) = Map(args, globals);
            return true;
        }
        catch (UsageException e)
        {
            error = e.Message;
            return false;
        }
    }

    private static (string, JsonObject) Map(string[] args, GlobalOptions g)
    {
        if (args.Length == 0) throw new UsageException("no command given (yawble help lists them)");
        var sub = args.Length > 1 ? args[1] : null;
        switch (args[0])
        {
            case "status": return Plain("system_status", args, 1, g);
            case "doctor": return Plain("doctor", args, 1, g);
            case "teams": return Plain("team_list", args, 1, g);
            case "team":
            {
                if (sub == "show")
                {
                    var s = Parse(g, args, 2, [], []);
                    s.Need(1, 1, "team show <name>");
                    return ("team_status", new JsonObject { ["team"] = s.Pos[0] });
                }
                if (sub == "delete")
                {
                    var p = Parse(g, args, 2, [], []);
                    p.Need(1, 1, "team delete <name>");
                    return ("team_delete", new JsonObject { ["team"] = p.Pos[0] });
                }
                var q = Parse(g, args, 1, [], []);
                q.Need(1, 1, "team <name>");
                return ("team_status", new JsonObject { ["team"] = q.Pos[0] });
            }
            case "backlog": return Backlog(args, g);
            case "board": return Board(args, g);
            case "tell":
            {
                var p = Parse(g, args, 1, [], [], textAfter: 1);
                p.Need(2, int.MaxValue, "tell <team> <instruction>");
                return ("tell", new JsonObject { ["team"] = p.Pos[0], ["instruction"] = p.Join(1) });
            }
            case "answer":
            {
                var p = Parse(g, args, 1, [], [], textAfter: 2);
                p.Need(3, int.MaxValue, "answer <team> <question> <answer>");
                return ("answer", new JsonObject { ["team"] = p.Pos[0], ["question"] = Number(p.Pos[1], "question"), ["answer"] = p.Join(2) });
            }
            case "pause":
            case "resume":
            {
                var p = Parse(g, args, 1, [], []);
                p.Need(1, 1, args[0] + " <team>");
                return (args[0] == "pause" ? "team_pause" : "team_resume", new JsonObject { ["team"] = p.Pos[0] });
            }
            case "git": return Git(args, g);
            case "workflow":
            {
                if (sub != "close") throw new UsageException("workflow close <team> <id> [--reason T]");
                var p = Parse(g, args, 2, ["--reason"], []);
                p.Need(2, 2, "workflow close <team> <id> [--reason T]");
                var o = new JsonObject { ["team"] = p.Pos[0], ["workflow"] = Number(p.Pos[1], "workflow") };
                p.Copy(o, "--reason", "reason");
                return ("workflow_close", o);
            }
            case "backup":
            {
                if (sub == "list") return Plain("backups_list", args, 2, g);
                if (sub != "restore") throw new UsageException("backup list | backup restore <name>");
                var p = Parse(g, args, 2, [], []);
                p.Need(1, 1, "backup restore <name>");
                return ("backup_restore", new JsonObject { ["backup"] = p.Pos[0] });
            }
            case "call": return Call(args, g);
            default: throw new UsageException($"'{args[0]}' is not a yawble command (yawble help lists them)");
        }
    }

    private static (string, JsonObject) Plain(string tool, string[] args, int start, GlobalOptions g)
    {
        Parse(g, args, start, [], []).Need(0, 0, args[0]);
        return (tool, []);
    }

    private static (string, JsonObject) Backlog(string[] args, GlobalOptions g)
    {
        var sub = args.Length > 1 ? args[1] : "list";
        if (args.Length > 1 && args[1].StartsWith("--", StringComparison.Ordinal)) sub = "list";
        var start = args.Length > 1 && sub == args[1] ? 2 : 1;
        switch (sub)
        {
            case "list":
            {
                var p = Parse(g, args, start, [], ["--archived"]);
                p.Need(0, 0, "backlog list [--archived]");
                var o = new JsonObject();
                if (p.Flags.Contains("--archived")) o["archived"] = true;
                return ("backlog_list", o);
            }
            case "show":
            case "ready":
            case "archive":
            case "restore":
            {
                var p = Parse(g, args, 2, [], []);
                p.Need(1, 1, $"backlog {sub} <B>");
                var tool = sub == "show" ? "backlog_item" : "backlog_" + sub;
                return (tool, new JsonObject { ["item"] = p.Pos[0] });
            }
            case "add":
            {
                // Options first: from the title's first word on, everything is the title.
                var p = Parse(g, args, 2, ["--body", "--team"], [], textAfter: 1);
                p.Need(1, int.MaxValue, "backlog add [--body T] [--team T] <title>");
                // "Fix login --team alpha" almost surely means the option, not a title ending in it: say where options go.
                if (p.Pos.Skip(1).Any(w => w is "--body" or "--team" or "--"))
                    throw new UsageException("options go before the title (backlog add [--body T] [--team T] <title>)");
                var o = new JsonObject { ["title"] = p.Join(0) };
                p.Copy(o, "--body", "body");
                p.Copy(o, "--team", "team");
                return ("backlog_add", o);
            }
            case "edit":
            {
                var p = Parse(g, args, 2, ["--title", "--body", "--team"], []);
                p.Need(1, 1, "backlog edit <B> [--title T] [--body T] [--team T]");
                var o = new JsonObject { ["item"] = p.Pos[0] };
                p.Copy(o, "--title", "title");
                p.Copy(o, "--body", "body");
                p.Copy(o, "--team", "team");
                return ("backlog_edit", o);
            }
            case "dispatch":
            {
                var p = Parse(g, args, 2, ["--team"], []);
                p.Need(1, 1, "backlog dispatch <B> --team <team>");
                if (!p.Values.TryGetValue("--team", out var team)) throw new UsageException("backlog dispatch needs --team <team>");
                return ("backlog_dispatch", new JsonObject { ["item"] = p.Pos[0], ["team"] = team });
            }
            default:
                throw new UsageException($"'backlog {sub}' is not a command: list, show, add, edit, ready, dispatch, archive or restore");
        }
    }

    private static (string, JsonObject) Board(string[] args, GlobalOptions g)
    {
        var sub = args.Length > 1 ? args[1] : null;
        switch (sub)
        {
            case "move":
            {
                var p = Parse(g, args, 2, ["--note"], []);
                p.Need(2, 2, "board move <card> <lane> [--note T]");
                var o = new JsonObject { ["card"] = Number(p.Pos[0], "card"), ["lane"] = p.Pos[1] };
                p.Copy(o, "--note", "note");
                return ("board_move", o);
            }
            case "comment":
            {
                var p = Parse(g, args, 2, [], [], textAfter: 1);
                p.Need(2, int.MaxValue, "board comment <card> <text>");
                return ("board_comment", new JsonObject { ["card"] = Number(p.Pos[0], "card"), ["text"] = p.Join(1) });
            }
            default:
            {
                var p = Parse(g, args, 1, ["--team"], []);
                p.Need(0, 0, "board [--team T] | board move ... | board comment ...");
                var o = new JsonObject();
                p.Copy(o, "--team", "team");
                return ("board_list", o);
            }
        }
    }

    private static (string, JsonObject) Git(string[] args, GlobalOptions g)
    {
        var sub = args.Length > 1 ? args[1] : null;
        switch (sub)
        {
            case "delete-branches":
            {
                var p = Parse(g, args, 2, ["--branches", "--repo"], ["--remote"]);
                p.Need(1, 1, "git delete-branches <team> [--branches a,b] [--remote] [--repo R]");
                var o = new JsonObject { ["team"] = p.Pos[0] };
                p.Copy(o, "--branches", "branches");
                if (p.Flags.Contains("--remote")) o["remote"] = true;
                p.Copy(o, "--repo", "repo");
                return ("git_branches_delete", o);
            }
            case "prune":
            case "merge":
            {
                var p = Parse(g, args, 2, ["--repo"], []);
                p.Need(1, 1, $"git {sub} <team> [--repo R]");
                var o = new JsonObject { ["team"] = p.Pos[0] };
                p.Copy(o, "--repo", "repo");
                return (sub == "prune" ? "git_prune" : "git_merge_team", o);
            }
            case "push":
            case "sync":
            {
                var p = Parse(g, args, 2, ["--repo"], []);
                p.Need(2, 2, $"git {sub} <team> <branch> [--repo R]");
                var o = new JsonObject { ["team"] = p.Pos[0], ["branch"] = p.Pos[1] };
                p.Copy(o, "--repo", "repo");
                return (sub == "push" ? "git_push_branch" : "git_sync_branch", o);
            }
            default:
            {
                var p = Parse(g, args, 1, [], []);
                p.Need(0, 1, "git [<team>]");
                return ("team_git", p.Pos.Count == 1 ? new JsonObject { ["team"] = p.Pos[0] } : []);
            }
        }
    }

    private static (string, JsonObject) Call(string[] args, GlobalOptions g)
    {
        if (args.Length < 2 || !IsToolName(args[1])) throw new UsageException("call <tool> [--arg k=v]... [--args-json '{...}']");
        JsonObject? fromJson = null;
        var pairs = new List<(string, string)>();
        for (var i = 2; i < args.Length; i++)
        {
            var a = args[i];
            if (g.TryTake(args, ref i)) continue;
            if (a is not ("--arg" or "--args-json")) throw new UsageException($"call takes only --arg k=v and --args-json, not '{a}'");
            if (i + 1 >= args.Length) throw new UsageException($"{a} needs a value");
            var value = args[++i];
            if (a == "--args-json")
            {
                if (fromJson is not null) throw new UsageException("--args-json is given once");
                try
                {
                    fromJson = JsonNode.Parse(value, null, new JsonDocumentOptions { AllowDuplicateProperties = false }) as JsonObject
                               ?? throw new UsageException("--args-json must be a JSON object");
                    Walk(fromJson);
                }
                catch (Exception e) when (e is JsonException or ArgumentException or InvalidOperationException)
                {
                    throw new UsageException($"--args-json is not a JSON object ({e.Message})");
                }
            }
            else
            {
                var eq = value.IndexOf('=');
                if (eq <= 0) throw new UsageException($"--arg takes key=value, not '{value}'");
                pairs.Add((value[..eq], value[(eq + 1)..]));
            }
        }
        var o = fromJson ?? [];
        foreach (var (k, v) in pairs) o[k] = v;
        return (args[1], o);
    }

    /// <summary>Touches every nested object so a duplicate key anywhere is refused now.</summary>
    private static void Walk(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var (_, child) in o) Walk(child);
                break;
            case JsonArray a:
                foreach (var child in a) Walk(child);
                break;
        }
    }

    private static bool IsToolName(string s) => s.Length is > 0 and <= 64 && s.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_');

    private static JsonNode Number(string text, string what) =>
        long.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n)
            ? JsonValue.Create(n)
            : throw new UsageException($"the {what} is a number, not '{text}'");

    private sealed class Parsed
    {
        public List<string> Pos { get; } = [];
        public Dictionary<string, string> Values { get; } = [];
        public HashSet<string> Flags { get; } = [];

        public void Need(int min, int max, string usage)
        {
            if (Pos.Count < min || Pos.Count > max) throw new UsageException("usage: yawble-companion " + usage);
        }

        public string Join(int from) => string.Join(' ', Pos.Skip(from));

        public void Copy(JsonObject o, string option, string name)
        {
            if (Values.TryGetValue(option, out var v)) o[name] = v;
        }
    }

    private static Parsed Parse(GlobalOptions g, string[] args, int start, string[] valueOptions, string[] flagOptions, int textAfter = int.MaxValue)
    {
        var p = new Parsed();
        var textOnly = false;
        for (var i = start; i < args.Length; i++)
        {
            var a = args[i];
            // The free text starts after textAfter words: from there on every word is the text's, options and `--` too.
            if (textOnly || p.Pos.Count >= textAfter || !a.StartsWith("--", StringComparison.Ordinal))
            {
                p.Pos.Add(a);
                continue;
            }
            if (a == "--")
            {
                textOnly = true;
                continue;
            }
            if (g.TryTake(args, ref i)) continue;
            var eq = a.IndexOf('=');
            var name = eq > 0 ? a[..eq] : a;
            if (flagOptions.Contains(name) && eq < 0)
            {
                p.Flags.Add(name);
                continue;
            }
            if (!valueOptions.Contains(name)) throw new UsageException($"'{name}' is not an option of '{args[0]}'");
            string value;
            if (eq > 0) value = a[(eq + 1)..];
            else if (i + 1 < args.Length) value = args[++i];
            else throw new UsageException($"{name} needs a value");
            if (!p.Values.TryAdd(name, value)) throw new UsageException($"{name} is given more than once");
        }
        return p;
    }
}
