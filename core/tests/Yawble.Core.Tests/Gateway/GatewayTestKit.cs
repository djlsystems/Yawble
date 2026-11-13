using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging;
using Yawble.Core.Credentials;
using Yawble.Core.Gateway;
using Yawble.Core.Tests.Bus;
using Xunit;

namespace Yawble.Core.Tests.Gateway;

/// <summary>Fake secrets only. None of these is a real credential, and no real credential on the machine is read.</summary>
internal static class FakeSecrets
{
    public const string SetupToken = "sk-ant-oat01-FAKE-setup-token-for-tests-0000";
    public const string AnthropicKey = "sk-ant-api03-FAKE-api-key-for-tests-0000";
    public const string XaiKey = "xai-FAKE-api-key-for-tests-0000";
    public const string GrokSession1 = "FAKE-grok-session-access-token-1";
    public const string GrokSession2 = "FAKE-grok-session-access-token-2";
    public const string CopilotToken = "gho_FAKEcopilotTokenForTests000000000000";
    public const string CodexAccess = "FAKE-codex-access-token";

    public static readonly string[] All = [SetupToken, AnthropicKey, XaiKey, GrokSession1, GrokSession2];

    /// <summary>Writes an auth.json shaped like grok's (research G5/G6) into a test Grok home.</summary>
    public static void WriteGrokAuth(GrokHome home, string token, DateTimeOffset expiresAt)
    {
        home.EnsureCreated();
        var json = new JsonObject
        {
            ["https://auth.x.ai::00000000-0000-0000-0000-000000000000"] = new JsonObject
            {
                ["access_token"] = token,
                ["refresh_token"] = "FAKE-refresh-token",
                ["auth_mode"] = "oauth",
                ["expires_at"] = expiresAt.ToUnixTimeSeconds(),
            },
        };
        File.WriteAllText(home.AuthFile, json.ToJsonString());
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(home.AuthFile, FileCredentialStore.OwnerOnly);
    }
}

/// <summary>A settable clock.</summary>
internal sealed class FakeClock(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;
    public override DateTimeOffset GetUtcNow() => Now;
    public void Advance(TimeSpan by) => Now += by;
}

/// <summary>
/// A local fake of api.anthropic.com, cli-chat-proxy.grok.com and api.x.ai in one ASP.NET Core app: in memory
/// (TestServer, reached through <see cref="Handler"/>; no port) by default, or on Kestrel at a loopback port for the
/// opt-in real-loopback tests. It accepts a
/// request only when it carries the REAL (fake) credential the gateway must swap in, and records what it saw, so a
/// placeholder that leaks upstream is caught. Streaming endpoints send their first event, then wait for
/// <see cref="ReleaseStream"/>, so a test can prove the gateway passes events through before the response ends.
/// </summary>
internal sealed class FakeUpstream : IAsyncDisposable
{
    private readonly WebApplication _app;
    private TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public sealed record Seen(string Method, string PathAndQuery, string? Authorization, string? ApiKey, IReadOnlyDictionary<string, string> Headers, string Body);

    public ConcurrentQueue<Seen> Requests { get; } = new();

    /// <summary>When set, every request is answered 401 (a revoked credential upstream).</summary>
    public volatile bool RefuseAll;

    private FakeUpstream(WebApplication app) => _app = app;

    public Uri BaseUri { get; private set; } = null!;

    /// <summary>In memory: the handler the gateway's upstream client sends through. Null on Kestrel (real sockets).</summary>
    public HttpMessageHandler? Handler { get; private set; }

    public void ReleaseStream() => _release.TrySetResult();

    public static async Task<FakeUpstream> StartAsync(bool realLoopback = false)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        if (realLoopback) builder.WebHost.UseKestrel(o => o.Listen(IPAddress.Loopback, 0));
        else builder.WebHost.UseTestServer();
        var app = builder.Build();
        var fake = new FakeUpstream(app);
        app.Run(fake.HandleAsync);
        await app.StartAsync();
        if (realLoopback)
        {
            fake.BaseUri = new Uri(app.Urls.First());
        }
        else
        {
            fake.BaseUri = new Uri("http://fake-upstream.test/");
            fake.Handler = app.GetTestServer().CreateHandler();
        }
        return fake;
    }

    private async Task HandleAsync(HttpContext http)
    {
        var req = http.Request;
        var body = await new StreamReader(req.Body).ReadToEndAsync();
        var headers = req.Headers.ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase);
        var auth = headers.GetValueOrDefault("Authorization");
        var key = headers.GetValueOrDefault("x-api-key");
        Requests.Enqueue(new Seen(req.Method, req.Path + req.QueryString, auth, key, headers, body));

        var path = req.Path.Value ?? "/";
        var accepted = RefuseAll ? null : (auth, key) switch
        {
            ("Bearer " + FakeSecrets.SetupToken, null) => "claude-subscription",
            (null, FakeSecrets.AnthropicKey) => "claude-apikey",
            ("Bearer " + FakeSecrets.XaiKey, null) => "xai-apikey",
            ("Bearer " + FakeSecrets.GrokSession1, null) => "grok-session-1",
            ("Bearer " + FakeSecrets.GrokSession2, null) => "grok-session-2",
            ("Bearer " + FakeSecrets.CopilotToken, null) => "copilot-host",
            ("Bearer " + FakeSecrets.CodexAccess, null) => "codex-host:" + headers.GetValueOrDefault("ChatGPT-Account-Id"),
            _ => null,
        };
        if (accepted is null)
        {
            http.Response.StatusCode = 401;
            await http.Response.WriteAsync("{\"type\":\"error\",\"error\":{\"type\":\"authentication_error\",\"message\":\"invalid credential (fake upstream)\"}}");
            return;
        }
        // Rate-limit headers both vendors send; the gateway must pass them back.
        http.Response.Headers["anthropic-ratelimit-unified-status"] = "allowed";
        http.Response.Headers["anthropic-ratelimit-unified-5h-utilization"] = "0.42";
        http.Response.Headers["x-ratelimit-remaining-requests"] = "99";
        http.Response.Headers["x-fake-accepted"] = accepted;

        if (req.Method == "POST" && path is "/v1/messages" or "/v1/responses" or "/v1/chat/completions")
        {
            http.Response.ContentType = "text/event-stream";
            http.Response.Headers.CacheControl = "no-cache";
            await http.Response.WriteAsync("event: message_start\ndata: {\"n\":1}\n\n");
            await http.Response.Body.FlushAsync();
            // Hold the stream open until the test has seen the first event through the gateway.
            var release = _release.Task;
            if (await Task.WhenAny(release, Task.Delay(TimeSpan.FromSeconds(15))) != release)
            {
                await http.Response.WriteAsync("event: error\ndata: {\"timeout\":true}\n\n");
                return;
            }
            // The usage figures a model API reports at the end, which the gateway's usage meter reads.
            await http.Response.WriteAsync("event: message_stop\ndata: {\"n\":2,\"model\":\"fake-model\",\"usage\":{\"input_tokens\":12,\"output_tokens\":7,\"cache_read_input_tokens\":3}}\n\n");
            return;
        }
        http.Response.ContentType = "application/json";
        await http.Response.WriteAsync($"{{\"ok\":true,\"path\":\"{path}\",\"accepted\":\"{accepted}\"}}");
    }

    public void ResetStream() => _release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    public async ValueTask DisposeAsync()
    {
        ReleaseStream();
        Handler?.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}

/// <summary>A bus endpoint that answers nothing: for gateway tests that do not involve the bus.</summary>
internal sealed class NoBus : IBusEndpoint
{
    public Task<BusReply> HandleAsync(byte[] body, CancellationToken ct) => Task.FromResult(new BusReply(404, "{}"));
}

/// <summary>The real built binaries next to this test run (Debug or Release, whichever this is).</summary>
internal static class Binaries
{
    public static string RepoRoot { get; } = FindRoot();

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Yawble.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repository root not found");
    }

    private static string Configuration =>
        AppContext.BaseDirectory.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries) is var parts
        && Array.LastIndexOf(parts, "bin") is var i and >= 0 ? parts[i + 1] : "Debug";

    /// <summary>yawble-bridge: YAWBLE_BRIDGE_BIN (e.g. the native AOT publish) if set, else the JIT build run by dotnet.</summary>
    public static ProcessStartInfo Bridge() => Start("YAWBLE_BRIDGE_BIN", "bridge/src/Yawble.Bridge", "yawble-bridge.dll");

    /// <summary>The folder of yawble-run's JIT build (its apphost runs with the dll beside it).</summary>
    public static string RunnerBuild() => Path.Combine(RepoRoot, "bridge/src/Yawble.Run", "bin", Configuration, "net10.0");

    /// <summary>The folder of yawble-bridge's JIT build (its apphost runs with the dll beside it).</summary>
    public static string BridgeBuild() => Path.Combine(RepoRoot, "bridge/src/Yawble.Bridge", "bin", Configuration, "net10.0");

    /// <summary>The folder of yawble-grok-auth's JIT build (Grok's placeholder auth provider).</summary>
    public static string GrokAuthBuild() => Path.Combine(RepoRoot, "bridge/src/Yawble.GrokAuth", "bin", Configuration, "net10.0");

    /// <summary>The folder of the dotnet wrapper's JIT build (Windows sandboxes: builds in one MSBuild process).</summary>
    public static string DotnetShimBuild() => Path.Combine(RepoRoot, "bridge/src/Yawble.DotnetShim", "bin", Configuration, "net10.0");

    public static ProcessStartInfo GrokAuth() => Start("YAWBLE_GROK_AUTH_BIN", "bridge/src/Yawble.GrokAuth", "yawble-grok-auth.dll");

    /// <summary>yawble-guardcheck, the guardrail check's probe: YAWBLE_GUARDCHECK_BIN (native AOT) if set, else the JIT build.</summary>
    public static ProcessStartInfo GuardCheck() => Start("YAWBLE_GUARDCHECK_BIN", "bridge/src/Yawble.GuardCheck", "yawble-guardcheck.dll");

    private static ProcessStartInfo Start(string overrideVariable, string project, string dll)
    {
        var native = Environment.GetEnvironmentVariable(overrideVariable);
        ProcessStartInfo psi;
        if (!string.IsNullOrEmpty(native))
        {
            psi = new ProcessStartInfo(native);
        }
        else
        {
            var path = Path.Combine(RepoRoot, project, "bin", Configuration, "net10.0", dll);
            Assert.True(File.Exists(path), $"{path} was not built");
            psi = new ProcessStartInfo("dotnet");
            psi.ArgumentList.Add(path);
        }
        psi.UseShellExecute = false;
        psi.RedirectStandardInput = psi.RedirectStandardOutput = psi.RedirectStandardError = true;
        // A sandbox gets an explicit environment; so does the bridge here. Nothing of the test process leaks in.
        psi.Environment.Clear();
        psi.Environment["PATH"] = Environment.GetEnvironmentVariable("PATH");
        if (Environment.GetEnvironmentVariable("DOTNET_ROOT") is { } root) psi.Environment["DOTNET_ROOT"] = root;
        psi.Environment["HOME"] = Path.GetTempPath();
        if (OperatingSystem.IsWindows())
        {
            // As the compiler gives a Windows sandbox (SandboxRequestCompiler: SYSTEMROOT, WINDIR): without SystemRoot,
            // Winsock cannot load its providers and every socket fails with WSAEPROVIDERFAILEDINIT (10106).
            foreach (var name in new[] { "SystemRoot", "windir" })
                if (Environment.GetEnvironmentVariable(name) is { } value) psi.Environment[name] = value;
        }
        return psi;
    }
}

/// <summary>
/// A client of the gateway as the CLIs are: absolute-form requests through it as an HTTP proxy. The connection is in
/// memory when the gateway is (the default), a loopback socket for a real-loopback fixture.
/// </summary>
internal static class ProxyClient
{
    public static HttpClient For(RunGateway gateway) => ForUrl(gateway.ProxyUrl);

    /// <summary>Through the gateway named by a proxy URL (e.g. a compiled request's runtimeConfig.networkProxy).</summary>
    public static HttpClient ForUrl(Uri proxy) => new(InMemoryNetwork.ProxyHandler(proxy)) { Timeout = TimeSpan.FromSeconds(30) };

    /// <summary>A raw connection to the gateway on <paramref name="port"/> (in memory when one listens there).</summary>
    public static async Task<Stream> ConnectAsync(int port)
    {
        if (InMemoryNetwork.IsListening(port)) return await InMemoryNetwork.ConnectAsync(port);
        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        return new NetworkStream(client.Client, ownsSocket: true);
    }

    /// <summary>Sends raw bytes to the gateway and returns the response's status line (for CONNECT and odd forms).</summary>
    public static async Task<(string StatusLine, Stream Stream, IDisposable Client)> RawAsync(RunGateway gateway, string request)
    {
        var stream = await ConnectAsync(gateway.Port);
        var client = stream;
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request));
        var line = new StringBuilder();
        var one = new byte[1];
        while (await stream.ReadAsync(one) == 1 && one[0] != '\n') if (one[0] != '\r') line.Append((char)one[0]);
        return (line.ToString(), stream, client);
    }
}

/// <summary>A migrated temp database plus the gateway's collaborators, all on fake secrets.</summary>
internal sealed class GatewayFixture : IAsyncDisposable
{
    public TempDb Db { get; } = new();
    public FakeClock Clock { get; } = new(DateTimeOffset.UtcNow);
    public CredentialService Credentials { get; }
    public GrokHome GrokHome { get; }
    public GrokKeeper? Keeper { get; private set; }
    public GatewayLog Log { get; }
    public RunTokenRegistry Tokens { get; }
    public GatewayHost Host { get; private set; } = null!;
    public FakeUpstream Upstream { get; private set; } = null!;

    /// <summary>Where CONNECT goes in memory (names and servers by port); null for a real-loopback fixture.</summary>
    public FakeConnectDialer? Dialer { get; private set; }

    /// <summary>The opt-in switch for tests that need real loopback sockets: YAWBLE_REAL_LOOPBACK=1.</summary>
    public const string RealLoopbackVariable = "YAWBLE_REAL_LOOPBACK";

    public static bool RealLoopbackEnabled => Environment.GetEnvironmentVariable(RealLoopbackVariable) == "1";

    /// <summary>Skips the calling test unless real loopback sockets are allowed (they are not by default).</summary>
    public static void SkipUnlessRealLoopback(string why) =>
        Assert.SkipUnless(RealLoopbackEnabled, $"{why}: binds real loopback ports, set {RealLoopbackVariable}=1 to run it");

    private GatewayFixture()
    {
        Credentials = CredentialService.FileFallback(Path.Combine(Db.Dir, "data"), Db.ConnectionString);
        GrokHome = GrokHome.InDataDir(Path.Combine(Db.Dir, "data")).EnsureCreated();
        Log = new GatewayLog(Db.ConnectionString, Clock);
        Tokens = new RunTokenRegistry(Clock);
    }

    public static async Task<GatewayFixture> StartAsync(bool allowPrivateConnect = false, IReadOnlySet<int>? connectPorts = null, GrokKeeperOptions? keeper = null,
        ClaudeHostSession? claudeHost = null, Func<string?>? copilotToken = null, CodexHostSession? codexHost = null, bool realLoopback = false)
    {
        // A real-loopback fixture binds ports: only for tests gated on SkipUnlessRealLoopback.
        if (realLoopback && !RealLoopbackEnabled)
            throw new InvalidOperationException($"a real-loopback gateway fixture needs {RealLoopbackVariable}=1 (call SkipUnlessRealLoopback first)");
        var f = new GatewayFixture();
        f.Upstream = await FakeUpstream.StartAsync(realLoopback);
        if (!realLoopback) f.Dialer = new FakeConnectDialer();
        if (keeper is not null) f.Keeper = new GrokKeeper(f.GrokHome, keeper, f.Clock);
        var upstreams = new Dictionary<string, Uri>(StringComparer.OrdinalIgnoreCase)
        {
            [Vhosts.Model] = f.Upstream.BaseUri, [Vhosts.Grok] = f.Upstream.BaseUri, [Vhosts.Xai] = f.Upstream.BaseUri,
            [Vhosts.Copilot] = f.Upstream.BaseUri, [Vhosts.Codex] = f.Upstream.BaseUri,
        };
        f.Host = new GatewayHost(new GatewayCredentials(f.Credentials, f.GrokHome, f.Keeper, f.Clock, claudeHost, copilotToken ?? (() => null), codexHost), f.Log, f.Tokens, new GatewayHostOptions
        {
            Upstreams = upstreams,
            AllowPrivateConnectTargets = allowPrivateConnect,
            ConnectPorts = connectPorts ?? new HashSet<int> { 443 },
            GrokRefreshWait = TimeSpan.FromSeconds(10),
            // In memory unless real loopback was asked for: the same gateway pipeline, no port bound.
            Listener = realLoopback ? null : () => new InMemoryGatewayListener(),
            UpstreamHandler = f.Upstream.Handler,
            ConnectDialer = f.Dialer,
        });
        return f;
    }

    public static readonly IReadOnlySet<string> AllModelVhosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Vhosts.Model, Vhosts.Grok, Vhosts.Xai };

    public RunGateway StartRun(long runId, IReadOnlyList<string>? allowHosts = null, IBusEndpoint? bus = null, TimeSpan? lifetime = null,
        IReadOnlySet<string>? vhosts = null, bool claudeFromHost = false, bool copilotFromHost = false, bool codexFromHost = false) =>
        Host.Start(new RunGatewaySpec(runId, $"Alpha/m{runId}", vhosts ?? AllModelVhosts, allowHosts ?? [], lifetime ?? TimeSpan.FromHours(1))
            { ClaudeFromHost = claudeFromHost, CopilotFromHost = copilotFromHost, CodexFromHost = codexFromHost, RunKey = $"run-{runId}" }, bus ?? new NoBus());

    public async ValueTask DisposeAsync()
    {
        if (Keeper is not null) await Keeper.DisposeAsync();
        Host.Dispose();
        await Upstream.DisposeAsync();
        Db.Dispose();
    }
}
