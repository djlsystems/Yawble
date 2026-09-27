using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Harness.Containers;
using Harness.Contracts;
using Harness.Host;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests;

/// <summary>
/// E5: <c>GET /api/teams/{team}/members/{member}/runs</c> lists a PLUGIN member's runs with what
/// each reported. A plugin run records no agent transcript, so the transcript filter the route
/// applies to agents answered <c>{"runs":[]}</c> for a plugin that had run. Here sample-echo runs
/// for real, hired through the person's route, beside an agent member whose listing is unchanged.
/// </summary>
public sealed class PluginMemberRunsTests : IAsyncLifetime
{
    private const string Email = "person@example.test";
    private const string Password = "correct horse battery";

    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), $"harness-plugin-runs-{Guid.NewGuid():N}");
    private readonly FakeAgent _agents = new();
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _person = null!;
    private string _team = "";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IServiceProvider Services => _factory.Services;

    private ContainerId Echo => new(_team, "Echo");

    private ContainerId Dev => new(_team, "Dev");

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_dataRoot);
        PluginMemberEndToEndTests.InstallSampleEcho(_dataRoot);

        _agents.Behaviour = invocation => Task.FromResult(new AgentResult(0, $"noted by {invocation.Container.Name}"));

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host => host
            .UseSetting("DataRoot", _dataRoot)
            .UseSetting("Logging:LogLevel:Default", "Warning")
            .ConfigureTestServices(services => services.AddSingleton<IAgentRunner>(_agents)));

        _team = (await Services.GetRequiredService<TeamRegistry>()
            .CreateAsync("Runs", "claude-headless", memberAgent: "claude-headless", ct: Ct)).Id;

        await Services.GetRequiredService<IUserStore>().CreateAsync(Email, Password);
        _person = _factory.CreateClient();
        (await _person.PostAsJsonAsync("/api/auth/login", new { email = Email, password = Password }, Ct)).EnsureSuccessStatusCode();

        var echo = await _person.PostAsJsonAsync($"/api/teams/{_team}/containers", new
        {
            name = "Echo",
            agent = "plugin:sample-echo",
            config = new { mode = "reverse" },
        }, Ct);
        Assert.Equal(HttpStatusCode.OK, echo.StatusCode);

        var dev = await _person.PostAsJsonAsync($"/api/teams/{_team}/containers", new { name = "Dev", agent = "claude-headless" }, Ct);
        Assert.Equal(HttpStatusCode.OK, dev.StatusCode);
    }

    public async ValueTask DisposeAsync()
    {
        _person.Dispose();
        await _factory.DisposeAsync();
        try { Directory.Delete(_dataRoot, recursive: true); }
        catch (IOException) { }
    }

    private async Task TellAsync(ContainerId who, string instruction, long? causation = null)
    {
        var told = await _person.PostAsJsonAsync(
            $"/api/teams/{_team}/containers/{who.Name}/tell",
            new { instruction, causation = causation?.ToString(System.Globalization.CultureInfo.InvariantCulture) },
            Ct);
        Assert.True(told.IsSuccessStatusCode, await told.Content.ReadAsStringAsync(Ct));
    }

    private async Task<IReadOnlyList<Message>> TerminalRowsAsync(ContainerId who) =>
        [.. (await Services.GetRequiredService<IMessageLog>()
                .ReadAfterAsync(0, [MessageTypes.Completed, MessageTypes.Failed], int.MaxValue, Ct))
            .Where(m => m.Source == who.ToString())];

    private async Task<IReadOnlyList<Message>> AwaitTerminalRowsAsync(ContainerId who, int count)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);

        while (DateTime.UtcNow < deadline)
        {
            var rows = await TerminalRowsAsync(who);
            if (rows.Count >= count) return rows;
            await Task.Delay(50, Ct);
        }

        throw new TimeoutException($"{who} wrote fewer than {count} terminal rows.");
    }

    private async Task AwaitStateAsync(ContainerId who, ContainerState state)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (Services.GetRequiredService<ContainerHost>().Find(who)!.State != state && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20, Ct);
        }

        Assert.Equal(state, Services.GetRequiredService<ContainerHost>().Find(who)!.State);
    }

    private async Task<JsonElement[]> RunsAsync(ContainerId who)
    {
        using var body = JsonDocument.Parse(await _person.GetStringAsync($"/api/teams/{_team}/members/{who.Name}/runs", Ct));
        return [.. body.RootElement.GetProperty("runs").EnumerateArray().Select(run => run.Clone())];
    }

    [Fact]
    public async Task A_plugin_members_runs_are_listed_newest_first_with_their_output()
    {
        await TellAsync(Echo, "abc");
        await AwaitTerminalRowsAsync(Echo, 1);
        await AwaitStateAsync(Echo, ContainerState.Idle);

        await TellAsync(Echo, "fail:nope");
        var rows = await AwaitTerminalRowsAsync(Echo, 2);
        await AwaitStateAsync(Echo, ContainerState.Idle);

        var runs = await RunsAsync(Echo);

        Assert.Equal(2, runs.Length);
        Assert.Equal(rows.Max(r => r.Seq), runs[0].GetProperty("seq").GetInt64());

        // A failed run lists what its row carries. The plugin's own failure sentence is the card's
        // failed mark, not a field of the row, so it is not asserted here.
        Assert.Equal("failed", runs[0].GetProperty("outcome").GetString());
        var failedRow = JsonDocument.Parse(rows.MaxBy(r => r.Seq)!.Payload).RootElement;
        var carried = new[] { "launchError", "output" }
            .Select(field => failedRow.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null)
            .FirstOrDefault(text => !string.IsNullOrWhiteSpace(text));
        Assert.Equal(carried, runs[0].GetProperty("output").GetString());

        Assert.Equal("completed", runs[1].GetProperty("outcome").GetString());
        Assert.Equal("cba", runs[1].GetProperty("output").GetString());
        Assert.Equal(JsonValueKind.String, runs[1].GetProperty("startedAt").ValueKind);
    }

    [Fact]
    public async Task A_batched_plugin_run_is_listed_once_not_once_per_message()
    {
        // The first run holds the member while two more instructions of the SAME workflow queue
        // behind it (a batch never spans workflows); the second run takes both, and writes a
        // terminal row for each. It is still one run.
        await TellAsync(Echo, "sleep:2");
        await AwaitStateAsync(Echo, ContainerState.Running);

        var first = Assert.Single(await Services.GetRequiredService<IMessageLog>()
            .ReadAfterAsync(0, [MessageTypes.InstructionFor(Echo)], int.MaxValue, Ct));
        await TellAsync(Echo, "ab", first.Seq);
        await TellAsync(Echo, "cd", first.Seq);

        var rows = await AwaitTerminalRowsAsync(Echo, 3);
        await AwaitStateAsync(Echo, ContainerState.Idle);

        var runs = await RunsAsync(Echo);

        Assert.Equal(3, rows.Count);
        Assert.Equal(2, runs.Length);
        Assert.Equal("ba\ndc", runs[0].GetProperty("output").GetString());
    }

    [Fact]
    public async Task An_agent_members_listing_is_unchanged_and_a_plugin_run_has_no_transcript()
    {
        // The fake agent records no transcript, so its run is not listed - as before.
        await TellAsync(Dev, "build the thing");
        await AwaitTerminalRowsAsync(Dev, 1);

        Assert.Empty(await RunsAsync(Dev));

        await TellAsync(Echo, "abc");
        var row = Assert.Single(await AwaitTerminalRowsAsync(Echo, 1));

        var transcript = await _person.GetAsync($"/api/teams/{_team}/members/Echo/runs/{row.Seq}/transcript", Ct);
        Assert.Equal(HttpStatusCode.NotFound, transcript.StatusCode);
    }
}
