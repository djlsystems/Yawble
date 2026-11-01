using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Harness.Host;

/// <summary>
/// THE AGENT CLIS ARE INSTALLED AND UPDATED AFTER THE WORKER CONNECTS, NEVER BEFORE IT. A container's start
/// used to install every missing CLI, and update every present one, before its Host ran: a fresh volume
/// kept the worker from connecting for minutes, and every restart ran npm for each vendor in turn. Now
/// the entrypoint starts the Host at once, and this does that work in the background, through the one
/// update gate the Agents screen's update goes through:
/// <list type="bullet">
/// <item>From the Host's start every command the platform can install is held (<see cref="AgentUpdateGate.BeginPending"/>):
/// a run queued for it waits rather than being sent to a worker that may not have it yet.</item>
/// <item>When the first worker joins (control), or at the Host's start (a Host that runs its runs itself), each command
/// is measured on a worker, in parallel. A missing one is installed by the image's installer
/// (<see cref="AgentCliUpdater.RequestInstall"/>, <see cref="AgentUpdateKinds.Install"/>); an installed one
/// whose last check is older than <c>agents.updateInterval</c> is updated by its preset's own update
/// (<see cref="AgentCliUpdater.Request"/>); any other is let go. Each command is its own request: one vendor
/// failing or slow holds no other, and launches of the others go on.</item>
/// <item>Every hour an installed command whose interval has passed is updated the same way. A restart inside
/// the interval updates nothing, since the last check is kept on the volume (<see cref="ChecksFile"/>).</item>
/// </list>
/// <c>HARNESS_UPDATE_AGENTS=0</c> turns the updates off (missing CLIs are still installed);
/// <c>HARNESS_REINSTALL_AGENTS=1</c> installs every one again. Only with an installer configured
/// (<c>HARNESS_AGENT_INSTALLER</c>, which the image's entrypoint sets): a Host run any other way installs nothing.
/// What it is doing is recorded for the doctor and <c>yawble up</c> in <see cref="AgentInstallsRecord"/>.
/// </summary>
public sealed class AgentInstallsAtStart : IDisposable
{
    /// <summary>The setting that names the installer the workers run, as the agent: the image's <c>ensure-agent-clis.sh</c>.</summary>
    public const string InstallerSetting = "HARNESS_AGENT_INSTALLER";

    /// <summary>When each command was last checked for an update, on the volume, so a restart does not check again.</summary>
    public const string ChecksFile = "agent-update-checks.json";

    /// <summary>How often the interval is looked at while the Host runs.</summary>
    public static readonly TimeSpan CheckEvery = TimeSpan.FromHours(1);

    /// <summary>What the Host logs when it starts the work.</summary>
    public const string StartedText = "Agent CLIs: measuring each on a worker; missing ones are installed and due ones updated, in the background, through the update gate.";

    /// <summary>One command the platform installs, and the preset it is asked through.</summary>
    public sealed record Command(string Agent, string Name);

    /// <summary>What one command came to, as far as this has seen.</summary>
    private sealed record Outcome(string State, string? Detail, DateTimeOffset At);

    private readonly AgentUpdateGate _gate;
    private readonly Func<IReadOnlyList<Command>> _commands;
    private readonly Func<string, CancellationToken, Task<bool?>> _installed;
    private readonly Func<string, bool, Func<AgentUpdateState, Task>, bool> _install;
    private readonly Func<string, Func<AgentUpdateState, Task>, bool>? _update;
    private readonly Func<TimeSpan> _interval;
    private readonly bool _reinstall;
    private readonly string _dataRoot;
    private readonly string? _chromeRoot;
    private readonly TimeProvider _clock;
    private readonly ILogger? _log;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, Outcome> _outcomes = new(StringComparer.Ordinal);
    private readonly List<ITimer> _timers = [];
    private int _started;
    private string? _worker;
    private string? _lastWritten;

    /// <param name="installed">Whether a command is installed, asked of a worker; null when it could not be said.</param>
    /// <param name="install">Asks the gate to install a preset's command (reinstalling when asked); answers whether it started.</param>
    /// <param name="update">Asks the gate to update a preset's command; null when updates are off.</param>
    /// <param name="interval">The least time between two update checks of one command (<c>agents.updateInterval</c>).</param>
    /// <param name="chromeRoot">Where Google Chrome for the sign-in page is installed; its installer's status file is read from there.</param>
    public AgentInstallsAtStart(
        AgentUpdateGate gate,
        Func<IReadOnlyList<Command>> commands,
        Func<string, CancellationToken, Task<bool?>> installed,
        Func<string, bool, Func<AgentUpdateState, Task>, bool> install,
        Func<string, Func<AgentUpdateState, Task>, bool>? update,
        Func<TimeSpan> interval,
        bool reinstall,
        string dataRoot,
        string? chromeRoot = null,
        TimeProvider? clock = null,
        ILogger? log = null)
    {
        _gate = gate;
        _commands = commands;
        _installed = installed;
        _install = install;
        _update = update;
        _interval = interval;
        _reinstall = reinstall;
        _dataRoot = dataRoot;
        _chromeRoot = chromeRoot;
        _clock = clock ?? TimeProvider.System;
        _log = log;
        _gate.UpdateEnded += _ => Record();
    }

    /// <summary>
    /// The commands the platform can install: one preset per command the catalog launches whose built-in
    /// declares an update - which is what the image's installer knows how to install.
    /// </summary>
    public static IReadOnlyList<Command> Installable(AgentCatalog catalog) =>
        [.. catalog.Definitions
            .Where(d => d.Launch is not null && !string.IsNullOrWhiteSpace(d.Launch.FileName))
            .GroupBy(d => d.Launch!.FileName.Trim(), StringComparer.Ordinal)
            .Where(g => AgentUpdates.ForCommand(g.Key)?.Update is { Count: > 0 })
            .Select(g => new Command(g.First().Name, g.Key))];

    /// <summary>
    /// Holds every installable command's launches from now until it is measured, for at most
    /// <see cref="AgentUpdateGate.InstallBound"/>: a run never goes to a worker before its CLI is known to be there.
    /// </summary>
    public void Hold()
    {
        foreach (var command in _commands()) _gate.BeginPending(command.Name);

        // Bounded: a control no worker joins lets its launches go, which then wait for a worker as before.
        _timers.Add(_clock.CreateTimer(_ =>
        {
            foreach (var command in _commands()) _gate.EndPending(command.Name);
            Record();
        }, null, AgentUpdateGate.InstallBound, Timeout.InfiniteTimeSpan));
        Record();
    }

    /// <summary>
    /// Measures each command on a worker and installs, updates or lets it go, each on its own; once per Host start
    /// (a second call does nothing). <paramref name="worker"/> names the worker for the record. Also starts the hourly
    /// interval check. Never throws.
    /// </summary>
    public async Task StartAsync(string? worker, CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) return;

        _worker = worker;
        _log?.LogInformation(StartedText);
        _timers.Add(_clock.CreateTimer(_ => _ = CheckDueAsync(CancellationToken.None), null, CheckEvery, CheckEvery));
        _timers.Add(_clock.CreateTimer(_ => Record(), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2)));

        await Task.WhenAll(_commands().Select(command => DecideAsync(command, ct)));
        Record();
    }

    /// <summary>Updates every installed command whose interval has passed. Never throws.</summary>
    public async Task CheckDueAsync(CancellationToken ct = default)
    {
        if (_update is null) return;

        foreach (var command in _commands())
        {
            try
            {
                if (_gate.Holding(command.Name) is not null || !Due(command.Name)) continue;
                lock (_lock)
                {
                    if (_outcomes.TryGetValue(command.Name, out var was) && was.State is AgentInstallWords.Failed or AgentInstallWords.Missing) continue;
                }

                if (await _installed(command.Name, ct) != true) continue;
                Ask(command, AgentInstallWords.Updating, () => _update(command.Agent, state => FinishedAsync(command, state, update: true)));
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                _log?.LogWarning("Agent CLIs: the update check of {Command} failed: {Message}", command.Name, exception.Message);
            }
        }

        Record();
    }

    private async Task DecideAsync(Command command, CancellationToken ct)
    {
        try
        {
            bool? have = null;
            if (!_reinstall)
            {
                try
                {
                    have = await _installed(command.Name, ct);
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    _log?.LogWarning("Agent CLIs: whether {Command} is installed could not be measured: {Message}", command.Name, exception.Message);
                }
            }

            if (_reinstall || have != true)
            {
                // Not measured is installed too: the installer itself installs only what is missing.
                Ask(command, AgentInstallWords.Installing, () => _install(command.Agent, _reinstall, state => FinishedAsync(command, state, update: false)));
            }
            else if (_update is not null && Due(command.Name))
            {
                Ask(command, AgentInstallWords.Updating, () => _update(command.Agent, state => FinishedAsync(command, state, update: true)));
            }
            else
            {
                Set(command.Name, AgentInstallWords.Ready, null);
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _log?.LogWarning("Agent CLIs: {Command} was not installed or updated: {Message}", command.Name, exception.Message);
            Set(command.Name, AgentInstallWords.Failed, exception.Message);
        }
        finally
        {
            // After the ask: an install or update asked holds the launches on from here, with no gap.
            _gate.EndPending(command.Name);
            Record();
        }
    }

    private void Ask(Command command, string state, Func<bool> ask)
    {
        Set(command.Name, state, null);
        if (!ask())
        {
            // Another update of it was already waiting or running (a person's): that one stands.
            _log?.LogInformation("Agent CLIs: {Command} already has an update asked for; this one was not added.", command.Name);
        }
    }

    private async Task FinishedAsync(Command command, AgentUpdateState state, bool update)
    {
        var result = state.Result;
        string outcome;
        string? detail = result?.Detail ?? state.Error;

        if (state.Phase == AgentUpdatePhases.Done && result is { Updated: true })
        {
            outcome = AgentInstallWords.Ready;
            Checked(command.Name);
        }
        else if (update && state.Phase == AgentUpdatePhases.Done)
        {
            // The update ran and failed (a vendor unreachable): the installed version stays, and it is not tried
            // again before the interval.
            outcome = AgentInstallWords.Ready;
            Checked(command.Name);
        }
        else if (update)
        {
            // Nothing ran (no worker): asked again at the next hourly check.
            outcome = AgentInstallWords.Ready;
        }
        else
        {
            outcome = AgentInstallWords.Failed;
        }

        Set(command.Name, outcome, detail);
        if (outcome == AgentInstallWords.Failed)
        {
            _log?.LogWarning("Agent CLIs: installing {Command} failed: {Detail}", command.Name, detail);
        }
        else
        {
            _log?.LogInformation("Agent CLIs: {Command}: {Detail}", command.Name, detail);
        }

        Record();
        await Task.CompletedTask;
    }

    private void Set(string command, string state, string? detail)
    {
        lock (_lock) _outcomes[command] = new Outcome(state, detail, _clock.GetUtcNow());
    }

    /// <summary>Whether <paramref name="command"/>'s last update check is older than the interval, or there is none.</summary>
    public bool Due(string command)
    {
        var checks = ReadChecks(_dataRoot);
        return !checks.TryGetValue(command, out var at) || _clock.GetUtcNow() - at >= _interval();
    }

    private void Checked(string command)
    {
        lock (_lock)
        {
            var checks = ReadChecks(_dataRoot);
            checks[command] = _clock.GetUtcNow();
            WriteChecks(_dataRoot, checks, _log);
        }
    }

    /// <summary>When each command was last checked for an update, from <see cref="ChecksFile"/>; empty when there is none.</summary>
    public static Dictionary<string, DateTimeOffset> ReadChecks(string dataRoot)
    {
        try
        {
            var path = Path.Combine(dataRoot, ChecksFile);
            return File.Exists(path)
                ? JsonSerializer.Deserialize<Dictionary<string, DateTimeOffset>>(File.ReadAllText(path)) is { } read
                    ? new Dictionary<string, DateTimeOffset>(read, StringComparer.Ordinal)
                    : new(StringComparer.Ordinal)
                : new(StringComparer.Ordinal);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return new(StringComparer.Ordinal);
        }
    }

    /// <summary>Writes <see cref="ChecksFile"/>; never throws.</summary>
    public static void WriteChecks(string dataRoot, IReadOnlyDictionary<string, DateTimeOffset> checks, ILogger? log = null)
    {
        var path = Path.Combine(dataRoot, ChecksFile);
        try
        {
            var temp = path + "." + Environment.CurrentManagedThreadId + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(checks));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            log?.LogWarning("Agent CLIs: could not record the update checks in {Path}: {Error}", path, exception.Message);
        }
    }

    /// <summary>What each command and Google Chrome are doing now, as <see cref="AgentInstallsRecord"/> says it.</summary>
    public AgentInstallsRecord Now()
    {
        var items = new List<AgentInstallItem>();
        foreach (var command in _commands())
        {
            Outcome? outcome;
            lock (_lock) _outcomes.TryGetValue(command.Name, out outcome);

            var item = _gate.Holding(command.Name) is { } hold
                ? new AgentInstallItem(
                    command.Agent, command.Name,
                    hold.Kind switch
                    {
                        AgentUpdateKinds.Install => AgentInstallWords.Installing,
                        AgentUpdateKinds.Check => AgentInstallWords.Checking,
                        _ => AgentInstallWords.Updating,
                    },
                    UpdatingOn.Text(hold), hold.Worker, hold.Since)
                : outcome is { } known
                    ? new AgentInstallItem(command.Agent, command.Name, known.State, known.Detail, null, known.At)
                    : new AgentInstallItem(command.Agent, command.Name, AgentInstallWords.Checking, null, null, null);

            items.Add(item);
        }

        return new AgentInstallsRecord(_clock.GetUtcNow(), _worker, items, GoogleChromeStatus.Read(_chromeRoot ?? Path.Combine(_dataRoot, "google-chrome")));
    }

    /// <summary>Writes <see cref="Now"/> when it changed. Never throws.</summary>
    public void Record()
    {
        try
        {
            var now = Now();
            var key = JsonSerializer.Serialize(now with { RecordedAt = default });
            lock (_lock)
            {
                if (key == _lastWritten) return;
                _lastWritten = key;
            }

            now.Write(_dataRoot, _log);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _log?.LogWarning("Agent CLIs: could not record the installs: {Message}", exception.Message);
        }
    }

    public void Dispose()
    {
        foreach (var timer in _timers) timer.Dispose();
    }
}

/// <summary>The words an item of <see cref="AgentInstallsRecord"/> takes for its state (the doctor and <c>yawble up</c> read them).</summary>
public static class AgentInstallWords
{
    /// <summary>Being installed: launches of it wait.</summary>
    public const string Installing = "installing";

    /// <summary>Not yet known whether it is installed: control is waiting for a worker to say.</summary>
    public const string Checking = "checking";

    /// <summary>Installed and not held: launches go.</summary>
    public const string Ready = "ready";

    /// <summary>The install ran and failed (a vendor unreachable): launches go, and find no program.</summary>
    public const string Failed = "failed";

    /// <summary>Measured missing and not being installed.</summary>
    public const string Missing = "missing";

    /// <summary>Being updated: launches of it wait.</summary>
    public const string Updating = "updating";
}

/// <summary>One command in <see cref="AgentInstallsRecord"/>: its preset, its state and, while held, the gate's sentence.</summary>
public sealed record AgentInstallItem(string Agent, string Command, string State, string? Detail, string? Worker, DateTimeOffset? Since);

/// <summary>Google Chrome for the sign-in page, as its installer's status file says: a state, a percent while it downloads, a sentence.</summary>
public sealed record GoogleChromeStatus(string State, int? Percent, string? Detail)
{
    /// <summary>The installer's status file, in Chrome's own folder (install-google-chrome.sh).</summary>
    public const string FileName = ".status";

    /// <summary>The status in <paramref name="root"/>, or null when its installer has written none.</summary>
    public static GoogleChromeStatus? Read(string root)
    {
        try
        {
            var path = Path.Combine(root, FileName);
            if (!File.Exists(path)) return null;
            var line = File.ReadAllLines(path).FirstOrDefault(l => l.Trim().Length > 0)?.Trim();
            if (line is null) return null;

            var parts = line.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            var state = parts[0];
            int? percent = parts.Length > 1 && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var p) ? Math.Clamp(p, 0, 100) : null;
            var detail = percent is null ? string.Join(' ', parts.Skip(1)) : parts.Length > 2 ? parts[2] : null;
            return new GoogleChromeStatus(state, percent, string.IsNullOrWhiteSpace(detail) ? null : detail);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

/// <summary>
/// WHAT THE AGENT CLIS AND GOOGLE CHROME ARE DOING, for the doctor (another process) and <c>yawble up</c>, written to
/// <see cref="FileName"/> whenever it changes. <see cref="Worker"/> is the worker that joined first, where they were measured.
/// </summary>
public sealed record AgentInstallsRecord(
    DateTimeOffset RecordedAt, string? Worker, IReadOnlyList<AgentInstallItem> Items, GoogleChromeStatus? GoogleChrome)
{
    public const string FileName = "agent-installs.json";

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>Never throws.</summary>
    public void Write(string dataRoot, ILogger? log = null)
    {
        var path = Path.Combine(dataRoot, FileName);
        try
        {
            var temp = path + "." + Environment.CurrentManagedThreadId + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(this, Json));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            log?.LogWarning("Agent CLIs: could not record the installs in {Path}: {Error}", path, exception.Message);
        }
    }

    /// <summary>The last record, or null when there is none or it cannot be read.</summary>
    public static AgentInstallsRecord? Read(string dataRoot)
    {
        try
        {
            var path = Path.Combine(dataRoot, FileName);
            return File.Exists(path) ? JsonSerializer.Deserialize<AgentInstallsRecord>(File.ReadAllText(path), Json) : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }
}
