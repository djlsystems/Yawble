using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Harness.Contracts;
using Harness.Host;

namespace Harness.Tests;

/// <summary>
/// A BINARY RESULT WRITTEN AS THE MEMBER, where the Host switches users: control asks a worker (<see cref="WriteAsAgent"/>),
/// whose <c>mkdir</c> and <c>dd</c> start with the agent prefix, so the system decides what the member may write.
///
/// <para>
/// The suite cannot switch users (that needs CAP_SETUID), so <see cref="AsMember"/> is a real
/// <see cref="AgentLaunchUser"/> that switches, with a <c>setpriv</c> stand-in that records its arguments and runs the
/// command as the suite's own user. Every line of the Host's switched path runs; only the uid change itself does not.
/// </para>
///
/// <para>
/// THE HOST-ONLY TESTS need a file and a folder this user cannot write, owned by another user (in the image, the
/// Host's <c>harness</c> files, mode 600/700): name them in <c>HARNESS_TEST_HOST_ONLY_FILE</c> and
/// <c>HARNESS_TEST_HOST_ONLY_DIR</c>. They are skipped otherwise, and refused if this user could write either.
/// </para>
/// </summary>
public sealed class McpServerToolsAsMemberTests
{
    private static CancellationToken Ct => McpServerToolsBed.Ct;

    [Fact]
    public async Task A_binary_result_is_written_as_the_member_and_the_Hosts_copy_is_removed()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "The member's write is Linux only.");
        using var member = new StandIn();
        var root = Directory.CreateTempSubdirectory("mcp-as-member-").FullName;
        try
        {
            var path = Path.Combine(root, "new", "deep", "c.png");
            Assert.Equal((WroteAsAgent.Written, null), await member.WriteAsync(root, path, [1, 2, 3]));

            Assert.Equal([1, 2, 3], await File.ReadAllBytesAsync(path, Ct));
            Assert.Equal(getuid().ToString(), Stat("%u", path));
            var calls = member.Calls();
            Assert.Contains(calls, c => c.Contains("/mkdir -p -- " + Path.GetDirectoryName(path)));
            var copy = Assert.Single(calls, c => c.Contains("/dd "));
            Assert.Contains("of=" + path + " conv=excl", copy);
            var staging = copy.Split(' ').Single(a => a.StartsWith("if=", StringComparison.Ordinal))[3..];
            Assert.False(Directory.Exists(Path.GetDirectoryName(staging)), "the Host's copy is left in " + staging);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task As_the_member_nothing_is_overwritten_and_a_link_at_the_name_is_not_written_through()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "The member's write is Linux only.");
        using var member = new StandIn();
        var root = Directory.CreateTempSubdirectory("mcp-as-member-").FullName;
        var outside = Directory.CreateTempSubdirectory("mcp-outside-").FullName;
        try
        {
            var path = Path.Combine(root, "c.png");
            await File.WriteAllBytesAsync(path, [9], Ct);
            File.CreateSymbolicLink(Path.Combine(root, "l.png"), Path.Combine(outside, "made.png"));
            File.WriteAllText(Path.Combine(root, "file"), "");

            Assert.Equal(WroteAsAgent.Exists, (await member.WriteAsync(root, path, [1])).Result);
            Assert.Equal(WroteAsAgent.Link, (await member.WriteAsync(root, Path.Combine(root, "l.png"), [1])).Result);
            Assert.Equal(WroteAsAgent.Link, (await member.WriteAsync(root, Path.Combine(root, "file", "x.png"), [1])).Result);

            Assert.Equal([9], await File.ReadAllBytesAsync(path, Ct));
            Assert.Empty(Directory.GetFileSystemEntries(outside));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public async Task A_members_call_writes_through_the_agent_prefix()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "The member's write is Linux only.");
        using var member = new StandIn();
        await using var bed = await McpServerToolsBed.StartAsync(runAs: member.User);
        JsonObject? answer = null;
        await bed.RunAsync("Alpha", "Dev", async run =>
        {
            using var mcp = bed.Mcp(McpServerToolsBed.Key(run));
            answer = await mcp.CallAsync("fake_draw");
        });

        var file = JsonNode.Parse(((JsonArray)answer!["result"]!["content"]!)[0]!["text"]!.GetValue<string>())!;
        var path = file["path"]!.GetValue<string>();
        Assert.Equal(FakeToolServer.Png, await File.ReadAllBytesAsync(path, Ct));
        Assert.Contains(member.Calls(), c => c.Contains("of=" + path));
    }

    [Theory]
    [InlineData("target")]
    [InlineData("folder-to-file")]
    [InlineData("folder-to-dir")]
    public async Task A_link_to_a_Host_only_place_leaves_it_unchanged_and_the_call_answers_a_sentence(string where)
    {
        var (hostFile, hostDir) = HostOnly();
        using var member = new StandIn();
        await using var bed = await McpServerToolsBed.StartAsync(runAs: member.User);
        var before = (Fingerprint(hostFile), Fingerprint(hostDir));
        JsonObject? answer = null;
        await bed.RunAsync("Alpha", "Dev", async run =>
        {
            var folder = Path.Combine(run.WorkingDirectory, McpRunTools.Folder, "fake");
            Directory.CreateDirectory(Path.GetDirectoryName(folder)!);
            using var mcp = bed.Mcp(McpServerToolsBed.Key(run));
            if (where == "target")
            {
                // The output path's folders are checked before the call; the name itself is the member's to swap.
                Directory.CreateDirectory(Path.Combine(run.WorkingDirectory, "art"));
                File.CreateSymbolicLink(Path.Combine(run.WorkingDirectory, "art", "pic.png"), hostFile);
                answer = await mcp.CallAsync("fake_draw", new JsonObject { ["outputPath"] = "art/pic.png" });
            }
            else
            {
                Directory.CreateSymbolicLink(folder, where == "folder-to-file" ? hostFile : hostDir);
                answer = await mcp.CallAsync("fake_draw");
            }
        });

        Assert.Equal(before, (Fingerprint(hostFile), Fingerprint(hostDir)));
        Assert.True(HarnessMcp.IsError(answer!), answer!.ToJsonString());
        Assert.Matches(@"^A image/png result of \d+ bytes from fake was not written: .+\.$", HarnessMcp.Text(answer!).Split('\n')[^1]);
    }

    [Fact]
    public async Task A_folder_swapped_for_a_link_to_a_Host_only_folder_never_changes_it()
    {
        var (hostFile, hostDir) = HostOnly();
        using var member = new StandIn();
        var root = Directory.CreateTempSubdirectory("mcp-as-member-race-").FullName;
        var real = Path.Combine(root, "d");
        var link = Path.Combine(root, "lnk");
        Directory.CreateDirectory(real);
        Directory.CreateSymbolicLink(link, hostDir);
        var before = (Fingerprint(hostFile), Fingerprint(hostDir));
        using var stop = new CancellationTokenSource();
        long swaps = 0;
        var outcomes = new Dictionary<string, int>();
        try
        {
            // renameat2(RENAME_EXCHANGE): `d` and `lnk` trade places atomically, so `d` is always there.
            var flipper = Task.Factory.StartNew(() =>
            {
                while (!stop.IsCancellationRequested)
                {
                    if (renameat2(-100, real, -100, link, 2) == 0) Interlocked.Increment(ref swaps);
                }
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

            for (var i = 0; i < 200; i++)
            {
                var (outcome, _) = await member.WriteAsync(root, Path.Combine(real, $"pic-{i}.png"), [1, 2, 3]);
                outcomes[outcome] = outcomes.GetValueOrDefault(outcome) + 1;
            }

            await stop.CancelAsync();
            await flipper;

            var said = $"{swaps} swaps; " + string.Join(", ", outcomes.Select(o => $"{o.Key} {o.Value}"));
            Assert.True(before == (Fingerprint(hostFile), Fingerprint(hostDir)), "a Host-only place changed: " + said);
            Assert.True(outcomes.GetValueOrDefault(WroteAsAgent.Denied) > 0, "the swap never landed mid-write: " + said);
            TestContext.Current.SendDiagnosticMessage(said);
        }
        finally
        {
            await stop.CancelAsync();
            Directory.Delete(root, recursive: true);
        }
    }

    // ---- helpers ----------------------------------------------------------------------------------

    /// <summary>The Host-only file and folder named for this run, after making sure this user can write neither.</summary>
    private static (string File, string Dir) HostOnly()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "The member's write is Linux only.");
        var file = Environment.GetEnvironmentVariable("HARNESS_TEST_HOST_ONLY_FILE");
        var dir = Environment.GetEnvironmentVariable("HARNESS_TEST_HOST_ONLY_DIR");
        Assert.SkipWhen(string.IsNullOrEmpty(file) || string.IsNullOrEmpty(dir), "HARNESS_TEST_HOST_ONLY_FILE and HARNESS_TEST_HOST_ONLY_DIR are not set");
        Assert.True(File.Exists(file) && Directory.Exists(dir), "the Host-only file and folder must exist");
        const int writable = 2;
        Assert.True(access(file!, writable) != 0 && access(dir!, writable) != 0, "this user can write the Host-only file or folder");
        Assert.NotEqual(getuid().ToString(), Stat("%u", file!));
        return (file!, dir!);
    }

    /// <summary>What would change if anything were written to, created in or replaced at <paramref name="path"/>.</summary>
    private static string Fingerprint(string path) => Stat("%i %s %Y %Z %U %a", path);

    private static string Stat(string format, string path)
    {
        var start = new ProcessStartInfo("stat") { RedirectStandardOutput = true, UseShellExecute = false };
        foreach (var a in new[] { "-c", format, "--", path }) start.ArgumentList.Add(a);
        using var process = Process.Start(start)!;
        var said = process.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit();
        return said;
    }

    /// <summary>A switching <see cref="AgentLaunchUser"/> whose <c>setpriv</c> records its call and runs what follows <c>--</c>.</summary>
    private sealed class StandIn : IDisposable
    {
        private readonly string _folder = Directory.CreateTempSubdirectory("setpriv-standin-").FullName;

        public StandIn()
        {
            var script = Path.Combine(_folder, "setpriv");
            File.WriteAllText(script,
                "#!/bin/sh\necho \"$*\" >> \"$(dirname \"$0\")/calls\"\nwhile [ \"$1\" != \"--\" ]; do shift; done\nshift\nexec \"$@\"\n");
            File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            User = new AgentLaunchUser(true, "agent", (int)getuid(), (int)getgid(), "the suite's setpriv stand-in", script);
        }

        public AgentLaunchUser User { get; }

        /// <summary>A worker's answer to a <see cref="WriteAsAgent"/>, as control would ask it.</summary>
        public async Task<(string Result, string? Detail)> WriteAsync(string root, string path, byte[] bytes)
        {
            var answer = Assert.IsType<WroteAsAgent>(
                await new WorkerAgentCli(WorkerId.Local, User).AnswerAsync(new WriteAsAgent("w1", root, path, bytes), Ct));
            return (answer.Result, answer.Detail);
        }

        public IReadOnlyList<string> Calls() =>
            File.Exists(Path.Combine(_folder, "calls")) ? File.ReadAllLines(Path.Combine(_folder, "calls")) : [];

        public void Dispose() => Directory.Delete(_folder, recursive: true);
    }

    [DllImport("libc")]
    private static extern uint getuid();

    [DllImport("libc")]
    private static extern uint getgid();

    [DllImport("libc", SetLastError = true)]
    private static extern int access(string path, int mode);

    [DllImport("libc", SetLastError = true)]
    private static extern int renameat2(int olddirfd, string oldpath, int newdirfd, string newpath, uint flags);
}
