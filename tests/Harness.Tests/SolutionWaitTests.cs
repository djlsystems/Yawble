using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Harness.Containers;
using Harness.Contracts;
using Harness.Host;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests;

/// <summary>
/// A TEAM BLOCKED ON A MISSING REQUIRED INPUT SPENDS NOTHING. While `GET /api/teams/{team}/solution`
/// names something `missing`, every trigger of the team - schedule, event and folder - and a person's
/// Run now skip their fire with `schedule.skipped`, the reason "waiting for …" and a tenant row, as a
/// capped fire does. Providing the input unblocks the team at once, the next fire runs, and a member
/// card's blocked or failed mark from the wait clears.
///
/// On the real Host with a fake agent member (Dev) and the background runner off, so nothing fires
/// but what a test asks for. The team's solution row is written directly: one agent member and a
/// required Resume folder, no plugin and no server.
/// </summary>
public sealed class SolutionWaitTests : IAsyncLifetime
{
    private const string Email = "person@example.test";
    private const string Password = "correct horse battery";
    private const string Waiting = "waiting for a file in Resume/";

    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), $"harness-solution-wait-{Guid.NewGuid():N}");
    private readonly FakeAgent _agents = new();
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _person = null!;
    private string _team = "";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IServiceProvider Services => _factory.Services;

    private IMessageLog Log => Services.GetRequiredService<IMessageLog>();

    private ContainerId Dev => new(_team, "Dev");

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_dataRoot);

        _agents.Behaviour = _ => Task.FromResult(new AgentResult(0, "done", Usage: new InvocationUsage(1000, 500, "test")));

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host => host
            .UseSetting("DataRoot", _dataRoot)
            .UseSetting("Logging:LogLevel:Default", "Warning")
            .UseSetting("ScheduleRunnerEnabled", "false")
            .ConfigureTestServices(services => services.AddSingleton<IAgentRunner>(_agents)));

        _team = (await Services.GetRequiredService<TeamRegistry>()
            .CreateAsync("Waits", "claude-headless", memberAgent: "claude-headless", ct: Ct)).Id;

        await Services.GetRequiredService<IUserStore>().CreateAsync(Email, Password);
        _person = _factory.CreateClient();
        (await _person.PostAsJsonAsync("/api/auth/login", new { email = Email, password = Password }, Ct)).EnsureSuccessStatusCode();

        var dev = await _person.PostAsJsonAsync($"/api/teams/{_team}/containers", new { name = "Dev", agent = "claude-headless" }, Ct);
        Assert.Equal(HttpStatusCode.OK, dev.StatusCode);

        var manifest = new JsonObject
        {
            ["format"] = 1,
            ["id"] = "waits",
            ["name"] = "Waits",
            ["version"] = "1.0.0",
            ["description"] = "A team that needs a resume.",
            ["team"] = new JsonObject { ["name"] = "Waits", ["instructions"] = "Write letters." },
            ["members"] = new JsonArray(new JsonObject { ["name"] = "Dev", ["role"] = "member", ["instructions"] = "Write." }),
            ["inputs"] = new JsonObject
            {
                ["documents"] = new JsonArray(new JsonObject
                {
                    ["folder"] = "Resume",
                    ["description"] = "Your reference resume.",
                    ["required"] = true,
                }),
            },
        }.ToJsonString();

        await Services.GetRequiredService<ITeamSolutionStore>().SaveAsync(
            new TeamSolutionRow(
                _team, "waits", "Waits", "1.0.0", _dataRoot, DateTimeOffset.UtcNow, Email, null, manifest,
                new Dictionary<string, string>(), new Dictionary<string, string> { ["Dev"] = "Dev" },
                new Dictionary<string, string>(), new Dictionary<string, string>()),
            new TriggerAudit("test", null, "solution.installed", _team, "Waits", null),
            Ct);

        // The premise: the team reads blocked on its Resume.
        var solution = await _person.GetFromJsonAsync<JsonElement>($"/api/teams/{_team}/solution", Ct);
        Assert.Equal("Resume/", Assert.Single(solution.GetProperty("missing").EnumerateArray()).GetProperty("name").GetString());
    }

    public async ValueTask DisposeAsync()
    {
        _person.Dispose();
        await _factory.DisposeAsync();
        try { Directory.Delete(_dataRoot, recursive: true); }
        catch (IOException) { }
    }

    private async Task<string> CreateAsync(object body)
    {
        var created = await _person.PostAsJsonAsync($"/api/teams/{_team}/triggers", body, Ct);
        Assert.True(created.StatusCode == HttpStatusCode.Created, await created.Content.ReadAsStringAsync(Ct));
        return (await created.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetString()!;
    }

    /// <summary>Daily at 03:00 UTC on Dev: never due while a test runs unless a test says so.</summary>
    private Task<string> DailyAsync() => CreateAsync(new
    {
        name = "Nightly",
        kind = "cron",
        expression = "0 0 3 * * *",
        timezone = "UTC",
        container = "Dev",
        idleOnly = false,
        instruction = "look",
        wakeManager = "never",
    });

    private async Task<JsonElement> RunNowAsync(string id)
    {
        var response = await _person.PostAsync($"/api/teams/{_team}/triggers/{id}/run", null, Ct);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    private async Task UploadResumeAsync()
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent("resume"u8.ToArray());
        file.Headers.ContentType = new MediaTypeHeaderValue("text/markdown");
        form.Add(file, "file", "resume.md");
        form.Add(new StringContent("Resume"), "path");
        Assert.Equal(HttpStatusCode.OK, (await _person.PostAsync($"/api/teams/{_team}/documents/upload", form, Ct)).StatusCode);
    }

    private async Task<Message> AwaitRowAsync(IReadOnlyCollection<string> types, Func<Message, bool> match, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);

        while (DateTime.UtcNow < deadline)
        {
            if ((await Log.ReadAfterAsync(0, types, int.MaxValue, Ct)).FirstOrDefault(match) is { } row) return row;
            await Task.Delay(50, Ct);
        }

        throw new TimeoutException($"No row: {what}.");
    }

    private async Task<IReadOnlyList<TenantEvent>> TenantRowsAsync(string action, string id) =>
        [.. (await Services.GetRequiredService<ITenantLog>().ReadAsync(take: 10_000, ct: Ct)).Events
            .Where(e => e.Action == action && e.Subject == id)];

    private Task<IReadOnlyList<Message>> InstructionsForDevAsync() =>
        Log.ReadAfterAsync(0, [MessageTypes.InstructionFor(Dev)], int.MaxValue, Ct);

    private static string ReasonOf(Message skipped) =>
        JsonDocument.Parse(skipped.Payload).RootElement.GetProperty("reason").GetString()!;

    private static string? DetailReason(TenantEvent row) =>
        JsonDocument.Parse(row.Detail!).RootElement.GetProperty("reason").GetString();

    /// <summary>The skip and its tenant row for trigger <paramref name="id"/>, sourced <paramref name="source"/>,
    /// and nothing delivered to Dev.</summary>
    private async Task AssertSkippedWaitingAsync(string id, string source)
    {
        var skipped = await AwaitRowAsync([MessageTypes.ScheduleSkipped], m => m.Source == source, "the skip");
        Assert.Equal(Waiting, ReasonOf(skipped));

        // The pump writes the tenant row after the skip row: wait for it rather than race it.
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while ((await TenantRowsAsync(TenantActions.ScheduleSkipped, id)).Count == 0 && DateTime.UtcNow < deadline) await Task.Delay(50, Ct);

        var tenant = Assert.Single(await TenantRowsAsync(TenantActions.ScheduleSkipped, id));
        Assert.Equal(Waiting, DetailReason(tenant));
        Assert.Equal("skipped", (await Services.GetRequiredService<ITriggerStore>().FindAsync(id, Ct))!.LastOutcome);

        await Task.Delay(300, Ct);
        Assert.DoesNotContain(await InstructionsForDevAsync(), m => m.Source == source);
        Assert.Equal(0, _agents.RunsFor(Dev));
    }

    [Fact]
    public async Task A_blocked_teams_schedule_skips_its_fire_waiting_for_the_input_with_a_tenant_row()
    {
        var id = await DailyAsync();
        var due = (await Services.GetRequiredService<ITriggerStore>().FindAsync(id, Ct))!.NextDueAt!.Value;

        await Services.GetRequiredService<TriggerSweep>().FireDueAsync(due, Ct);

        await AssertSkippedWaitingAsync(id, $"schedule:{id}");
    }

    [Fact]
    public async Task A_blocked_teams_event_trigger_skips_the_event_waiting_for_the_input_with_a_tenant_row()
    {
        var id = await CreateAsync(new
        {
            name = "New posting",
            kind = "event",
            container = "Dev",
            eventType = "site.action",
            instruction = "A posting.",
            wakeManager = "never",
        });

        await Log.AppendAsync(
            SiteService.ActionMessage(_team, "tracker", "apply", JsonNode.Parse("""{"id":"j1"}"""), Email, DateTimeOffset.UtcNow), Ct);

        await AssertSkippedWaitingAsync(id, $"trigger:{id}");
    }

    [Fact]
    public async Task A_blocked_teams_folder_trigger_skips_the_change_waiting_for_the_input_with_a_tenant_row()
    {
        Directory.CreateDirectory(Path.Combine(Services.GetRequiredService<TeamDocuments>().EnsureFor(_team), "Inbox"));
        var id = await CreateAsync(new
        {
            name = "Inbox changed",
            kind = "folderChange",
            container = "Dev",
            watchRoot = "documents",
            watchPath = "Inbox",
            instruction = "The inbox changed.",
            wakeManager = "never",
        });

        await Services.GetRequiredService<FolderWatch>().AnnounceAsync(_team, "Inbox", ["Inbox/a.md"], "person", Ct);

        await AssertSkippedWaitingAsync(id, $"trigger:{id}");
    }

    [Fact]
    public async Task A_blocked_teams_run_now_is_skipped_and_says_it_waits_for_the_input()
    {
        var id = await DailyAsync();

        var run = await RunNowAsync(id);

        Assert.Equal("skipped", run.GetProperty("outcome").GetString());
        Assert.Equal(Waiting, run.GetProperty("reason").GetString());
        var skipped = Assert.Single(await Log.ReadAfterAsync(0, [MessageTypes.ScheduleSkipped], int.MaxValue, Ct), m => m.Source == $"schedule:{id}");
        Assert.Equal(Waiting, ReasonOf(skipped));
        Assert.Equal(Waiting, DetailReason(Assert.Single(await TenantRowsAsync(TenantActions.ScheduleSkipped, id))));
        Assert.Empty(await InstructionsForDevAsync());
    }

    [Fact]
    public async Task Providing_the_input_unblocks_the_team_at_once_and_the_next_fire_runs()
    {
        var id = await DailyAsync();
        Assert.Equal("skipped", (await RunNowAsync(id)).GetProperty("outcome").GetString());

        await UploadResumeAsync();

        var solution = await _person.GetFromJsonAsync<JsonElement>($"/api/teams/{_team}/solution", Ct);
        Assert.Empty(solution.GetProperty("missing").EnumerateArray());

        var run = await RunNowAsync(id);
        Assert.Equal("fired", run.GetProperty("outcome").GetString());
        await AwaitRowAsync([MessageTypes.Completed], m => m.Source == Dev.ToString(), "Dev's run");
        Assert.Equal(1, _agents.RunsFor(Dev));
    }

    [Fact]
    public async Task A_members_failed_or_blocked_mark_from_the_wait_clears_when_the_input_arrives()
    {
        // Dev is told directly, and its run fails for want of the resume.
        _agents.Behaviour = _ => Task.FromResult(new AgentResult(1, "CANNOT REACH: the Resume folder is empty"));
        var host = Services.GetRequiredService<ContainerHost>();

        await Log.AppendAsync(new NewMessage(
            MessageTypes.InstructionFor(Dev), WakeManagerPolicy.InstructionPayload("write a letter", "never"), "person"), Ct);
        await AwaitRowAsync([MessageTypes.Failed], m => m.Source == Dev.ToString(), "Dev's failed run");

        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (host.Find(Dev)!.Snapshot().Failed is null && DateTime.UtcNow < deadline) await Task.Delay(50, Ct);
        Assert.NotNull(host.Find(Dev)!.Snapshot().Failed);
        host.Find(Dev)!.MarkBlocked("CANNOT REACH: the Resume folder is empty");

        await UploadResumeAsync();

        var card = host.Find(Dev)!.Snapshot();
        Assert.Null(card.Failed);
        Assert.Null(card.Blocked);
    }
}
