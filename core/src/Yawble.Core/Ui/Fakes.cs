using System.Text;
using Yawble.Core.Bus;

namespace Yawble.Core.Ui;

/// <summary>
/// The credential service's stand-in for unit tests (the app runs Ui/ModelCredentials.cs). It keeps no credential at all: a "set" checks the
/// shape of the input and records only that one is present and when. Nothing it holds can leak, so the screens
/// and the e2e test run on any machine without touching a real credential.
/// </summary>
public sealed class FakeModelCredentials(TimeProvider? clock = null) : IModelCredentials
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly Lock _lock = new();
    private string _claudeMode = "subscription";
    private string _grokMode = "login";
    private DateTimeOffset? _setupTokenAt;
    private DateTimeOffset? _anthropicKeyAt;
    private DateTimeOffset? _xaiKeyAt;
    private DateTimeOffset? _grokLoginAt;

    /// <summary>For the 30-day prompt: pretend the setup-token was issued this many days ago (tests only).</summary>
    public int SetupTokenAgeDays { get; init; }

    /// <summary>A setup-token lasts one year.</summary>
    public static readonly TimeSpan SetupTokenLifetime = TimeSpan.FromDays(365);

    public static readonly TimeSpan RenewWindow = TimeSpan.FromDays(30);

    public CredentialStatus Status()
    {
        lock (_lock)
        {
            var now = _clock.GetUtcNow();
            SecretStatus Secret(DateTimeOffset? at, TimeSpan? lifetime = null)
            {
                if (at is null) return new SecretStatus(false);
                var expires = lifetime is { } l ? at + l : null;
                return new SecretStatus(true, at, expires, null, expires is { } e && e - now <= RenewWindow);
            }
            return new CredentialStatus(
                "fake", "none", "Fake credential service (the real one is not wired): nothing is stored, and no run can use these.",
                new ClaudeCredentials(_claudeMode, Secret(_setupTokenAt, SetupTokenLifetime), Secret(_anthropicKeyAt)),
                new GrokCredentials(_grokMode,
                    _grokLoginAt is { } g ? new GrokLoginStatus(true, g + TimeSpan.FromHours(6), g) : new GrokLoginStatus(false),
                    Secret(_xaiKeyAt)));
        }
    }

    public void SetClaudeSetupToken(string token)
    {
        Check(token, "the setup-token");
        lock (_lock) _setupTokenAt = _clock.GetUtcNow() - TimeSpan.FromDays(SetupTokenAgeDays);
    }

    public void SetAnthropicApiKey(string key)
    {
        Check(key, "the Anthropic API key");
        lock (_lock) _anthropicKeyAt = _clock.GetUtcNow();
    }

    public void SetXaiApiKey(string key)
    {
        Check(key, "the xAI API key");
        lock (_lock) _xaiKeyAt = _clock.GetUtcNow();
    }

    public void Remove(string which)
    {
        lock (_lock)
        {
            switch (which)
            {
                case "claudeSubscription": _setupTokenAt = null; break;
                case "anthropicApiKey": _anthropicKeyAt = null; break;
                case "xaiApiKey": _xaiKeyAt = null; break;
                case "grokLogin": _grokLoginAt = null; break;
                default: throw new BusRefusal("bad_credential", $"no credential named '{which}'");
            }
        }
    }

    public void SetMode(string cli, string mode)
    {
        lock (_lock)
        {
            switch (cli, mode)
            {
                case ("claude", "subscription"):
                    if (_setupTokenAt is null) throw new BusRefusal("not_connected", "connect a Claude subscription before switching to it");
                    _claudeMode = mode;
                    break;
                case ("claude", "apiKey"):
                    if (_anthropicKeyAt is null) throw new BusRefusal("not_connected", "add an Anthropic API key before switching to it");
                    _claudeMode = mode;
                    break;
                case ("grok", "login"):
                    if (_grokLoginAt is null) throw new BusRefusal("not_connected", "sign in to Grok before switching to it");
                    _grokMode = mode;
                    break;
                case ("grok", "apiKey"):
                    if (_xaiKeyAt is null) throw new BusRefusal("not_connected", "add an xAI API key before switching to it");
                    _grokMode = mode;
                    break;
                default:
                    throw new BusRefusal("bad_mode", $"'{mode}' is not a credential mode of {cli}");
            }
        }
    }

    public void GrokLoginFinished(int exitCode)
    {
        if (exitCode != 0) return;
        lock (_lock) _grokLoginAt = _clock.GetUtcNow();
    }

    private static void Check(string? value, string what)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new BusRefusal("empty", $"paste {what}");
        if (value.Trim().Length < 8 || value.Trim().Any(char.IsWhiteSpace))
            throw new BusRefusal("bad_format", $"that does not look like {what}: one line, no spaces, at least 8 characters");
    }
}

/// <summary>
/// The host panes' stand-in for unit tests (the app runs Ui/HostPaneTerminals.cs): prints what the real command would do, never runs claude or grok
/// and never opens a browser. setup-token ends by asking for the paste; grok login ends signed in.
/// </summary>
public sealed class FakeHostTerminals(TerminalHub hub, TimeSpan? step = null) : IHostTerminals
{
    private readonly TimeSpan _step = step ?? TimeSpan.FromMilliseconds(250);

    public Task StartAsync(string kind, string channel, Action<int> onExit, CancellationToken ct)
    {
        string[] lines = kind switch
        {
            IHostTerminals.ClaudeSetupToken =>
            [
                "\x1b[2m$ claude setup-token   (unsandboxed host pane · FAKE: nothing runs)\x1b[0m",
                "Opening your browser to sign in to your Claude subscription…",
                "✔ Signed in.",
                "Your OAuth token (valid for one year) would be printed here.",
                "\x1b[33mCopy it and paste it into the box below. The app never reads this pane.\x1b[0m",
            ],
            IHostTerminals.GrokLogin =>
            [
                "\x1b[2m$ grok login   (unsandboxed host pane, GROK_HOME=<app>/grok-home · FAKE: nothing runs)\x1b[0m",
                "Opening auth.x.ai in your browser…",
                "Waiting for the browser sign-in to finish…",
                "\x1b[32m✔ Signed in to Grok. Session saved to the app's host Grok home.\x1b[0m",
            ],
            _ => throw new BusRefusal("bad_kind", $"no host pane named '{kind}'"),
        };
        hub.Begin(channel, new TerminalSession("host", null, DateTimeOffset.UtcNow.ToString("O"), false,
            kind == IHostTerminals.ClaudeSetupToken ? "claude setup-token" : "grok login"), null, clearReplay: true);
        _ = Task.Run(async () =>
        {
            foreach (var line in lines)
            {
                await Task.Delay(_step, CancellationToken.None);
                hub.Write(channel, line + "\r\n");
            }
            hub.End(channel, 0, "exited 0");
            onExit(0);
        }, CancellationToken.None);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Open interactive CLI's stand-in when the launcher is off (the UI tests' fake-pty core, or a machine with no sandbox
/// backend): a line-echo "TUI" on the member's channel. It launches no CLI and no sandbox. With the bus it still
/// takes a global run slot like the real one: it waits in the one FIFO queue, holds the slot
/// while open, and gives it back on Stop or when the window's session is stopped. Without a bus it takes none.
/// </summary>
public sealed class FakeInteractiveSessions(TerminalHub hub, BusEngine? bus = null) : IInteractiveSessions
{
    /// <summary>The size the last session was asked to start at (the window's fitted terminal).</summary>
    public Launch.PtySize? LastSize { get; private set; }

    public async Task StartAsync(MemberRow member, string channel, CancellationToken ct, Launch.PtySize? size = null)
    {
        LastSize = size ?? Launch.PtySize.Default;
        if (hub.IsLive(channel)) throw new BusRefusal("busy", $"{member.Name} already has a session open in its terminal");
        if (bus is null)
        {
            Begin(member, channel, null);
            return;
        }
        var began = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ticket = await bus.RequestSessionAsync(member.Team, member.Name, async token =>
        {
            var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Begin(member, channel, ended);
            began.TrySetResult();
            using (token.Register(() => ended.TrySetResult()))
                await ended.Task;
            if (hub.IsLive(channel))
            {
                hub.Write(channel, "\r\n\x1b[33m■ stopped by the person\x1b[0m\r\n");
                hub.End(channel, 130, "stopped");
            }
        }, ct);
        // Admitted at once (a slot was free): return once the session is on the channel, so the window's attach
        // sees it. Otherwise it waits in the queue and says where.
        if (await ticket.PositionAsync() is { } position)
            hub.Write(channel, $"\r\n\x1b[33m… waiting for a run slot (position {position})\x1b[0m\r\n");
        else
            await Task.WhenAny(began.Task, ticket.Completion);
    }

    private void Begin(MemberRow member, string channel, TaskCompletionSource? ended)
    {
        var session = new EchoSession(hub, channel, member.Cli, ended);
        hub.Begin(channel, new TerminalSession("interactive", null, DateTimeOffset.UtcNow.ToString("O"), true, member.Cli), session);
        hub.Write(channel, $"\r\n\x1b[36m── interactive {member.Cli} (FAKE: no CLI or sandbox started) ──\x1b[0m\r\n> ");
    }

    private sealed class EchoSession(TerminalHub hub, string channel, string cli, TaskCompletionSource? ended) : ITerminalInput
    {
        private readonly StringBuilder _line = new();

        public void Write(ReadOnlySpan<byte> data)
        {
            foreach (var ch in Encoding.UTF8.GetString(data))
            {
                if (ch is '\r' or '\n')
                {
                    hub.Write(channel, $"\r\n{cli} (fake): you said \"{_line}\"\r\n> ");
                    _line.Clear();
                }
                else if (ch == '\x7f')
                {
                    if (_line.Length == 0) continue;
                    _line.Length--;
                    hub.Write(channel, "\b \b");
                }
                else if (!char.IsControl(ch))
                {
                    _line.Append(ch);
                    hub.Write(channel, ch.ToString());
                }
            }
        }

        public void Resize(int cols, int rows) { }

        public Task StopAsync()
        {
            hub.Write(channel, "\r\n\x1b[33m■ stopped by the person\x1b[0m\r\n");
            hub.End(channel, 130, "stopped");
            ended?.TrySetResult(); // gives the run slot back
            return Task.CompletedTask;
        }
    }
}

/// <summary>
/// A test runner that behaves like a headless member run on a PTY (YAWBLE_RUNNER=fake-pty): it writes the run's
/// header and a line per tick to the member's channel, reports progress, and (as a member) hands back at the end.
/// Ticks: YAWBLE_FAKE_RUN_TICKS (default 8; 0 = members run until stopped, a Manager for 3). A Manager given a
/// person's instruction tells each member of its team a part of it, so the team's cards and terminals show the
/// real bus traffic. It launches nothing.
/// </summary>
public sealed class FakePtyRunner(TerminalHub hub, Func<long, string> cliOf, int ticks = 8, TimeSpan? tick = null) : IAgentRunner
{
    private readonly TimeSpan _tick = tick ?? TimeSpan.FromMilliseconds(600);

    /// <summary>Lines per run; 0 runs until stopped. Read when a run starts.</summary>
    public int Ticks { get; set; } = ticks;

    public async Task<RunResult> RunAsync(RunContext run, CancellationToken ct)
    {
        var ticks = Ticks == 0 && run.Member.Role == MemberRole.Manager ? 3 : Ticks;
        var channel = TerminalHub.MemberChannel(run.Member.Id);
        var cli = cliOf(run.Member.Id);
        var command = cli == "grok" ? "grok -p … --output-format json" : "claude -p … --output-format stream-json";
        hub.Begin(channel, new TerminalSession("headless", run.RunId, DateTimeOffset.UtcNow.ToString("O"), false, command),
            new StopOnly());
        hub.Write(channel, $"\r\n\x1b[2m$ {cli} -p (batch of {run.Items.Count}, workflow {run.Correlation}) · run {run.RunId} · FAKE runner\x1b[0m\r\n");
        hub.Write(channel, $"\x1b[2m  {FirstLine(run.Prompt)}\x1b[0m\r\n");
        try
        {
            if (run.Member.Role == MemberRole.Manager && run.Items.Any(m => m.Source == MessageTypes.PersonSource))
            {
                foreach (var other in (await run.StatusAsync(ct)).Where(s => s.Role == "member"))
                {
                    await run.TellAsync(other.Name, $"your part of: {FirstLine(run.Prompt)}", ct);
                    hub.Write(channel, $"\x1b[35m● tell {other.Name}: your part of the work\x1b[0m\r\n");
                }
            }
            for (var i = 1; ticks == 0 || i <= ticks; i++)
            {
                await Task.Delay(_tick, ct);
                hub.Write(channel, $"● step {i}: working on \"{FirstLine(run.Prompt)}\"\r\n");
                if (i % 3 == 1)
                {
                    await run.ProgressAsync($"step {i} of {(ticks == 0 ? "∞" : ticks.ToString())}", ct);
                    hub.Write(channel, $"\x1b[36m● progress: step {i}\x1b[0m\r\n");
                }
            }
            if (run.Member.Role == MemberRole.Member)
            {
                await run.HandbackAsync($"fake run {run.RunId} done", ct);
                hub.Write(channel, "\x1b[32m● handback\x1b[0m\r\n");
            }
            hub.Write(channel, "\x1b[2m— run completed —\x1b[0m\r\n");
            hub.End(channel, 0, "completed");
            return RunResult.Ok;
        }
        catch (OperationCanceledException)
        {
            hub.Write(channel, "\x1b[33m■ stopped by the person\x1b[0m\r\n");
            hub.End(channel, 130, "stopped");
            return RunResult.Fail("stopped: by the person");
        }
    }

    private static string FirstLine(string text)
    {
        var line = text.Split('\n', 2)[0].Trim();
        return line.Length <= 60 ? line : line[..57] + "…";
    }

    /// <summary>A headless run takes no input; Stop goes to the bus, which cancels the run.</summary>
    private sealed class StopOnly : ITerminalInput
    {
        public void Write(ReadOnlySpan<byte> data) { }
        public void Resize(int cols, int rows) { }
        public Task StopAsync() => Task.CompletedTask;
    }
}
