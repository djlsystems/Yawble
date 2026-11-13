using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Yawble.Core.Guardrail;
using Yawble.Core.Launch;
using Yawble.Core.Sandboxing;

namespace Yawble.Core.Ipc;

/// <summary>The methods the shell may call: the core's identity, the sandbox's backend probe, and every screen's methods.</summary>
public sealed class RpcDispatcher(
    ISandboxPlatformInfo sandbox,
    Func<IReadOnlyList<string>> appliedMigrations,
    RunRegistry? runs = null,
    GuardrailService? guardrail = null,
    Ui.UiRpc? ui = null,
    IReadOnlyList<HostCheck>? hostChecks = null,
    Func<CancellationToken, Task<Scheduling.SchedulerSnapshot>>? scheduler = null,
    Func<string, string, CancellationToken, Task<bool>>? stopQueued = null,
    Agents.ICliDetector? agents = null,
    Agents.AgentTestSessions? agentTests = null,
    Link.CloudLink? link = null,
    Concierge.ConciergeService? concierge = null,
    Board.BoardService? board = null,
    Provenance.AuditLog? audit = null,
    Integrations.IntegrationService? integrations = null,
    Storage.BackupRpc? backups = null,
    Ops.DoctorRpc? doctor = null,
    Agents.Profiles.AgentProfileRpc? agentProfiles = null,
    Agents.AgentInfoRpc? agentInfo = null,
    Companion.CompanionRpc? companion = null,
    Code.CodeBrowserRpc? code = null,
    Teams.GitRpc? git = null,
    Logs.LogRpc? logs = null,
    Updates.UpdateRpc? updates = null)
{
    /// <summary>
    /// A call as the person (or a cloud viewer) made it: run as <paramref name="actor"/>, so whatever it records carries
    /// who and how, and written to the audit log when it changes something.
    /// </summary>
    public async Task<JsonNode?> DispatchAsAsync(Provenance.Actor actor, string method, JsonObject? parameters, CancellationToken ct)
    {
        using var acting = Provenance.Actor.Use(actor);
        try
        {
            var result = await DispatchAsync(method, parameters, ct).ConfigureAwait(false);
            audit?.Record(actor, method, parameters, "done");
            return result;
        }
        catch (RpcException e)
        {
            audit?.Record(actor, method, parameters, $"refused: {e.Code}: {e.Message}");
            throw;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            audit?.Record(actor, method, parameters, $"failed: {e.GetType().Name}");
            throw;
        }
    }

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public Task<JsonNode?> DispatchAsync(string method, JsonObject? parameters, CancellationToken ct) =>
        method switch
        {
            "core.getInfo" => Task.FromResult(GetInfo()),
            // Settings > System > About: this build's version, and the cloud's when the Cloud Link is connected.
            "app.version" => Task.FromResult(AppVersion()),
            "sandbox.getAvailableBackends" => Task.FromResult(Serialize(sandbox.GetAvailableBackends())),
            "sandbox.getPlatformSupport" => Task.FromResult(Serialize(sandbox.GetPlatformSupport())),
            // Global running / limit, the queue with "Queued · position N of M", per-team counts.
            "scheduler.getState" when scheduler is not null => SchedulerState(ct),
            // The in-app guardrail check (GuardrailService documents the shape). Starts, then the window polls.
            "guardrail.start" => Task.FromResult<JsonNode?>(Guardrail().Start(parameters)),
            "guardrail.get" => Task.FromResult<JsonNode?>(Guardrail().Get(parameters)),
            "guardrail.stop" => StopGuardrailAsync(parameters),
            // The Agents dialog: every supported CLI as installed on this host right now (no cache), and its Test.
            "agents.detect" => DetectAgentsAsync(ct),
            "agents.test" => TestAgentAsync(parameters, ct),
            "agents.stopTest" => StopAgentTestAsync(parameters),
            "agents.testSessions" => AgentTestSessionsList(),
            "runs.list" => Task.FromResult(ListRuns()),
            // The Cloud Link (optional, generic): attach this instance to a cloud site by a pairing code.
            "link.status" => Task.FromResult<JsonNode?>(Link().Status()),
            "link.pair" => PairAsync(parameters, ct),
            "link.cancel" => Task.FromResult<JsonNode?>(Then(() => Link().CancelPairing())),
            "link.forget" => ForgetAsync(ct),
            "link.setAllowHostTerminals" => Task.FromResult<JsonNode?>(Then(() => Link().SetAllowHostTerminals(parameters?["allow"]?.GetValue<bool>() ?? false))),
            // Settings > Cloud Link, "Show members' code to cloud viewers" (the window only: link. is refused remotely).
            "link.setShowCode" => Task.FromResult<JsonNode?>(Then(() => Link().SetShowCode(parameters?["show"]?.GetValue<bool>() ?? true))),
            "runs.stop" => StopRunAsync(parameters, ct),
            // The git on this computer: the check, and installing or updating it (Teams/GitRpc.cs).
            _ when git is not null && Teams.GitRpc.Handles(method) => git.DispatchAsync(method, parameters, ct),
            // The Logs dialog: every log, its entries, pruning and retention (Logs/LogRpc.cs).
            _ when logs is not null && Logs.LogRpc.Handles(method) => logs.DispatchAsync(method, parameters, ct),
            // Updates to the app itself (Updates/UpdateRpc.cs).
            _ when updates is not null && Updates.UpdateRpc.Handles(method) => updates.DispatchAsync(method, parameters, ct),
            // The read-only code browser of members' clones (Code/CodeBrowserRpc.cs).
            _ when code is not null && Yawble.CodeBrowser.CodeBrowserService.Handles(method) => code.DispatchAsync(method, parameters, ct),
            // The screens' methods (teams, members, policy, credentials, terminals).
            // Backups: list, back up now, restore (the person's window only).
            _ when backups is not null && Storage.BackupRpc.Handles(method) => backups.DispatchAsync(method, parameters, ct),
            // The yawble companion (Companion/CompanionRpc.cs): pairings, approvals and revocations, the window on this computer only.
            _ when companion is not null && Companion.CompanionRpc.Handles(method) => companion.DispatchAsync(method, parameters, ct),
            _ when ui is not null && Ui.UiRpc.Handles(method) => ui.DispatchAsync(method, parameters, ct),
            // The Concierge: the person's chats with an agent that knows every team (Concierge/ConciergeService.cs).
            _ when concierge is not null && Concierge.ConciergeService.Handles(method) => concierge.DispatchAsync(method, parameters, ct),
            // The Board: cards in four lanes and the backlog (Board/BoardService.cs).
            _ when board is not null && Board.BoardService.Handles(method) => board.DispatchAsync(method, parameters, ct),
        // Custom agents and the agent/model change history (local only: not in RemoteAccess).
        _ when agentProfiles is not null && Agents.Profiles.AgentProfileRpc.Handles(method) => agentProfiles.DispatchAsync(method, parameters, ct),
            // Host tasks: the person's definitions, approvals and runs; refused for any actor but the window or a cloud viewer.
            _ when integrations?.HostTasks is not null && Tasks.HostTaskRpc.Handles(method) => integrations.HostTasks.DispatchAsync(method, parameters, ct),
            // Integrations: the person's CLIs, their rules, and the calls waiting for approval.
            _ when integrations is not null && Integrations.IntegrationService.Handles(method) => integrations.DispatchAsync(method, parameters, ct),
            // The doctor: health checks and their fixes (Ops/DoctorRpc.cs). Not offered over the Cloud Link.
            // Command line, default model, run records, versions and updates (Agents/AgentInfoRpc.cs).
            _ when agentInfo is not null && Agents.AgentInfoRpc.Handles(method) => agentInfo.DispatchAsync(method, parameters, ct),
            _ when doctor is not null && Ops.DoctorRpc.Handles(method) => doctor.DispatchAsync(method, parameters, ct),
            _ => throw new RpcException("unknown_method", $"no method named '{method}'"),
        };

    private Link.CloudLink Link() =>
        link ?? throw new RpcException("unavailable", "the Cloud Link is not wired in this core");

    private async Task<JsonNode?> PairAsync(JsonObject? p, CancellationToken ct) =>
        await Link().StartPairingAsync(p?["url"]?.GetValue<string>(), ct);

    private async Task<JsonNode?> ForgetAsync(CancellationToken ct)
    {
        await Link().ForgetAsync(ct);
        return Link().Status();
    }

    private JsonNode? Then(Action action)
    {
        action();
        return Link().Status();
    }

    private GuardrailService Guardrail() =>
        guardrail ?? throw new RpcException("unavailable", "the guardrail check is not wired in this core");

    private async Task<JsonNode?> StopGuardrailAsync(JsonObject? parameters) => await Guardrail().StopAsync(parameters);

    /// <summary>app.version: { version, commit, dirty, released, cloudVersion } (cloudVersion null when not known).</summary>
    private JsonNode? AppVersion() => new JsonObject
    {
        ["version"] = CoreInfo.Version,
        ["commit"] = CoreInfo.Commit,
        ["dirty"] = CoreInfo.Dirty,
        ["released"] = CoreInfo.Released,
        ["cloudVersion"] = link?.CloudVersion,
    };

    private JsonNode? GetInfo() => new JsonObject
    {
        ["product"] = CoreInfo.ProductName,
        ["version"] = CoreInfo.Version,
        ["platform"] = CoreInfo.Platform,
        ["os"] = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
        ["dotnet"] = Environment.Version.ToString(),
        ["pid"] = Environment.ProcessId,
        ["sandbox"] = new JsonObject
        {
            ["sdkVersion"] = sandbox.SdkVersion,
            ["nativeVersion"] = sandbox.NativeVersion,
            ["requestContract"] = SandboxPin.RequestContract,
            ["telemetryEnabled"] = SandboxPin.TelemetryEnabled,
            // Windows only: the bundled executor the core runs under ConPTY.
            ["wxcExec"] = OperatingSystem.IsWindows() ? WxcExec() : null,
        },
        ["schema"] = new JsonArray(appliedMigrations().Select(m => (JsonNode?)m).ToArray()),
        // The startup probe: what this host still needs before an agent can start (Linux: AppArmor, bwrap).
        ["hostChecks"] = Serialize(hostChecks ?? []),
    };

    // The launcher: the live sandboxes, and the card's Stop (ends the member's whole sandbox tree).
    /// <summary>runs.list. A cloud viewer is not shown an agent's Terminal / Test session (team _agent-test): it stays in the window.</summary>
    private JsonNode? ListRuns() => new JsonArray((runs?.All() ?? [])
        .Where(r => Provenance.Actor.Current?.Via != Provenance.Actor.Cloud || r.Team != Agents.AgentTestSessions.Team)
        .Select(r => (JsonNode?)new JsonObject
    {
        ["runKey"] = r.RunKey,
        ["busRunId"] = r.BusRunId,
        ["team"] = r.Team,
        ["member"] = r.Member,
        ["kind"] = r.Kind == RunKind.Headless ? "headless" : "interactive",
        ["sandboxPid"] = r.SandboxPid,
    }).ToArray());

    /// <summary>
    /// The card's Stop. A live sandbox (headless or interactive) is ended through the launcher, whose run ends
    /// "stopped" and gives its slot back; with none live, the bus withdraws the member's interactive session that is
    /// still waiting for a slot. Queued instructions stay queued: Stop ends a sandbox, it does not drop work.
    /// </summary>
    private async Task<JsonNode?> StopRunAsync(JsonObject? p, CancellationToken ct)
    {
        var team = p?["team"]?.GetValue<string>() ?? throw new RpcException("bad_params", "team is required");
        var member = p?["member"]?.GetValue<string>() ?? throw new RpcException("bad_params", "member is required");
        var stopped = runs?.Stop(team, member) ?? false;
        if (!stopped && stopQueued is not null) stopped = await stopQueued(team, member, ct);
        return new JsonObject { ["stopped"] = stopped };
    }

    private static JsonNode WxcExec()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "wxc-exec.exe");
        return new JsonObject { ["path"] = path, ["present"] = File.Exists(path) };
    }

    private async Task<JsonNode?> DetectAgentsAsync(CancellationToken ct) =>
        Serialize(await (agents ?? throw new RpcException("unavailable", "CLI detection is not wired in this core")).DetectAllAsync(ct));

    private Agents.AgentTestSessions AgentTests() =>
        agentTests ?? throw new RpcException("unavailable", "the agent test is not wired in this core");

    private static string Cli(JsonObject? p) =>
        p?["cli"]?.GetValue<string>() is { Length: > 0 } cli ? cli : throw new RpcException("bad_params", "cli is required");

    /// <summary>agents.test {cli, agent?, diagnostic}: the CLI's (or the custom agent's) interactive session, from the person's window only.</summary>
    private async Task<JsonNode?> TestAgentAsync(JsonObject? p, CancellationToken ct)
    {
        WindowOnly();
        var agent = p?["agent"] switch
        {
            null => null,
            JsonValue v when v.TryGetValue<string>(out var s) => s,
            _ => throw new RpcException("bad_params", "agent must be a custom agent's name"),
        };
        var diagnostic = p?["diagnostic"] switch
        {
            null => false,
            JsonValue v when v.TryGetValue<bool>(out var b) => b,
            _ => throw new RpcException("bad_params", "diagnostic must be true or false"),
        };
        return await AgentTests().StartAsync(Cli(p), agent, diagnostic, ct);
    }

    private async Task<JsonNode?> StopAgentTestAsync(JsonObject? p)
    {
        WindowOnly();
        var runId = p?["runId"] switch
        {
            null => null,
            JsonValue v when v.TryGetValue<string>(out var s) && s.Length > 0 => s,
            _ => throw new RpcException("bad_params", "runId must be the run agents.test answered"),
        };
        // Through AgentTestSessions.StopAsync: it waits for the run to finish (the CLI is free again when this answers).
        return new JsonObject { ["stopped"] = await AgentTests().StopAsync(Cli(p), runId) };
    }

    /// <summary>agents.testSessions: the live Terminal / Test sessions [{cli, agent, kind, startedAt}], for the window only.</summary>
    private Task<JsonNode?> AgentTestSessionsList()
    {
        WindowOnly();
        return Task.FromResult<JsonNode?>(AgentTests().Sessions());
    }

    /// <summary>An agent's terminal is a session on this computer the person watches and types into: the window only (never the Cloud Link).</summary>
    private static void WindowOnly()
    {
        if (Provenance.Actor.Current is not { Via: Provenance.Actor.App })
            throw new RpcException("forbidden", "an agent's terminal is opened in the app's window on this computer");
    }

    private async Task<JsonNode?> SchedulerState(CancellationToken ct) => Serialize(await scheduler!(ct));

    private static JsonNode? Serialize<T>(T value) => JsonSerializer.SerializeToNode(value, Json);
}
