using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Harness.Containers;
using Harness.Contracts;
using Harness.Host;
using Harness.Host.Auth;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests.Host;

/// <summary>
/// <c>GET /api/workers</c> lists each worker - its build, when it connected, what it measured and the
/// runs placed on it - for people only; a Host that runs its runs itself lists its own one.
/// </summary>
public sealed class WorkersEndpointTests
{
    private const string Key = "the-worker-key-Ep9";

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Workers_lists_each_worker_with_version_connected_since_capacity_and_runs()
    {
        var root = Directory.CreateTempSubdirectory("harness-workers-route-").FullName;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        try
        {
            await using var control = Host(root, "control", Key);
            var sockets = control.Server.CreateWebSocketClient();
            sockets.ConfigureRequest = request => request.Headers[ApiKeyAuthenticationHandler.Header] = Key;
            var worker = new ControlConnection(
                new WorkerId("w1"), BuildVersion.Current.Version, Key,
                ct => sockets.ConnectAsync(new Uri(control.Server.BaseAddress, WorkerKeyGate.ConnectRoute.TrimStart('/')), ct),
                capacity: () => new WorkerHelloCapacity(4, 8L * 1024 * 1024 * 1024));
            var running = worker.RunAsync(stop.Token);

            var pool = control.Services.GetRequiredService<WorkerPool>();
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (pool.Workers.Count == 0 && DateTime.UtcNow < deadline) await Task.Delay(20, Ct);

            // A run placed on it.
            var developer = new ContainerId("alpha", "Developer");
            using var slot = control.Services.GetRequiredService<WipLedger>().TryEnter(developer);
            Assert.NotNull(slot);

            using var person = await PersonAsync(control);
            var workers = await person.GetFromJsonAsync<JsonElement>("/api/workers", Ct);
            var w1 = Assert.Single(workers.EnumerateArray());

            Assert.Equal("w1", w1.GetProperty("id").GetString());
            Assert.Equal(BuildVersion.Current.Version, w1.GetProperty("version").GetString());
            Assert.Equal("connected", w1.GetProperty("state").GetString());
            Assert.True(w1.GetProperty("connectedSince").GetDateTimeOffset() > DateTimeOffset.UtcNow.AddMinutes(-5));
            var capacity = w1.GetProperty("capacity");
            Assert.Equal(4, capacity.GetProperty("cpus").GetDouble());
            Assert.Equal(8L * 1024 * 1024 * 1024, capacity.GetProperty("memoryLimitBytes").GetInt64());
            // Its own bound, 4 CPUs - 1, unless the run limit is set (as an instance container's configuration sets it).
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("Wip__MaxRunning")))
            {
                Assert.Equal(3, capacity.GetProperty("bound").GetInt32());
            }
            var run = Assert.Single(w1.GetProperty("runs").EnumerateArray());
            Assert.Equal("Developer", run.GetProperty("member").GetString());

            stop.Cancel();
            await running.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        }
        finally
        {
            Clean(root);
        }
    }

    [Fact]
    public async Task Workers_is_for_people_only()
    {
        var root = Directory.CreateTempSubdirectory("harness-workers-people-").FullName;
        try
        {
            await using var all = Host(root, null, null);
            var key = await all.Services.GetRequiredService<IPrincipalStore>().MintAsync(
                "alpha/Worker", PrincipalKind.Container, "alpha", Permits.All, ct: Ct);
            using var machine = all.CreateClient();
            machine.DefaultRequestHeaders.Add(ApiKeyAuthenticationHandler.Header, key);

            var response = await machine.GetAsync("/api/workers", Ct);

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Contains("person's action", await response.Content.ReadAsStringAsync(Ct));
        }
        finally
        {
            Clean(root);
        }
    }

    [Fact]
    public async Task In_all_workers_lists_the_hosts_own()
    {
        var root = Directory.CreateTempSubdirectory("harness-workers-all-").FullName;
        try
        {
            await using var all = Host(root, null, null);
            using var person = await PersonAsync(all);

            var workers = await person.GetFromJsonAsync<JsonElement>("/api/workers", Ct);
            var own = Assert.Single(workers.EnumerateArray());

            Assert.Equal(WorkerId.Local.Value, own.GetProperty("id").GetString());
            Assert.Equal(BuildVersion.Current.Version, own.GetProperty("version").GetString());
            Assert.Equal("connected", own.GetProperty("state").GetString());
            Assert.Equal(JsonValueKind.Null, own.GetProperty("capacity").GetProperty("bound").ValueKind);
        }
        finally
        {
            Clean(root);
        }
    }

    private static WebApplicationFactory<Program> Host(string root, string? role, string? key) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("DataRoot", root).UseSetting("Logging:LogLevel:Default", "Warning");
            if (role is not null) builder.UseSetting("Role", role);
            if (key is not null) builder.UseSetting("Workers:Key", key);
        });

    private async Task<HttpClient> PersonAsync(WebApplicationFactory<Program> host)
    {
        await host.Services.GetRequiredService<IUserStore>().CreateAsync("person@example.test", HostFixture.Password, Ct);
        var client = host.CreateClient();
        (await client.PostAsJsonAsync("/api/auth/login", new { email = "person@example.test", password = HostFixture.Password }, Ct))
            .EnsureSuccessStatusCode();
        return client;
    }

    private static void Clean(string root)
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(root, recursive: true); }
        catch (IOException) { }
    }
}
