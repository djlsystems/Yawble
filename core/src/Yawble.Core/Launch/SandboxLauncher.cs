using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Yawble.Core.Agents;
using Yawble.Core.Bus;
using Yawble.Core.Sandboxing;
using Yawble.Core.Policy;
using PolicyMember = Yawble.Core.Policy.MemberRef;

namespace Yawble.Core.Launch;

/// <summary>
/// The host facts the launcher builds every run's layout from. <paramref name="Clis"/> finds the agent CLIs where the
/// person installed them; it is asked again at every launch (never cached across runs), so a CLI the person updated
/// on the host is the one the next run gets. <paramref name="Folders"/> lists the host's folders for the CLI's config
/// denials (tests pass a fake). <paramref name="ToolFoldersNow"/>, when set, is read at every launch in place of
/// <paramref name="ToolFolders"/>.
/// </summary>
public sealed record LaunchHost(
    TargetOs Os,
    string Home,
    string AppDataRoot,
    string InstallDir,
    ICliDetector Clis,
    Func<string, string>? Canonicalize = null,
    IHostFolders? Folders = null,
    IReadOnlyList<string>? ToolFolders = null,
    Func<IReadOnlyList<string>>? ToolFoldersNow = null);

/// <summary>Host tools every run gets on PATH, read-only (<see cref="RunLayout.HostToolFolders"/>).</summary>
public static class HostTools
{
    /// <summary>The developer tools a member may run, by program name, in PATH order on the sandbox.</summary>
    public static IReadOnlyList<string> WindowsTools { get; } = ["git", "dotnet", "node", "python"];

    /// <summary>
    /// Windows: for each of <see cref="WindowsTools"/>, the folder of the first such program on the core's PATH: git
    /// (Git for Windows: ...\Git\cmd, which runs without MSYS2; its sh-based parts cannot start in a ProcessContainer,
    /// microsoft/mxc#1061), the .NET SDK, Node.js and Python, as the person installed them. Never a folder inside the app's own
    /// root, never the person's home itself, and never WindowsApps (its python.exe is a Store alias, not Python).
    /// Linux and macOS: none (the tools are in the system folders every sandbox reads).
    /// </summary>
    public static IReadOnlyList<string> Detect(string appDataRoot, string? pathVariable = null, string? home = null)
    {
        if (!OperatingSystem.IsWindows()) return [];
        var root = Path.GetFullPath(appDataRoot).TrimEnd('\\') + "\\";
        var profile = (home ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)).TrimEnd('\\');
        var dirs = new List<string>();
        foreach (var dir in (pathVariable ?? Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            string full;
            try { full = Path.GetFullPath(dir.Trim().Trim('"')).TrimEnd('\\'); }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { continue; }
            if ((full + "\\").StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;
            if (full.Length <= 3 || string.Equals(full, profile, StringComparison.OrdinalIgnoreCase)) continue;
            if (full.Contains(@"\WindowsApps", StringComparison.OrdinalIgnoreCase)) continue;
            dirs.Add(full);
        }
        var found = new List<string>();
        foreach (var tool in WindowsTools)
        {
            var dir = dirs.FirstOrDefault(d => File.Exists(Path.Combine(d, tool + ".exe")));
            if (dir is not null && !found.Contains(dir, StringComparer.OrdinalIgnoreCase)) found.Add(dir);
        }
        return found;
    }
}

/// <summary>The command line shown for an agent: one per run kind (see <see cref="SandboxLauncher.DescribeAsync"/>).</summary>
/// <param name="HeadlessGroups">The headless line in parts (launcher tokens and built-in groups), for the agent editor.</param>
/// <param name="InteractiveGroups">The interactive line in parts.</param>
public sealed record AgentCommandLines(CommandLineDescription Headless, CommandLineDescription Interactive,
    CommandLineGroups HeadlessGroups, CommandLineGroups InteractiveGroups);

/// <summary>A member as the launcher needs it: its policy id and its CLI preset.</summary>
/// <param name="TeamEnv">The team's environment variables (0016), given to the run under the app's own.</param>
/// <param name="Model">The member's own model (member_settings, 0006); empty or null follows the agent's default.</param>
/// <param name="Agent">The agent profile's name when it is a custom agent (recorded on the run); null = the built-in, named by its CLI.</param>
/// <param name="Custom">The custom agent's argument changes; null for a built-in.</param>
/// <param name="AgentModel">The custom agent's own default model; null for a built-in (its default is in agent_defaults).</param>
public sealed record LaunchMember(long Id, string Team, string Name, AgentCli Cli, IReadOnlyDictionary<string, string>? TeamEnv = null,
    string? Model = null, string? Agent = null, ArgvCustomization? Custom = null, string? AgentModel = null);

public interface IMemberDirectory
{
    LaunchMember? Find(string team, string member);

    /// <summary>Every member of every team: the other members' folders are denied in each request.</summary>
    IReadOnlyList<PolicyMember> All();
}

/// <summary>The bus's members table (0002): cli is 'claude' or 'grok'.</summary>
public sealed class SqliteMemberDirectory(string connectionString, Agents.Profiles.IAgentResolver? agents = null) : IMemberDirectory
{
    private readonly Agents.Profiles.IAgentResolver _agents = agents ?? Agents.Profiles.AgentProfileStore.ForReading(connectionString);

    public LaunchMember? Find(string team, string member) =>
        Query("WHERE t.name = $t COLLATE NOCASE AND m.name = $m COLLATE NOCASE", ("$t", team), ("$m", member)).SingleOrDefault();

    public IReadOnlyList<PolicyMember> All() => Query("").Select(m => new PolicyMember(m.Team, m.Name)).ToList();

    private List<LaunchMember> Query(string where, params (string, object)[] args)
    {
        using var c = new SqliteConnection(connectionString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT m.id, t.name, m.name, m.cli, t.env, s.model FROM members m JOIN teams t ON t.id = m.team_id LEFT JOIN member_settings s ON s.member_id = m.id {where} ORDER BY m.id;";
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v);
        using var r = cmd.ExecuteReader();
        var list = new List<LaunchMember>();
        while (r.Read())
        {
            // members.cli is a built-in's id or a custom agent's name: the custom one brings its argv changes and default model.
            var agent = Agents.Profiles.AgentResolvers.Resolve(_agents, r.GetString(3));
            list.Add(new LaunchMember(r.GetInt64(0), r.GetString(1), r.GetString(2), agent.Cli, Teams.TeamEnv.Parse(r.IsDBNull(4) ? null : r.GetString(4)),
                r.IsDBNull(5) ? null : r.GetString(5), agent.RecordedAgent, agent.Argv, agent.Custom ? agent.DefaultModel : null));
        }
        return list;
    }
}

/// <summary>What one launch asks for.</summary>
/// <remarks>
/// <see cref="Subject"/>: a stand-in that is not in the members table (the Agents dialog's Test, Agents/AgentTestSessions):
/// it gets the global policy alone, like a synthetic probe, but runs the CLI itself (interactive). For an interactive
/// launch <see cref="Prompt"/> is the CLI's optional initial prompt.
/// <see cref="Tools"/>: the MCP tools the run gets at bus.yawble.internal instead of a team bus run's (the Concierge's).
/// </remarks>
public sealed record LaunchRequest(string Team, string Member, RunKind Kind, string? Prompt, RunContext? Bus, PtySize? Size = null,
    ILaunchProbe? Probe = null, LaunchMember? Subject = null, Gateway.IBusEndpoint? Tools = null);

/// <summary>How one launch ended: the card's class and reason, and the details written to the bus.</summary>
public sealed record LaunchOutcome(
    string RunKey,
    RunEnd End,
    string? Reason,
    SupervisedExit? Exit,
    IReadOnlyList<GuardrailEvent> Guardrails)
{
    /// <summary>The model API's usage limit stopped the run: when it said to come back (null otherwise).</summary>
    public DateTimeOffset? RateLimitedUntil { get; init; }

    /// <summary>A probe launch's own output (<see cref="LaunchRequest.Probe"/>); null for an agent run.</summary>
    public LaunchProbeOutput? ProbeOutput { get; init; }

    public IReadOnlyDictionary<string, object?> Details() => new Dictionary<string, object?>
    {
        ["runKey"] = RunKey,
        ["end"] = RunEnds.Code(End),
        ["exitCode"] = Exit?.ExitCode,
        ["sandboxTimedOut"] = Exit?.SandboxTimedOut,
        ["killLatencyMs"] = Exit?.KillLatency is { } k ? (long)k.TotalMilliseconds : null,
        ["treeSurvived"] = Exit?.TreeSurvived,
        ["durationMs"] = Exit is null ? null : (long)Exit.Duration.TotalMilliseconds,
        ["guardrails"] = Guardrails.Select(g => new Dictionary<string, string> { ["kind"] = g.Kind, ["path"] = g.Path, ["action"] = g.Action }).ToList(),
        ["retryAfter"] = RateLimitedUntil?.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
    };
}

/// <summary>
/// The sandboxed launcher: from a member and a batch to one sandbox and
/// back. Per launch: credential mode → layout → effective policy → gateway (port) → the compiled request
/// → folders and seeds → spawn (headless, or on a sandbox PTY) → supervisor (timeout, idle watchdog, Stop) →
/// post-run checks → gateway verdict → run record. The request is never edited after compiling, except by a probe
/// launch (<see cref="ILaunchProbe"/>, the guardrail check), which swaps only the command line and process.timeout.
/// </summary>
public sealed class SandboxLauncher(
    LaunchHost host,
    IMemberDirectory members,
    PolicyStore policies,
    ICredentialModes credentials,
    IRunGatewayFactory gateways,
    ISandboxBackend backend,
    RunRecordStore records,
    RunRegistry registry,
    TimeProvider? clock = null,
    SupervisorOptions? supervisorOptions = null,
    IRunTranscripts? transcripts = null,
    Agents.AgentDefaultsStore? agentDefaults = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <summary>The model a member's run uses: member override > the agent's default > the CLI's own default.</summary>
    public Agents.ModelResolution ResolveModel(LaunchMember member) =>
        Agents.AgentModels.Resolve(member.Model, member.Agent is not null ? member.AgentModel : agentDefaults?.Get(member.Cli));

    /// <summary>The credential mode a run of this CLI and kind uses (the one place it is decided; Describe uses it too).</summary>
    private CredentialMode ModeFor(AgentCli cli, RunKind kind, CredentialMode? configured)
    {
        var mode = configured ?? cli switch
        {
            AgentCli.Claude => CredentialMode.ClaudeSubscription,
            AgentCli.Grok => CredentialMode.GrokLogin,
            var other => AgentPresets.HostSignInMode(other),
        };
        // Codex's TUI requires an HTTPS ChatGPT backend, which the gateway's plain-HTTP
        // codex.yawble.internal is not: an interactive session keeps the host sign-in; headless runs stay gateway-held.
        return mode == CredentialMode.CodexGatewaySignIn && kind == RunKind.Interactive ? CredentialMode.CodexHostSignIn : mode;
    }

    /// <summary>
    /// The command line the person is shown for an agent (both run kinds), built by the same function a launch uses
    /// (<see cref="AgentPresets.Arguments"/>): placeholders for the per-run values, environment variable NAMES only.
    /// </summary>
    public async Task<AgentCommandLines> DescribeAsync(AgentCli cli, string? model, ArgvCustomization? custom, CancellationToken ct) =>
        Describe(cli, model, custom, await DetectCliAsync(cli, forProbe: true, ct));

    /// <summary>
    /// <see cref="DescribeAsync(AgentCli, string?, ArgvCustomization?, CancellationToken)"/> for a CLI the caller has just
    /// detected (<paramref name="detected"/>, from the same <see cref="LaunchHost.Clis"/>), so it is not detected again.
    /// </summary>
    public AgentCommandLines Describe(AgentCli cli, string? model, ArgvCustomization? custom, DetectedCli? detected) =>
        Describe(cli, model, custom, InstallOf(cli, detected, forProbe: true));

    private AgentCommandLines Describe(AgentCli cli, string? model, ArgvCustomization? custom, CliInstall install)
    {
        string Root(string p) => host.Canonicalize is { } c ? c(p) : p;
        RunLayout LayoutFor(CredentialMode mode) => RunLayout.Standard(host.Os, Root(host.Home), Root(host.AppDataRoot), Root(host.InstallDir),
            new Policy.MemberRef("team", "member"), cli, mode is CredentialMode.ClaudeHostSignIn or CredentialMode.GrokHostSignIn or CredentialMode.CodexHostSignIn
                or CredentialMode.CopilotHostSignIn or CredentialMode.AntigravityHostSignIn ? install : install with { SignIn = null }, []);
        var configured = credentials.ModeFor(cli);
        var headlessMode = ModeFor(cli, RunKind.Headless, configured);
        var interactiveMode = ModeFor(cli, RunKind.Interactive, configured);
        var m = string.IsNullOrEmpty(model) ? null : model;
        var headless = LayoutFor(headlessMode);
        var interactive = LayoutFor(interactiveMode);
        return new AgentCommandLines(
            AgentPresets.Describe(headless, headlessMode, RunKind.Headless, m, custom),
            AgentPresets.Describe(interactive, interactiveMode, RunKind.Interactive, m, custom),
            AgentPresets.DescribeGroups(headless, headlessMode, RunKind.Headless, m, custom),
            AgentPresets.DescribeGroups(interactive, interactiveMode, RunKind.Interactive, m, custom));
    }

    public RunRegistry Registry => registry;

    /// <summary>The host facts every run's layout is built from (the guardrail check reports on the same layout).</summary>
    public LaunchHost Host => host;

    /// <summary>The sandbox backend's short name ("yawble-ffi", "wxc-exec").</summary>
    public string BackendName => backend.Name;

    /// <summary>
    /// Detects the member's CLI now and turns it into the run's grants. A probe (the guardrail check) never runs the
    /// CLI, so a CLI that is not installed gives it a placeholder with no CLI grants instead of failing.
    /// </summary>
    public async Task<CliInstall> DetectCliAsync(AgentCli cli, bool forProbe, CancellationToken ct)
    {
        var detected = await host.Clis.DetectOneAsync(cli.Id(), ct); // that CLI only: no --version probe of the others
        return InstallOf(cli, detected, forProbe);
    }

    /// <summary>A detection turned into the run's grants (see <see cref="DetectCliAsync"/>).</summary>
    private CliInstall InstallOf(AgentCli cli, DetectedCli? detected, bool forProbe)
    {
        var id = cli.Id();
        string Root(string p) => host.Canonicalize is { } c ? c(p) : p;
        if (detected is null || !detected.Found || detected.ExecutablePath is null)
        {
            if (forProbe)
                return CliInstall.Missing(host.Os, AgentPresets.Join(host.Os, Root(host.InstallDir), "bin"), id);
            var help = detected?.InstallHelp is { Length: > 0 } h ? " " + h : "";
            throw new ArgumentException($"{id} is not installed on this computer, so this member cannot run.{help}");
        }
        return CliInstall.FromDetected(host.Os, Root(host.Home), detected, host.Folders ?? DiskFolders.Instance, host.Canonicalize);
    }

    public async Task<LaunchOutcome> RunAsync(LaunchRequest request, CancellationToken ct)
    {
        var runKey = "r-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
        var probe = request.Probe;
        var synthetic = probe?.Synthetic ?? request.Subject;
        var member = synthetic ?? members.Find(request.Team, request.Member);
        if (member is null) return new LaunchOutcome(runKey, RunEnd.StartFailed, $"no member {request.Team}/{request.Member}", null, []);
        var cliName = member.Cli.Id();
        var kind = probe is null ? request.Kind : RunKind.Headless;
        // A probe is recorded as the headless run it is (run_records.kind is headless or interactive, 0005); its report
        // names the run key.
        var kindName = kind == RunKind.Headless ? "headless" : "interactive";
        records.Started(runKey, request.Bus?.RunId, member.Team, member.Name, cliName, kindName, backend.Name);

        LaunchOutcome Fail(string reason) => Finish(runKey, RunEnd.StartFailed, reason, null, []);

        // A probe never uses the model credential (it makes no model call), so it runs under the default mode when the
        // person has not set one up yet, and says so; an agent run cannot start without one.
        var configuredMode = credentials.ModeFor(member.Cli);
        if (configuredMode is null && probe is null && request.Subject is null)
            return Fail($"no model credential is set up for {cliName}: add one in Settings → Model credentials");
        var mode = ModeFor(member.Cli, kind, configuredMode);

        // The member's CLI as installed on this computer at launch (no pinned version, no toolchain).
        CliInstall cliInstall;
        try
        {
            cliInstall = await DetectCliAsync(member.Cli, probe is not null, ct);
        }
        catch (ArgumentException ex)
        {
            return Fail(ex.Message);
        }

        // Host sign-in: the CLI's own sign-in on this computer, found by the same detection (presence only). A run that would
        // need it and cannot have it stops here with the reason; a probe makes no model call and runs anyway.
        var credentialReady = configuredMode is not null;
        if (AgentPresets.NeedsHostSignIn(mode))
        {
            var signIn = cliInstall.SignIn;
            credentialReady = signIn is { Status: SignInStatus.SignedIn };
            if (probe is null && !credentialReady)
            {
                return Fail(signIn switch
                {
                    { Status: SignInStatus.Pending } => $"{cliName} cannot run here yet: {signIn.Detail}",
                    { } s => $"{cliName} is not signed in on this computer. {s.Detail}",
                    null => $"{cliName} has no sign-in the app knows how to use on this computer",
                });
            }
        }
        // Only the host sign-in modes grant the host's sign-in files to the sandbox; the gateway-held mode checked it is there
        // (above) and grants nothing: the core reads it per request.
        // Codex, gateway-held: the person's own sign-in file, read only to build the run's placeholder (no secret in it).
        var codexHostAuth = mode == CredentialMode.CodexGatewaySignIn
            ? cliInstall.SignIn?.Files.FirstOrDefault(f => f.Present)?.Path
            : null;
        if (!AgentPresets.IsHostSignIn(mode)) cliInstall = cliInstall with { SignIn = null };

        // Antigravity: its sign-in is in the host's credential store; read now, written into the member's home below.
        string? antigravitySignIn = null;
        if (mode == CredentialMode.AntigravityHostSignIn && credentialReady && probe is null)
        {
            antigravitySignIn = SignInCatalog.Store.Read(SignInCatalog.AntigravityTarget);
            if (antigravitySignIn is not { Length: > 0 } || !antigravitySignIn.TrimStart().StartsWith('{'))
                return Fail($"{cliName}: its sign-in in the credential store could not be read. Run 'agy' in your own terminal and sign in.");
        }

        // Copilot: its token is in the host's credential store, which the sandbox cannot read; read it now, for this run
        // (host sign-in), or only to check GitHub still accepts it (the gateway holds it and reads it per request).
        if (mode is CredentialMode.CopilotHostSignIn or CredentialMode.CopilotGatewaySignIn && credentialReady && probe is null)
        {
            // The gateway-held mode does not carry the sign-in details here (nothing of it is granted): read it afresh.
            var stored = mode == CredentialMode.CopilotGatewaySignIn
                ? SignInCatalog.CopilotToken()
                : SignInCatalog.Store.Read(cliInstall.SignIn!.Files.First(f => f.Access == SignInAccess.HostStore).Path);
            if (stored is not { Length: > 0 } token)
                return Fail($"{cliName}: its sign-in in the credential store could not be read. Run 'copilot' in your own terminal and sign in with /login.");
            if (await Credentials.CopilotSignInCheck.IsRejectedAsync(token, ct))
                return Fail(Credentials.CopilotSignInCheck.Expired);
            if (mode == CredentialMode.CopilotHostSignIn)
                cliInstall = cliInstall with { SecretEnvironment = new Dictionary<string, string> { [AgentPresets.CopilotTokenVariable] = token } };
        }

        RunLayout layout;
        EffectivePolicy policy;
        try
        {
            // The roots are canonicalised once, so every path derived from them (env HOME/TMPDIR/PATH, the
            // CLI path, the grants) is the same canonical path the compiler and the sandbox see. Otherwise a symlinked root
            // (macOS /var -> /private/var) gives a HOME that differs from the granted workspace.
            string Root(string p) => host.Canonicalize is { } c ? c(p) : p;
            layout = RunLayout.Standard(host.Os, Root(host.Home), Root(host.AppDataRoot), Root(host.InstallDir),
                new PolicyMember(member.Team, member.Name), member.Cli, cliInstall,
                probe is null ? members.All() : [.. members.All(), .. probe.ExtraMembers]) with { HostToolFolders = host.ToolFoldersNow?.Invoke() ?? host.ToolFolders ?? [] };
            // The team's shared view, once the host has made it (a member's first run makes it).
            if (synthetic is null && (host.Folders ?? DiskFolders.Instance).Exists(layout.SharedRepoCandidate))
                layout = layout with { SharedRepo = layout.SharedRepoCandidate };
            // The team's other repositories this run was prepared with (TeamRepos.PrepareAsync), those whose folders exist.
            if (synthetic is null && request.Bus?.Checkout is { Extras.Count: > 0 } checkout)
            {
                var folders = host.Folders ?? DiskFolders.Instance;
                var named = layout with { ExtraRepoNames = [.. checkout.Extras.Where(e => e.Writable).Select(e => e.Name)],
                    ReferenceRepoNames = [.. checkout.Extras.Where(e => !e.Writable).Select(e => e.Name)] };
                layout = layout with
                {
                    ExtraRepoNames = [.. named.ExtraRepoNames.Where((n, i) => folders.Exists(named.ExtraClonePaths[i]) && folders.Exists(named.ExtraSharedPaths[i]))],
                    ReferenceRepoNames = [.. named.ReferenceRepoNames.Where((n, i) => folders.Exists(named.ReferencePaths[i]))],
                };
            }
            // The team's documents, made on a member's first run (owner-only, like the member's own folders).
            if (synthetic is null && layout.TeamRoot.Length > 0)
                layout = layout with { TeamDocuments = layout.TeamDocumentsCandidate };
            var merged = synthetic is not null
                ? PolicyMerger.Merge(policies.GetGlobal(), null, null, layout, host.Canonicalize)
                : policies.Effective(member.Id, layout);
            if (merged.Violations.Count > 0) return Fail("the effective policy is refused: " + string.Join("; ", merged.Violations));
            policy = merged.Policy;
        }
        catch (Exception ex) when (ex is ArgumentException or PolicyException)
        {
            return Fail("cannot lay out this run: " + ex.Message);
        }

        var resolvedModel = ResolveModel(member);
        var activity = new RunActivity(_clock);
        // The heavy-work lease holds this run's idle watchdog while the run waits in the lease's queue.
        if (request.Bus is { } busRun && probe is null) busRun.IdleClock = activity;
        var runNumber = request.Bus?.RunId ?? LocalRunNumbers.Next();
        // The token outlives the wall-clock limit by a grace, and is revoked anyway when the gateway is disposed.
        var tokenLifetime = TimeSpan.FromSeconds(policy.Compute.WallTimeoutSec) + TimeSpan.FromMinutes(5);
        IRunGateway gateway;
        try
        {
            gateway = await gateways.StartAsync(new RunIdentity(runKey, runNumber, request.Bus?.RunId, member.Team, member.Name, member.Cli, mode,
                [.. policy.AllowHosts, .. AgentPresets.PassthroughHosts(mode)], tokenLifetime, request.Bus, request.Tools), activity, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Fail("the run's gateway did not start: " + ex.Message);
        }

        await using (gateway)
        {
            var token = gateway.RunToken;
            CompiledRun compiled;
            string requestJson;
            LaunchProbeContext? probeContext = null;
            SignInCopy? signInCopy = null;
            try
            {
                compiled = SandboxRequestCompiler.Compile(policy, new RunSpec
                {
                    Layout = layout,
                    Credential = mode,
                    Kind = kind,
                    Prompt = request.Prompt,
                    Model = string.IsNullOrEmpty(resolvedModel.Model) ? null : resolvedModel.Model,
                    Custom = member.Custom,
                    RunId = runKey,
                    SessionId = Guid.NewGuid().ToString(),
                    RunToken = token,
                    GatewayPort = gateway.Port,
                    TeamEnv = member.TeamEnv,
                }, host.Canonicalize);
                // What this run really ran with, for the run's record (and cost over time): agent, resolved model, CLI version, argv.
                records.RanWith(runKey, member.Agent ?? cliName, resolvedModel.Model, resolvedModel.Source, cliInstall.Version,
                    Agents.RunArgv.ForRecord(compiled.Argv, request.Prompt, [token, .. layout.CliInstall.SecretEnvironment.Values, AgentPresets.CopilotPlaceholder(token)]));
                if (request.Bus?.Checkout?.CloneBaseCommit is { } cloneBase) records.BaseCommit(runKey, cloneBase);
                gateway.Configure(compiled.Gateway);
                RunFolders.Prepare(layout, compiled.Seeds);
                // Codex, gateway-held: the placeholder replaces whatever is there (an earlier run's copy of the real sign-in).
                if (mode == CredentialMode.CodexGatewaySignIn)
                {
                    var placeholder = codexHostAuth is null ? null : new Credentials.CodexHostSession(codexHostAuth, () => null).PlaceholderAuthJson(token)
                        ?? throw new InvalidOperationException("Codex's sign-in on this computer could not be read: run 'codex login' in your own terminal");
                    var file = AgentPresets.CodexAuthFile(layout);
                    Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                    File.WriteAllText(file, placeholder ?? "{}");
                }
                // Codex: a copy of the person's sign-in in the member's own Codex home (copied back if refreshed).
                if (probe is null && layout.CliInstall.SignInCopies.Count > 0)
                    signInCopy = await SignInCopy.PrepareAsync(layout, _clock, null, ct);
                // Antigravity: the person's sign-in where agy reads it when the OS store is denied (the member's own home).
                if (antigravitySignIn is not null)
                {
                    var file = AgentPresets.AntigravityTokenFile(layout);
                    Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                    File.WriteAllText(file, antigravitySignIn);
                }
                var submitted = compiled.Request;
                if (probe is not null)
                {
                    probeContext = new LaunchProbeContext(runKey, runNumber, layout, policy, mode, credentialReady, gateway.Port,
                        (JsonObject)compiled.Request.DeepClone());
                    submitted = probe.Rewrite(probeContext);
                }
                requestJson = submitted.ToJsonString(SandboxJson.Compact);
            }
            catch (LaunchProbeRefusedException ex)
            {
                if (signInCopy is not null) await signInCopy.DisposeAsync();
                return Fail(ex.Message);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or PolicyException or IOException or UnauthorizedAccessException)
            {
                if (signInCopy is not null) await signInCopy.DisposeAsync();
                return Fail("the run could not be prepared: " + ex.Message);
            }
            // After the run (or a failed start): a refreshed sign-in goes back to the host, and the lease is released.
            await using var signInCopyBack = signInCopy;

            // A credentials file left by an earlier, crashed run is removed before this one starts.
            var guardrails = new List<GuardrailEvent>(PostRunChecks.PreRun(layout, mode));

            ISandboxProcess process;
            try
            {
                activity.Touch();
                process = kind == RunKind.Headless
                    ? backend.Spawn(requestJson)
                    : backend.SpawnWithPty(requestJson, request.Size ?? PtySize.Default);
            }
            catch (SandboxStartException ex)
            {
                return Finish(runKey, RunEnd.StartFailed, ex.Message, null, guardrails);
            }

            using (process)
            // A probe's output is its result, not the member's terminal: no transcript for it.
            await using (var transcript = probe is null ? OpenTranscript(runNumber, member) : null)
            {
                records.Spawned(runKey, process.Id);
                var output = new RunOutputBuffer();
                if (transcript is not null) output.Data += transcript.Write;
                // A probe keeps stderr apart; an agent run's two streams are one terminal.
                var errors = probe is null ? null : new RunOutputBuffer();
                // An interactive session waits on the person, not on progress calls: no idle watchdog for it. A probe
                // makes no progress calls and is bounded by its own wall clock instead.
                var idle = kind == RunKind.Headless && probe is null ? compiled.Compute.IdleTimeoutSec : 0;
                var wall = probe is null
                    ? compiled.Compute.WallTimeoutSec
                    : Math.Min(compiled.Compute.WallTimeoutSec, (int)Math.Ceiling(probe.MaxWall.TotalSeconds));
                // A headless agent run ends once the CLI's final answer is out, even if a leftover process keeps the CLI
                // waiting (FinalAnswer). Not for a probe (its script is not a CLI) nor an interactive session.
                var answer = kind == RunKind.Headless && probe is null && FinalAnswer.Detects(member.Cli) ? new FinalAnswer(member.Cli, _clock) : null;
                if (answer is not null) output.Data += answer.Write;
                var supervisor = new RunSupervisor(process, wall, idle, activity, output, _clock, supervisorOptions, errors,
                    answer is null ? null : () => answer.At);
                var live = new LiveRun(runKey, request.Bus?.RunId, member.Team, member.Name, kind, process, supervisor, output);
                registry.Add(live);
                SupervisedExit exit;
                try
                {
                    exit = await supervisor.RunAsync(ct);
                }
                finally
                {
                    registry.Remove(live);
                }

                guardrails.AddRange(PostRunChecks.Run(layout, mode, token));
                var verdict = gateway.Verdict();
                var end = exit.End;
                // grok -p exits 0 on a revoked credential: the gateway's log decides, not the exit code.
                if (end is RunEnd.Completed or RunEnd.Failed && verdict.CredentialRejected) end = RunEnd.Credential;
                var outcome = Finish(runKey, end, ReasonFor(end, exit, compiled.Compute with { WallTimeoutSec = wall }, verdict.Reason), exit, guardrails)
                    with { RateLimitedUntil = end == RunEnd.Failed ? verdict.RateLimitedUntil : null };
                return probeContext is null ? outcome : outcome with
                {
                    ProbeOutput = new LaunchProbeOutput(runNumber, mode, credentialReady,
                        System.Text.Encoding.UTF8.GetString(output.Snapshot()), System.Text.Encoding.UTF8.GetString(errors!.Snapshot()),
                        JsonNode.Parse(requestJson)!.AsObject()),
                };
            }
        }
    }

    private IRunTranscript? OpenTranscript(long runNumber, LaunchMember member)
    {
        try
        {
            return transcripts?.Open(runNumber, member.Team, member.Name);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null; // a transcript is a record, not a reason to refuse the run
        }
    }

    private LaunchOutcome Finish(string runKey, RunEnd end, string? reason, SupervisedExit? exit, IReadOnlyList<GuardrailEvent> guardrails)
    {
        records.Ended(runKey, end, exit, reason, guardrails);
        return new LaunchOutcome(runKey, end, reason, exit, guardrails);
    }

    private static string? ReasonFor(RunEnd end, SupervisedExit exit, ComputeLimits limits, string? credentialReason = null)
    {
        var reason = end switch
        {
            RunEnd.Completed => null,
            RunEnd.Failed => $"the agent CLI exited with code {exit.ExitCode}",
            RunEnd.Timeout => $"timeout: ended at the {limits.WallTimeoutSec} s wall-clock limit",
            RunEnd.Idle => $"idle: no progress for {limits.IdleTimeoutSec} s",
            RunEnd.Stopped => "stopped by the person",
            RunEnd.Interrupted => "interrupted: the core stopped while this run was running",
            RunEnd.Credential => credentialReason ?? GatewayVerdict.CredentialReason,
            _ => end.ToString(),
        };
        if (exit.EndedAfterAnswer)
            reason = "the agent gave its final answer but did not exit (a leftover process kept it waiting), so its sandbox was ended";
        if (exit.TreeSurvived)
            reason = (reason is null ? "" : reason + "; ") + "the sandbox was not gone within 5 s of the kill, so its handle was freed";
        return reason;
    }

}

/// <summary>Run numbers for runs with no bus run: negative, and unique across core restarts (time-seeded).</summary>
public static class LocalRunNumbers
{
    private static long s_last = -DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000;

    public static long Next() => Interlocked.Decrement(ref s_last);
}

/// <summary>The member's three folders and the seeded config. Owner-only on Unix.</summary>
public static class RunFolders
{
    public static void Prepare(RunLayout layout, IReadOnlyList<SeedFile> seeds)
    {
        foreach (var dir in layout.ImplicitReadwrite) CreateOwnerOnly(dir);
        // Windows: in a ProcessContainer, Windows itself points TEMP, TMP and LOCALAPPDATA at
        // <LOCALAPPDATA>\Packages\<container>\AC\Temp and creates it only if <LOCALAPPDATA> exists. Without it the
        // temp folder is missing, and a CLI that needs one fails (Grok's interactive session cannot
        // make its session folder, so every tool fails "workspace session not found ... call bind_local_session()").
        if (layout.Os == TargetOs.Windows)
            foreach (var sub in new[] { "Local", "Roaming" }) CreateOwnerOnly(Path.Combine(layout.Workspace, "AppData", sub));
        foreach (var seed in seeds)
        {
            CreateOwnerOnly(Path.GetDirectoryName(seed.Path)!);
            File.WriteAllText(seed.Path, MergeJsonSeed(seed));
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(seed.Path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    /// <summary>
    /// A JSON seed is merged into what the CLI already wrote there (Claude keeps its own state in .claude.json),
    /// with the seed's keys winning; anything else is written as is.
    /// </summary>
    public static string MergeJsonSeed(SeedFile seed)
    {
        if (!seed.Path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || !File.Exists(seed.Path)) return seed.Content;
        try
        {
            if (JsonNode.Parse(File.ReadAllText(seed.Path)) is not JsonObject existing || JsonNode.Parse(seed.Content) is not JsonObject wanted)
                return seed.Content;
            Merge(existing, wanted);
            return existing.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        }
        catch (System.Text.Json.JsonException)
        {
            return seed.Content;
        }
    }

    private static void Merge(JsonObject target, JsonObject source)
    {
        foreach (var (key, value) in source.ToList())
        {
            if (value is JsonObject child && target[key] is JsonObject existing) Merge(existing, child);
            else target[key] = value?.DeepClone();
        }
    }

    private static void CreateOwnerOnly(string dir)
    {
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(dir);
        else Directory.CreateDirectory(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
}
