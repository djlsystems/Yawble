using System.Net;
using System.Net.Http.Json;
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
/// THE TEAM'S TOKENS RUN BY RUN, on the real Host with a fake agent. <c>GET /api/teams/{team}/tokens/runs</c>
/// answers one row per run from <c>usage_ledger</c>, at the instant the run ended - when its tokens
/// were measured - with the ledger's own figures and nothing estimated: an unmeasured run carries no
/// figures, never zeros. Most tests write ledger rows by hand at fixed instants in 2001 on a team of
/// their own; the batched run and the reset drive real runs.
/// </summary>
public sealed class TeamTokenRunsTests(TeamTokenRunsTests.Bed bed) : IClassFixture<TeamTokenRunsTests.Bed>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>An instant in a fixed hour of 2001: the base every hand-written row counts from.</summary>
    private static readonly DateTimeOffset T0 = new(2001, 2, 3, 4, 0, 0, TimeSpan.Zero);

    private static DateTimeOffset T(int seconds) => T0.AddSeconds(seconds);

    private static readonly Figures Split = new(In: 100, CachedIn: 2000, CacheCreation: 40, Out: 30, Combined: null, Billable: 380);

    [Fact]
    public async Task Each_run_is_one_row_at_its_end_with_its_ledger_figures()
    {
        var team = await bed.TeamAsync("Token runs rows", "Dev", "Ops", "Gone");

        // A run that began before the period and ended inside it is in it, at its end; one that
        // began inside and ended after is not. Nothing is spread over a run's duration.
        await bed.RunAsync(team, "Dev", started: T(-50), ended: T(10), Split);
        await bed.RunAsync(team, "Ops", started: T(20), ended: T(40), Split with { In = 7, Billable = 287 });
        await bed.RunAsync(team, "Dev", started: T(90), ended: T(110), Split);
        await bed.RunAsync(team, "Ops", started: T(-90), ended: T(-10), Split);
        await bed.RunAsync(team, "Gone", started: T(50), ended: T(60), Split with { Billable = 381 });
        await bed.RemoveAsync(team, "Gone");

        var answer = await bed.RunsAsync(team, T(0), T(100));

        Assert.Equal("requested", answer.GetProperty("window").GetString());
        Assert.Equal(T(0), answer.GetProperty("from").GetDateTimeOffset());
        Assert.Equal(T(100), answer.GetProperty("to").GetDateTimeOffset());
        Assert.True(answer.TryGetProperty("serverNow", out _));

        Assert.Equal(
            ["Dev 10 current billable 380", "Ops 40 current billable 287", "Gone 60 removed billable 381"],
            Rows(answer));

        var ops = answer.GetProperty("runs")[1];
        Assert.True(ops.GetProperty("measured").GetBoolean());
        Assert.Equal(7, ops.GetProperty("tokensIn").GetInt64());
        Assert.Equal(2000, ops.GetProperty("tokensCachedIn").GetInt64());
        Assert.Equal(40, ops.GetProperty("tokensCacheCreation").GetInt64());
        Assert.Equal(30, ops.GetProperty("tokensOut").GetInt64());
        Assert.False(ops.TryGetProperty("combined", out _));
    }

    [Fact]
    public async Task An_unmeasured_run_has_no_figures_never_zero()
    {
        var team = await bed.TeamAsync("Token runs unmeasured", "Dev");
        await bed.RunAsync(team, "Dev", started: T(10), ended: T(20), figures: null);

        var run = Assert.Single((await bed.RunsAsync(team, T(0), T(100))).GetProperty("runs").EnumerateArray());

        Assert.False(run.GetProperty("measured").GetBoolean());
        Assert.Equal(T(20), run.GetProperty("endedAt").GetDateTimeOffset());

        foreach (var figure in new[] { "billable", "tokensIn", "tokensCachedIn", "tokensCacheCreation", "tokensOut", "combined" })
        {
            Assert.False(run.TryGetProperty(figure, out var value), $"An unmeasured run carries {figure}: {value}.");
        }
    }

    [Fact]
    public async Task A_combined_total_has_no_split()
    {
        var team = await bed.TeamAsync("Token runs combined", "Dev");
        await bed.RunAsync(team, "Dev", started: T(10), ended: T(20), new Figures(null, null, null, null, Combined: 900, Billable: 900));

        var run = Assert.Single((await bed.RunsAsync(team, T(0), T(100))).GetProperty("runs").EnumerateArray());

        Assert.True(run.GetProperty("measured").GetBoolean());
        Assert.Equal(900, run.GetProperty("combined").GetInt64());
        Assert.Equal(900, run.GetProperty("billable").GetInt64());

        foreach (var figure in new[] { "tokensIn", "tokensCachedIn", "tokensCacheCreation", "tokensOut" })
        {
            Assert.False(run.TryGetProperty(figure, out var value), $"A combined total carries {figure}: {value}.");
        }
    }

    [Fact]
    public async Task A_batched_run_is_one_row()
    {
        var team = await bed.TeamAsync("Token runs batch", "Dev");
        var from = DateTimeOffset.UtcNow.AddMinutes(-1);
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bed.Hold(team, "Dev", hold);

        // Two deliveries in the same workflow arrive while Dev's first run is going; its next run
        // takes both as one batch and closes each with its own terminal row.
        var root = await bed.TellAsync(team, "Dev", "first");
        await bed.AwaitRowAsync(m => m.Type == MessageTypes.Started && m.Source == $"{team}/Dev", "Dev's first start");
        await bed.TellAsync(team, "Dev", "second", root.Seq);
        await bed.TellAsync(team, "Dev", "third", root.Seq);
        bed.Release(team, "Dev");
        hold.SetResult();

        await bed.AwaitCountAsync(
            m => m.Type == MessageTypes.Completed && m.Source == $"{team}/Dev" && m.CorrelationId == root.Seq, 3, "Dev's three closes");
        await bed.QuietAsync(team);

        var dev = Mine(await bed.RunsAsync(team, from, DateTimeOffset.UtcNow.AddMinutes(1)), "Dev");

        Assert.Equal(2, dev.Length);
        Assert.All(dev, run => Assert.Equal(Bed.RunBillable, run.GetProperty("billable").GetInt64()));
    }

    [Fact]
    public async Task The_default_window_is_the_teams_whole_history_since_its_creation()
    {
        var team = await bed.TeamAsync("Token runs default", "Dev");
        var created = await bed.CreatedAsync(team);
        var ended = DateTimeOffset.UtcNow;

        await bed.RunAsync(team, "Dev", started: ended.AddSeconds(-5), ended: ended, Split);

        var answer = await bed.RunsAsync(team);

        Assert.Equal("all", answer.GetProperty("window").GetString());
        Assert.Equal(created, answer.GetProperty("from").GetDateTimeOffset());
        Assert.Equal(answer.GetProperty("serverNow").GetDateTimeOffset(), answer.GetProperty("to").GetDateTimeOffset());
        Assert.True(answer.GetProperty("to").GetDateTimeOffset() >= ended);
        Assert.Equal(ended.ToUnixTimeMilliseconds(), Assert.Single(Mine(answer, "Dev")).GetProperty("endedAt").GetDateTimeOffset().ToUnixTimeMilliseconds());
    }

    [Fact]
    public async Task A_recreated_team_does_not_show_its_predecessors_runs()
    {
        var team = await bed.TeamAsync("Token runs recreated", "Dev");

        // The predecessor's runs carry the log positions they ran at, below anything its successor runs.
        await bed.RunAsync(team, "Dev", started: T(10), ended: T(20), Split, seq: await bed.HeadAsync());
        await bed.RunAsync(team, "Old", started: T(10), ended: T(30), Split, seq: await bed.HeadAsync());

        await bed.DeleteTeamAsync(team);
        Assert.Equal(team, await bed.TeamAsync("Token runs recreated", "Dev"));

        await bed.RunAsync(team, "Dev", started: T(60), ended: T(70), Split);

        Assert.Equal(["Dev 70 current billable 380"], Rows(await bed.RunsAsync(team, T(0), T(100))));
        Assert.Equal(["Dev 70 current billable 380"], Rows(await bed.RunsAsync(team)));
    }

    [Fact]
    public async Task A_period_over_a_year_is_refused_with_a_sentence()
    {
        var team = await bed.TeamAsync("Token runs year", "Dev");
        var person = bed.Person;

        var year = await person.GetAsync(Url(team, T0, T0.AddYears(1)), Ct);
        Assert.Equal(HttpStatusCode.OK, year.StatusCode);

        var longer = await person.GetAsync(Url(team, T0, T0.AddYears(1).AddSeconds(1)), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, longer.StatusCode);
        var tooLong = (await longer.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("error").GetString()!;
        Assert.EndsWith(".", tooLong);
        Assert.Contains("year", tooLong);

        var half = await person.GetAsync($"/api/teams/{team}/tokens/runs?from={Uri.EscapeDataString(T0.ToString("O"))}", Ct);
        Assert.Equal(HttpStatusCode.BadRequest, half.StatusCode);
        Assert.EndsWith(".", (await half.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("error").GetString()!);

        var backwards = await person.GetAsync(Url(team, T(10), T(0)), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, backwards.StatusCode);
        Assert.EndsWith(".", (await backwards.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("error").GetString()!);
    }

    [Fact]
    public async Task A_container_is_refused_another_teams_runs_with_what_a_missing_team_gets()
    {
        var own = await bed.TeamAsync("Token runs own", "Dev");
        var other = await bed.TeamAsync("Token runs other", "Dev");
        using var container = await bed.ContainerAsync(own, "Dev");

        var mine = await container.GetAsync($"/api/teams/{own}/tokens/runs", Ct);
        var theirs = await container.GetAsync($"/api/teams/{other}/tokens/runs", Ct);
        var missing = await container.GetAsync("/api/teams/no-such-team/tokens/runs", Ct);

        Assert.Equal(HttpStatusCode.OK, mine.StatusCode);
        Assert.Equal(missing.StatusCode, theirs.StatusCode);
        Assert.Equal(TeamGate.NoSuchTeam, await theirs.Content.ReadAsStringAsync(Ct));
        Assert.Equal(await missing.Content.ReadAsStringAsync(Ct), await theirs.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task Runs_survive_a_reset_that_deletes_memory()
    {
        var team = await bed.TeamAsync("Token runs reset", "Dev");
        var from = DateTimeOffset.UtcNow.AddMinutes(-1);

        var root = await bed.TellAsync(team, "Dev", "work");
        await bed.AwaitCountAsync(
            m => m.Type == MessageTypes.Completed && m.Source == $"{team}/Dev" && m.CorrelationId == root.Seq, 1, "Dev's run");
        await bed.QuietAsync(team);

        var to = DateTimeOffset.UtcNow.AddMinutes(1);
        var before = await bed.RunsAsync(team, from, to);
        var whole = await bed.RunsAsync(team);
        Assert.NotEmpty(Mine(before, "Dev"));

        var reset = await bed.Person.PostAsJsonAsync($"/api/teams/{team}/reset", new
        {
            members = new[] { "Dev", TeamRegistry.DefaultManagerName },
            forgetHistory = true,
            purge = true,
            clearTranscripts = true,
        }, Ct);
        Assert.True(reset.IsSuccessStatusCode, await reset.Content.ReadAsStringAsync(Ct));
        Assert.Null(await bed.Log.FindAsync(root.Seq, Ct));

        Assert.Equal(Rows(before), Rows(await bed.RunsAsync(team, from, to)));
        Assert.Equal(Rows(whole), Rows(await bed.RunsAsync(team)));
    }

    private static string Url(string team, DateTimeOffset from, DateTimeOffset to) =>
        $"/api/teams/{team}/tokens/runs?from={Uri.EscapeDataString(from.ToString("O"))}&to={Uri.EscapeDataString(to.ToString("O"))}";

    private static JsonElement[] Mine(JsonElement answer, string member) =>
        [.. answer.GetProperty("runs").EnumerateArray().Where(r => r.GetProperty("member").GetString() == member)];

    /// <summary>Each run as "member end current|removed billable N", seconds from <see cref="T0"/>.</summary>
    private static string[] Rows(JsonElement answer) =>
        [.. answer.GetProperty("runs").EnumerateArray().Select(run =>
        {
            var ended = run.GetProperty("endedAt").GetDateTimeOffset();
            var at = ended.Year == T0.Year
                ? ((long)(ended - T0).TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture)
                : ended.ToString("O");
            var current = run.GetProperty("current").GetBoolean() ? "current" : "removed";
            var billable = run.TryGetProperty("billable", out var b) ? b.GetInt64().ToString(System.Globalization.CultureInfo.InvariantCulture) : "none";
            return $"{run.GetProperty("member").GetString()} {at} {current} billable {billable}";
        })];

    /// <summary>A run's token figures as the ledger stores them; null is not measured.</summary>
    public sealed record Figures(long? In, long? CachedIn, long? CacheCreation, long? Out, long? Combined, long? Billable);

    /// <summary>
    /// The real Host over its own data root with a fake agent that reports the same usage for every
    /// run and ends it at once unless a test holds it. Each test makes its own team.
    /// </summary>
    public sealed class Bed : IAsyncLifetime
    {
        private const string Email = "person@example.test";
        private const string Password = "correct horse battery";

        /// <summary>What every fake run is billed: 100 in and 40 out.</summary>
        public const long RunBillable = 140;

        private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), $"harness-token-runs-{Guid.NewGuid():N}");
        private readonly FakeAgent _agents = new();
        private readonly Dictionary<ContainerId, TaskCompletionSource> _holds = [];
        private WebApplicationFactory<Program> _factory = null!;
        private static long _run = 9_000_000_000 + Random.Shared.Next(1_000_000);

        public HttpClient Person { get; private set; } = null!;

        public IMessageLog Log => _factory.Services.GetRequiredService<IMessageLog>();

        private string Database => Path.Combine(_dataRoot, "messages.db");

        public async ValueTask InitializeAsync()
        {
            Directory.CreateDirectory(_dataRoot);

            _agents.Behaviour = async invocation =>
            {
                TaskCompletionSource? hold;
                lock (_holds) _holds.TryGetValue(invocation.Container, out hold);

                if (hold is not null) await hold.Task;

                return new AgentResult(0, $"done by {invocation.Container.Name}", Usage: new InvocationUsage(100, 40, "claude"));
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

        public async Task<DateTimeOffset> CreatedAsync(string team) =>
            await _factory.Services.GetRequiredService<ITeamStore>().CreatedAtAsync(team, Ct)
                ?? throw new InvalidOperationException($"{team} has no creation time.");

        public async Task RemoveAsync(string team, string member)
        {
            var removed = await Person.DeleteAsync($"/api/teams/{team}/containers/{member}", Ct);
            Assert.True(removed.IsSuccessStatusCode, await removed.Content.ReadAsStringAsync(Ct));
        }

        public async Task<HttpClient> ContainerAsync(string team, string member)
        {
            var key = await _factory.Services.GetRequiredService<IPrincipalStore>().MintAsync(
                new ContainerId(team, member).ToString(), PrincipalKind.Container, team, Permits.All);

            var client = _factory.CreateClient();
            client.DefaultRequestHeaders.Add(ApiKeyAuthenticationHandler.Header, key);
            return client;
        }

        /// <summary>Holds this member's runs until <paramref name="hold"/> is set.</summary>
        public void Hold(string team, string member, TaskCompletionSource hold)
        {
            lock (_holds) _holds[new ContainerId(team, member)] = hold;
        }

        /// <summary>This member's later runs are no longer held.</summary>
        public void Release(string team, string member)
        {
            lock (_holds) _holds.Remove(new ContainerId(team, member));
        }

        /// <summary>One finished run, written to the ledger as its writer would.</summary>
        public async Task RunAsync(
            string team, string member, DateTimeOffset started, DateTimeOffset ended, Figures? figures, long? seq = null)
        {
            await using var connection = new SqliteConnection($"Data Source={Database};Pooling=false");
            await connection.OpenAsync(Ct);
            await using var insert = connection.CreateCommand();
            insert.CommandText =
                """
                INSERT INTO usage_ledger (run_seq, correlation, team_id, team_name, member, member_kind, run_outcome,
                    started_at, ended_at, measured, tokens_in, tokens_cached_in, tokens_cache_creation, tokens_out,
                    tokens_combined, billable)
                VALUES ($seq, 1, $team, $team, $member, 'agent', 'completed', $started, $ended, $measured,
                    $in, $cachedIn, $cacheCreation, $out, $combined, $billable)
                """;
            insert.Parameters.AddWithValue("$seq", seq ?? Interlocked.Increment(ref _run));
            insert.Parameters.AddWithValue("$team", team);
            insert.Parameters.AddWithValue("$member", member);
            insert.Parameters.AddWithValue("$started", started.ToString("O"));
            insert.Parameters.AddWithValue("$ended", ended.ToString("O"));
            insert.Parameters.AddWithValue("$measured", figures is null ? 0 : 1);
            insert.Parameters.AddWithValue("$in", (object?)figures?.In ?? DBNull.Value);
            insert.Parameters.AddWithValue("$cachedIn", (object?)figures?.CachedIn ?? DBNull.Value);
            insert.Parameters.AddWithValue("$cacheCreation", (object?)figures?.CacheCreation ?? DBNull.Value);
            insert.Parameters.AddWithValue("$out", (object?)figures?.Out ?? DBNull.Value);
            insert.Parameters.AddWithValue("$combined", (object?)figures?.Combined ?? DBNull.Value);
            insert.Parameters.AddWithValue("$billable", (object?)figures?.Billable ?? DBNull.Value);
            await insert.ExecuteNonQueryAsync(Ct);
        }

        /// <summary>A log position no ledger row holds yet: a fresh row's own.</summary>
        public async Task<long> HeadAsync() =>
            (await Log.AppendAsync(new NewMessage("test.mark", "{}", "console"), Ct)).Seq;

        public async Task DeleteTeamAsync(string team)
        {
            var deleted = await Person.DeleteAsync($"/api/teams/{team}", Ct);
            Assert.True(deleted.IsSuccessStatusCode, await deleted.Content.ReadAsStringAsync(Ct));
        }

        public Task<Message> TellAsync(string team, string member, string instruction, long? causation = null) =>
            Log.AppendAsync(new NewMessage(
                MessageTypes.InstructionFor(new ContainerId(team, member)),
                JsonSerializer.Serialize(new { instruction }), "console", causation), Ct);

        public async Task<JsonElement> RunsAsync(string team, DateTimeOffset? from = null, DateTimeOffset? to = null)
        {
            var url = from is { } f && to is { } t ? Url(team, f, t) : $"/api/teams/{team}/tokens/runs";
            var response = await Person.GetAsync(url, Ct);
            var body = await response.Content.ReadAsStringAsync(Ct);
            Assert.True(response.StatusCode == HttpStatusCode.OK, $"{(int)response.StatusCode} from {url}: {body}");
            return JsonDocument.Parse(body).RootElement.Clone();
        }

        public async Task AwaitRowAsync(Func<Message, bool> match, string what) => await AwaitCountAsync(match, 1, what);

        public async Task AwaitCountAsync(Func<Message, bool> match, int count, string what)
        {
            var deadline = DateTime.UtcNow.AddSeconds(30);

            while (DateTime.UtcNow < deadline)
            {
                if ((await Log.ReadAfterAsync(0, [MessageTypes.Started, MessageTypes.Completed, MessageTypes.Failed], int.MaxValue, Ct))
                    .Count(match) >= count)
                {
                    return;
                }

                await Task.Delay(50, Ct);
            }

            throw new TimeoutException($"No row: {what}.");
        }

        /// <summary>Waits until no member of <paramref name="team"/> has a run going and none has
        /// started one for a moment.</summary>
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

                if (!going && newest == last) return;

                last = going ? null : newest;
                await Task.Delay(300, Ct);
            }

            throw new TimeoutException($"{team} did not go quiet.");
        }
    }
}
