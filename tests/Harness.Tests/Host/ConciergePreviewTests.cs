using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using Harness.Containers;
using Harness.Contracts;
using Harness.Host;
using Harness.Host.Auth;
using Harness.Pty;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Harness.Tests.Host;

/// <summary>
/// THE CONCIERGE LETS A PERSON TRY A TEAM'S APP, through the real Host. Its launch carries the
/// preview ports the Host listens on (<c>HARNESS_PREVIEW_PORTS</c>), the app port behind each
/// (<c>HARNESS_PREVIEW_APP_PORTS</c>, from <see cref="PreviewPorts.Map"/>) and the link base
/// (<c>HARNESS_PREVIEW_URL</c>); a member's and a Manager's never do. The <c>preview</c> tool's
/// <c>env</c> writes the team's environment to a 0600 file in the Concierge's folder and answers the
/// path and the names, never a value, in no answer, row or log; it is refused to a Manager, a member,
/// any other key and a person; the file goes when the session ends.
/// </summary>
public sealed class ConciergePreviewTests : IAsyncLifetime
{
    private const string Email = "person@example.test";

    /// <summary>A value with a quote in it, so the file's quoting is tried too.</summary>
    private const string Secret = "pa'ss-Q7vX2pL9tRw4kZs8";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), $"harness-concierge-preview-{Guid.NewGuid():N}");
    private readonly int _published = FreePort();
    private readonly CapturingEngine _engine = new();
    private readonly CapturingLogs _logs = new();
    private WebApplicationFactory<Program> _factory = null!;
    private string _user = "";

    private IServiceProvider Services => _factory.Services;

    private ConciergeSessionStore Consoles => Services.GetRequiredService<ConciergeSessionStore>();

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_dataRoot);
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host => host
            .UseSetting("DataRoot", _dataRoot)
            .UseSetting("Logging:LogLevel:Default", "Warning")
            .UseSetting("ScheduleRunnerEnabled", "false")
            .UseSetting(PreviewPorts.Variable, _published.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .ConfigureLogging(logging => logging.AddProvider(_logs).AddFilter<CapturingLogs>(null, LogLevel.Trace))
            .ConfigureTestServices(services =>
            {
                services.AddSingleton<IPtyEngine>(_engine);
                services.AddSingleton<IAgentRunner>(new FakeAgent());
            }));

        _user = (await Services.GetRequiredService<IUserStore>().CreateAsync(Email, HostFixture.Password, Ct)).Id;
    }

    public async ValueTask DisposeAsync()
    {
        await _factory.DisposeAsync();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataRoot, recursive: true); }
        catch (IOException) { }
    }

    [Fact]
    public async Task The_concierges_launch_carries_the_preview_ports_the_app_ports_and_the_link_base()
    {
        var env = await LaunchAsync();
        var app = PreviewPorts.Map([_published], _ => { })[_published];

        Assert.Equal(_published.ToString(System.Globalization.CultureInfo.InvariantCulture), env[PreviewPorts.ConciergePortsVariable]);
        Assert.Equal(app.ToString(System.Globalization.CultureInfo.InvariantCulture), env[PreviewPorts.ConciergeAppPortsVariable]);
        Assert.NotEqual(_published, app);
        Assert.Equal("http://" + new Uri(env["HARNESS_PUBLIC_URL"]).Host, env[PreviewPorts.ConciergeUrlVariable]);
    }

    [Theory]
    [InlineData("https://tunnel.example.net/base", "http://tunnel.example.net")]
    [InlineData("http://10.0.0.5:8080", "http://10.0.0.5")]
    [InlineData("http://[::1]:8080", "http://[::1]")]
    [InlineData("not an address", "http://localhost")]
    public void The_link_base_is_http_on_the_browsers_host_with_no_port(string publicUrl, string expected)
    {
        var env = PreviewPorts.ForConcierge(new Dictionary<int, int> { [28082] = 18082, [28081] = 18081 }, publicUrl);

        Assert.Equal(expected, env[PreviewPorts.ConciergeUrlVariable]);
        Assert.Equal("28081,28082", env[PreviewPorts.ConciergePortsVariable]);
        Assert.Equal("18081,18082", env[PreviewPorts.ConciergeAppPortsVariable]);
    }

    [Fact]
    public void With_no_preview_ports_the_lists_are_empty_and_the_link_base_is_still_set()
    {
        var env = PreviewPorts.ForConcierge(new Dictionary<int, int>(), "http://10.0.0.5:8080");

        Assert.Equal("", env[PreviewPorts.ConciergePortsVariable]);
        Assert.Equal("", env[PreviewPorts.ConciergeAppPortsVariable]);
        Assert.Equal("http://10.0.0.5", env[PreviewPorts.ConciergeUrlVariable]);
    }

    [Fact]
    public async Task A_members_and_a_managers_launch_never_carry_them()
    {
        var team = await TeamAsync("Shop");
        var catalog = Services.GetRequiredService<AgentCatalog>();
        var agent = catalog.Definitions.First(d => d.Mode == AgentMode.Headless).Name;
        var environment = new AgentEnvironment(
            new MintingPrincipals(), catalog, "http://127.0.0.1:5000", new TeamDocuments(new TeamPaths(_dataRoot)));

        // Even a team env that names them: the container path drops a HARNESS_ entry.
        var teamEnv = new Dictionary<string, string>
        {
            [PreviewPorts.ConciergePortsVariable] = "28081",
            [PreviewPorts.ConciergeAppPortsVariable] = "18081",
            [PreviewPorts.ConciergeUrlVariable] = "http://tunnel.example.net",
        };

        foreach (var name in new[] { TeamRegistry.DefaultManagerName, "Worker" })
        {
            var launched = await environment.ForContainerAsync(
                new ContainerId(team, name), agent, new HashSet<string>(Permits.All), teamEnv, [], Ct);

            Assert.DoesNotContain(launched.Keys, key => key.StartsWith("HARNESS_PREVIEW", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task The_env_action_writes_a_0600_file_and_answers_its_path_and_names_never_a_value()
    {
        var team = await TeamAsync("Shop");
        await SetEnvAsync(team, new() { ["DB_PASSWORD"] = Secret, ["APP_MODE"] = "trial" });
        var key = (await LaunchAsync())["HARNESS_KEY"];

        var reply = await Tools(key).Preview("env", team, Ct);

        Assert.StartsWith("HTTP 200", reply, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, reply, StringComparison.Ordinal);
        Assert.DoesNotContain("trial", reply, StringComparison.Ordinal);
        Assert.Contains("\"DB_PASSWORD\"", reply, StringComparison.Ordinal);
        Assert.Contains("\"APP_MODE\"", reply, StringComparison.Ordinal);

        var path = EnvFile(team);
        Assert.Contains(JsonEncodedText.Encode(path).ToString(), reply, StringComparison.Ordinal);
        Assert.Equal(Path.GetDirectoryName(path), ConciergeWorkspaces.TryExisting(Services.GetRequiredService<TeamPaths>(), _user));
        if (!OperatingSystem.IsWindows()) Assert.Equal(PreviewEnvFiles.FileMode, File.GetUnixFileMode(path));

        // What a shell's `. <path>` sets: the values, quoted.
        Assert.Equal(
            "export APP_MODE='trial'\nexport DB_PASSWORD='pa'\\''ss-Q7vX2pL9tRw4kZs8'\n",
            await File.ReadAllTextAsync(path, Ct));

        // NO ROW AND NO LOG carries a value.
        await using (var connection = new SqliteConnection($"Data Source={Path.Combine(_dataRoot, "messages.db")};Pooling=false"))
        {
            await connection.OpenAsync(Ct);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM tenant_events WHERE detail LIKE '%' || $v || '%' OR subject_name LIKE '%' || $v || '%'";
            command.Parameters.AddWithValue("$v", "Q7vX2pL9tRw4kZs8");
            Assert.Equal(0L, (long)(await command.ExecuteScalarAsync(Ct))!);
        }

        Assert.DoesNotContain(_logs.Lines, line => line.Contains("Q7vX2pL9tRw4kZs8", StringComparison.Ordinal));

        // A second write replaces the first, and a link planted at the name is never written through.
        var target = Path.Combine(_dataRoot, "planted-target");
        await File.WriteAllTextAsync(target, "kept", Ct);
        File.Delete(path);
        File.CreateSymbolicLink(path, target);
        Assert.StartsWith("HTTP 200", await Tools(key).Preview("env", team, Ct), StringComparison.Ordinal);
        Assert.Equal("kept", await File.ReadAllTextAsync(target, Ct));
        Assert.Null(new FileInfo(path).LinkTarget);
    }

    [Fact]
    public async Task A_manager_a_member_a_plugin_any_other_key_and_a_person_are_refused_and_no_file_is_written()
    {
        var team = await TeamAsync("Shop");
        await SetEnvAsync(team, new() { ["DB_PASSWORD"] = Secret });
        await LaunchAsync();

        var owner = (await Services.GetRequiredService<IUserStore>().FindAsync(Email, Ct))!;
        var every = new HashSet<string>(Permits.All, StringComparer.Ordinal) { Permits.Preview };
        var principals = Services.GetRequiredService<IPrincipalStore>();
        var keys = new List<(string Who, string Key)>
        {
            ("Manager", await principals.MintAsync(
                new ContainerId(team, TeamRegistry.DefaultManagerName).ToString(), PrincipalKind.Container, team, every, ct: Ct)),
            ("member", await principals.MintAsync(new ContainerId(team, "Worker").ToString(), PrincipalKind.Container, team, every, ct: Ct)),
            ("plugin", await principals.MintAsync(new ContainerId(team, "plugin-widget").ToString(), PrincipalKind.Container, team, every, ct: Ct)),
            ("api key", await principals.MintAsync("key-with-preview", PrincipalKind.ApiKey, null, every, ownerUserId: owner.Id, ct: Ct)),
            ("tenant concierge kind, other id", await principals.MintAsync(
                "not-the-concierge", PrincipalKind.TenantConcierge, null, every, ownerUserId: owner.Id, ct: Ct)),
        };

        foreach (var (who, key) in keys)
        {
            var reply = await Tools(key).Preview("env", team, Ct);
            Assert.True(reply.StartsWith("HTTP 403", StringComparison.Ordinal), $"{who}: {reply}");
            Assert.DoesNotContain(Secret, reply, StringComparison.Ordinal);
        }

        using var person = _factory.CreateClient();
        (await person.PostAsJsonAsync("/api/auth/login", new { email = Email, password = HostFixture.Password }, Ct)).EnsureSuccessStatusCode();
        using var refused = await person.PostAsync($"/api/teams/{team}/preview-env", null, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Contains(PreviewEnvFiles.Refusal, await refused.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);

        Assert.False(File.Exists(EnvFile(team)));
        Assert.DoesNotContain(Permits.Preview, Permits.All);
        Assert.DoesNotContain(Permits.Preview, TeamRegistry.ManagerPermits);
        Assert.Contains(Permits.Preview, ConciergeLaunchFactory.ConciergeOnlyPermits);
    }

    [Fact]
    public async Task The_file_is_removed_when_the_session_ends_and_any_left_is_removed_at_start()
    {
        var team = await TeamAsync("Shop");
        await SetEnvAsync(team, new() { ["DB_PASSWORD"] = Secret });
        var key = (await LaunchAsync())["HARNESS_KEY"];
        Assert.StartsWith("HTTP 200", await Tools(key).Preview("env", team, Ct), StringComparison.Ordinal);
        Assert.True(File.Exists(EnvFile(team)));

        await Consoles.EndAsync(new ConciergeSessionKey(_user));

        Assert.False(File.Exists(EnvFile(team)));

        // One a Host stopped without ending its sessions left behind.
        await File.WriteAllTextAsync(EnvFile(team), "export DB_PASSWORD='x'\n", Ct);
        Services.GetRequiredService<PreviewEnvFiles>().RemoveAll();
        Assert.False(File.Exists(EnvFile(team)));
    }

    [Fact]
    public async Task The_tool_takes_action_env_and_a_team()
    {
        var key = (await LaunchAsync())["HARNESS_KEY"];

        Assert.StartsWith("Refused:", await Tools(key).Preview("start", "Shop", Ct), StringComparison.Ordinal);
        Assert.StartsWith("Refused:", await Tools(key).Preview("env", null, Ct), StringComparison.Ordinal);
    }

    private async Task<IReadOnlyDictionary<string, string>> LaunchAsync()
    {
        await Consoles.AttachAsync(new ConciergeSessionKey(_user), "", 80, 24, Ct);
        return _engine.Spec!.Env!;
    }

    private string EnvFile(string team) =>
        Path.Combine(ConciergeWorkspaces.TryExisting(Services.GetRequiredService<TeamPaths>(), _user)!, PreviewEnvFiles.NameFor(team));

    private async Task<string> TeamAsync(string name) =>
        (await Services.GetRequiredService<TeamRegistry>().CreateAsync(name, "claude-headless", memberAgent: "claude-headless", ct: Ct)).Id;

    private async Task SetEnvAsync(string team, Dictionary<string, string> env)
    {
        using var person = _factory.CreateClient();
        (await person.PostAsJsonAsync("/api/auth/login", new { email = Email, password = HostFixture.Password }, Ct)).EnsureSuccessStatusCode();
        (await person.PutAsJsonAsync($"/api/teams/{team}/env", env, Ct)).EnsureSuccessStatusCode();
    }

    private PlatformMcpTools Tools(string key)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers[ApiKeyAuthenticationHandler.Header] = key;
        context.User = PrincipalClaims.ToClaimsPrincipal(new Principal("tools", PrincipalKind.TenantConcierge, new HashSet<string>()), "test");

        return new PlatformMcpTools(
            new FixedAccessor(context),
            Services.GetRequiredService<IPrincipalStore>(),
            Services.GetRequiredService<AgentCatalog>(),
            new ServerClients(_factory.Server.CreateHandler()));
    }

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private sealed class FixedAccessor(HttpContext context) : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get => context; set { } }
    }

    private sealed class ServerClients(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(handler, disposeHandler: false) { BaseAddress = new Uri("http://localhost") };
    }

    /// <summary>A terminal that prints nothing and never exits, keeping the spec it was spawned with.</summary>
    private sealed class CapturingEngine : IPtyEngine
    {
        public PtySpec? Spec { get; private set; }

        public Task<IPtySession> SpawnAsync(PtySpec spec, CancellationToken ct)
        {
            Spec = spec;
            return Task.FromResult<IPtySession>(new SilentSession());
        }
    }

    private sealed class SilentSession : IPtySession
    {
#pragma warning disable CS0067 // never raised: this terminal is silent
        public event Action<byte[]>? Output;
        public event Action<int>? Exited;
#pragma warning restore CS0067

        public void Write(ReadOnlySpan<byte> bytes) { }

        public void Resize(int cols, int rows) { }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>Every line the Host logs, at every level.</summary>
    private sealed class CapturingLogs : ILoggerProvider
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> _lines = new();

        public IEnumerable<string> Lines => _lines;

        public ILogger CreateLogger(string categoryName) => new Logger(_lines);

        public void Dispose() { }

        private sealed class Logger(System.Collections.Concurrent.ConcurrentQueue<string> lines) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                lines.Enqueue(formatter(state, exception) + exception);
        }
    }
}
