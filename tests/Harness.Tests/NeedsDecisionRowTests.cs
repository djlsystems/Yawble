using System.Text.Json;
using Harness.Containers;
using Harness.Contracts;
using Harness.Host;
using Harness.Kanban;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests;

/// <summary>
/// A member waiting on a decision, and its card in `needs-decision`, name the row they wait on: its
/// seq as `needsDecisionSeq` and its workflow as `needsDecisionCorrelation`, the newest when several
/// are open. A blocked or failed member or card carries neither, and the JSON omits them.
/// </summary>
public sealed class NeedsDecisionRowTests
{
    private static readonly ContainerId Dev = new("alpha", "dev");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static JsonElement Json<T>(T value) =>
        JsonSerializer.SerializeToElement(value, JsonSerializerOptions.Web);

    private static async Task<(ContainerTestBed Bed, MemberReports Reports)> RunningAsync(TaskCompletionSource release)
    {
        var bed = new ContainerTestBed();
        var started = new TaskCompletionSource();

        bed.Agent.Behaviour = async _ =>
        {
            started.TrySetResult();
            await release.Task;
            return new AgentResult(0, "done");
        };

        await bed.AddAsync(Dev);
        await bed.Store.AppendAsync(
            new NewMessage(MessageTypes.InstructionFor(Dev), """{"instruction":"one"}""", "console"), Ct);
        Assert.True(await bed.PumpUntilAsync(() => started.Task.IsCompleted));

        return (bed, new MemberReports(bed.Host, bed.Store, new RunHeartbeat()));
    }

    [Fact]
    public async Task The_member_snapshot_names_the_newest_needs_decision_row_and_its_workflow()
    {
        var release = new TaskCompletionSource();
        var (bed, reports) = await RunningAsync(release);
        await using var _ = bed;

        await reports.NeedsDecisionAsync(Dev, "first?", Ct);
        await reports.NeedsDecisionAsync(Dev, "second?", Ct);

        var newest = (await bed.OfTypeAsync(MessageTypes.NeedsDecision)).MaxBy(row => row.Seq)!;
        var snapshot = bed.Host.Find(Dev)!.Snapshot();

        Assert.Equal("second?", snapshot.NeedsDecision);
        Assert.Equal(newest.Seq, snapshot.NeedsDecisionSeq);
        Assert.Equal(newest.CorrelationId, snapshot.NeedsDecisionCorrelation);

        var json = Json(snapshot);
        Assert.Equal(newest.Seq, json.GetProperty("needsDecisionSeq").GetInt64());
        Assert.Equal(newest.CorrelationId, json.GetProperty("needsDecisionCorrelation").GetInt64());
        release.SetResult();
    }

    [Fact]
    public async Task A_blocked_member_carries_neither_and_the_json_omits_them()
    {
        var release = new TaskCompletionSource();
        var (bed, reports) = await RunningAsync(release);
        await using var _ = bed;

        await reports.BlockedAsync(Dev, "no key", ct: Ct);

        var snapshot = bed.Host.Find(Dev)!.Snapshot();
        Assert.Equal("no key", snapshot.Blocked);
        Assert.Null(snapshot.NeedsDecisionSeq);
        Assert.Null(snapshot.NeedsDecisionCorrelation);

        var json = Json(snapshot);
        Assert.False(json.TryGetProperty("needsDecisionSeq", out var _));
        Assert.False(json.TryGetProperty("needsDecisionCorrelation", out var _));
        release.SetResult();
    }

    [Fact]
    public async Task A_failed_member_carries_neither()
    {
        await using var bed = new ContainerTestBed();
        bed.Agent.Behaviour = _ => Task.FromResult(new AgentResult(1, ""));
        await bed.AddAsync(Dev);

        await bed.Store.AppendAsync(
            new NewMessage(MessageTypes.InstructionFor(Dev), """{"instruction":"one"}""", "console"), Ct);
        Assert.True(await bed.PumpUntilAsync(() => bed.Host.Find(Dev)!.Snapshot().Failed is not null));

        var snapshot = bed.Host.Find(Dev)!.Snapshot();
        Assert.Null(snapshot.NeedsDecisionSeq);
        Assert.Null(snapshot.NeedsDecisionCorrelation);
    }

    [Fact]
    public async Task The_next_wake_clears_the_row_with_the_question()
    {
        var release = new TaskCompletionSource();
        var (bed, reports) = await RunningAsync(release);
        await using var _ = bed;

        await reports.NeedsDecisionAsync(Dev, "which one?", Ct);
        release.SetResult();
        await bed.SettleAsync();
        Assert.NotNull(bed.Host.Find(Dev)!.Snapshot().NeedsDecisionSeq);

        await bed.Store.AppendAsync(
            new NewMessage(MessageTypes.InstructionFor(Dev), """{"instruction":"two"}""", "console"), Ct);
        Assert.True(await bed.PumpUntilAsync(() => bed.Host.Find(Dev)!.Snapshot().NeedsDecision is null));

        var snapshot = bed.Host.Find(Dev)!.Snapshot();
        Assert.Null(snapshot.NeedsDecisionSeq);
        Assert.Null(snapshot.NeedsDecisionCorrelation);
    }

    [Fact]
    public async Task A_restarted_host_brings_the_member_back_naming_its_newest_needs_decision_row()
    {
        var root = Directory.CreateTempSubdirectory("harness-decision-restart-").FullName;
        try
        {
            ContainerId member;
            Message newest;
            await using (var host = StartHost(root))
            {
                var registry = host.Services.GetRequiredService<TeamRegistry>();
                var agent = host.Services.GetRequiredService<AgentCatalog>().Definitions.First(d => d.Mode == AgentMode.Headless).Name;
                var team = (await registry.CreateAsync("Alpha", agent, memberAgent: agent, ct: Ct)).Id;
                var added = await registry.AddContainerAsync(team, "DeveloperRowan", agent, "", [], ct: Ct);
                member = new ContainerId(added.Team, added.Name);

                // Two open decisions, the second under the first's workflow, so the newest is the one
                // that must come back and its seq and workflow are different numbers.
                var log = host.Services.GetRequiredService<IMessageLog>();
                var first = await log.AppendAsync(new NewMessage(MessageTypes.NeedsDecision, """{"question":"first?"}""", member.ToString()), Ct);
                newest = await log.AppendAsync(new NewMessage(MessageTypes.NeedsDecision, """{"question":"second?"}""", member.ToString(), first.Seq), Ct);
                Assert.NotEqual(newest.Seq, newest.CorrelationId);
            }

            await using var restarted = StartHost(root);
            var snapshot = restarted.Services.GetRequiredService<ContainerHost>().Find(member)!.Snapshot();

            Assert.Equal("second?", snapshot.NeedsDecision);
            Assert.Equal(newest.Seq, snapshot.NeedsDecisionSeq);
            Assert.Equal(newest.CorrelationId, snapshot.NeedsDecisionCorrelation);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
        }
    }

    /// <summary>The real Host over <paramref name="root"/>, its agents a fake that runs nothing.</summary>
    private static WebApplicationFactory<Program> StartHost(string root) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder
            .UseSetting("DataRoot", root)
            .UseSetting("Logging:LogLevel:Default", "Warning")
            .ConfigureTestServices(services => services.AddSingleton<IAgentRunner>(new FakeAgent())));

    private const long Workflow = 7;
    private const string CardId = "7_Developer";
    private static string ToldType => MessageTypes.InstructionPrefix + "Alpha/Developer";

    private static Message Row(long seq, string type, string source, string payload = "{}") =>
        new(seq, type, payload, source, Workflow, seq > 1 ? seq - 1 : null, 0, DateTimeOffset.UnixEpoch.AddSeconds(seq));

    private static List<Message> Running() =>
    [
        Row(1, ToldType, "Alpha/Manager", """{"instruction":"Do it"}"""),
        Row(2, MessageTypes.Started, "Alpha/Developer", $$"""{"trigger":"{{ToldType}}"}"""),
    ];

    private static Message Decision(long seq, string question) =>
        Row(seq, MessageTypes.NeedsDecision, "Alpha/Developer", $$"""{"question":"{{question}}"}""");

    private static KanbanCardDetail Card(IEnumerable<Message> rows) =>
        KanbanProjector.ProjectCard(rows.ToList(), CardId) ?? throw new InvalidOperationException("no card");

    [Fact]
    public void A_needs_decision_card_names_the_newest_row_and_its_workflow()
    {
        var card = Card([.. Running(), Decision(3, "first?"), Decision(4, "second?")]);

        Assert.Equal("needs-decision", card.Status);
        Assert.Equal(4, card.NeedsDecisionSeq);
        Assert.Equal(Workflow, card.NeedsDecisionCorrelation);

        var json = Json<KanbanCard>(card);
        Assert.Equal(4, json.GetProperty("needsDecisionSeq").GetInt64());
        Assert.Equal(Workflow, json.GetProperty("needsDecisionCorrelation").GetInt64());
    }

    [Fact]
    public void A_blocked_or_failed_card_carries_neither_and_the_json_omits_them()
    {
        var blocked = Card([.. Running(), Row(3, MessageTypes.Blocked, "Alpha/Developer", """{"reason":"no key"}""")]);
        var failed = Card([.. Running(), Row(3, MessageTypes.Failed, "Alpha/Developer", """{"launchError":"gone"}""")]);
        var decidedThenFailed = Card([.. Running(), Decision(3, "which?"), Row(4, MessageTypes.Failed, "Alpha/Developer", """{"launchError":"gone"}""")]);

        foreach (var (card, status) in new[] { (blocked, "blocked"), (failed, "failed"), (decidedThenFailed, "failed") })
        {
            Assert.Equal(status, card.Status);
            Assert.Null(card.NeedsDecisionSeq);
            Assert.Null(card.NeedsDecisionCorrelation);

            var json = Json<KanbanCard>(card);
            Assert.False(json.TryGetProperty("needsDecisionSeq", out var _));
            Assert.False(json.TryGetProperty("needsDecisionCorrelation", out var _));
        }
    }

    [Fact]
    public void A_card_started_again_after_its_decision_carries_neither()
    {
        var card = Card([.. Running(), Decision(3, "which?"), Row(4, MessageTypes.Started, "Alpha/Developer", $$"""{"trigger":"{{ToldType}}"}""")]);

        Assert.NotEqual("needs-decision", card.Status);
        Assert.Null(card.NeedsDecisionSeq);
        Assert.Null(card.NeedsDecisionCorrelation);
    }
}
