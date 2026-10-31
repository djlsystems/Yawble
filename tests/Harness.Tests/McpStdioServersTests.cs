using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Harness.Contracts;
using Harness.Host;

namespace Harness.Tests;

/// <summary>
/// THE MCP SERVERS A WORKER RUNS, against a fake stdio server (<see cref="FakeStdioServer"/>): one process per server,
/// shared by every request; its environment the entry's variables, PATH and HOME and nothing else; its own working
/// folder; started again after a crash or a kill; gone after its idle time and started by the next call; restarted
/// when a request brings another fingerprint (a changed value); the call's bound counted from initialize.
/// The switch to the <c>mcp</c> user is the agent's (<see cref="AgentLaunchUser"/>): here a <c>setpriv</c> stand-in
/// records it, and <see cref="McpStdioRealSwitchTests"/> runs it for real where the process can switch.
/// </summary>
public sealed class McpStdioServersTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeStdioServer _fake = new();

    public void Dispose() => _fake.Dispose();

    private static McpStdioRequest Call(McpStdioLaunch launch, string tool, JsonObject? arguments = null, int callSeconds = 20, int startSeconds = 20) =>
        new(WorkerAsks.NewRequest(), launch, McpStdioRequest.Call, tool, arguments, callSeconds, startSeconds);

    private static JsonElement Who(McpStdioAnswered answer)
    {
        Assert.True(answer.Result is not null, $"no result: {answer.Failure} {answer.Error} {answer.NoAnswer}");
        return JsonDocument.Parse(answer.Result!["content"]![0]!["text"]!.GetValue<string>()).RootElement.Clone();
    }

    [Fact]
    public async Task A_call_starts_the_server_once_in_its_own_folder_with_only_the_entrys_variables_PATH_and_HOME()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Linux only.");
        await using var servers = _fake.Servers();
        var launch = FakeStdioServer.Launch();

        var first = Who(await servers.AnswerAsync(Call(launch, "whoami"), Ct));
        var second = Who(await servers.AnswerAsync(Call(launch, "whoami"), Ct));

        // ONE PROCESS, SHARED by every request.
        Assert.Equal(first.GetProperty("pid").GetInt32(), second.GetProperty("pid").GetInt32());
        Assert.Equal(first.GetProperty("pid").GetInt32(), servers.Pid(launch.Server));

        Assert.Equal(servers.WorkingFolder(launch.Server), first.GetProperty("cwd").GetString());
        Assert.Equal(Path.Combine(_fake.Root, "home"), first.GetProperty("home").GetString());
        Assert.Equal(["-y", "@fake/server@1.2.3", "--flag", "two words"], first.GetProperty("argv").EnumerateArray().Select(a => a.GetString()!));

        // NOTHING OF THE WORKER'S: no key, no HARNESS_* variable, no tool cache the image sets.
        var env = first.GetProperty("env").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString());
        var shells = new[] { "PWD", "OLDPWD", "SHLVL", "_", "LC_CTYPE" };
        Assert.Equal(["API_TOKEN", "HOME", "PATH"], env.Keys.Except(shells).Order(StringComparer.Ordinal));
        Assert.Equal("stdio-secret-9f8e7d", env["API_TOKEN"]);
        Assert.Equal(_fake.ServerPath, env["PATH"]);

        // Its home and its working folder are its own alone.
        Assert.Equal("700", Mode(Path.Combine(_fake.Root, "home")));
        Assert.Equal("700", Mode(servers.WorkingFolder(launch.Server)));
    }

    [Fact]
    public async Task Uvx_runs_the_pinned_release_and_an_entry_cannot_replace_PATH_or_HOME()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Linux only.");
        await using var servers = _fake.Servers();
        var launch = FakeStdioServer.Launch(
            new Dictionary<string, string> { ["PATH"] = "/nowhere", ["HOME"] = "/tmp/elsewhere", ["KEY"] = "v" }, command: "uvx", args: []);

        var who = Who(await servers.AnswerAsync(Call(launch, "whoami"), Ct));

        Assert.Equal(["fake-server==1.2.3"], who.GetProperty("argv").EnumerateArray().Select(a => a.GetString()!));
        Assert.Equal(_fake.ServerPath, who.GetProperty("env").GetProperty("PATH").GetString());
        Assert.Equal(Path.Combine(_fake.Root, "home"), who.GetProperty("env").GetProperty("HOME").GetString());
        Assert.Equal("v", who.GetProperty("env").GetProperty("KEY").GetString());
    }

    [Fact]
    public async Task A_call_during_a_crash_says_the_server_stopped_and_the_next_call_starts_it_again()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Linux only.");
        await using var servers = _fake.Servers();
        var launch = FakeStdioServer.Launch();
        var before = Who(await servers.AnswerAsync(Call(launch, "whoami"), Ct)).GetProperty("pid").GetInt32();

        var crashed = await servers.AnswerAsync(Call(launch, "crash"), Ct);

        Assert.Equal("fake stopped; the next call starts it again.", crashed.Failure);
        Assert.Null(crashed.Result);

        var after = Who(await servers.AnswerAsync(Call(launch, "whoami"), Ct)).GetProperty("pid").GetInt32();
        Assert.NotEqual(before, after);
    }

    [Fact]
    public async Task Killing_the_process_makes_the_next_call_start_it_again_and_succeed()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Linux only.");
        await using var servers = _fake.Servers();
        var launch = FakeStdioServer.Launch();
        var before = Who(await servers.AnswerAsync(Call(launch, "whoami"), Ct)).GetProperty("pid").GetInt32();

        Assert.Equal(0, FakeStdioServer.kill(before, 9));
        await WaitAsync(() => servers.Pid(launch.Server) is null);

        var after = Who(await servers.AnswerAsync(Call(launch, "whoami"), Ct)).GetProperty("pid").GetInt32();
        Assert.NotEqual(before, after);
        Assert.False(FakeStdioServer.Running(before));
    }

    [Fact]
    public async Task After_its_idle_time_without_a_call_the_process_is_gone_and_the_next_call_starts_it()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Linux only.");
        await using var servers = _fake.Servers(idle: TimeSpan.FromMilliseconds(400));
        var launch = FakeStdioServer.Launch();
        var before = Who(await servers.AnswerAsync(Call(launch, "whoami"), Ct)).GetProperty("pid").GetInt32();

        await WaitAsync(() => !FakeStdioServer.Running(before));
        Assert.Null(servers.Pid(launch.Server));

        var after = Who(await servers.AnswerAsync(Call(launch, "whoami"), Ct)).GetProperty("pid").GetInt32();
        Assert.NotEqual(before, after);
    }

    [Fact]
    public async Task The_idle_time_is_ten_minutes_and_a_call_in_flight_keeps_the_process()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Linux only.");
        Assert.Equal(TimeSpan.FromMinutes(10), McpStdioServers.DefaultIdle);

        var clock = new ManualTime(DateTimeOffset.Parse("2026-10-31T12:00:00Z"));
        await using var servers = _fake.Servers(clock: clock);
        var launch = FakeStdioServer.Launch();
        var pid = Who(await servers.AnswerAsync(Call(launch, "whoami"), Ct)).GetProperty("pid").GetInt32();

        clock.Advance(TimeSpan.FromMinutes(9));
        await servers.SweepAsync();
        Assert.Equal(pid, servers.Pid(launch.Server));

        // A CALL STILL RUNNING is not idle, however long it has run.
        var sleeping = servers.AnswerAsync(Call(launch, "sleep", new JsonObject { ["seconds"] = 2 }), Ct);
        await Task.Delay(500, Ct);
        clock.Advance(TimeSpan.FromMinutes(11));
        await servers.SweepAsync();
        Assert.Equal("slept", (await sleeping).Result!["content"]![0]!["text"]!.GetValue<string>());
        Assert.Equal(pid, servers.Pid(launch.Server));

        clock.Advance(TimeSpan.FromMinutes(10));
        await servers.SweepAsync();
        await WaitAsync(() => !FakeStdioServer.Running(pid));
        Assert.Null(servers.Pid(launch.Server));
    }

    [Fact]
    public async Task Another_fingerprint_restarts_it_with_the_new_value_and_a_stop_stops_it()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Linux only.");
        await using var servers = _fake.Servers();
        var first = Who(await servers.AnswerAsync(Call(FakeStdioServer.Launch(), "whoami"), Ct));

        var changed = FakeStdioServer.Launch(new Dictionary<string, string> { ["API_TOKEN"] = "rotated-value-123" }, fingerprint: "two");
        var second = Who(await servers.AnswerAsync(Call(changed, "whoami"), Ct));

        Assert.NotEqual(first.GetProperty("pid").GetInt32(), second.GetProperty("pid").GetInt32());
        Assert.False(FakeStdioServer.Running(first.GetProperty("pid").GetInt32()));
        Assert.Equal("rotated-value-123", second.GetProperty("env").GetProperty("API_TOKEN").GetString());

        Assert.True(await servers.StopAsync(changed.Server));
        Assert.False(FakeStdioServer.Running(second.GetProperty("pid").GetInt32()));
        Assert.Null(servers.Pid(changed.Server));
        Assert.False(await servers.StopAsync(changed.Server));
    }

    [Fact]
    public async Task The_call_bound_counts_from_initialize_so_a_slow_start_still_answers()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Linux only.");
        await using var servers = _fake.Servers();
        var launch = FakeStdioServer.Launch(new Dictionary<string, string> { ["FAKE_START_DELAY"] = "2" });

        // TWO SECONDS OF "INSTALL", then a call with a one-second bound: the bound starts once initialize is answered.
        var answered = await servers.AnswerAsync(Call(launch, "echo", new JsonObject { ["said"] = "hi" }, callSeconds: 1, startSeconds: 10), Ct);
        Assert.Equal("{\"said\": \"hi\"}", answered.Result!["content"]![0]!["text"]!.GetValue<string>());

        var slow = await servers.AnswerAsync(Call(launch, "sleep", new JsonObject { ["seconds"] = 3 }, callSeconds: 1), Ct);
        Assert.True(slow.NoAnswer);
    }

    [Fact]
    public async Task A_start_past_its_bound_fails_with_a_sentence_and_the_next_call_tries_again()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Linux only.");
        await using var servers = _fake.Servers();
        var launch = FakeStdioServer.Launch(new Dictionary<string, string> { ["FAKE_START_DELAY"] = "3" });

        var answered = await servers.AnswerAsync(Call(launch, "whoami", startSeconds: 1), Ct);

        Assert.Equal("fake did not start within 1 seconds; the next call tries again.", answered.Failure);
        Assert.Null(servers.Pid(launch.Server));
    }

    [Fact]
    public async Task A_server_that_stops_at_start_says_so_with_its_last_words_redacted_of_its_values()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Linux only.");
        await using var servers = _fake.Servers();
        var launch = FakeStdioServer.Launch(new Dictionary<string, string> { ["FAKE_STDERR_THEN_EXIT"] = "secret-in-stderr-42" });

        var answered = await servers.AnswerAsync(Call(launch, "whoami"), Ct);

        Assert.StartsWith("fake stopped before it answered initialize (exit code 3); the next call tries again.", answered.Failure);
        Assert.Contains("failed to start with [redacted]", answered.Failure);
        Assert.DoesNotContain("secret-in-stderr-42", answered.Failure);
    }

    [Fact]
    public async Task A_list_reads_every_page_and_a_check_runs_a_process_of_its_own_and_stops_it()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Linux only.");
        await using var servers = _fake.Servers();
        var launch = FakeStdioServer.Launch();

        var listed = await servers.AnswerAsync(new McpStdioRequest(WorkerAsks.NewRequest(), launch, McpStdioRequest.List), Ct);
        Assert.Equal(["whoami", "echo", "crash", "sleep", "pingme"], listed.Result!["tools"]!.AsArray().Select(t => t!["name"]!.GetValue<string>()));
        var shared = servers.Pid(launch.Server);
        Assert.NotNull(shared);

        var checkedOnce = await servers.AnswerAsync(new McpStdioRequest(WorkerAsks.NewRequest(), launch, McpStdioRequest.Check), Ct);
        Assert.Equal(5, checkedOnce.Result!["tools"]!.AsArray().Count);

        // The check's process is not the shared one, and it is gone; the shared one is untouched.
        Assert.Equal(shared, servers.Pid(launch.Server));
        Assert.Single(ChildrenNamed(Path.Combine(_fake.Folder, "fake_mcp.py")));
    }

    [Fact]
    public async Task A_ping_from_the_server_is_answered()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Linux only.");
        await using var servers = _fake.Servers();

        var answered = await servers.AnswerAsync(Call(FakeStdioServer.Launch(), "pingme"), Ct);

        Assert.Contains("\"id\": \"from-server\", \"result\": {}", answered.Result!["content"]![0]!["text"]!.GetValue<string>());
    }

    [Fact]
    public async Task It_starts_through_setsid_and_the_agents_setpriv_switch_with_every_capability_cleared()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Linux only.");
        var standIn = Path.Combine(_fake.Folder, "setpriv");
        File.WriteAllText(standIn, "#!/bin/sh\necho \"$*\" >> \"$(dirname \"$0\")/calls\"\nwhile [ \"$1\" != \"--\" ]; do shift; done\nshift\nexec \"$@\"\n");
        File.SetUnixFileMode(standIn, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var user = new AgentLaunchUser(true, McpStdioServers.UserName, (int)getuid(), (int)getgid(), "the suite's setpriv stand-in", standIn);
        await using var servers = _fake.Servers(user);

        Who(await servers.AnswerAsync(Call(FakeStdioServer.Launch(), "whoami"), Ct));

        var call = Assert.Single(File.ReadAllLines(Path.Combine(_fake.Folder, "calls")));
        Assert.StartsWith($"--reuid={getuid()} --regid={getgid()} --init-groups --inh-caps=-all --ambient-caps=-all -- /", call);
        Assert.Contains(" -c ", call);
        Assert.EndsWith($"{servers.WorkingFolder("mcp-fake")} npx -y @fake/server@1.2.3 --flag two words", call);
        Assert.DoesNotContain("stdio-secret-9f8e7d", call);
    }

    [Fact]
    public async Task A_worker_that_must_switch_to_mcp_and_cannot_starts_nothing()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Linux only.");
        var refusing = AgentLaunchUser.Same(McpStdioServers.UserName, "the Host lacks CAP_SETUID") with { AgentUserUnreachable = true };
        await using var servers = _fake.Servers(refusing);

        var answered = await servers.AnswerAsync(Call(FakeStdioServer.Launch(), "whoami"), Ct);

        Assert.StartsWith("MCP server fake was not started: a user 'mcp' exists on this machine", answered.Failure);
        Assert.Null(servers.Pid("mcp-fake"));
    }

    [Fact]
    public void The_mcp_user_is_resolved_without_sharing_a_file_with_its_group()
    {
        var passwd = Path.Combine(_fake.Folder, "passwd");
        File.WriteAllText(passwd, "mcp:x:10004:10004::/data/mcp/home:/usr/sbin/nologin\n");

        var user = McpStdioServers.ResolveUser(passwd);

        // Here nobody holds CAP_SETUID, so it cannot switch; it says so, and fails closed rather than run as the worker.
        Assert.Equal(McpStdioServers.UserName, user.Name);
        Assert.False(user.Switches);
        Assert.True(user.Refuses, user.Reason);
        Assert.DoesNotContain("cannot give a file", user.Reason);
    }

    private static IEnumerable<int> ChildrenNamed(string script) =>
        Directory.EnumerateDirectories("/proc")
            .Select(d => int.TryParse(Path.GetFileName(d), out var pid) ? pid : 0)
            .Where(pid => pid > 0 && FakeStdioServer.Running(pid))
            .Where(pid =>
            {
                try
                {
                    return File.ReadAllText($"/proc/{pid}/cmdline").Contains(script, StringComparison.Ordinal)
                        && File.ReadAllText($"/proc/{pid}/status").Contains($"PPid:\t", StringComparison.Ordinal)
                        && getppid(pid) == Environment.ProcessId;
                }
                catch (IOException)
                {
                    return false;
                }
            });

    private static int getppid(int pid)
    {
        foreach (var line in File.ReadLines($"/proc/{pid}/status"))
        {
            if (line.StartsWith("PPid:", StringComparison.Ordinal)) return int.Parse(line[5..].Trim(), System.Globalization.CultureInfo.InvariantCulture);
        }

        return 0;
    }

    private static async Task WaitAsync(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++) await Task.Delay(50, Ct);
        Assert.True(condition(), "the condition did not hold within 10 seconds");
    }

    /// <summary>The permission bits alone: a setgid bit inherited from the temp folder is not the server's.</summary>
    private static string Mode(string path) => Convert.ToString((int)File.GetUnixFileMode(path) & 0x1FF, 8);

    [DllImport("libc")]
    private static extern uint getuid();

    [DllImport("libc")]
    private static extern uint getgid();
}
