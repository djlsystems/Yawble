using System.Collections;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Harness.Containers;
using Harness.Contracts;
using Harness.Contracts.Link;
using Harness.Host;
using Harness.Host.Link;
using Harness.Tests.Contracts;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests;

/// <summary>
/// THE LINK'S STATUS SUMMARY: what the Teams list, the header's Needs you and the live-status dropdown
/// already show a person, and nothing more. Each figure is read from the source that screen reads; a
/// team crosses as its id and name and counts only; a burst of changes is one status, the latest.
/// </summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class LinkStatusSourceTests
{
    private const string Email = "person@example.test";
    private const string Password = "correct horse battery";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_summary_matches_what_the_teams_list_shows()
    {
        var root = Directory.CreateTempSubdirectory("harness-link-status-").FullName;
        try
        {
            var dataRoot = Path.Combine(root, "data");
            Directory.CreateDirectory(dataRoot);
            await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host => host
                .UseSetting("DataRoot", dataRoot)
                .UseSetting("Logging:LogLevel:Default", "Warning")
                .UseSetting("ScheduleRunnerEnabled", "false")
                .ConfigureTestServices(services => services.AddSingleton<IAgentRunner>(new HeldAgent())));
            var services = factory.Services;
            var registry = services.GetRequiredService<TeamRegistry>();
            var containers = services.GetRequiredService<ContainerHost>();
            var log = services.GetRequiredService<IMessageLog>();
            var agent = services.GetRequiredService<AgentCatalog>().Definitions.First(d => d.Mode == AgentMode.Headless).Name;

            // Decides: its Manager asks a person, its Developer gave up, its Tester is idle.
            var decides = (await registry.CreateAsync("Decides", agent, memberAgent: agent, ct: Ct)).Id;
            await registry.SetPausedAsync(decides, true, Ct);
            await registry.AddContainerAsync(decides, "Developer", agent, "", [], ct: Ct);
            await registry.AddContainerAsync(decides, "Tester", agent, "", [], ct: Ct);
            var manager = containers.Find(new ContainerId(decides, TeamRegistry.DefaultManagerName))!;
            var asked = await log.AppendAsync(new NewMessage(
                MessageTypes.NeedsDecision, """{"question":"SQLite or Postgres?"}""", manager.Id.ToString()), Ct);
            manager.MarkNeedsDecision("SQLite or Postgres?", asked.Seq, asked.CorrelationId);
            manager.NoteMarkRow(asked);
            var developer = containers.Find(new ContainerId(decides, "Developer"))!;
            var gaveUp = await log.AppendAsync(new NewMessage(
                MessageTypes.Blocked, """{"reason":"The tests cannot start."}""", developer.Id.ToString()), Ct);
            developer.MarkBlocked("The tests cannot start.");
            developer.NoteMarkRow(gaveUp);

            // Quiet: one idle Manager, paused. Put away: archived, so the Teams list does not show it.
            var quiet = (await registry.CreateAsync("Quiet", agent, memberAgent: agent, ct: Ct)).Id;
            await registry.SetPausedAsync(quiet, true, Ct);
            var putAway = (await registry.CreateAsync("Put away", agent, memberAgent: agent, ct: Ct)).Id;
            await registry.SetPausedAsync(putAway, true, Ct);
            await registry.SetArchivedAsync(
                putAway, Email, DateTimeOffset.UtcNow, team => new TriggerAudit("test", Email, "team.archived", team, "Put away", null), Ct);

            var source = services.GetRequiredService<StatusSource>();
            await source.RefreshAsync(Ct);
            var summary = source.Current();

            // The Teams list's rows, as teamStatus and memberStateOf read them.
            Assert.Equal([decides, quiet], summary.Teams.Select(t => t.Id).Order(StringComparer.Ordinal));
            var decidesRow = summary.Teams.Single(t => t.Id == decides);
            Assert.Equal(new TeamState(decides, "Decides", "blocked", Running: 0, Idle: 1, Blocked: 1, NeedsYou: 1), decidesRow);
            var quietRow = summary.Teams.Single(t => t.Id == quiet);
            Assert.Equal(new TeamState(quiet, "Quiet", "paused", Running: 0, Idle: 1, Blocked: 0, NeedsYou: 0), quietRow);

            // The header badge: GET /api/needs-you, both parts, counted.
            using var person = await PersonAsync(factory);
            var needsYou = await person.GetFromJsonAsync<JsonElement>("/api/needs-you", Ct);
            Assert.Equal(2, needsYou.GetProperty("items").GetArrayLength());
            Assert.Equal(needsYou.GetProperty("items").GetArrayLength(), summary.NeedsYou);

            // The dropdown: GET /api/service-status's health, build and workers.
            var status = await person.GetFromJsonAsync<JsonElement>(ServiceStatusEndpoints.Route, Ct);
            Assert.Equal(status.GetProperty("status").GetString(), summary.Health);
            Assert.Equal(status.GetProperty("build").GetProperty("version").GetString(), summary.Version);
            Assert.Equal(
                status.GetProperty("workers").EnumerateArray().Select(w => (w.GetProperty("id").GetString()!, w.GetProperty("state").GetString()!)),
                summary.Workers.Select(w => (w.Name, w.State)));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    [Fact]
    public void Member_states_follow_the_order_the_board_draws()
    {
        var idle = new ContainerSnapshot("core", "Dev", "Dev", "agent", ContainerState.Idle, 0, 1, [], null);

        // A mark first - failed, then needs you, then blocked - then the run.
        Assert.Equal(MemberStates.Idle, LinkStatusFacts.MemberStateOf(idle));
        Assert.Equal(MemberStates.Running, LinkStatusFacts.MemberStateOf(idle with { State = ContainerState.Running }));
        Assert.Equal(MemberStates.Waiting, LinkStatusFacts.MemberStateOf(idle with { State = ContainerState.Running, Held = true }));
        Assert.Equal(MemberStates.Waiting, LinkStatusFacts.MemberStateOf(idle with { QueueDepth = 2 }));
        Assert.Equal(MemberStates.Blocked, LinkStatusFacts.MemberStateOf(idle with { Blocked = "stuck", State = ContainerState.Running }));
        Assert.Equal(MemberStates.Decision, LinkStatusFacts.MemberStateOf(idle with { NeedsDecision = "which?", Blocked = "stuck" }));
        Assert.Equal(MemberStates.Failed, LinkStatusFacts.MemberStateOf(idle with { Failed = "exit 1", NeedsDecision = "which?" }));
        Assert.Equal(MemberStates.Decision, LinkStatusFacts.MemberStateOf(idle with { Failed = "sign in", FailureClass = "needs-person" }));
        Assert.Equal(MemberStates.Decision, LinkStatusFacts.MemberStateOf(idle with { NeedsPerson = "a key", Failed = "exit 1" }));

        // The team's word: misconfigured, failed, blocked, paused, running, waiting, queued, undeclared, idle.
        var now = new DateTimeOffset(2026, 11, 2, 9, 0, 0, TimeSpan.Zero);
        TeamSummary Team(params ContainerSnapshot[] members) => new("core", "Core", members);
        Assert.Equal("idle", LinkStatusFacts.TeamStatusOf(Team(idle), null, now));
        Assert.Equal("paused", LinkStatusFacts.TeamStatusOf(Team(idle) with { Paused = true }, null, now));
        Assert.Equal("running", LinkStatusFacts.TeamStatusOf(Team(idle with { State = ContainerState.Running }), null, now));
        Assert.Equal("waiting", LinkStatusFacts.TeamStatusOf(Team(idle with { Held = true }), null, now));
        Assert.Equal("queued", LinkStatusFacts.TeamStatusOf(Team(idle with { QueueDepth = 1 }), null, now));
        Assert.Equal("blocked", LinkStatusFacts.TeamStatusOf(Team(idle with { NeedsPerson = "a key" }) with { Paused = true }, null, now));
        Assert.Equal("misconfigured", LinkStatusFacts.TeamStatusOf(Team(idle with { MissingAgent = "gone", Blocked = "x" }), null, now));

        // Failed and undeclared are the log's, read from the team's workflow as the Teams list reads it.
        var workflow = new TeamWorkflowTiming(
            true, 1, "Running", now.AddMinutes(-10), null, now, 0, false, 1, 0, null, 1, ["Dev"], null,
            [new MemberExecution("Dev", 1, 60, 0)], LastActivityAt: now.AddMinutes(-5));
        Assert.Equal("failed", LinkStatusFacts.TeamStatusOf(Team(idle), workflow, now));
        Assert.Equal("running", LinkStatusFacts.TeamStatusOf(Team(idle with { State = ContainerState.Running }), workflow, now));
        var finished = workflow with { RunsFailed = 0, FailedMembers = [] };
        Assert.Equal("undeclared", LinkStatusFacts.TeamStatusOf(Team(idle), finished, now));
        Assert.Equal("idle", LinkStatusFacts.TeamStatusOf(Team(idle), finished with { LastActivityAt = now.AddMinutes(-1) }, now));
        Assert.Equal("failed", LinkStatusFacts.TeamStatusOf(Team(idle), workflow with { State = "Paused" }, now));
        Assert.Equal("undeclared", LinkStatusFacts.TeamStatusOf(Team(idle), workflow with { State = "Closed" }, now));
    }

    [Fact]
    public void Only_allow_listed_fields_cross()
    {
        // Every field of a team, its members, its workflow and a worker carries a marker of its own.
        var team = Markers.Fill<TeamSummary>("team");
        var workflow = Markers.Fill<TeamWorkflowTiming>("workflow");
        var worker = Markers.Fill<WorkerSample>("worker");
        Assert.Contains("marker-team-AdditionalInstructions", JsonSerializer.Serialize(team));
        Assert.Contains("marker-team-Root", JsonSerializer.Serialize(team));
        Assert.Contains("marker-team-Containers-Blocked", JsonSerializer.Serialize(team));

        var summary = LinkStatusFacts.Summarize(
            healthy: true, "1.2.3", [worker], [(team, workflow)], needsYou: 3, DateTimeOffset.UtcNow);
        var wire = LinkJson.Serialize(new Status(1, DateTimeOffset.UtcNow, summary));

        var crossed = Markers.In(wire).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(["marker-team-Id", "marker-team-Name", "marker-worker-Id"], crossed);
    }

    [Fact]
    public async Task A_burst_of_changes_sends_one_status_with_the_latest_state()
    {
        const string credential = "status-credential-91c2";
        await using var database = new SqliteIdentityDatabase();
        await using var service = new LinkClientTests.FakeLinkService();
        await service.StartAsync();
        var protection = new EphemeralDataProtectionProvider();
        var clock = new LinkClientTests.LinkClock(new DateTimeOffset(2026, 11, 2, 9, 0, 0, TimeSpan.Zero));
        var now = clock.GetUtcNow();
        await database.Links().SaveAsync(new StoredLink(
            service.Url, "Manage", "office", LinkCredential.Protect(protection, credential), now, LinkState.Connecting, now, null, null), Ct);

        // The real source, reading a team whose Needs you the test moves.
        var needsYou = 0;
        var reads = 0;
        var source = new StatusSource(clock, new LinkClientTests.ListLogger<StatusSource>())
        {
            Read = _ =>
            {
                Interlocked.Increment(ref reads);
                var n = Volatile.Read(ref needsYou);
                return Task.FromResult(new StatusSummary(
                    StatusSummary.Healthy, "1.2.3", [], [new TeamState("core", "Core", "running", 1, 0, 0, n)], n));
            },
        };
        await source.RefreshAsync(Ct);

        using var client = new LinkClient(
            database.Links(), protection, database.TenantLog(), source, clock, new LinkClientTests.ListLogger<LinkClient>(), () => 1.0);
        await client.StartAsync(Ct);
        try
        {
            var session = await service.NextSessionAsync();
            Assert.IsType<Hello>(await session.NextFrameAsync());
            await session.SendAsync(new Welcome("Manage", "office", HeartbeatSeconds: 300));
            var first = Assert.IsType<Status>(await session.NextFrameAsync());
            Assert.Equal(0, first.Summary.NeedsYou);

            // A burst of five changes. Current() is the latest throughout.
            for (var i = 1; i <= 5; i++)
            {
                Volatile.Write(ref needsYou, i);
                source.Notify();
                await Until(() => source.Current().NeedsYou == i);
            }

            // One wait of 2 s on the clock, then one status, the latest.
            var due = await clock.NextDueAsync();
            Assert.Equal(StatusSource.Quiet, due - clock.GetUtcNow());
            clock.Advance(StatusSource.Quiet);
            var sent = Assert.IsType<Status>(await session.NextFrameAsync());
            Assert.Equal(5, sent.Summary.NeedsYou);
            Assert.Equal(5, sent.Summary.Teams.Single().NeedsYou);
            Assert.True(sent.Seq > first.Seq);

            // Nothing else follows it: the next frame is the heartbeat, 300 s after the welcome.
            due = await clock.NextDueAsync();
            clock.Advance(due - clock.GetUtcNow());
            Assert.IsType<Heartbeat>(await session.NextFrameAsync());

            // A change that changes nothing is not a status either.
            source.Notify();
            due = await clock.NextDueAsync();
            Assert.Equal(StatusSource.Quiet, due - clock.GetUtcNow());
            clock.Advance(StatusSource.Quiet);
            due = await clock.NextDueAsync();
            clock.Advance(due - clock.GetUtcNow());
            Assert.IsType<Heartbeat>(await session.NextFrameAsync());
        }
        finally
        {
            await client.StopAsync(CancellationToken.None);
        }
    }

    // ---- HELPERS ----

    private static async Task<HttpClient> PersonAsync(WebApplicationFactory<Program> factory)
    {
        var users = factory.Services.GetRequiredService<IUserStore>();
        if (await users.FindAsync(Email, Ct) is null) await users.CreateAsync(Email, Password);

        var client = factory.CreateClient();
        (await client.PostAsJsonAsync("/api/auth/login", new { email = Email, password = Password }, Ct)).EnsureSuccessStatusCode();
        return client;
    }

    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("The condition did not come true in time.");
            await Task.Delay(10, Ct);
        }
    }

    private sealed class HeldAgent : IAgentRunner
    {
        public async Task<AgentResult> RunAsync(AgentInvocation invocation, CancellationToken ct = default)
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new AgentResult(0, "done");
        }
    }

    /// <summary>
    /// Builds a record with a marker in every string it holds - <c>marker-{path}</c> - through its
    /// primary constructor, nested records and lists of them included, so a field added later is
    /// marked without this test changing.
    /// </summary>
    private static class Markers
    {
        public static T Fill<T>(string path) => (T)Fill(typeof(T), path, depth: 0)!;

        public static IEnumerable<string> In(string text) =>
            System.Text.RegularExpressions.Regex.Matches(text, "marker-[A-Za-z0-9-]+").Select(m => m.Value).Distinct();

        private static object? Fill(Type type, string path, int depth)
        {
            type = Nullable.GetUnderlyingType(type) ?? type;
            if (type == typeof(string)) return $"marker-{path}";
            if (type == typeof(bool)) return true;
            if (type == typeof(int) || type == typeof(long) || type == typeof(double)) return Convert.ChangeType(7, type);
            if (type == typeof(DateTimeOffset)) return new DateTimeOffset(2026, 11, 2, 9, 0, 0, TimeSpan.Zero);
            if (type.IsEnum) return Enum.GetValues(type).GetValue(0);
            if (depth > 4) return null;

            if (type.IsGenericType && typeof(IEnumerable).IsAssignableFrom(type))
            {
                var arguments = type.GetGenericArguments();
                if (arguments.Length == 2)
                {
                    var dictionary = (IDictionary)Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(arguments))!;
                    dictionary[Fill(arguments[0], path + "-Key", depth + 1)!] = Fill(arguments[1], path + "-Value", depth + 1);
                    return dictionary;
                }

                var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(arguments[0]))!;
                list.Add(Fill(arguments[0], path, depth + 1));
                return list;
            }

            var constructor = type.GetConstructors().OrderByDescending(c => c.GetParameters().Length).FirstOrDefault();
            if (constructor is null) return null;

            return constructor.Invoke([.. constructor.GetParameters().Select(p => Fill(p.ParameterType, $"{path}-{p.Name}", depth + 1))]);
        }
    }
}
