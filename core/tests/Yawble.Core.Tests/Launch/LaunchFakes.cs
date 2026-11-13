using System.Diagnostics;
using System.Text.Json.Nodes;
using Yawble.Core.Launch;
using Yawble.Core.Sandboxing;
using Yawble.Core.Policy;

namespace Yawble.Core.Tests.Launch;

/// <summary>A clock that runs <c>factor</c> times faster than real time, so 60 s policy floors pass in 1 s.</summary>
internal sealed class ScaledTimeProvider(double factor) : TimeProvider
{
    private readonly DateTimeOffset _start = DateTimeOffset.UtcNow;
    private readonly long _t0 = Stopwatch.GetTimestamp();

    public override DateTimeOffset GetUtcNow() => _start + Stopwatch.GetElapsedTime(_t0) * factor;

    public override long GetTimestamp() => _t0 + (long)((Stopwatch.GetTimestamp() - _t0) * factor);
}

/// <summary>Fixed credential modes (the credential service decides these for real).</summary>
internal sealed class FakeCredentialModes(CredentialMode? claude = CredentialMode.ClaudeSubscription, CredentialMode? grok = CredentialMode.GrokLogin) : ICredentialModes
{
    public CredentialMode? ModeFor(AgentCli cli) => cli == AgentCli.Claude ? claude : grok;
}

/// <summary>
/// Stands in for the per-run gateway: binds a real 127.0.0.1 port (so the compiled request carries a real one), keeps
/// the plan it was given, and returns a verdict the test sets. It can touch the activity clock periodically,
/// as the bridge's progress calls will.
/// </summary>
internal sealed class FakeGatewayFactory : IRunGatewayFactory
{
    public bool RejectCredential { get; set; }
    public TimeSpan? ProgressEvery { get; set; }
    public List<FakeGateway> Started { get; } = [];

    public Task<IRunGateway> StartAsync(RunIdentity run, RunActivity activity, CancellationToken ct)
    {
        var g = new FakeGateway(run, activity, this);
        lock (Started) Started.Add(g);
        return Task.FromResult<IRunGateway>(g);
    }

    internal sealed class FakeGateway : IRunGateway
    {
        private readonly FakeGatewayFactory _owner;
        private readonly CancellationTokenSource _stop = new();

        public FakeGateway(RunIdentity run, RunActivity activity, FakeGatewayFactory owner)
        {
            Run = run;
            _owner = owner;
            // A port number only: nothing in the launcher tests connects to the fake gateway, so none is bound.
            Port = Yawble.Core.Tests.Gateway.InMemoryNetwork.AllocatePort();
            if (owner.ProgressEvery is { } every)
            {
                _ = Task.Run(async () =>
                {
                    while (!_stop.IsCancellationRequested)
                    {
                        activity.Touch();
                        try { await Task.Delay(every, _stop.Token); } catch (OperationCanceledException) { }
                    }
                });
            }
        }

        public RunIdentity Run { get; }
        public string RunToken { get; } = new Yawble.Core.Gateway.RunTokenRegistry().Issue(0, TimeSpan.FromHours(1)).Value;
        public GatewayPlan? Plan { get; private set; }
        public bool Disposed { get; private set; }
        public int Port { get; }

        public void Configure(GatewayPlan plan) => Plan = plan;

        public GatewayVerdict Verdict() => _owner.RejectCredential ? new GatewayVerdict(true, 0, []) : GatewayVerdict.Clean;

        public ValueTask DisposeAsync()
        {
            _stop.Cancel();
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>
/// A backend with no OS process: each spawn runs a script against the compiled request and the launch folders.
/// Exit when the script says so; Kill ends it unless <see cref="FakeProcess.IgnoreKill"/>.
/// </summary>
internal sealed class ScriptedBackend : ISandboxBackend
{
    public string Name => "fake";
    public List<(string Json, RunKind Kind)> Requests { get; } = [];
    public Func<JsonObject, FakeProcess, Task>? Script { get; set; }
    public bool IgnoreKill { get; set; }
    public SandboxStartException? Refuse { get; set; }

    /// <summary>What each sandbox writes to its stdout (read by the supervisor's pump, into the transcript).</summary>
    public string? Stdout { get; set; }
    public List<FakeProcess> Spawned { get; } = [];

    public ISandboxProcess Spawn(string requestJson) => Start(requestJson, RunKind.Headless);

    public ISandboxProcess SpawnWithPty(string requestJson, PtySize size) => Start(requestJson, RunKind.Interactive);

    private FakeProcess Start(string json, RunKind kind)
    {
        if (Refuse is { } refusal) throw refusal;
        lock (Requests) Requests.Add((json, kind));
        var p = new FakeProcess(kind) { IgnoreKill = IgnoreKill, Stdout = Stdout is null ? null : new MemoryStream(System.Text.Encoding.UTF8.GetBytes(Stdout)) };
        lock (Spawned) Spawned.Add(p);
        var request = (JsonObject)JsonNode.Parse(json)!;
        _ = Task.Run(async () =>
        {
            if (Script is not null) await Script(request, p);
        });
        return p;
    }
}

internal sealed class FakeProcess(RunKind kind) : ISandboxProcess
{
    private static int s_pid = 40000;
    private readonly Lock _lock = new();
    private SandboxExit? _exit;
    private readonly MemoryStream _output = new();

    public bool IgnoreKill { get; set; }
    public int Id { get; } = Interlocked.Increment(ref s_pid);
    public Stream? Stdout { get; init; }
    public Stream? Output => Stdout;
    public Stream? Error => null;
    public Stream? Input => kind == RunKind.Interactive ? _output : null;
    public byte[] InputBytes { get { lock (_lock) return _output.ToArray(); } }
    public int KillCalls { get; private set; }
    public int TimeoutKillCalls { get; private set; }
    public List<PtySize> Resizes { get; } = [];

    public void Exit(int code)
    {
        lock (_lock) _exit ??= new SandboxExit(code, false);
    }

    public SandboxExit? TryWait()
    {
        lock (_lock) return _exit;
    }

    public void Kill()
    {
        KillCalls++;
        if (!IgnoreKill) lock (_lock) _exit ??= new SandboxExit(137, false);
    }

    public void KillForTimeout()
    {
        TimeoutKillCalls++;
        if (!IgnoreKill) lock (_lock) _exit ??= new SandboxExit(-1, true);
    }

    public void Resize(PtySize size) => Resizes.Add(size);

    public void Dispose()
    {
    }
}

/// <summary>
/// A real process tree in its own PID namespace (unshare --user --pid --fork --kill-child), the same mechanism the sandbox's
/// bwrap backend uses (--unshare-pid --die-with-parent): killing the outer process ends every process inside,
/// including ones that called setsid. A stand-in, not the sandbox.
/// </summary>
internal sealed class PidNamespaceBackend(Func<string> script) : ISandboxBackend
{
    public static bool Available { get; } = Probe();

    public string Name => "pidns-standin";

    public ISandboxProcess Spawn(string requestJson) => Start();

    public ISandboxProcess SpawnWithPty(string requestJson, PtySize size) => Start();

    private NamespaceProcess Start()
    {
        var info = new ProcessStartInfo("unshare")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in new[] { "--user", "--map-root-user", "--pid", "--fork", "--kill-child", "sh", "-c", script() }) info.ArgumentList.Add(a);
        var p = Process.Start(info)!;
        p.StandardInput.Close();
        return new NamespaceProcess(p);
    }

    private static bool Probe()
    {
        if (!OperatingSystem.IsLinux()) return false;
        try
        {
            using var p = Process.Start(new ProcessStartInfo("unshare", "--user --map-root-user --pid --fork --kill-child true")
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
            })!;
            return p.WaitForExit(5000) && p.ExitCode == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private sealed class NamespaceProcess(Process p) : ISandboxProcess
    {
        private bool _timedOut;

        public int Id => p.Id;
        public Stream? Output => p.StandardOutput.BaseStream;
        public Stream? Error => p.StandardError.BaseStream;
        public Stream? Input => null;

        public SandboxExit? TryWait() => p.HasExited ? new SandboxExit(_timedOut ? -1 : p.ExitCode, _timedOut) : null;

        // Only the outer process: the namespace takes the rest down. Never a process-group or tree walk here.
        public void Kill()
        {
            if (!p.HasExited) p.Kill(entireProcessTree: false);
        }

        public void KillForTimeout()
        {
            _timedOut = true;
            Kill();
        }

        public void Resize(PtySize size) { }

        public void Dispose()
        {
            Kill();
            p.Dispose();
        }
    }
}

internal static class Procs
{
    /// <summary>Processes whose command line contains the marker (Linux /proc).</summary>
    public static IReadOnlyList<int> WithMarker(string marker)
    {
        var found = new List<int>();
        foreach (var dir in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(dir), out var pid)) continue;
            try
            {
                var cmd = File.ReadAllText(Path.Combine(dir, "cmdline")).Replace('\0', ' ');
                if (cmd.Contains(marker, StringComparison.Ordinal)) found.Add(pid);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return found;
    }

    public static string NewMarker() => $"{Random.Shared.Next(70000, 99999)}.{Random.Shared.Next(10000, 99999)}";
}
