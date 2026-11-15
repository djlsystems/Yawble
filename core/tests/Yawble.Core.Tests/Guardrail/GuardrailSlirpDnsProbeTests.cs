using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Yawble.Core.Guardrail;
using Yawble.Core.Launch;
using Yawble.Core.Policy;
using Yawble.Core.Sandboxing;
using Yawble.Core.Tests.Gateway;
using Yawble.Core.Tests.Launch;
using Xunit;

namespace Yawble.Core.Tests.Guardrail;

/// <summary>
/// B038 (card 173): the routes to a resolver that slirp4netns opens and the user's session bus. On Linux the plan asks
/// for a raw DNS query to slirp's forwarder 10.0.2.3:53 by UDP (dns.slirp-udp) and TCP (dns.slirp-tcp), to 10.0.2.2:53
/// by UDP (dns.gateway-udp), and a connect to /run/user/&lt;uid&gt;/bus (dbus.user); each must be BLOCKED. The first
/// tests use a scripted sandbox; the last ones run the real yawble-guardcheck against the test's own listeners.
/// </summary>
public class GuardrailSlirpDnsProbeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly string[] NewIds = ["dns.slirp-udp", "dns.slirp-tcp", "dns.gateway-udp", "dbus.user"];

    private static string Line(string id, string outcome) =>
        $"{{\"id\":\"{id}\",\"category\":\"network\",\"outcome\":\"{outcome}\",\"evidence\":\"x\"}}";

    private static IEnumerable<string> Ids(JsonObject plan, string key) =>
        (plan[key]?.AsArray() ?? []).Select(n => n!["id"]!.GetValue<string>());

    /// <summary>
    /// A sandbox that holds: every attempt GuardCheck can plan, on any OS, answered the way the plan expects it (the
    /// positive controls reachable, the clone write reaching the host, everything else blocked), except the given ids,
    /// which are answered allowed.
    /// </summary>
    private static string Report(JsonObject plan, params string[] allowed)
    {
        var lines = new List<string>();
        var reads = plan["reads"]!.AsArray().Select(r => (Id: r!["id"]!.GetValue<string>(), Mode: r["mode"]!.GetValue<string>(), Path: r["path"]!.GetValue<string>())).ToList();
        var writes = plan["writes"]!.AsArray().Select(w => (Id: w!["id"]!.GetValue<string>(), Mode: w["mode"]?.GetValue<string>(), Path: w["path"]!.GetValue<string>())).ToList();
        // Positive controls: the CLI's program and install folders, its sign-in files (by any id that opens them, e.g.
        // home.claude-credentials), and on Windows the home's and CLI config folder's names (the drive-root residual).
        var signInPaths = reads.Where(r => r.Id.StartsWith("signin.read:", StringComparison.Ordinal)).Select(r => r.Path).ToHashSet();
        bool ReadAllowed((string Id, string Mode, string Path) r) =>
            r.Id == "cli.read-program" || r.Id.StartsWith("cli.list-install:", StringComparison.Ordinal) || signInPaths.Contains(r.Path)
            || (OperatingSystem.IsWindows() && (r.Id == "home.list" || r.Id.StartsWith("cli.config-list:", StringComparison.Ordinal)));
        foreach (var r in reads)
            lines.Add(ReadAllowed(r)
                ? Line(r.Id, "allowed") // a listing without entries: the judge keeps the probe's own outcome
                : r.Mode == "list"
                    ? $"{{\"id\":\"{r.Id}\",\"category\":\"t\",\"outcome\":\"blocked\",\"evidence\":\"x\",\"entries\":0}}"
                    : Line(r.Id, "blocked"));
        foreach (var w in writes)
        {
            if (w.Mode == "open")
            {
                // An existing host file opened for writing: allowed only for a read-write sign-in file (one with no
                // signin.folder-write beside it); the CLI's program and read-only sign-in files refuse it.
                var readWriteSignIn = w.Id.StartsWith("signin.write:", StringComparison.Ordinal)
                                      && !writes.Any(x => x.Id == "signin.folder-write:" + w.Id["signin.write:".Length..]);
                lines.Add(Line(w.Id, readWriteSignIn ? "allowed" : "blocked"));
                continue;
            }
            if (w.Id == "write.clone") File.WriteAllText(w.Path, plan["nonce"]!.GetValue<string>());
            lines.Add($"{{\"id\":\"{w.Id}\",\"category\":\"t\",\"outcome\":\"allowed\",\"evidence\":\"x\",\"sandboxWrote\":true}}");
        }
        foreach (var id in Ids(plan, "tcp").Concat(Ids(plan, "dns")).Concat(Ids(plan, "unix")).Concat(Ids(plan, "dnsRaw")))
            lines.Add(Line(id, allowed.Contains(id) ? "allowed" : "blocked"));
        // The attempts the probe makes on its own (not listed in the wire plan), for every OS GuardCheck plans for: an id
        // the plan does not expect on this OS is ignored by the judge. The gateway's positive controls are allowed; its
        // refusals must also be in the gateway log, which this fake does not write, so they answer skip (not under test).
        var gateway = plan["gateway"]!;
        lines.Add(Line("gateway.reachable", "allowed"));
        if (gateway["allowedHost"]?.GetValue<string>() is not null) lines.Add(Line("gateway.connect-allowed-host", "allowed"));
        foreach (var id in new[] { "gateway.guessed-token", "gateway.no-token", "gateway.http-denied-host", "gateway.connect-denied-host" }
                     .Concat((gateway["vendorHosts"]?.AsArray() ?? []).Select(v => "gateway.connect-vendor:" + v!.GetValue<string>())))
            lines.Add(Line(id, "skip"));
        foreach (var id in new[]
                 {
                     "proc.host-signal", "proc.host-environ", "proc.host-ptrace", "proc.host-open", "proc.host-memory",
                     "credstore.dbus-session", "credstore.dbus-env", "credstore.cmdkey", "display.env", "display.x11-socket",
                     "display.clipboard", "secrets.scan",
                 }.Concat((plan["keychainServices"]?.AsArray() ?? []).Select(s => "credstore.keychain:" + s!.GetValue<string>())))
            lines.Add(Line(id, "blocked"));
        return string.Join('\n', lines);
    }

    /// <summary>The attempts that did not come out as expected, one per line, for an assert message.</summary>
    private static string NotAsExpected(GuardrailReport report) =>
        string.Join('\n', report.Results.Where(r => !r.AsExpected)
            .Select(r => $"{r.Id} ({r.Category}): expected {r.Expected ?? "-"}, verdict {r.Verdict}: {r.Evidence}"));

    [Fact]
    public async Task TheSlirpNameserversAndTheUserBusAreTriedOnLinuxOnly()
    {
        await using var kit = GuardrailKit.Create();
        JsonObject? seen = null;
        var sandbox = new ScriptedSandbox((_, plan) => { seen = plan; return new SandboxOutcome(0, 0, false, Report(plan), "", null); });
        var report = await kit.Check(sandbox).RunAsync("payments", "ada", Ct);

        var raw = (seen!["dnsRaw"]?.AsArray() ?? [])
            .Select(n => (Id: n!["id"]!.GetValue<string>(), Host: n["host"]!.GetValue<string>(), Port: n["port"]!.GetValue<int>(), Transport: n["transport"]!.GetValue<string>()))
            .ToList();
        var unix = Ids(seen, "unix").ToList();
        // The fake answers every attempt the way the plan expects on this OS, so the check passes wherever it runs.
        // Card 209: on Fedora the older fake (a09113a) failed Passed without naming a probe. It answered only the wire
        // plan's lists, and those blocked/allowed without regard to the plan. So every attempt the probe makes on its
        // own (gateway.*, proc.host-*, credstore.dbus-*, display.*, secrets.scan) came back MISSING. The positive
        // controls (cli.read-program, cli.list-install:*) came back BLOCKED, and write.cli-program came back ALLOWED.
        // Only on Linux did that test reach its Passed assert. The ids come first, so the next failure names the probe.
        Assert.Equal(Array.Empty<string>(), report.Results.Where(r => !r.AsExpected).Select(r => $"{r.Id}={r.Verdict}").ToArray());
        Assert.True(report.Passed, "not as expected:\n" + NotAsExpected(report));
        if (!OperatingSystem.IsLinux())
        {
            Assert.Empty(raw);
            Assert.DoesNotContain("dbus.user", unix);
            foreach (var id in NewIds) Assert.DoesNotContain(report.Results, r => r.Id == id);
            return;
        }
        Assert.Equal([("dns.slirp-udp", "10.0.2.3", 53, "udp"), ("dns.slirp-tcp", "10.0.2.3", 53, "tcp"), ("dns.gateway-udp", "10.0.2.2", 53, "udp")], raw);
        var bus = seen["unix"]!.AsArray().Single(n => n!["id"]!.GetValue<string>() == "dbus.user")!["path"]!.GetValue<string>();
        Assert.Equal($"/run/user/{GuardrailCheck.HostUid()}/bus", bus);
        foreach (var id in NewIds)
        {
            var r = report.Results.Single(x => x.Id == id);
            Assert.Equal(Verdicts.Blocked, r.Expected);
            Assert.Equal(Verdicts.Blocked, r.Verdict);
            Assert.True(r.AsExpected);
        }
    }

    [Theory]
    [InlineData("dns.slirp-udp")]
    [InlineData("dns.slirp-tcp")]
    [InlineData("dns.gateway-udp")]
    [InlineData("dbus.user")]
    public async Task AnyOfThemReachableFailsTheCheck(string id)
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "these routes are tried on Linux only");
        await using var kit = GuardrailKit.Create();
        var sandbox = new ScriptedSandbox((_, plan) => new SandboxOutcome(0, 0, false, Report(plan, id), "", null));
        var report = await kit.Check(sandbox).RunAsync("payments", "ada", Ct);

        var r = report.Results.Single(x => x.Id == id);
        Assert.Equal(Verdicts.Allowed, r.Verdict);
        Assert.False(r.AsExpected);
        Assert.False(report.Passed);
    }

    [Fact]
    public async Task AnOlderProbeThatDoesNotTryThemFailsTheCheck()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "these routes are tried on Linux only");
        await using var kit = GuardrailKit.Create();
        var sandbox = new ScriptedSandbox((_, plan) =>
        {
            var old = (JsonObject)plan.DeepClone();
            old.Remove("dnsRaw");
            return new SandboxOutcome(0, 0, false, Report(old), "", null);
        });
        var report = await kit.Check(sandbox).RunAsync("payments", "ada", Ct);

        foreach (var id in new[] { "dns.slirp-udp", "dns.slirp-tcp", "dns.gateway-udp" })
            Assert.Equal(Verdicts.Missing, report.Results.Single(x => x.Id == id).Verdict);
        Assert.False(report.Passed);
    }

    // ------------------------------------------------------------- the real probe, outside any sandbox

    /// <summary>Runs the real yawble-guardcheck with only these attempts; its results by id.</summary>
    private static async Task<Dictionary<string, (string Outcome, string Evidence)>> RunProbe(JsonArray dnsRaw, JsonArray unix)
    {
        var plan = new JsonObject
        {
            ["v"] = 1, ["nonce"] = "n", ["reads"] = new JsonArray(), ["writes"] = new JsonArray(), ["tcp"] = new JsonArray(),
            ["dns"] = new JsonArray(), ["unix"] = unix, ["dnsRaw"] = dnsRaw, ["gateway"] = null, ["hostPid"] = null,
            ["scanDirs"] = new JsonArray(), ["keychainServices"] = new JsonArray(),
        };
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(plan.ToJsonString())).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var psi = Binaries.GuardCheck();
        psi.ArgumentList.Add("--plan");
        psi.ArgumentList.Add(b64);
        using var p = Process.Start(psi)!;
        p.StandardInput.Close();
        var stdout = p.StandardOutput.ReadToEndAsync(Ct);
        var stderr = p.StandardError.ReadToEndAsync(Ct);
        await p.WaitForExitAsync(Ct);
        Assert.True(p.ExitCode == 0, $"exit {p.ExitCode}: {await stderr}");
        return (await stdout).Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => JsonNode.Parse(l)!.AsObject())
            .Where(o => o["id"]!.GetValue<string>().StartsWith("t.", StringComparison.Ordinal))
            .ToDictionary(o => o["id"]!.GetValue<string>(), o => (o["outcome"]!.GetValue<string>(), o["evidence"]!.GetValue<string>()));
    }

    private static JsonObject Raw(string id, int port, string transport) =>
        new() { ["id"] = id, ["category"] = "network", ["host"] = "127.0.0.1", ["port"] = port, ["transport"] = transport, ["name"] = "example.com" };

    /// <summary>A DNS answer to <paramref name="query"/>: its header with QR set and NXDOMAIN, and its question.</summary>
    private static byte[] Answer(ReadOnlySpan<byte> query)
    {
        var a = query.ToArray();
        a[2] |= 0x80;
        a[3] = (byte)((a[3] & 0xF0) | 3);
        return a;
    }

    private static int FreePort(SocketType type, ProtocolType proto)
    {
        using var s = new Socket(AddressFamily.InterNetwork, type, proto);
        s.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)s.LocalEndPoint!).Port;
    }

    [Fact]
    public async Task TheRealProbeSaysAllowedWhenANameserverAnswersAndBlockedWhenNoneDoes()
    {
        UdpClient udp = null!;
        TcpListener tcp = null!;
        try
        {
            udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            tcp = new TcpListener(IPAddress.Loopback, 0);
            tcp.Start();
            // Some agent sandboxes (Windows AppContainer) allow the listener but refuse a loopback connect to it.
            using var check = new TcpClient(AddressFamily.InterNetwork);
            await check.ConnectAsync((IPEndPoint)tcp.LocalEndpoint, Ct);
            using (var accepted = await tcp.AcceptTcpClientAsync(Ct)) { }
        }
        catch (SocketException e)
        {
            udp?.Dispose();
            tcp?.Stop();
            Assert.Skip($"this host refuses a loopback listener or a connect to it ({e.SocketErrorCode})");
            return;
        }
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        using (udp)
        {
            var udpServer = Task.Run(async () =>
            {
                while (!stop.IsCancellationRequested)
                {
                    var got = await udp.ReceiveAsync(stop.Token);
                    await udp.SendAsync(Answer(got.Buffer), got.RemoteEndPoint, stop.Token);
                }
            }, stop.Token);
            var tcpServer = Task.Run(async () =>
            {
                using var c = await tcp.AcceptTcpClientAsync(stop.Token);
                var s = c.GetStream();
                var len = new byte[2];
                await s.ReadExactlyAsync(len, stop.Token);
                var q = new byte[(len[0] << 8) | len[1]];
                await s.ReadExactlyAsync(q, stop.Token);
                var a = Answer(q);
                await s.WriteAsync(new[] { (byte)(a.Length >> 8), (byte)a.Length }, stop.Token);
                await s.WriteAsync(a, stop.Token);
            }, stop.Token);
            var udpPort = ((IPEndPoint)udp.Client.LocalEndPoint!).Port;
            var tcpPort = ((IPEndPoint)tcp.LocalEndpoint).Port;
            try
            {
                var results = await RunProbe(new JsonArray(
                        Raw("t.udp-live", udpPort, "udp"),
                        Raw("t.tcp-live", tcpPort, "tcp"),
                        Raw("t.udp-dead", FreePort(SocketType.Dgram, ProtocolType.Udp), "udp"),
                        Raw("t.tcp-dead", FreePort(SocketType.Stream, ProtocolType.Tcp), "tcp")),
                    new JsonArray());

                Assert.Equal("allowed", results["t.udp-live"].Outcome);
                Assert.Contains("NXDOMAIN", results["t.udp-live"].Evidence);
                Assert.Equal("allowed", results["t.tcp-live"].Outcome);
                Assert.Contains("NXDOMAIN", results["t.tcp-live"].Evidence);
                Assert.Equal("blocked", results["t.udp-dead"].Outcome);
                Assert.Equal("blocked", results["t.tcp-dead"].Outcome);
            }
            finally
            {
                await stop.CancelAsync();
                tcp.Stop();
                try { await Task.WhenAll(udpServer, tcpServer); } catch (Exception e) when (e is OperationCanceledException or SocketException or IOException or ObjectDisposedException) { }
            }
        }
    }

    [Fact]
    public async Task TheRealProbeSaysAllowedForALiveUserBusAndBlockedForAMaskedOne()
    {
        // The plan the core sends names the host's /run/user/<uid>/bus; here a live socket of the test's own stands in.
        var root = Path.Combine(Path.GetTempPath(), "ub" + Guid.NewGuid().ToString("N")[..8]);
        var live = Path.Combine(root, "bus");
        Assert.SkipWhen(Encoding.UTF8.GetByteCount(live) > 100, $"the temp folder is too long for a Unix socket path: {live}");
        Directory.CreateDirectory(Path.Combine(root, "masked"));
        try
        {
            using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try
            {
                listener.Bind(new UnixDomainSocketEndPoint(live));
                listener.Listen(8);
            }
            catch (SocketException e)
            {
                Assert.Skip($"this host refuses a Unix socket here ({e.SocketErrorCode})");
            }
            var results = await RunProbe(new JsonArray(), new JsonArray(
                new JsonObject { ["id"] = "t.dbus-user-live", ["category"] = "network", ["path"] = live },
                new JsonObject { ["id"] = "t.dbus-user-masked", ["category"] = "network", ["path"] = Path.Combine(root, "masked", "bus") }));

            Assert.Equal("allowed", results["t.dbus-user-live"].Outcome);
            Assert.Equal("blocked", results["t.dbus-user-masked"].Outcome);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    [Fact]
    public async Task OnLinuxTheRealProbeTriesTheRealSlirpAddressesAndTheHostsUserBus()
    {
        // Outside any sandbox there is no slirp: 10.0.2.3 and 10.0.2.2 are not on this network, so each attempt must
        // come back (blocked, within the probe's 3 s), never hang. Only checks that the real probe handles the real plan.
        Assert.SkipUnless(OperatingSystem.IsLinux(), "the slirp addresses and /run/user are Linux's");
        var raw = new JsonArray(GuardrailCheck.SlirpDnsProbes
            .Select(p => (JsonNode)new JsonObject { ["id"] = "t." + p.Id, ["category"] = "network", ["host"] = p.Host, ["port"] = 53, ["transport"] = p.Transport, ["name"] = "example.com" })
            .ToArray());
        var unix = new JsonArray(new JsonObject { ["id"] = "t.dbus.user", ["category"] = "network", ["path"] = GuardrailCheck.UserBusPath() });
        var results = await RunProbe(raw, unix);
        foreach (var p in GuardrailCheck.SlirpDnsProbes) Assert.Contains("t." + p.Id, results.Keys);
        Assert.Contains("t.dbus.user", results.Keys);
    }
}
