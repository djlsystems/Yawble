using Yawble.Core.Bus;
using Yawble.Core.Credentials;
using Yawble.Core.Gateway;
using Yawble.Core.Sandboxing;
using Yawble.Core.Policy;
using Yawble.Core.Transcripts;

namespace Yawble.Core.Launch;

// The launcher's seams (Seams.cs) on the real pieces: the per-run gateway and its log, the run-token
// registry, the MCP bus server behind bus.yawble.internal, the credential service with the host Grok home, and the
// transcripts. Nothing here holds a credential value: the gateway reads them per request.

/// <summary>Starts one <see cref="RunGateway"/> per run, with the run's vhosts and CONNECT allowlist.</summary>
public sealed class GatewayRunFactory(GatewayHost host) : IRunGatewayFactory
{
    public Task<IRunGateway> StartAsync(RunIdentity run, RunActivity activity, CancellationToken ct)
    {
        var cli = run.Cli.Id();
        var grokMode = run.Credential == CredentialMode.GrokApiKey ? GrokMode.ApiKey : GrokMode.Session;
        var copilotFromHost = run.Credential == CredentialMode.CopilotGatewaySignIn;
        var vhosts = new HashSet<string>(RunGatewaySpec.VhostsFor(cli, grokMode), StringComparer.OrdinalIgnoreCase);
        if (copilotFromHost) vhosts.Add(Vhosts.Copilot);
        var codexFromHost = run.Credential == CredentialMode.CodexGatewaySignIn;
        if (codexFromHost) vhosts.Add(Vhosts.Codex);
        var spec = new RunGatewaySpec(run.RunNumber, $"{run.Team}/{run.Member}", vhosts,
            run.AllowHosts.ToList(), run.TokenLifetime)
        {
            HostSignIn = AgentPresets.IsHostSignIn(run.Credential),
            RunKey = run.RunKey,
            ClaudeFromHost = run.Credential == CredentialMode.ClaudeGatewaySignIn,
            CopilotFromHost = copilotFromHost,
            CodexFromHost = codexFromHost,
        };
        IBusEndpoint bus = run.Tools ?? (run.Bus is null ? new NoBusEndpoint() : new McpBusServer(run.Bus));
        var since = host.Log.Now;
        var gateway = host.Start(spec, new TouchingBusEndpoint(bus, activity));
        return Task.FromResult<IRunGateway>(new GatewayAdapter(gateway, host.Log, spec, since));
    }

    private sealed class GatewayAdapter(RunGateway gateway, GatewayLog log, RunGatewaySpec spec, DateTimeOffset since) : IRunGateway
    {
        public int Port => gateway.Port;

        public string RunToken => gateway.Token.Value;

        /// <summary>
        /// The gateway was bound with the run's vhosts and allowlist before the compiler ran (it needs the port and
        /// token); the compiled plan must say the same, or the request and the gateway would disagree.
        /// </summary>
        public void Configure(GatewayPlan plan)
        {
            if (plan.Port != gateway.Port) throw new InvalidOperationException($"the compiled request names port {plan.Port}, the gateway is on {gateway.Port}");
            var extraHosts = plan.AllowHosts.Except(spec.AllowHosts, StringComparer.OrdinalIgnoreCase).ToList();
            if (extraHosts.Count > 0) throw new InvalidOperationException("the compiled request allows hosts the gateway does not: " + string.Join(", ", extraHosts));
            var extraVhosts = plan.Vhosts.Select(v => v.Host).Where(h => !h.Equals(Vhosts.Bus, StringComparison.OrdinalIgnoreCase) && !spec.ModelVhosts.Contains(h)).ToList();
            if (extraVhosts.Count > 0) throw new InvalidOperationException("the compiled request uses vhosts the gateway does not serve: " + string.Join(", ", extraVhosts));
        }

        public GatewayVerdict Verdict() => GatewayVerdicts.From(log.Read(spec.RunId).Where(e => e.At >= since).ToList());

        public ValueTask DisposeAsync() => gateway.DisposeAsync(); // revokes the run token
    }
}

/// <summary>Reads a run's verdict from its gateway log (never from the CLI's exit code).</summary>
public static class GatewayVerdicts
{
    public static GatewayVerdict From(IReadOnlyList<GatewayEvent> events)
    {
        var denied = events.Where(e => e.Kind == GatewayEventKinds.ConnectDenied).ToList();
        var hosts = denied.Select(e => e.Target ?? "?").Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        // A credential problem counts unless a later forward on the same vhost succeeded (the keeper refreshed and the
        // CLI retried). "Grok login expired: sign in again" is the gateway's wording when the keeper cannot refresh.
        GatewayEvent? problem = null;
        foreach (var e in events)
        {
            if (e.Kind == GatewayEventKinds.CredentialProblem) problem = e;
            else if (problem is not null && e.Kind == GatewayEventKinds.Forward && e.Status is >= 200 and < 300
                && string.Equals(e.Vhost, problem.Vhost, StringComparison.OrdinalIgnoreCase)) problem = null;
        }
        GatewayEvent? limited = null;
        foreach (var e in events)
        {
            if (e.Kind == GatewayEventKinds.RateLimited) limited = e;
            else if (limited is not null && e.Kind == GatewayEventKinds.Forward && e.Status is >= 200 and < 300
                && string.Equals(e.Vhost, limited.Vhost, StringComparison.OrdinalIgnoreCase)) limited = null;
        }
        return new GatewayVerdict(problem is not null, denied.Count, hosts, problem?.Detail)
        {
            RateLimitedUntil = limited?.Detail is { } d && DateTimeOffset.TryParse(d, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out var until) ? until : null,
        };
    }
}

/// <summary>Every bus call the run's bridge makes resets the idle watchdog (progress).</summary>
public sealed class TouchingBusEndpoint(IBusEndpoint inner, RunActivity activity) : IBusEndpoint
{
    public async Task<BusReply> HandleAsync(byte[] body, CancellationToken ct)
    {
        activity.Touch();
        try
        {
            return await inner.HandleAsync(body, ct);
        }
        finally
        {
            activity.Touch();
        }
    }
}

/// <summary>A run with no bus run (an interactive session) has no bus tools: bus.yawble.internal answers 404.</summary>
public sealed class NoBusEndpoint : IBusEndpoint
{
    public Task<BusReply> HandleAsync(byte[] body, CancellationToken ct) =>
        Task.FromResult(new BusReply(404, """{"jsonrpc":"2.0","id":null,"error":{"code":-32601,"message":"this session has no bus tools"}}"""));
}

/// <summary>
/// Host sign-in: every CLI uses its own sign-in on this computer. The app's own credential
/// store (pasted setup-token / API keys) and its private Grok home with the keeper are not a source for a run;
/// whether the person is signed in is decided per launch from the detected CLI (presence of its sign-in files).
/// </summary>
public sealed class HostSignInModes : ICredentialModes
{
    public CredentialMode? ModeFor(AgentCli cli) => AgentPresets.HostSignInMode(cli);
}

/// <summary>
/// The person's credential choice per CLI, from the credential service and the host Grok home. Not wired
/// into the launcher, which uses <see cref="HostSignInModes"/>.
/// </summary>
public sealed class ServiceCredentialModes(CredentialService credentials, GrokHome grokHome) : ICredentialModes
{
    public CredentialMode? ModeFor(AgentCli cli)
    {
        if (cli == AgentCli.Claude)
        {
            var (mode, value) = credentials.ClaudeCredential();
            if (value is null) return null;
            return mode == ClaudeMode.Subscription ? CredentialMode.ClaudeSubscription : CredentialMode.ClaudeApiKey;
        }
        if (credentials.GrokMode == GrokMode.ApiKey)
            return credentials.Get(CredentialSlot.XaiApiKey) is null ? null : CredentialMode.GrokApiKey;
        // Login mode: a Grok login in the host Grok home (expired is fine: the gateway asks the keeper to refresh it).
        return File.Exists(grokHome.AuthFile) ? CredentialMode.GrokLogin : null;
    }
}

/// <summary>One run's transcript: the raw terminal bytes, as the supervisor's pumps read them.</summary>
public interface IRunTranscript : IAsyncDisposable
{
    void Write(ReadOnlyMemory<byte> data);
}

public interface IRunTranscripts
{
    IRunTranscript Open(long runNumber, string team, string member);
}

/// <summary>The transcript store behind the launcher's seam.</summary>
public sealed class StoreTranscripts(TranscriptStore store) : IRunTranscripts
{
    public IRunTranscript Open(long runNumber, string team, string member) => new Writer(store.Open(runNumber, team, member));

    private sealed class Writer(TranscriptWriter writer) : IRunTranscript
    {
        private volatile bool _failed;

        // Called on a pump thread (blocking reads), so waiting here only paces that pump.
        public void Write(ReadOnlyMemory<byte> data)
        {
            if (_failed) return;
            try
            {
                writer.WriteAsync(data).GetAwaiter().GetResult();
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                _failed = true;
            }
        }

        public ValueTask DisposeAsync() => writer.DisposeAsync();
    }
}
