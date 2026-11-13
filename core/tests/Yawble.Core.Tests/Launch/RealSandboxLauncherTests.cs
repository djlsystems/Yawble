using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Yawble.Core.Bus;
using Yawble.Core.Credentials;
using Yawble.Core.Gateway;
using Yawble.Core.Launch;
using Yawble.Core.Sandboxing;
using Yawble.Core.Policy;
using Yawble.Core.Tests.Agents;
using Yawble.Core.Tests.Bus;
using Yawble.Core.Tests.Gateway;
using Xunit;

namespace Yawble.Core.Tests.Launch;

/// <summary>
/// Whether the sandbox's own native backend on this host starts a sandbox in proxy-only mode (the mode every agent
/// run uses). The sandbox has no request probe on Linux or macOS (mxc_probe_request_json_with_error is Windows-only),
/// so the probe is the sandbox's answer to a harmless real request: <c>true</c> with egress through a loopback proxy.
/// The tests below skip with the sandbox's own message when it refuses, and are otherwise never skipped.
/// </summary>
internal static class RealSandbox
{
    private static readonly Lazy<string?> s_refusal = new(ProbeProxyMode);

    /// <summary>Null when the sandbox starts a proxy-mode sandbox here; else the sandbox's refusal.</summary>
    public static string? ProxyModeRefusal => s_refusal.Value;

    private static string? ProbeProxyMode()
    {
        if (!OperatingSystem.IsLinux()) return "these real-sandbox tests are written for the sandbox's Linux (bubblewrap) backend";
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        var request = new JsonObject
        {
            ["version"] = SandboxPin.RequestContract,
            ["containment"] = "bubblewrap",
            ["process"] = new JsonObject { ["commandLine"] = "/bin/true", ["cwd"] = "/", ["env"] = new JsonArray("PATH=/usr/bin:/bin"), ["timeout"] = 60_000 },
            ["filesystem"] = new JsonObject { ["readonlyPaths"] = new JsonArray(), ["readwritePaths"] = new JsonArray(), ["deniedPaths"] = new JsonArray() },
            ["network"] = new JsonObject { ["egress"] = new JsonObject { ["default"] = "deny" }, ["ingress"] = new JsonObject { ["default"] = "deny", ["hostLoopback"] = "deny" } },
            ["runtimeConfig"] = new JsonObject { ["networkProxy"] = $"http://127.0.0.1:{port}" },
            ["telemetry"] = new JsonObject { ["enabled"] = false },
        };
        try
        {
            using var p = new NativeSandboxBackend().Spawn(request.ToJsonString());
            var deadline = DateTime.UtcNow.AddSeconds(30);
            SandboxExit? exit;
            while ((exit = p.TryWait()) is null && DateTime.UtcNow < deadline) Thread.Sleep(20);
            if (exit is null) { p.Kill(); return "the proxy-mode sandbox did not finish /bin/true in 30 s"; }
            return exit.Value.ExitCode == 0 ? null : $"/bin/true exited {exit.Value.ExitCode} in the proxy-mode sandbox";
        }
        catch (SandboxStartException e)
        {
            return "the sandbox refused a proxy-mode request: " + e.Message;
        }
        catch (DllNotFoundException e)
        {
            return "libmxc_ffi is not loadable: " + e.Message;
        }
    }

    /// <summary>Also appends the line to $YAWBLE_REAL_SANDBOX_REPORT when set.</summary>
    public static void Report(string line)
    {
        TestContext.Current.SendDiagnosticMessage(line);
        if (Environment.GetEnvironmentVariable("YAWBLE_REAL_SANDBOX_REPORT") is { Length: > 0 } file)
            File.AppendAllText(file, $"{DateTimeOffset.UtcNow:O} {line}{Environment.NewLine}");
    }

    public static void SkipUnlessAvailable()
    {
        if (ProxyModeRefusal is { } why) Assert.Skip(why);
    }
}

/// <summary>
/// The launcher under a REAL sandbox (Linux, bubblewrap + slirp4netns): the real launcher, the
/// real per-run gateway and run token, the real yawble-bridge and yawble-grok-auth, and the native mxc_ffi. Only the
/// agent CLI is a stand-in (Launch/RealSandbox/standin-cli.py, installed on the host (<root>/host-clis/) as claude and grok and found by the test detector), and the
/// model upstream is the fake one; every secret is a fake. Nothing here reads or uses a real credential.
/// </summary>
[Collection(RealSandboxCollection.Name)]
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public class RealSandboxLauncherTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static JsonElement Run(Message m) => JsonDocument.Parse(m.Payload).RootElement.GetProperty("run");

    /// <summary>The launcher on the real gateway, with the sandbox as its backend and the stand-in CLIs installed.</summary>
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    internal sealed class Kit : IAsyncDisposable
    {
        public required GatewayFixture Gateway { get; init; }
        public required LaunchKit Launch { get; init; }
        public List<(RunIdentity Run, IRunGateway Gateway)> Started { get; } = [];
        public string Install => Launch.Host.InstallDir;
        public string App => Launch.Host.AppDataRoot;

        public static async Task<Kit> StartAsync(object scenario, int idleSec = 3600, int wallSec = 3600, double speed = 60, IReadOnlyList<string>? allowHosts = null, int limit = 4,
            Func<IAgentRunner, LaunchHost, string, IAgentRunner>? wrapRunner = null)
        {
            var f = await GatewayFixture.StartAsync();
            f.Credentials.Set(CredentialSlot.ClaudeSetupToken, FakeSecrets.SetupToken);
            FakeSecrets.WriteGrokAuth(f.GrokHome, FakeSecrets.GrokSession1, f.Clock.GetUtcNow() + TimeSpan.FromHours(6));
            var factory = new Recording(new GatewayRunFactory(f.Host));
            var launch = await LaunchKit.StartAsync(idleSec: idleSec, wallSec: wallSec, credentials: new ServiceCredentialModes(f.Credentials, f.GrokHome),
                db: f.Db, gateways: factory, realBackend: new NativeSandboxBackend(), speed: speed, allowHosts: allowHosts, limit: limit, wrapRunner: wrapRunner);
            var kit = new Kit { Gateway = f, Launch = launch };
            factory.Started = kit.Started;
            kit.InstallTools(scenario);
            return kit;
        }

        public void WriteScenario(object scenario)
        {
            foreach (var cli in new[] { AgentCli.Claude, AgentCli.Grok })
                File.WriteAllText(Path.Combine(Launch.Layout("dev1", cli).CliFolder(), "scenario.json"), JsonSerializer.Serialize(scenario));
        }

        private void InstallTools(object scenario)
        {
            var bin = Path.Combine(Install, "bin");
            Directory.CreateDirectory(bin);
            Directory.CreateDirectory(Path.Combine(Install, "roles"));
            InstallTool(bin, "yawble-bridge", Binaries.Bridge());
            InstallTool(bin, "yawble-grok-auth", Binaries.GrokAuth());
            var standin = Path.Combine(AppContext.BaseDirectory, "Launch", "RealSandbox", "standin-cli.py");
            var json = JsonSerializer.Serialize(scenario);
            foreach (var cli in new[] { AgentCli.Claude, AgentCli.Grok })
            {
                var layout = Launch.Layout("dev1", cli);
                File.Copy(standin, layout.CliPath, overwrite: true);
                File.SetUnixFileMode(layout.CliPath, (UnixFileMode)0b111_101_101);
                File.WriteAllText(Path.Combine(layout.CliFolder(), "scenario.json"), json);
            }
            // Things on the host the sandbox must not reach (fake content only).
            var l = Launch.Layout("dev1", AgentCli.Claude);
            Directory.CreateDirectory(l.DataDir);
            File.WriteAllText(l.DatabasePath, "fake app database");
            Directory.CreateDirectory(l.CredentialDir);
            File.WriteAllText(Path.Combine(l.CredentialDir, "credentials.json"), FakeSecrets.SetupToken);
            Directory.CreateDirectory(l.HostGrokHome);
            File.WriteAllText(Path.Combine(l.HostGrokHome, "auth.json"), "{\"key\":\"fake-host-grok-session\"}");
            Directory.CreateDirectory(Path.Combine(Launch.Host.Home, ".ssh"));
            File.WriteAllText(Path.Combine(Launch.Host.Home, ".ssh", "id_ed25519"), "fake key");
        }

        /// <summary>The bridge binaries: YAWBLE_*_BIN (native AOT) if set, else the JIT build behind a /usr/bin/dotnet wrapper.</summary>
        private static void InstallTool(string bin, string name, ProcessStartInfo psi)
        {
            var target = Path.Combine(bin, name);
            if (psi.FileName != "dotnet")
            {
                File.Copy(psi.FileName, target, overwrite: true);
            }
            else
            {
                var dll = psi.ArgumentList[0];
                var dir = Path.Combine(bin, name + ".d");
                foreach (var file in Directory.EnumerateFiles(Path.GetDirectoryName(dll)!))
                {
                    Directory.CreateDirectory(dir);
                    File.Copy(file, Path.Combine(dir, Path.GetFileName(file)), overwrite: true);
                }
                var dotnet = File.Exists("/usr/bin/dotnet") ? "/usr/bin/dotnet" : "dotnet";
                // Relative to the wrapper: the sandbox binds the install folder at the request's path, canonical or not.
                File.WriteAllText(target, $"#!/bin/sh\nexec {dotnet} \"$(dirname \"$0\")/{name}.d/{Path.GetFileName(dll)}\" \"$@\"\n");
            }
            File.SetUnixFileMode(target, (UnixFileMode)0b111_101_101);
        }

        public JsonElement Out(string member, AgentCli cli)
        {
            var path = Path.Combine(Launch.Layout(member, cli).Workspace, "standin-out.json");
            Assert.True(File.Exists(path), $"the stand-in never ran in {member}'s sandbox ({path})");
            return JsonDocument.Parse(File.ReadAllText(path)).RootElement;
        }

        public async ValueTask DisposeAsync()
        {
            await Launch.DisposeAsync();
            await Gateway.DisposeAsync();
        }
    }

    private sealed class Recording(IRunGatewayFactory inner) : IRunGatewayFactory
    {
        public List<(RunIdentity Run, IRunGateway Gateway)> Started { get; set; } = [];

        public async Task<IRunGateway> StartAsync(RunIdentity run, RunActivity activity, CancellationToken ct)
        {
            var g = await inner.StartAsync(run, activity, ct);
            lock (Started) Started.Add((run, g));
            return g;
        }
    }

    private static object Agent(string say) => new
    {
        say,
        model = true,
        bus = new object[]
        {
            new { tool = "progress", args = new { status = say + ": working" } },
            new { tool = "handback", args = new { delivered = say + ": done" } },
        },
    };

    [Theory]
    [InlineData("claude")]
    [InlineData("grok")]
    public async Task AMemberRunsHeadlessInSandboxThroughTheGatewayAndTheBridge(string cliName)
    {
        RealSandbox.SkipUnlessAvailable();
        var cli = cliName == "claude" ? AgentCli.Claude : AgentCli.Grok;
        var member = cli == AgentCli.Claude ? "dev1" : "dev2";
        await using var kit = await Kit.StartAsync(new Dictionary<string, object> { [member] = Agent($"{cliName} in sandbox") });

        await kit.Launch.Bus.PersonTellAsync("alpha", member, "say hello from the sandbox", ct: Ct);
        var outcome = await kit.Launch.OutcomeAsync(member);
        var o = kit.Out(member, cli);
        Assert.True(MessageTypes.Completed == outcome.Type, outcome.Payload + " / " + o);
        RealSandbox.Report($"{cliName}: {outcome.Type}, sandbox ran {Run(outcome).GetProperty("durationMs").GetInt64() / LaunchKit.Speed:0} ms real; stand-in uid {o.GetProperty("uid")}, proxy {o.GetProperty("proxy")}; steps {o.GetProperty("steps")}");

        // It ran inside the sandbox: as the bwrap user namespace's user, with the gateway as its only proxy (10.0.2.2 is the
        // slirp4netns host alias the sandbox rewrites 127.0.0.1 to).
        Assert.Equal(cliName, o.GetProperty("cli").GetString());
        Assert.StartsWith("http://10.0.2.2:", o.GetProperty("proxy").GetString());
        var model = o.GetProperty("steps").GetProperty("model");
        Assert.True(model.TryGetProperty("status", out var st) && st.GetInt32() == 200, model.ToString());
        Assert.Contains(cli == AgentCli.Claude ? "claude-subscription" : "grok-session-1", model.GetProperty("body").GetString());
        if (cli == AgentCli.Grok) Assert.True(model.GetProperty("tokenIsRunToken").GetBoolean());

        // The bridge's progress and handback landed on the bus as alpha/<member>, caused by the person's tell.
        var bus = o.GetProperty("steps").GetProperty("bus");
        Assert.True(bus.ValueKind == JsonValueKind.Array, bus.ToString());
        Assert.All(bus.EnumerateArray(), c => Assert.False(c.GetProperty("isError").GetBoolean(), c.ToString()));
        var messages = kit.Launch.Messages();
        var tell = messages.Single(m => m.Type == MessageTypes.InstructionPrefix + "alpha/" + member);
        Assert.Equal(tell.Seq, messages.Single(m => m.Type == MessageTypes.Progress && m.Source == "alpha/" + member).CausationSeq);
        Assert.Equal(tell.Seq, messages.Single(m => m.Type == MessageTypes.Handback && m.Source == "alpha/" + member).CausationSeq);

        // Only the run token was in the sandbox; the upstream saw the fake secret, never the token; the token is revoked.
        var runId = JsonDocument.Parse(outcome.Payload).RootElement.GetProperty("runId").GetInt64();
        var gateway = kit.Started.First(g => g.Run.Member == member).Gateway;
        Assert.Equal(TokenCheck.Revoked, kit.Gateway.Tokens.Check(runId, gateway.RunToken));
        Assert.All(kit.Gateway.Upstream.Requests, r => Assert.DoesNotContain(gateway.RunToken, r.Authorization ?? ""));
        Assert.Contains(kit.Gateway.Log.Read(runId), e => e.Kind == GatewayEventKinds.Forward && e.Status == 200);
        Assert.Equal(0, Run(outcome).GetProperty("guardrails").GetArrayLength()); // grok's external auth.json is accepted
        Assert.Equal(0, Run(outcome).GetProperty("exitCode").GetInt32());
    }

    [Fact]
    public async Task GuardrailsHoldInsideTheSandbox()
    {
        RealSandbox.SkipUnlessAvailable();
        await using var kit = await Kit.StartAsync(new Dictionary<string, object>(), allowHosts: ["registry.npmjs.org"]);
        var l = kit.Launch.Layout("dev1", AgentCli.Claude);
        Directory.CreateDirectory(Path.Combine(l.AppDataRoot, "teams", "alpha", "members", "dev2"));
        File.WriteAllText(Path.Combine(l.AppDataRoot, "teams", "alpha", "members", "dev2", "notes.txt"), "dev2's work");
        var upstream = kit.Gateway.Upstream.BaseUri;
        var otherGateway = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        otherGateway.Start();
        var otherPort = ((System.Net.IPEndPoint)otherGateway.LocalEndpoint).Port;
        // Folders the sandbox may see only as its masks (an empty tmpfs, or /dev/null over a file): the names it may
        // list in each. None of the host's own entries (the database's content, auth.json, dev2's notes, keys).
        var home = kit.Launch.Host.Home;
        var dev2 = Path.Combine(l.AppDataRoot, "teams", "alpha", "members", "dev2");
        var masks = new Dictionary<string, string[]>
        {
            [l.DataDir] = ["credentials", "yawble.db"],
            [l.CredentialDir] = [],
            [l.HostGrokHome] = [],
            [dev2] = [],
            [home] = [".aws", ".config", ".ssh", "src"],
            [Path.Combine(home, ".ssh")] = [],
        };
        try
        {
            kit.WriteScenario(new Dictionary<string, object>
            {
                ["dev1"] = new
                {
                    say = "guardrails",
                    guardrails = new
                    {
                        write = new[]
                        {
                            Path.Combine(l.AppDataRoot, "escape.txt"), Path.Combine(l.DataDir, "escape.txt"), Path.Combine(l.HostGrokHome, "escape.txt"),
                            Path.Combine(kit.Launch.Host.Home, "escape.txt"), Path.Combine(l.AppBinDir, "escape"), Path.Combine(l.CliFolder(), "escape"),
                            Path.Combine(l.AppDataRoot, "teams", "alpha", "members", "dev2", "escape.txt"), "/etc/escape", "/usr/escape", "/escape",
                            Path.Combine(l.ClonePath, "allowed.txt"),
                        },
                        read = new[]
                        {
                            l.DatabasePath, Path.Combine(l.CredentialDir, "credentials.json"), Path.Combine(l.HostGrokHome, "auth.json"),
                            Path.Combine(kit.Gateway.GrokHome.Path, "auth.json"), kit.Launch.Db.Path,
                            Path.Combine(kit.Launch.Host.Home, ".ssh", "id_ed25519"),
                        },
                        listNames = masks.Keys.ToArray(),
                        tcp = new[]
                        {
                            $"10.0.2.2:{upstream.Port}", $"127.0.0.1:{upstream.Port}", $"10.0.2.2:{otherPort}", $"127.0.0.1:{otherPort}",
                            "1.1.1.1:443", "8.8.8.8:53", "[2606:4700:4700::1111]:443",
                        },
                        directHttp = new[] { "http://example.com/", "https://registry.npmjs.org/" },
                        connect = new[] { "example.com:443", "api.anthropic.com:443", "registry.npmjs.org:443" },
                    },
                },
            });
            await kit.Launch.Bus.PersonTellAsync("alpha", "dev1", "try to get out", ct: Ct);
            var outcome = await kit.Launch.OutcomeAsync("dev1");
            var g = kit.Out("dev1", AgentCli.Claude).GetProperty("steps").GetProperty("guardrails");
            RealSandbox.Report("guardrails: " + g);

            var allowedWrite = Path.Combine(l.ClonePath, "allowed.txt");
            foreach (var p in g.EnumerateObject())
            {
                if (p.Name.StartsWith("connect ", StringComparison.Ordinal)) continue;
                if (p.Name.StartsWith("write ", StringComparison.Ordinal))
                {
                    // What counts is the host: the file exists on the host only for the member's own clone.
                    var path = p.Name["write ".Length..];
                    Assert.True(File.Exists(path) == (path == allowedWrite), $"{p.Name}: on the host {File.Exists(path)}; in the sandbox {p.Value}");
                    continue;
                }
                if (p.Name.StartsWith("names ", StringComparison.Ordinal))
                {
                    // escape.txt is the stand-in's own write above, which stayed in the sandbox's mask (checked on the host).
                    var names = p.Value.GetProperty("names").EnumerateArray().Select(n => n.GetString()!).Where(n => n != "escape.txt").ToList();
                    Assert.True(names.All(n => masks[p.Name["names ".Length..]].Contains(n)), $"{p.Name}: {p.Value}");
                    continue;
                }
                Assert.False(p.Value.GetProperty("exposed").GetBoolean(), $"{p.Name}: {p.Value}");
            }
            // The app's data folder and host Grok home are not listable with their contents: at most the sandbox's masks.
            Assert.Equal("written from the sandbox", File.ReadAllText(allowedWrite));
            Assert.Equal("fake app database", File.ReadAllText(l.DatabasePath));

            // Through the gateway: a host not on the allowlist and a vendor host are refused (403) and logged;
            // the allowlisted host is let through when the host machine can reach it.
            Assert.Contains("403", g.GetProperty("connect example.com:443").GetProperty("statusLine").GetString());
            Assert.Contains("403", g.GetProperty("connect api.anthropic.com:443").GetProperty("statusLine").GetString());
            var npm = g.GetProperty("connect registry.npmjs.org:443").GetProperty("statusLine").GetString();
            Assert.DoesNotContain("403", npm);
            if (HostReaches("registry.npmjs.org", 443)) Assert.Contains("200", npm);

            var runId = JsonDocument.Parse(outcome.Payload).RootElement.GetProperty("runId").GetInt64();
            var denied = kit.Gateway.Log.Read(runId).Where(e => e.Kind == GatewayEventKinds.ConnectDenied).Select(e => e.Target).ToList();
            Assert.Contains("example.com:443", denied);
            Assert.Contains("api.anthropic.com:443", denied);
            Assert.Equal(2, denied.Count);
        }
        finally
        {
            otherGateway.Stop();
        }
    }

    private static bool HostReaches(string host, int port)
    {
        try
        {
            using var c = new System.Net.Sockets.TcpClient();
            return c.ConnectAsync(host, port).Wait(TimeSpan.FromSeconds(5)) && c.Connected;
        }
        catch (Exception)
        {
            return false;
        }
    }

    [Theory]
    [InlineData("stop")]
    [InlineData("timeout")]
    public async Task StopAndTimeoutEndTheWholeTreeInsideSandboxWithinFiveSeconds(string how)
    {
        RealSandbox.SkipUnlessAvailable();
        var marker = Procs.NewMarker();
        // Timeout: the policy floor is 60 s of wall time; at 10x that is 6 s real, long enough for the tree to start.
        await using var kit = await Kit.StartAsync(new Dictionary<string, object> { ["dev1"] = new { say = "tree", tree = marker } },
            idleSec: 3600, wallSec: how == "timeout" ? 60 : 3600, speed: how == "timeout" ? 10 : 60);
        await kit.Launch.Bus.PersonTellAsync("alpha", "dev1", "start a tree and hang", ct: Ct);
        await Wait.UntilAsync(() => Procs.WithMarker("detached-" + marker).Count > 0 && Procs.WithMarker("child-" + marker).Count > 0,
            "the tree inside the sandbox", TimeSpan.FromSeconds(30));
        var before = Procs.WithMarker(marker).Count;

        var sw = Stopwatch.StartNew();
        if (how == "stop") Assert.True(kit.Launch.Registry.Stop("alpha", "dev1"));
        else sw = Stopwatch.StartNew(); // the core's timer decides; measured from here to the tree's end below
        var outcome = await kit.Launch.OutcomeAsync("dev1");
        var gone = Stopwatch.StartNew();
        while (Procs.WithMarker(marker).Count > 0 && gone.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(10, Ct);
        var treeGoneAfterOutcome = gone.Elapsed;
        var left = Procs.WithMarker(marker);
        var latency = Run(outcome).GetProperty("killLatencyMs").GetInt64();
        RealSandbox.Report(
            $"{how}: {before} marked processes; killLatencyMs={latency}; outcome after {sw.ElapsedMilliseconds} ms; tree gone {treeGoneAfterOutcome.TotalMilliseconds:0} ms after the outcome; left={left.Count}");

        Assert.Empty(left);
        Assert.Equal(how == "stop" ? FailureClasses.Stopped : FailureClasses.Timeout, Payload.Field(outcome, "failureClass"));
        Assert.True(latency < 5000, $"kill latency {latency} ms");
        Assert.False(Run(outcome).GetProperty("treeSurvived").GetBoolean());
        if (how == "stop") Assert.True(sw.ElapsedMilliseconds < 5000, $"Stop to outcome {sw.ElapsedMilliseconds} ms");
    }
}

[CollectionDefinition(RealSandboxCollection.Name, DisableParallelization = true)]
public sealed class RealSandboxCollection
{
    public const string Name = "real sandboxes";
}
