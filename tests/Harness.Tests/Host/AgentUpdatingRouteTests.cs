using System.Net.Http.Json;
using System.Text.Json;
using Harness.Contracts;
using Harness.Host;
using Harness.Host.Auth;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests.Host;

/// <summary>
/// THE COMPOSED CONTROL HOST MEASURES AGAIN WHEN A REAL WORKER JOINS: a control Host that has written its
/// start-time records - "no worker is connected" - is joined after start by a real
/// <see cref="ControlConnection"/> over the Host's own WebSocket route, and with no catalog save its tool
/// pre-flight, sign-in record and launch check come to name that worker. The worker answers control's
/// requests with canned events from its <see cref="ControlConnection.Apply"/>: no CLI is run, and no
/// update is asked (updates are off, and the image is not control's).
/// </summary>
public sealed class AgentUpdatingRouteTests
{
    private const string Key = "the-worker-key-Rm7";
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(90);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task In_control_a_real_worker_joining_after_start_is_measured_on_it_without_a_catalog_save()
    {
        var root = Directory.CreateTempSubdirectory("harness-remeasure-route-").FullName;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        try
        {
            await using var control = Host(root);
            _ = control.Server;

            // The start's records, written before any worker could join.
            await Until(() => AgentToolsRecord.Read(root) is { } tools && NoWorker(tools));

            var sockets = control.Server.CreateWebSocketClient();
            sockets.ConfigureRequest = request => request.Headers[ApiKeyAuthenticationHandler.Header] = Key;
            var id = new WorkerId("w1");
            var worker = new ControlConnection(
                id, BuildVersion.Current.Version, Key,
                ct => sockets.ConnectAsync(new Uri(control.Server.BaseAddress, WorkerKeyGate.ConnectRoute.TrimStart('/')), ct),
                capacity: () => new WorkerHelloCapacity(4, 8L * 1024 * 1024 * 1024));
            long seq = 0;
            worker.Apply = async (message, ct) =>
            {
                if (Answer(message) is { } answer)
                {
                    await worker.PublishAsync(new WorkerEnvelope(id, Interlocked.Increment(ref seq), answer), ct);
                }
            };
            var running = worker.RunAsync(stop.Token);

            // Measured on w1, with no catalog save: the pass runs once the join has been quiet a moment.
            await Until(() => AgentAuthRecord.Read(root)?.Worker == "w1"
                && AgentToolsRecord.Read(root) is { } tools && !NoWorker(tools)
                && AgentLaunchChecksRecord.Read(root)?.Presets.Any(p => p.Launch.Detail == "started on w1") == true);

            Assert.All(AgentAuthRecord.Read(root)!.Commands, c => Assert.True(c.Installed));

            // Nothing ever asked an update.
            using var person = await PersonAsync(control);
            var updates = await person.GetFromJsonAsync<JsonElement>("/api/agents/updates", Ct);
            Assert.Empty(Updates(updates));

            stop.Cancel();
            await running.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>What the scripted worker says to each request: installed and signed in, an empty listing, a version, a launch that started.</summary>
    private static WorkerEvent? Answer(ControlMessage message) => message switch
    {
        ProbeSignIn probe => new SignInProbed(probe.Request, [.. probe.Commands.Select(c => new SignInProbeResult(c.Command, true, true, $"{c.Command} on w1"))]),
        RunAgentCommands run => new AgentCommandsRan(run.Request, [.. run.Commands.Select(c => new AgentCliRunResult(
            true, 0, false,
            c.Arguments.Contains("--version") ? "1.0.0\n" : c.Arguments.Contains("--json") ? "[]" : "",
            "", false, null))]),
        CheckLaunch check => new LaunchChecked(check.Request, AgentLaunchReport.Ok, 0, null, "started on w1"),
        _ => null,
    };

    private static bool NoWorker(AgentToolsRecord tools) =>
        tools.Presets.Any(p => p.Detail?.Contains(WorkerListingRunner.NoWorkerText, StringComparison.Ordinal) == true);

    private static IEnumerable<JsonElement> Updates(JsonElement body) =>
        body.ValueKind == JsonValueKind.Array ? body.EnumerateArray()
        : body.TryGetProperty("updates", out var list) ? list.EnumerateArray()
        : [];

    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Bound;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"Not true within {Bound.TotalSeconds:0} s.");
            await Task.Delay(50, Ct);
        }
    }

    private static WebApplicationFactory<Program> Host(string root) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder
            .UseSetting("DataRoot", root)
            .UseSetting("Logging:LogLevel:Default", "Warning")
            .UseSetting("Role", "control")
            .UseSetting("Workers:Key", Key)
            .UseSetting("HARNESS_UPDATE_AGENTS", "0")
            .UseSetting("HARNESS_IMAGE", ""));

    private static async Task<HttpClient> PersonAsync(WebApplicationFactory<Program> host)
    {
        await host.Services.GetRequiredService<IUserStore>().CreateAsync("person@example.test", HostFixture.Password, Ct);
        var client = host.CreateClient();
        (await client.PostAsJsonAsync("/api/auth/login", new { email = "person@example.test", password = HostFixture.Password }, Ct))
            .EnsureSuccessStatusCode();
        return client;
    }
}
