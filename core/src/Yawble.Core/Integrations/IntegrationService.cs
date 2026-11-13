using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Yawble.Core.Bus;
using Yawble.Core.Ipc;

namespace Yawble.Core.Integrations;

/// <summary>An enabled integration as stored.</summary>
public sealed record IntegrationRow(long Id, string Kind, string Name, string? Path, string? Version, string? Manifest, string? Status, bool EnabledAll);

/// <summary>A call waiting for the person, or decided.</summary>
public sealed record AskRow(long Id, string At, string Integration, long TeamId, string Member, IReadOnlyList<string> Args, string Reason,
    long Workflow, long CausationSeq, string State, string? DecidedBy, string? Note, string? Repo = null, string? Action = null);

/// <summary>What an agent's tool call returned: the text it reads and, for a website screenshot, a PNG.</summary>
public sealed record ToolReply(string Text, byte[]? Png);

/// <summary>
/// Integrations: the person's own CLIs, enabled for every team or
/// some, offered to those teams' agents as tools of the app's MCP server, and run on this computer under rules -
/// allowed calls run at once; asking ones wait for the person, who approves (once, or always for the team) or declines,
/// and the agent is woken with the result in its workflow; denied ones never run. Every call is logged with who
/// started the work.
/// </summary>
public sealed partial class IntegrationService(string connectionString, BusEngine bus, TimeProvider? clock = null,
    Connections.ConnectionService? connections = null, Func<HttpMessageHandler>? httpHandler = null,
    Func<HttpMessageHandler>? apiCallHandler = null, Func<HttpMessageHandler>? mcpHandler = null,
    IBrowser? browser = null, Func<IRedirectReceiver>? redirect = null,
    Func<IReadOnlyList<McpConfigSource>>? mcpSources = null, IWebsiteHost? websites = null)
{
    /// <summary>The shared sign-ins (null in a host that has no credential store: the connection methods then say so).</summary>
    private Connections.ConnectionService Conns => connections ?? throw new RpcException("unavailable", "connections are not available here");

    /// <summary>A fresh handler for reading a spec: follows a few redirects. Task 5's calls use their own, non-redirecting one.</summary>
    private HttpMessageHandler NewSpecHandler() => (httpHandler ?? (() => new SocketsHttpHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 5 }))();

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <summary>The team's model for Discover's fallback classification (command path and one-line summary only); the host sets it, null = off.</summary>
    public ICommandModel? Model { get; set; }

    private DiscoverService? _discover;

    /// <summary>Discover: walk a CLI's help, the checklist, and its ticks as rules.</summary>
    public DiscoverService Discover => _discover ??= new DiscoverService(connectionString, () => Model, _clock);

    private JsonNode DiscoverStart(JsonObject? p)
    {
        var name = Str(p, "name");
        var mcp = IsMcp(p);
        var api = IsApi(p);
        var started = api ? Discover.StartApi(name, ct => ReadApiSpecAsync(name, ct)) : mcp ? Discover.StartMcp(name, ct => ListAndStoreToolsAsync(name, ct)) : Discover.Start(name);
        var status = DiscoverService.ToJson(api ? Discover.ApiStatus(name) : mcp ? Discover.McpStatus(name) : Discover.Status(name));
        status["started"] = started;
        return status;
    }

    private JsonNode SaveChecklist(JsonObject? p)
    {
        var mcp = IsMcp(p);
        var api = IsApi(p);
        var name = Str(p, "name");
        SaveResult r;
        if ((mcp || api) && p?["choices"] is not JsonArray) throw new RpcException("bad_params", "send 'choices' (path and state per tool)");
        if (p?["choices"] is JsonArray choices)
        {
            // the per-row states win over a ticked list; a team must be offered the integration (or already have choices on it)
            var list = choices.Select(n => new RowChoice(ChoiceText(n, "path"), ChoiceText(n, "state"))).ToList();
            var team = OptLong(p, "teamId");
            if (api)
            {
                if (team is { } at) Discover.CheckApiTeamOffered(name, at);
                r = Discover.SaveApiChoices(name, team, list);
            }
            else if (mcp)
            {
                if (team is { } mt) Discover.CheckMcpTeamOffered(name, mt);
                r = Discover.SaveMcpChoices(name, team, list);
            }
            else
            {
                if (team is { } t) Discover.CheckTeamOffered(name, t);
                r = Discover.SaveChoices(name, team, list);
            }
        }
        else
        {
            var ticked = (p?["ticked"] as JsonArray)?.Select(n => n?.GetValue<string>() ?? "").ToList() ?? throw new RpcException("bad_params", "send 'choices' (path and state per row) or 'ticked' (a list of command paths)");
            r = Discover.SaveChecklist(name, OptLong(p, "teamId"), ticked);
        }
        return new JsonObject { ["ok"] = true, ["allowed"] = r.Allowed, ["asked"] = r.Asked, ["denied"] = r.Denied, ["removed"] = r.Removed };
    }

    /// <summary>A choice's path or state: a string, else bad_params (never an InvalidOperationException from GetValue).</summary>
    private static string ChoiceText(JsonNode? choice, string field) =>
        choice is JsonObject o && o[field] is JsonValue v && v.TryGetValue<string>(out var s) ? s
        : throw new ArgumentException($"each choice needs '{field}' as text");

    private JsonNode ChecklistJson(JsonObject? p)
    {
        var mcp = IsMcp(p);
        var name = Str(p, "name");
        var team = OptLong(p, "teamId");
        if (IsApi(p))
        {
            if (team is { } at) Discover.CheckApiTeamOffered(name, at);
            return DiscoverService.ToJson(Discover.GetApiChecklist(name, team));
        }
        if (mcp)
        {
            if (team is { } mt) Discover.CheckMcpTeamOffered(name, mt);
            return DiscoverService.ToJson(Discover.GetMcpChecklist(name, team));
        }
        if (team is { } t) Discover.CheckTeamOffered(name, t);
        return DiscoverService.ToJson(Discover.GetChecklist(name, team));
    }

    /// <summary>The kind of a checklist call: "cli" (the default) or "mcp".</summary>
    private static bool IsMcp(JsonObject? p) => OptStr(p, "kind") switch
    {
        null or "cli" => false,
        "mcp" => true,
        "api" => false,
        var kind => throw new RpcException("bad_params", $"'{kind}' has no checklist here yet (kind: cli or mcp)"),
    };

    /// <summary>Whether a checklist or Discover call is about an API (kind "api").</summary>
    private static bool IsApi(JsonObject? p) => OptStr(p, "kind") == "api";

    public static bool Handles(string method) => method.StartsWith("integrations.", StringComparison.Ordinal);

    /// <summary>Host tasks: their tools, their asks (integration "task:&lt;name&gt;") and the person's tasks.* RPCs. Set by the host at start.</summary>
    public Tasks.HostTaskRpc? HostTasks { get; set; }

    // ------------------------------------------------------------------ the screens

    public async Task<JsonNode?> DispatchAsync(string method, JsonObject? p, CancellationToken ct)
    {
        try
        {
            if (IsMcpMethod(method)) return await DispatchMcpAsync(method, p, ct);
            if (IsApiMethod(method)) return await DispatchApiAsync(method, p, ct);
            if (IsWatchMethod(method)) return await DispatchWatchAsync(method, p, ct);
            return method switch
            {
                "integrations.list" => await ListAsync(ct),
                "integrations.detect" => await DetectAsync(Str(p, "name"), ct),
                "integrations.enable" => await EnableAsync(Str(p, "name"), p?["all"]?.GetValue<bool>(), OptLong(p, "teamId"), p?["on"]?.GetValue<bool>() ?? true, ct),
                "integrations.remove" => Remove(Str(p, "name")),
                "integrations.saveRule" => SaveRule(Str(p, "name"), OptLong(p, "teamId"), Str(p, "pattern"), Str(p, "decision")),
                "integrations.deleteRule" => DeleteRule(OptLong(p, "id") ?? throw new RpcException("bad_params", "which rule?")),
                "integrations.asks" => AsksJson(Asks(OptLong(p, "teamId"), pendingOnly: true)),
                "integrations.taskRuns" => HostTasks is null ? new JsonArray() : HostTasks.Runs(OptLong(p, "teamId"), (int)(OptLong(p, "limit") ?? 50)),
                "integrations.approve" => await ApproveAsync(OptLong(p, "id") ?? throw new RpcException("bad_params", "which request?"), ApproveRemember(p), AllTeams(p), ct),
                "integrations.decline" => await DeclineAsync(OptLong(p, "id") ?? throw new RpcException("bad_params", "which request?"), OptStr(p, "note"), DeclineRemember(p), AllTeams(p), ct),
                "integrations.calls" => Calls((int)(OptLong(p, "limit") ?? 50)),
                "integrations.discover" => DiscoverStart(p),
                "integrations.discoverStatus" => DiscoverService.ToJson(IsApi(p) ? Discover.ApiStatus(Str(p, "name")) : IsMcp(p) ? Discover.McpStatus(Str(p, "name")) : Discover.Status(Str(p, "name"))),
                "integrations.discoverCancel" => new JsonObject { ["ok"] = IsApi(p) ? Discover.ApiCancel(Str(p, "name")) : IsMcp(p) ? Discover.McpCancel(Str(p, "name")) : Discover.Cancel(Str(p, "name")) },
                "integrations.checklist" => ChecklistJson(p),
                "integrations.saveChecklist" => SaveChecklist(p),
                "integrations.openLogin" => OpenLogin(Str(p, "name")),
                _ => throw new RpcException("unknown_method", $"no method named '{method}'"),
            };
        }
        catch (ArgumentException e)
        {
            throw new RpcException("bad_params", e.Message);
        }
    }

    private async Task<JsonNode> ListAsync(CancellationToken ct)
    {
        var teams = await bus.TeamsAsync(ct);
        var rows = Rows();
        var list = new JsonArray();
        var names = CliCatalog.Known.Select(k => k.Name).Concat(rows.Select(r => r.Name)).Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var name in names)
        {
            var entry = CliCatalog.Of(name) ?? CliCatalog.Generic(name);
            var row = rows.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));
            var teamIds = row is null ? [] : TeamsOf(row.Id);
            list.Add(new JsonObject
            {
                ["name"] = entry.Name, ["label"] = entry.Label, ["description"] = entry.Description, ["known"] = CliCatalog.Of(name) is not null,
                ["login"] = entry.LoginArgs is { Length: > 0 } la ? $"{entry.Name} {string.Join(' ', la)}" : null,
                ["enabled"] = row is not null && (row.EnabledAll || teamIds.Count > 0), ["all"] = row?.EnabledAll ?? false,
                ["teams"] = new JsonArray(teamIds.Select(t => (JsonNode?)t).ToArray()),
                ["path"] = row?.Path ?? CliRunner.Find(name), ["version"] = row?.Version, ["status"] = row?.Status,
                ["floors"] = new JsonArray(entry.Floors.Select(f => (JsonNode?)f).ToArray()),
                ["configured"] = row is not null,
                ["counts"] = row is null ? null : CountsJson(() => Discover.GetChecklist(name, null)),
                ["preset"] = new JsonArray(entry.Preset.Select(x => (JsonNode?)new JsonObject { ["pattern"] = x.Pattern, ["decision"] = x.Decision }).ToArray()),
                ["rules"] = row is null ? new JsonArray() : new JsonArray(Rules(row.Id).Select(r => (JsonNode?)new JsonObject
                {
                    ["id"] = r.Rule.Id, ["teamId"] = r.TeamId, ["team"] = teams.FirstOrDefault(t => t.Id == r.TeamId)?.Name,
                    ["pattern"] = r.Rule.Pattern, ["decision"] = r.Rule.Decision,
                }).ToArray()),
            });
        }
        return new JsonObject { ["integrations"] = list, ["pendingAsks"] = PendingCount() };
    }

    /// <summary>The allowed / ask first / off / undecided totals of a checklist; null when it cannot be built (not set up, never discovered), so a list never fails for one integration.</summary>
    private static JsonNode? CountsJson(Func<Checklist> get)
    {
        try
        {
            var c = get().Counts;
            return new JsonObject { ["allowed"] = c.Allowed, ["ask"] = c.Ask, ["off"] = c.Off, ["undecided"] = c.Undecided };
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or KeyNotFoundException) { return null; }
    }

    /// <summary>Opens a terminal window on this computer for the person to sign a CLI in (Windows only). Never from an agent: only the catalog and the stored path are used.</summary>
    private JsonNode OpenLogin(string name)
    {
        var entry = CliCatalog.Of(name) ?? CliCatalog.Generic(CheckName(name));
        var path = Rows().FirstOrDefault(r => string.Equals(r.Name, entry.Name, StringComparison.OrdinalIgnoreCase))?.Path ?? CliRunner.Find(entry.Name);
        var start = LoginTerminal.Build(path, entry, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        if (!OperatingSystem.IsWindows())
            throw new RpcException("unsupported", $"Opening a terminal is Windows-only: run '{start.Command ?? entry.Name}' in your own terminal");
        using var _ = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(start.FileName, start.Arguments)
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = start.WorkingDirectory,
        });
        return new JsonObject { ["ok"] = true, ["command"] = start.Command };
    }

    /// <summary>What a status command's output says, as stored and shown: "signed in: ..." or "not signed in: ...".</summary>
    public static string StatusText(CliOutput status) => status.ExitCode == 0
        ? "signed in: " + Clip(StatusSummary(status.Stdout) ?? FirstLine(status), 200)
        : "not signed in: " + Clip((status.Stderr + status.Stdout).Trim(), 300);

    private static string FirstLine(CliOutput o) => (o.Stdout + o.Stderr).Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";

    /// <summary>The CLI entry for a name that may become an integration: a catalog CLI, or an unknown one that is not a system program (Ruling 11).</summary>
    private static CliEntry AddableEntry(string name) => CliCatalog.Of(name) ?? CliCatalog.Generic(DiscoverService.CheckAddable(name));

    /// <summary>
    /// Whether a CLI with this name is already an integration detected here: a row with a stored path to a program that exists
    /// (a row a saved rule added, never detected, does not count). The Cloud Link lets a viewer detect, enable, discover it or
    /// save its rules; detect then runs that stored program, the one its calls run.
    /// </summary>
    public bool HasIntegration(string name) => Rows().Any(r => r.Kind == "cli" && string.Equals(r.Name, (name ?? "").Trim(), StringComparison.OrdinalIgnoreCase)
        && r.Path is { Length: > 0 } p && File.Exists(p));

    /// <summary>Looks at a CLI again: where it is, its version, whether it is signed in (its own status command), its help.</summary>
    private async Task<JsonNode> DetectAsync(string name, CancellationToken ct)
    {
        var entry = AddableEntry(name);
        var stored = Rows().FirstOrDefault(r => string.Equals(r.Name, entry.Name, StringComparison.OrdinalIgnoreCase))?.Path;
        // the stored program first (the one the calls run), then PATH
        var path = (stored is { Length: > 0 } && File.Exists(stored) ? stored : null) ?? CliRunner.Find(entry.Name)
            ?? throw new ArgumentException($"{entry.Name} is not installed on this computer (not on PATH)");
        var version = FirstLine(await CliRunner.RunAsync(path, ["--version"], null, entry.Env, TimeSpan.FromSeconds(20), ct));
        var statusText = StatusText(await CliRunner.RunAsync(path, entry.StatusArgs, null, entry.Env, TimeSpan.FromSeconds(30), ct));
        var help = await CliRunner.RunAsync(path, [entry.HelpFlag], null, entry.Env, TimeSpan.FromSeconds(20), ct);
        var manifest = Clip((help.Stdout.Length > 0 ? help.Stdout : help.Stderr).Trim(), 6000);
        var id = Ensure(entry.Name);
        Exec("UPDATE integrations SET path = $p, version = $v, status = $s, manifest = $m, updated_at = $now WHERE id = $id;",
            ("$p", path), ("$v", version), ("$s", statusText), ("$m", manifest), ("$now", Now()), ("$id", id));
        return new JsonObject { ["name"] = entry.Name, ["path"] = path, ["version"] = version, ["status"] = statusText };
    }

    private async Task<JsonNode> EnableAsync(string name, bool? all, long? teamId, bool on, CancellationToken ct)
    {
        var entry = AddableEntry(name);
        var id = Ensure(entry.Name);
        if (Rows().First(r => r.Id == id).Path is null) await DetectAsync(entry.Name, ct);
        if (all is { } a) Exec("UPDATE integrations SET enabled_all = $a, updated_at = $now WHERE id = $id;", ("$a", a ? 1 : 0), ("$now", Now()), ("$id", id));
        if (teamId is { } t)
        {
            Exec(on ? "INSERT OR IGNORE INTO integration_teams (integration_id, team_id) VALUES ($i, $t);" : "DELETE FROM integration_teams WHERE integration_id = $i AND team_id = $t;",
                ("$i", id), ("$t", t));
        }
        return new JsonObject { ["ok"] = true };
    }

    private JsonNode Remove(string name)
    {
        RemoveWithPendingAsks(name, "CLI removed", "DELETE FROM integrations WHERE name = $n;", ("$n", name));
        return new JsonObject { ["ok"] = true };
    }

    private JsonNode SaveRule(string name, long? teamId, string pattern, string decision)
    {
        if (CliRules.Problem(pattern, decision) is { } why) throw new ArgumentException(why);
        if (!Decisions.All.Contains(decision)) throw new ArgumentException("decision is allow, ask or deny");
        var id = Ensure(AddableEntry(name).Name);
        Exec("DELETE FROM integration_rules WHERE integration_id = $i AND team_id IS $t AND pattern = $p COLLATE NOCASE;", ("$i", id), ("$t", teamId), ("$p", pattern.Trim()));
        Exec("INSERT INTO integration_rules (integration_id, team_id, pattern, decision, created_at) VALUES ($i, $t, $p, $d, $now);",
            ("$i", id), ("$t", teamId), ("$p", pattern.Trim()), ("$d", decision), ("$now", Now()));
        return new JsonObject { ["ok"] = true };
    }

    private JsonNode DeleteRule(long id)
    {
        Exec("DELETE FROM integration_rules WHERE id = $id;", ("$id", id));
        return new JsonObject { ["ok"] = true };
    }

    // ------------------------------------------------------------------ the agents' tools

    private const string RepoParameter =
        "Which of the team's repositories the command runs in, by name (your prompt lists them); omit for the main one. It runs in the " +
        "person's own checkout of that repository on its base branch: only work the person has merged, never your branch.";

    /// <summary>
    /// Where a call runs: the person's checkout of the team's main repository (no name) or of one of its others by name
    /// (0022), always on its base branch, so only merged work is ever built or deployed from it. Null: no repository yet.
    /// </summary>
    private (string? Path, string? BaseBranch) RepoFolder(long teamId, string? repo)
    {
        if (bus.Repos is not { } repos) return (null, null);
        if (string.IsNullOrWhiteSpace(repo)) return repos.RepoOf(teamId) is { } main ? (main.Path, main.BaseBranch) : (null, null);
        var extra = repos.ExtrasOf(teamId).FirstOrDefault(e => string.Equals(e.Name, repo.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"your team has no repository named '{repo}' (yours are: {string.Join(", ", repos.ExtrasOf(teamId).Select(e => e.Name).Prepend("the main one"))})");
        return (extra.Path, extra.BaseBranch);
    }

    /// <summary>The integrations enabled for a team, as MCP tools (one per CLI, named after it) plus cli_help.</summary>
    public IReadOnlyList<JsonObject> ToolsFor(long teamId)
    {
        var enabled = EnabledFor(teamId);
        var mcp = McpToolsFor(teamId).ToList();
        mcp.AddRange(ApiToolsFor(teamId));
        mcp.AddRange(WebsiteToolsFor(teamId));
        if (HostTasks is { } hostTasks) mcp.InsertRange(0, hostTasks.ToolsFor(teamId));
        if (enabled.Count == 0) return mcp;
        var tools = enabled.Select(r =>
        {
            var entry = CliCatalog.Of(r.Name) ?? CliCatalog.Generic(r.Name);
            var summary = Clip(r.Manifest ?? "", 1500);
            return Tool(ToolName(r.Name),
                $"Run the person's own `{r.Name}` ({entry.Label}) on their computer, with their sign-in, under the team's rules: some calls run at once, " +
                "some ask the person first (you are woken with the result), some are never allowed. Give the arguments as a list, never a shell line. " +
                "Use cli_commands to see which commands the person has discovered and what each does for your team, and cli_help to read a command's help first.\n\n" + summary,
                ("args", "array", $"The arguments after `{r.Name}`, one per item, e.g. [\"pr\", \"list\", \"--state\", \"open\"].", true),
                ("reason", "string", "Why you need this call, in one line (shown to the person if it asks them).", true),
                ("repo", "string", RepoParameter, false));
        }).ToList();
        if (enabled.Any(r => string.Equals(r.Name, "gh", StringComparison.OrdinalIgnoreCase)))
        {
            // The GitHub built-in: the team branch as a pull request, under the same gh rules (pr create asks by default).
            tools.Add(Tool("pr_open", "Open a pull request on GitHub from your team's branch into the repository's base branch, with your delivery as its description. " +
                "It shows the team branch as last pushed: your run's own commits are pushed when the run ends, and the pull request updates then. Under the gh rules (asks the person unless allowed).",
                ("title", "string", "The pull request's title.", true), ("body", "string", "What it delivers and how it was checked (Markdown).", true),
                ("reason", "string", "Why now, in one line (shown to the person if it asks them).", true), ("repo", "string", RepoParameter, false)));
            tools.Add(Tool("pr_status", "Your team branch's pull requests on GitHub: state, review decision, checks, mergeable, and links.",
                ("repo", "string", RepoParameter, false)));
        }
        tools.AddRange(mcp);
        tools.Add(Tool("cli_help", "Read the help of an integration's command (always allowed, nothing changes): cli_help(cli: \"gh\", args: [\"pr\", \"create\"]).",
            ("cli", "string", $"One of: {string.Join(", ", enabled.Select(r => r.Name))}.", true),
            ("args", "array", "The command to read the help of (empty for the top level).", false)));
        tools.Add(Tool("cli_commands", "List the commands of an integration the person has discovered (their checklist), and what each does for YOUR team right now: " +
            "run (runs at once), ask (asks the person first, give a reason), refused (never allowed; do not try), or never (reveals a sign-in; never allowed). Read this before guessing a command. " +
            "Empty when the CLI was not discovered yet; then use cli_help. cli_commands(cli: \"gh\", prefix: [\"pr\"]).",
            ("cli", "string", $"One of: {string.Join(", ", enabled.Select(r => r.Name))}.", true),
            ("prefix", "array", "Only the commands under this path (empty for all).", false)));
        return tools;
    }

    public bool IsTool(string name) => Tasks.HostTaskRpc.IsTool(name) || name is "cli_help" or "cli_commands" or "pr_open" or "pr_status" || Rows().Any(r => ToolName(r.Name) == name)
        || (name.Contains("__", StringComparison.Ordinal) && McpRows().Any(s => name.StartsWith(ToolName(s.Name) + "__", StringComparison.Ordinal))) || IsApiTool(name) || IsWebsiteTool(name);

    /// <summary>An agent's call: decided by the rules; run now, asked, or refused. The text the agent reads.</summary>
    public async Task<string> CallToolAsync(RunContext run, string tool, JsonObject args, CancellationToken ct) =>
        (await CallToolReplyAsync(run, tool, args, ct)).Text;

    /// <summary>Like <see cref="CallToolAsync"/>, with an image when the tool returned one (a website screenshot).</summary>
    public async Task<ToolReply> CallToolReplyAsync(RunContext run, string tool, JsonObject args, CancellationToken ct) =>
        tool.StartsWith(WebsiteToolPrefix, StringComparison.Ordinal) && WebsiteToolOf(run.Member.TeamId, tool) is { } site
            ? await CallWebsiteAsync(run, site, args, ct)
            : new(await CallToolTextAsync(run, tool, args, ct), null);

    private async Task<string> CallToolTextAsync(RunContext run, string tool, JsonObject args, CancellationToken ct)
    {
        if (Tasks.HostTaskRpc.IsTool(tool))
            return await (HostTasks ?? throw new ArgumentException("host tasks are not running")).CallToolAsync(run, tool, args, ct);
        if (tool.Contains("__", StringComparison.Ordinal) && McpToolOf(run.Member.TeamId, tool) is var (server, mcpTool))
            return await CallMcpAsync(run, server, mcpTool, args, ct);
        if (tool.StartsWith(ApiToolPrefix, StringComparison.Ordinal) && ApiToolOf(run.Member.TeamId, tool) is { } api)
            return await CallApiAsync(run, api, args, ct);
        var enabled = EnabledFor(run.Member.TeamId);
        if (tool == "cli_help")
        {
            var cli = enabled.FirstOrDefault(r => string.Equals(r.Name, args["cli"]?.GetValue<string>(), StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"no integration named '{args["cli"]}' is enabled for your team");
            var helpArgs = Strings(args["args"]).Append((CliCatalog.Of(cli.Name) ?? CliCatalog.Generic(cli.Name)).HelpFlag).ToList();
            var help = await RunAsync(cli, helpArgs, run.Member.TeamId, ct);
            Log(cli.Name, run, helpArgs, "help", null, help);
            return Show(help);
        }
        if (tool == "cli_commands")
        {
            var cli = enabled.FirstOrDefault(r => string.Equals(r.Name, args["cli"]?.GetValue<string>(), StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"no integration named '{args["cli"]}' is enabled for your team");
            return CommandsFor(cli, run.Member.TeamId, Strings(args["prefix"]));
        }
        var reason = args["reason"] is JsonValue rv && rv.TryGetValue<string>(out var rs) && !string.IsNullOrWhiteSpace(rs) ? rs.Trim() : null;
        var repoName = args["repo"] is JsonValue pv && pv.TryGetValue<string>(out var ps) && !string.IsNullOrWhiteSpace(ps) ? ps.Trim() : null;
        var (_, repoBase) = RepoFolder(run.Member.TeamId, repoName); // an unknown name is refused here, before anything runs
        if (tool is "pr_open" or "pr_status")
        {
            var gh = enabled.FirstOrDefault(r => string.Equals(r.Name, "gh", StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException("the GitHub CLI is not enabled for your team");
            var baseBranch = repoBase ?? throw new ArgumentException("your team has no repository here");
            var teamBranch = Teams.TeamRepos.TeamBranchFor(run.Member.Team);
            var ghArgs = tool == "pr_status"
                ? new List<string> { "pr", "list", "--head", teamBranch, "--state", "all", "--limit", "5", "--json", "number,title,state,url,reviewDecision,mergeable,statusCheckRollup" }
                : ["pr", "create", "--head", teamBranch, "--base", baseBranch, "--title", Text(args, "title"), "--body", Text(args, "body")];
            // decided by the built-in's own row (pr_open / pr_status), the floors still checked on the real gh arguments
            return await DecideAndRunAsync(run, gh, ghArgs, tool == "pr_status" ? reason ?? "read the team branch's pull requests" : reason, ct, repoName,
                decideOn: [tool], action: tool);
        }
        var row = enabled.FirstOrDefault(r => ToolName(r.Name) == tool) ?? throw new ArgumentException($"'{tool}' is not enabled for your team");
        var list = Strings(args["args"]);
        if (list.Count == 0) throw new ArgumentException("give the arguments as a list, e.g. [\"pr\", \"list\"]");
        // a built-in's row (gh: pr_open, pr_status) decides only its own tool, never a plain call that names it
        if (CliCatalog.BuiltinRows(row.Name).FirstOrDefault(b => string.Equals(b.Path, list[0], StringComparison.OrdinalIgnoreCase)) is { Path: not null } builtin)
            throw new ArgumentException($"'{builtin.Path}' is a built-in tool, not a {row.Name} command: call the {builtin.Path} tool instead");
        return await DecideAndRunAsync(run, row, list, reason, ct, repoName);
    }

    /// <summary>The discovered commands of a CLI and what the rules decide for this team, from Decide itself (so it cannot disagree with a call).</summary>
    public string CommandsFor(IntegrationRow cli, long teamId, IReadOnlyList<string> prefix)
    {
        const int Cap = 400;
        var entry = CliCatalog.Of(cli.Name) ?? CliCatalog.Generic(cli.Name);
        var team = Rules(cli.Id, teamId);
        var global = Rules(cli.Id, null);
        var lead = string.Join(" ", prefix.Select(p => p.ToLowerInvariant()));
        List<ChecklistNode> nodes;
        try { nodes = Discover.GetChecklist(cli.Name, teamId).Nodes.ToList(); }
        catch (ArgumentException) { nodes = []; }
        var leaves = nodes.Where(n => !n.HasChildren && !n.Gone && (lead.Length == 0 || n.Path == lead || n.Path.StartsWith(lead + " ", StringComparison.Ordinal))).ToList();
        if (leaves.Count == 0)
            return $"No discovered commands for `{cli.Name}`{(lead.Length > 0 ? $" under `{lead}`" : "")}: the person has not run Discover on it (or nothing matches). Use cli_help to read the help.";
        var sb = new System.Text.StringBuilder($"`{cli.Name}` commands and what they do for your team (run = at once, ask = the person is asked, refused/never = not allowed):\n");
        foreach (var n in leaves.Take(Cap))
        {
            var (d, rule) = CliRules.Decide(n.Path.Split(' '), entry, team, global);
            var word = d == Decisions.Allow ? "run" : d == Decisions.Ask ? "ask" : rule?.Layer == "floor" ? "never" : "refused";
            sb.Append("- ").Append(n.Path).Append(" [").Append(word).Append(']');
            if (n.Summary.Length > 0) sb.Append(' ').Append(Clip(n.Summary, 80));
            sb.Append('\n');
        }
        if (leaves.Count > Cap) sb.Append($"... {leaves.Count - Cap} more: narrow with prefix.\n");
        return sb.ToString();
    }

    private static string Text(JsonObject args, string name) =>
        args[name] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s.Trim() : throw new ArgumentException($"'{name}' is required");

    /// <summary>A call decided by the rules: refused, asked (a request for the person), or run now.</summary>
    /// <param name="decideOn">The words the rules decide on (a built-in's name, e.g. ["pr_open"]); the real arguments by default.</param>
    /// <param name="action">The row the ask is remembered under; by default <see cref="DiscoverService.FoundPath"/> of the arguments.</param>
    private async Task<string> DecideAndRunAsync(RunContext run, IntegrationRow row, IReadOnlyList<string> list, string? reason, CancellationToken ct,
        string? repo = null, IReadOnlyList<string>? decideOn = null, string? action = null)
    {
        var entry = CliCatalog.Of(row.Name) ?? CliCatalog.Generic(row.Name);
        var (decision, rule) = CliRules.Decide(decideOn ?? list, entry, Rules(row.Id, run.Member.TeamId), Rules(row.Id, null));
        // The floors are checked on the real arguments too: no row (a built-in's included) loosens them.
        if (decideOn is not null && CliRules.Decide(list, entry, [], []) is { Decision: Decisions.Deny, Rule.Layer: "floor" } floor) (decision, rule) = floor;
        var line = $"{row.Name} {CliRules.Join(list)}{(repo is null ? "" : $" (in {repo})")}";
        switch (decision)
        {
            case Decisions.Deny:
                Log(row.Name, run, list, Decisions.Deny, rule, null);
                return $"refused: `{line}` is not allowed{(rule?.Layer == "floor" ? " (it would reveal or change the person's sign-in)" : rule is null ? "" : $" (rule \"{rule.Pattern}\")")}. Do not try to work around it; say so in your report if you needed it.";
            case Decisions.Ask:
                if (reason is null) throw new ArgumentException("this call asks the person first: give a reason (one line) for it");
                var id = Scalar(
                    """
                    INSERT INTO integration_asks (at, integration, team_id, member, args, reason, workflow, causation_seq, repo, action)
                    VALUES ($at, $i, $t, $m, $a, $r, $w, $c, $repo, $action) RETURNING id;
                    """,
                    ("$at", Now()), ("$i", row.Name), ("$t", run.Member.TeamId), ("$m", run.Member.Qualified), ("$a", JsonSerializer.Serialize(list)),
                    ("$r", Clip(reason, 500)), ("$w", run.Correlation), ("$c", run.Causation), ("$repo", (object?)repo ?? DBNull.Value),
                    ("$action", action ?? Discover.FoundPathOf(row.Name, list)));
                Log(row.Name, run, list, "asked", rule, null);
                return $"asked: `{line}` waits for the person's approval (request {id}). You will be woken with the result in this workflow. Do other work or end your turn; do not ask again.";
            default:
                var output = await RunAsync(row, list, run.Member.TeamId, ct, repo);
                Log(row.Name, run, list, Decisions.Allow, rule, output);
                return Show(output);
        }
    }

    // ------------------------------------------------------------------ the person's approvals

    public int PendingCount(long? teamId = null) =>
        (int)Scalar($"SELECT count(*) FROM integration_asks WHERE state = 'pending'{(teamId is null ? "" : " AND team_id = $t")};", ("$t", teamId));

    /// <summary>What an approval remembers: "once" (nothing), "always" or "ask"; an old shell's always: true is "always" for the team.</summary>
    private static string ApproveRemember(JsonObject? p) => OptStr(p, "remember") switch
    {
        null => p?["always"]?.GetValue<bool>() == true ? RememberChoice.Always : RememberChoice.Once,
        RememberChoice.Once => RememberChoice.Once,
        RememberChoice.Always => RememberChoice.Always,
        RememberChoice.Ask => RememberChoice.Ask,
        var r => throw new RpcException("bad_params", $"'{r}' is not something an approval remembers (once, always or ask)"),
    };

    private static string? DeclineRemember(JsonObject? p) => OptStr(p, "remember") switch
    {
        null => null,
        RememberChoice.Never => RememberChoice.Never,
        var r => throw new RpcException("bad_params", $"'{r}' is not something a decline remembers (never)"),
    };

    /// <summary>The scope of a remembered answer: the asking team (the default) or every team.</summary>
    private static bool AllTeams(JsonObject? p) => OptStr(p, "scope") switch
    {
        null or "team" => false,
        "all" => true,
        var s => throw new RpcException("bad_params", $"'{s}' is not a scope (team or all)"),
    };

    /// <summary>What the agent is told was remembered, after "The person approved (or declined) your call ... (request n)".</summary>
    private static string RememberedText(string? decision, string? path, bool all) => path is null ? "" : decision switch
    {
        Decisions.Allow => $" and allowed `{path}` for {(all ? "every team" : "your team")} from now on",
        Decisions.Ask => $" and will keep asking for `{path}`",
        Decisions.Deny => $" and turned `{path}` off for {(all ? "every team" : "your team")}",
        _ => "",
    };

    private async Task<JsonNode> ApproveAsync(long id, string remember, bool all, CancellationToken ct)
    {
        var ask = Asks(null, pendingOnly: true).FirstOrDefault(a => a.Id == id) ?? throw new ArgumentException($"request {id} is not waiting");
        var who = Provenance.Actor.Current ?? Provenance.Actor.Window;
        if (ask.Integration.StartsWith("task:", StringComparison.Ordinal))
        {
            // Host tasks keep their own "always" (scope does not apply)
            var started = await (HostTasks ?? throw new ArgumentException("host tasks are not running")).Service.ApproveAsync(id, remember == RememberChoice.Always, ct);
            return Tasks.HostTaskRpc.Result(started);
        }
        var decision = remember switch { RememberChoice.Always => Decisions.Allow, RememberChoice.Ask => Decisions.Ask, _ => null };
        long? scope = all ? null : ask.TeamId;
        if (ask.Integration.StartsWith("mcp:", StringComparison.Ordinal))
        {
            var server = McpRow(ask.Integration["mcp:".Length..]);
            var tool = ask.Action ?? ask.Args[0];
            // checked first, written after: a refusal (a tool that reveals a sign-in) leaves the request waiting, and a losing approval saves nothing
            if (decision is not null && server is not null)
            {
                CheckRememberScope(scope);
                if (!Discover.CanAlwaysMcp(server.Name, tool)) throw new ArgumentException($"'{tool}' is locked: it reveals or changes a sign-in and is never allowed");
            }
            // exactly one approval wins: a second one (window and remote at once) finds nothing pending and sends nothing
            if (Exec("UPDATE integration_asks SET state = 'approved', decided_by = $by, decided_at = $now WHERE id = $id AND state = 'pending';",
                ("$by", who.User), ("$now", Now()), ("$id", id)) == 0)
                throw new ArgumentException($"request {id} is not waiting");
            var remembered = decision is not null && server is not null ? RememberAfterApproval(() => Discover.RememberMcp(server.Name, scope, tool, decision)) : null;
            var result = await RunApprovedMcpAsync(ask, ct);
            LogFor(ask, "approved", null, who);
            await Tell(ask, $"The person approved your call to {ask.Integration[4..]}'s {ask.Args[0]} (request {id}){RememberedText(decision, remembered, all)}. It returned:\n\n{result}", ct);
            return new JsonObject { ["ok"] = true, ["remembered"] = remembered };
        }
        if (ask.Integration.StartsWith("api:", StringComparison.Ordinal))
        {
            var api = ApiRows(ask.Integration["api:".Length..]).FirstOrDefault();
            var action = ask.Action ?? ask.Args[0];
            // checked first, written after (as for MCP)
            if (decision is not null && api is not null)
            {
                CheckRememberScope(scope);
                if (ApiRememberProblem(api, action) is { } problem) throw new ArgumentException(problem);
            }
            if (Exec("UPDATE integration_asks SET state = 'approved', decided_by = $by, decided_at = $now WHERE id = $id AND state = 'pending';",
                ("$by", who.User), ("$now", Now()), ("$id", id)) == 0)
                throw new ArgumentException($"request {id} is not waiting");
            var remembered = decision is not null && api is not null ? RememberAfterApproval(() => Discover.RememberApi(api.Name, scope, action, decision)) : null;
            // The decision is recorded; the request is sent in the background on the app's lifetime (never the RPC's token), so the
            // person's click returns at once and a closed window cannot leave the agent unwoken.
            _ = RunApprovedApiInBackgroundAsync(ask, RememberedText(decision, remembered, all), who);
            return new JsonObject { ["ok"] = true, ["started"] = true, ["remembered"] = remembered };
        }
        var row = Rows().FirstOrDefault(r => string.Equals(r.Name, ask.Integration, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"{ask.Integration} is no longer enabled");
        // Remembered first, so a refusal (a row that reveals a sign-in) leaves the request waiting. No row (a flag first): the exact line.
        var path = decision is null ? null : Discover.Remember(row.Name, scope, ask.Args, ask.Action, decision);
        Exec("UPDATE integration_asks SET state = 'approved', decided_by = $by, decided_at = $now WHERE id = $id AND state = 'pending';",
            ("$by", who.User), ("$now", Now()), ("$id", id));
        var note = decision is null ? "" : RememberedText(decision, path ?? CliRules.Join(ask.Args), all);
        // The decision is recorded; the call itself runs in the background (an image build takes minutes), so the person's
        // click returns at once. The agent is told the result in its workflow when it ends.
        _ = RunApprovedAsync(row, ask, note, who);
        return new JsonObject { ["ok"] = true, ["started"] = true, ["remembered"] = path };
    }

    /// <summary>A remembered answer's team must still exist (checked before the request is approved, as Remember would refuse it).</summary>
    private void CheckRememberScope(long? scope)
    {
        if (scope is { } t && Scalar("SELECT count(*) FROM teams WHERE id = $t;", ("$t", t)) == 0) throw new ArgumentException($"no team {t}");
    }

    /// <summary>
    /// Writes the remembered answer once the request is approved. Its validity was checked before; if it changed in between (the row became a
    /// sign-in by a Discover run, the team was removed), the approval still stands and nothing is remembered.
    /// </summary>
    private static string? RememberAfterApproval(Func<string?> remember)
    {
        try { return remember(); }
        catch (ArgumentException) { return null; }
    }

    /// <param name="remembered">What the approval remembered, as the agent reads it (" and allowed `pr lock` for your team from now on"), or "".</param>
    private async Task RunApprovedAsync(IntegrationRow row, AskRow ask, string remembered, Provenance.Actor who)
    {
        string text;
        try
        {
            var output = await RunAsync(row, ask.Args, ask.TeamId, CancellationToken.None, ask.Repo);
            LogFor(ask, "approved", output, who);
            text = $"The person approved your call `{ask.Integration} {CliRules.Join(ask.Args)}` (request {ask.Id}){remembered}. It ran:\n\n{Show(output)}";
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        {
            LogFor(ask, "approved", null, who);
            text = $"The person approved your call `{ask.Integration} {CliRules.Join(ask.Args)}` (request {ask.Id}){remembered}, but it could not run: {e.Message}";
        }
        try { await Tell(ask, text, CancellationToken.None); }
        catch (Exception e) when (e is BusRefusal or InvalidOperationException) { /* the member or team is gone: the call is still recorded */ }
    }

    private async Task<JsonNode> DeclineAsync(long id, string? note, string? remember, bool all, CancellationToken ct)
    {
        var ask = Asks(null, pendingOnly: true).FirstOrDefault(a => a.Id == id) ?? throw new ArgumentException($"request {id} is not waiting");
        var who = Provenance.Actor.Current ?? Provenance.Actor.Window;
        if (ask.Integration.StartsWith("task:", StringComparison.Ordinal))
        {
            await (HostTasks ?? throw new ArgumentException("host tasks are not running")).Service.DeclineAsync(id, note, ct);
            return new JsonObject { ["ok"] = true };
        }
        var mcp = ask.Integration.StartsWith("mcp:", StringComparison.Ordinal);
        var isApi = ask.Integration.StartsWith("api:", StringComparison.Ordinal);
        string? remembered = null;
        long? scope = all ? null : ask.TeamId;
        if (remember == RememberChoice.Never)
        {
            // "never" saves the row off at the scope (a sign-in row writes nothing: the floor refuses it anyway)
            if (mcp)
            {
                if (McpRow(ask.Integration["mcp:".Length..]) is { } server) remembered = Discover.RememberMcp(server.Name, scope, ask.Action ?? ask.Args[0], Decisions.Deny);
            }
            else if (isApi)
            {
                // null for a sign-in action (refused anyway); an API or action that is gone has nothing to remember
                if (ApiRows(ask.Integration["api:".Length..]).FirstOrDefault() is { } api)
                    try { remembered = Discover.RememberApi(api.Name, scope, ask.Action ?? ask.Args[0], Decisions.Deny); }
                    catch (ArgumentException) { remembered = null; }
            }
            else if (Rows().FirstOrDefault(r => string.Equals(r.Name, ask.Integration, StringComparison.OrdinalIgnoreCase)) is { } row)
            {
                remembered = Discover.Remember(row.Name, scope, ask.Args, ask.Action, Decisions.Deny); // null: the exact line (or a sign-in row: nothing)
            }
        }
        Exec("UPDATE integration_asks SET state = 'declined', decided_by = $by, decided_at = $now, note = $note WHERE id = $id AND state = 'pending';",
            ("$by", who.User), ("$now", Now()), ("$note", note), ("$id", id));
        LogFor(ask, "declined", null, who);
        var what = mcp || isApi ? $"to {ask.Integration[4..]}'s {ask.Args[0]}" : $"`{ask.Integration} {CliRules.Join(ask.Args)}`";
        await Tell(ask, $"The person declined your call {what} (request {id}){RememberedText(Decisions.Deny, remembered, all)}" +
            (string.IsNullOrWhiteSpace(note) ? "." : $": {note.Trim()}") + " Do not try it another way; carry on without it or say what you need.", ct);
        return new JsonObject { ["ok"] = true, ["remembered"] = remembered };
    }

    private async Task Tell(AskRow ask, string text, CancellationToken ct)
    {
        var slash = ask.Member.IndexOf('/');
        try { await bus.PersonTellAsync(ask.Member[..slash], ask.Member[(slash + 1)..], text, ask.CausationSeq, ct); }
        catch (BusRefusal) { /* the member or team is gone or archived: the decision is still recorded */ }
    }

    public IReadOnlyList<AskRow> Asks(long? teamId, bool pendingOnly)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id, at, integration, team_id, member, args, reason, workflow, causation_seq, state, decided_by, note, repo, action FROM integration_asks " +
            $"WHERE 1 = 1 {(pendingOnly ? "AND state = 'pending'" : "")} {(teamId is null ? "" : "AND team_id = $t")} ORDER BY id;";
        if (teamId is not null) cmd.Parameters.AddWithValue("$t", teamId);
        using var r = cmd.ExecuteReader();
        var list = new List<AskRow>();
        while (r.Read())
            list.Add(new AskRow(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetInt64(3), r.GetString(4),
                JsonSerializer.Deserialize<List<string>>(r.GetString(5)) ?? [], r.GetString(6), r.GetInt64(7), r.GetInt64(8), r.GetString(9),
                r.IsDBNull(10) ? null : r.GetString(10), r.IsDBNull(11) ? null : r.GetString(11), r.IsDBNull(12) ? null : r.GetString(12), r.IsDBNull(13) ? null : r.GetString(13)));
        return list;
    }

    private JsonNode AsksJson(IReadOnlyList<AskRow> asks) => new JsonArray(asks.Select(a => a.Integration.StartsWith("task:", StringComparison.Ordinal) && HostTasks?.Service.Store.Ask(a.Id) is { } t
        ? HostTasks.AskJson(t) : new JsonObject
    {
        ["id"] = a.Id, ["at"] = a.At, ["integration"] = a.Integration, ["teamId"] = a.TeamId, ["member"] = a.Member,
        ["command"] = a.Integration.StartsWith("mcp:", StringComparison.Ordinal) || a.Integration.StartsWith("api:", StringComparison.Ordinal) ? $"{a.Integration[4..]} › {a.Args[0]} {(a.Args.Count > 1 ? a.Args[1] : "")}" : $"{a.Integration} {CliRules.Join(a.Args)}", ["reason"] = a.Reason, ["workflow"] = a.Workflow, ["repo"] = a.Repo,
        ["action"] = ShownAction(a), ["canAlways"] = CanAlways(a),
    }).ToArray());

    /// <summary>The row the card says it remembers as: a CLI ask's stored action only while it still holds (an older ask's group or short guess is the exact line).</summary>
    private string? ShownAction(AskRow a)
    {
        if (a.Action is null || a.Integration.StartsWith("mcp:", StringComparison.Ordinal) || a.Integration.StartsWith("api:", StringComparison.Ordinal)) return a.Action;
        try { return Discover.ActionOf(a.Integration, a.Args, a.Action); }
        catch (ArgumentException) { return null; }
    }

    /// <summary>Whether the approval card may offer "always allow": the ask has a row to remember it under and the row is not a sign-in.</summary>
    private bool CanAlways(AskRow a)
    {
        try
        {
            return a.Integration.StartsWith("mcp:", StringComparison.Ordinal) ? Discover.CanAlwaysMcp(a.Integration[4..], a.Action ?? a.Args[0])
                : a.Integration.StartsWith("api:", StringComparison.Ordinal) ? CanAlwaysApi(a.Integration[4..], a.Action ?? a.Args[0])
                : Discover.CanAlways(a.Integration, a.Args, a.Action);
        }
        catch (ArgumentException) { return false; }
    }

    // ------------------------------------------------------------------ running and logging

    private async Task<CliOutput> RunAsync(IntegrationRow row, IReadOnlyList<string> args, long teamId, CancellationToken ct, string? repo = null)
    {
        var path = row.Path ?? CliRunner.Find(row.Name) ?? throw new ArgumentException($"{row.Name} is not installed on this computer");
        var entry = CliCatalog.Of(row.Name);
        // In the person's checkout of the team's repository (the one named, else the main one), so a CLI that reads the
        // git remote (gh) knows which repository it is, and one that uploads the folder (az acr build .) sends merged work.
        var cwd = RepoFolder(teamId, repo).Path;
        return await CliRunner.RunAsync(path, args, cwd, entry?.Env, null, ct);
    }

    private static string Show(CliOutput o)
    {
        var sb = new StringBuilder();
        sb.Append(o.TimedOut ? "timed out (stopped)" : $"exit {o.ExitCode}").Append($" in {o.Duration.TotalSeconds:0.0}s");
        if (o.Truncated) sb.Append(" (output cut at 64 KB)");
        sb.AppendLine();
        if (o.Stdout.Length > 0) sb.AppendLine(o.Stdout.TrimEnd());
        if (o.Stderr.Length > 0) sb.AppendLine("stderr:").AppendLine(o.Stderr.TrimEnd());
        return sb.ToString().TrimEnd();
    }

    private void Log(string integration, RunContext run, IReadOnlyList<string> args, string decision, CliRule? rule, CliOutput? output) =>
        Insert(integration, run.Member.Team, run.Member.Qualified, run.Correlation, args, decision, rule is null ? null : $"{rule.Layer}: {rule.Pattern}",
            output, Provenance.Actor.Current);

    private void LogFor(AskRow ask, string decision, CliOutput? output, Provenance.Actor who) =>
        Insert(ask.Integration, ask.Member[..ask.Member.IndexOf('/')], ask.Member, ask.Workflow, ask.Args, decision, null, output, who);

    private void Insert(string integration, string? team, string? member, long? workflow, IReadOnlyList<string> args, string decision, string? rule,
        CliOutput? output, Provenance.Actor? who, SqliteConnection? within = null, SqliteTransaction? tx = null) =>
        Exec(within, tx,
            """
            INSERT INTO integration_calls (at, integration, team, member, workflow, args, decision, rule, exit_code, duration_ms, output_bytes, via, user_name)
            VALUES ($at, $i, $team, $m, $w, $a, $d, $r, $x, $ms, $b, $via, $u);
            """,
            ("$at", Now()), ("$i", integration), ("$team", team), ("$m", member), ("$w", workflow), ("$a", CliRunner.Redact(JsonSerializer.Serialize(args))),
            ("$d", decision), ("$r", rule), ("$x", output?.ExitCode), ("$ms", output is null ? null : (long)output.Duration.TotalMilliseconds),
            ("$b", output?.Bytes), ("$via", who?.Via), ("$u", who?.User));

    private JsonNode Calls(int limit)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT at, integration, team, member, args, decision, rule, exit_code, duration_ms FROM integration_calls ORDER BY id DESC LIMIT $n;";
        cmd.Parameters.AddWithValue("$n", Math.Clamp(limit, 1, 500));
        using var r = cmd.ExecuteReader();
        var list = new JsonArray();
        while (r.Read())
        {
            list.Add(new JsonObject
            {
                ["at"] = r.GetString(0), ["integration"] = r.GetString(1), ["team"] = r.IsDBNull(2) ? null : r.GetString(2),
                ["member"] = r.IsDBNull(3) ? null : r.GetString(3), ["args"] = r.GetString(4), ["decision"] = r.GetString(5),
                ["rule"] = r.IsDBNull(6) ? null : r.GetString(6), ["exitCode"] = r.IsDBNull(7) ? null : r.GetInt64(7),
                ["durationMs"] = r.IsDBNull(8) ? null : r.GetInt64(8),
            });
        }
        return list;
    }

    // ------------------------------------------------------------------ the store

    private IReadOnlyList<IntegrationRow> Rows()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id, kind, name, path, version, manifest, status, enabled_all FROM integrations ORDER BY name;";
        using var r = cmd.ExecuteReader();
        var list = new List<IntegrationRow>();
        while (r.Read())
            list.Add(new IntegrationRow(r.GetInt64(0), r.GetString(1), r.GetString(2), S(r, 3), S(r, 4), S(r, 5), S(r, 6), r.GetInt64(7) == 1));
        return list;
    }

    private IReadOnlyList<long> TeamsOf(long integrationId)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT team_id FROM integration_teams WHERE integration_id = $i ORDER BY team_id;";
        cmd.Parameters.AddWithValue("$i", integrationId);
        using var r = cmd.ExecuteReader();
        var list = new List<long>();
        while (r.Read()) list.Add(r.GetInt64(0));
        return list;
    }

    public IReadOnlyList<IntegrationRow> EnabledFor(long teamId) =>
        Rows().Where(r => r.EnabledAll || TeamsOf(r.Id).Contains(teamId)).ToList();

    private IReadOnlyList<CliRule> Rules(long integrationId, long? teamId) =>
        Rules(integrationId).Where(r => r.TeamId == teamId).Select(r => r.Rule with { Layer = teamId is null ? "global" : "team" }).ToList();

    private IReadOnlyList<(CliRule Rule, long? TeamId)> Rules(long integrationId)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id, team_id, pattern, decision FROM integration_rules WHERE integration_id = $i ORDER BY id;";
        cmd.Parameters.AddWithValue("$i", integrationId);
        using var r = cmd.ExecuteReader();
        var list = new List<(CliRule, long?)>();
        while (r.Read()) list.Add((new CliRule(r.GetString(2), r.GetString(3), "", r.GetInt64(0)), r.IsDBNull(1) ? null : r.GetInt64(1)));
        return list;
    }

    private long Ensure(string name)
    {
        Exec("INSERT OR IGNORE INTO integrations (kind, name, created_at, updated_at) VALUES ('cli', $n, $now, $now);", ("$n", name), ("$now", Now()));
        return Scalar("SELECT id FROM integrations WHERE name = $n;", ("$n", name));
    }

    private static string CheckName(string name)
    {
        var n = (name ?? "").Trim();
        if (!System.Text.RegularExpressions.Regex.IsMatch(n, "^[A-Za-z0-9][A-Za-z0-9._-]{0,40}$"))
            throw new ArgumentException($"'{name}' is not a command name (letters, digits, . _ -)");
        return n;
    }

    public static string ToolName(string name) => new string(name.ToLowerInvariant().Select(ch => char.IsLetterOrDigit(ch) ? ch : '_').ToArray());

    private static IReadOnlyList<string> Strings(JsonNode? node) =>
        node is JsonArray a ? a.Select(n => n?.ToString() ?? "").Where(s => s.Length > 0).ToList()
        : node is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 ? throw new ArgumentException("give the arguments as a list, not one line")
        : [];

    private static JsonObject Tool(string name, string description, params (string Name, string Type, string Description, bool Required)[] parameters)
    {
        var properties = new JsonObject();
        foreach (var p in parameters)
        {
            var prop = new JsonObject { ["type"] = p.Type, ["description"] = p.Description };
            if (p.Type == "array") prop["items"] = new JsonObject { ["type"] = "string" };
            properties[p.Name] = prop;
        }
        return new JsonObject
        {
            ["name"] = name, ["description"] = description,
            ["inputSchema"] = new JsonObject
            {
                ["type"] = "object", ["properties"] = properties,
                ["required"] = new JsonArray(parameters.Where(p => p.Required).Select(p => (JsonNode)JsonValue.Create(p.Name)!).ToArray()),
                ["additionalProperties"] = false,
            },
        };
    }

    /// <summary>
    /// A status command's JSON output (az account show) in a few words: the account's name and who is signed in, e.g.
    /// "Visual Studio Enterprise (a@b.com)". Null when the output is not a JSON object (its first line is used then).
    /// </summary>
    public static string? StatusSummary(string stdout)
    {
        var text = (stdout ?? "").Trim();
        if (!text.StartsWith('{')) return null;
        try
        {
            if (JsonNode.Parse(text) is not JsonObject o) return null;
            static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) && s.Trim().Length > 0 ? s.Trim() : null;
            var account = Str(o["name"]) ?? Str(o["id"]) ?? Str(o["account"]);
            var user = Str(o["user"]?["name"]) ?? Str(o["user"]) ?? Str(o["login"]) ?? Str(o["email"]);
            return (account, user) switch
            {
                (not null, not null) => $"{account} ({user})",
                (not null, null) => account,
                (null, not null) => user,
                _ => null,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
    private static string? S(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
    private string Now() => _clock.GetUtcNow().ToString("O", CultureInfo.InvariantCulture);
    private static string Str(JsonObject? p, string n) => p?[n]?.GetValue<string>() ?? throw new RpcException("bad_params", $"'{n}' is required");
    private static string? OptStr(JsonObject? p, string n) => p?[n] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 ? s : null;
    private static long? OptLong(JsonObject? p, string n) => p?[n] is JsonValue v && Ui.UiRpc.WholeNumber(v, out var l) ? l : null;

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(connectionString);
        c.Open();
        using var fk = c.CreateCommand();
        fk.CommandText = "PRAGMA foreign_keys=ON;";
        fk.ExecuteNonQuery();
        return c;
    }

    private int Exec(string sql, params (string, object?)[] args) => Exec(null, null, sql, args);

    /// <summary>Runs a statement on <paramref name="within"/> (inside <paramref name="tx"/>) when given, else on its own connection.</summary>
    private int Exec(SqliteConnection? within, SqliteTransaction? tx, string sql, params (string, object?)[] args)
    {
        using var own = within is null ? Open() : null;
        var c = within ?? own!;
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (n, v) in args) if (sql.Contains(n, StringComparison.Ordinal)) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        return cmd.ExecuteNonQuery();
    }

    private long Scalar(string sql, params (string, object?)[] args)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in args) if (sql.Contains(n, StringComparison.Ordinal)) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }
}
