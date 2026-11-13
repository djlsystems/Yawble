using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Yawble.Core.Guardrail;
using Yawble.Core.Launch;
using Yawble.Core.Sandboxing;
using Yawble.Core.Policy;
using Yawble.Core.Tests.Guardrail;
using Xunit;
using PolicyMember = Yawble.Core.Policy.MemberRef;

namespace Yawble.Core.Tests.Agents;

/// <summary>
/// Run from install under a REAL sandbox with the REAL claude and grok this host has installed (found on PATH, in
/// their own folders by the app's detector, the person's real home): each starts from its install folder and prints its version; it cannot
/// write its install folder (and a self-update attempt changes nothing); it cannot see its config folders or the rest
/// of the home; and the guardrail check passes against the new grants. No credential is used: `--version` and
/// `update` need none, the gateway holds none, and the config files are only tested for existence and opened for
/// zero bytes - never read or printed.
/// Skipped only when a proxy-mode sandbox is refused here or the CLI is not installed.
/// <para>Trait Category=HostCli: the HostCli filter runs these too (on Linux, Windows and macOS). When YAWBLE_HOSTCLI_EXPECT lists the CLI
/// ("claude:native grok:native" ...) it MUST be detected with that install kind (that is asserted even where the sandbox
/// cannot run); the launch itself is then skipped, with the sandbox's own refusal as the reason, only on a machine where the sandbox's
/// proxy-mode sandbox does not run (macOS/Windows here: these probes are Linux shell scripts; Linux without
/// bubblewrap or with the AppArmor user-namespace restriction).</para>
/// </summary>
[Collection(RealSandboxGuardrailCollection.Name)]
[Trait("Category", "HostCli")]
public class RealSandboxRunFromInstallTests(ITestOutputHelper output)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string RealHome => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private static DetectedCliOrSkip Real(string id)
    {
        // The app's own detector (agents.detect), on this host, now.
        var detector = Yawble.Core.Agents.CliDetectorRegistry.ForThisHost();
        var cli = detector.DetectAllAsync(CancellationToken.None).GetAwaiter().GetResult().Single(c => c.CliId == id);
        var expected = RealHostDetectionTests.Expected(Environment.GetEnvironmentVariable("YAWBLE_HOSTCLI_EXPECT"));
        if (expected.TryGetValue(id, out var kind))
        {
            Assert.True(cli.Found, $"YAWBLE_HOSTCLI_EXPECT lists {id}:{kind} but it was not found ({string.Join("; ", cli.Problems)})");
            Assert.Equal(kind, cli.InstallKind);
        }
        else if (!cli.Found) Assert.Skip($"{id} is not installed on this host");
        if (RealSandboxProxyMode.Refusal is { } why)
            Assert.Skip($"{id} ({cli.InstallKind}, {cli.ExecutablePath}) detected, but the sandbox cannot run here: {why}");
        return new DetectedCliOrSkip(cli, detector);
    }

    private sealed record DetectedCliOrSkip(Yawble.Core.Agents.DetectedCli Cli, Yawble.Core.Agents.ICliDetector Detector);

    /// <summary>Runs a shell script in the member's real launch (layout, policy, request, env) instead of the CLI.</summary>
    private sealed class ScriptProbe(Yawble.Core.Agents.DetectedCli cli, Func<RunLayout, string> script) : ILaunchProbe
    {
        public IReadOnlyList<PolicyMember> ExtraMembers => [];

        public LaunchMember? Synthetic { get; } = new(0, "_real-cli", "probe", cli.CliId == "grok" ? AgentCli.Grok : AgentCli.Claude);

        public TimeSpan MaxWall => TimeSpan.FromMinutes(3);

        public RunLayout? Layout { get; private set; }

        public JsonObject? Request { get; private set; }

        public JsonObject Rewrite(LaunchProbeContext context)
        {
            Layout = context.Layout;
            var request = (JsonObject)context.Request.DeepClone();
            request["process"]!["commandLine"] = CommandLine.Join(context.Layout.Os, ["/bin/sh", "-c", script(context.Layout)]);
            Request = request;
            return request;
        }
    }

    private static string Q(string s) => "'" + s.Replace("'", "'\\''") + "'";

    private static string Sha(string file)
    {
        using var f = File.OpenRead(file);
        return Convert.ToHexString(SHA256.HashData(f));
    }

    private static Dictionary<string, string> Lines(string stdout) => stdout.Split('\n')
        .Where(l => l.StartsWith("@", StringComparison.Ordinal) && l.Contains('='))
        .Select(l => l[1..]).ToDictionary(l => l[..l.IndexOf('=')], l => l[(l.IndexOf('=') + 1)..]);

    [Theory]
    [InlineData("claude")]
    [InlineData("grok")]
    public async Task TheInstalledCliStartsFromItsFolderAndCannotChangeIt(string id)
    {
        var (cli, detector) = Real(id);
        await using var kit = GuardrailKit.Create(realHome: RealHome, clis: detector, realGateway: true);
        var exe = cli.ExecutablePath!;
        var folder = cli.ReadOnlyFolders[0];
        var shaBefore = Sha(exe);
        var mtimeBefore = File.GetLastWriteTimeUtc(exe);
        var entriesBefore = Directory.EnumerateFileSystemEntries(folder).Order(StringComparer.Ordinal).ToList();
        var marker = ".yawble-test-" + Guid.NewGuid().ToString("N")[..8];
        const string update = "update";

        var probe = new ScriptProbe(cli, l =>
        {
            var cmd = string.Join(' ', l.CliCommand.Select(Q));
            var f = Q(folder);
            var lines = new List<string>
            {
                "cd \"$HOME\"",
                $"{cmd} --version > \"$TMPDIR/v.out\" 2>&1; echo \"@version_exit=$?\"; echo \"@version=$(head -c 200 \"$TMPDIR/v.out\" | tr '\\n' ' ')\"",
                // Write attempts on the install: all create-only or zero-byte, so even a leak destroys nothing.
                $"touch {f}/{marker} 2>\"$TMPDIR/t.err\"; echo \"@touch=$?\"; echo \"@touch_error=$(head -c 200 \"$TMPDIR/t.err\")\"",
                $"mkdir {f}/{marker}.d 2>/dev/null; echo \"@mkdir=$?\"",
                $"ln -s x {f}/{marker}.l 2>/dev/null; echo \"@symlink=$?\"",
                $"cp {Q(exe)} {f}/{marker}.new 2>/dev/null; echo \"@copy=$?\"",
                // Zero-byte appends (in a subshell: a failed redirection ends sh). On the running program Linux may answer
                // ETXTBSY before EROFS, so also on a file of the install that is not executing.
                $"( : >> {Q(exe)} ) 2>\"$TMPDIR/a.err\"; echo \"@append=$?\"; echo \"@append_error=$(head -c 200 \"$TMPDIR/a.err\")\"",
                $"other=$(find {f} -maxdepth 2 -type f ! -path {Q(exe)} | head -1); echo \"@other_file=$other\"; ( : >> \"$other\" ) 2>\"$TMPDIR/o.err\"; echo \"@append_other=$?\"; echo \"@append_other_error=$(head -c 200 \"$TMPDIR/o.err\")\"",
                // A self-update attempt, with the app's "updates off" variables REMOVED: it must fail without a change.
                $"env -u DISABLE_UPDATES -u DISABLE_AUTOUPDATER -u GROK_DISABLE_AUTOUPDATER -u GROK_AUTO_UPDATE timeout 90 {cmd} {update} > \"$TMPDIR/u.out\" 2>&1; echo \"@update_exit=$?\"; echo \"@update=$(tail -c 300 \"$TMPDIR/u.out\" | tr '\\n' ' ')\"",
                // ...and with them as the app sets them.
                $"timeout 60 {cmd} {update} > \"$TMPDIR/u2.out\" 2>&1; echo \"@update_off_exit=$?\"; echo \"@update_off=$(tail -c 200 \"$TMPDIR/u2.out\" | tr '\\n' ' ')\"",
            };
            for (var i = 0; i < cli.ConfigFolders.Count; i++)
            {
                var c = Q(cli.ConfigFolders[i]);
                lines.Add($"[ -e {c} ]; echo \"@config{i}_exists=$?\"");
                lines.Add($"ls -A {c} 2>/dev/null | head -50 | tr '\\n' ' ' | sed 's/^/@config{i}_list=/'; echo");
            }
            // Credential files: existence and a zero-byte open only (never read, never printed).
            foreach (var (k, p) in new[] { ("claude_creds", Path.Combine(RealHome, ".claude", ".credentials.json")), ("grok_auth", Path.Combine(RealHome, ".grok", "auth.json")), ("claude_json", Path.Combine(RealHome, ".claude.json")) })
                lines.Add($"head -c 0 {Q(p)} 2>/dev/null; echo \"@open_{k}=$?\"");
            lines.Add($"ls -A {Q(RealHome)} 2>/dev/null | tr '\\n' ' ' | sed 's/^/@home_list=/'; echo");
            lines.Add($"ls -A {Q(Path.GetDirectoryName(folder)!)} 2>/dev/null | tr '\\n' ' ' | sed 's/^/@parent_list=/'; echo");
            return string.Join("\n", lines);
        });
        var launcher = kit.Launcher(new NativeSandboxBackend());
        var outcome = await launcher.RunAsync(new LaunchRequest("_real-cli", "probe", RunKind.Headless, "probe", null, Probe: probe), Ct);
        var stdout = outcome.ProbeOutput?.Stdout ?? "";
        output.WriteLine($"{id}: end {outcome.End} {outcome.Reason}\n{stdout}\nstderr: {outcome.ProbeOutput?.Stderr}");
        Launch.RealSandbox.Report($"real {id} ({cli.Version}, {cli.InstallKind}, {exe}) in the sandbox: end {outcome.End}\n{stdout}");
        var o = Lines(stdout);

        // It started from the install folder (the request ran exactly the detected program) and printed its version.
        var ro = probe.Request!["filesystem"]!["readonlyPaths"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();
        Assert.Contains(HostPathCanonicalizer.Canonicalize(folder), ro);
        Assert.Equal(HostPathCanonicalizer.Canonicalize(exe), probe.Layout!.CliPath);
        Assert.Equal("0", o["version_exit"]);
        Assert.Contains(cli.Version!, o["version"]);

        // Nothing could be written into the install folder; the program is byte-for-byte unchanged.
        foreach (var k in new[] { "touch", "mkdir", "symlink", "copy", "append", "append_other" }) Assert.NotEqual("0", o[k]);
        // The folder is a read-only bind, not just a permission or a busy file: the kernel says so.
        Assert.Contains("Read-only file system", o["touch_error"]);
        Assert.Contains("Read-only file system", o["append_other_error"]);
        Assert.Equal(shaBefore, Sha(exe));
        Assert.Equal(mtimeBefore, File.GetLastWriteTimeUtc(exe));
        Assert.Equal(entriesBefore, Directory.EnumerateFileSystemEntries(folder).Order(StringComparer.Ordinal).ToList());

        // Its config folders and credentials are not reachable; the home shows only the sandbox's mount points.
        foreach (var k in new[] { "open_claude_creds", "open_grok_auth", "open_claude_json" }) Assert.NotEqual("0", o[k]);
        for (var i = 0; i < cli.ConfigFolders.Count; i++)
        {
            var listed = o.GetValueOrDefault($"config{i}_list", "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
            // Grok's install is inside ~/.grok: only "downloads" (on the way to the grant) and the empty masks may show.
            foreach (var name in listed)
            {
                var hostPath = Path.Combine(cli.ConfigFolders[i], name);
                var onTheWay = HostPathCanonicalizer.Canonicalize(folder).StartsWith(HostPathCanonicalizer.Canonicalize(hostPath) + "/", StringComparison.Ordinal)
                               || HostPathCanonicalizer.Canonicalize(folder) == HostPathCanonicalizer.Canonicalize(hostPath);
                var masked = probe.Request["filesystem"]!["deniedPaths"]!.AsArray().Any(d => d!.GetValue<string>() == HostPathCanonicalizer.Canonicalize(hostPath));
                Assert.True(onTheWay || masked, $"{cli.ConfigFolders[i]}/{name} is visible in the sandbox and is neither the way to the install nor a mask");
            }
        }
        var home = o.GetValueOrDefault("home_list", "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var denied = probe.Request["filesystem"]!["deniedPaths"]!.AsArray().Select(d => d!.GetValue<string>()).ToList();
        var granted = ro.Concat(probe.Request["filesystem"]!["readwritePaths"]!.AsArray().Select(d => d!.GetValue<string>())).ToList();
        foreach (var name in home)
        {
            var p = HostPathCanonicalizer.Canonicalize(Path.Combine(RealHome, name));
            Assert.True(denied.Concat(granted).Any(g => g == p || g.StartsWith(p + "/", StringComparison.Ordinal)),
                $"~/{name} is visible in the sandbox and is not a mount point the sandbox made for a grant or a denial");
        }
        output.WriteLine($"update attempt (updates on): exit {o["update_exit"]}: {o["update"]}");
        output.WriteLine($"update attempt (as the app runs it): exit {o["update_off_exit"]}: {o["update_off"]}");
    }

    /// <summary>The guardrail check against the real installs and the real home: install readable and not writable,
    /// config folders and the rest of the home blocked, plus the whole S5 battery.</summary>
    [Theory]
    [InlineData("claude")]
    [InlineData("grok")]
    public async Task TheGuardrailCheckPassesAgainstTheRealInstall(string id)
    {
        var (cli, detector) = Real(id);
        await using var kit = GuardrailKit.Create(realHome: RealHome, clis: detector, realGateway: true);
        var report = await kit.Check(new NativeSandboxBackend()).RunAsync(null, null, Ct, syntheticCli: id == "grok" ? AgentCli.Grok : AgentCli.Claude);
        RealSandboxProxyMode.Report(report);
        output.WriteLine(report.ToText());
        Assert.True(report.Sandbox.Refusal is null, $"the sandbox refused: {report.Sandbox.Refusal}");
        var bad = report.Results.Where(r => !r.AsExpected).Select(r => $"{r.Id}={r.Verdict}: {r.Evidence}").ToList();
        Assert.True(bad.Count == 0, "unexpected: " + string.Join(" | ", bad));
        Assert.True(report.Passed);
        // A sibling canary only when the install folder's parent is inside the home (grok: ~/.grok).
        var parent = Path.GetDirectoryName(cli.ReadOnlyFolders[0])!;
        RealSandboxGuardrailTests.AssertCliInstallHolds(report, expectSibling: parent.StartsWith(RealHome + "/", StringComparison.Ordinal));
        // The canaries the check planted in the real home are gone again.
        foreach (var dir in cli.ConfigFolders.Where(Directory.Exists).Append(parent))
            Assert.Empty(Directory.EnumerateFiles(dir, ".yawble-guardcheck-*"));
    }
}
