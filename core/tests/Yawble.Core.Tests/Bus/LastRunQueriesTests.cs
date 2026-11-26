using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Yawble.Core.Bus;
using Yawble.Core.Scheduling;
using Xunit;

namespace Yawble.Core.Tests.Bus;

/// <summary>
/// The member cards and the scheduler snapshot read only the runs they show: each member's newest run through the
/// member index, the live runs by id, and the wait totals from the runs made since the last snapshot. Their results
/// equal what the whole run history gives, and no statement of a cards call or of a repeated snapshot scans
/// <c>runs</c>. A seeded store with a manual clock; no run is started.
/// </summary>
public class LastRunQueriesTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly DateTimeOffset Start = new(2030, 1, 1, 8, 0, 0, TimeSpan.Zero);

    private sealed class Rig : IAsyncDisposable
    {
        public TempDb Db { get; } = new();
        public DeliverableExpiryTests.ManualClock Clock { get; } = new(Start);
        public BusStore Store { get; private set; } = null!;
        public BusEngine Engine { get; private set; } = null!;
        public Dictionary<string, long> Teams { get; } = [];
        public long Root { get; private set; }
        private long _queueId;

        public static async Task<Rig> CreateAsync()
        {
            var rig = new Rig();
            rig.Store = new BusStore(rig.Db.ConnectionString, rig.Clock);
            rig.Engine = new BusEngine(rig.Store, new ScriptedRunner(), new BusOptions { IdleOffer = false });
            foreach (var (team, members) in new[] { ("Alpha", new[] { "Ada", "Bo", "Cy" }), ("Beta", ["Dee", "Eli"]) })
            {
                rig.Teams[team] = await rig.Engine.CreateTeamAsync(team, ct: Ct);
                foreach (var m in members) await rig.Engine.AddMemberAsync(team, m, ct: Ct);
            }
            rig.Root = rig.Store.Append(new NewMessage(MessageTypes.InstructionPrefix + "Alpha/Ada", """{"text":"go"}""", MessageTypes.PersonSource, null)).Seq;
            return rig;
        }

        /// <summary>One ended run of a member that waited <paramref name="waitMs"/> for its slot.</summary>
        public long Run(string team, string member, int waitMs, string state, string? reason = null, string? failureClass = null)
        {
            var id = Store.Member(team, member)!.Id;
            var queuedAt = Store.Now();
            Clock.Advance(TimeSpan.FromMilliseconds(waitMs));
            var run = Store.InsertRun(id, ++_queueId, Root, Root, 1, queuedAt);
            Clock.Advance(TimeSpan.FromSeconds(3));
            Store.EndRun(run, state, reason);
            if (failureClass is not null) Store.SetFailureClass(run, failureClass);
            return run;
        }

        /// <summary>Runs spread over every member but Cy, who never runs.</summary>
        public void Seed(int runs)
        {
            (string Team, string Member)[] who = [("Alpha", "Ada"), ("Alpha", "Bo"), ("Beta", "Dee"), ("Beta", "Eli")];
            Store.InTransaction(() =>
            {
                for (var i = 0; i < runs; i++)
                {
                    var (team, member) = who[i % who.Length];
                    var failed = i % 5 == 0;
                    Run(team, member, 10 + i * 7 % 900, failed ? "failed" : "completed", failed ? $"broke {i}" : null, failed ? FailureClasses.NotStarted : null);
                }
                return 0;
            });
        }

        public async ValueTask DisposeAsync()
        {
            await Engine.DisposeAsync();
            Db.Dispose();
        }
    }

    private static long WaitMs(BusStore.RunRow r) =>
        (long)(DateTimeOffset.Parse(r.StartedAt, System.Globalization.CultureInfo.InvariantCulture)
               - DateTimeOffset.Parse(r.QueuedAt, System.Globalization.CultureInfo.InvariantCulture)).TotalMilliseconds;

    [Fact]
    public async Task LastRuns_OfATeam_AreEachMembersNewestRun_AsTheWholeHistoryGives()
    {
        await using var rig = await Rig.CreateAsync();
        rig.Seed(203);
        var all = rig.Store.Runs();

        foreach (var (team, teamId) in rig.Teams)
        {
            var members = rig.Store.MemberRows(teamId);
            var expected = members.Select(m => all.Where(r => r.MemberId == m.Id).MaxBy(r => r.Id)).Where(r => r is not null)
                .ToDictionary(r => r!.MemberId, r => r!);
            var last = rig.Store.LastRuns(teamId);
            Assert.Equal(expected.OrderBy(e => e.Key), last.OrderBy(e => e.Key));
            foreach (var m in members)
                Assert.Equal(all.Where(r => r.MemberId == m.Id).MaxBy(r => r.Id), rig.Store.LastRun(m.Id));
            Assert.True(team != "Alpha" || !last.ContainsKey(members.Single(m => m.Name == "Cy").Id), "a member that never ran has no last run");
        }

        long[] ids = [all[3].Id, all[77].Id, 999_999];
        Assert.Equal([all[3], all[77]], rig.Store.RunsById(ids));
        Assert.Empty(rig.Store.RunsById([]));
    }

    [Fact]
    public async Task Cards_ShowEachMembersNewestRun()
    {
        await using var rig = await Rig.CreateAsync();
        rig.Seed(60);
        rig.Run("Alpha", "Bo", 40, "failed", "the agent stopped", FailureClasses.NotStarted);
        var all = rig.Store.Runs();

        foreach (var teamId in rig.Teams.Values)
        {
            foreach (var card in await rig.Engine.AgentCardsAsync(teamId, Ct))
            {
                var last = all.Where(r => r.MemberId == card.Member.Id).MaxBy(r => r.Id);
                Assert.Equal((last?.State, last?.Reason, last?.FailureClass), (card.LastRunState, card.LastRunReason, card.LastRunFailureClass));
            }
        }
        var bo = (await rig.Engine.AgentCardsAsync(rig.Teams["Alpha"], Ct)).Single(c => c.Member.Name == "Bo");
        Assert.Equal(("failed", "the agent stopped"), (bo.LastRunState, bo.LastRunReason));
    }

    [Fact]
    public async Task Snapshot_TeamWaits_EqualTheWholeHistorys_AlsoAfterMoreRuns()
    {
        await using var rig = await Rig.CreateAsync();

        async Task Check()
        {
            var all = rig.Store.Runs();
            var teamOf = rig.Store.AllMembers().ToDictionary(m => m.Id, m => m.Team);
            var snap = await rig.Engine.SchedulerSnapshotAsync(Ct);
            Assert.Equal(["Alpha", "Beta"], snap.Teams.Select(t => t.Team));
            foreach (var load in snap.Teams)
            {
                var waits = all.Where(r => teamOf[r.MemberId] == load.Team).Select(WaitMs).ToArray();
                Assert.Equal(new TeamLoad(load.Team, 0, 0, waits.Length, waits.Sum(), waits.Length == 0 ? 0 : waits.Max()), load);
            }
        }

        await Check(); // no runs yet
        rig.Seed(120);
        await Check();
        rig.Run("Beta", "Eli", 5_000, "completed");
        rig.Seed(31);
        await Check();
        await Check(); // nothing new: the same totals again
    }

    [Fact]
    public async Task ARemovedMembersRuns_LeaveItsTeamsWaits()
    {
        await using var rig = await Rig.CreateAsync();
        rig.Seed(40);
        var before = (await rig.Engine.SchedulerSnapshotAsync(Ct)).Teams.Single(t => t.Team == "Beta");
        var dee = rig.Store.Member("Beta", "Dee")!;
        var deeRuns = rig.Store.Runs().Where(r => r.MemberId == dee.Id).ToArray();

        await rig.Engine.RemoveMemberAsync(dee.Id, Ct);

        var after = (await rig.Engine.SchedulerSnapshotAsync(Ct)).Teams.Single(t => t.Team == "Beta");
        Assert.Equal(before.Admitted - deeRuns.Length, after.Admitted);
        Assert.Equal(before.TotalWaitMs - deeRuns.Sum(WaitMs), after.TotalWaitMs);
    }

    [Fact]
    public async Task NoStatement_OfACardsCall_OrOfARepeatedSnapshot_ScansTheRuns()
    {
        await using var rig = await Rig.CreateAsync();
        rig.Seed(400);
        await rig.Engine.SchedulerSnapshotAsync(Ct); // the first snapshot of a process reads the history once

        var statements = new List<string>();
        rig.Store.Statement = statements.Add;
        var queries = rig.Store.Queries;
        await rig.Engine.AgentCardsAsync(rig.Teams["Alpha"], Ct);
        var cardQueries = rig.Store.Queries - queries;
        await rig.Engine.SchedulerSnapshotAsync(Ct);
        _ = rig.Store.LastRun(rig.Store.Member("Alpha", "Ada")!.Id);
        rig.Store.Statement = null;

        var onRuns = statements.Where(s => Regex.IsMatch(s, @"\bruns\b")).ToArray();
        Assert.True(onRuns.Length >= 4, "the cards, the snapshot (live runs and new waits) and the last run each read runs");
        using var connection = new SqliteConnection(rig.Db.ConnectionString);
        connection.Open();
        foreach (var sql in onRuns)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "EXPLAIN QUERY PLAN " + sql;
            foreach (var name in Regex.Matches(sql, @"\$\w+").Select(m => m.Value).Distinct())
                cmd.Parameters.AddWithValue(name, name == "$ids" ? "[1,2]" : 1);
            using var reader = cmd.ExecuteReader();
            var plan = new List<string>();
            while (reader.Read()) plan.Add(reader.GetString(3));
            Assert.DoesNotContain(plan, step => Regex.IsMatch(step, @"^SCAN (runs|r)\b"));
            Assert.Contains(plan, step => step.StartsWith("SEARCH runs", StringComparison.Ordinal));
        }

        // The cards run the same statements whatever the history's length.
        rig.Seed(400);
        queries = rig.Store.Queries;
        await rig.Engine.AgentCardsAsync(rig.Teams["Alpha"], Ct);
        Assert.Equal(cardQueries, rig.Store.Queries - queries);
    }
}
