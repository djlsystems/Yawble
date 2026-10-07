using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Harness.Containers;
using Harness.Contracts;
using Harness.Host;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests.Host;

/// <summary>
/// ARCHIVING A TEAM: kept, hidden, doing no work, and a template to clone from. Archive and delete
/// are refused unless the team is quiet, member by member; an archived team is paused, every trigger
/// of it skips with "team archived" and a tenant row, and a tell, a dispatch, a planned card and a
/// site action are refused with a sentence naming Unarchive; unarchive leaves it paused until Resume;
/// a clone of it is active; and the flag and its tenant row land together or not at all.
///
/// On the real Host with a fake agent member (Dev) and the background runner off, so nothing fires
/// but what a test asks for.
/// </summary>
public sealed class TeamArchiveTests : IAsyncLifetime
{
    private const string Email = "person@example.test";
    private const string Password = "correct horse battery";

    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), $"harness-archive-{Guid.NewGuid():N}");
    private readonly FakeAgent _agents = new();
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _person = null!;
    private string _team = "";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IServiceProvider Services => _factory.Services;

    private IMessageLog Log => Services.GetRequiredService<IMessageLog>();

    private TeamRegistry Teams => Services.GetRequiredService<TeamRegistry>();

    private ContainerId Dev => new(_team, "Dev");

    private ContainerId Manager => new(_team, TeamRegistry.DefaultManagerName);

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_dataRoot);

        _agents.Behaviour = _ => Task.FromResult(new AgentResult(0, "done", Usage: new InvocationUsage(1000, 500, "test")));

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host => host
            .UseSetting("DataRoot", _dataRoot)
            .UseSetting("Logging:LogLevel:Default", "Warning")
            .UseSetting("ScheduleRunnerEnabled", "false")
            .ConfigureTestServices(services => services.AddSingleton<IAgentRunner>(_agents)));

        _team = (await Teams.CreateAsync("Shelf", "claude-headless", memberAgent: "claude-headless", ct: Ct)).Id;

        await Services.GetRequiredService<IUserStore>().CreateAsync(Email, Password);
        _person = _factory.CreateClient();
        (await _person.PostAsJsonAsync("/api/auth/login", new { email = Email, password = Password }, Ct)).EnsureSuccessStatusCode();

        var dev = await _person.PostAsJsonAsync($"/api/teams/{_team}/containers", new { name = "Dev", agent = "claude-headless" }, Ct);
        Assert.Equal(HttpStatusCode.OK, dev.StatusCode);
    }

    public async ValueTask DisposeAsync()
    {
        _person.Dispose();
        await _factory.DisposeAsync();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataRoot, recursive: true); }
        catch (IOException) { }
    }

    // ---- quiet first ----

    [Fact]
    public async Task Archive_and_delete_are_refused_naming_the_member_while_it_runs()
    {
        var release = new TaskCompletionSource();
        _agents.Behaviour = async _ =>
        {
            await release.Task;
            return new AgentResult(0, "done", Usage: new InvocationUsage(1000, 500, "test"));
        };

        try
        {
            await TellAsync("Dev", "take your time");
            await UntilAsync(() => Services.GetRequiredService<ContainerHost>().Find(Dev)!.Snapshot().State == ContainerState.Running, "Dev running");

            // Running is set a moment before its delivery is marked started; wait for both.
            await UntilAsync(async () => (await Services.GetRequiredService<IPendingDeliveries>().ForTeamAsync(_team, Ct))
                .Any(row => row.Subscriber == Dev.ToString() && row.Started), "Dev's delivery started");

            var sentence = $"'{Teams.LabelFor(_team)}' is not quiet: Dev is running. "
                + "Wait for that work to finish, or stop it, then try again. Nothing was changed.";

            var archive = await _person.PostAsync($"/api/teams/{_team}/archive", null, Ct);
            Assert.Equal(HttpStatusCode.Conflict, archive.StatusCode);
            Assert.Equal(sentence, await ErrorAsync(archive));

            var delete = await _person.DeleteAsync($"/api/teams/{_team}", Ct);
            Assert.Equal(HttpStatusCode.Conflict, delete.StatusCode);
            Assert.Equal(sentence, await ErrorAsync(delete));

            var check = await _person.GetFromJsonAsync<JsonElement>($"/api/teams/{_team}/archive-check", Ct);
            Assert.False(check.GetProperty("quiet").GetBoolean());
            Assert.Equal(sentence, check.GetProperty("reason").GetString());

            // Nothing changed: not archived, not paused, nothing deleted, no row.
            Assert.False(Teams.IsArchived(_team));
            Assert.False(Teams.IsPaused(_team));
            Assert.Equal(_team, Teams.ExistingName(_team));
            Assert.Empty(await TenantRowsAsync(TenantActions.TeamArchived));
            Assert.Empty(await TenantRowsAsync(TenantActions.TeamDeleting));
        }
        finally
        {
            release.TrySetResult();
        }
    }

    [Fact]
    public async Task Archive_and_delete_are_refused_naming_the_member_while_instructions_are_queued()
    {
        // Two deliveries the Manager has accepted and not started.
        var pending = Services.GetRequiredService<IPendingDeliveries>();
        var first = await Log.AppendAsync(new NewMessage("test.queued", "{}", "test"), Ct);
        var second = await Log.AppendAsync(new NewMessage("test.queued", "{}", "test"), Ct);
        await pending.AddAsync(Manager, first.Seq, Ct);
        await pending.AddAsync(Manager, second.Seq, Ct);

        var sentence = $"'{Teams.LabelFor(_team)}' is not quiet: Manager has 2 instructions queued. "
            + "Wait for that work to finish, or stop it, then try again. Nothing was changed.";

        var archive = await _person.PostAsync($"/api/teams/{_team}/archive", null, Ct);
        Assert.Equal(HttpStatusCode.Conflict, archive.StatusCode);
        Assert.Equal(sentence, await ErrorAsync(archive));

        var delete = await _person.DeleteAsync($"/api/teams/{_team}", Ct);
        Assert.Equal(HttpStatusCode.Conflict, delete.StatusCode);
        Assert.Equal(sentence, await ErrorAsync(delete));

        Assert.False(Teams.IsArchived(_team));
        Assert.Equal(_team, Teams.ExistingName(_team));

        // Once they are gone the team is quiet, and the archive goes through.
        await pending.RemoveAsync(Manager, first.Seq, Ct);
        await pending.RemoveAsync(Manager, second.Seq, Ct);
        Assert.Equal(HttpStatusCode.OK, (await _person.PostAsync($"/api/teams/{_team}/archive", null, Ct)).StatusCode);
    }

    [Fact]
    public async Task Open_workflows_do_not_block_and_the_archive_check_lists_them()
    {
        await TellAsync("Dev", "write the report", subject: "Write the report");
        var root = Assert.Single(await Log.ReadAfterAsync(0, [MessageTypes.InstructionFor(Dev)], int.MaxValue, Ct));
        await QuietAsync();

        var check = await _person.GetFromJsonAsync<JsonElement>($"/api/teams/{_team}/archive-check", Ct);
        Assert.True(check.GetProperty("quiet").GetBoolean());
        Assert.Equal(JsonValueKind.Null, check.GetProperty("reason").ValueKind);
        var open = Assert.Single(check.GetProperty("openWorkflows").EnumerateArray());
        Assert.Equal(root.Seq, open.GetProperty("workflow").GetInt64());
        Assert.Equal("Write the report", open.GetProperty("title").GetString());

        var archived = await _person.PostAsync($"/api/teams/{_team}/archive", null, Ct);
        Assert.Equal(HttpStatusCode.OK, archived.StatusCode);

        // Archived, paused, with its row; the overview carries it; the workflow is still there.
        Assert.True(Teams.IsArchived(_team));
        Assert.True(Teams.IsPaused(_team));
        var row = Assert.Single(await TenantRowsAsync(TenantActions.TeamArchived));
        Assert.Equal(Email, row.ActorEmail);

        var team = await OverviewTeamAsync();
        Assert.True(team.GetProperty("archived").GetBoolean());
        Assert.Equal(Email, team.GetProperty("archivedBy").GetString());
        Assert.Equal(JsonValueKind.String, team.GetProperty("archivedAt").ValueKind);
        Assert.True(team.GetProperty("paused").GetBoolean());

        var after = await _person.GetFromJsonAsync<JsonElement>($"/api/teams/{_team}/archive-check", Ct);
        Assert.Single(after.GetProperty("openWorkflows").EnumerateArray());

        // Archiving it again is refused with a plain sentence.
        var again = await _person.PostAsync($"/api/teams/{_team}/archive", null, Ct);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal(TeamArchive.AlreadyArchived(Teams.LabelFor(_team)), await ErrorAsync(again));
    }

    [Fact]
    public async Task A_quiet_archived_team_is_deleted_like_an_active_one()
    {
        await ArchiveAsync();

        var deleted = await _person.DeleteAsync($"/api/teams/{_team}", Ct);

        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        Assert.Null(Teams.ExistingName(_team));
        Assert.Single(await TenantRowsAsync(TenantActions.TeamDeleting));
        Assert.Single(await TenantRowsAsync(TenantActions.TeamDeleted));
    }

    // ---- an archived team does no work ----

    [Fact]
    public async Task The_pump_delivers_nothing_to_an_archived_team_and_unarchive_leaves_it_paused_until_resume()
    {
        await ArchiveAsync();

        // Work reaching it by a road no route guards: queued, never run.
        await Log.AppendAsync(new NewMessage(
            MessageTypes.InstructionFor(Dev), WakeManagerPolicy.InstructionPayload("do it later", "never"), "person"), Ct);
        await Services.GetRequiredService<ContainerHost>().PumpOnceAsync(Ct);
        await Task.Delay(300, Ct);
        Assert.Equal(0, _agents.RunsFor(Dev));
        Assert.DoesNotContain(Services.GetRequiredService<WipLedger>().View().Waiting, hold => hold.Team == _team);

        var unarchived = await _person.PostAsync($"/api/teams/{_team}/unarchive", null, Ct);
        Assert.Equal(HttpStatusCode.OK, unarchived.StatusCode);
        Assert.False(Teams.IsArchived(_team));
        Assert.True(Teams.IsPaused(_team));
        Assert.Single(await TenantRowsAsync(TenantActions.TeamUnarchived));
        var team = await OverviewTeamAsync();
        Assert.False(team.GetProperty("archived").GetBoolean());
        Assert.Equal(JsonValueKind.Null, team.GetProperty("archivedAt").ValueKind);

        await Task.Delay(300, Ct);
        Assert.Equal(0, _agents.RunsFor(Dev));

        // Unarchiving an active team is refused with a plain sentence.
        var again = await _person.PostAsync($"/api/teams/{_team}/unarchive", null, Ct);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal(TeamArchive.NotArchived(Teams.LabelFor(_team)), await ErrorAsync(again));

        // Resume runs the queued work.
        Assert.Equal(HttpStatusCode.NoContent, (await _person.PostAsync($"/api/teams/{_team}/resume", null, Ct)).StatusCode);
        await AwaitRowAsync([MessageTypes.Completed], m => m.Source == Dev.ToString(), "Dev's run after Resume");
        Assert.Equal(1, _agents.RunsFor(Dev));
    }

    [Fact]
    public async Task An_archived_teams_schedule_skips_its_fire_with_team_archived_and_a_tenant_row()
    {
        var id = await DailyAsync();
        var due = (await Services.GetRequiredService<ITriggerStore>().FindAsync(id, Ct))!.NextDueAt!.Value;
        await ArchiveAsync();

        await Services.GetRequiredService<TriggerSweep>().FireDueAsync(due, Ct);

        await AssertSkippedArchivedAsync(id, $"schedule:{id}");
    }

    [Fact]
    public async Task An_archived_teams_event_trigger_skips_the_event_with_team_archived_and_a_tenant_row()
    {
        var id = await CreateTriggerAsync(new
        {
            name = "New posting",
            kind = "event",
            container = "Dev",
            eventType = "site.action",
            instruction = "A posting.",
            wakeManager = "never",
        });
        await ArchiveAsync();

        await Log.AppendAsync(
            SiteService.ActionMessage(_team, "tracker", "apply", JsonNode.Parse("""{"id":"j1"}"""), Email, DateTimeOffset.UtcNow), Ct);

        await AssertSkippedArchivedAsync(id, $"trigger:{id}");

        // Skipped, not held for later: Resume after Unarchive fires nothing for it.
        await UnarchiveAndResumeAsync();
        await Task.Delay(300, Ct);
        Assert.DoesNotContain(await InstructionsForDevAsync(), m => m.Source == $"trigger:{id}");
    }

    [Fact]
    public async Task An_archived_teams_folder_trigger_skips_the_change_with_team_archived_and_a_tenant_row()
    {
        Directory.CreateDirectory(Path.Combine(Services.GetRequiredService<TeamDocuments>().EnsureFor(_team), "Inbox"));
        var id = await CreateTriggerAsync(new
        {
            name = "Inbox changed",
            kind = "folderChange",
            container = "Dev",
            watchRoot = "documents",
            watchPath = "Inbox",
            instruction = "The inbox changed.",
            wakeManager = "never",
        });
        await ArchiveAsync();

        await Services.GetRequiredService<FolderWatch>().AnnounceAsync(_team, "Inbox", ["Inbox/a.md"], "person", Ct);

        await AssertSkippedArchivedAsync(id, $"trigger:{id}");
    }

    [Fact]
    public async Task An_archived_teams_run_now_is_skipped_with_team_archived()
    {
        var id = await DailyAsync();
        await ArchiveAsync();

        var response = await _person.PostAsync($"/api/teams/{_team}/triggers/{id}/run", null, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var run = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

        Assert.Equal("skipped", run.GetProperty("outcome").GetString());
        Assert.Equal(MessageTypes.ScheduleSkippedArchivedReason, run.GetProperty("reason").GetString());
        var skipped = Assert.Single(await Log.ReadAfterAsync(0, [MessageTypes.ScheduleSkipped], int.MaxValue, Ct), m => m.Source == $"schedule:{id}");
        Assert.Equal("team archived", ReasonOf(skipped));
        Assert.Equal("team archived", DetailReason(Assert.Single(await TriggerRowsAsync(TenantActions.ScheduleSkipped, id))));
        Assert.Empty(await InstructionsForDevAsync());
    }

    [Fact]
    public async Task Tell_dispatch_and_card_creation_are_refused_with_a_sentence_naming_archiving_and_unarchive()
    {
        var item = await Services.GetRequiredService<IBacklogStore>().CreateAsync(null, "An item", "body", Email, Ct);
        await ArchiveAsync();
        var sentence = TeamArchive.Refusal(Teams.LabelFor(_team));
        Assert.Contains("archived", sentence, StringComparison.Ordinal);
        Assert.Contains("Unarchive", sentence, StringComparison.Ordinal);
        var before = await HeadAsync();

        var tell = await _person.PostAsJsonAsync($"/api/teams/{_team}/containers/Dev/tell", new { instruction = "work" }, Ct);
        Assert.Equal(HttpStatusCode.Conflict, tell.StatusCode);
        Assert.Equal(sentence, await ErrorAsync(tell));

        var dispatch = await _person.PostAsync($"/api/teams/{_team}/backlog/{item.Id}/dispatch", null, Ct);
        Assert.Equal(HttpStatusCode.Conflict, dispatch.StatusCode);
        Assert.Equal(sentence, await ErrorAsync(dispatch));
        Assert.Empty(await Services.GetRequiredService<IBacklogStore>().DispatchesAsync(item.Id, Ct));

        var card = await _person.PostAsJsonAsync($"/api/teams/{_team}/kanban/plan", new { title = "A card" }, Ct);
        Assert.Equal(HttpStatusCode.Conflict, card.StatusCode);
        Assert.Equal(sentence, await ErrorAsync(card));

        // Nothing was appended for any of them.
        Assert.Equal(before, await HeadAsync());
    }

    [Fact]
    public async Task A_site_action_is_refused_with_the_sentence_and_the_site_stays_readable()
    {
        var sites = Services.GetRequiredService<SiteService>();
        var actor = SiteActor.Person("person-1", Email);
        Assert.True((await sites.CreateAsync(_team, "tracker", actor, Ct)).Ok);
        var folder = Path.Combine(Services.GetRequiredService<TeamDocuments>().EnsureFor(_team), "site-src");
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, "index.html"), "<p>jobs</p>", Ct);
        Assert.True((await sites.PublishAsync(_team, "tracker", folder, actor, Ct)).Ok);

        await ArchiveAsync();

        var capability = Services.GetRequiredService<SiteCapability>()
            .Issue(_team, "tracker", "person-1", Email, DateTimeOffset.UtcNow.AddMinutes(10));
        var page = $"/sites/{_team}/tracker/_c/{capability}/";
        using var sandbox = _factory.CreateClient();
        var before = await HeadAsync();

        var action = await sandbox.PostAsync($"{page}_api/actions/done", new StringContent("{}", Encoding.UTF8, "text/plain"), Ct);
        Assert.Equal(HttpStatusCode.Conflict, action.StatusCode);
        Assert.Equal(TeamArchive.Refusal(Teams.LabelFor(_team)), await ErrorAsync(action));
        Assert.Equal(before, await HeadAsync());

        // Still live and readable: its page and its data.
        var file = await sandbox.GetAsync($"{page}index.html", Ct);
        Assert.Equal(HttpStatusCode.OK, file.StatusCode);
        Assert.Equal("<p>jobs</p>", await file.Content.ReadAsStringAsync(Ct));
        Assert.Equal(HttpStatusCode.OK, (await sandbox.GetAsync($"{page}_api/data/jobs", Ct)).StatusCode);
    }

    // ---- clone ----

    [Fact]
    public async Task A_clone_of_an_archived_team_is_active_and_unpaused()
    {
        await ArchiveAsync();

        var cloned = await _person.PostAsJsonAsync($"/api/teams/{_team}/clone", new { name = "Shelf Copy" }, Ct);
        Assert.Equal(HttpStatusCode.Created, cloned.StatusCode);
        var id = (await cloned.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("team").GetProperty("id").GetString()!;

        Assert.False(Teams.IsArchived(id));
        Assert.False(Teams.IsPaused(id));
        Assert.Contains(Teams.ContainerIdsOf(id), c => c.Name == "Dev");
        var row = Assert.Single(await Services.GetRequiredService<ITeamStore>().TeamsAsync(Ct), t => t.Id == id);
        Assert.Null(row.ArchivedAt);
        Assert.False(row.Paused);

        // The source is still archived.
        Assert.True(Teams.IsArchived(_team));
    }

    // ---- no row, no change ----

    [Fact]
    public async Task Archive_and_unarchive_change_nothing_when_their_tenant_row_cannot_be_written()
    {
        await RefuseTenantRowAsync(TenantActions.TeamArchived);

        var refused = await _person.PostAsync($"/api/teams/{_team}/archive", null, Ct);
        Assert.Equal(HttpStatusCode.InternalServerError, refused.StatusCode);
        Assert.StartsWith($"'{Teams.LabelFor(_team)}' was not archived: its tenant log row could not be written", await ErrorAsync(refused), StringComparison.Ordinal);
        Assert.False(Teams.IsArchived(_team));
        Assert.False(Teams.IsPaused(_team));
        var stored = Assert.Single(await Services.GetRequiredService<ITeamStore>().TeamsAsync(Ct), t => t.Id == _team);
        Assert.Null(stored.ArchivedAt);
        Assert.False(stored.Paused);

        await AllowTenantRowsAsync();
        await ArchiveAsync();
        await RefuseTenantRowAsync(TenantActions.TeamUnarchived);

        var unrefused = await _person.PostAsync($"/api/teams/{_team}/unarchive", null, Ct);
        Assert.Equal(HttpStatusCode.InternalServerError, unrefused.StatusCode);
        Assert.True(Teams.IsArchived(_team));
        Assert.NotNull(Assert.Single(await Services.GetRequiredService<ITeamStore>().TeamsAsync(Ct), t => t.Id == _team).ArchivedAt);
    }

    // ---- readers ----

    [Fact]
    public async Task The_kanban_board_and_the_rollup_leave_an_archived_teams_cards_out_and_offer_active_teams_only()
    {
        var other = (await Teams.CreateAsync("Active Desk", "claude-headless", memberAgent: "claude-headless", ct: Ct)).Id;
        await TellAsync("Dev", "card on the shelf", subject: "Shelf card");
        (await _person.PostAsJsonAsync($"/api/teams/{other}/containers/{TeamRegistry.DefaultManagerName}/tell", new { instruction = "card on the desk", subject = "Desk card" }, Ct)).EnsureSuccessStatusCode();
        await QuietAsync();
        await UntilAsync(async () => (await BoardTeamsAsync("/api/kanban/board")).Contains(_team), "the shelf card on the board");

        await ArchiveAsync();

        var board = await BoardTeamsAsync("/api/kanban/board");
        Assert.DoesNotContain(_team, board);
        Assert.Contains(other, board);

        // Named in the filter, it still contributes nothing.
        Assert.Empty(await BoardTeamsAsync($"/api/kanban/board?team={_team}"));

        var rollup = await _person.GetFromJsonAsync<JsonElement>("/api/teams/rollup", Ct);
        var rows = rollup.EnumerateObject().First(p => p.Value.ValueKind == JsonValueKind.Array).Value.EnumerateArray()
            .Select(r => r.GetProperty("team").GetString()).ToList();
        Assert.DoesNotContain(_team, rows);
        Assert.Contains(other, rows);

        // The overview still lists it, marked, for the Teams list's Show archived.
        Assert.True((await OverviewTeamAsync()).GetProperty("archived").GetBoolean());
    }

    [Fact]
    public async Task The_status_tool_marks_an_archived_team_archived()
    {
        await ArchiveAsync();

        var overview = "HTTP 200" + Environment.NewLine + await _person.GetStringAsync("/api/overview", Ct);
        var lines = PlatformMcpTools.ArchivedLines(overview, null)!;
        Assert.Contains($"({_team}): ARCHIVED", lines, StringComparison.Ordinal);
        Assert.Contains("unarchived", lines, StringComparison.Ordinal);
        Assert.NotNull(PlatformMcpTools.ArchivedLines(overview, _team));

        await UnarchiveAndResumeAsync();
        var active = "HTTP 200" + Environment.NewLine + await _person.GetStringAsync("/api/overview", Ct);
        Assert.Null(PlatformMcpTools.ArchivedLines(active, null));
    }

    // ---- helpers ----

    private async Task ArchiveAsync()
    {
        var response = await _person.PostAsync($"/api/teams/{_team}/archive", null, Ct);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        Assert.True(Teams.IsArchived(_team));
    }

    private async Task UnarchiveAndResumeAsync()
    {
        Assert.Equal(HttpStatusCode.OK, (await _person.PostAsync($"/api/teams/{_team}/unarchive", null, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await _person.PostAsync($"/api/teams/{_team}/resume", null, Ct)).StatusCode);
    }

    private async Task TellAsync(string member, string instruction, string? subject = null)
    {
        var response = await _person.PostAsJsonAsync(
            $"/api/teams/{_team}/containers/{member}/tell", new { instruction, subject }, Ct);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
    }

    /// <summary>Until every member has finished and nothing is queued.</summary>
    private Task QuietAsync() =>
        UntilAsync(async () => await Services.GetRequiredService<TeamArchive>().NotQuietAsync(_team, Ct) is null
            && await Log.ReadAfterAsync(0, [MessageTypes.Completed], int.MaxValue, Ct) is { Count: > 0 }, "the team quiet");

    private async Task<JsonElement> OverviewTeamAsync()
    {
        var overview = await _person.GetFromJsonAsync<JsonElement>("/api/overview", Ct);
        return overview.GetProperty("teams").EnumerateArray().Single(t => t.GetProperty("id").GetString() == _team);
    }

    private async Task<IReadOnlyList<string>> BoardTeamsAsync(string path)
    {
        var board = await _person.GetFromJsonAsync<JsonElement>(path, Ct);
        return [.. board.GetProperty("cards").EnumerateArray().Select(c => c.GetProperty("team").GetString()!).Distinct()];
    }

    private async Task<string> CreateTriggerAsync(object body)
    {
        var created = await _person.PostAsJsonAsync($"/api/teams/{_team}/triggers", body, Ct);
        Assert.True(created.StatusCode == HttpStatusCode.Created, await created.Content.ReadAsStringAsync(Ct));
        return (await created.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetString()!;
    }

    /// <summary>Daily at 03:00 UTC on Dev: never due while a test runs unless a test says so.</summary>
    private Task<string> DailyAsync() => CreateTriggerAsync(new
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

    /// <summary>The skip and its tenant row for trigger <paramref name="id"/>, sourced <paramref name="source"/>,
    /// with the reason "team archived", and nothing delivered to Dev.</summary>
    private async Task AssertSkippedArchivedAsync(string id, string source)
    {
        var skipped = await AwaitRowAsync([MessageTypes.ScheduleSkipped], m => m.Source == source, "the skip");
        Assert.Equal("team archived", ReasonOf(skipped));

        await UntilAsync(async () => (await TriggerRowsAsync(TenantActions.ScheduleSkipped, id)).Count > 0, "the tenant row");
        Assert.Equal("team archived", DetailReason(Assert.Single(await TriggerRowsAsync(TenantActions.ScheduleSkipped, id))));
        Assert.Equal("skipped", (await Services.GetRequiredService<ITriggerStore>().FindAsync(id, Ct))!.LastOutcome);

        await Task.Delay(300, Ct);
        Assert.DoesNotContain(await InstructionsForDevAsync(), m => m.Source == source);
        Assert.Equal(0, _agents.RunsFor(Dev));
    }

    private async Task<Message> AwaitRowAsync(IReadOnlyCollection<string> types, Func<Message, bool> match, string what)
    {
        Message? found = null;
        await UntilAsync(async () =>
        {
            await Services.GetRequiredService<ContainerHost>().PumpOnceAsync(Ct);
            found = (await Log.ReadAfterAsync(0, types, int.MaxValue, Ct)).FirstOrDefault(match);
            return found is not null;
        }, what);
        return found!;
    }

    private static Task UntilAsync(Func<bool> condition, string what) => UntilAsync(() => Task.FromResult(condition()), what);

    private static async Task UntilAsync(Func<Task<bool>> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"Never: {what}.");
            await Task.Delay(50, Ct);
        }
    }

    private async Task<long> HeadAsync() =>
        (await Log.ReadRangeAsync(0, int.MaxValue)).Select(m => m.Seq).DefaultIfEmpty(0).Max();

    private async Task<IReadOnlyList<TenantEvent>> TenantRowsAsync(string action) =>
        [.. (await Services.GetRequiredService<ITenantLog>().ReadAsync(take: 10_000, ct: Ct)).Events
            .Where(e => e.Action == action && e.Subject == _team)];

    private async Task<IReadOnlyList<TenantEvent>> TriggerRowsAsync(string action, string id) =>
        [.. (await Services.GetRequiredService<ITenantLog>().ReadAsync(take: 10_000, ct: Ct)).Events
            .Where(e => e.Action == action && e.Subject == id)];

    private Task<IReadOnlyList<Message>> InstructionsForDevAsync() =>
        Log.ReadAfterAsync(0, [MessageTypes.InstructionFor(Dev)], int.MaxValue, Ct);

    private static string ReasonOf(Message skipped) =>
        JsonDocument.Parse(skipped.Payload).RootElement.GetProperty("reason").GetString()!;

    private static string? DetailReason(TenantEvent row) =>
        JsonDocument.Parse(row.Detail!).RootElement.GetProperty("reason").GetString();

    private static async Task<string> ErrorAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync(Ct);
        using var body = JsonDocument.Parse(text);
        return body.RootElement.GetProperty("error").GetString()!;
    }

    private string Database => Path.Combine(_dataRoot, "messages.db");

    /// <summary>A tenant row of <paramref name="action"/> cannot be written from now on.</summary>
    private Task RefuseTenantRowAsync(string action) =>
        ExecuteAsync(
            "DROP TRIGGER IF EXISTS refuse_row; "
            + $"CREATE TRIGGER refuse_row BEFORE INSERT ON tenant_events WHEN NEW.action = '{action}' "
            + "BEGIN SELECT RAISE(ABORT, 'refused by the test'); END");

    private Task AllowTenantRowsAsync() => ExecuteAsync("DROP TRIGGER IF EXISTS refuse_row");

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={Database}");
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(Ct);
    }
}
