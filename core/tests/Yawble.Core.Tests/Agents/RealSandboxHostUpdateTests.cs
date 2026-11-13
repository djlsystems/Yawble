using System.Text.Json.Nodes;
using Yawble.Core.Launch;
using Yawble.Core.Sandboxing;
using Yawble.Core.Policy;
using Yawble.Core.Tests.Guardrail;
using Xunit;
using PolicyMember = Yawble.Core.Policy.MemberRef;

namespace Yawble.Core.Tests.Agents;

/// <summary>
/// Under a REAL sandbox (Linux, bubblewrap): what a running sandboxed CLI sees
/// when the person's CLI is updated on the host meanwhile, for the three ways the vendors update. The "CLI" is a copy
/// of /bin/sleep in a scratch install shaped like each vendor's layout (no real CLI is touched):
/// <list type="bullet">
/// <item>native Claude: a new file in versions/, the launcher symlink swapped, the old version deleted;</item>
/// <item>npm -g: the package folder (the grant itself) moved aside and replaced by a new one, then the old removed;</item>
/// <item>in-place overwrite of the running program (install.cjs's copy fallback): refused by Linux (ETXTBSY).</item>
/// </list>
/// Each case records what the sandbox saw; the assertions pin the behaviour the report describes.
/// </summary>
[Collection(RealSandboxGuardrailCollection.Name)]
public class RealSandboxHostUpdateTests(ITestOutputHelper output)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Probe(string script) : ILaunchProbe
    {
        public IReadOnlyList<PolicyMember> ExtraMembers => [];
        public LaunchMember? Synthetic { get; } = new(0, "_update", "probe", AgentCli.Claude);
        public TimeSpan MaxWall => TimeSpan.FromMinutes(3);
        public RunLayout? Layout { get; private set; }

        public JsonObject Rewrite(LaunchProbeContext context)
        {
            Layout = context.Layout;
            var request = (JsonObject)context.Request.DeepClone();
            request["process"]!["commandLine"] = CommandLine.Join(context.Layout.Os, ["/bin/sh", "-c", script]);
            return request;
        }
    }

    private static string Q(string s) => "'" + s.Replace("'", "'\\''") + "'";

    private static Dictionary<string, string> Lines(string stdout) => stdout.Split('\n')
        .Where(l => l.StartsWith("@", StringComparison.Ordinal) && l.Contains('='))
        .Select(l => l[1..]).GroupBy(l => l[..l.IndexOf('=')]).ToDictionary(g => g.Key, g => g.Last()[(g.Last().IndexOf('=') + 1)..]);

    /// <summary>
    /// Starts the "old version" in the sandbox, waits for the host to update, then reports what it sees. The host side
    /// is <paramref name="update"/>, run once the sandbox says it is running the old program.
    /// </summary>
    private async Task<(Dictionary<string, string> Seen, RunLayout Layout)> RunAsync(string exe, string folder, string newExe, Action update)
    {
        RealSandboxProxyMode.SkipUnlessAvailable();
        var root = HostPathCanonicalizer.Canonicalize(Directory.CreateTempSubdirectory("yawble-upd-").FullName);
        var clis = new FakeCliDetector(TestClis.Found("claude", exe, [folder], []));
        await using var kit = GuardrailKit.Create(clis: clis, realGateway: true);
        var script = string.Join("\n",
            "cd \"$HOME\"",
            $"{Q(exe)} 120 & pid=$!",
            "echo started > \"$HOME/started\"",
            "i=0; while [ ! -e \"$HOME/updated\" ] && [ $i -lt 600 ]; do sleep 0.1; i=$((i+1)); done",
            "kill -0 $pid 2>/dev/null; echo \"@old_still_running=$?\"",
            $"echo \"@folder_now=$(ls -A {Q(folder)} 2>&1 | tr '\\n' ' ')\"",
            $"[ -e {Q(exe)} ]; echo \"@old_path_exists=$?\"",
            $"{Q(exe)} 0 2>\"$TMPDIR/e1\"; echo \"@exec_old_path=$?\"; echo \"@exec_old_path_error=$(head -c 200 \"$TMPDIR/e1\")\"",
            $"{Q(newExe)} 0 2>\"$TMPDIR/e2\"; echo \"@exec_new_path=$?\"; echo \"@exec_new_path_error=$(head -c 200 \"$TMPDIR/e2\")\"",
            "ls -l /proc/$pid/exe 2>&1 | sed 's/.*-> //' | sed 's/^/@old_proc_exe=/'",
            "kill $pid 2>/dev/null; wait $pid 2>/dev/null; echo \"@done=1\"");
        var probe = new Probe(script);
        var launcher = kit.Launcher(new NativeSandboxBackend());
        var run = launcher.RunAsync(new LaunchRequest("_update", "probe", RunKind.Headless, "probe", null, Probe: probe), Ct);
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while ((probe.Layout is null || !File.Exists(Path.Combine(probe.Layout.Workspace, "started"))) && !run.IsCompleted && DateTime.UtcNow < deadline)
            await Task.Delay(100, Ct);
        Assert.True(probe.Layout is not null && File.Exists(Path.Combine(probe.Layout.Workspace, "started")), "the sandbox did not start the old version");
        update();
        await File.WriteAllTextAsync(Path.Combine(probe.Layout!.Workspace, "updated"), "1", Ct);
        var outcome = await run;
        var stdout = outcome.ProbeOutput?.Stdout ?? "";
        output.WriteLine($"outcome: end={outcome.End} reason={outcome.Reason} exit={outcome.Exit}");
        output.WriteLine(stdout + "\nstderr: " + outcome.ProbeOutput?.Stderr);
        Launch.RealSandbox.Report($"host update ({Path.GetFileName(folder)}): {stdout}");

        // And the next run: detection now names the new program, so the new run gets it.
        return (Lines(stdout), probe.Layout);
    }

    private static void CopySleep(string to)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        File.Copy("/bin/sleep", to, overwrite: true);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(to, (UnixFileMode)0b111_101_101);
    }

    /// <summary>
    /// Native Claude: versions/ is granted as a folder, so a version the host adds appears in the running sandbox and a
    /// version it deletes disappears; the running process keeps its program (the inode stays alive) but can no longer
    /// re-run it by path.
    /// </summary>
    [Fact]
    public async Task NativeLayoutNewVersionAppearsAndTheDeletedOneVanishesWhileTheProcessRuns()
    {
        if (!OperatingSystem.IsLinux()) Assert.Skip("Linux (bubblewrap) only");
        var root = HostPathCanonicalizer.Canonicalize(Directory.CreateTempSubdirectory("yawble-native-").FullName);
        var versions = Path.Combine(root, "share", "claude", "versions");
        var bin = Path.Combine(root, "bin");
        var v1 = Path.Combine(versions, "1.0.0");
        var v2 = Path.Combine(versions, "2.0.0");
        CopySleep(v1);
        Directory.CreateDirectory(bin);
        File.CreateSymbolicLink(Path.Combine(bin, "claude"), v1);
        var (seen, _) = await RunAsync(v1, versions, v2, () =>
        {
            // What the native updater does: stage the new version, swap the launcher symlink atomically, clean up the old.
            CopySleep(v2);
            File.CreateSymbolicLink(Path.Combine(bin, "claude.tmp"), v2);
            File.Move(Path.Combine(bin, "claude.tmp"), Path.Combine(bin, "claude"), overwrite: true);
            File.Delete(v1);
        });
        Assert.Equal("0", seen["old_still_running"]);
        Assert.Contains("2.0.0", seen["folder_now"]);
        Assert.DoesNotContain("1.0.0", seen["folder_now"]);
        Assert.NotEqual("0", seen["old_path_exists"]);
        Assert.NotEqual("0", seen["exec_old_path"]);
        Assert.Equal("0", seen["exec_new_path"]);
        Assert.Contains("(deleted)", seen.GetValueOrDefault("old_proc_exe", ""));
    }

    /// <summary>
    /// npm -g: the granted folder itself is replaced (moved aside, a new one put in place, the old removed). The
    /// sandbox's bind still points at the OLD folder: it keeps seeing the old files (until they are removed), never the
    /// new version; only the next run, detected again, gets the new one.
    /// </summary>
    [Fact]
    public async Task NpmLayoutTheReplacedPackageFolderIsNotSeenByTheRunningSandbox()
    {
        if (!OperatingSystem.IsLinux()) Assert.Skip("Linux (bubblewrap) only");
        var root = HostPathCanonicalizer.Canonicalize(Directory.CreateTempSubdirectory("yawble-npm-").FullName);
        var pkg = Path.Combine(root, "lib", "node_modules", "@anthropic-ai", "claude-code");
        var exe = Path.Combine(pkg, "bin", "claude.exe");
        var newMarker = Path.Combine(pkg, "bin", "claude-v2.exe");
        CopySleep(exe);
        var (seen, _) = await RunAsync(exe, pkg, newMarker, () =>
        {
            // npm's reify: move the old package aside, put the new one in place, delete the old.
            var aside = Path.Combine(Path.GetDirectoryName(pkg)!, ".claude-code-old");
            Directory.Move(pkg, aside);
            CopySleep(exe);
            CopySleep(newMarker);
            Directory.Delete(aside, recursive: true);
        });
        Assert.Equal("0", seen["old_still_running"]);
        // The bind follows the moved-and-deleted old folder: empty, the new files are not visible.
        Assert.DoesNotContain("bin", seen["folder_now"]);
        Assert.NotEqual("0", seen["exec_new_path"]);
        Assert.NotEqual("0", seen["exec_old_path"]);
    }

    /// <summary>
    /// npm's copy fallback writes the new program over the old file in place: Linux refuses to open a file that is
    /// being executed for writing (ETXTBSY), so an update on the host fails while a sandboxed run uses that program.
    /// </summary>
    [Fact]
    public async Task OverwritingTheRunningProgramInPlaceIsRefusedOnTheHost()
    {
        if (!OperatingSystem.IsLinux()) Assert.Skip("Linux (bubblewrap) only");
        var root = HostPathCanonicalizer.Canonicalize(Directory.CreateTempSubdirectory("yawble-inplace-").FullName);
        var pkg = Path.Combine(root, "pkg");
        var exe = Path.Combine(pkg, "claude");
        CopySleep(exe);
        Exception? refused = null;
        var (seen, _) = await RunAsync(exe, pkg, exe, () =>
        {
            try { File.Copy("/bin/true", exe, overwrite: true); }
            catch (IOException e) { refused = e; }
        });
        output.WriteLine($"in-place overwrite on the host: {refused?.Message ?? "SUCCEEDED"}");
        Assert.NotNull(refused);
        Assert.Contains("busy", refused!.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("0", seen["old_still_running"]);
    }
}
