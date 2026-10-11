using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Harness.Contracts;
using Harness.Host;
using Harness.Host.Auth;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests.Host;

/// <summary>
/// WHERE A TEAM'S ACTIVITY VIEW BEGINS, on the real Host with a fake agent whose every run measures
/// 1000 in and 500 out. <c>PUT /api/teams/{team}/activity-from</c> is a person's: "now" narrows the
/// activity read and the token runs to what came after it, null restores them, a workflow rooted
/// before it is drawn from it, and nothing else reads it - spend, caps, recent cost, outcomes and the
/// ledger export read the same, and no ledger, hold or log row changes. The change and its tenant
/// row land together; a clone does not copy it and a deletion forgets it.
/// </summary>
public sealed class TeamActivityFromTests(TeamActivityFromTests.Bed bed) : IClassFixture<TeamActivityFromTests.Bed>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Starting_from_now_narrows_the_activity_and_token_runs_and_clearing_restores_them()
    {
        var team = await bed.TeamAsync("From narrows", "Dev");

        var first = await bed.TellAndSettleAsync(team, "Dev", "before");
        await bed.DeclareAsync(team, first);
        await bed.QuietAsync(team);

        var whole = await bed.ActivityAsync(team);
        var wholeRuns = await bed.TokenRunsAsync(team);
        Assert.Equal(first.OccurredAt, whole.GetProperty("from").GetDateTimeOffset());
        Assert.False(whole.TryGetProperty("activityFrom", out _));
        Assert.NotEmpty(wholeRuns.GetProperty("runs").EnumerateArray());

        var set = await bed.SetAsync(team, "now");
        Assert.Equal(HttpStatusCode.OK, set.StatusCode);
        var answer = await JsonAsync(set);
        var at = answer.GetProperty("activityFrom").GetProperty("at").GetDateTimeOffset();
        Assert.Equal(Bed.Email, answer.GetProperty("activityFrom").GetProperty("by").GetString());
        Assert.True(at > first.OccurredAt);

        // Nothing has happened since: no workflow at or after it, and no run.
        var empty = await bed.ActivityAsync(team);
        Assert.Equal("none", empty.GetProperty("window").GetString());
        Assert.Equal(at, empty.GetProperty("activityFrom").GetProperty("at").GetDateTimeOffset());
        Assert.Equal(Bed.Email, empty.GetProperty("activityFrom").GetProperty("by").GetString());
        var emptyRuns = await bed.TokenRunsAsync(team);
        Assert.Equal("none", emptyRuns.GetProperty("window").GetString());
        Assert.Empty(emptyRuns.GetProperty("runs").EnumerateArray());
        Assert.Equal(at, emptyRuns.GetProperty("activityFrom").GetProperty("at").GetDateTimeOffset());

        var second = await bed.TellAndSettleAsync(team, "Dev", "after");
        await bed.DeclareAsync(team, second);
        await bed.QuietAsync(team);

        // The window starts at the root of the earliest workflow after it; only that one's runs show.
        var narrowed = await bed.ActivityAsync(team);
        Assert.Equal("workflows", narrowed.GetProperty("window").GetString());
        Assert.Equal(second.OccurredAt, narrowed.GetProperty("from").GetDateTimeOffset());
        Assert.Equal([second.Seq], RunningWorkflows(narrowed, "Dev"));
        Assert.All(Spans(narrowed, "Dev"), span => Assert.True(span.GetProperty("from").GetDateTimeOffset() >= at));

        var narrowedRuns = await bed.TokenRunsAsync(team);
        Assert.Equal(second.OccurredAt, narrowedRuns.GetProperty("from").GetDateTimeOffset());
        Assert.All(narrowedRuns.GetProperty("runs").EnumerateArray(), run => Assert.True(run.GetProperty("endedAt").GetDateTimeOffset() > at));

        // Cleared: the whole history again, and no activityFrom.
        var cleared = await bed.SetAsync(team, null);
        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
        Assert.Equal(JsonValueKind.Null, (await JsonAsync(cleared)).GetProperty("activityFrom").ValueKind);

        var restored = await bed.ActivityAsync(team);
        Assert.Equal(first.OccurredAt, restored.GetProperty("from").GetDateTimeOffset());
        Assert.Equal([first.Seq, second.Seq], RunningWorkflows(restored, "Dev"));
        Assert.False(restored.TryGetProperty("activityFrom", out _));

        var restoredRuns = await bed.TokenRunsAsync(team);
        Assert.Equal(first.OccurredAt, restoredRuns.GetProperty("from").GetDateTimeOffset());
        Assert.Equal(Ends(wholeRuns), Ends(restoredRuns).Where(end => end <= at));
        Assert.Equal(Ends(narrowedRuns), Ends(restoredRuns).Where(end => end > at));
        Assert.False(restoredRuns.TryGetProperty("activityFrom", out _));

        Assert.Equal(
            [TenantActions.TeamActivityFromSet, TenantActions.TeamActivityFromCleared],
            (await bed.TenantRowsAsync(team)).Select(row => row.Action));
    }

    [Fact]
    public async Task A_workflow_rooted_before_it_and_still_running_is_drawn_from_it()
    {
        var team = await bed.TeamAsync("From running", "Dev");
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bed.Hold(team, "Dev", hold);

        Message root;
        DateTimeOffset at;

        try
        {
            root = await bed.TellAsync(team, "Dev", "long work");
            var started = await bed.AwaitRowAsync(
                m => m.Type == MessageTypes.Started && m.Source == $"{team}/Dev" && m.CorrelationId == root.Seq, "Dev's start");

            var set = await bed.SetAsync(team, "now");
            Assert.Equal(HttpStatusCode.OK, set.StatusCode);
            at = (await JsonAsync(set)).GetProperty("activityFrom").GetProperty("at").GetDateTimeOffset();
            Assert.True(started.OccurredAt < at);
        }
        finally
        {
            hold.TrySetResult();
        }

        await bed.AwaitRowAsync(m => m.Type == MessageTypes.Completed && m.Source == $"{team}/Dev" && m.CorrelationId == root.Seq, "Dev's end");
        await bed.QuietAsync(team);

        // The window and Dev's run start at it, not at the workflow's root or the run's start.
        var answer = await bed.ActivityAsync(team);
        Assert.Equal("workflows", answer.GetProperty("window").GetString());
        Assert.Equal(at, answer.GetProperty("from").GetDateTimeOffset());
        var first = Spans(answer, "Dev")[0];
        Assert.Equal("running", first.GetProperty("state").GetString());
        Assert.Equal(at, first.GetProperty("from").GetDateTimeOffset());
        Assert.Equal(root.Seq, first.GetProperty("workflow").GetInt64());

        // The run began before it and ended after it: it is listed, at its end.
        var runs = await bed.TokenRunsAsync(team);
        Assert.Equal(at, runs.GetProperty("from").GetDateTimeOffset());
        Assert.Contains(runs.GetProperty("runs").EnumerateArray(), run => run.GetProperty("member").GetString() == "Dev");
    }

    [Fact]
    public async Task Spend_caps_recent_cost_outcomes_and_every_ledger_hold_and_log_row_read_the_same_before_and_after()
    {
        var team = await bed.TeamAsync("From unchanged", "Dev");

        // A trigger with a cap its one fire reaches, a workflow serving an outcome, and a hold.
        var trigger = await bed.TriggerAsync(team);
        var fired = await bed.Person.PostAsync($"/api/teams/{team}/triggers/{trigger}/run", null, Ct);
        Assert.True(fired.IsSuccessStatusCode, await fired.Content.ReadAsStringAsync(Ct));
        var fire = await bed.AwaitRowAsync(m => m.Type == MessageTypes.Completed && m.Source == $"{team}/Dev", "the fire's run");

        var outcome = await bed.OutcomeAsync($"From unchanged {Guid.NewGuid():N}");
        var told = await bed.Person.PostAsJsonAsync(
            $"/api/teams/{team}/containers/{TeamRegistry.DefaultManagerName}/tell", new { instruction = "serve it", outcome }, Ct);
        Assert.True(told.IsSuccessStatusCode, await told.Content.ReadAsStringAsync(Ct));
        var workflow = (await JsonAsync(told)).GetProperty("correlationId").GetInt64();
        await bed.AwaitRowAsync(
            m => m.Type == MessageTypes.Completed && m.Source == $"{team}/{TeamRegistry.DefaultManagerName}" && m.CorrelationId == workflow,
            "the Manager's run");

        await bed.HoldRowAsync(team, "Dev", DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddMinutes(-4));
        await bed.DeclareAsync(team, fire.CorrelationId);
        await bed.DeclareAsync(team, workflow);
        await bed.QuietAsync(team);

        var rows = await bed.SettledRowsAsync();
        var figures = await bed.FiguresAsync(team, trigger, workflow, outcome);
        Assert.Contains("\"capReachedToday\":true", figures[0]);
        Assert.Contains(rows, table => table.StartsWith("admission_holds ", StringComparison.Ordinal)
            && !table.StartsWith("admission_holds 0", StringComparison.Ordinal));

        Assert.Equal(HttpStatusCode.OK, (await bed.SetAsync(team, "now")).StatusCode);
        Assert.Equal(figures, await bed.FiguresAsync(team, trigger, workflow, outcome));
        Assert.Equal(rows, await bed.RowsAsync());

        Assert.Equal(HttpStatusCode.OK, (await bed.SetAsync(team, null)).StatusCode);
        Assert.Equal(figures, await bed.FiguresAsync(team, trigger, workflow, outcome));
        Assert.Equal(rows, await bed.RowsAsync());
    }

    [Fact]
    public async Task A_machine_principal_is_refused_and_nothing_changes()
    {
        var team = await bed.TeamAsync("From machine", "Dev");
        using var manager = await bed.ContainerAsync(team, TeamRegistry.DefaultManagerName);

        var refused = await manager.PutAsJsonAsync($"/api/teams/{team}/activity-from", new { from = "now" }, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal(PermitGate.HumansOnlyMessage, (await JsonAsync(refused)).GetProperty("error").GetString());
        Assert.Null(await bed.Store.ActivityFromAsync(team, Ct));
        Assert.Empty(await bed.TenantRowsAsync(team));
    }

    [Fact]
    public async Task A_body_that_is_neither_now_nor_null_is_refused_with_a_sentence()
    {
        var team = await bed.TeamAsync("From body", "Dev");

        foreach (var body in new object[] { new { from = "yesterday" }, new { at = "now" } })
        {
            var refused = await bed.Person.PutAsJsonAsync($"/api/teams/{team}/activity-from", body, Ct);
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            Assert.Equal(TeamActivityFrom.BodyRefusal, (await JsonAsync(refused)).GetProperty("error").GetString());
        }

        Assert.Null(await bed.Store.ActivityFromAsync(team, Ct));
    }

    [Fact]
    public async Task A_write_whose_tenant_row_cannot_land_changes_nothing()
    {
        var team = await bed.TeamAsync("From unrecorded", "Dev");

        await bed.RefuseTenantRowAsync(TenantActions.TeamActivityFromSet);
        try
        {
            var refused = await bed.SetAsync(team, "now");
            Assert.Equal(HttpStatusCode.InternalServerError, refused.StatusCode);
            Assert.Contains("its tenant log row could not be written", (await JsonAsync(refused)).GetProperty("error").GetString());
            Assert.Null(await bed.Store.ActivityFromAsync(team, Ct));
            Assert.False((await bed.ActivityAsync(team)).TryGetProperty("activityFrom", out _));
        }
        finally
        {
            await bed.AllowTenantRowsAsync();
        }

        Assert.Equal(HttpStatusCode.OK, (await bed.SetAsync(team, "now")).StatusCode);
        var set = await bed.Store.ActivityFromAsync(team, Ct);
        Assert.NotNull(set);

        await bed.RefuseTenantRowAsync(TenantActions.TeamActivityFromCleared);
        try
        {
            var refused = await bed.SetAsync(team, null);
            Assert.Equal(HttpStatusCode.InternalServerError, refused.StatusCode);
            Assert.Equal(set, await bed.Store.ActivityFromAsync(team, Ct));
        }
        finally
        {
            await bed.AllowTenantRowsAsync();
        }

        Assert.Equal([TenantActions.TeamActivityFromSet], (await bed.TenantRowsAsync(team)).Select(row => row.Action));
    }

    [Fact]
    public async Task A_clone_does_not_copy_it_and_a_deletion_forgets_it()
    {
        var team = await bed.TeamAsync("From clone", "Dev");
        Assert.Equal(HttpStatusCode.OK, (await bed.SetAsync(team, "now")).StatusCode);

        var cloned = await bed.Person.PostAsJsonAsync($"/api/teams/{team}/clone", new { name = "From clone copy" }, Ct);
        Assert.Equal(HttpStatusCode.Created, cloned.StatusCode);
        var copy = (await JsonAsync(cloned)).GetProperty("team").GetProperty("id").GetString()!;
        Assert.Null(await bed.Store.ActivityFromAsync(copy, Ct));
        Assert.NotNull(await bed.Store.ActivityFromAsync(team, Ct));

        await bed.DeleteTeamAsync(team);
        Assert.Null(await bed.Store.ActivityFromAsync(team, Ct));

        Assert.Equal(team, await bed.TeamAsync("From clone", "Dev"));
        Assert.Null(await bed.Store.ActivityFromAsync(team, Ct));
        Assert.False((await bed.ActivityAsync(team)).TryGetProperty("activityFrom", out _));
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement.Clone();

    private static DateTimeOffset[] Ends(JsonElement runs) =>
        [.. runs.GetProperty("runs").EnumerateArray().Select(run => run.GetProperty("endedAt").GetDateTimeOffset())];

    private static List<JsonElement> Spans(JsonElement answer, string member) =>
        [.. answer.GetProperty("members").EnumerateArray()
            .Single(m => m.GetProperty("member").GetString() == member)
            .GetProperty("spans").EnumerateArray()];

    private static long[] RunningWorkflows(JsonElement answer, string member) =>
        [.. Spans(answer, member)
            .Where(s => s.GetProperty("state").GetString() == "running")
            .Select(s => s.GetProperty("workflow").GetInt64())
            .Distinct()
            .Order()];

    /// <summary>
    /// The real Host over its own data root with a fake agent: every run measures 1000 in and 500
    /// out and ends at once unless a test holds it. Each test makes its own team.
    /// </summary>
    public sealed class Bed : IAsyncLifetime
    {
        public const string Email = "person@example.test";
        private const string Password = "correct horse battery";

        /// <summary>Every ledger, hold and log table, read whole to tell whether a row changed.</summary>
        private static readonly string[] Tables =
            ["messages", "usage_ledger", "workflow_ledger", "delivery_ledger", "nudge_ledger", "admission_holds", "workflow_outcome_links"];

        private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), $"harness-activity-from-{Guid.NewGuid():N}");
        private readonly FakeAgent _agents = new();
        private readonly Dictionary<ContainerId, TaskCompletionSource> _holds = [];
        private WebApplicationFactory<Program> _factory = null!;

        public HttpClient Person { get; private set; } = null!;

        public IMessageLog Log => _factory.Services.GetRequiredService<IMessageLog>();

        public ITeamStore Store => _factory.Services.GetRequiredService<ITeamStore>();

        private string Database => Path.Combine(_dataRoot, "messages.db");

        public async ValueTask InitializeAsync()
        {
            Directory.CreateDirectory(_dataRoot);

            _agents.Behaviour = async invocation =>
            {
                TaskCompletionSource? hold;
                lock (_holds) _holds.TryGetValue(invocation.Container, out hold);

                if (hold is not null) await hold.Task;

                return new AgentResult(0, $"done by {invocation.Container.Name}", Usage: new InvocationUsage(1000, 500, "test"));
            };

            _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host => host
                .UseSetting("DataRoot", _dataRoot)
                .UseSetting("Logging:LogLevel:Default", "Warning")
                .ConfigureTestServices(services => services.AddSingleton<IAgentRunner>(_agents)));

            await _factory.Services.GetRequiredService<IUserStore>().CreateAsync(Email, Password);
            Person = _factory.CreateClient();
            (await Person.PostAsJsonAsync("/api/auth/login", new { email = Email, password = Password })).EnsureSuccessStatusCode();
        }

        public async ValueTask DisposeAsync()
        {
            lock (_holds)
            {
                foreach (var hold in _holds.Values) hold.TrySetResult();
            }

            Person.Dispose();
            await _factory.DisposeAsync();
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(_dataRoot, recursive: true); }
            catch (IOException) { }
        }

        /// <summary>A new team with these members beside its Manager; answers its id.</summary>
        public async Task<string> TeamAsync(string label, params string[] members)
        {
            var team = (await _factory.Services.GetRequiredService<TeamRegistry>()
                .CreateAsync(label, "claude-headless", memberAgent: "claude-headless", ct: Ct)).Id;

            foreach (var member in members)
            {
                var added = await Person.PostAsJsonAsync(
                    $"/api/teams/{team}/containers", new { name = member, agent = "claude-headless" }, Ct);
                Assert.Equal(HttpStatusCode.OK, added.StatusCode);
            }

            return team;
        }

        public async Task<HttpClient> ContainerAsync(string team, string member)
        {
            var key = await _factory.Services.GetRequiredService<IPrincipalStore>().MintAsync(
                new ContainerId(team, member).ToString(), PrincipalKind.Container, team, Permits.All);

            var client = _factory.CreateClient();
            client.DefaultRequestHeaders.Add(ApiKeyAuthenticationHandler.Header, key);
            return client;
        }

        /// <summary>The person's PUT: <c>"now"</c>, or null to clear.</summary>
        public Task<HttpResponseMessage> SetAsync(string team, string? from) =>
            Person.PutAsync(
                $"/api/teams/{team}/activity-from",
                new StringContent(JsonSerializer.Serialize(new { from }), Encoding.UTF8, "application/json"),
                Ct);

        /// <summary>Holds this member's runs until <paramref name="hold"/> is set.</summary>
        public void Hold(string team, string member, TaskCompletionSource hold)
        {
            lock (_holds) _holds[new ContainerId(team, member)] = hold;
        }

        public Task<Message> TellAsync(string team, string member, string instruction) =>
            Log.AppendAsync(new NewMessage(
                MessageTypes.InstructionFor(new ContainerId(team, member)),
                JsonSerializer.Serialize(new { instruction }), "console"), Ct);

        /// <summary>Roots a workflow with an instruction to <paramref name="member"/> and waits for its
        /// run and the Manager run its completion wakes.</summary>
        public async Task<Message> TellAndSettleAsync(string team, string member, string instruction)
        {
            var root = await TellAsync(team, member, instruction);
            await AwaitRowAsync(m => IsTerminal(m) && m.Source == $"{team}/{member}" && m.CorrelationId == root.Seq, $"{member}'s run");
            await AwaitRowAsync(
                m => IsTerminal(m) && m.Source == $"{team}/{TeamRegistry.DefaultManagerName}" && m.CorrelationId == root.Seq,
                "the Manager's run");
            return root;
        }

        public Task DeclareAsync(string team, Message root) => DeclareAsync(team, root.Seq);

        /// <summary>The Manager declares workflow <paramref name="correlation"/> complete, so nothing
        /// more is written into it.</summary>
        public async Task DeclareAsync(string team, long correlation)
        {
            var last = (await Log.ReadCorrelationAsync(correlation, Ct)).Last(IsTerminal);

            await Log.AppendAsync(new NewMessage(
                MessageTypes.WorkflowCompleted, "{}", $"{team}/{TeamRegistry.DefaultManagerName}", last.Seq), Ct);
        }

        public async Task<JsonElement> ActivityAsync(string team) => await GetAsync($"/api/teams/{team}/activity");

        public async Task<JsonElement> TokenRunsAsync(string team) => await GetAsync($"/api/teams/{team}/tokens/runs");

        /// <summary>A daily trigger on Dev, capped at 100 tokens, waking no Manager.</summary>
        public async Task<string> TriggerAsync(string team)
        {
            var created = await Person.PostAsJsonAsync($"/api/teams/{team}/triggers", new
            {
                name = "Nightly",
                kind = "cron",
                expression = "0 0 3 * * *",
                timezone = "UTC",
                container = "Dev",
                idleOnly = false,
                instruction = "look",
                wakeManager = "never",
                dailyTokenCap = 100,
            }, Ct);
            Assert.True(created.StatusCode == HttpStatusCode.Created, await created.Content.ReadAsStringAsync(Ct));
            return (await created.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetString()!;
        }

        public async Task<string> OutcomeAsync(string name)
        {
            var created = await Person.PostAsJsonAsync("/api/outcomes", new { name }, Ct);
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            return (await created.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetString()!;
        }

        /// <summary>
        /// Every figure that must not read the activity-from: the trigger's spend today and cap, the
        /// workflow's spend, Dev's recent cost, the outcome's figures and the ledger export.
        /// </summary>
        public async Task<string[]> FiguresAsync(string team, string trigger, long workflow, string outcome)
        {
            var triggers = await GetAsync($"/api/teams/{team}/triggers");
            var row = triggers.EnumerateArray().Single(t => t.GetProperty("id").GetString() == trigger);

            return
            [
                JsonSerializer.Serialize(new
                {
                    spentToday = row.GetProperty("spentToday"),
                    capReachedToday = row.GetProperty("capReachedToday"),
                    dailyTokenCap = row.GetProperty("dailyTokenCap"),
                }),
                JsonSerializer.Serialize(await Log.GetWorkflowSpendAsync(workflow, Ct)),
                (await GetAsync($"/api/teams/{team}/containers/Dev/cost")).GetRawText(),
                (await GetAsync($"/api/outcomes/{outcome}")).GetRawText(),
                (await GetAsync("/api/ledger/export?take=200")).GetRawText(),
            ];
        }

        /// <summary>One admission hold, written as its writer would.</summary>
        public async Task HoldRowAsync(string team, string member, DateTimeOffset heldAt, DateTimeOffset releasedAt)
        {
            await using var connection = new SqliteConnection($"Data Source={Database};Pooling=false");
            await connection.OpenAsync(Ct);
            await using var insert = connection.CreateCommand();
            insert.CommandText =
                """
                INSERT INTO admission_holds (team_id, team_name, member, held_at, released_at, reason_kind, reason)
                VALUES ($team, $team, $member, $held, $released, 'slot', 'Waiting for a slot.')
                """;
            insert.Parameters.AddWithValue("$team", team);
            insert.Parameters.AddWithValue("$member", member);
            insert.Parameters.AddWithValue("$held", heldAt.ToString("O"));
            insert.Parameters.AddWithValue("$released", releasedAt.ToString("O"));
            await insert.ExecuteNonQueryAsync(Ct);
        }

        /// <summary>Each table as its name, its row count and every row, column by column.</summary>
        public async Task<List<string>> RowsAsync()
        {
            await using var connection = new SqliteConnection($"Data Source={Database};Pooling=false");
            await connection.OpenAsync(Ct);
            var tables = new List<string>();

            foreach (var table in Tables)
            {
                await using var command = connection.CreateCommand();
                command.CommandText = $"SELECT * FROM {table} ORDER BY rowid";
                var rows = new List<string>();
                await using var reader = await command.ExecuteReaderAsync(Ct);

                while (await reader.ReadAsync(Ct))
                {
                    rows.Add(string.Join("|", Enumerable.Range(0, reader.FieldCount)
                        .Select(i => reader.IsDBNull(i) ? "NULL" : Convert.ToString(reader.GetValue(i), System.Globalization.CultureInfo.InvariantCulture))));
                }

                tables.Add($"{table} {rows.Count}\n{string.Join("\n", rows)}");
            }

            return tables;
        }

        /// <summary><see cref="RowsAsync"/> once two reads 600 ms apart agree: a workflow's close
        /// lands in the ledger a moment after its row.</summary>
        public async Task<List<string>> SettledRowsAsync()
        {
            var deadline = DateTime.UtcNow.AddSeconds(30);
            var previous = await RowsAsync();

            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(600, Ct);
                var current = await RowsAsync();
                if (current.SequenceEqual(previous)) return current;
                previous = current;
            }

            throw new TimeoutException("The ledger and log did not settle.");
        }

        public async Task<IReadOnlyList<TenantEvent>> TenantRowsAsync(string team) =>
            [.. (await _factory.Services.GetRequiredService<ITenantLog>().ReadAsync(take: 10_000, ct: Ct)).Events
                .Where(row => row.Subject == team
                    && row.Action is TenantActions.TeamActivityFromSet or TenantActions.TeamActivityFromCleared)
                .OrderBy(row => row.Seq)];

        /// <summary>A tenant row of <paramref name="action"/> cannot be written from now on.</summary>
        public Task RefuseTenantRowAsync(string action) =>
            ExecuteAsync(
                "DROP TRIGGER IF EXISTS refuse_row; "
                + $"CREATE TRIGGER refuse_row BEFORE INSERT ON tenant_events WHEN NEW.action = '{action}' "
                + "BEGIN SELECT RAISE(ABORT, 'the disk is full'); END;");

        public Task AllowTenantRowsAsync() => ExecuteAsync("DROP TRIGGER IF EXISTS refuse_row");

        public async Task DeleteTeamAsync(string team)
        {
            await QuietAsync(team);
            var deleted = await Person.DeleteAsync($"/api/teams/{team}", Ct);
            Assert.True(deleted.IsSuccessStatusCode, await deleted.Content.ReadAsStringAsync(Ct));
        }

        public async Task<Message> AwaitRowAsync(Func<Message, bool> match, string what)
        {
            var deadline = DateTime.UtcNow.AddSeconds(30);

            while (DateTime.UtcNow < deadline)
            {
                if ((await Log.ReadAfterAsync(0, [MessageTypes.Started, MessageTypes.Completed, MessageTypes.Failed], int.MaxValue, Ct))
                    .FirstOrDefault(match) is { } row)
                {
                    return row;
                }

                await Task.Delay(50, Ct);
            }

            throw new TimeoutException($"No row: {what}.");
        }

        /// <summary>Waits until no member of <paramref name="team"/> has a run going, nothing is
        /// queued, and no run has started for a moment.</summary>
        public async Task QuietAsync(string team)
        {
            var deadline = DateTime.UtcNow.AddSeconds(30);
            long? last = null;

            while (DateTime.UtcNow < deadline)
            {
                var rows = (await Log.ReadAfterAsync(0, [MessageTypes.Started, MessageTypes.Completed, MessageTypes.Failed], int.MaxValue, Ct))
                    .Where(m => m.Source.StartsWith($"{team}/", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                var going = rows.GroupBy(m => m.Source).Any(g => g.MaxBy(m => m.Seq)!.Type == MessageTypes.Started);
                var newest = rows.Count == 0 ? 0 : rows.Max(m => m.Seq);

                if (!going && newest == last && await TeamQuiet.IsQuietAsync(_factory.Services, team, Ct)) return;

                last = going ? null : newest;
                await Task.Delay(300, Ct);
            }

            throw new TimeoutException($"{team} did not go quiet.");
        }

        private async Task<JsonElement> GetAsync(string url)
        {
            var response = await Person.GetAsync(url, Ct);
            var body = await response.Content.ReadAsStringAsync(Ct);
            Assert.True(response.StatusCode == HttpStatusCode.OK, $"{(int)response.StatusCode} from {url}: {body}");
            return JsonDocument.Parse(body).RootElement.Clone();
        }

        private async Task ExecuteAsync(string sql)
        {
            await using var connection = new SqliteConnection($"Data Source={Database};Pooling=false");
            await connection.OpenAsync(Ct);
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync(Ct);
        }

        private static bool IsTerminal(Message m) => m.Type is MessageTypes.Completed or MessageTypes.Failed;
    }
}
