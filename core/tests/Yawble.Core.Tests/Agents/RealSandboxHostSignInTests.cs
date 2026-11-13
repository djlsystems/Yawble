using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Yawble.Core.Agents;
using Yawble.Core.Guardrail;
using Yawble.Core.Launch;
using Yawble.Core.Sandboxing;
using Yawble.Core.Policy;
using Yawble.Core.Tests.Guardrail;
using Xunit;
using PolicyMember = Yawble.Core.Policy.MemberRef;

namespace Yawble.Core.Tests.Agents;

/// <summary>
/// Host sign-in under a REAL sandbox with the REAL claude and grok of this host and the person's REAL home
/// (HostSignInModes, as the core wires it): the CLI's own sign-in file is visible at its host path, readable (opened for
/// zero bytes, never read) and NOT writable; a dummy file the test plants beside it in the same config folder, and the
/// rest of the home, are not reachable; the host's sign-in file is unchanged afterwards (size and time, never content).
/// The guardrail check under host sign-in passes against the real home. No value of a sign-in is read, copied or printed.
/// <para>The real prompt (a model call on the person's account) runs only with YAWBLE_REAL_SIGNIN_PROMPT=1.</para>
/// <para>Skipped when the CLI is not installed or not signed in on this host, or a proxy-mode sandbox cannot run here.</para>
/// </summary>
[Collection(RealSandboxGuardrailCollection.Name)]
[Trait("Category", "HostCli")]
public partial class RealSandboxHostSignInTests(ITestOutputHelper output)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string RealHome => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private static (DetectedCli Cli, ICliDetector Detector, string SignIn) SignedIn(string id)
    {
        var detector = CliDetectorRegistry.ForThisHost();
        var cli = detector.DetectAllAsync(CancellationToken.None).GetAwaiter().GetResult().Single(c => c.CliId == id);
        if (!cli.Found) Assert.Skip($"{id} is not installed on this host");
        if (cli.SignIn is not { Status: SignInStatus.SignedIn })
            Assert.Skip($"{id} is not signed in on this host ({cli.SignIn?.Status}): {cli.SignIn?.Detail}");
        if (Launch.RealSandboxHost.Refusal is { } why) Assert.Skip($"{id} is installed and signed in, but the sandbox cannot run here: {why}");
        return (cli, detector, cli.SignIn.Files.Single().Path);
    }

    /// <summary>Runs a command line (a script in this OS's shell, <see cref="Launch.RealSandboxHost.Script"/>) in the member's real launch (layout, policy, request, env) instead of the CLI.</summary>
    private sealed class ScriptProbe(string cliId, Func<RunLayout, string> commandLine, TimeSpan? wall = null) : ILaunchProbe
    {
        public IReadOnlyList<PolicyMember> ExtraMembers => [];

        public LaunchMember? Synthetic { get; } = new(0, "_real-signin", "probe", cliId == "grok" ? AgentCli.Grok : AgentCli.Claude);

        public TimeSpan MaxWall => wall ?? TimeSpan.FromMinutes(3);

        public JsonObject? Request { get; private set; }

        public JsonObject Rewrite(LaunchProbeContext context)
        {
            var request = (JsonObject)context.Request.DeepClone();
            request["process"]!["commandLine"] = commandLine(context.Layout);
            Request = request;
            return request;
        }
    }

    private static string Q(string s) => "'" + s.Replace("'", "'\\''") + "'";

    private static Dictionary<string, string> Lines(string stdout) => stdout.Split('\n').Select(l => l.TrimEnd('\r'))
        .Where(l => l.StartsWith("@", StringComparison.Ordinal) && l.Contains('='))
        .Select(l => l[1..]).GroupBy(l => l[..l.IndexOf('=')]).ToDictionary(g => g.Key, g => g.Last()[(g.Last().IndexOf('=') + 1)..]);

    /// <summary>Anything that could be a token is cut before it reaches the test log or the report.</summary>
    public static string Redact(string s) => TokenLike().Replace(s, m => m.Value[..Math.Min(6, m.Value.Length)] + "<redacted>");

    [GeneratedRegex(@"(sk-ant-[A-Za-z0-9_\-]+|xai-[A-Za-z0-9_\-]{8,}|eyJ[A-Za-z0-9_\-\.]{20,}|[A-Za-z0-9_\-]{40,})")]
    private static partial Regex TokenLike();

    [Theory]
    [InlineData("claude")]
    [InlineData("grok")]
    public async Task TheSignInFileIsReadableReadOnlyAndNothingElseOfItsFolderIs(string id)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "a POSIX-shell probe with Linux error texts; on Windows TheGuardrailCheckPassesWithTheHostSignInGranted covers the read-only sign-in");
        var (cli, detector, signIn) = SignedIn(id);
        var folder = Path.GetDirectoryName(signIn)!;
        var before = (new FileInfo(signIn).Length, File.GetLastWriteTimeUtc(signIn));
        // A dummy beside the real sign-in, with fake content (never a real one), removed afterwards.
        var dummy = Path.Combine(folder, ".yawble-test-dummy-" + Guid.NewGuid().ToString("N")[..8] + ".json");
        File.WriteAllText(dummy, "{\"FAKE\":\"dummy planted by the test\"}");
        var marker = ".yawble-test-w-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await using var kit = GuardrailKit.Create(realHome: RealHome, clis: detector, realGateway: true);
            var variable = id == "claude" ? SignInCatalog.ClaudeStoreVariable : SignInCatalog.GrokAuthVariable;
            var probe = new ScriptProbe(id, l => CommandLine.Join(l.Os, ["/bin/sh", "-c", string.Join("\n",
            [
                $"echo \"@pointer=${variable}\"",
                // Readable: opened, zero bytes read.
                $"head -c 0 {Q(signIn)}; echo \"@open_signin=$?\"",
                // Not writable: a zero-byte append (nothing written even if it worked), in a subshell.
                $"( : >> {Q(signIn)} ) 2>\"$TMPDIR/a.err\"; echo \"@append_signin=$?\"; echo \"@append_signin_error=$(head -c 200 \"$TMPDIR/a.err\")\"",
                $"touch {Q(Path.Combine(folder, marker))} 2>/dev/null; echo \"@create_beside=$?\"",
                $"mv {Q(signIn)} {Q(signIn + ".moved")} 2>/dev/null; echo \"@rename_signin=$?\"",
                // The dummy beside it: not visible, not readable.
                $"head -c 0 {Q(dummy)} 2>/dev/null; echo \"@open_dummy=$?\"",
                $"echo \"@dummy_content=$(cat {Q(dummy)} 2>/dev/null | head -c 40)\"",
                $"ls -A {Q(folder)} 2>/dev/null | tr '\\n' ' ' | sed 's/^/@folder_list=/'; echo",
                $"head -c 0 {Q(Path.Combine(RealHome, ".claude.json"))} 2>/dev/null; echo \"@open_claude_json=$?\"",
                $"head -c 0 {Q(Path.Combine(folder, id == "grok" ? "auth.json.lock" : "settings.json"))} 2>/dev/null; echo \"@open_sibling=$?\"",
            ])]));
            var outcome = await kit.Launcher(new NativeSandboxBackend(), new HostSignInModes())
                .RunAsync(new LaunchRequest("_real-signin", "probe", RunKind.Headless, "probe", null, Probe: probe), Ct);
            var stdout = outcome.ProbeOutput?.Stdout ?? "";
            output.WriteLine(Redact($"{id}: end {outcome.End} {outcome.Reason}\n{stdout}\nstderr: {outcome.ProbeOutput?.Stderr}"));
            Launch.RealSandbox.Report(Redact($"host sign-in: {id} ({cli.Version}) in the sandbox: end {outcome.End}\n{stdout}"));
            var o = Lines(stdout);

            var fs = probe.Request!["filesystem"]!;
            List<string> L(string k) => fs[k]!.AsArray().Select(n => n!.GetValue<string>()).ToList();
            Assert.Contains(HostPathCanonicalizer.Canonicalize(signIn), L("readonlyPaths"));
            Assert.DoesNotContain(L("readwritePaths"), g => g.StartsWith(HostPathCanonicalizer.Canonicalize(folder), StringComparison.Ordinal));
            Assert.Equal(id == "claude" ? folder : signIn, o["pointer"]);

            Assert.Equal("0", o["open_signin"]);
            Assert.NotEqual("0", o["append_signin"]);
            Assert.Contains("Read-only file system", o["append_signin_error"]);
            // A create beside it may succeed INSIDE the sandbox only: the sandbox makes the folders on the way to a grant
            // as writable tmpfs mount points. It never reaches the host (asserted below).
            Assert.NotEqual("0", o["rename_signin"]);
            Assert.NotEqual("0", o["open_dummy"]);
            Assert.Equal("", o["dummy_content"]);
            Assert.NotEqual("0", o["open_claude_json"]);
            Assert.NotEqual("0", o["open_sibling"]);
            // Every name the sandbox sees in the folder is the sign-in itself, an empty mask the sandbox made for a denied entry
            // (names are visible, contents are not), or the sandbox's own file that never reached the host.
            var denied = L("deniedPaths");
            foreach (var name in o.GetValueOrDefault("folder_list", "").Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var host = HostPathCanonicalizer.Canonicalize(Path.Combine(folder, name));
                var install = cli.ReadOnlyFolders.Any(f => HostPathCanonicalizer.Canonicalize(f) == host); // grok: ~/.grok/downloads
                Assert.True(host == HostPathCanonicalizer.Canonicalize(signIn) || denied.Contains(host) || install || name == marker,
                    $"{folder}/{name} is visible in the sandbox and is neither the sign-in, the install, nor a mask");
            }
            // The host's sign-in is unchanged and nothing appeared beside it.
            Assert.Equal(before, (new FileInfo(signIn).Length, File.GetLastWriteTimeUtc(signIn)));
            Assert.False(File.Exists(Path.Combine(folder, marker)));
            Assert.False(File.Exists(signIn + ".moved"));
        }
        finally
        {
            File.Delete(dummy);
        }
    }

    /// <summary>
    /// The guardrail check as the core runs it against the real home and the real sign-in: neither CLI's sign-in file is
    /// granted at all (the gateway holds both); the launcher still checks it is there.
    /// </summary>
    [Theory]
    [InlineData("claude")]
    [InlineData("grok")]
    public async Task TheGuardrailCheckPassesWithTheHostSignInGranted(string id)
    {
        var (_, detector, signIn) = SignedIn(id);
        var before = (new FileInfo(signIn).Length, File.GetLastWriteTimeUtc(signIn));
        await using var kit = GuardrailKit.Create(realHome: RealHome, clis: detector, realGateway: true);
        var report = await kit.Check(Launch.RealSandboxHost.Backend(), new HostSignInModes()).RunAsync(null, null, Ct,
            syntheticCli: id == "grok" ? AgentCli.Grok : AgentCli.Claude);
        RealSandboxProxyMode.Report(report);
        output.WriteLine(report.ToText());
        // Known upstream gaps the check rightly still reports, by name: on Windows, DNS still resolves in proxy mode
        // (microsoft/mxc#1666; nothing can connect, egress is blocked). Not an accepted residual; listed here so any OTHER
        // unexpected result fails this test.
        string[] knownUpstream = OperatingSystem.IsWindows() ? ["net.dns"] : [];
        var bad = report.Results.Where(r => !r.AsExpected && !knownUpstream.Contains(r.Id)).Select(r => $"{r.Id}={r.Verdict}: {r.Evidence}").ToList();
        Assert.True(bad.Count == 0, "unexpected: " + string.Join(" | ", bad));
        Assert.Equal(knownUpstream.Length == 0, report.Passed);
        Assert.Equal(Launch.RealSandboxHost.BackendName, report.Run!.Backend);
        Assert.True(report.Run!.CredentialConfigured);
        string V(string rid) => report.Results.Single(r => r.Id == rid).Verdict;
        // Both sign-ins are held by the gateway: the sandbox is granted no sign-in file at all.
        Assert.DoesNotContain(report.Results, r => r.Id.StartsWith("signin.", StringComparison.Ordinal));
        Assert.Equal(Verdicts.Blocked, V("home.claude-credentials"));
        Assert.Equal(Verdicts.Blocked, V("home.grok-auth"));
        Assert.Equal(Verdicts.Blocked, V("home.claude-json"));
        Assert.All(report.Results.Where(r => r.Id.StartsWith("cli.config-canary", StringComparison.Ordinal)), r => Assert.Equal(Verdicts.Blocked, r.Verdict));
        Assert.Equal(before, (new FileInfo(signIn).Length, File.GetLastWriteTimeUtc(signIn)));
        output.WriteLine($"counts: blocked {report.Counts.Blocked}, allowed {report.Counts.Allowed}, unexpected {report.Counts.Unexpected}");
    }

    [GeneratedRegex(@"\x1b\[[0-9;?]*[ -/]*[@-~]|\x1b\][^\x07]*\x07|\x1b[@-_]")]
    private static partial Regex Ansi();

    /// <summary>
    /// The terminal window's interactive session with the real CLI: its full-screen TUI starts on the
    /// run's PTY (Windows: the core's ConPTY around wxc-exec) on the host's own sign-in, and shows no sign-in or
    /// onboarding screen. Nothing is typed, so no model call is made; the person's Stop ends it.
    /// </summary>
    [Theory]
    [InlineData("claude", "Claude Code")]
    [InlineData("grok", "grok")]
    public async Task TheCliTuiOpensOnThePtyWithTheHostSignIn(string id, string shows)
    {
        var (cli, detector, signIn) = SignedIn(id);
        var before = (new FileInfo(signIn).Length, File.GetLastWriteTimeUtc(signIn));
        await using var kit = GuardrailKit.Create(realHome: RealHome, clis: detector, realGateway: true);
        var registry = new RunRegistry();
        LiveRun? live = null;
        registry.Added += r => live = r;
        var subject = new LaunchMember(0, "_real-signin", id, id == "grok" ? AgentCli.Grok : AgentCli.Claude);
        var run = kit.Launcher(Launch.RealSandboxHost.Backend(), new HostSignInModes(), registry)
            .RunAsync(new LaunchRequest("_real-signin", id, RunKind.Interactive, null, null, new PtySize(40, 120), Subject: subject), Ct);

        string Screen() => Ansi().Replace(System.Text.Encoding.UTF8.GetString(live?.Output.Snapshot() ?? []), " ");
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline && !run.IsCompleted && !Screen().Contains(shows, StringComparison.OrdinalIgnoreCase))
            await Task.Delay(200, Ct);
        await Task.Delay(2000, Ct); // let the first screen finish drawing
        var screen = Screen();
        live?.Stop();
        var outcome = await run.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        output.WriteLine($"{id} ({cli.Version}): end {outcome.End} {outcome.Reason}\n{Redact(screen)}");
        Launch.RealSandbox.Report(Redact($"{id} ({cli.Version}) TUI on the PTY: end {outcome.End}\n{screen}"));
        Assert.Equal(RunEnd.Stopped, outcome.End); // it was still running when the person's Stop ended it
        Assert.Contains(shows, screen, StringComparison.OrdinalIgnoreCase);
        foreach (var screenOfSignIn in new[] { "Select login method", "Sign in with", "/login", "Log in to", "Paste code" })
            Assert.DoesNotContain(screenOfSignIn, screen, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, (new FileInfo(signIn).Length, File.GetLastWriteTimeUtc(signIn)));
    }

    /// <summary>
    /// The real CLI answers a trivial prompt in the sandbox with the host's sign-in, through the run's gateway (the gateway
    /// puts the host sign-in on the request; the sandbox never has it). A real model call on the person's account: only with YAWBLE_REAL_SIGNIN_PROMPT=1.
    /// </summary>
    [Theory]
    [InlineData("claude")]
    [InlineData("grok")]
    public async Task TheCliAnswersAPromptWithTheHostSignIn(string id)
    {
        if (Environment.GetEnvironmentVariable("YAWBLE_REAL_SIGNIN_PROMPT") != "1")
            Assert.Skip("a real model call on the person's account: set YAWBLE_REAL_SIGNIN_PROMPT=1 to run it");
        var (cli, detector, signIn) = SignedIn(id);
        var before = (new FileInfo(signIn).Length, File.GetLastWriteTimeUtc(signIn));
        await using var kit = GuardrailKit.Create(realHome: RealHome, clis: detector, realGateway: true);
        const string prompt = "Reply with exactly the single word: pong";
        var probe = new ScriptProbe(id, l =>
        {
            string[] args = id == "claude"
                ? ["-p", prompt, "--output-format", "text"]
                : ["--no-auto-update", "-p", prompt, "--output-format", "json", "--always-approve"];
            var posix = string.Join("\n",
            [
                "cd \"$HOME\"",
                $"timeout 150 {string.Join(' ', l.CliCommand.Concat(args).Select(Q))} > \"$TMPDIR/p.out\" 2>\"$TMPDIR/p.err\"; echo \"@prompt_exit=$?\"",
                "echo \"@answer=$(head -c 600 \"$TMPDIR/p.out\" | tr '\\n' ' ')\"",
                "echo \"@stderr=$(tail -c 600 \"$TMPDIR/p.err\" | tr '\\n' ' ')\"",
            ]);
            // Windows PowerShell 5.1: the probe's MaxWall ends a hung CLI (no timeout command there).
            static string P(string s) => "'" + s.Replace("'", "''") + "'";
            var powershell = string.Join("\n",
            [
                "Set-Location $env:HOME",
                $"$out = & {string.Join(' ', l.CliCommand.Concat(args).Select(P))} 2> (Join-Path $env:TEMP 'p.err')",
                "\"@prompt_exit=$LASTEXITCODE\"",
                "$a = ($out -join ' '); \"@answer=\" + $a.Substring(0, [Math]::Min(600, $a.Length))",
                "$e = (Get-Content (Join-Path $env:TEMP 'p.err') -Raw); if ($e) { \"@stderr=\" + ($e -replace \"`r?`n\", ' ').Substring(0, [Math]::Min(600, $e.Length)) } else { '@stderr=' }",
            ]);
            return Launch.RealSandboxHost.Script(l.Os, posix, powershell);
        }, TimeSpan.FromMinutes(4));
        var outcome = await kit.Launcher(Launch.RealSandboxHost.Backend(), new HostSignInModes())
            .RunAsync(new LaunchRequest("_real-signin", "probe", RunKind.Headless, "probe", null, Probe: probe), Ct);
        var stdout = Redact(outcome.ProbeOutput?.Stdout ?? "");
        var events = kit.Gateways.Log.Read(outcome.ProbeOutput?.RunNumber ?? -1);
        var log = string.Join("\n", events.Select(e => Redact($"  {e.Kind} {e.Vhost} {e.Method} {e.Target} -> {e.Status} {e.Detail}")));
        output.WriteLine($"{id} ({cli.Version}): end {outcome.End} {outcome.Reason}\n{stdout}\ngateway:\n{log}");
        Launch.RealSandbox.Report($"host sign-in: {id} ({cli.Version}) answered a prompt with the host sign-in: end {outcome.End}\n{stdout}\ngateway:\n{log}");
        var o = Lines(stdout);
        Assert.Equal(before, (new FileInfo(signIn).Length, File.GetLastWriteTimeUtc(signIn)));
        // The gateway swapped the run token (Claude) or yawble-grok-auth's placeholder (Grok) for the host sign-in.
        var how = id == "claude" ? "credential swapped: bearer (host Claude sign-in)" : "credential swapped: bearer (grok session)";
        Assert.Contains(events, e => e.Kind == Yawble.Core.Gateway.GatewayEventKinds.Forward && e.Status is >= 200 and < 300
                                     && (e.Detail ?? "").Contains(how));
        Assert.Equal("0", o["prompt_exit"]);
        Assert.Contains("pong", o["answer"], StringComparison.OrdinalIgnoreCase);
    }
}
