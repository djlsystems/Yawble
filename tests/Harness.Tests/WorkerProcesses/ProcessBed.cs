using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using Harness.Contracts;
using Harness.Host;

namespace Harness.Tests;

/// <summary>
/// CONTROL AND WORKERS AS REAL PROCESSES, on one data root, with a fake agent CLI. Every process the
/// bed starts is recorded by its PID when it starts - control, each worker, each fake CLI from its pid
/// file - and only those are ever stopped, by that PID (a fake CLI's after its start time is checked
/// against the one recorded). Nothing here stops a process by name or pattern.
/// </summary>
/// <remarks>
/// <para>
/// Each child's stdin is closed at once and its stdout and stderr are drained into a bounded buffer,
/// so a chatty log never blocks it and its last lines are attached to a failure. Each child's
/// environment is built on its own <see cref="ProcessStartInfo.Environment"/>, never this process's.
/// </para>
/// <para>
/// The fake CLI behaves by its member's name: a name with <c>Block</c> waits for its go file (600 s,
/// far past any wait of a test); one with <c>Heavy</c> takes the <c>heavy</c> lease first. Every run
/// writes, under <see cref="Out"/>, its PID, its parent chain, its progress call's HTTP status, its
/// lease answer and its environment, and an <c>.ended</c> file only when it ends by itself.
/// </para>
/// </remarks>
internal sealed class ProcessBed : IAsyncDisposable
{
    public static readonly TimeSpan Bound = TimeSpan.FromSeconds(60);

    private readonly List<HostProcess> _hosts = [];
    private readonly Dictionary<int, long> _fakeStarts = [];
    private readonly CookieContainer _cookies = new();

    public ProcessBed()
    {
        Work = Directory.CreateTempSubdirectory("worker-bed-").FullName;
        Root = Directory.CreateDirectory(Path.Combine(Work, "root")).FullName;
        Out = Directory.CreateDirectory(Path.Combine(Work, "out")).FullName;
        GoFolder = Directory.CreateDirectory(Path.Combine(Work, "go")).FullName;
        State = Directory.CreateDirectory(Path.Combine(Work, "state")).FullName;
        Fake = Path.Combine(Work, "fake.sh");
        File.WriteAllText(Fake, FakeScript());

        AgentCatalogFile.Save(Root,
        [
            new AgentDefinition("fake", AgentMode.Headless, new AgentLaunch("sh", [Fake], UsageFormat: "claude-json", LanguageModel: false)),
        ]);
    }

    public string Work { get; }

    public string Root { get; }

    public string Out { get; }

    public string GoFolder { get; }

    /// <summary>Where the workers keep their record of their runs' groups.</summary>
    public string State { get; }

    public string Fake { get; }

    public string Key { get; } = "bed-key-" + Guid.NewGuid().ToString("N");

    public Uri Url { get; private set; } = null!;

    public HostProcess Control { get; private set; } = null!;

    public Dictionary<string, HostProcess> Workers { get; } = [];

    public HttpClient Person { get; private set; } = null!;

    private static string Dll => Path.Combine(AppContext.BaseDirectory, "Harness.Host.dll");

    /// <summary>
    /// Control on a free loopback port, bootstrapped with one person signed in; under a prefix (another
    /// user, which must be able to write <see cref="Root"/>) and with more environment, when given.
    /// </summary>
    public async Task StartControlAsync(
        int graceSeconds = 2, int keepAliveSeconds = 1, IReadOnlyList<string>? prefix = null, IReadOnlyDictionary<string, string>? more = null)
    {
        var environment = (more ?? new Dictionary<string, string>()).ToDictionary(p => p.Key, string? (p) => p.Value);

        for (var attempt = 0; ; attempt++)
        {
            var port = FreePort();
            Url = new Uri($"http://127.0.0.1:{port}");
            Control = Start("control", [
                .. prefix ?? [], "dotnet", Dll, "--Role", "control", "--DataRoot", Root, "--urls", Url.ToString().TrimEnd('/'),
                "--Workers:Key", Key, "--Workers:GraceSeconds", $"{graceSeconds}", "--Workers:KeepAliveSeconds", $"{keepAliveSeconds}",

                // Its own lines, not the request log of the bed's polling, are what a failure shows.
                "--Logging:LogLevel:Microsoft.AspNetCore", "Warning",
            ], environment);

            if (await ReadyAsync()) break;
            if (attempt == 1 || !Control.Exited) throw new TimeoutException($"Control did not answer /api/version within {Bound.TotalSeconds:0} s:\n{Control.Text()}");
        }

        Person = new HttpClient(new HttpClientHandler { CookieContainer = _cookies }) { BaseAddress = Url, Timeout = Bound };
        var person = new { email = "person@example.test", password = "correct horse battery" };
        (await Person.PostAsJsonAsync("/api/auth/admin", person)).EnsureSuccessStatusCode();
        (await Person.PostAsJsonAsync("/api/auth/login", person)).EnsureSuccessStatusCode();
    }

    /// <summary>A worker process, optionally reading a cgroup fixture, under a prefix (another user), in a working directory, with another key, with more environment.</summary>
    public HostProcess StartWorker(string id, string? cgroup = null, IReadOnlyList<string>? prefix = null, string? key = null, string? cwd = null, IReadOnlyDictionary<string, string>? more = null)
    {
        var environment = new Dictionary<string, string?>
        {
            ["HARNESS_CONTROL_URL"] = Url.ToString().TrimEnd('/'),
            ["HARNESS_WORKER_KEY"] = key ?? Key,
            ["HARNESS_WORKER_ID"] = id,

            // Its record of its runs' groups is the bed's, never a file another test or team shares.
            ["HARNESS_WORKER_STATE_DIR"] = State,
        };
        if (cgroup is not null) environment["Capacity__CgroupRoot"] = cgroup;
        foreach (var (name, value) in more ?? new Dictionary<string, string>()) environment[name] = value;

        var worker = Start(id, [.. prefix ?? [], "dotnet", Dll, "--Role", "worker"], environment, cwd);
        Workers[id] = worker;
        return worker;
    }

    /// <summary>A team on the fake CLI, its Manager and these members.</summary>
    public async Task<string> TeamAsync(string name, params string[] members)
    {
        var created = await Person.PostAsJsonAsync("/api/teams", new { name, agent = "fake", memberAgent = "fake" });
        created.EnsureSuccessStatusCode();
        var team = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;

        foreach (var member in members)
        {
            (await Person.PostAsJsonAsync($"/api/teams/{team}/containers", new { name = member, agent = "fake" })).EnsureSuccessStatusCode();
        }

        return team;
    }

    public Task<HttpResponseMessage> TellAsync(string team, string member, string instruction) =>
        Person.PostAsJsonAsync($"/api/teams/{team}/containers/{member}/tell", new { instruction });

    public async Task<JsonElement> GetAsync(string path) => await Person.GetFromJsonAsync<JsonElement>(path);

    /// <summary>Every row of the log, oldest first.</summary>
    public async Task<IReadOnlyList<Row>> RowsAsync()
    {
        var rows = await Person.GetFromJsonAsync<JsonElement>("/api/messages?take=2000");
        return [.. rows.EnumerateArray().Select(Row.Of).OrderBy(r => r.Seq)];
    }

    /// <summary>The rows of one member's runs, of these types.</summary>
    public async Task<IReadOnlyList<Row>> RowsOfAsync(string team, string member, params string[] types) =>
        [.. (await RowsAsync()).Where(r => r.Source.Equals($"{team}/{member}", StringComparison.OrdinalIgnoreCase) && (types.Length == 0 || types.Contains(r.Type)))];

    /// <summary>The worker ids a capacity sample or /api/workers names, with their state.</summary>
    public async Task<IReadOnlyList<JsonElement>> WorkersAsync() => [.. (await GetAsync("/api/workers")).EnumerateArray()];

    /// <summary>Every run of the fake CLI so far, read from its files.</summary>
    public IReadOnlyList<FakeRun> FakeRuns()
    {
        var runs = new List<FakeRun>();
        foreach (var pidFile in Directory.GetFiles(Out, "*.pid"))
        {
            var run = Path.GetFileNameWithoutExtension(pidFile);
            if (!int.TryParse(Read(pidFile)?.Trim(), out var pid)) continue;

            lock (_fakeStarts)
            {
                if (!_fakeStarts.ContainsKey(pid) && StartTicks(pid) is { } ticks) _fakeStarts[pid] = ticks;
            }

            runs.Add(new FakeRun(
                run, run[..run.LastIndexOf('-')], pid,
                [.. (Read(Path.Combine(Out, run + ".chain")) ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse)],
                Read(Path.Combine(Out, run + ".progress"))?.Trim(),
                Read(Path.Combine(Out, run + ".lease"))?.Trim(),
                Read(Path.Combine(Out, run + ".env")) ?? "",
                File.Exists(Path.Combine(Out, run + ".ended"))));
        }

        return runs;
    }

    /// <summary>Lets every blocking run of <paramref name="member"/> go.</summary>
    public void Go(string member) => File.WriteAllText(Path.Combine(GoFolder, member), "");

    /// <summary>Sends <paramref name="signal"/> (KILL, STOP, CONT) to a process this bed started, by its PID.</summary>
    public static void Signal(HostProcess process, string signal) => Signal(process.Pid, signal);

    private static void Signal(int pid, string signal)
    {
        using var kill = Process.Start(new ProcessStartInfo("kill", ["-" + signal, pid.ToString()]) { UseShellExecute = false })!;
        kill.WaitForExit(10_000);
    }

    /// <summary>Whether a process is still alive.</summary>
    public static bool Alive(int pid) => Directory.Exists($"/proc/{pid}") && !(Read($"/proc/{pid}/stat")?.Contains(") Z ") ?? true);

    /// <summary>Polls <paramref name="condition"/> every 100 ms until it holds, failing after <see cref="Bound"/> with what was last seen and the hosts' last lines.</summary>
    public async Task UntilAsync(string what, Func<Task<bool>> condition, TimeSpan? bound = null)
    {
        var deadline = DateTime.UtcNow + (bound ?? Bound);
        Exception? last = null;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if (await condition()) return;
                last = null;
            }
            catch (Exception exception) when (exception is HttpRequestException or JsonException or IOException or TaskCanceledException)
            {
                last = exception;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException($"Not true within {(bound ?? Bound).TotalSeconds:0} s: {what}. {last?.Message}\n{Logs()}");
    }

    public string Logs() => string.Join("\n", _hosts.Select(h => $"--- {h.Name} (pid {h.Pid}{(h.Exited ? ", exited" : "")}) ---\n{h.Text(60)}"));

    public async ValueTask DisposeAsync()
    {
        // 1. Each fake CLI this bed recorded, by PID, after its start time is checked.
        FakeRuns();
        lock (_fakeStarts)
        {
            foreach (var (pid, ticks) in _fakeStarts)
            {
                if (StartTicks(pid) == ticks) Signal(pid, "KILL");
            }
        }

        // 2. Workers, then control; each one this bed started, with its own process tree.
        foreach (var host in _hosts.Where(h => h != Control).Append(Control).OfType<HostProcess>())
        {
            host.Stop();
        }

        foreach (var host in _hosts) await host.WaitAsync(TimeSpan.FromSeconds(10));

        Person?.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(Work, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Left to the system's temp clearing: a file another user wrote.
        }
    }

    private HostProcess Start(string name, IReadOnlyList<string> command, Dictionary<string, string?> environment, string? cwd = null)
    {
        var start = new ProcessStartInfo(command[0])
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = cwd ?? Work,
        };
        foreach (var argument in command.Skip(1)) start.ArgumentList.Add(argument);

        start.Environment.Remove("HARNESS_DATA_ROOT");
        start.Environment.Remove("DataRoot");
        start.Environment.Remove(HostRoles.Variable);
        foreach (var (key, value) in environment)
        {
            if (value is null) start.Environment.Remove(key);
            else start.Environment[key] = value;
        }

        var host = new HostProcess(name, start);
        _hosts.Add(host);
        return host;
    }

    private async Task<bool> ReadyAsync()
    {
        using var probe = new HttpClient { BaseAddress = Url, Timeout = TimeSpan.FromSeconds(5) };
        var deadline = DateTime.UtcNow + Bound;
        while (DateTime.UtcNow < deadline && !Control.Exited)
        {
            try
            {
                if ((await probe.GetAsync("/api/version")).StatusCode == HttpStatusCode.OK) return true;
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
            {
            }

            await Task.Delay(100);
        }

        return false;
    }

    private string FakeScript() =>
        $$$"""
        #!/bin/sh
        out='{{{Out}}}'
        run="$HARNESS_MEMBER-$$"
        p=$$; : > "$out/$run.chain.tmp"
        while [ -n "$p" ] && [ "$p" -gt 1 ]; do echo "$p" >> "$out/$run.chain.tmp"; p=$(sed 's/.*) //' /proc/$p/stat 2>/dev/null | cut -d' ' -f2); done
        mv "$out/$run.chain.tmp" "$out/$run.chain"
        env > "$out/$run.env"
        curl -s -o /dev/null -w '%{http_code}' -X POST -H "X-Api-Key: $HARNESS_KEY" -H 'Content-Type: application/json' \
          -d '{"status":"working on the worker"}' "$HARNESS_URL/api/teams/$HARNESS_TEAM/containers/$HARNESS_MEMBER/progress" > "$out/$run.progress"
        case "$HARNESS_MEMBER" in *Heavy*)
          curl -s -X POST -H "X-Api-Key: $HARNESS_KEY" -H 'Content-Type: application/json' \
            -d '{"action":"acquire","name":"heavy"}' "$HARNESS_URL/api/me/lease" > "$out/$run.lease";;
        esac
        echo $$ > "$out/$run.pid"
        case "$HARNESS_MEMBER" in *Block*)
          i=0; while [ ! -e '{{{GoFolder}}}'/"$HARNESS_MEMBER" ] && [ $i -lt 12000 ]; do sleep 0.05; i=$((i+1)); done;;
        esac
        touch "$out/$run.ended"
        printf '%s' '{"result":"done","usage":{"input_tokens":7,"output_tokens":11}}'
        """;

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static string? Read(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>A process's start time in clock ticks since boot, from <c>/proc/&lt;pid&gt;/stat</c>; null when it is gone.</summary>
    private static long? StartTicks(int pid) =>
        Read($"/proc/{pid}/stat") is { } stat && stat[(stat.LastIndexOf(')') + 2)..].Split(' ') is { Length: > 19 } fields
            && long.TryParse(fields[19], out var ticks)
            ? ticks
            : null;

    /// <summary>One row of the log.</summary>
    public sealed record Row(long Seq, string Type, string Source, long? CausationSeq, DateTimeOffset At, JsonElement Payload)
    {
        public static Row Of(JsonElement row) => new(
            row.GetProperty("seq").GetInt64(),
            row.GetProperty("type").GetString()!,
            row.GetProperty("source").GetString()!,
            row.TryGetProperty("causationSeq", out var causation) && causation.ValueKind == JsonValueKind.Number ? causation.GetInt64() : null,
            row.GetProperty("occurredAt").GetDateTimeOffset(),
            Parse(row.GetProperty("payload")));

        public string? FailureClass =>
            Payload.ValueKind == JsonValueKind.Object && Payload.TryGetProperty(PayloadFields.FailureClass, out var value) ? value.GetString() : null;

        private static JsonElement Parse(JsonElement payload) =>
            payload.ValueKind == JsonValueKind.String && payload.GetString() is { Length: > 0 } text && text.TrimStart().StartsWith('{')
                ? JsonDocument.Parse(text).RootElement.Clone()
                : payload.Clone();
    }

    /// <summary>One run of the fake CLI: its file name, member, PID, parent chain, progress status, lease answer, environment, and whether it ended by itself.</summary>
    public sealed record FakeRun(string Name, string Member, int Pid, IReadOnlyList<int> Chain, string? Progress, string? Lease, string Environment, bool Ended);
}

/// <summary>One process the bed started: its recorded PID, and its output drained into a bounded buffer.</summary>
internal sealed class HostProcess
{
    private readonly Process _process;
    private readonly Queue<string> _lines = new();

    public HostProcess(string name, ProcessStartInfo start)
    {
        Name = name;
        _process = new Process { StartInfo = start, EnableRaisingEvents = true };
        _process.OutputDataReceived += (_, e) => Keep(e.Data);
        _process.ErrorDataReceived += (_, e) => Keep(e.Data);
        if (!_process.Start()) throw new InvalidOperationException($"{name} did not start.");
        Pid = _process.Id;
        _process.StandardInput.Close();
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
    }

    public string Name { get; }

    public int Pid { get; }

    public bool Exited => _process.HasExited;

    public int ExitCode => _process.ExitCode;

    public string Text(int last = int.MaxValue)
    {
        lock (_lines) return string.Join("\n", _lines.TakeLast(last));
    }

    /// <summary>Stops this process and every child of it, by its recorded PID's own tree.</summary>
    public void Stop()
    {
        try
        {
            if (!_process.HasExited) _process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
    }

    /// <summary>SIGKILL to this process alone, as a crash would: its children are left as a crash leaves them.</summary>
    public void KillAlone()
    {
        if (!_process.HasExited) _process.Kill(entireProcessTree: false);
    }

    public async Task WaitAsync(TimeSpan bound)
    {
        try
        {
            await _process.WaitForExitAsync().WaitAsync(bound);
        }
        catch (TimeoutException)
        {
        }
    }

    private void Keep(string? line)
    {
        if (line is null) return;
        lock (_lines)
        {
            _lines.Enqueue(line);
            while (_lines.Count > 2000) _lines.Dequeue();
        }
    }
}
