using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Yawble.Core.Credentials;
using Yawble.Core.Gateway;
using Yawble.Core.Launch;
using Yawble.Core.Sandboxing;
using Yawble.Core.Policy;
using PolicyMember = Yawble.Core.Policy.MemberRef;

namespace Yawble.Core.Guardrail;

/// <summary>A member as the bus stores it.</summary>
public sealed record GuardrailMember(long Id, string Team, string Name, AgentCli Cli);

/// <summary>
/// The in-app guardrail check: a battery of filesystem, network and process escape attempts run INSIDE a sandbox
/// under a member's effective policy. The probe rides on the member's real launch
/// (<see cref="SandboxLauncher"/> with an <see cref="ILaunchProbe"/>): the same layout, effective policy, per-run gateway
/// and run token, compiled request, folders and seeds, backend, supervisor and run record as the member's next run.
/// Only the command line (yawble-guardcheck, in the install folder's read-only bin/) and a tighter process.timeout
/// differ. The probe makes no model call, so the gateway never swaps a credential in (the report checks this in the
/// gateway log). Each attempt is then judged on the HOST: a write counts as blocked unless the file reached the host,
/// and a gateway refusal must also be in the gateway log.
/// </summary>
public sealed class GuardrailCheck(SandboxLauncher launcher, string connectionString, Action<string>? log = null, GuardrailRunSlot? slot = null)
{
    public const string ProbeName = "yawble-guardcheck";
    public const string SyntheticTeam = "_guardrail-check";
    public const string SyntheticMember = "probe";
    public const string Decoy = "_guardrail-decoy";

    /// <summary>The probe's own bound inside the sandbox; the sandbox's floor for process.timeout is 60 s.</summary>
    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(120);

    /// <summary>Hosts a sandbox must never CONNECT to: only the gateway's vhosts reach the vendors.</summary>
    public static readonly IReadOnlyList<string> VendorHosts =
        ["api.anthropic.com", "platform.claude.com", "claude.ai", "api.x.ai", "cli-chat-proxy.grok.com", "auth.x.ai", "accounts.x.ai"];

    public const string DeniedHost = "example.com";

    public const string TmpfsNote =
        "Sandbox release 1.0.1 masks a denied folder with a WRITABLE empty tmpfs, so a " +
        "create inside a denied folder can succeed in the sandbox. Such a file never reaches the host and is discarded " +
        "with the sandbox; the check looks for it on the host and counts it as BLOCKED (marked sandboxOnly).";

    private readonly SemaphoreSlim _one = new(1, 1);
    private readonly GatewayLog _gatewayLog = new(connectionString);

    /// <summary>
    /// Makes the decoy: a host loopback port the sandbox must not reach. Null (production) = a TCP listener on
    /// 127.0.0.1 (<see cref="LoopbackGatewayListener"/>); the scripted-sandbox tests use an in-memory one (no port bound).
    /// </summary>
    public Func<IGatewayListener>? DecoyListener { get; init; }

    private LaunchHost Host => launcher.Host;

    public string ReportDir => Path.Combine(Host.AppDataRoot, "data", "guardrail");

    /// <summary>Runs the check for a member (team and member given) or under the global policy alone (both null).</summary>
    public async Task<GuardrailReport> RunAsync(string? team, string? member, CancellationToken ct = default, GuardrailProgress? progress = null,
        AgentCli syntheticCli = AgentCli.Claude)
    {
        progress?.Queued();
        await _one.WaitAsync(ct);
        try
        {
            return await RunOneAsync(team, member, ct, progress, syntheticCli);
        }
        finally
        {
            _one.Release();
        }
    }

    /// <summary>The last saved report for the subject, or null.</summary>
    public GuardrailReport? Last(string? team, string? member)
    {
        var path = ReportPath(team ?? SyntheticTeam, member ?? SyntheticMember);
        return File.Exists(path) ? GuardrailReport.FromJson(File.ReadAllText(path)) : null;
    }

    private string ReportPath(string team, string member) => Path.Combine(ReportDir, $"{team}__{member}.json");

    private async Task<GuardrailReport> RunOneAsync(string? team, string? memberName, CancellationToken ct, GuardrailProgress? progress,
        AgentCli syntheticCli)
    {
        var started = DateTimeOffset.UtcNow;
        var clock = Stopwatch.StartNew();
        var nonce = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(12));
        var members = LoadMembers();
        GuardrailMember subject;
        bool synthetic;
        if (team is null && memberName is null)
        {
            subject = new GuardrailMember(0, SyntheticTeam, SyntheticMember, syntheticCli);
            synthetic = true;
        }
        else
        {
            subject = members.FirstOrDefault(m => string.Equals(m.Team, team, StringComparison.OrdinalIgnoreCase)
                                                  && string.Equals(m.Name, memberName, StringComparison.OrdinalIgnoreCase))
                      ?? throw new GuardrailException("unknown_member", $"no member {team}/{memberName}");
            synthetic = false;
        }

        using var decoyListener = DecoyListener?.Invoke() ?? new LoopbackGatewayListener();
        decoyListener.Start();
        var probe = new Probe(this, nonce, subject, synthetic, decoyListener.Port);
        try
        {
            log?.Invoke($"guardrail check {nonce} for {subject.Team}/{subject.Name}: launching {ProbeName} through the sandbox launcher ({Host.Os.Containment()})");
            var launch = new LaunchRequest(subject.Team, subject.Name, RunKind.Headless, "guardrail check", null, Probe: probe);
            LaunchOutcome? launched = null;
            if (slot is not null && !synthetic)
            {
                // A member's check is one of its runs: it waits for a global run slot in the bus's queue (like a
                // terminal session) and holds the member's deliveries while it runs. The synthetic subject is no
                // member, so it takes no slot.
                await slot(subject.Team, subject.Name, async token =>
                {
                    progress?.Running(subject.Team, subject.Name);
                    launched = await launcher.RunAsync(launch, token);
                }, ct, progress);
            }
            else
            {
                progress?.Running(subject.Team, subject.Name);
                launched = await launcher.RunAsync(launch, ct);
            }
            var outcome = launched ?? throw new GuardrailException("not_run", "the run slot ended before the check ran");
            var output = outcome.ProbeOutput;
            if (probe.Context is null && outcome.End != RunEnd.StartFailed)
                throw new InvalidOperationException("the launcher ran the probe without calling it");

            var ran = outcome.End != RunEnd.StartFailed && output is not null;
            var missingGrants = outcome.Reason?.StartsWith(NativeSandboxBackend.MissingGrantsReason, StringComparison.Ordinal) == true;
            var status = ran ? 0 : missingGrants ? -2 : -1;
            var events = output is null ? [] : _gatewayLog.Read(output.RunNumber);
            var request = output?.Request;
            List<string> Paths(string key) => request?["filesystem"]?[key]?.AsArray().Select(n => n!.GetValue<string>()).ToList() ?? [];
            var denied = Paths("deniedPaths");
            // The sandbox binds every granted path and creates the folders on the way to it as mount points; a listing of a
            // parent shows those names. The Linux tarball installs to ~/.local/opt/yawble, so ~/.local is one.
            var granted = Paths("readonlyPaths").Concat(Paths("readwritePaths")).ToList();
            var results = ran && probe.Plan is not null ? Judge(probe.Plan, ParseLines(output!.Stdout), denied, granted, events) : [];
            if (ran) results.Add(NoModelCall(events));
            var exitCode = outcome.Exit?.ExitCode ?? -1;
            var sandbox = new GuardrailSandbox(Host.Os.Containment(), status, exitCode, outcome.End == RunEnd.Timeout,
                ran ? null : outcome.Reason ?? "the launcher did not start the sandbox",
                ran && exitCode == 0 ? null : Trim(output?.Stderr ?? ""));
            var counts = new GuardrailCounts(
                results.Count(r => r.Verdict == Verdicts.Blocked),
                results.Count(r => r.Verdict == Verdicts.Allowed),
                results.Count(r => !r.AsExpected),
                results.Count(r => r.Verdict == Verdicts.Skip),
                results.Count(r => r.Verdict == Verdicts.Info));
            var notes = new List<string>();
            if (results.Any(r => r.SandboxOnly)) notes.Add(TmpfsNote);
            if (outcome.End == RunEnd.Stopped) notes.Add("The check was stopped before it finished, so this report is incomplete; run it again.");
            if (synthetic) notes.Add($"No member was named: the check ran under the global policy alone, as a {subject.Cli.DisplayName()} member of a synthetic team.");
            var cliName = subject.Cli.Id();
            if (probe.Context is { CredentialConfigured: false } hc && AgentPresets.NeedsHostSignIn(hc.Credential))
                notes.Add($"{cliName} is not signed in on this computer (or its sign-in cannot be used here yet): the check never uses it, but the member's runs will not start until you sign in to {cliName} in your own terminal.");
            else if (probe.Context is { CredentialConfigured: false } c)
                notes.Add($"No model credential is set up for {cliName} yet: the check ran as {c.Credential} (it never uses one), but the member's runs will not start until one is added in Settings → Model credentials.");
            // Only when the sandbox itself refused the request (the probe's request was built and submitted), not when
            // the launcher refused the run before calling the sandbox (policy, layout, compiler invariants).
            if (status == -1 && probe.Plan is not null && Host.Os == TargetOs.Linux)
                notes.Add("On Ubuntu 24.04 and later, AppArmor's kernel.apparmor_restrict_unprivileged_userns=1 refuses the user and network namespaces the sandbox's proxy mode needs, unless the app's AppArmor profile is installed (the Linux installer does this with root once).");
            var policy = probe.Context?.Policy;
            var run = new GuardrailRun(outcome.RunKey, probe.Context?.RunNumber, RunEnds.Code(outcome.End), outcome.Reason, launcher.BackendName,
                (probe.Context?.Credential ?? CredentialMode.ClaudeSubscription).ToString(), probe.Context?.CredentialConfigured ?? false,
                outcome.Exit?.KillLatency is { } k ? (long)k.TotalMilliseconds : null);
            var report = new GuardrailReport(nonce, new GuardrailSubject(subject.Team, subject.Name, cliName, synthetic),
                Host.Os.Slug(), started, clock.ElapsedMilliseconds,
                status == 0 && results.Count > 1 && results.All(r => r.AsExpected),
                counts, sandbox, policy?.AllowHosts ?? [], policy?.Loosened ?? [], notes, results, run);
            Save(report);
            return report;
        }
        finally
        {
            probe.CleanUp();
        }
    }

    /// <summary>The check's own guard: the gateway forwarded nothing to a model vhost, so no credential was swapped in.</summary>
    private static GuardrailResult NoModelCall(IReadOnlyList<GatewayEvent> events)
    {
        var model = events.Where(e => e.Kind == GatewayEventKinds.Forward
                                      && !string.Equals(e.Vhost, Vhosts.Bus, StringComparison.OrdinalIgnoreCase)).ToList();
        return model.Count == 0
            ? new GuardrailResult("check.no-model-call", "check", Verdicts.Blocked, Verdicts.Blocked, true,
                "the run's gateway forwarded nothing to a model vhost during the check, so no credential was swapped in")
            : new GuardrailResult("check.no-model-call", "check", Verdicts.Allowed, Verdicts.Blocked, false,
                $"the run's gateway forwarded {model.Count} request(s) to a model vhost ({string.Join(", ", model.Select(e => e.Vhost).Distinct())})");
    }

    /// <summary>The probe launch: plants the canaries and builds the plan on the launcher's layout and policy.</summary>
    private sealed class Probe(GuardrailCheck check, string nonce, GuardrailMember subject, bool synthetic, int decoyPort) : ILaunchProbe
    {
        private readonly List<string> _planted = [];
        private readonly List<string> _createdDirs = [];

        public IReadOnlyList<PolicyMember> ExtraMembers { get; } =
            [new PolicyMember(subject.Team, Decoy), new PolicyMember(Decoy, Decoy)];

        public LaunchMember? Synthetic { get; } = synthetic ? new LaunchMember(0, subject.Team, subject.Name, subject.Cli) : null;

        public TimeSpan MaxWall => ProbeTimeout + TimeSpan.FromSeconds(30);

        public LaunchProbeContext? Context { get; private set; }

        public Plan? Plan { get; private set; }

        public JsonObject Rewrite(LaunchProbeContext context)
        {
            Context = context;
            check.PrepareFolders(context.Layout, _createdDirs);
            Plan = check.BuildPlan(context.Layout, context.Policy, nonce, decoyPort, _planted);
            return WithProbe(context.Request, context.Layout, Plan);
        }

        public void CleanUp()
        {
            foreach (var f in _planted) TryDelete(f);
            foreach (var d in _createdDirs.AsEnumerable().Reverse()) TryDeleteEmptyDir(d);
        }
    }

    // ---------------------------------------------------------------- the plan

    internal sealed record Expectation(string Expected, bool MustBeLogged = false, string? Host = null);

    internal sealed class Plan
    {
        public required JsonObject Wire { get; init; }
        public Dictionary<string, Expectation> Expect { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> WritePaths { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> HostPaths { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, (string File, (long Size, DateTime Mtime)? Before)> OpenWrites { get; } = new(StringComparer.Ordinal);
        public required string Nonce { get; init; }
    }

    internal Plan BuildPlan(RunLayout l, EffectivePolicy policy, string nonce, int decoyPort, List<string> planted)
    {
        string C(string p) => HostPathCanonicalizer.Canonicalize(p);
        var marker = ".yawble-guardcheck-" + nonce;
        var reads = new JsonArray();
        var writes = new JsonArray();
        var tcp = new JsonArray();
        var plan = new Plan { Wire = new JsonObject(), Nonce = nonce };

        void ReadOpen(string id, string category, string path, string expected = Verdicts.Blocked)
        {
            reads.Add(new JsonObject { ["id"] = id, ["category"] = category, ["path"] = C(path), ["mode"] = "open" });
            plan.Expect[id] = new Expectation(expected);
            plan.HostPaths[id] = path;
        }
        void ReadList(string id, string category, string path, string expected = Verdicts.Blocked)
        {
            reads.Add(new JsonObject { ["id"] = id, ["category"] = category, ["path"] = C(path), ["mode"] = "list" });
            plan.Expect[id] = new Expectation(expected);
            plan.HostPaths[id] = path;
        }
        void OpenForWrite(string id, string category, string file)
        {
            // Opens an existing file for writing and closes it: nothing is written, so even a leak changes nothing.
            writes.Add(new JsonObject { ["id"] = id, ["category"] = category, ["path"] = C(file), ["mode"] = "open" });
            plan.Expect[id] = new Expectation(l.CliInstall.SignInReadWrite.Any(f => SamePath(f, file)) ? Verdicts.Allowed : Verdicts.Blocked);
            plan.OpenWrites[id] = (file, Fingerprint(file));
        }
        void Canary(string id, string category, string dir)
        {
            var file = Path.Combine(dir, marker);
            Directory.CreateDirectory(dir);
            File.WriteAllText(file, nonce);
            planted.Add(file);
            reads.Add(new JsonObject { ["id"] = id, ["category"] = category, ["path"] = C(file), ["mode"] = "canary", ["canary"] = nonce });
            plan.Expect[id] = new Expectation(Verdicts.Blocked);
        }
        void Write(string id, string category, string dir, string expected = Verdicts.Blocked)
        {
            var file = Path.Combine(dir, marker + "-w");
            writes.Add(new JsonObject { ["id"] = id, ["category"] = category, ["path"] = C(file) });
            plan.Expect[id] = new Expectation(expected);
            plan.WritePaths[id] = file;
        }
        void Tcp(string id, string ip, int port)
        {
            tcp.Add(new JsonObject { ["id"] = id, ["category"] = "network", ["host"] = ip, ["port"] = port });
            plan.Expect[id] = new Expectation(Verdicts.Blocked);
        }
        string H(params string[] parts) => Path.Combine([l.Home, .. parts]);
        // Host sign-in: the CLI's own sign-in files are granted on purpose (opened, never read); every other secret is not.
        var signIn = l.CliInstall.SignInReadOnly.Concat(l.CliInstall.SignInReadWrite).ToList();
        string Secret(string path) => signIn.Any(f => SamePath(f, path)) ? Verdicts.Allowed : Verdicts.Blocked;

        // The app's own state: canaries the core plants (fake content), and the real files opened without reading.
        Canary("app.data", "app-data", l.DataDir);
        ReadOpen("app.database", "app-data", l.DatabasePath);
        Canary("app.credentials-dir", "app-data", l.CredentialDir);
        Canary("app.grok-home", "app-data", l.HostGrokHome);
        ReadOpen("app.grok-home-auth", "app-data", Path.Combine(l.HostGrokHome, "auth.json"));
        Canary("member.other-member", "other-members", Path.Combine(l.AppDataRoot, "teams", MemberTeam(l), "members", Decoy));
        Canary("member.other-team", "other-members", Path.Combine(l.AppDataRoot, "teams", Decoy));
        if (l.OtherMemberRoots.FirstOrDefault(r => !r.EndsWith(Decoy, StringComparison.Ordinal)) is { } realOther)
            Canary("member.real-other-member", "other-members", realOther);

        // The person's home: secrets opened (never read), ~/.ssh listed.
        ReadList("home.ssh", "home", H(".ssh"));
        foreach (var key in new[] { "id_rsa", "id_ed25519", "id_ecdsa" }) ReadOpen("home.ssh-" + key, "home", H(".ssh", key));
        ReadOpen("home.claude-credentials", "home", H(".claude", ".credentials.json"), Secret(H(".claude", ".credentials.json")));
        ReadOpen("home.claude-json", "home", H(".claude.json"));
        ReadOpen("home.grok-auth", "home", H(".grok", "auth.json"), Secret(H(".grok", "auth.json")));
        ReadOpen("home.aws-credentials", "home", H(".aws", "credentials"));
        ReadOpen("home.gh-hosts", "home", H(".config", "gh", "hosts.yml"));
        ReadOpen("home.git-credentials", "home", H(".git-credentials"));
        ReadOpen("home.netrc", "home", H(".netrc"));
        if (Host.Os == TargetOs.MacOS) ReadOpen("home.login-keychain", "home", H("Library", "Keychains", "login.keychain-db"));
        if (Host.Os == TargetOs.Linux) ReadOpen("home.gnome-keyring", "home", H(".local", "share", "keyrings", "login.keyring"));
        // Windows: the drive root is enumerable (microsoft/mxc#1676), so the home's NAMES are listable: an accepted
        // residual. Everywhere: every denied folder in the home that exists on the host (the
        // personal folders by default) must refuse a listing, which is what keeps those names hidden on Windows.
        ReadList("home.list", "home", l.Home, Host.Os == TargetOs.Windows ? Verdicts.Allowed : Verdicts.Blocked);
        var homePaths = new PolicyPaths(Host.Os, l.Home);
        foreach (var denied in policy.Denied)
            if (homePaths.IsWithin(denied, l.Home) && !homePaths.Same(denied, l.Home) && Directory.Exists(denied)
                && !plan.HostPaths.Values.Any(p => homePaths.Same(p, denied)))
                ReadList("home.denied-list:" + Path.GetFileName(denied.TrimEnd(Path.DirectorySeparatorChar)), "home", denied);

        // Writes: the clone is the positive control (it must reach the host); nothing else may.
        Write("write.clone", "filesystem", l.ClonePath, Verdicts.Allowed);
        Write("write.member-root", "filesystem", l.MemberRoot);
        Write("write.app-data", "filesystem", l.DataDir);
        Write("write.grok-home", "filesystem", l.HostGrokHome);
        Write("write.other-member", "filesystem", Path.Combine(l.AppDataRoot, "teams", MemberTeam(l), "members", Decoy));
        Write("write.other-team", "filesystem", Path.Combine(l.AppDataRoot, "teams", Decoy));
        Write("write.host-home", "filesystem", l.Home);
        Write("write.install-bin", "filesystem", l.AppBinDir);
        CliInstallPlan(l, ReadOpen, ReadList, Canary, Write, OpenForWrite);
        if (Host.Os == TargetOs.Windows)
        {
            Write("write.windows-temp", "filesystem", @"C:\Windows\Temp");
            Write("write.programdata", "filesystem", @"C:\ProgramData");
            Write("write.users-public", "filesystem", @"C:\Users\Public");
        }
        else
        {
            Write("write.etc", "filesystem", "/etc");
            Write("write.usr-local", "filesystem", "/usr/local");
            Write("write.var-tmp", "filesystem", "/var/tmp");
            Write("write.tmp", "filesystem", "/tmp");
        }

        // Direct network: every path except the gateway must fail.
        Tcp("net.direct-ipv4", "1.1.1.1", 443);
        Tcp("net.direct-ipv6", "2606:4700:4700::1111", 443);
        Tcp("net.host-loopback", "127.0.0.1", decoyPort);
        if (Host.Os == TargetOs.Linux) Tcp("net.host-loopback-slirp", "10.0.2.2", decoyPort);
        if (DefaultGateway() is { } lan) Tcp("net.lan-gateway", lan, 80);
        var dns = new JsonArray(new JsonObject { ["id"] = "net.dns", ["category"] = "network", ["name"] = DeniedHost });
        plan.Expect["net.dns"] = new Expectation(Verdicts.Blocked);

        // The gateway: reachable with the run token (positive control); a guessed or missing token, a host off the
        // allowlist and every vendor host refused (and the CONNECTs logged).
        var allowed = policy.AllowHosts.FirstOrDefault(h => !h.Contains('*'));
        plan.Expect["gateway.reachable"] = new Expectation(Verdicts.Allowed);
        plan.Expect["gateway.guessed-token"] = new Expectation(Verdicts.Blocked, MustBeLogged: true);
        plan.Expect["gateway.no-token"] = new Expectation(Verdicts.Blocked, MustBeLogged: true);
        plan.Expect["gateway.http-denied-host"] = new Expectation(Verdicts.Blocked, MustBeLogged: true, Host: DeniedHost);
        plan.Expect["gateway.connect-denied-host"] = new Expectation(Verdicts.Blocked, MustBeLogged: true, Host: DeniedHost);
        foreach (var v in VendorHosts) plan.Expect["gateway.connect-vendor:" + v] = new Expectation(Verdicts.Blocked, MustBeLogged: true, Host: v);
        if (allowed is not null) plan.Expect["gateway.connect-allowed-host"] = new Expectation(Verdicts.Allowed, Host: allowed);

        plan.Expect["proc.host-signal"] = new Expectation(Verdicts.Blocked);
        if (Host.Os == TargetOs.Linux)
        {
            plan.Expect["proc.host-environ"] = new Expectation(Verdicts.Blocked);
            plan.Expect["proc.host-ptrace"] = new Expectation(Verdicts.Blocked);
            plan.Expect["credstore.dbus-session"] = new Expectation(Verdicts.Blocked);
            plan.Expect["credstore.dbus-env"] = new Expectation(Verdicts.Blocked);
            plan.Expect["display.x11-socket"] = new Expectation(Verdicts.Blocked);
        }
        if (Host.Os == TargetOs.Windows)
        {
            plan.Expect.Remove("proc.host-signal");
            plan.Expect["proc.host-open"] = new Expectation(Verdicts.Blocked);
            plan.Expect["proc.host-memory"] = new Expectation(Verdicts.Blocked);
            plan.Expect["credstore.cmdkey"] = new Expectation(Verdicts.Blocked);
        }
        else plan.Expect["display.env"] = new Expectation(Verdicts.Blocked);
        if (Host.Os == TargetOs.MacOS)
        {
            plan.Expect["credstore.keychain:Claude Code-credentials"] = new Expectation(Verdicts.Blocked);
            plan.Expect["credstore.keychain:" + CredentialService.ServiceName] = new Expectation(Verdicts.Blocked);
            plan.Expect["display.clipboard"] = new Expectation(Verdicts.Blocked);
        }
        plan.Expect["secrets.scan"] = new Expectation(Verdicts.Blocked);

        plan.Wire["v"] = 1;
        plan.Wire["nonce"] = nonce;
        plan.Wire["reads"] = reads;
        plan.Wire["writes"] = writes;
        plan.Wire["tcp"] = tcp;
        plan.Wire["dns"] = dns;
        plan.Wire["gateway"] = new JsonObject
        {
            ["deniedHost"] = DeniedHost,
            ["allowedHost"] = allowed,
            ["vendorHosts"] = new JsonArray(VendorHosts.Select(v => (JsonNode?)v).ToArray()),
        };
        plan.Wire["hostPid"] = Environment.ProcessId;
        plan.Wire["scanDirs"] = new JsonArray(C(l.Workspace), C(l.Tmp));
        plan.Wire["keychainServices"] = new JsonArray("Claude Code-credentials", CredentialService.ServiceName);
        return plan;
    }

    /// <summary>
    /// The member's CLI where the person installed it (detected at launch): its program readable and its install folders
    /// listable (positive controls), neither writable (a self-update from inside can only fail); a sibling of each
    /// install folder in the home and the CLI's own config/credential folders blocked.
    /// </summary>
    private void CliInstallPlan(RunLayout l, Action<string, string, string, string> readOpen, Action<string, string, string, string> readList,
        Action<string, string, string> canary, Action<string, string, string, string> write, Action<string, string, string> openForWrite)
    {
        var cli = l.CliInstall;
        if (cli.Placeholder) return;
        var paths = new PolicyPaths(l.Os, l.Home);
        readOpen("cli.read-program", "cli-install", cli.ExecutablePath, Verdicts.Allowed);
        openForWrite("write.cli-program", "cli-install", cli.ExecutablePath);
        for (var i = 0; i < cli.ReadOnlyFolders.Count; i++)
        {
            var folder = cli.ReadOnlyFolders[i];
            if (!Directory.Exists(folder)) continue; // a file grant: the program checks above cover it
            readList($"cli.list-install:{i}", "cli-install", folder, Verdicts.Allowed);
            write($"write.cli-install:{i}", "cli-install", folder, Verdicts.Blocked);
            // Only the install itself: a canary next to it (in its parent, inside the home) must stay unreadable.
            var parent = Path.GetDirectoryName(folder.TrimEnd(Path.DirectorySeparatorChar));
            if (parent is not null && paths.IsWithin(parent, l.Home) && !paths.Same(parent, l.Home) && Directory.Exists(parent)
                && !cli.ReadOnlyFolders.Any(g => paths.IsWithin(parent, g)))
                canary($"cli.install-sibling:{i}", "cli-install", parent);
        }
        // Host sign-in: each sign-in file readable (opened, never read) and, when read-only, not writable: opened for
        // writing without truncation (nothing is written; the host file's size and time are compared after), and no
        // new file can be made beside it. Everything else of its folder stays blocked (cli.config-canary plants a
        // dummy file there; the host's own entries are listed by cli.config-list).
        var signIn = cli.SignInReadOnly.Select(f => (File: f, ReadOnly: true)).Concat(cli.SignInReadWrite.Select(f => (File: f, ReadOnly: false))).ToList();
        for (var i = 0; i < signIn.Count; i++)
        {
            var (file, readOnly) = signIn[i];
            readOpen($"signin.read:{i}", "cli-signin", file, Verdicts.Allowed);
            openForWrite($"signin.write:{i}", "cli-signin", file);
            if (Path.GetDirectoryName(file) is { } folder && readOnly) write($"signin.folder-write:{i}", "cli-signin", folder, Verdicts.Blocked);
        }
        for (var i = 0; i < cli.ConfigFolders.Count; i++)
        {
            var config = cli.ConfigFolders[i];
            if (Directory.Exists(config))
            {
                // Windows: names listable (drive-root enumeration, an accepted residual); contents stay closed (the canary).
                readList($"cli.config-list:{i}", "cli-config", config, Host.Os == TargetOs.Windows ? Verdicts.Allowed : Verdicts.Blocked);
                canary($"cli.config-canary:{i}", "cli-config", config);
            }
            else if (File.Exists(config)) readOpen($"cli.config-file:{i}", "cli-config", config, Verdicts.Blocked);
        }
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(HostPathCanonicalizer.Canonicalize(a).TrimEnd(Path.DirectorySeparatorChar), HostPathCanonicalizer.Canonicalize(b).TrimEnd(Path.DirectorySeparatorChar),
            OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);

    private static (long Size, DateTime Mtime)? Fingerprint(string file)
    {
        try
        {
            var info = new FileInfo(file);
            return info.Exists ? (info.Length, info.LastWriteTimeUtc) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    private static string MemberTeam(RunLayout l) =>
        Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(l.MemberRoot))!)!;

    /// <summary>The member's request with only the command line (the probe) and a tighter process.timeout.</summary>
    private static JsonObject WithProbe(JsonObject compiled, RunLayout l, Plan plan)
    {
        var request = (JsonObject)compiled.DeepClone();
        var probe = AgentPresets.Join(l, l.AppBinDir, l.Os == TargetOs.Windows ? ProbeName + ".exe" : ProbeName);
        var arg = Convert.ToBase64String(Encoding.UTF8.GetBytes(plan.Wire.ToJsonString(SandboxJson.Compact)))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var process = (JsonObject)request["process"]!;
        // The layout's own path, as granted: the launcher already canonicalised its roots, and canonicalising
        // again here could name a path the request does not grant.
        process["commandLine"] = CommandLine.Join(l.Os, [probe, "--plan", arg]);
        var timeout = process["timeout"]!.GetValue<long>();
        process["timeout"] = Math.Min(timeout, (long)ProbeTimeout.TotalMilliseconds);
        return request;
    }

    internal void PrepareFolders(RunLayout l, List<string> created)
    {
        foreach (var dir in new[] { l.ClonePath, l.Workspace, l.Tmp, l.DataDir, l.CredentialDir, l.HostGrokHome })
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
        // The decoy folders exist only for the check; each folder this creates is removed afterwards if empty again.
        foreach (var decoy in new[]
                 {
                     Path.Combine(l.AppDataRoot, "teams", Decoy, "members", Decoy),
                     Path.Combine(l.AppDataRoot, "teams", MemberTeam(l), "members", Decoy),
                 })
        {
            var missing = new Stack<string>();
            for (var d = decoy; d is not null && !Directory.Exists(d); d = Path.GetDirectoryName(d)) missing.Push(d);
            Directory.CreateDirectory(decoy);
            created.AddRange(missing); // top-down; cleaned up in reverse (deepest first)
        }
    }

    // ---------------------------------------------------------------- judging

    private sealed record Line(string Id, string Category, string Outcome, string Evidence, bool? SandboxWrote, int? Entries, IReadOnlyList<string>? Names);

    private static List<Line> ParseLines(string stdout)
    {
        var lines = new List<Line>();
        foreach (var raw in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var o = JsonNode.Parse(raw)!.AsObject();
                lines.Add(new Line(o["id"]!.GetValue<string>(), o["category"]!.GetValue<string>(), o["outcome"]!.GetValue<string>(),
                    o["evidence"]!.GetValue<string>(), o["sandboxWrote"]?.GetValue<bool>(), o["entries"]?.GetValue<int>(),
                    o["names"]?.AsArray().Select(n => n!.GetValue<string>()).ToList()));
            }
            catch (Exception e) when (e is JsonException or InvalidOperationException or NullReferenceException)
            {
                // Not a probe line (the probe prints nothing else; a wrapper might). Ignored.
            }
        }
        return lines;
    }

    private List<GuardrailResult> Judge(Plan plan, List<Line> lines, IReadOnlyList<string> denied, IReadOnlyList<string> granted,
        IReadOnlyList<GatewayEvent> events)
    {
        var results = new List<GuardrailResult>();
        foreach (var line in lines)
        {
            plan.Expect.TryGetValue(line.Id, out var expect);
            var verdict = line.Outcome switch { "blocked" => Verdicts.Blocked, "allowed" => Verdicts.Allowed, "skip" => Verdicts.Skip, _ => Verdicts.Info };
            var evidence = line.Evidence;
            var sandboxOnly = false;
            bool? logged = null;

            if (plan.OpenWrites.TryGetValue(line.Id, out var opened))
            {
                // The probe's own outcome (it wrote nothing either way), plus: the host file must be unchanged.
                var after = Fingerprint(opened.File);
                if (after != opened.Before)
                {
                    verdict = Verdicts.Allowed;
                    evidence = $"the host file changed during the check: {opened.File}";
                }
            }
            else if (plan.WritePaths.TryGetValue(line.Id, out var hostFile))
            {
                var reached = File.Exists(hostFile) && SafeRead(hostFile) == plan.Nonce;
                if (reached)
                {
                    verdict = Verdicts.Allowed;
                    evidence = $"the file reached the host: {hostFile}";
                }
                else if (line.SandboxWrote == true)
                {
                    verdict = Verdicts.Blocked;
                    sandboxOnly = true;
                    evidence = $"created inside the sandbox only; it never reached the host ({hostFile}). The sandbox's writable tmpfs mask";
                }
                else verdict = Verdicts.Blocked;
                if (reached) TryDelete(hostFile);
            }
            else if (plan.HostPaths.TryGetValue(line.Id, out var hostPath))
            {
                if (line.Entries is { } seen)
                {
                    var onHost = CountEntries(hostPath);
                    // The sandbox masks each denied path with an empty mount and creates its parents as mount points, and
                    // does the same on the way to each granted path, so a listing can show names that are only placeholders
                    // or a grant the policy made. Only a host entry that is none of those counts.
                    var names = line.Names ?? [];
                    var leaked = names.Where(n => Path.Exists(Path.Combine(hostPath, n))
                                                  && !IsMaskPoint(Path.Combine(hostPath, n), denied)
                                                  && !IsMaskPoint(Path.Combine(hostPath, n), granted)).ToList();
                    var masks = names.Except(leaked).ToList();
                    if (seen > 0 && leaked.Count == 0)
                    {
                        verdict = Verdicts.Blocked;
                        evidence = $"listable, but only the sandbox's mount points for denied and granted paths are visible ({string.Join(", ", masks)}), none of the host's {Math.Max(onHost, 0)} entries: {hostPath}";
                    }
                    else if (leaked.Count > 0)
                    {
                        verdict = Verdicts.Allowed;
                        evidence = $"the host's entries are visible ({leaked.Count}): {hostPath}";
                    }
                    else evidence += onHost < 0 ? " (absent on the host too)" : $" (the host has {onHost} entries)";
                }
                else
                {
                    var present = File.Exists(hostPath);
                    evidence += present ? " (present on the host)" : " (absent on the host too, so this attempt proves less)";
                }
            }

            if (expect is { MustBeLogged: true } && verdict == Verdicts.Blocked)
            {
                logged = events.Any(e => e.Guardrail && (expect.Host is null
                    ? e.Kind == GatewayEventKinds.Unauthorized
                    : (e.Target ?? "").Contains(expect.Host, StringComparison.OrdinalIgnoreCase)));
                evidence += logged == true ? "; logged by the gateway" : "; NOT found in the gateway log";
            }

            var asExpected = expect is null || verdict == Verdicts.Skip || (verdict == expect.Expected && logged != false);
            results.Add(new GuardrailResult(line.Id, line.Category, verdict, expect?.Expected, asExpected, evidence, sandboxOnly, logged));
        }
        foreach (var (id, expect) in plan.Expect)
            if (!results.Any(r => r.Id == id))
                results.Add(new GuardrailResult(id, "missing", Verdicts.Missing, expect.Expected, false, "the probe did not report this attempt"));
        // Writes the probe never got to: still look on the host, and clean up.
        foreach (var (_, file) in plan.WritePaths)
            if (File.Exists(file) && SafeRead(file) == plan.Nonce) TryDelete(file);
        return results;
    }

    /// <summary>A denied path, or a folder the sandbox had to create to mount one (an ancestor of a denied path).</summary>
    private static bool IsMaskPoint(string path, IReadOnlyList<string> denied)
    {
        var p = HostPathCanonicalizer.Canonicalize(path).TrimEnd(Path.DirectorySeparatorChar);
        var cmp = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        return denied.Any(d => string.Equals(d.TrimEnd(Path.DirectorySeparatorChar), p, cmp)
                               || d.StartsWith(p + Path.DirectorySeparatorChar, cmp));
    }

    private static int CountEntries(string dir)
    {
        try { return Directory.Exists(dir) ? Directory.EnumerateFileSystemEntries(dir).Count() : -1; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return -1; }
    }

    private static string? SafeRead(string path)
    {
        try { return File.ReadAllText(path).Trim(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private static void TryDeleteEmptyDir(string path)
    {
        try { if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any()) Directory.Delete(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private static string? Trim(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Length > 2000 ? s[..2000] + "..." : s.Trim();

    private static string? DefaultGateway()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up)
                .SelectMany(n => n.GetIPProperties().GatewayAddresses)
                .Select(g => g.Address)
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !a.Equals(IPAddress.Any))?.ToString();
        }
        catch (NetworkInformationException)
        {
            return null;
        }
    }

    // ---------------------------------------------------------------- data

    private void Save(GuardrailReport report)
    {
        Directory.CreateDirectory(ReportDir);
        var path = ReportPath(report.Subject.Team, report.Subject.Member);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(report, GuardrailReport.Json) + "\n");
        File.Move(path + ".tmp", path, overwrite: true);
    }

    private List<GuardrailMember> LoadMembers()
    {
        using var c = new SqliteConnection(connectionString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT m.id, t.name, m.name, m.cli FROM members m JOIN teams t ON t.id = m.team_id ORDER BY m.id;";
        using var r = cmd.ExecuteReader();
        var list = new List<GuardrailMember>();
        while (r.Read())
            list.Add(new GuardrailMember(r.GetInt64(0), r.GetString(1), r.GetString(2), Agents.Profiles.AgentResolvers.Resolve(Agents.Profiles.AgentProfileStore.ForReading(connectionString), r.GetString(3)).Cli));
        return list;
    }
}

/// <summary>
/// Runs <c>body</c> once the member holds a global run slot, and returns when it has given the slot back. Throws
/// <see cref="GuardrailException"/> ("busy") when the member is running or queued.
/// </summary>
public delegate Task GuardrailRunSlot(string team, string member, Func<CancellationToken, Task> body, CancellationToken ct,
    GuardrailProgress? progress = null);

/// <summary>
/// Where a started check is, for guardrail.get and guardrail.stop: "queued" (waiting for another check or for a
/// global run slot), then "running" (its sandbox is up). Thread-safe; set by the check and its slot.
/// </summary>
public sealed class GuardrailProgress
{
    private readonly object _lock = new();
    private Bus.SessionTicket? _ticket;

    public string Phase { get; private set; } = "queued";

    /// <summary>The sandbox's team and member in the run registry once running (the synthetic subject's too).</summary>
    public (string Team, string Member)? Run { get; private set; }

    internal void Queued() { lock (_lock) Phase = "queued"; }

    internal void Running(string team, string member)
    {
        lock (_lock)
        {
            Phase = "running";
            Run = (team, member);
        }
    }

    internal void Waiting(Bus.SessionTicket ticket) { lock (_lock) _ticket = ticket; }

    /// <summary>The position in the global run queue while waiting for a slot; null when not waiting in it.</summary>
    public async Task<int?> PositionAsync()
    {
        Bus.SessionTicket? ticket;
        lock (_lock) ticket = Phase == "queued" ? _ticket : null;
        return ticket is null ? null : await ticket.PositionAsync();
    }

    /// <summary>Takes a queued check out of the global run queue (its slot request ends "withdrawn").</summary>
    internal async Task WithdrawAsync()
    {
        Bus.SessionTicket? ticket;
        lock (_lock) ticket = Phase == "queued" ? _ticket : null;
        if (ticket is not null) await ticket.WithdrawAsync();
    }
}

/// <summary>The bus's run slots for the check (a session-FIFO entry, ordered beside run_queue).</summary>
public static class GuardrailRunSlots
{
    public static GuardrailRunSlot From(Bus.BusEngine bus) => async (team, member, body, ct, progress) =>
    {
        Exception? failed = null;
        Bus.SessionTicket ticket;
        try
        {
            ticket = await bus.RequestSessionAsync(team, member, async token =>
            {
                try { await body(token); }
                catch (Exception e) { failed = e; }
            }, ct);
        }
        catch (Bus.BusRefusal e)
        {
            throw new GuardrailException(e.Code, e.Message + "; run the check when the member is idle");
        }
        progress?.Waiting(ticket);
        if (!await ticket.Admission) throw new GuardrailException("withdrawn", "the check was withdrawn before it got a run slot");
        await ticket.Completion;
        if (failed is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(failed);
    };
}

public sealed class GuardrailException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
