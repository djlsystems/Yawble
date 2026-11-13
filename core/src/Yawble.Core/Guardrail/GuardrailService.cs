using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Yawble.Core.Launch;

namespace Yawble.Core.Guardrail;

/// <summary>
/// The guardrail check as the core serves it (IPC) and as the CLI runs it. A check takes a few seconds and the IPC
/// connection answers one request at a time, so IPC starts a check and the window polls:
/// <code>
///   guardrail.start {team?, member?}  → {state:"running", subject:{team, member}}           (both omitted: the global policy)
///   guardrail.get   {team?, member?}  → {state:"none"|"running"|"done"|"failed", report?, error?}
///                                         running adds phase:"queued"|"running" and position (in the run queue, or null)
///   guardrail.stop  {team?, member?}  → {stopped:bool}  withdraws a queued check, or stops its sandbox (the report
///                                         then says run.end "stopped")
/// </code>
/// report is <see cref="GuardrailReport"/> (camelCase JSON). The last report per subject is kept in
/// &lt;data&gt;/guardrail/&lt;team&gt;__&lt;member&gt;.json, so guardrail.get answers after a restart too.
/// </summary>
public sealed class GuardrailService
{
    private readonly ConcurrentDictionary<(string, string), Task<GuardrailReport>> _running = new();
    private readonly ConcurrentDictionary<(string, string), string> _errors = new();
    private readonly ConcurrentDictionary<(string, string), (GuardrailProgress Progress, CancellationTokenSource Cancel)> _live = new();
    private readonly RunRegistry? _registry;

    private GuardrailService(GuardrailCheck? check, string? problem, RunRegistry? registry = null)
    {
        Check = check;
        Problem = problem;
        _registry = registry;
    }

    public GuardrailCheck? Check { get; }

    /// <summary>Why the check cannot run here (the launcher is off, or the probe is not installed); null when it can.</summary>
    public string? Problem { get; }

    /// <summary>
    /// The check on the core's sandbox launcher (<see cref="LaunchWiring"/>): the same layout, policy, gateway and backend
    /// as the members' runs. When the launcher is off (wrong data folder layout, no install folder) the check is off
    /// for the same reason. The probe is &lt;install&gt;/bin/yawble-guardcheck.
    /// </summary>
    public static GuardrailService Create(SandboxLauncher? launcher, string? launcherProblem, string connectionString, Action<string>? log = null,
        GuardrailRunSlot? slot = null, Func<Gateway.IGatewayListener>? decoyListener = null)
    {
        if (launcher is null) return new GuardrailService(null, "the sandbox launcher is off: " + (launcherProblem ?? "unknown reason"));
        var probe = Path.Combine(launcher.Host.InstallDir, "bin", OperatingSystem.IsWindows() ? GuardrailCheck.ProbeName + ".exe" : GuardrailCheck.ProbeName);
        if (!File.Exists(probe)) return new GuardrailService(null, $"the probe is not installed: {probe}");
        return new GuardrailService(new GuardrailCheck(launcher, connectionString, log, slot) { DecoyListener = decoyListener }, null, launcher.Registry);
    }

    private static (string, string) Key(string? team, string? member) =>
        ((team ?? GuardrailCheck.SyntheticTeam).ToLowerInvariant(), (member ?? GuardrailCheck.SyntheticMember).ToLowerInvariant());

    private static (string?, string?) Subject(JsonObject? p)
    {
        var team = p?["team"]?.GetValue<string>();
        var member = p?["member"]?.GetValue<string>();
        if ((team is null) != (member is null)) throw new Ipc.RpcException("bad_params", "give both team and member, or neither (the global policy)");
        return (team, member);
    }

    /// <summary>guardrail.start: starts a check unless one is already running for the subject.</summary>
    public JsonNode Start(JsonObject? p)
    {
        if (Check is null) throw new Ipc.RpcException("unavailable", "the guardrail check is not available: " + Problem);
        var (team, member) = Subject(p);
        var key = Key(team, member);
        _errors.TryRemove(key, out _);
        _running.AddOrUpdate(key, _ => Run(team, member, key), (_, existing) => existing.IsCompleted ? Run(team, member, key) : existing);
        return new JsonObject
        {
            ["state"] = "running",
            ["subject"] = new JsonObject { ["team"] = team, ["member"] = member },
        };
    }

    private Task<GuardrailReport> Run(string? team, string? member, (string, string) key)
    {
        var live = (new GuardrailProgress(), new CancellationTokenSource());
        _live[key] = live;
        return Task.Run(async () =>
        {
            try
            {
                return await Check!.RunAsync(team, member, live.Item2.Token, live.Item1);
            }
            catch (Exception e)
            {
                _errors[key] = live.Item2.IsCancellationRequested || e is GuardrailException { Code: "withdrawn" }
                    ? "stopped: the check was stopped before its sandbox started"
                    : e is GuardrailException g ? $"{g.Code}: {g.Message}" : $"{e.GetType().Name}: {e.Message}";
                throw;
            }
        });
    }

    /// <summary>guardrail.get: the running state, or the last report for the subject.</summary>
    public JsonNode Get(JsonObject? p)
    {
        var (team, member) = Subject(p);
        var key = Key(team, member);
        if (_running.TryGetValue(key, out var task) && !task.IsCompleted)
        {
            var progress = _live.TryGetValue(key, out var live) ? live.Progress : null;
            return new JsonObject
            {
                ["state"] = "running",
                ["phase"] = progress?.Phase ?? "queued",
                ["position"] = progress?.PositionAsync().GetAwaiter().GetResult(),
            };
        }
        if (_errors.TryGetValue(key, out var error)) return new JsonObject { ["state"] = "failed", ["error"] = error };
        if (Check?.Last(team, member) is { } report) return new JsonObject { ["state"] = "done", ["report"] = report.ToJson() };
        return new JsonObject { ["state"] = "none", ["error"] = Check is null ? Problem : null };
    }

    /// <summary>
    /// guardrail.stop: a queued check leaves the run queue; a running one has its sandbox tree ended through the run
    /// registry (as the card's Stop does), and its report says run.end "stopped". False when no check is running.
    /// </summary>
    public async Task<JsonNode> StopAsync(JsonObject? p)
    {
        var (team, member) = Subject(p);
        var key = Key(team, member);
        if (!_running.TryGetValue(key, out var task) || task.IsCompleted || !_live.TryGetValue(key, out var live))
            return new JsonObject { ["stopped"] = false };
        if (live.Progress.Phase == "queued")
        {
            await live.Progress.WithdrawAsync();
            if (live.Progress.Phase == "queued") live.Cancel.Cancel();
        }
        // Running (or admitted while we withdrew): the sandbox may take a moment to register with the launcher.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!task.IsCompleted && live.Progress.Run is { } run && !(_registry?.Stop(run.Team, run.Member) ?? false) && DateTime.UtcNow < deadline)
            await Task.Delay(50);
        await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(10)));
        return new JsonObject { ["stopped"] = true };
    }

}
