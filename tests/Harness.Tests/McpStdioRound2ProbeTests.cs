using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Harness.Contracts;
using Harness.Host;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests;

/// <summary>
/// TESTER PROBES, ROUND 2 OF MCP 3: the numbered environment across the switch, the stuck-write bound, unreadable
/// server lines. Each test states what the developer's notes or docs/mcp-servers.md promise and asserts it.
/// </summary>
public sealed class McpStdioRound2ProbeTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Script = """
        import json, os, sys, time
        def send(m):
            sys.stdout.write(json.dumps(m) + "\n"); sys.stdout.flush()
        mode = os.environ.get("FAKE_MODE", "")
        for line in sys.stdin:
            m = json.loads(line)
            method, ident = m.get("method"), m.get("id")
            if ident is None: continue
            if method == "initialize":
                if mode == "slow-start": time.sleep(3)
                send({"jsonrpc": "2.0", "id": ident, "result": {"protocolVersion": "2025-06-18", "capabilities": {"tools": {}}, "serverInfo": {"name": "probe", "version": "1"}}})
                if mode == "deaf": time.sleep(3600)
            elif method == "tools/call":
                if mode == "junk":
                    out = sys.stdout.buffer
                    for junk in [b'[1]', b'"x"', b'null', b'{"id":', b'\xff\xfe{}', b'{"jsonrpc":"2.0","id":123456789012345678901234567890,"result":{}}',
                                 b'{"jsonrpc":"2.0","id":{"a":1},"method":"ping"}', b'{"jsonrpc":"2.0","id":null,"method":"x"}',
                                 b'{"jsonrpc":"2.0","id":7,"method":["ping"]}', b'{"jsonrpc":"2.0","id":"%d","result":{}}' % ident]:
                        out.write(junk + b"\n")
                    out.flush()
                send({"jsonrpc": "2.0", "id": ident, "result": {"content": [{"type": "text", "text": json.dumps(
                    {"pid": os.getpid(), "env": dict(os.environ), "cmdline": open("/proc/self/cmdline", "rb").read().decode("utf-8", "replace"),
                     "parent": open("/proc/%d/cmdline" % os.getppid(), "rb").read().decode("utf-8", "replace")})}]}})
            else:
                send({"jsonrpc": "2.0", "id": ident, "result": {"tools": []}})
        """;

    private readonly string _folder = Directory.CreateTempSubdirectory("mcp-stdio-r2-").FullName;

    public McpStdioRound2ProbeTests()
    {
        var bin = Path.Combine(_folder, "bin");
        Directory.CreateDirectory(bin);
        File.WriteAllText(Path.Combine(_folder, "probe.py"), Script);
        foreach (var command in new[] { "npx", "uvx" })
        {
            var path = Path.Combine(bin, command);
            File.WriteAllText(path, $"#!/bin/sh\nexec python3 -u '{Path.Combine(_folder, "probe.py")}' \"$@\"\n");
            File.SetUnixFileMode(path, (UnixFileMode)Convert.ToInt32("755", 8));
        }

        // The setpriv stand-in records its arguments and environment, then runs the command, as the suite's own does.
        var standIn = Path.Combine(_folder, "setpriv");
        File.WriteAllText(standIn, "#!/bin/sh\necho \"$*\" >> \"$(dirname \"$0\")/calls\"\nenv -0 > \"$(dirname \"$0\")/switch-env\"\nwhile [ \"$1\" != \"--\" ]; do shift; done\nshift\nexec \"$@\"\n");
        File.SetUnixFileMode(standIn, (UnixFileMode)Convert.ToInt32("700", 8));
    }

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); }
        catch (IOException) { }
    }

    private McpStdioServers Servers(bool switches = false, TimeSpan? idle = null) => new(
        WorkerId.Local,
        switches
            ? new AgentLaunchUser(true, McpStdioServers.UserName, (int)getuid(), (int)getgid(), "the probe's setpriv stand-in", Path.Combine(_folder, "setpriv"))
            : AgentLaunchUser.Same(McpStdioServers.UserName, "probe"),
        Path.Combine(_folder, "mcp"), $"{Path.Combine(_folder, "bin")}:{McpStdioServers.DefaultPath}", idle);

    private static McpStdioLaunch Launch(IReadOnlyDictionary<string, string> env, string fingerprint = "one") => new(
        "mcp-r2", "probe", "npx", "@probe/server", "1.0.0", ["--flag"], env, fingerprint);

    private static McpStdioRequest Call(McpStdioLaunch launch, JsonObject? arguments = null, int callSeconds = 10, int startSeconds = 20) =>
        new(WorkerAsks.NewRequest(), launch, McpStdioRequest.Call, "anything", arguments, callSeconds, startSeconds);

    private static JsonElement Seen(McpStdioAnswered answer)
    {
        Assert.True(answer.Result is not null, $"no result: failure={answer.Failure} error={answer.Error} noAnswer={answer.NoAnswer}");
        return JsonDocument.Parse(answer.Result!["content"]![0]!["text"]!.GetValue<string>()).RootElement.Clone();
    }

    // PWD/OLDPWD are dash's (cd), LC_CTYPE is python3's own locale coercion (PEP 538).
    private static readonly string[] ShellOwn = ["PWD", "OLDPWD", "SHLVL", "_", "LC_CTYPE"];

    /// <summary>
    /// D4: values holding '=', newlines, quotes and shell syntax arrive byte for byte under their own names; PATH and
    /// HOME stay the platform's; a name colliding with HARNESS_MCP_ENV_* neither replaces another value nor leaks a
    /// numbered copy; no value is on the switch's command line or under its own name in the switch's environment.
    /// </summary>
    [Fact]
    public async Task The_numbered_environment_arrives_exactly_and_no_value_crosses_the_switch_named_or_on_argv()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Linux only.");
        await using var servers = Servers(switches: true);
        var env = new Dictionary<string, string>
        {
            ["A_EQ"] = "k=v=w==",
            ["B_NL"] = "line1\nline2\n\nend\n",
            ["C_SH"] = "$(touch /tmp/r2-pwn) `id` \"q\" 'q' \\ ; | & $HOME ${PATH}",
            ["D_EMPTY"] = "",
            ["HARNESS_MCP_ENV_0"] = "collide-zero-value",
            ["HARNESS_MCP_ENV_1"] = "collide-one-value",
            ["PATH"] = "/evil-path-value",
            ["HOME"] = "/evil-home-value",
            ["z_lower"] = "lower-value",
        };

        var seen = Seen(await servers.AnswerAsync(Call(Launch(env)), Ct));
        var final = seen.GetProperty("env").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString());
        TestContext.Current.TestOutputHelper?.WriteLine("final env: " + string.Join(", ", final.Keys.Order()));

        Assert.Equal("k=v=w==", final["A_EQ"]);
        Assert.Equal("line1\nline2\n\nend\n", final["B_NL"]);
        Assert.Equal(env["C_SH"], final["C_SH"]);
        Assert.Equal("", final["D_EMPTY"]);
        Assert.Equal("lower-value", final["z_lower"]);
        Assert.False(File.Exists("/tmp/r2-pwn"));
        Assert.Equal($"{Path.Combine(_folder, "bin")}:{McpStdioServers.DefaultPath}", final["PATH"]);
        Assert.DoesNotContain(final.Keys, k => k.StartsWith("HARNESS_", StringComparison.Ordinal));
        Assert.Equal(["A_EQ", "B_NL", "C_SH", "D_EMPTY", "HOME", "PATH", "z_lower"], final.Keys.Except(ShellOwn).Order(StringComparer.Ordinal));

        var calls = File.ReadAllText(Path.Combine(_folder, "calls"));
        var switchEnv = File.ReadAllText(Path.Combine(_folder, "switch-env")).Split('\0', StringSplitOptions.RemoveEmptyEntries);
        foreach (var value in new[] { "k=v=w", "line1", "touch /tmp/r2-pwn", "collide-zero", "collide-one", "evil", "lower-value" })
        {
            Assert.DoesNotContain(value, calls);
            Assert.DoesNotContain(value, seen.GetProperty("cmdline").GetString()!);
            Assert.DoesNotContain(value, seen.GetProperty("parent").GetString()!);
        }

        Assert.DoesNotContain(switchEnv, e => e.StartsWith("A_EQ=") || e.StartsWith("B_NL=") || e.StartsWith("C_SH=") || e.StartsWith("z_lower="));
        Assert.DoesNotContain(switchEnv, e => e.Contains("evil") || e.Contains("collide"));
    }

    /// <summary>D4: names the switched shell (dash) treats specially still start the server, with their value.</summary>
    [Theory]
    [InlineData("IFS")]
    [InlineData("CDPATH")]
    [InlineData("UID")]
    [InlineData("PPID")]
    [InlineData("OPTIND")]
    [InlineData("ENV")]
    [InlineData("PS4")]
    [InlineData("PWD")]
    [InlineData("SHELLOPTS")]
    [InlineData("BASH_ENV")]
    public async Task A_name_the_shell_treats_specially_still_starts_the_server(string name)
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Linux only.");
        await using var servers = Servers(switches: true);

        var answer = await servers.AnswerAsync(Call(Launch(new Dictionary<string, string> { [name] = "/tmp" })), Ct);

        Assert.True(answer.Result is not null, $"{name}: failure={answer.Failure} error={answer.Error} noAnswer={answer.NoAnswer}");
        var final = Seen(answer).GetProperty("env");
        TestContext.Current.TestOutputHelper?.WriteLine($"{name} -> {(final.TryGetProperty(name, out var v) ? v.GetString() : "(absent)")}");
    }

    /// <summary>D2: many calls queued behind a stuck write all end near their own bound; the next start and the idle stop work.</summary>
    [Fact]
    public async Task Calls_queued_behind_a_stuck_write_all_end_and_the_idle_stop_then_works()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Linux only.");
        await using var servers = Servers(idle: TimeSpan.FromSeconds(1));
        var launch = Launch(new Dictionary<string, string> { ["FAKE_MODE"] = "deaf" });

        var clock = Stopwatch.StartNew();
        var big = servers.AnswerAsync(Call(launch, new JsonObject { ["blob"] = new string('x', 1024 * 1024) }, callSeconds: 3), Ct);
        await Task.Delay(500, Ct);
        var pid = servers.Pid(launch.Server);
        var queued = Enumerable.Range(0, 8).Select(_ => servers.AnswerAsync(Call(launch, callSeconds: 3), Ct)).ToArray();
        var all = Task.WhenAll(queued.Append(big));
        Assert.True(await Task.WhenAny(all, Task.Delay(TimeSpan.FromSeconds(15), Ct)) == all, $"calls had not all ended after {clock.Elapsed.TotalSeconds:0} s");
        TestContext.Current.TestOutputHelper?.WriteLine($"all ended in {clock.Elapsed.TotalSeconds:0.0} s; pid {pid} running: {FakeStdioServer.Running(pid!.Value)}; " +
            string.Join(" | ", (await all).Select(a => $"noAnswer={a.NoAnswer} failure={a.Failure}")));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10));
        Assert.False(FakeStdioServer.Running(pid!.Value));

        // The next call starts it again (a small write fits the pipe; the deaf server leaves it unanswered within its bound).
        var next = await servers.AnswerAsync(Call(launch, callSeconds: 2), Ct);
        Assert.True(next.NoAnswer, $"next: failure={next.Failure}");
        Assert.NotNull(servers.Pid(launch.Server));
        Assert.NotEqual(pid, servers.Pid(launch.Server));
        await Task.Delay(TimeSpan.FromSeconds(2), Ct);
        await servers.SweepAsync();
        Assert.Null(servers.Pid(launch.Server));
    }

    /// <summary>The call bound still counts from initialize: a 3 s start does not eat a 2 s call bound.</summary>
    [Fact]
    public async Task The_call_bound_still_counts_from_the_initialize_answer()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Linux only.");
        await using var servers = Servers();

        var answer = await servers.AnswerAsync(Call(Launch(new Dictionary<string, string> { ["FAKE_MODE"] = "slow-start" }), callSeconds: 2), Ct);

        Assert.True(answer.Result is not null, $"failure={answer.Failure} noAnswer={answer.NoAnswer}");
    }

    /// <summary>D3: lines that are not objects, are cut off, are not UTF-8, carry odd ids or a non-string method do not end the session.</summary>
    [Fact]
    public async Task Unreadable_and_odd_lines_leave_the_session_and_the_call_answered()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Linux only.");
        await using var servers = Servers();
        var launch = Launch(new Dictionary<string, string> { ["FAKE_MODE"] = "junk" });

        var first = await servers.AnswerAsync(Call(launch), Ct);
        var pid = servers.Pid(launch.Server);
        var second = await servers.AnswerAsync(Call(launch), Ct);

        Assert.True(first.Result is not null, $"first: failure={first.Failure} error={first.Error} noAnswer={first.NoAnswer}");
        Assert.True(second.Result is not null, $"second: failure={second.Failure} error={second.Error} noAnswer={second.NoAnswer}");
        Assert.Equal(pid, servers.Pid(launch.Server));
    }

    [DllImport("libc")]
    private static extern uint getuid();

    [DllImport("libc")]
    private static extern uint getgid();
}

/// <summary>D1 round 2: package and version forms beyond the developer's theory cases, through the real save route.</summary>
public sealed class McpStdioPackageRound2ProbeTests : IAsyncLifetime
{
    private const string Email = "person@example.test";
    private const string PersonPassword = "correct horse battery";
    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), $"harness-test-mcp-r2-{Guid.NewGuid():N}");
    private readonly FakeStdioServer _stdio = new();
    private McpStdioServers _servers = null!;
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _person = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_dataRoot);
        _servers = _stdio.Servers();
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host => host
            .UseSetting("DataRoot", _dataRoot)
            .ConfigureTestServices(services =>
            {
                services.AddSingleton<IAgentRunner>(new FakeAgent());
                services.AddSingleton(_servers);
            }));
        await _factory.Services.GetRequiredService<IUserStore>().CreateAsync(Email, PersonPassword);
        _person = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        (await _person.PostAsJsonAsync("/api/auth/login", new { email = Email, password = PersonPassword }, Ct)).EnsureSuccessStatusCode();
    }

    public async ValueTask DisposeAsync()
    {
        _person.Dispose();
        await _factory.DisposeAsync();
        await _servers.DisposeAsync();
        _stdio.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataRoot, recursive: true); }
        catch (IOException) { }
    }

    [Theory]
    [InlineData("npx", "@-x/server", "1.2.3")]
    [InlineData("npx", "@.x/server", "1.2.3")]
    [InlineData("npx", "@x/-server", "1.2.3")]
    [InlineData("npx", "@x/.server", "1.2.3")]
    [InlineData("npx", "@/server", "1.2.3")]
    [InlineData("npx", "cöwsay", "1.2.3")]
    [InlineData("npx", "cow say", "1.2.3")]
    [InlineData("npx", "cow\tsay", "1.2.3")]
    [InlineData("npx", "cowsay\n@1.2.3", null)]
    [InlineData("npx", "cowsay", "1.2.3 --yes")]
    [InlineData("npx", "cowsay", "1.2.3;id")]
    [InlineData("npx", "cowsay", "$(id)")]
    [InlineData("npx", "cowsay", "١.٢.٣")]
    [InlineData("npx", "cowsay", "latest")]
    [InlineData("npx", "github:user/repo", "1.2.3")]
    [InlineData("npx", "https://x.test/a.tgz", "1.2.3")]
    [InlineData("npx", "file:../x", "1.2.3")]
    [InlineData("uvx", "-x", "1.0")]
    [InlineData("uvx", ".x", "1.0")]
    [InlineData("uvx", "_x", "1.0")]
    [InlineData("uvx", "foo[--with]", "1.0")]
    [InlineData("uvx", "foo[x] --with evil", "1.0")]
    [InlineData("uvx", "git+https://x.test/r", "1.0")]
    [InlineData("uvx", "foo @ https://x.test/f.whl", null)]
    [InlineData("uvx", "./local", "1.0")]
    [InlineData("uvx", "/abs", "1.0")]
    [InlineData("uvx", "foo", "1.0 --with evil")]
    [InlineData("uvx", "foo", "1.0;id")]
    [InlineData("uvx", "foo==1.0 --with evil", null)]
    [InlineData("uvx", "foo\n==1.0", null)]
    [InlineData("uvx", "foo", "١.٠")]
    [InlineData("uvx", "foo", ">=1.0")]
    public async Task A_package_or_version_beyond_the_rule_is_refused(string command, string package, string? version)
    {
        var response = await _person.PostAsJsonAsync("/api/mcp-servers",
            new { name = "r2", kind = "runs", command, package, version, args = Array.Empty<string>() }, Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);
        TestContext.Current.TestOutputHelper?.WriteLine($"{(int)response.StatusCode} {body}");

        Assert.True(response.StatusCode == HttpStatusCode.BadRequest,
            $"{command} package `{package}` version `{version}` was accepted ({(int)response.StatusCode}): {body}");
    }

    [Theory]
    [InlineData("npx", "@scope/server", "1.2.3")]
    [InlineData("npx", "cowsay", "1.2.3-rc.1")]
    [InlineData("uvx", "foo[x,y]", "1.0")]
    [InlineData("uvx", "mcp-server-fetch", "2025.4.7")]
    public async Task A_good_pinned_package_is_still_saved(string command, string package, string version)
    {
        var response = await _person.PostAsJsonAsync("/api/mcp-servers",
            new { name = "ok", kind = "runs", command, package, version, args = Array.Empty<string>() }, Ct);
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(Ct)}");
    }
}

/// <summary>Merge decision with MCP 4: a "Yawble runs it" entry is never reached through the Concierge, by list or by call.</summary>
public sealed class McpStdioConciergeProbeTests : IAsyncLifetime
{
    private static CancellationToken Ct => McpServerToolsBed.Ct;
    private readonly FakeStdioServer _fake = new();
    private McpStdioServers _servers = null!;
    private McpServerToolsBed _bed = null!;

    public async ValueTask InitializeAsync()
    {
        _servers = _fake.Servers();
        _bed = await McpServerToolsBed.StartAsync(stdio: _servers);
    }

    public async ValueTask DisposeAsync()
    {
        await _bed.DisposeAsync();
        await _servers.DisposeAsync();
        _fake.Dispose();
    }

    [Fact]
    public async Task A_Concierge_call_naming_a_runs_entrys_tool_is_refused_and_starts_nothing()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Linux only.");
        var id = (await _bed.PostAsync("/api/mcp-servers", new
        {
            name = "local", kind = "runs", command = "npx", package = "@fake/server", version = "1.2.3", args = Array.Empty<string>(),
        })).GetProperty("server").GetProperty("id").GetString()!;
        await _bed.PostAsync("/api/teams/Alpha/mcp-servers", new { server = id });
        (await _bed.Person.PutAsJsonAsync("/api/me/current-team", new { team = "Alpha" }, Ct)).EnsureSuccessStatusCode();
        var tools = _bed.Services.GetRequiredService<McpRunTools>();
        await tools.BeginConciergeAsync(_bed.PersonId, "Alpha", _bed.DataRoot, Ct);
        var concierge = new Principal(ConciergeLaunchFactory.PrincipalId(_bed.PersonId), PrincipalKind.TenantConcierge, new HashSet<string>(), _bed.PersonId);

        var answer = await tools.CallAsync(concierge, "local_whoami", null, Ct);

        var text = string.Join(" ", answer.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>().Select(c => c.Text));
        TestContext.Current.TestOutputHelper?.WriteLine(text);
        Assert.True(answer.IsError == true, text);
        Assert.Contains("is not one of", text);
        Assert.Null(_servers.Pid(id));
    }
}
