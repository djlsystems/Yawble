using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Harness.Contracts;
using Harness.Host;
using Harness.Host.Auth;
using Harness.Identity;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests.Host;

/// <summary>
/// The worker key is a principal with no permit, stored hashed, taken on the worker connection
/// alone: every other route refuses it with a sentence, and the connection refuses anything else with
/// one.
/// </summary>
public sealed class WorkerKeyTests(WorkerKeyTests.KeyedHost host) : IClassFixture<WorkerKeyTests.KeyedHost>
{
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_wrong_worker_key_is_refused_with_a_sentence()
    {
        foreach (var key in new[] { "not-the-worker-key", null })
        {
            using var client = host.Client(key);
            var response = await client.GetAsync(WorkerKeyGate.ConnectRoute, Ct);

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Equal(WorkerEndpoints.WrongKeyText, await ErrorOf(response));
        }
    }

    [Fact]
    public async Task A_worker_key_on_any_other_route_is_refused_with_a_sentence()
    {
        var requests = host.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText != WorkerKeyGate.ConnectRoute)
            .SelectMany(e => (e.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? ["GET"]).Select(m => (Method: m, Url: Url(e.RoutePattern))))
            .Concat(
            [
                ("GET", "/console"), ("GET", "/"), ("GET", "/index.html"), ("GET", "/assets/app.js"), ("POST", "/mcp"),
                ("GET", "/hub/containers"), ("GET", VersionEndpoints.Route),
            ])
            .Distinct()
            .ToList();

        Assert.True(requests.Count > 100, $"only {requests.Count} routes were found");

        using var client = host.Client(KeyedHost.Key);
        var wrong = new List<string>();
        foreach (var (method, url) in requests)
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), url);
            if (method is "POST" or "PUT" or "PATCH") request.Content = JsonContent.Create(new { });
            var response = await client.SendAsync(request, Ct);
            var error = await ErrorOf(response);

            if (response.StatusCode != HttpStatusCode.Forbidden || error != WorkerKeyGate.ElsewhereText)
            {
                wrong.Add($"{method} {url}: {(int)response.StatusCode} {error}");
            }
        }

        Assert.True(wrong.Count == 0, "Not refused with the worker-key sentence:" + Environment.NewLine + string.Join(Environment.NewLine, wrong));
    }

    [Fact]
    public async Task The_worker_key_is_accepted_on_the_worker_connection()
    {
        // The upgrade is taken: the connection opens, and this Host - which runs its runs itself - says so.
        var sockets = host.Server.CreateWebSocketClient();
        sockets.ConfigureRequest = request => request.Headers[ApiKeyAuthenticationHandler.Header] = KeyedHost.Key;
        using var webSocket = await sockets.ConnectAsync(new Uri(host.Server.BaseAddress, WorkerKeyGate.ConnectRoute.TrimStart('/')), Ct);
        using var socket = new WorkerSocket(webSocket, new WorkerFrameCodec(null));
        await socket.SendAsync(
            new WorkerHello(new WorkerId("w1"), BuildVersion.Current.Version, "s1", "n", new WorkerHelloCapacity(null, null), [], 0), Ct);
        Assert.Equal(WorkerSentences.NotControl, Assert.IsType<WorkerRefused>(await socket.ReceiveAsync(Ct)).Sentence);

        // Past the key and the kind, without a WebSocket: only the WebSocket is missing.
        using var client = host.Client(KeyedHost.Key);
        var response = await client.GetAsync(WorkerKeyGate.ConnectRoute, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(WorkerEndpoints.NotAWebSocketText, await ErrorOf(response));

        // And a wrong key gets no upgrade.
        var wrong = host.Server.CreateWebSocketClient();
        wrong.ConfigureRequest = request => request.Headers[ApiKeyAuthenticationHandler.Header] = "not-it";
        await Assert.ThrowsAnyAsync<Exception>(() => wrong.ConnectAsync(new Uri(host.Server.BaseAddress, WorkerKeyGate.ConnectRoute.TrimStart('/')), Ct));
    }

    [Fact]
    public async Task A_person_on_the_worker_connection_is_refused_with_a_sentence()
    {
        using var person = await host.PersonAsync();
        var response = await person.GetAsync(WorkerKeyGate.ConnectRoute, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(WorkerEndpoints.NotAWorkerText, await ErrorOf(response));

        var containerKey = await host.Services.GetRequiredService<IPrincipalStore>().MintAsync(
            "alpha/Worker", PrincipalKind.Container, "alpha", Permits.All, ct: Ct);
        using var container = host.Client(containerKey);
        var asContainer = await container.GetAsync(WorkerKeyGate.ConnectRoute, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, asContainer.StatusCode);
        Assert.Equal(WorkerEndpoints.NotAWorkerText, await ErrorOf(asContainer));
    }

    [Fact]
    public void The_worker_key_is_stored_hashed()
    {
        var (kind, hash, permits, label) = host.Row() ?? throw new InvalidOperationException("No worker key row.");

        Assert.Equal(nameof(PrincipalKind.Worker), kind);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(KeyedHost.Key))), hash);
        Assert.DoesNotContain(KeyedHost.Key, hash, StringComparison.Ordinal);
        Assert.Equal("[]", permits);
        Assert.Equal("worker key", label);
    }

    [Fact]
    public async Task In_all_with_no_key_no_worker_principal_exists()
    {
        var root = Directory.CreateTempSubdirectory("harness-worker-key-").FullName;
        try
        {
            await using (var keyed = KeyedHost.Over(root, KeyedHost.Key))
            {
                _ = keyed.Services;
                Assert.NotNull(KeyedHost.Row(root));
            }

            await using var bare = KeyedHost.Over(root, null);
            _ = bare.Services;
            Assert.Null(KeyedHost.Row(root));
            Assert.False(bare.Services.GetRequiredService<WorkerKeyGate>().Configured);

            using var client = bare.CreateClient();
            client.DefaultRequestHeaders.Add(ApiKeyAuthenticationHandler.Header, KeyedHost.Key);
            var response = await client.GetAsync(WorkerKeyGate.ConnectRoute, Ct);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
        }
    }

    /// <summary>A URL the pattern matches: each parameter filled to satisfy its constraints.</summary>
    private static string Url(RoutePattern pattern)
    {
        var segments = pattern.PathSegments.Select(segment => string.Concat(segment.Parts.Select(part => part switch
        {
            RoutePatternLiteralPart literal => literal.Content,
            RoutePatternSeparatorPart separator => separator.Content,
            RoutePatternParameterPart parameter => Value(parameter),
            _ => "x",
        })));
        return "/" + string.Join('/', segments);
    }

    private static string Value(RoutePatternParameterPart parameter)
    {
        var constraints = parameter.ParameterPolicies.Select(p => p.Content ?? "").ToList();
        if (constraints.Any(c => c is "int" or "long" || c.StartsWith("min", StringComparison.Ordinal) || c.StartsWith("range", StringComparison.Ordinal))) return "1";
        if (constraints.Contains("guid")) return Guid.Empty.ToString();
        if (constraints.Contains("bool")) return "true";
        return parameter.IsCatchAll ? "x/y" : "x";
    }

    private static async Task<string?> ErrorOf(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        try
        {
            using var json = JsonDocument.Parse(text);
            return json.RootElement.TryGetProperty("error", out var error) ? error.GetString() : text;
        }
        catch (JsonException)
        {
            return text;
        }
    }

    /// <summary>A Host whose worker key is set.</summary>
    public sealed class KeyedHost : IAsyncLifetime
    {
        public const string Key = "the-worker-key-for-this-test-Wk7";

        private readonly string _root = Directory.CreateTempSubdirectory("harness-worker-key-").FullName;
        private WebApplicationFactory<Program> _factory = null!;

        public IServiceProvider Services => _factory.Services;

        public Microsoft.AspNetCore.TestHost.TestServer Server => _factory.Server;

        public static WebApplicationFactory<Program> Over(string root, string? key) =>
            new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseSetting("DataRoot", root).UseSetting("Logging:LogLevel:Default", "Warning");
                if (key is not null) builder.UseSetting("Workers:Key", key);
            });

        public ValueTask InitializeAsync()
        {
            _factory = Over(_root, Key);
            _ = Services;
            return ValueTask.CompletedTask;
        }

        public HttpClient Client(string? key)
        {
            var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            if (key is not null) client.DefaultRequestHeaders.Add(ApiKeyAuthenticationHandler.Header, key);
            return client;
        }

        public async Task<HttpClient> PersonAsync()
        {
            await Services.GetRequiredService<IUserStore>().CreateAsync("person@example.test", HostFixture.Password);
            var client = _factory.CreateClient();
            var login = await client.PostAsJsonAsync("/api/auth/login", new { email = "person@example.test", password = HostFixture.Password });
            login.EnsureSuccessStatusCode();
            return client;
        }

        public (string Kind, string Hash, string Permits, string? Label)? Row() => Row(_root);

        public static (string Kind, string Hash, string Permits, string? Label)? Row(string root)
        {
            using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(root, "messages.db")}");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT kind, hash, permits, label FROM principals WHERE id = $id";
            command.Parameters.AddWithValue("$id", WorkerKeyGate.PrincipalId);
            using var reader = command.ExecuteReader();
            return reader.Read() ? (reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3)) : null;
        }

        public async ValueTask DisposeAsync()
        {
            await _factory.DisposeAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(_root, recursive: true); }
            catch (IOException) { }
        }
    }
}
