using Yawble.Core.Bus;
using Yawble.Core.Sandboxing;
using Yawble.Core.Policy;

namespace Yawble.Core.Launch;

// The seams to the credentials, gateway, bridge and transcripts (Credentials/, Gateway/, bridge/, Transcripts/). Each is small on purpose: the launcher
// needs a port and a run token before it compiles, a verdict after the run, and a credential mode before it starts.
// The real implementations are in GatewayWiring.cs (wired in Program.cs); the launcher tests also run with fakes
// (core/tests/.../Launch/LaunchFakes.cs).

/// <summary>
/// Who a run is, for the gateway: the bus identity that the run token stands for. RunNumber keys the run in
/// the gateway's token registry, the gateway log and the transcripts: the bus run id, or for a run with no bus run
/// (an interactive session) a negative number unique to this core.
/// </summary>
public sealed record RunIdentity(
    string RunKey,
    long RunNumber,
    long? BusRunId,
    string Team,
    string Member,
    AgentCli Cli,
    CredentialMode Credential,
    IReadOnlyList<string> AllowHosts,
    TimeSpan TokenLifetime,
    RunContext? Bus,
    Gateway.IBusEndpoint? Tools = null);

/// <summary>Starts the per-run gateway on 127.0.0.1 (IPv4 only) before the request is compiled.</summary>
public interface IRunGatewayFactory
{
    /// <summary>
    /// Binds the run's gateway. Every bridge tool call the gateway relays to the bus must call
    /// <see cref="RunActivity.Touch"/>: that is what resets the idle watchdog (progress).
    /// </summary>
    Task<IRunGateway> StartAsync(RunIdentity run, RunActivity activity, CancellationToken ct);
}

public interface IRunGateway : IAsyncDisposable
{
    int Port { get; }

    /// <summary>
    /// The run token the gateway issued for this run: random, bound to the run, revoked when the gateway is disposed.
    /// It is also every placeholder credential in the sandbox and the bridge's bus identity.
    /// </summary>
    string RunToken { get; }

    /// <summary>The compiled plan: CONNECT allowlist and vhosts (with the credential to swap in). Before launch.</summary>
    void Configure(GatewayPlan plan);

    /// <summary>
    /// What the gateway saw, read after the sandbox exited. A credential the upstream rejected is reported from
    /// here and never from the CLI's exit code: grok -p exits 0 on a revoked credential (on Linux).
    /// </summary>
    GatewayVerdict Verdict();
}

/// <summary>
/// CredentialRejected: the upstream refused the swapped-in credential, or the gateway had none to swap in (no Grok
/// session the keeper could refresh). Reason is the gateway's own wording for the card, when it has one.
/// </summary>
public sealed record GatewayVerdict(bool CredentialRejected, int DeniedConnects, IReadOnlyList<string> DeniedHosts, string? Reason = null)
{
    /// <summary>The model API limited the run (429) and no later request on it went through: when it said to come back.</summary>
    public DateTimeOffset? RateLimitedUntil { get; init; }

    public static GatewayVerdict Clean { get; } = new(false, 0, []);

    /// <summary>The card reason for a rejected credential.</summary>
    public const string CredentialReason = "credential expired or revoked";
}

/// <summary>Which credential each CLI uses on this machine (the person's choice in Settings → Model credentials).</summary>
public interface ICredentialModes
{
    /// <summary>Null when the person has not set one up: the run fails at once with a reason the card shows.</summary>
    CredentialMode? ModeFor(AgentCli cli);
}

/// <summary>
/// The idle clock. Touched by every bridge tool call; read by the watchdog. Held while the run waits in the heavy-work
/// lease's queue (the watchdog then sees no idle time); resumed with a full window when the wait ends.
/// </summary>
public sealed class RunActivity(TimeProvider clock) : Bus.IRunIdleClock
{
    private long _lastTicks = clock.GetUtcNow().UtcTicks;
    private volatile bool _held;

    public DateTimeOffset Last => _held ? clock.GetUtcNow() : new(Interlocked.Read(ref _lastTicks), TimeSpan.Zero);

    public bool Held => _held;

    public void Touch() => Interlocked.Exchange(ref _lastTicks, clock.GetUtcNow().UtcTicks);

    public void Hold() => _held = true;

    public void Resume()
    {
        Touch();
        _held = false;
    }
}

/// <summary>Where a run's terminal output goes (the replay buffer).</summary>
public interface IRunOutput
{
    void Write(ReadOnlySpan<byte> data);
}
