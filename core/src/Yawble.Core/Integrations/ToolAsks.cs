using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Yawble.Core.Bus;
using Yawble.Core.Ipc;
using Yawble.Core.Launch;
using Yawble.Core.Teams;

namespace Yawble.Core.Integrations;

/// <summary>
/// One developer tool the app can install for the person (B013). Agents name it (<see cref="Name"/>); everything else
/// is the app's own: an agent never supplies a winget id, a command or an argument.
/// </summary>
/// <param name="Name">What request_tool takes, e.g. "dotnet-sdk-10".</param>
/// <param name="WingetId">The winget package, from the winget source, matched exactly.</param>
/// <param name="UserScope">The package installs for the current user (--scope user); false: it installs for the whole computer and Windows asks.</param>
/// <param name="Program">The program looked for afterwards (Windows adds .exe), the same one HostTools.Detect puts on a run's PATH.</param>
/// <param name="SdkMajor">For the .NET SDK: the major version `dotnet --list-sdks` must list; null: the program alone is enough.</param>
/// <param name="ManualCommand">Linux and macOS: what the ask shows the person to run themselves (the app installs nothing there).</param>
public sealed record ToolEntry(string Name, string DisplayName, string Publisher, string WingetId, bool UserScope, string Program, int? SdkMajor,
    IReadOnlyList<string> Aliases, string LinuxCommand, string MacCommand)
{
    public string ManualCommand(bool mac) => mac ? MacCommand : LinuxCommand;
}

/// <summary>The fixed catalog of host tools an agent may ask for, and the exact winget command each installs with.</summary>
public static class ToolCatalog
{
    /// <summary>The .NET SDK's current long-term-support major version (.NET 10, November 2025 to November 2028).</summary>
    public const int DotNetLtsMajor = 10;

    public static IReadOnlyList<ToolEntry> All { get; } =
    [
        // The .NET SDK installer always installs for the whole computer (it ignores a user scope).
        new("dotnet-sdk-10", ".NET SDK 10", "Microsoft", "Microsoft.DotNet.SDK.10", false, "dotnet", 10,
            ["dotnet-10", ".net sdk 10", "dotnet sdk 10"],
            "sudo apt-get install -y dotnet-sdk-10.0", "brew install --cask dotnet-sdk"),
        new("dotnet-sdk-lts", $".NET SDK (current LTS, {DotNetLtsMajor})", "Microsoft", $"Microsoft.DotNet.SDK.{DotNetLtsMajor}", false, "dotnet", DotNetLtsMajor,
            ["dotnet", "dotnet-sdk", "dotnet-lts", ".net", ".net sdk", "dotnet sdk"],
            $"sudo apt-get install -y dotnet-sdk-{DotNetLtsMajor}.0", "brew install --cask dotnet-sdk"),
        // Node.js's MSI installs for the whole computer.
        new("nodejs-lts", "Node.js LTS", "OpenJS Foundation", "OpenJS.NodeJS.LTS", false, "node", null,
            ["node", "nodejs", "node.js", "node-lts", "npm"],
            "sudo apt-get install -y nodejs npm", "brew install node"),
        // Python's installer supports a per-user install (no permission prompt).
        new("python-3", "Python 3", "Python Software Foundation", "Python.Python.3.14", true, OperatingSystem.IsWindows() ? "python" : "python3", null,
            ["python", "python3", "py", "pip"],
            "sudo apt-get install -y python3 python3-pip", "brew install python@3"),
    ];

    public static IReadOnlyList<string> Names => All.Select(t => t.Name).ToList();

    /// <summary>The entry by its name or an alias (case-insensitive), or null: an unknown tool is never installed.</summary>
    public static ToolEntry? Find(string? name)
    {
        var n = (name ?? "").Trim().ToLowerInvariant();
        if (n.Length == 0) return null;
        return All.FirstOrDefault(t => t.Name == n) ?? All.FirstOrDefault(t => t.Aliases.Contains(n) || t.DisplayName.Equals(n, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Every catalog name that installs the same winget package as <paramref name="t"/> (itself included): one package is
    /// one tool for the ask and the Not now hold, whichever name an agent used (dotnet-sdk-10 and dotnet-sdk-lts).
    /// </summary>
    public static IReadOnlyList<string> SamePackage(ToolEntry t) =>
        All.Where(e => e.WingetId.Equals(t.WingetId, StringComparison.OrdinalIgnoreCase)).Select(e => e.Name).ToList();

    /// <summary>
    /// The winget arguments an Install runs, exactly (the same form as git.install's): install from the winget source by
    /// the exact id, silently, accepting the agreements, with no prompts; --scope user where the package supports it.
    /// </summary>
    public static IReadOnlyList<string> WingetArgs(ToolEntry t) =>
    [
        "install", "--id", t.WingetId, "--exact", "--source", "winget",
        "--silent", "--accept-package-agreements", "--accept-source-agreements", "--disable-interactivity",
        .. t.UserScope ? (string[])["--scope", "user"] : [],
    ];

    /// <summary>The command as the person reads it (in the ask and the logs).</summary>
    public static string CommandLine(ToolEntry t) => "winget " + string.Join(' ', WingetArgs(t));
}

/// <summary>Finding and installing host tools on this computer. Tests pass a fake: no test runs winget or an installer.</summary>
public interface IToolHost
{
    bool IsWindows { get; }

    bool IsMac { get; }

    /// <summary>The full path of winget, or null.</summary>
    string? FindWinget();

    /// <summary>Whether the tool is on this computer as the next run's PATH would find it (HostTools.Detect), with the SDK version when it has one.</summary>
    Task<bool> IsInstalledAsync(ToolEntry tool, CancellationToken ct);

    Task<GitResult> RunAsync(string exe, IReadOnlyList<string> args, TimeSpan limit, CancellationToken ct);
}

/// <summary>The production <see cref="IToolHost"/>: winget and the runner of git.install (<see cref="IGitHost"/>), and the launcher's own tool detection.</summary>
public sealed class SystemToolHost(IGitHost git, string appDataRoot) : IToolHost
{
    public bool IsWindows => git.IsWindows;

    public bool IsMac => OperatingSystem.IsMacOS();

    public string? FindWinget() => git.FindWinget();

    public async Task<bool> IsInstalledAsync(ToolEntry tool, CancellationToken ct)
    {
        var exe = Find(tool.Program);
        if (exe is null) return false;
        if (tool.SdkMajor is not { } major) return true;
        try
        {
            var r = await git.RunAsync(exe, ["--list-sdks"], TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
            return r.ExitCode == 0 && ToolAskService.ListsSdk(r.Stdout, major);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            return false;
        }
    }

    private string? Find(string program)
    {
        if (OperatingSystem.IsWindows())
            return HostTools.Detect(appDataRoot).Select(d => Path.Combine(d, program + ".exe")).FirstOrDefault(File.Exists);
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            var p = Path.Combine(dir, program);
            if (File.Exists(p)) return p;
        }
        return null;
    }

    public Task<GitResult> RunAsync(string exe, IReadOnlyList<string> args, TimeSpan limit, CancellationToken ct) => git.RunAsync(exe, args, limit, ct);
}

/// <summary>A tool ask (tool_asks, 0040). <paramref name="Member"/>: "Team/Name" of the latest member who asked.</summary>
public sealed record ToolAskRow(long Id, string At, long TeamId, string Team, string Tool, string Member, string? Reason, int Count, string LastSeen,
    long? Workflow, long? CausationSeq, string State, int Attempts, string? Error, string? DecidedBy, string? DecidedVia, string? DecidedAt, string? Note)
{
    public string MemberName => Member[(Member.IndexOf('/') + 1)..];
}

/// <summary>
/// Host tool asks (B013): an agent's request_tool names a tool from <see cref="ToolCatalog"/>; the person is asked once
/// per (team, package) in Needs you: catalog names that install the same winget package are one tool for the ask and Not now. Install runs exactly the catalog's winget command in the app's process (the person, in
/// the window on this computer, only: never over the Cloud Link, from the companion, by an agent or the Concierge); the
/// program is looked for again afterwards, and when it is found every open ask for the tool closes and each team's
/// Manager is told to retry. A failed install leaves the ask open with winget's message (Try again). Not now: the team is
/// not asked about that tool again for 24 hours. Each ask, answer and result is in the Approvals log (tool_ask_events).
/// The network asks' plumbing is shared: the same tell (as the person, in the ask's workflow), the same Needs you count
/// and Concierge status, the same Approvals log.
/// </summary>
public sealed class ToolAskService
{
    /// <summary>How long a team is not asked again about a tool after Not now.</summary>
    public static readonly TimeSpan NotNowQuiet = TimeSpan.FromHours(24);

    /// <summary>How long one winget install may take.</summary>
    public static readonly TimeSpan InstallLimit = TimeSpan.FromMinutes(30);

    /// <summary>Pushed when an ask is raised, answered or changes state: Needs you refreshes.</summary>
    public const string ChangedEvent = "tools.asksChanged";

    /// <summary>Pushed while an install runs: { id, tool, phase: installing | checking | installed | failed, message, command }.</summary>
    public const string ProgressEvent = "tools.installProgress";

    public const string InstallRefusal = "a tool is installed by the person, in the app's window on this computer";

    private readonly string _cs;
    private readonly IToolHost _host;
    private readonly NetworkAskTell _tell;
    private readonly TimeProvider _clock;
    private readonly Lock _lock = new();
    private readonly SemaphoreSlim _installing = new(1, 1);

    public ToolAskService(string connectionString, IToolHost host, NetworkAskTell tell, TimeProvider? clock = null)
    {
        _cs = connectionString;
        _host = host;
        _tell = tell;
        _clock = clock ?? TimeProvider.System;
        // An install the app was closed during never finished here: the ask waits again, with Try again, and the
        // Approvals log gets its result (a failed event), so no install there is left without one.
        using var c = Open();
        using var tx = c.BeginTransaction();
        var interrupted = new List<long>();
        using (var cmd = Command(c, "SELECT id FROM tool_asks WHERE state = 'installing' ORDER BY id;"))
        {
            cmd.Transaction = tx;
            using var r = cmd.ExecuteReader();
            while (r.Read()) interrupted.Add(r.GetInt64(0));
        }
        foreach (var id in interrupted)
        {
            Exec(c, tx, "UPDATE tool_asks SET state = 'pending', error = 'the app closed while it was installing; try again' WHERE id = $id AND state = 'installing';", ("$id", id));
            Event(c, tx, id, "failed", null, null, InterruptedDetail);
        }
        tx.Commit();
    }

    /// <summary>The Approvals log's result for an install the app was closed during.</summary>
    public const string InterruptedDetail = "install interrupted: the app closed";

    /// <summary>winget's exit code in decimal and hex, as winget documents them: "winget exit code -1978335212 (0x8A150014)".</summary>
    public static string WingetExitCode(int code) => $"winget exit code {code} (0x{code:X8})";

    public Action? Changed { get; set; }

    /// <summary>Install progress for the window (<see cref="ProgressEvent"/>).</summary>
    public Action<JsonObject>? Progress { get; set; }

    public static bool Handles(string method) => method.StartsWith("tools.", StringComparison.Ordinal);

    /// <summary>Whether `dotnet --list-sdks` output lists an SDK of that major version ("10.0.100 [C:\Program Files\dotnet\sdk]").</summary>
    public static bool ListsSdk(string listSdks, int major) =>
        listSdks.Split('\n', StringSplitOptions.RemoveEmptyEntries).Any(l => l.Trim().StartsWith($"{major}.", StringComparison.Ordinal));

    // ------------------------------------------------------------------ request_tool

    /// <summary>The request_tool bus tool: the agent's answer, as text.</summary>
    public async Task<string> RequestFromAgentAsync(MemberRef member, string tool, string reason, long workflow, long causation, CancellationToken ct)
    {
        var entry = ToolCatalog.Find(tool);
        if (entry is null)
            return $"refused: '{tool.Trim()}' is not a tool the app installs. Ask for one of: {string.Join(", ", ToolCatalog.Names)}. " +
                   "Anything else the person installs themselves: hand back blocked on it and say what it is.";
        if (string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("give a reason: one line on what you need the tool for");
        if (await _host.IsInstalledAsync(entry, ct).ConfigureAwait(false))
            return $"{entry.DisplayName} is already installed on this computer: your next run has it on PATH (a run that started before it was installed does not). " +
                   $"If this run cannot find it, hand back blocked on {entry.DisplayName} so the work is retried in a new run; nothing is asked.";
        var qualified = $"{member.Team}/{member.Name}";
        var clean = Clip(reason.Trim(), 500);
        long id;
        bool first;
        lock (_lock)
        {
            var now = _clock.GetUtcNow();
            using var c = Open();
            // The ask and the Not now hold are per package (winget id), not per catalog name: every name of the same package
            // finds the same ask and the same hold.
            var (inList, inArgs) = InNames(ToolCatalog.SamePackage(entry));
            if (Scalar(c, $"SELECT decided_at FROM tool_asks WHERE team_id = $t AND tool IN ({inList}) AND state = 'dismissed' ORDER BY decided_at DESC LIMIT 1;",
                    [("$t", member.TeamId), .. inArgs]) is string dismissed
                && DateTimeOffset.TryParse(dismissed, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at)
                && now - at < NotNowQuiet)
                return $"refused: the person chose Not now for {entry.DisplayName}; your team is not asked about it again before {at + NotNowQuiet:yyyy-MM-dd HH:mm} UTC. " +
                       "Carry on without it, or hand back blocked on it.";
            // An open ask for the package under any of its names counts up; otherwise a new ask under the name asked for.
            var open = Scalar(c, $"SELECT id FROM tool_asks WHERE team_id = $t AND tool IN ({inList}) AND state IN ('pending', 'installing') ORDER BY id LIMIT 1;",
                [("$t", member.TeamId), .. inArgs]) as long?;
            using var tx = c.BeginTransaction();
            using (var cmd = open is { } openId
                       ? Command(c,
                           """
                           UPDATE tool_asks SET count = count + 1, last_seen = $now, member = $m, reason = $r, workflow = $w, causation_seq = $c
                           WHERE id = $id RETURNING id, count;
                           """,
                           ("$now", Stamp(now)), ("$m", qualified), ("$r", clean), ("$w", workflow), ("$c", causation), ("$id", openId))
                       : Command(c,
                       """
                       INSERT INTO tool_asks (at, team_id, tool, member, reason, count, last_seen, workflow, causation_seq)
                       VALUES ($now, $t, $tool, $m, $r, 1, $now, $w, $c)
                       ON CONFLICT (team_id, tool) WHERE state IN ('pending', 'installing') DO UPDATE SET
                           count = count + 1, last_seen = excluded.last_seen, member = excluded.member, reason = excluded.reason,
                           workflow = excluded.workflow, causation_seq = excluded.causation_seq
                       RETURNING id, count;
                       """,
                       ("$now", Stamp(now)), ("$t", member.TeamId), ("$tool", entry.Name), ("$m", qualified), ("$r", clean), ("$w", workflow), ("$c", causation)))
            {
                cmd.Transaction = tx;
                using var r = cmd.ExecuteReader();
                r.Read();
                id = r.GetInt64(0);
                first = r.GetInt64(1) == 1;
            }
            Event(c, tx, id, first ? "asked" : "asked again", null, Provenance.Actor.Agent, $"{qualified}: {clean}");
            tx.Commit();
        }
        Changed?.Invoke();
        var how = _host.IsWindows
            ? "The person can install it from Needs you"
            : $"The person installs it themselves on this computer ({entry.ManualCommand(_host.IsMac)})";
        return $"asked: the person is asked to install {entry.DisplayName} (tool request {id}){(first ? "" : "; it was already waiting, and your reason is added")}. " +
               $"{how}; it is on PATH from your next run, not this one. Hand back now as blocked on {entry.DisplayName} (say what it is for); " +
               "the Manager is told when it is installed. Do not retry in a loop or install it another way.";
    }

    // ------------------------------------------------------------------ reading

    public int PendingCount() => (int)(long)(Scalar(null, "SELECT count(*) FROM tool_asks WHERE state IN ('pending', 'installing');") ?? 0L);

    public IReadOnlyList<ToolAskRow> Asks(bool openOnly = true, long? teamId = null)
    {
        using var c = Open();
        using var cmd = Command(c,
            "SELECT a.id, a.at, a.team_id, COALESCE(t.name, ''), a.tool, a.member, a.reason, a.count, a.last_seen, a.workflow, a.causation_seq, a.state, " +
            "a.attempts, a.error, a.decided_by, a.decided_via, a.decided_at, a.note FROM tool_asks a LEFT JOIN teams t ON t.id = a.team_id WHERE 1 = 1" +
            (openOnly ? " AND a.state IN ('pending', 'installing')" : "") + (teamId is null ? "" : " AND a.team_id = $t") + " ORDER BY a.id;",
            ("$t", teamId));
        using var r = cmd.ExecuteReader();
        var list = new List<ToolAskRow>();
        while (r.Read())
            list.Add(new ToolAskRow(r.GetInt64(0), r.GetString(1), r.GetInt64(2), r.GetString(3), r.GetString(4), r.GetString(5), S(r, 6), (int)r.GetInt64(7),
                r.GetString(8), r.IsDBNull(9) ? null : r.GetInt64(9), r.IsDBNull(10) ? null : r.GetInt64(10), r.GetString(11), (int)r.GetInt64(12), S(r, 13),
                S(r, 14), S(r, 15), S(r, 16), S(r, 17)));
        return list;
    }

    /// <summary>One catalog entry as the window reads it.</summary>
    public JsonObject ToJson(ToolEntry t) => new()
    {
        ["name"] = t.Name, ["displayName"] = t.DisplayName, ["publisher"] = t.Publisher, ["wingetId"] = t.WingetId,
        ["scope"] = t.UserScope ? "user" : "machine", ["program"] = t.Program,
        ["command"] = _host.IsWindows ? ToolCatalog.CommandLine(t) : t.ManualCommand(_host.IsMac),
    };

    /// <summary>One ask as the Needs you item reads it (tools.asks).</summary>
    public JsonObject ToJson(ToolAskRow a)
    {
        var t = ToolCatalog.Find(a.Tool);
        var canInstall = _host.IsWindows && t is not null;
        return new JsonObject
        {
            ["id"] = a.Id, ["at"] = a.At, ["teamId"] = a.TeamId, ["team"] = a.Team, ["member"] = a.Member, ["memberName"] = a.MemberName,
            ["tool"] = a.Tool, ["displayName"] = t?.DisplayName ?? a.Tool, ["publisher"] = t?.Publisher, ["wingetId"] = t?.WingetId,
            ["scope"] = t is null ? null : t.UserScope ? "user" : "machine",
            ["reason"] = a.Reason, ["count"] = a.Count, ["lastSeen"] = a.LastSeen, ["workflow"] = a.Workflow,
            ["state"] = a.State, ["attempts"] = a.Attempts, ["error"] = a.Error,
            ["decidedBy"] = a.DecidedBy, ["decidedVia"] = a.DecidedVia, ["decidedAt"] = a.DecidedAt,
            // Windows: Install (or Try again after an error) runs this command; Linux and macOS: the person runs it themselves.
            ["canInstall"] = canInstall,
            ["command"] = t is null ? null : canInstall ? ToolCatalog.CommandLine(t) : t.ManualCommand(_host.IsMac),
            ["choices"] = new JsonArray(canInstall ? ["install", "notNow"] : (JsonNode?[])["notNow"]),
        };
    }

    /// <summary>The open asks as plain lines (the Concierge's and the companion's status reads).</summary>
    public IReadOnlyList<string> PendingLines() => Asks().Select(a =>
        $"- {a.Member} asks for {ToolCatalog.Find(a.Tool)?.DisplayName ?? a.Tool}: \"{Clip(a.Reason ?? "", 200)}\"; {a.Count} time(s) since {a.At}" +
        (a.State == "installing" ? ", installing now" : a.Error is null ? "" : $", the last install failed: {Clip(a.Error, 200)}") + $" (tool request {a.Id})").ToList();

    // ------------------------------------------------------------------ the person's answer

    public async Task<JsonNode?> DispatchAsync(string method, JsonObject? p, CancellationToken ct)
    {
        try
        {
            return method switch
            {
                // [{ name, displayName, publisher, wingetId, scope, program, command }]
                "tools.catalog" => new JsonArray(ToolCatalog.All.Select(t => (JsonNode?)ToJson(t)).ToArray()),
                // [{ id, at, teamId, team, member, memberName, tool, displayName, publisher, wingetId, scope, reason, count, lastSeen,
                //    workflow, state, attempts, error, decidedBy, decidedVia, decidedAt, canInstall, command, choices }]
                "tools.asks" => new JsonArray(Asks(p?["all"]?.GetValue<bool>() != true, OptLong(p, "teamId")).Select(a => (JsonNode?)ToJson(a)).ToArray()),
                "tools.notNow" => await NotNowAsync(OptLong(p, "id") ?? throw new ArgumentException("id is required"), OptStr(p, "note"), ct),
                "tools.install" => await InstallAsync(OptLong(p, "id") ?? throw new ArgumentException("id is required"), ct),
                _ => throw new RpcException("unknown_method", $"no method named '{method}'"),
            };
        }
        catch (ArgumentException e)
        {
            throw new RpcException("bad_params", e.Message);
        }
    }

    /// <summary>Not now: the ask closes and the team is not asked about the tool for 24 hours; the member who asked is told.</summary>
    public async Task<JsonObject> NotNowAsync(long id, string? note, CancellationToken ct)
    {
        var who = Provenance.Actor.Current ?? Provenance.Actor.Window;
        if (who.Via is not (Provenance.Actor.App or Provenance.Actor.Cloud))
            throw new ArgumentException("a tool request is answered by the person, in Needs you");
        var cleanNote = string.IsNullOrWhiteSpace(note) ? null : Clip(note.Trim(), 500);
        ToolAskRow ask;
        lock (_lock)
        {
            ask = Asks().FirstOrDefault(a => a.Id == id && a.State == "pending") ?? throw new ArgumentException($"tool request {id} is not waiting");
            using var c = Open();
            using var tx = c.BeginTransaction();
            if (Exec(c, tx, "UPDATE tool_asks SET state = 'dismissed', decided_by = $by, decided_via = $via, decided_at = $now, note = $note WHERE id = $id AND state = 'pending';",
                    ("$by", who.User), ("$via", who.Via), ("$now", Stamp(_clock.GetUtcNow())), ("$note", cleanNote), ("$id", id)) == 0)
                throw new ArgumentException($"tool request {id} is not waiting");
            Event(c, tx, id, "not now", who.User, who.Via, cleanNote);
            tx.Commit();
        }
        Changed?.Invoke();
        var name = ToolCatalog.Find(ask.Tool)?.DisplayName ?? ask.Tool;
        var text = $"The person chose Not now for installing {name} (tool request {id}){(cleanNote is null ? "" : $": {cleanNote}")}. " +
                   $"Your team is not asked about it again for 24 hours. Do not install it another way: carry on without it, or hand back blocked on {name}.";
        var told = await TellAsync(ask.Team, ask.MemberName, text, ask.CausationSeq, ct);
        return new JsonObject { ["ok"] = true, ["id"] = id, ["tool"] = ask.Tool, ["told"] = told ? ask.Member : null };
    }

    /// <summary>
    /// Install (or Try again): the person at the window only. Runs the catalog's winget command for the ask's tool, then
    /// looks for the program again. Returns when winget has finished; <see cref="Progress"/> reports each phase meanwhile.
    /// </summary>
    public async Task<JsonObject> InstallAsync(long id, CancellationToken ct)
    {
        var who = Provenance.Actor.Current;
        if (who is not { Via: Provenance.Actor.App }) throw new RpcException("forbidden", InstallRefusal);
        var ask = Asks().FirstOrDefault(a => a.Id == id) ?? throw new ArgumentException($"tool request {id} is not waiting");
        var tool = ToolCatalog.Find(ask.Tool) ?? throw new ArgumentException($"'{ask.Tool}' is not in the app's catalog");
        if (!_host.IsWindows)
            return Result(false, id, tool, $"The app installs tools on Windows only: run {tool.ManualCommand(_host.IsMac)} on this computer, then ask the team to retry.",
                manual: true);
        if (ask.State == "installing") return Result(false, id, tool, $"{tool.DisplayName} is being installed already; wait for it to finish.");
        if (await _host.IsInstalledAsync(tool, ct).ConfigureAwait(false))
            return await InstalledAsync(ask, tool, who, "it was already installed", ct);
        var winget = _host.FindWinget();
        if (winget is null)
        {
            var message = $"winget is not available on this computer: install {tool.DisplayName} from its publisher ({tool.Publisher}), then ask the team to retry.";
            Fail(ask.Id, tool, who, message, ran: false);
            return Result(false, id, tool, message);
        }
        if (!await _installing.WaitAsync(0, ct).ConfigureAwait(false))
            return Result(false, id, tool, "Another tool is being installed; wait for it to finish, then try again.");
        try
        {
            lock (_lock)
            {
                using var c = Open();
                using var tx = c.BeginTransaction();
                if (Exec(c, tx, "UPDATE tool_asks SET state = 'installing', attempts = attempts + 1, error = NULL WHERE id = $id AND state = 'pending';", ("$id", id)) == 0)
                    throw new ArgumentException($"tool request {id} is not waiting");
                Event(c, tx, id, "install", who.User, who.Via, ToolCatalog.CommandLine(tool));
                tx.Commit();
            }
            Changed?.Invoke();
            Report(id, tool, "installing", $"Installing {tool.DisplayName} with winget{(tool.UserScope ? "" : " (Windows may ask for permission)")}...");
            GitResult r;
            try
            {
                r = await _host.RunAsync(winget, ToolCatalog.WingetArgs(tool), InstallLimit, ct).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                r = new GitResult(-1, "", e.Message);
            }
            catch (OperationCanceledException)
            {
                Fail(id, tool, who, "the install was stopped before it finished", ran: true);
                throw;
            }
            Report(id, tool, "checking", $"Looking for {tool.Program} again...");
            if (await _host.IsInstalledAsync(tool, ct).ConfigureAwait(false))
                return await InstalledAsync(ask, tool, who, WingetExitCode(r.ExitCode), ct);
            var output = Last(string.Join('\n', r.Stdout, r.Stderr));
            var failed = $"Installing {tool.DisplayName} did not finish ({WingetExitCode(r.ExitCode)}{(output.Length > 0 ? $": {output}" : "")}).";
            Fail(id, tool, who, failed, ran: true);
            return Result(false, id, tool, failed);
        }
        finally
        {
            _installing.Release();
        }
    }

    /// <summary>The program is here: every open ask for the tool closes, and each team's Manager is told to retry.</summary>
    private async Task<JsonObject> InstalledAsync(ToolAskRow ask, ToolEntry tool, Provenance.Actor who, string detail, CancellationToken ct)
    {
        List<ToolAskRow> closed;
        lock (_lock)
        {
            var package = ToolCatalog.SamePackage(tool);
            closed = Asks().Where(a => package.Contains(a.Tool)).ToList();
            using var c = Open();
            using var tx = c.BeginTransaction();
            var now = Stamp(_clock.GetUtcNow());
            foreach (var a in closed)
            {
                Exec(c, tx, "UPDATE tool_asks SET state = 'installed', error = NULL, decided_by = $by, decided_via = $via, decided_at = $now, note = $note WHERE id = $id;",
                    ("$by", who.User), ("$via", who.Via), ("$now", now), ("$note", a.Id == ask.Id ? null : $"installed by tool request {ask.Id}"), ("$id", a.Id));
                Event(c, tx, a.Id, "installed", who.User, who.Via, a.Id == ask.Id ? detail : $"installed by tool request {ask.Id}");
            }
            tx.Commit();
        }
        Changed?.Invoke();
        var message = $"{tool.DisplayName} is installed; runs that start from now on have it on PATH.";
        Report(ask.Id, tool, "installed", message);
        var told = new JsonArray();
        foreach (var team in closed.GroupBy(a => a.TeamId))
        {
            var first = team.First();
            if (ManagerOf(team.Key) is not { } manager) continue;
            var names = string.Join(" and ", team.Select(a => a.MemberName).Distinct(StringComparer.OrdinalIgnoreCase));
            var text = $"{tool.DisplayName} is installed; retry what was blocked (asked for by {names}). It is on PATH for runs that start from now on.";
            if (await TellAsync(first.Team, manager, text, team.Select(a => a.CausationSeq).FirstOrDefault(s => s is not null), ct)) told.Add($"{first.Team}/{manager}");
        }
        var result = Result(true, ask.Id, tool, message);
        result["closed"] = new JsonArray(closed.Select(a => (JsonNode?)a.Id).ToArray());
        result["told"] = told;
        return result;
    }

    /// <summary>The ask waits again with winget's message (Try again).</summary>
    private void Fail(long id, ToolEntry tool, Provenance.Actor who, string message, bool ran)
    {
        lock (_lock)
        {
            using var c = Open();
            using var tx = c.BeginTransaction();
            Exec(c, tx, "UPDATE tool_asks SET state = 'pending', error = $e WHERE id = $id AND state IN ('pending', 'installing');", ("$e", Clip(message, 1000)), ("$id", id));
            Event(c, tx, id, "failed", who.User, who.Via, message);
            tx.Commit();
        }
        Changed?.Invoke();
        if (ran) Report(id, tool, "failed", message);
    }

    private void Report(long id, ToolEntry tool, string phase, string message) =>
        Progress?.Invoke(new JsonObject { ["id"] = id, ["tool"] = tool.Name, ["phase"] = phase, ["message"] = message, ["command"] = ToolCatalog.CommandLine(tool) });

    private static JsonObject Result(bool done, long id, ToolEntry tool, string message, bool manual = false) => new()
    {
        ["done"] = done, ["id"] = id, ["tool"] = tool.Name, ["message"] = message, ["manual"] = manual,
    };

    private async Task<bool> TellAsync(string team, string member, string text, long? causation, CancellationToken ct)
    {
        try
        {
            await _tell(team, member, text, causation, ct);
            return true;
        }
        catch (BusRefusal) when (causation is not null)
        {
            try
            {
                await _tell(team, member, text, null, ct);
                return true;
            }
            catch (BusRefusal) { return false; }
        }
        catch (BusRefusal) { return false; }
    }

    // ------------------------------------------------------------------ the store

    private void Event(SqliteConnection c, SqliteTransaction tx, long askId, string kind, string? by, string? via, string? detail) =>
        Exec(c, tx, "INSERT INTO tool_ask_events (ask_id, at, kind, by_user, via, detail) VALUES ($a, $at, $k, $by, $via, $d);",
            ("$a", askId), ("$at", Stamp(_clock.GetUtcNow())), ("$k", kind), ("$by", by), ("$via", via), ("$d", detail is null ? null : Clip(detail, 1000)));

    /// <summary>What happened to one ask, oldest first: (kind, at, by, via, detail).</summary>
    public IReadOnlyList<(string Kind, string At, string? By, string? Via, string? Detail)> Events(long askId)
    {
        using var c = Open();
        using var cmd = Command(c, "SELECT kind, at, by_user, via, detail FROM tool_ask_events WHERE ask_id = $a ORDER BY id;", ("$a", askId));
        using var r = cmd.ExecuteReader();
        var list = new List<(string, string, string?, string?, string?)>();
        while (r.Read()) list.Add((r.GetString(0), r.GetString(1), S(r, 2), S(r, 3), S(r, 4)));
        return list;
    }

    private string? ManagerOf(long teamId) =>
        Scalar(null, "SELECT name FROM members WHERE team_id = $t AND role = 'manager' LIMIT 1;", ("$t", teamId)) as string;

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_cs);
        c.Open();
        return c;
    }

    private static SqliteCommand Command(SqliteConnection c, string sql, params (string Name, object? Value)[] args)
    {
        var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }

    private object? Scalar(SqliteConnection? within, string sql, params (string Name, object? Value)[] args)
    {
        var c = within ?? Open();
        try
        {
            using var cmd = Command(c, sql, args);
            var v = cmd.ExecuteScalar();
            return v is DBNull ? null : v;
        }
        finally
        {
            if (within is null) c.Dispose();
        }
    }

    private static int Exec(SqliteConnection c, SqliteTransaction tx, string sql, params (string Name, object? Value)[] args)
    {
        using var cmd = Command(c, sql, args);
        cmd.Transaction = tx;
        return cmd.ExecuteNonQuery();
    }

    /// <summary>Parameters for "tool IN (...)": the placeholders and their values.</summary>
    private static (string List, (string Name, object? Value)[] Args) InNames(IReadOnlyList<string> names) =>
        (string.Join(", ", names.Select((_, i) => $"$n{i}")), names.Select((n, i) => ($"$n{i}", (object?)n)).ToArray());

    private static string? S(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    private static string Stamp(DateTimeOffset t) => t.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    /// <summary>winget's last meaningful line (it draws progress bars with lone characters).</summary>
    private static string Last(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).LastOrDefault(l => l.Length > 3 && l.Any(char.IsLetter)) ?? "";

    private static long? OptLong(JsonObject? p, string name) =>
        p?[name] is JsonValue v && (v.TryGetValue<long>(out var l) || (v.TryGetValue<string>(out var s) && long.TryParse(s, CultureInfo.InvariantCulture, out l))) ? l : null;

    private static string? OptStr(JsonObject? p, string name) =>
        p?[name] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;
}
