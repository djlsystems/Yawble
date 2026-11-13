using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Yawble.Core.Credentials;
using Yawble.Core.Gateway;
using Yawble.Core.Launch;
using Yawble.Core.Tests.Launch;
using Yawble.Core.Guardrail;
using Yawble.Core.Sandboxing;
using Yawble.Core.Policy;
using Yawble.Core.Storage;
using Yawble.Core.Tests.Gateway;
using Yawble.Core.Tests.Agents;
using Xunit;

namespace Yawble.Core.Tests.Guardrail;

/// <summary>
/// An app on disk for the guardrail check, all under one temp folder: a fake home (with FAKE secrets only), the app data
/// root (data/ with a migrated database, grok-home/, teams/), and an install folder whose bin/ holds yawble-guardcheck
/// (the native binary if YAWBLE_GUARDCHECK_BIN is set, else the JIT build behind a /usr/bin/dotnet wrapper). Teams:
/// payments (ada on Claude, ben on Grok) and docs (cy on Claude). No real credential is read or used.
/// </summary>
internal sealed class GuardrailKit : IAsyncDisposable
{
    public const string FakeSshKey = "FAKE ssh key, not a real one";

    private GuardrailKit(string root, string install, string? home = null)
    {
        Root = root;
        Home = home ?? Path.Combine(root, "home");
        App = Path.Combine(root, "app");
        Install = install;
        Data = Path.Combine(App, "data");
        ConnectionString = $"Data Source={Path.Combine(Data, "yawble.db")};Pooling=False";
    }

    public string Root { get; }
    public string Home { get; }
    public string App { get; }
    public string Install { get; }
    public string Data { get; }
    public string ConnectionString { get; }
    public required LaunchHost Host { get; init; }
    public required GatewayHost Gateways { get; init; }

    /// <summary>The check's decoy listener: in memory unless the kit is on a real gateway (null: the production TCP one).</summary>
    public Func<IGatewayListener>? DecoyListener { get; init; }
    public RunRecordStore Records => new(ConnectionString);

    /// <param name="installUnderHome">Install the app under the home folder (~/.local/opt/yawble), as the Linux
    /// tarball's install.sh does, instead of beside it.</param>
    /// <param name="realHome">The person's REAL home folder (the real-CLI tests): nothing is written into it by the kit
    /// (no fake secrets); the check itself only plants and removes its canaries.</param>
    /// <param name="clis">The detector; stand-ins outside the home by default.</param>
    /// <param name="realGateway">The real-sandbox tests: the per-run gateway and the check's decoy on real loopback ports (the
    /// sandbox connects to them). Otherwise both are in memory and no port is bound (the scripted-sandbox tests).</param>
    public static GuardrailKit Create(bool installUnderHome = false, string? realHome = null, Yawble.Core.Agents.ICliDetector? clis = null,
        bool realGateway = false)
    {
        var root = Directory.CreateTempSubdirectory("yawble-guard-").FullName;
        var install = installUnderHome ? Path.Combine(root, "home", ".local", "opt", "yawble") : Path.Combine(root, "install");
        // The person's CLIs, "installed on the host" outside the home and the app (<root>/host-clis/<cli>/); a test can
        // swap the detector's answer (Clis.Replace) for another layout, e.g. grok inside ~/.grok.
        var home = realHome ?? Path.Combine(root, "home");
        Directory.CreateDirectory(home);
        clis ??= TestClis.StandIns(root, home);
        var host = new LaunchHost(TargetOsExtensions.Host, home, Path.Combine(root, "app"), install,
            clis, HostPathCanonicalizer.Canonicalize);
        var data = Path.Combine(root, "app", "data");
        Directory.CreateDirectory(data);
        var cs = $"Data Source={Path.Combine(data, "yawble.db")};Pooling=False";
        SchemaMigrator.Migrate(cs);
        var kit = new GuardrailKit(root, install, realHome)
        {
            Host = host,
            DecoyListener = realGateway ? null : () => new Yawble.Core.Tests.Gateway.InMemoryGatewayListener(),
            // With the person's real home: Claude's sign-in is held by the gateway, read from the real host sign-in (never
            // renewed by the kit: no host claude is run).
            Gateways = new GatewayHost(realHome is null ? new NoGatewayCredentials()
                : new NoGatewayCredentials
                {
                    ClaudeHost = new Yawble.Core.Credentials.ClaudeHostSession(Path.Combine(realHome, ".claude", ".credentials.json"), () => null),
                    GrokHost = Yawble.Core.Credentials.GrokHome.OfThisComputer(v => v == "GROK_HOME" ? Path.Combine(realHome, ".grok") : null),
                    CopilotHost = true,
                    CodexHost = new Yawble.Core.Credentials.CodexHostSession(Path.Combine(realHome, ".codex", "auth.json"), () => null),
                },
                new GatewayLog(cs), new RunTokenRegistry(),
                realGateway ? null : new GatewayHostOptions { Listener = () => new Yawble.Core.Tests.Gateway.InMemoryGatewayListener() }),
        };
        kit.Seed(seedHome: realHome is null);
        return kit;
    }

    /// <summary>
    /// The core's real sandbox launcher on this kit (real per-run gateway with NO credentials, real policy store, real
    /// run records), with the given backend: <see cref="NativeSandboxBackend"/> for a real sandbox, or a scripted one.
    /// </summary>
    public SandboxLauncher Launcher(ISandboxBackend backend, ICredentialModes? modes = null, RunRegistry? registry = null) =>
        new(Host, new SqliteMemberDirectory(ConnectionString), new PolicyStore(ConnectionString, canonicalize: HostPathCanonicalizer.Canonicalize),
            modes ?? new FakeCredentialModes(), new GatewayRunFactory(Gateways), backend, new RunRecordStore(ConnectionString), registry ?? new RunRegistry());

    public GuardrailCheck Check(ISandboxBackend backend, ICredentialModes? modes = null) =>
        new(Launcher(backend, modes), ConnectionString) { DecoyListener = DecoyListener };

    public GuardrailCheck Check(IGuardrailSandbox sandbox, ICredentialModes? modes = null) => Check(new GuardrailSandboxBackend(sandbox), modes);

    private void Seed(bool seedHome)
    {
        // The person's home, with fake secrets where the real ones would be (never in a real home).
        if (seedHome) SeedHome();
        SeedApp();
    }

    private void SeedHome()
    {
        Directory.CreateDirectory(Path.Combine(Home, ".ssh"));
        File.WriteAllText(Path.Combine(Home, ".ssh", "id_ed25519"), FakeSshKey);
        Directory.CreateDirectory(Path.Combine(Home, ".claude"));
        File.WriteAllText(Path.Combine(Home, ".claude", ".credentials.json"), $"{{\"claudeAiOauth\":{{\"accessToken\":\"{FakeSecrets.SetupToken}\"}}}}");
        Directory.CreateDirectory(Path.Combine(Home, ".grok"));
        File.WriteAllText(Path.Combine(Home, ".grok", "auth.json"), "{\"key\":\"FAKE-grok\"}");
    }

    private void SeedApp()
    {
        // The app's private state.
        Directory.CreateDirectory(Path.Combine(App, "grok-home"));
        File.WriteAllText(Path.Combine(App, "grok-home", "auth.json"), "{\"key\":\"FAKE-host-grok-session\"}");
        Directory.CreateDirectory(Path.Combine(Data, "credentials"));
        // Two teams.
        using (var c = new SqliteConnection(ConnectionString))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = """
                INSERT INTO teams (name, created_at) VALUES ('payments', '2026-11-06T00:00:00Z'), ('docs', '2026-11-06T00:00:00Z');
                INSERT INTO members (team_id, name, role, cli, created_at) VALUES
                  (1, 'ada', 'member', 'claude', '2026-11-06T00:00:00Z'),
                  (1, 'ben', 'member', 'grok', '2026-11-06T00:00:00Z'),
                  (2, 'cy', 'member', 'claude', '2026-11-06T00:00:00Z');
                """;
            cmd.ExecuteNonQuery();
        }
        foreach (var (team, member) in new[] { ("payments", "ada"), ("payments", "ben"), ("docs", "cy") })
            foreach (var sub in new[] { "repo", "home", "tmp" })
                Directory.CreateDirectory(Path.Combine(App, "teams", team, "members", member, sub));
        // The app's install folder: bin/ and roles/ (the agent CLIs are the host's, see Create).
        Directory.CreateDirectory(Path.Combine(Install, "roles"));
        InstallProbe(Path.Combine(Install, "bin"));
        // yawble-run (Windows: in front of Grok); its JIT build, apphost and dll together, as for the probe.
        if (OperatingSystem.IsWindows() && Directory.Exists(Gateway.Binaries.RunnerBuild()))
            foreach (var file in Directory.EnumerateFiles(Gateway.Binaries.RunnerBuild()))
                File.Copy(file, Path.Combine(Install, "bin", Path.GetFileName(file)), overwrite: true);
        // yawble-bridge (Windows): the bus MCP server and, for Antigravity, its --shell tool server; yawble-grok-auth: Grok's
        // placeholder auth provider (the gateway holds Grok's sign-in).
        foreach (var build in new[] { Gateway.Binaries.BridgeBuild(), Gateway.Binaries.GrokAuthBuild() })
            if (OperatingSystem.IsWindows() && Directory.Exists(build))
                foreach (var file in Directory.EnumerateFiles(build))
                    File.Copy(file, Path.Combine(Install, "bin", Path.GetFileName(file)), overwrite: true);
        // The dotnet wrapper (Windows), in its own folder first on the sandbox's PATH.
        if (OperatingSystem.IsWindows() && Directory.Exists(Gateway.Binaries.DotnetShimBuild()))
        {
            var shim = Path.Combine(Install, "bin", Yawble.Core.Sandboxing.SandboxRequestCompiler.DotnetShimFolder);
            Directory.CreateDirectory(shim);
            foreach (var file in Directory.EnumerateFiles(Gateway.Binaries.DotnetShimBuild()))
                File.Copy(file, Path.Combine(shim, Path.GetFileName(file)), overwrite: true);
        }
    }

    internal static void InstallProbe(string bin)
    {
        Directory.CreateDirectory(bin);
        var psi = Binaries.GuardCheck();
        var target = Path.Combine(bin, OperatingSystem.IsWindows() ? GuardrailCheck.ProbeName + ".exe" : GuardrailCheck.ProbeName);
        if (psi.FileName != "dotnet")
        {
            File.Copy(psi.FileName, target, overwrite: true);
            return;
        }
        var dll = psi.ArgumentList[0];
        if (OperatingSystem.IsWindows())
        {
            // No POSIX wrapper on Windows: the build's apphost yawble-guardcheck.exe runs the JIT build when its dll and
            // runtimeconfig sit beside it, so the whole build output goes into bin/ as the installed probe.
            foreach (var file in Directory.EnumerateFiles(Path.GetDirectoryName(dll)!))
                File.Copy(file, Path.Combine(bin, Path.GetFileName(file)), overwrite: true);
            if (!File.Exists(target)) throw new InvalidOperationException($"the probe build has no apphost: {target}");
            return;
        }
        var dir = Path.Combine(bin, GuardrailCheck.ProbeName + ".d");
        Directory.CreateDirectory(dir);
        foreach (var file in Directory.EnumerateFiles(Path.GetDirectoryName(dll)!))
            File.Copy(file, Path.Combine(dir, Path.GetFileName(file)), overwrite: true);
        var dotnet = File.Exists("/usr/bin/dotnet") ? "/usr/bin/dotnet" : "dotnet";
        // Relative to the wrapper: the sandbox binds the install folder at the path in the request (canonical or not,
        // e.g. a symlinked temp root such as macOS /var), and the dll is wherever the wrapper is.
        File.WriteAllText(target, $"#!/bin/sh\nexec {dotnet} \"$(dirname \"$0\")/{GuardrailCheck.ProbeName}.d/{Path.GetFileName(dll)}\" \"$@\"\n");
        File.SetUnixFileMode(target, (UnixFileMode)0b111_101_101);
    }

    public long MemberId(string team, string member)
    {
        using var c = new SqliteConnection(ConnectionString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT m.id FROM members m JOIN teams t ON t.id = m.team_id WHERE t.name = $t AND m.name = $m;";
        cmd.Parameters.AddWithValue("$t", team);
        cmd.Parameters.AddWithValue("$m", member);
        return (long)cmd.ExecuteScalar()!;
    }

    public RunLayout Layout(string team, string member, AgentCli cli) =>
        RunLayout.Standard(Host.Os, Home, App, Install, new MemberRef(team, member), cli,
            CliInstall.FromDetected(Host.Os, Home, Clis.Clis.First(c => c.CliId == (cli == AgentCli.Claude ? "claude" : "grok")), DiskFolders.Instance,
                HostPathCanonicalizer.Canonicalize),
            [new("payments", "ada"), new("payments", "ben"), new("docs", "cy")]);

    /// <summary>The kit's detector: stand-ins by default.</summary>
    public FakeCliDetector Clis => (FakeCliDetector)Host.Clis;

    /// <summary>
    /// Installs a CLI the way its vendor does, INSIDE the fake home, and points the detector at it:
    /// "grok" = ~/.grok/downloads/grok-1.0.77-&lt;platform&gt; (inside the config folder ~/.grok, beside auth.json);
    /// "claude-native" = ~/.local/share/claude/versions/2.1.318 granted as the versions folder.
    /// </summary>
    public void InstallInHome(string layout)
    {
        string exe;
        DetectedCliHelper cli;
        switch (layout)
        {
            case "grok":
                var downloads = Path.Combine(Home, ".grok", "downloads");
                Directory.CreateDirectory(downloads);
                exe = Path.Combine(downloads, "grok-1.0.77-linux-x86_64");
                File.WriteAllText(Path.Combine(downloads, "grok-1.0.70-linux-x86_64"), "old version");
                Directory.CreateDirectory(Path.Combine(Home, ".grok", "bin"));
                Directory.CreateDirectory(Path.Combine(Home, ".grok", "sessions"));
                File.WriteAllText(Path.Combine(Home, ".grok", "config.toml"), "fake = true");
                cli = new("grok", exe, [downloads], [Path.Combine(Home, ".grok")]);
                break;
            case "claude-native":
                var versions = Path.Combine(Home, ".local", "share", "claude", "versions");
                Directory.CreateDirectory(versions);
                exe = Path.Combine(versions, "2.1.318");
                Directory.CreateDirectory(Path.Combine(Home, ".local", "bin"));
                File.WriteAllText(Path.Combine(Home, ".claude.json"), "{\"fake\":true}");
                cli = new("claude", exe, [versions], [Path.Combine(Home, ".claude"), Path.Combine(Home, ".claude.json")]);
                break;
            default:
                throw new ArgumentException(layout);
        }
        File.WriteAllText(exe, "#!/bin/sh\necho stand-in\n");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(exe, (UnixFileMode)0b111_101_101);
        Clis.Replace(TestClis.Found(cli.Id, exe, cli.ReadOnly, cli.Config));
    }

    private sealed record DetectedCliHelper(string Id, string Exe, IReadOnlyList<string> ReadOnly, IReadOnlyList<string> Config);

    public IReadOnlyList<GatewayEvent> GatewayEvents() => Gateways.Log.Read();

    public ValueTask DisposeAsync()
    {
        Gateways.Dispose();
        // Diagnostics: YAWBLE_KEEP_RUNS=1 keeps the run folders (the CLIs' own logs are in the members' homes).
        if (Environment.GetEnvironmentVariable("YAWBLE_KEEP_RUNS") == "1") return ValueTask.CompletedTask;
        try { Directory.Delete(Root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        return ValueTask.CompletedTask;
    }

    /// <summary>The probe plan the check put on the request's command line.</summary>
    public static JsonObject PlanOf(string requestJson)
    {
        var commandLine = JsonNode.Parse(requestJson)!["process"]!["commandLine"]!.GetValue<string>();
        var arg = commandLine.Split(' ')[^1].Trim('\'');
        var b = arg.Replace('-', '+').Replace('_', '/');
        b = b.PadRight(b.Length + (4 - b.Length % 4) % 4, '=');
        return JsonNode.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(b)))!.AsObject();
    }
}

/// <summary>Runs one exact sandbox request to completion and returns what it printed (a test double for the backend).</summary>
internal interface IGuardrailSandbox
{
    Task<SandboxOutcome> RunAsync(string requestJson, CancellationToken ct);
}

/// <summary>Status 0 means the sandbox ran; anything else is a refusal (<see cref="Message"/>), as the sandbox reports it.</summary>
internal sealed record SandboxOutcome(int Status, int ExitCode, bool TimedOut, string Stdout, string Stderr, string? Message);

/// <summary>An <see cref="IGuardrailSandbox"/> behind the launcher's backend seam: runs it at spawn, then replays its output.</summary>
internal sealed class GuardrailSandboxBackend(IGuardrailSandbox sandbox) : ISandboxBackend
{
    public string Name => "scripted";

    public ISandboxProcess Spawn(string requestJson)
    {
        var o = sandbox.RunAsync(requestJson, CancellationToken.None).GetAwaiter().GetResult();
        if (o.Status != 0) throw new SandboxStartException(o.Status, o.Message ?? $"sandbox status {o.Status}");
        return new Done(o);
    }

    public ISandboxProcess SpawnWithPty(string requestJson, PtySize size) => throw new NotSupportedException();

    private sealed class Done(SandboxOutcome o) : ISandboxProcess
    {
        public int Id => 0;
        public Stream? Output { get; } = new MemoryStream(Encoding.UTF8.GetBytes(o.Stdout));
        public Stream? Error { get; } = new MemoryStream(Encoding.UTF8.GetBytes(o.Stderr));
        public Stream? Input => null;
        public SandboxExit? TryWait() => new SandboxExit(o.ExitCode, o.TimedOut);
        public void Kill() { }
        public void KillForTimeout() { }
        public void Resize(PtySize size) { }
        public void Dispose() { }
    }
}

/// <summary>The gateway's credentials in these tests: none. The check never needs, injects or reads a credential.</summary>
internal sealed class NoGatewayCredentials : IGatewayCredentials
{
    public (ClaudeMode Mode, string? Value) Claude() => (ClaudeMode.Subscription, null);

    public string? XaiApiKey() => null;

    /// <summary>The real-CLI tests' host Grok home (read only: no keeper renews it here), or none.</summary>
    public GrokHome? GrokHost { get; init; }

    /// <summary>The real-CLI tests: the person's own Copilot token from the OS credential store (read only).</summary>
    public bool CopilotHost { get; init; }

    /// <summary>The real-CLI tests' host Codex sign-in (read only: no renewal here), or none.</summary>
    public Yawble.Core.Credentials.CodexHostSession? CodexHost { get; init; }

    public Task<Yawble.Core.Credentials.CodexHostToken?> CodexHostTokenAsync(CancellationToken ct) => Task.FromResult(CodexHost?.Read());

    public string? CopilotHostToken() => CopilotHost ? Yawble.Core.Agents.SignInCatalog.CopilotToken() : null;

    public GrokSession? GrokSession() => GrokHost?.ReadSession() is { } s && !s.ExpiredAt(DateTimeOffset.UtcNow) ? s : null;

    public Task<GrokRefreshResult> RefreshGrokAsync(bool force) =>
        Task.FromResult(new GrokRefreshResult(false, false, "the guardrail check has no credentials", null));

    /// <summary>The real-CLI tests' host Claude sign-in (read only), or none.</summary>
    public Yawble.Core.Credentials.ClaudeHostSession? ClaudeHost { get; init; }

    public Task<Yawble.Core.Credentials.ClaudeHostToken?> ClaudeHostTokenAsync(CancellationToken ct) =>
        Task.FromResult(ClaudeHost?.Read());
}

/// <summary>A scripted sandbox: answers with the lines a function makes from the plan (and can touch the host).</summary>
internal sealed class ScriptedSandbox(Func<JsonObject, JsonObject, SandboxOutcome> script) : IGuardrailSandbox
{
    public string? LastRequest { get; private set; }

    public Task<SandboxOutcome> RunAsync(string requestJson, CancellationToken ct)
    {
        LastRequest = requestJson;
        return Task.FromResult(script(JsonNode.Parse(requestJson)!.AsObject(), GuardrailKit.PlanOf(requestJson)));
    }
}

/// <summary>
/// NO sandbox: runs the probe as a plain host process with the request's env and the gateway as HTTP_PROXY. The check
/// must then FAIL, which proves its verdicts come from what actually happened, not from the probe's own claims.
/// </summary>
internal sealed class UnsandboxedRunner : IGuardrailSandbox
{
    public async Task<SandboxOutcome> RunAsync(string requestJson, CancellationToken ct)
    {
        var request = JsonNode.Parse(requestJson)!;
        var psi = Binaries.GuardCheck();
        var commandLine = request["process"]!["commandLine"]!.GetValue<string>();
        psi.ArgumentList.Add("--plan");
        psi.ArgumentList.Add(commandLine.Split(' ')[^1].Trim('\''));
        psi.Environment.Clear();
        foreach (var e in request["process"]!["env"]!.AsArray())
        {
            var kv = e!.GetValue<string>();
            var i = kv.IndexOf('=');
            psi.Environment[kv[..i]] = kv[(i + 1)..];
        }
        if (Environment.GetEnvironmentVariable("DOTNET_ROOT") is { } root) psi.Environment["DOTNET_ROOT"] = root;
        psi.Environment["HTTP_PROXY"] = request["runtimeConfig"]!["networkProxy"]!.GetValue<string>();
        psi.WorkingDirectory = request["process"]!["cwd"]!.GetValue<string>();
        using var p = Process.Start(psi)!;
        p.StandardInput.Close();
        var stdout = p.StandardOutput.ReadToEndAsync(ct);
        var stderr = p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        return new SandboxOutcome(0, p.ExitCode, false, await stdout, await stderr, null);
    }
}
