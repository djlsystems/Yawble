using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
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
/// TESTER PROBES FOR B007H (MCP 3), against 0c7205b. Each test states what the spec or the developer's notes promise and
/// asserts it; a failing test here is a defect in the report (/data/documents/McpServers/B007H-test-report.md).
/// </summary>
public sealed class McpStdioProbeTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // A server that answers initialize, then behaves as FAKE_MODE says.
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
                send({"jsonrpc": "2.0", "id": ident, "result": {"protocolVersion": "2025-06-18", "capabilities": {"tools": {}}, "serverInfo": {"name": "probe", "version": "1"}}})
                if mode == "deaf":
                    time.sleep(3600)
            elif method == "tools/call":
                if mode == "numeric-method":
                    sys.stdout.write('{"jsonrpc":"2.0","id":99,"method":5}\n'); sys.stdout.flush()
                if mode == "duplicate-key":
                    sys.stdout.write('{"jsonrpc":"2.0","id":%d,"result":{"content":[]},"result":{"content":[]}}\n' % ident); sys.stdout.flush()
                    continue
                send({"jsonrpc": "2.0", "id": ident, "result": {"content": [{"type": "text", "text": "pid %d" % os.getpid()}]}})
            else:
                send({"jsonrpc": "2.0", "id": ident, "result": {"tools": []}})
        """;

    private readonly string _folder = Directory.CreateTempSubdirectory("mcp-stdio-probe-").FullName;

    public McpStdioProbeTests()
    {
        var bin = Path.Combine(_folder, "bin");
        Directory.CreateDirectory(bin);
        File.WriteAllText(Path.Combine(_folder, "probe.py"), Script);
        foreach (var command in new[] { "npx", "uvx" })
        {
            var path = Path.Combine(bin, command);
            File.WriteAllText(path, $"#!/bin/sh\nexec python3 -u '{Path.Combine(_folder, "probe.py")}'\n");
            File.SetUnixFileMode(path, (UnixFileMode)Convert.ToInt32("755", 8));
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); }
        catch (IOException) { }
    }

    private McpStdioServers Servers(TimeSpan? idle = null) => new(
        WorkerId.Local, AgentLaunchUser.Same(McpStdioServers.UserName, "probe"), Path.Combine(_folder, "mcp"),
        $"{Path.Combine(_folder, "bin")}:{McpStdioServers.DefaultPath}", idle);

    private static McpStdioLaunch Launch(string mode) => new(
        "mcp-probe", "probe", "npx", "@probe/server", "1.0.0", [], new Dictionary<string, string> { ["FAKE_MODE"] = mode }, "one");

    private static McpStdioRequest Call(McpStdioLaunch launch, JsonObject? arguments = null, int callSeconds = 2) =>
        new(WorkerAsks.NewRequest(), launch, McpStdioRequest.Call, "anything", arguments, callSeconds, 20);

    /// <summary>
    /// SPEC: a call's bound (here 2 s, 120 s in production) counts from initialize. A server that stops reading its
    /// stdin (busy, hung or hostile) while a request bigger than the pipe buffer is written must still have the call
    /// answered within the bound - and must not hold the idle stop off forever.
    /// </summary>
    [Fact]
    public async Task A_call_to_a_server_that_stopped_reading_its_input_still_ends_within_its_bound()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Linux only.");
        await using var servers = Servers(idle: TimeSpan.FromSeconds(1));
        var launch = Launch("deaf");

        var big = new JsonObject { ["blob"] = new string('x', 1024 * 1024) };
        var clock = Stopwatch.StartNew();
        var call = servers.AnswerAsync(Call(launch, big), Ct);
        var done = await Task.WhenAny(call, Task.Delay(TimeSpan.FromSeconds(20), Ct));

        Assert.True(done == call, $"The call with a 2 s bound had not ended after {clock.Elapsed.TotalSeconds:0} s: the write to the server's stdin is not bounded.");
        Assert.True((await call).NoAnswer);

        // And the idle stop reaches it once nothing is in flight.
        await Task.Delay(TimeSpan.FromSeconds(2), Ct);
        await servers.SweepAsync();
        Assert.Null(servers.Pid(launch.Server));
    }

    /// <summary>Recovery from the hang above: a second call queues behind the stuck write, and a stop (an edit of the
    /// entry, as control sends it) still ends the process within its 2 s + 5 s.</summary>
    [Fact]
    public async Task A_stop_still_ends_a_server_whose_input_is_stuck_and_a_second_call_is_not_bounded_either()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Linux only.");
        await using var servers = Servers();
        var launch = Launch("deaf");

        var first = servers.AnswerAsync(Call(launch, new JsonObject { ["blob"] = new string('x', 1024 * 1024) }), Ct);
        await Task.Delay(TimeSpan.FromSeconds(1), Ct);
        var pid = servers.Pid(launch.Server);
        Assert.NotNull(pid);
        var second = servers.AnswerAsync(Call(launch), Ct);
        var secondDone = await Task.WhenAny(second, Task.Delay(TimeSpan.FromSeconds(8), Ct)) == second;

        var clock = Stopwatch.StartNew();
        var stopped = await servers.StopAsync(launch.Server);
        TestContext.Current.TestOutputHelper?.WriteLine($"second call ended within 8 s: {secondDone}; stop took {clock.Elapsed.TotalSeconds:0.0} s; pid {pid} running after: {FakeStdioServer.Running(pid!.Value)}");
        Assert.True(stopped);
        Assert.False(FakeStdioServer.Running(pid.Value));
        var firstDone = await Task.WhenAny(first, Task.Delay(TimeSpan.FromSeconds(10), Ct)) == first;
        Assert.True(firstDone, "the stuck call did not end even after the stop");
        Assert.True(secondDone, "a second call (2 s bound) queued behind the stuck write and did not end within 8 s");
    }

    /// <summary>A server line whose "method" is not a string is the server's mistake, not a reason to drop a healthy
    /// process: the call it is answering should still be answered.</summary>
    [Fact]
    public async Task A_server_request_whose_method_is_not_a_string_does_not_end_the_session()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Linux only.");
        await using var servers = Servers();
        var launch = Launch("numeric-method");

        var answer = await servers.AnswerAsync(Call(launch, callSeconds: 10), Ct);

        Assert.True(answer.Result is not null, $"no result: failure={answer.Failure} error={answer.Error} noAnswer={answer.NoAnswer}");
    }

    /// <summary>A response with a duplicated key: an answer or a clean failure, never a dropped session.</summary>
    [Fact]
    public async Task A_response_with_a_duplicate_key_is_answered_or_refused_and_the_next_call_works()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Linux only.");
        await using var servers = Servers();
        var launch = Launch("duplicate-key");

        var first = await servers.AnswerAsync(Call(launch, callSeconds: 5), Ct);
        var pid = servers.Pid(launch.Server);
        var second = await servers.AnswerAsync(Call(launch, callSeconds: 5), Ct);

        TestContext.Current.TestOutputHelper?.WriteLine($"first: result={first.Result?.ToJsonString()} failure={first.Failure} noAnswer={first.NoAnswer}; pid {pid} then {servers.Pid(launch.Server)}");
        Assert.False(first.NoAnswer, "the duplicate-key answer was lost: the call waited out its bound");
        Assert.Equal(pid, servers.Pid(launch.Server));
        Assert.False(second.NoAnswer);
    }
}

/// <summary>
/// TESTER PROBE: the package of an entry the platform runs is validated so that the version is pinned. A package that
/// starts with "-" is read by npx / uvx as an OPTION, so the pin is bypassed (npx -y --yes@1.2.3 cowsay runs the
/// latest cowsay, unpinned) - it must be refused like any other unpinned package.
/// </summary>
public sealed class McpStdioPackageProbeTests : IAsyncLifetime
{
    private const string Email = "person@example.test";
    private const string PersonPassword = "correct horse battery";
    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), $"harness-test-mcp-probe-{Guid.NewGuid():N}");
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
    [InlineData("npx", "--yes", "1.2.3")]
    [InlineData("npx", "-q", "1.2.3")]
    [InlineData("npx", "-c", "1.2.3")]
    [InlineData("uvx", "--offline", "1.2.3")]
    [InlineData("uvx", "-q", "1.2.3")]
    public async Task A_package_that_starts_with_a_dash_is_refused(string command, string package, string version)
    {
        var response = await _person.PostAsJsonAsync("/api/mcp-servers",
            new { name = "dash", kind = "runs", command, package, version, args = new[] { "cowsay" } }, Ct);

        Assert.True(response.StatusCode == HttpStatusCode.BadRequest,
            $"{command} package `{package}` version {version} was accepted ({(int)response.StatusCode}): {await response.Content.ReadAsStringAsync(Ct)}");
    }
}
