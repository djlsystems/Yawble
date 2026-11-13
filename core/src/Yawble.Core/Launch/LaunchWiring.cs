using Yawble.Core.Agents;
using Yawble.Core.Bus;
using Yawble.Core.Credentials;
using Yawble.Core.Gateway;
using Yawble.Core.Policy;
using Yawble.Core.Transcripts;

namespace Yawble.Core.Launch;

/// <summary>
/// The core's launcher, built on the credential, gateway and transcript pieces (Program.cs). The app folders follow
/// the standard layout (RunLayout.Standard): &lt;app root&gt;/data is the core's --data-dir, the credentials file fallback is in
/// &lt;app root&gt;/data/credentials, and the host Grok home is &lt;app root&gt;/grok-home. Every one of them is on each
/// run's deny list. The install folder holds bin/ (yawble-bridge, yawble-grok-auth, yawble-guardcheck) and roles/: it is
/// --install-dir, else YAWBLE_INSTALL_DIR, else the folder above the core's own folder when that has bin/yawble-bridge (the
/// packaged layout resources/core + resources/bin). The agent CLIs are not in it: each run uses the CLI the person
/// installed, found by <see cref="ICliDetector"/> at launch.
/// When the layout cannot be honoured, every run fails at once with the reason (never a run with a wrong deny list).
/// </summary>
public sealed class LaunchWiring : IAsyncDisposable
{
    public const string InstallDirVariable = "YAWBLE_INSTALL_DIR";

    private readonly GatewayHost? _gatewayHost;

    /// <summary>What the doctor's gateway line tests (null when there is no launcher): the host every run's gateway starts from.</summary>
    public Func<CancellationToken, Task<string?>>? GatewayProbe =>
        _gatewayHost is { } host ? ct => Gateway.GatewaySelfTest.CheckAsync(host, TimeSpan.FromSeconds(5), ct) : null;
    private readonly GrokKeeper? _keeper;

    /// <summary>Where every run's gateway reports a host refused for not being on the allowlist (the network asks); set once the ask store is up.</summary>
    public Gateway.IHostRefusals? HostRefusals
    {
        get => _gatewayHost?.Refusals;
        set
        {
            if (_gatewayHost is { } host) host.Refusals = value;
        }
    }

    private LaunchWiring(IAgentRunner runner, SandboxLauncher? launcher, GatewayHost? gatewayHost, GrokKeeper? keeper, string? problem)
    {
        Runner = runner;
        Launcher = launcher;
        _gatewayHost = gatewayHost;
        _keeper = keeper;
        Problem = problem;
    }

    /// <summary>The bus's runner: the sandbox launcher, or an <see cref="UnavailableRunner"/> naming <see cref="Problem"/>.</summary>
    public IAgentRunner Runner { get; }

    public SandboxLauncher? Launcher { get; }

    /// <summary>The credential service, host Grok home and keeper the launcher's gateway uses (null when off), so the
    /// Settings screens act on the same objects and the core never runs two keepers on one Grok home.</summary>
    public CredentialService? Credentials { get; private init; }

    public GrokHome? GrokHome { get; private init; }

    /// <summary>The default model per built-in agent (agent_defaults, 0033).</summary>
    public Agents.AgentDefaultsStore? AgentDefaults { get; private init; }

    /// <summary>The app-private Grok home's keeper for the Settings screens: none from here (the gateway's keeper
    /// renews the person's own Grok home and is not shared).</summary>
    public GrokKeeper? Keeper => null;

    public string? Problem { get; }

    public static LaunchWiring Create(string dataDir, string connectionString, ICliDetector clis, string? installDir,
        RunRegistry registry, Func<string, string?> env, Action<string>? log = null, bool startKeeper = true)
    {
        LaunchWiring Unavailable(string problem)
        {
            log?.Invoke("the sandbox launcher is off: " + problem);
            return new LaunchWiring(new UnavailableRunner("no agent launcher: " + problem), null, null, null, problem);
        }

        var data = Path.GetFullPath(dataDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!string.Equals(Path.GetFileName(data), "data", StringComparison.Ordinal))
            return Unavailable($"the data folder must be <app root>/data, so that each run's deny list covers it (got {data})");
        var appRoot = Path.GetDirectoryName(data)!;

        var install = ResolveInstallDir(installDir, env);
        if (string.IsNullOrEmpty(install))
            return Unavailable($"no install folder: pass --install-dir <install> or set {InstallDirVariable} (it holds bin/yawble-bridge)");

        var credentials = CredentialService.ForThisMachine(Path.Combine(data, "credentials"), connectionString, log);
        if (credentials.FallbackWarning is { } warning) log?.Invoke(warning);
        var grokHome = new GrokHome(Path.Combine(appRoot, GrokHome.FolderName)).EnsureCreated();
        // Grok's sign-in is held by the gateway: read from the person's OWN Grok home (GROK_HOME or ~/.grok)
        // at every request; the keeper has the person's own grok renew it before it expires. The sandbox gets only the
        // placeholder (yawble-grok-auth). This home and keeper stay inside the gateway: the Settings screens keep the
        // app-private home, so nothing there can sign the person out of their own grok.
        var personsGrok = GrokHome.OfThisComputer(env);
        GrokKeeper? keeper = startKeeper
            ? new GrokKeeper(personsGrok, new GrokKeeperOptions
            {
                GrokBinary = Agents.HostCli.Path(clis, "grok") ?? "grok-not-installed",
                ResolveBinary = () => Agents.HostCli.Path(clis, "grok"),
                Log = log,
            })
            : null;

        // Claude's sign-in is held by the gateway: read from the person's own host sign-in at every request,
        // renewed by the person's own claude on the host when it is about to expire.
        var claudeHost = new ClaudeHostSession(ClaudeHostSession.DefaultCredentialsFile(env), () => Agents.HostCli.Path(clis, "claude"), log);
        var gatewayHost = new GatewayHost(new GatewayCredentials(credentials, personsGrok, keeper, claudeHost: claudeHost,
            codexHost: new CodexHostSession(CodexHostSession.DefaultAuthFile(env), () => Agents.HostCli.Path(clis, "codex"), log)), new GatewayLog(connectionString),
            new RunTokenRegistry());
        ISandboxBackend backend = OperatingSystem.IsWindows()
            ? new WxcExecBackend(Path.Combine(AppContext.BaseDirectory, "wxc-exec.exe"), Path.Combine(data, "requests"))
            : new NativeSandboxBackend();
        var host = new LaunchHost(TargetOsExtensions.Host, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            appRoot, Path.GetFullPath(install), clis, HostPathCanonicalizer.Canonicalize,
            ToolFoldersNow: () => HostTools.Detect(appRoot)); // read at every run: a git installed while the app runs is on the next run's PATH
        // Paths are canonicalised (symlinks resolved) before they are compared or handed to the sandbox.
        var agentDefaults = new Agents.AgentDefaultsStore(connectionString);
        var launcher = new SandboxLauncher(host, new SqliteMemberDirectory(connectionString),
            new PolicyStore(connectionString, canonicalize: HostPathCanonicalizer.Canonicalize),
            // Each CLI's own sign-in on this host; the app's credential store is not a source for runs.
            new HostSignInModes(), new GatewayRunFactory(gatewayHost), backend,
            new RunRecordStore(connectionString), registry,
            transcripts: new StoreTranscripts(new TranscriptStore(data, connectionString)), agentDefaults: agentDefaults);
        return new LaunchWiring(new SandboxAgentRunner(launcher), launcher, gatewayHost, keeper, null)
        {
            AgentDefaults = agentDefaults,
            Credentials = credentials,
            GrokHome = grokHome,
        };
    }

    /// <summary>--install-dir, else YAWBLE_INSTALL_DIR, else the packaged layout (the core in &lt;install&gt;/core).</summary>
    public static string? ResolveInstallDir(string? installDir, Func<string, string?> env)
    {
        if (!string.IsNullOrEmpty(installDir)) return Path.GetFullPath(installDir);
        if (env(InstallDirVariable) is { Length: > 0 } fromEnv) return Path.GetFullPath(fromEnv);
        var above = Path.GetDirectoryName(Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar));
        var bridge = OperatingSystem.IsWindows() ? "yawble-bridge.exe" : "yawble-bridge";
        return above is not null && File.Exists(Path.Combine(above, "bin", bridge)) ? above : null;
    }

    public async ValueTask DisposeAsync()
    {
        if (_keeper is not null) await _keeper.DisposeAsync();
        _gatewayHost?.Dispose();
    }
}
