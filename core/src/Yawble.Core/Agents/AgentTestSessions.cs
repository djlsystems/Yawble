using System.Text.Json.Nodes;
using Yawble.Core.Ipc;
using Yawble.Core.Launch;
using Yawble.Core.Sandboxing;
using Yawble.Core.Policy;
using Yawble.Core.Ui;

namespace Yawble.Core.Agents;

/// <summary>
/// The Agents dialog's Test: the detected CLI's own TUI in a fresh sandbox, on a sandbox PTY, shown in the dialog's
/// terminal pane (hub channel agent-test:&lt;cli&gt;). It runs as a stand-in that is not a member (team
/// <see cref="Team"/>, member = the CLI id) under the GLOBAL policy alone, like the synthetic guardrail check, and
/// like it takes no global run slot: the person starts it and watches it. Terminal (no <c>diagnostic</c>) starts the
/// CLI with no prompt; Test (<c>diagnostic</c>) starts it with a prompt asking it to report its version, cwd, HOME and
/// whether it can read/write a few paths, so the guardrails are visible from inside. Either may be a custom agent's
/// (its argument changes and default model applied); the member is still the base CLI's id, so one session per CLI.
/// The first terminal line is the exact command line (<see cref="CommandLine"/>), built from this class's own
/// detection, so <c>detector</c> is the launcher's (<see cref="LaunchHost.Clis"/>; Program.cs passes the same one).
///
/// The live sessions are listed (<see cref="Sessions"/>, agents.testSessions) so the person can get back to one after
/// hiding the pane, and each start and end is pushed as <see cref="ChangedEvent"/> (window only: never over the Cloud
/// Link). A session with no activity (no input from the person AND no output from the agent) for <see cref="IdleLimit"/> ends on its own (<paramref name="idleLimit"/>
/// and <paramref name="idleCheck"/> shorten it in tests; <paramref name="clock"/> is the time the idle is measured in).
///
/// What it runs is what the launcher runs: today the launcher's own CLI path; once the launcher runs the detected
/// install (its ReadOnlyFolders granted, its ConfigFolders blocked) the Test runs exactly that, with no change here.
/// </summary>
public sealed class AgentTestSessions(
    SandboxLauncher? launcher,
    string? launcherProblem,
    TerminalHub hub,
    ICliDetector detector,
    Action<string>? log = null,
    Profiles.IAgentResolver? agents = null,
    TimeProvider? clock = null,
    TimeSpan? idleLimit = null,
    TimeSpan? idleCheck = null)
{
    /// <summary>A session with no activity (no input, no output) for this long ends on its own (the safety net).</summary>
    public static readonly TimeSpan IdleLimit = TimeSpan.FromMinutes(30);

    /// <summary>How often a live session's idle time is looked at.</summary>
    public static readonly TimeSpan IdleCheck = TimeSpan.FromMinutes(1);

    /// <summary>Pushed when a session starts or ends: {cli, agent, kind, live}. The window refreshes agents.testSessions.</summary>
    public const string ChangedEvent = "agents.testChanged";

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly TimeSpan _idleLimit = idleLimit ?? IdleLimit;
    private readonly TimeSpan _idleCheck = idleCheck ?? IdleCheck;

    /// <summary>The stand-in's team: its folders are &lt;app&gt;/teams/_agent-test/members/&lt;cli&gt;/.</summary>
    public const string Team = "_agent-test";

    public const string ChannelPrefix = "agent-test:";

    public static string Channel(string cliId) => ChannelPrefix + cliId;

    /// <summary>The terminal channel of a Test sandbox, or null when the run is not one (for RunTerminalBridge).</summary>
    public static string? ChannelFor(LiveRun live) => live.Team == Team ? Channel(live.Member) : null;

    public Task<JsonObject> StartAsync(string cliId, bool diagnostic, CancellationToken ct) => StartAsync(cliId, null, diagnostic, ct);

    /// <summary>
    /// Starts the CLI's interactive session. <paramref name="agent"/> names a custom agent based on that CLI: its
    /// argument changes and default model are applied as for a member of it (the same LaunchMember the Command line
    /// dialog describes, AgentInfoRpc.Subject). Without <paramref name="diagnostic"/> the session starts with no
    /// prompt (the Agents screen's Terminal); with it, the diagnostic prompt is its initial prompt (Test). The first
    /// line in the terminal is the exact command line, built by the launcher's own describe function.
    /// </summary>
    public async Task<JsonObject> StartAsync(string cliId, string? agent, bool diagnostic, CancellationToken ct)
    {
        var cli = cliId switch
        {
            "claude" => AgentCli.Claude,
            "grok" => AgentCli.Grok,
            "codex" => AgentCli.Codex,
            "copilot" => AgentCli.Copilot,
            "antigravity" => AgentCli.Antigravity,
            _ => throw new RpcException("bad_params", $"no supported CLI '{cliId}'"),
        };
        Profiles.ResolvedAgent? custom = null;
        if (agent is { Length: > 0 })
        {
            Profiles.ResolvedAgent resolved;
            try { resolved = Profiles.AgentResolvers.Resolve(agents, agent); }
            catch (ArgumentException e) { throw new RpcException("bad_params", e.Message); }
            if (resolved.Cli != cli)
                throw new RpcException("bad_params", $"{resolved.Name} is based on {resolved.Cli.Id()}, not {cliId}");
            if (resolved.Custom) custom = resolved;
        }
        var channel = Channel(cliId);
        // One session per CLI (its folders _agent-test/members/<cli>): the CLI is reserved before anything is awaited,
        // so a second start that arrives meanwhile is refused, and released once the run has ended or did not start.
        lock (_reserved)
        {
            if (_reserved.Contains(cliId) || hub.IsLive(channel))
                throw new RpcException("busy", $"a {cliId} session is already running in the terminal: stop it first");
            _reserved.Add(cliId);
        }
        var launched = false;
        try
        {
            var started = await LaunchAsync(cli, cliId, channel, custom, diagnostic, ct);
            launched = true;
            return started;
        }
        finally
        {
            if (!launched) Release(cliId);
        }
    }

    private readonly HashSet<string> _reserved = [];

    /// <summary>
    /// A live session: who it is, its run (<see cref="Token"/>: what agents.test answered, so a stop can name this run
    /// and not a newer one of the same CLI), when anything last happened on it, and whether a stop was asked before
    /// its sandbox had begun (then it is stopped as soon as it begins).
    /// </summary>
    private sealed class Live(string cli, string agent, string kind, DateTimeOffset startedAt, string token)
    {
        public string Cli { get; } = cli;
        public string Agent { get; } = agent;
        public string Kind { get; } = kind;
        public DateTimeOffset StartedAt { get; } = startedAt;
        public string Token { get; } = token;
        public long LastActivityTicks = startedAt.UtcTicks;
        public volatile bool StopRequested;
        public Task? Run;
    }

    private readonly Dictionary<string, Live> _live = new(StringComparer.Ordinal);

    /// <summary>agents.testSessions: the live sessions, [{cli, agent, kind: "terminal"|"test", startedAt}], by CLI.</summary>
    public JsonArray Sessions()
    {
        Live[] live;
        lock (_live) live = [.. _live.Values.OrderBy(l => l.Cli, StringComparer.Ordinal)];
        return [.. live.Select(l => (JsonNode)new JsonObject
        {
            ["cli"] = l.Cli,
            ["agent"] = l.Agent,
            ["kind"] = l.Kind,
            ["startedAt"] = l.StartedAt.ToString("O"),
            ["runId"] = l.Token,
        })];
    }

    private void Changed(Live l, bool live) =>
        hub.Events.Publish(ChangedEvent, new JsonObject { ["cli"] = l.Cli, ["agent"] = l.Agent, ["kind"] = l.Kind, ["runId"] = l.Token, ["live"] = live });

    /// <summary>
    /// Ends the session when nothing happened on it (no input, no output) for the idle limit, with a dimmed line saying so.
    /// True when it ended it. Called every <see cref="IdleCheck"/> while the session is live.
    /// </summary>
    internal async Task<bool> EndIfIdleAsync(string cliId)
    {
        Live? l;
        lock (_live) _live.TryGetValue(cliId, out l);
        if (l is null) return false;
        var idle = _clock.GetUtcNow() - new DateTimeOffset(Interlocked.Read(ref l.LastActivityTicks), TimeSpan.Zero);
        if (idle < _idleLimit) return false;
        var channel = Channel(cliId);
        if (!hub.IsLive(channel)) return false;
        // The line only when the stop happened (it would otherwise be untrue, and count as activity).
        if (!await hub.StopAsync(channel)) return false;
        hub.Write(channel, $"\r\n\x1b[2m■ ended after {Describe(_idleLimit)} with no activity\x1b[0m\r\n");
        return true;
    }

    private static string Describe(TimeSpan t) =>
        t.TotalMinutes >= 1 ? $"{t.TotalMinutes:0} minute{(Math.Round(t.TotalMinutes) == 1 ? "" : "s")}" : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{t.TotalSeconds:0.#} seconds");

    private void Release(string cliId) { lock (_reserved) _reserved.Remove(cliId); }

    private async Task<JsonObject> LaunchAsync(AgentCli cli, string cliId, string channel, Profiles.ResolvedAgent? custom, bool diagnostic, CancellationToken ct)
    {
        var detected = await detector.DetectOneAsync(cliId, ct);
        if (detected is not { Found: true }) throw new RpcException("not_found", $"{cliId} is not installed on this computer");
        if (launcher is null) throw new RpcException("unavailable", launcherProblem ?? "the sandbox launcher is not available on this computer");

        // Member = the CLI id: one session per base CLI (folders _agent-test/members/<cli>, channel agent-test:<cli>).
        var subject = custom is null
            ? new LaunchMember(0, Team, cliId, cli)
            : new LaunchMember(0, Team, cliId, cli, Agent: custom.Name, Custom: custom.Argv, AgentModel: custom.DefaultModel);
        string commandLine;
        try { commandLine = CommandLine(launcher, subject, diagnostic, detected); } // the detection above, not a second one
        catch (ArgumentException e) { throw new RpcException("bad_params", e.Message); }

        var prompt = diagnostic ? DiagnosticPrompt(detected, launcher.Host.Home, launcher.Host.AppDataRoot) : null;
        var name = custom?.Name ?? cliId;
        hub.Write(channel, $"\r\n\x1b[2m{commandLine}\x1b[0m\r\n");
        hub.Write(channel, $"\x1b[2m── {name} {(diagnostic ? "test" : "terminal")}: starting {detected.ExecutablePath ?? cliId} {detected.Version} in the sandbox (global policy) ──\x1b[0m\r\n");
        var request = new LaunchRequest(Team, cliId, RunKind.Interactive, prompt, null, PtySize.Default, Subject: subject);
        var token = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
        var live = new Live(cliId, name, diagnostic ? "test" : "terminal", _clock.GetUtcNow(), token);
        void Active(string activeChannel)
        {
            if (string.Equals(activeChannel, channel, StringComparison.Ordinal)) Interlocked.Exchange(ref live.LastActivityTicks, _clock.GetUtcNow().UtcTicks);
        }
        // A stop asked while the sandbox was still starting (nothing on the channel to stop yet) takes effect as soon
        // as the session begins; off the launcher's thread (Begin runs inside its registration).
        void Begun(string begunChannel)
        {
            if (live.StopRequested && string.Equals(begunChannel, channel, StringComparison.Ordinal))
                _ = Task.Run(() => hub.StopAsync(channel));
        }
        hub.Activity += Active;
        hub.Began += Begun;
        lock (_live) _live[cliId] = live;
        Changed(live, true);
        var watching = new CancellationTokenSource();
        var idleWatch = WatchIdleAsync(cliId, watching.Token);
        live.Run = Task.Run(async () =>
        {
            try
            {
                var outcome = await launcher.RunAsync(request, CancellationToken.None);
                if (outcome.End == RunEnd.StartFailed)
                    hub.Write(channel, $"\r\n\x1b[31m■ the session did not start: {outcome.Reason}\x1b[0m\r\n");
            }
            catch (Exception ex)
            {
                log?.Invoke($"agent test {cliId}: {ex.Message}");
                hub.Write(channel, $"\r\n\x1b[31m■ the session failed: {ex.Message}\x1b[0m\r\n");
            }
            finally
            {
                hub.Activity -= Active;
                hub.Began -= Begun;
                await watching.CancelAsync();
                try { await idleWatch; } catch (OperationCanceledException) { }
                watching.Dispose();
                lock (_live) _live.Remove(cliId);
                Release(cliId); // the run has ended (or did not start): its folders are free again
                Changed(live, false);
            }
        }, CancellationToken.None);
        return new JsonObject { ["channel"] = channel, ["diagnostic"] = prompt is not null, ["agent"] = name, ["commandLine"] = commandLine, ["runId"] = token };
    }

    private async Task WatchIdleAsync(string cliId, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(_idleCheck, _clock, ct).ConfigureAwait(false);
            try
            {
                if (await EndIfIdleAsync(cliId).ConfigureAwait(false)) return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log?.Invoke($"agent test {cliId}: idle check: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// The interactive command line this session runs, as the Command line dialog shows it (launcher.Describe, for
    /// the subject's CLI, resolved model and argument changes), joined with spaces. With no prompt, the prompt tail
    /// is left out; with the diagnostic, it is the &lt;prompt&gt; placeholder (or &lt;diagnostic prompt&gt; appended
    /// when the description has none).
    /// </summary>
    public static string CommandLine(SandboxLauncher launcher, LaunchMember subject, bool diagnostic, DetectedCli detected)
    {
        var model = launcher.ResolveModel(subject).Model;
        var lines = launcher.Describe(subject.Cli, string.IsNullOrEmpty(model) ? null : model, subject.Custom, detected);
        var argv = lines.Interactive.Argv.ToList();
        var tail = lines.InteractiveGroups.Groups.Where(g => g.Tail).Sum(g => g.Tokens.Count);
        if (!diagnostic) argv = argv.Take(argv.Count - tail).ToList();
        else if (tail == 0) argv.Add("<diagnostic prompt>");
        return string.Join(' ', argv);
    }

    /// <summary>How long <see cref="StopAsync"/> waits for a stopped run to finish its post-run work.</summary>
    public static readonly TimeSpan StopWait = TimeSpan.FromSeconds(10);

    /// <summary>
    /// agents.stopTest. Stops the CLI's session (only the run <paramref name="runId"/> names, when given: an old pane
    /// cannot stop a newer session of the same CLI), then waits up to <see cref="StopWait"/> for the run to finish, so
    /// the CLI's reservation is released before this answers and a start right after it is not refused as busy. A
    /// session whose sandbox has not begun yet is stopped as soon as it begins (the stop is kept, not lost).
    /// </summary>
    public async Task<bool> StopAsync(string cliId, string? runId = null)
    {
        Live? live;
        lock (_live) _live.TryGetValue(cliId, out live);
        if (runId is not null && live?.Token != runId) return false; // that run has ended; a newer one is not this pane's
        if (live is not null) live.StopRequested = true; // before the hub's stop: a Begin in between still stops it
        var stopped = await hub.StopAsync(Channel(cliId));
        if (live is null) return stopped;
        if (live.Run is { } run)
        {
            try { await run.WaitAsync(StopWait); }
            catch (TimeoutException) { log?.Invoke($"agent test {cliId}: the stopped run had not finished after {StopWait.TotalSeconds:0} s"); }
        }
        return true;
    }

    /// <summary>
    /// What the diagnostic asks. The paths are the person's real home and the CLI's real config folders (which the
    /// sandbox must not reach) next to the sandbox's own HOME and working folder (which it may write).
    /// </summary>
    public static string DiagnosticPrompt(DetectedCli cli, string hostHome, string appDataRoot)
    {
        var paths = new List<string> { "your current working directory", "$HOME (on Windows %USERPROFILE%)", hostHome };
        paths.AddRange(cli.ConfigFolders);
        paths.Add(appDataRoot);
        return "Yawble agent test. Using only your shell tool, report as a short numbered list: "
            + "1) the output of your own --version; 2) the current working directory; 3) the value of HOME (and USERPROFILE on Windows); "
            + "4) for each of these paths, whether you can LIST/READ it and whether you can WRITE a new file named .yawble-test in it "
            + "(create it, then delete it again): " + string.Join("; ", paths) + ". "
            + "Do not read the contents of any credential file; report each error message exactly. Change nothing else.";
    }
}
