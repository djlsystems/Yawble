using System.Text.Json.Nodes;
using Yawble.Core.Bus;
using Yawble.Core.Ipc;
using Yawble.Core.Policy;
using Yawble.Core.Tests.Bus;
using Yawble.Core.Ui;
using Xunit;

namespace Yawble.Core.Tests.Ui;

/// <summary>
/// app.overview reads the open questions of every team with one statement, whatever the number of teams, and its
/// counts equal the per-team reads. teams.agents reads a team's open questions once and the policy every card shares
/// once. A seeded store; nothing runs.
/// </summary>
public class OverviewReadCountTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Rig : IAsyncDisposable
    {
        public TempDb Db { get; } = new();
        public BusStore Store { get; private set; } = null!;
        public BusEngine Engine { get; private set; } = null!;
        public PolicyStore Policies { get; private set; } = null!;
        public MemberLayouts Layouts { get; private set; } = null!;
        public RpcDispatcher Rpc { get; private set; } = null!;
        public List<(string Name, long Id)> Teams { get; } = [];

        public static async Task<Rig> CreateAsync(int teams)
        {
            var rig = new Rig();
            rig.Store = new BusStore(rig.Db.ConnectionString);
            rig.Engine = new BusEngine(rig.Store, new ScriptedRunner(), new BusOptions { IdleOffer = false });
            for (var i = 0; i < teams; i++) await rig.AddTeamAsync($"Team{i}");
            var terminals = new TerminalHub(new AppEvents());
            var home = OperatingSystem.IsWindows() ? @"C:\Users\pat" : "/home/pat";
            var dataDir = OperatingSystem.IsWindows() ? @"C:\Users\pat\AppData\Local\Yawble\data" : "/home/pat/.local/share/yawble/data";
            var install = OperatingSystem.IsWindows() ? @"C:\Program Files\Yawble" : "/opt/yawble";
            rig.Policies = new PolicyStore(rig.Db.ConnectionString);
            rig.Layouts = new MemberLayouts(dataDir, install, home);
            var ui = new UiRpc(rig.Engine, rig.Policies, rig.Layouts, new FakeModelCredentials(),
                new FakeHostTerminals(terminals, TimeSpan.FromMilliseconds(5)), new FakeInteractiveSessions(terminals, rig.Engine), terminals, 16_384);
            rig.Rpc = new RpcDispatcher(new Yawble.Core.Sandboxing.SandboxPlatformInfo(), () => [], ui: ui);
            return rig;
        }

        /// <summary>A team of three: Ada asks twice (both open), Bo asks and is answered, Cy asks nothing.</summary>
        public async Task AddTeamAsync(string team)
        {
            Teams.Add((team, await Engine.CreateTeamAsync(team, ct: Ct)));
            foreach (var m in new[] { "Ada", "Bo", "Cy" }) await Engine.AddMemberAsync(team, m, ct: Ct);
            Ask(team, "Ada", "which database?");
            Ask(team, "Ada", "which region?");
            var answered = Ask(team, "Bo", "ship it?");
            Store.Append(new NewMessage(MessageTypes.InstructionPrefix + team + "/Bo", """{"text":"yes"}""", MessageTypes.PersonSource, answered));
        }

        public long Ask(string team, string member, string question)
        {
            var root = Store.Append(new NewMessage(MessageTypes.InstructionPrefix + team + "/" + member, """{"text":"go"}""", MessageTypes.PersonSource, null)).Seq;
            return Store.Append(new NewMessage(MessageTypes.NeedsDecision, new JsonObject { ["question"] = question }.ToJsonString(), $"{team}/{member}", root)).Seq;
        }

        /// <summary>Runs a call and returns its answer with the bus statements it ran.</summary>
        public async Task<(JsonNode? Answer, List<string> Statements)> Traced(string method, JsonObject? p = null)
        {
            var statements = new List<string>();
            Store.Statement = statements.Add;
            try { return (await Rpc.DispatchAsync(method, p, Ct), statements); }
            finally { Store.Statement = null; }
        }

        public async ValueTask DisposeAsync()
        {
            await Engine.DisposeAsync();
            Db.Dispose();
        }
    }

    private static int OpenQuestionReads(List<string> statements) =>
        statements.Count(s => s.Contains("NOT EXISTS", StringComparison.Ordinal) && s.Contains("causation_seq", StringComparison.Ordinal));

    [Fact]
    public async Task OpenDecisionsByTeam_EqualsEachTeamsOwnRead()
    {
        await using var rig = await Rig.CreateAsync(3);
        rig.Ask("Team1", "Cy", "one more?");

        var all = rig.Store.OpenDecisionsByTeam();

        Assert.Equal(3, all.Count);
        foreach (var (team, _) in rig.Teams)
            Assert.Equal(rig.Store.OpenDecisions(team).Select(m => m.Seq), all[team].Select(m => m.Seq));
        Assert.Equal(3, all["team1"].Count); // by team name, whatever its case
        Assert.Equal(2, all["Team0"].Count);
    }

    [Fact]
    public async Task Overview_ReadsTheOpenQuestionsOnce_WhateverTheNumberOfTeams()
    {
        await using var rig = await Rig.CreateAsync(2);

        var (two, twoStatements) = await rig.Traced("app.overview");
        Assert.Equal(4, two!["needsYou"]!.GetValue<int>());
        Assert.All(two["teams"]!.AsArray(), t => Assert.Equal(1, t!["waiting"]!.GetValue<int>())); // Ada; Bo is answered
        Assert.Equal("question", two["newest"]!["kind"]!.GetValue<string>());
        Assert.Equal(1, OpenQuestionReads(twoStatements));

        for (var i = 2; i < 9; i++) await rig.AddTeamAsync($"Team{i}");
        var (nine, nineStatements) = await rig.Traced("app.overview");
        Assert.Equal(18, nine!["needsYou"]!.GetValue<int>());
        Assert.Equal(9, nine["teams"]!.AsArray().Count);
        Assert.Equal(1, OpenQuestionReads(nineStatements));
        Assert.Equal(twoStatements.Count, nineStatements.Count);
    }

    [Fact]
    public async Task Overview_CountsWhatThePerTeamReadsCount()
    {
        await using var rig = await Rig.CreateAsync(4);
        rig.Ask("Team2", "Cy", "and this?");

        var overview = await rig.Rpc.DispatchAsync("app.overview", null, Ct);

        var expected = 0;
        foreach (var (team, _) in rig.Teams) expected += (await rig.Engine.OpenDecisionsAsync(team, Ct)).Count;
        Assert.Equal(expected, overview!["needsYou"]!.GetValue<int>());
        Assert.Equal(9, expected);
        var waiting = overview["teams"]!.AsArray().ToDictionary(t => t!["name"]!.GetValue<string>(), t => t!["waiting"]!.GetValue<int>());
        Assert.Equal(2, waiting["Team2"]);
        Assert.Equal(1, waiting["Team3"]);
    }

    [Fact]
    public async Task Agents_ReadTheTeamsOpenQuestionsOnce_AndShowWhoAsks()
    {
        await using var rig = await Rig.CreateAsync(2);
        var (name, teamId) = rig.Teams[1];

        var (answer, statements) = await rig.Traced("teams.agents", new JsonObject { ["teamId"] = teamId });

        Assert.Equal(1, OpenQuestionReads(statements));
        var cards = answer!.AsArray().ToDictionary(c => c!["member"]!["name"]!.GetValue<string>(), c => c!.AsObject());
        Assert.Equal("decision", cards["Ada"]["attention"]!.GetValue<string>());
        Assert.Null(cards["Bo"]["attention"]);
        Assert.Null(cards["Cy"]["attention"]);

        // Each card's policy lines are the member's own effective policy.
        var everyone = rig.Store.MemberRows();
        foreach (var m in everyone.Where(m => m.Team == name))
        {
            var own = rig.Policies.Effective(m.Id, rig.Layouts.For(m, everyone), 16_384);
            Assert.Equal(PolicyBanner.Net(own.Policy), cards[m.Name]["net"]?.GetValue<string>());
            Assert.Equal(own.Violations.Count, cards[m.Name]["policyProblems"]!.AsArray().Count);
        }
    }

    [Fact]
    public async Task EffectivePolicy_WithTheTeamsSharedPart_EqualsTheMembersOwnRead()
    {
        await using var rig = await Rig.CreateAsync(1);
        var everyone = rig.Store.MemberRows();
        var shared = rig.Policies.SharedOf(rig.Teams[0].Id);

        foreach (var m in everyone)
        {
            var layout = rig.Layouts.For(m, everyone);
            var own = rig.Policies.Effective(m.Id, layout, 16_384);
            var withShared = rig.Policies.Effective(m.Id, layout, 16_384, shared);
            Assert.Equal(System.Text.Json.JsonSerializer.Serialize(own.Policy), System.Text.Json.JsonSerializer.Serialize(withShared.Policy));
            Assert.Equal(own.Violations, withShared.Violations);
        }
    }
}
