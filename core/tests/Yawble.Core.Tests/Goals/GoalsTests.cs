using System.Text.Json;
using System.Text.Json.Nodes;
using Yawble.Core.Board;
using Yawble.Core.Bus;
using Yawble.Core.Companion;
using Yawble.Core.Concierge;
using Yawble.Core.Goals;
using Yawble.Core.Ipc;
using Yawble.Core.Link;
using Yawble.Core.Policy;
using Yawble.Core.Tests.Bus;
using Yawble.Core.Ui;
using Xunit;

namespace Yawble.Core.Tests.Goals;

/// <summary>
/// Goals: the store's rules, a goal's calendar time, goals.* and the goal on deliverables.list / deliverables.get through
/// the real UiRpc over RpcDispatcher, the Visitor's refusal, and the Concierge's and companion's goal tools. Fakes only: a
/// temp database, a fake clock, a fake runner that is never started (each deliverable's messages are written straight to
/// the log).
/// </summary>
public class GoalsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly DateTimeOffset Start = new(2026, 11, 18, 9, 0, 0, TimeSpan.Zero);

    private sealed class Rig : IAsyncDisposable
    {
        public TempDb Db { get; } = new();
        public DeliverableExpiryTests.ManualClock Clock { get; } = new(Start);
        public BusStore Store { get; private set; } = null!;
        public BusEngine Engine { get; private set; } = null!;
        public RpcDispatcher Rpc { get; private set; } = null!;
        public ConfirmationBook Book { get; } = new();

        public static async Task<Rig> CreateAsync()
        {
            var rig = new Rig();
            rig.Store = new BusStore(rig.Db.ConnectionString, rig.Clock);
            rig.Engine = new BusEngine(rig.Store, new ScriptedRunner(), new BusOptions { IdleOffer = false });
            rig.Engine.Board = new BoardService(new BoardStore(rig.Db.ConnectionString, rig.Clock), rig.Engine);
            await rig.Engine.CreateTeamAsync("Alpha", ct: Ct);
            await rig.Engine.AddMemberAsync("Alpha", "Ada", ct: Ct);
            var terminals = new TerminalHub(new AppEvents());
            var home = OperatingSystem.IsWindows() ? @"C:\Users\pat" : "/home/pat";
            var dataDir = OperatingSystem.IsWindows() ? @"C:\Users\pat\AppData\Local\Yawble\data" : "/home/pat/.local/share/yawble/data";
            var install = OperatingSystem.IsWindows() ? @"C:\Program Files\Yawble" : "/opt/yawble";
            var ui = new UiRpc(rig.Engine, new PolicyStore(rig.Db.ConnectionString), new MemberLayouts(dataDir, install, home), new FakeModelCredentials(),
                new FakeHostTerminals(terminals, TimeSpan.FromMilliseconds(5)), new FakeInteractiveSessions(terminals, rig.Engine), terminals, 16_384);
            rig.Rpc = new RpcDispatcher(new Yawble.Core.Sandboxing.SandboxPlatformInfo(), () => [], ui: ui);
            return rig;
        }

        /// <summary>A deliverable started by the person telling Ada something, a minute after the last thing that happened.</summary>
        public long Tell(string text)
        {
            Clock.Advance(TimeSpan.FromMinutes(1));
            return Store.Append(new NewMessage(MessageTypes.InstructionPrefix + "Alpha/Ada", new JsonObject { ["text"] = text }.ToJsonString(), MessageTypes.PersonSource, null)).Seq;
        }

        public void Complete(long deliverable)
        {
            Clock.Advance(TimeSpan.FromMinutes(1));
            var last = Store.Deliverable(deliverable)[^1].Seq;
            Store.Append(new NewMessage(MessageTypes.DeliverableCompleted, JsonSerializer.Serialize(new { delivered = "done" }), "Alpha/Ada", last));
        }

        public async Task<JsonObject> Call(string method, object? args = null) =>
            (await Rpc.DispatchAsync(method, args is null ? null : JsonSerializer.SerializeToNode(args)!.AsObject(), Ct))!.AsObject();

        public Task<JsonNode?> Route(string method, JsonObject? p, CancellationToken ct) => Rpc.DispatchAsync(method, p, ct);

        /// <summary>The Concierge's tools in a new turn of conversation c1 (the person's yes is a later turn than the list).</summary>
        public ConciergeTools Tools(bool withActions = true)
        {
            var turn = new ConciergeTurn("c1", byPerson: true, remote: false);
            var empty = new Func<string, JsonObject?, CancellationToken, Task<JsonNode?>>((_, _, _) => Task.FromResult<JsonNode?>(new JsonObject()));
            var actions = withActions
                ? new ConciergeActions(Engine, empty, empty, null!, (_, _) => Task.FromResult<IReadOnlyList<ActionRepo>>([]), Book, turn: turn)
                : null;
            return new ConciergeTools(Engine, new ConciergeStore(Db.ConnectionString), Route, () => null, "c1", null, actions, remote: false, turn);
        }

        public async ValueTask DisposeAsync()
        {
            await Engine.DisposeAsync();
            Db.Dispose();
        }
    }

    private static async Task<string> Refused(Func<Task> call) =>
        (await Assert.ThrowsAsync<RpcException>(call)).Code;

    private static long Id(JsonNode? n) => n!["id"]!.GetValue<long>();

    private static JsonObject Row(JsonObject page, long id) =>
        page["deliverables"]!.AsArray().Single(d => d!["id"]!.GetValue<long>() == id)!.AsObject();

    private static List<long> Ids(JsonNode? array) => [.. array!.AsArray().Select(d => d!.GetValue<long>())];

    // ------------------------------------------------------------------ rules

    [Fact]
    public async Task Names_AreTrimmed_UniqueIgnoringCase_OneLine_AndLimited_AndAnEditKeepsTheDescriptionWhenNoneIsGiven()
    {
        await using var rig = await Rig.CreateAsync();
        var g = await rig.Call("goals.save", new { name = "  Launch the site  ", description = " For customers " });
        Assert.Equal(("Launch the site", "For customers"), (g["name"]!.GetValue<string>(), g["description"]!.GetValue<string>()));
        Assert.Equal("bad_params", await Refused(() => rig.Call("goals.save", new { name = "launch THE site" })));
        Assert.Equal("bad_params", await Refused(() => rig.Call("goals.save", new { name = "   " })));
        Assert.Equal("bad_params", await Refused(() => rig.Call("goals.save", new { name = new string('x', GoalRules.NameMax + 1) })));
        Assert.Equal("bad_params", await Refused(() => rig.Call("goals.save", new { name = "two\nlines" })));
        Assert.Equal("bad_params", await Refused(() => rig.Call("goals.save", new { name = "Other", description = new string('d', GoalRules.DescriptionMax + 1) })));
        Assert.Equal("no_such_goal", await Refused(() => rig.Call("goals.save", new { id = 999, name = "Ghost" })));

        // The same goal may change its own name's case; the description stays when none is given.
        var renamed = await rig.Call("goals.save", new { id = Id(g), name = "LAUNCH the site" });
        Assert.Equal(("LAUNCH the site", "For customers"), (renamed["name"]!.GetValue<string>(), renamed["description"]!.GetValue<string>()));
        var cleared = await rig.Call("goals.save", new { id = Id(g), name = "Launch", description = "" });
        Assert.Equal("", cleared["description"]!.GetValue<string>());
        Assert.Equal(["Launch"], (await rig.Call("goals.list"))["goals"]!.AsArray().Select(x => x!["name"]!.GetValue<string>()));
    }

    [Fact]
    public async Task ADeliverableHasAtMostOneGoal_AssigningMovesIt_AndDeletingAGoalUnassignsItsDeliverables()
    {
        await using var rig = await Rig.CreateAsync();
        var a = Id(await rig.Call("goals.save", new { name = "A" }));
        var b = Id(await rig.Call("goals.save", new { name = "B" }));
        var d1 = rig.Tell("one");
        var d2 = rig.Tell("two");

        var set = await rig.Call("goals.assign", new { deliverable = d1, goal = a });
        Assert.Equal(("A", d1), (set["goal"]!["name"]!.GetValue<string>(), set["deliverable"]!.GetValue<long>()));
        await rig.Call("goals.assign", new { deliverable = d2, goal = a });
        await rig.Call("goals.assign", new { deliverable = d1, goal = b }); // moves: never in two goals
        var list = await rig.Call("goals.list");
        var goals = list["goals"]!.AsArray();
        Assert.Equal([d2], Ids(goals.Single(g => Id(g) == a)!["deliverables"]));
        Assert.Equal([d1], Ids(goals.Single(g => Id(g) == b)!["deliverables"]));
        Assert.Empty(Ids(list["unassigned"]!["deliverables"]));

        var none = await rig.Call("goals.assign", new JsonObject { ["deliverable"] = d1, ["goal"] = null });
        Assert.Null(none["goal"]);
        Assert.Equal("no_such_goal", await Refused(() => rig.Call("goals.assign", new { deliverable = d1, goal = 999 })));
        Assert.Equal("no_such_deliverable", await Refused(() => rig.Call("goals.assign", new { deliverable = 99_999, goal = a })));
        Assert.Equal("bad_params", await Refused(() => rig.Call("goals.assign", new { deliverable = d1 })));

        var deleted = await rig.Call("goals.delete", new { id = a });
        Assert.Equal((a, 1L), (deleted["deleted"]!.GetValue<long>(), deleted["unassigned"]!.GetValue<long>()));
        Assert.Equal("no_such_goal", await Refused(() => rig.Call("goals.delete", new { id = a })));
        list = await rig.Call("goals.list");
        Assert.Equal([b], list["goals"]!.AsArray().Select(Id));
        Assert.Equal([d2, d1], Ids(list["unassigned"]!["deliverables"])); // newest activity first
    }

    // ------------------------------------------------------------------ time

    [Fact]
    public void ATimeIsTheCalendarTimeCovered_OverlapsOnce_GapsLeftOut_AndAnOpenOneRunsToNow()
    {
        var t = Start;
        DeliverableSpan S(long id, int from, int? to) => new(id, t.AddMinutes(from), to is { } x ? t.AddMinutes(x) : null);
        Assert.Equal(new GoalTime(null, null, 0), GoalTime.Of([], t));
        var closed = GoalTime.Of([S(1, 0, 30), S(2, 10, 20), S(3, 25, 40), S(4, 60, 70)], t.AddHours(5));
        Assert.Equal((t, t.AddMinutes(70), 50L * 60_000), (closed.FirstAt, closed.EndAt, closed.ElapsedMs)); // 0-40 and 60-70
        var open = GoalTime.Of([S(1, 0, 30), S(2, 20, null)], t.AddMinutes(45));
        Assert.Equal((t, (DateTimeOffset?)null, 45L * 60_000), (open.FirstAt, open.EndAt, open.ElapsedMs));
    }

    [Fact]
    public async Task AGoalsTime_InGoalsList_IsItsDeliverablesUnion_WhileOneIsOpen_AndAfter()
    {
        await using var rig = await Rig.CreateAsync();
        var goal = Id(await rig.Call("goals.save", new { name = "Ship" }));
        var a = rig.Tell("a");           // +1
        var b = rig.Tell("b");           // +2
        rig.Complete(a);                 // +3: a is 1..3
        foreach (var d in new[] { a, b }) await rig.Call("goals.assign", new { deliverable = d, goal });
        rig.Clock.Advance(TimeSpan.FromMinutes(7)); // now +10, b still open: 1..10

        var g = (await rig.Call("goals.list"))["goals"]![0]!;
        Assert.Equal((2L, 1L), (g["total"]!.GetValue<long>(), g["open"]!.GetValue<long>()));
        Assert.Equal(9L * 60_000, g["time"]!["elapsedMs"]!.GetValue<long>());
        Assert.Null(g["time"]!["endAt"]);
        Assert.Equal(Start.AddMinutes(1), DateTimeOffset.Parse(g["time"]!["firstAt"]!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture));

        rig.Complete(b);                 // +11: b is 2..11
        var c = rig.Tell("c");           // +12
        rig.Complete(c);                 // +13
        await rig.Call("goals.assign", new { deliverable = c, goal });
        rig.Clock.Advance(TimeSpan.FromHours(1)); // ended: now does not count
        g = (await rig.Call("goals.list"))["goals"]![0]!;
        Assert.Equal(0L, g["open"]!.GetValue<long>());
        Assert.Equal(11L * 60_000, g["time"]!["elapsedMs"]!.GetValue<long>()); // 1..11 and 12..13
        Assert.Equal(Start.AddMinutes(13), DateTimeOffset.Parse(g["time"]!["endAt"]!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture));
    }

    // ------------------------------------------------------------------ the RPC shapes

    [Fact]
    public async Task TheShapes_AndTheDeliverablesPagesGoal_AndItsGoalFilter()
    {
        await using var rig = await Rig.CreateAsync();
        var goal = Id(await rig.Call("goals.save", new { name = "Ship", description = "the release" }));
        var mine = rig.Tell("in the goal");
        var other = rig.Tell("unassigned");
        await rig.Call("goals.assign", new { deliverable = mine, goal });

        var list = await rig.Call("goals.list");
        Assert.Equal(["goals", "unassigned", "now"], list.Select(kv => kv.Key));
        var g = list["goals"]![0]!.AsObject();
        Assert.Equal(["id", "name", "description", "createdAt", "updatedAt", "deliverables", "open", "total", "time"], g.Select(kv => kv.Key));
        Assert.Equal(["firstAt", "endAt", "elapsedMs"], g["time"]!.AsObject().Select(kv => kv.Key));
        Assert.Equal(["deliverables", "open", "total", "time"], list["unassigned"]!.AsObject().Select(kv => kv.Key));

        var page = await rig.Call("deliverables.list");
        Assert.Equal(goal, Id(Row(page, mine)["goal"]));
        Assert.Equal("Ship", Row(page, mine)["goal"]!["name"]!.GetValue<string>());
        Assert.True(Row(page, other).ContainsKey("goal"));
        Assert.Null(Row(page, other)["goal"]);
        Assert.Equal(goal, Id((await rig.Call("deliverables.get", new { deliverable = mine }))["deliverable"]!["goal"]));

        Assert.Equal([mine], (await rig.Call("deliverables.list", new { goal })) ["deliverables"]!.AsArray().Select(Id));
        Assert.Equal([mine], (await rig.Call("deliverables.list", new { goal = goal.ToString(System.Globalization.CultureInfo.InvariantCulture) }))["deliverables"]!.AsArray().Select(Id));
        var unassigned = await rig.Call("deliverables.list", new { goal = "none" });
        Assert.Equal([other], unassigned["deliverables"]!.AsArray().Select(Id));
        Assert.Equal(1, unassigned["counts"]!["all"]!.GetValue<int>()); // the chips count what the filter keeps
        Assert.Empty((await rig.Call("deliverables.list", new { goal = 999 }))["deliverables"]!.AsArray()); // a goal deleted since: nothing, no error
        Assert.Equal("bad_params", await Refused(() => rig.Call("deliverables.list", new { goal = "soon" })));
    }

    [Fact]
    public async Task AGoalDeletedWhileFiltered_ListsNothing_AndTheDeleteCountsWhatGoalsListCounts()
    {
        await using var rig = await Rig.CreateAsync();
        var goal = Id(await rig.Call("goals.save", new { name = "Ship" }));
        var mine = rig.Tell("in the goal");
        await rig.Call("goals.assign", new { deliverable = mine, goal });
        rig.Store.SetDeliverableGoal(99_999, goal); // a deliverable no team has any more: goals.list does not count it
        Assert.Equal(1L, (await rig.Call("goals.list"))["goals"]![0]!["total"]!.GetValue<long>());

        var deleted = await rig.Call("goals.delete", new { id = goal });
        Assert.Equal(1L, deleted["unassigned"]!.GetValue<long>()); // the same number the plan showed
        Assert.Empty(rig.Store.DeliverableGoals()); // and the leftover row went with the goal
        var page = await rig.Call("deliverables.list", new { goal });
        Assert.Empty(page["deliverables"]!.AsArray());
        Assert.Equal(0, page["counts"]!["all"]!.GetValue<int>());
    }

    [Fact]
    public async Task SameNameIgnoresCaseForEveryLetter_AsTheShellDoes()
    {
        await using var rig = await Rig.CreateAsync();
        await rig.Call("goals.save", new { name = "Été" });
        Assert.Equal("bad_params", await Refused(() => rig.Call("goals.save", new { name = "été" })));
        Assert.Equal("bad_params", await Refused(() => rig.Call("goals.save", new { name = "ÉTÉ" })));
        Assert.True(Yawble.Core.Goals.GoalRules.SameName("Été", "été"));
        Assert.False(Yawble.Core.Goals.GoalRules.SameName("Ete", "Été"));
    }

    [Fact]
    public async Task DeliverableGoalSet_TakesAnExactNameFirst_ThenAnId_ThenNone()
    {
        await using var rig = await Rig.CreateAsync();
        var d = rig.Tell("work");
        var first = Id(await rig.Call("goals.save", new { name = "First" }));
        var named = Id(await rig.Call("goals.save", new { name = first.ToString(System.Globalization.CultureInfo.InvariantCulture) })); // a goal named like the other's id
        var none = Id(await rig.Call("goals.save", new { name = "None" }));
        Assert.NotEqual(first, named);

        async Task<long?> Set(string goal)
        {
            var (text, err) = await rig.Tools().CallToolAsync("deliverable_goal_set", new JsonObject { ["deliverable"] = d, ["goal"] = goal }, Ct);
            Assert.False(err, text);
            return Row(await rig.Call("deliverables.list"), d)["goal"] is JsonObject g ? Id(g) : null;
        }

        Assert.Equal(named, await Set(first.ToString(System.Globalization.CultureInfo.InvariantCulture))); // the name wins over the id
        Assert.Equal(none, await Set("none"));                // a goal named None is that goal
        Assert.Equal(first, await Set(" first "));            // trimmed, ignoring case
        await rig.Call("goals.delete", new { id = none });
        Assert.Null(await Set("none"));                       // no goal has that name: Unassigned
        Assert.Equal(named, await Set(named.ToString(System.Globalization.CultureInfo.InvariantCulture))); // no goal has that name: the id
        Assert.Null(await Set("unassigned"));
    }

    [Fact]
    public void AVisitor_ReadsGoals_ButCannotSaveDeleteOrAssign()
    {
        Assert.True(RemoteAccess.Reads("goals.list", null));
        Assert.NotEqual(RemoteAccess.ReadOnlyRefusal, RemoteAccess.Refusal("goals.list", null, allowHostTerminals: false, RemoteAccess.Read));
        foreach (var change in new[] { "goals.save", "goals.delete", "goals.assign" })
        {
            Assert.Equal(RemoteAccess.ReadOnlyRefusal, RemoteAccess.Refusal(change, null, allowHostTerminals: false, RemoteAccess.Read));
            Assert.Null(RemoteAccess.Refusal(change, null, allowHostTerminals: false, RemoteAccess.Full));
        }
    }

    // ------------------------------------------------------------------ tools

    [Fact]
    public async Task TheConciergesGoalTools_SaveListAndAssign_ByIdOrName_AndDeleteOnlyAfterTheYes()
    {
        await using var rig = await Rig.CreateAsync();
        var d = rig.Tell("build the site");
        var (saved, err) = await rig.Tools().CallToolAsync("goal_save", new JsonObject { ["name"] = "Website", ["description"] = "the public site" }, Ct);
        Assert.False(err, saved);
        Assert.StartsWith("ok: goal #", saved);
        var goal = Id((await rig.Call("goals.list"))["goals"]![0]);

        var (assigned, _) = await rig.Tools().CallToolAsync("deliverable_goal_set", new JsonObject { ["deliverable"] = d, ["goal"] = "website" }, Ct);
        Assert.Equal($"ok: deliverable #{d} has goal \"Website\" now.", assigned);
        var (listed, _) = await rig.Tools().CallToolAsync("goals_list", [], Ct);
        Assert.Contains($"goal #{goal} \"Website\" - the public site: 1 deliverable, 1 open", listed);
        Assert.Contains($"(#{d})", listed);
        Assert.Contains("Unassigned: 0 deliverables, 0 open", listed);
        var (missing, missingErr) = await rig.Tools().CallToolAsync("deliverable_goal_set", new JsonObject { ["deliverable"] = d, ["goal"] = "Nope" }, Ct);
        Assert.True(missingErr);
        Assert.Contains("no_such_goal", missing);
        var (unset, _) = await rig.Tools().CallToolAsync("deliverable_goal_set", new JsonObject { ["deliverable"] = d, ["goal"] = "none" }, Ct);
        Assert.Equal($"ok: deliverable #{d} is Unassigned now.", unset);
        await rig.Tools().CallToolAsync("deliverable_goal_set", new JsonObject { ["deliverable"] = d, ["goal"] = goal.ToString(System.Globalization.CultureInfo.InvariantCulture) }, Ct);

        // goal_delete: the first call lists what it does with a token and deletes nothing.
        var asking = rig.Tools();
        var (plan, _) = await asking.CallToolAsync("goal_delete", new JsonObject { ["id"] = goal }, Ct);
        Assert.StartsWith("NOT DONE.", plan);
        Assert.Contains($"Delete goal #{goal} \"Website\". Its 1 deliverable stay and become Unassigned.", plan);
        Assert.Single((await rig.Call("goals.list"))["goals"]!.AsArray());
        var token = System.Text.RegularExpressions.Regex.Match(plan, "confirm=\"([^\"]+)\"").Groups[1].Value;
        var (sameTurn, sameTurnErr) = await asking.CallToolAsync("goal_delete", new JsonObject { ["id"] = goal, ["confirm"] = token }, Ct);
        Assert.True(sameTurnErr, sameTurn);
        Assert.Single((await rig.Call("goals.list"))["goals"]!.AsArray());

        var (done, doneErr) = await rig.Tools().CallToolAsync("goal_delete", new JsonObject { ["id"] = goal, ["confirm"] = token }, Ct);
        Assert.False(doneErr, done);
        Assert.Equal($"ok: goal #{goal} deleted; 1 deliverable is Unassigned now.", done);
        Assert.Empty((await rig.Call("goals.list"))["goals"]!.AsArray());

        // A chat that cannot take a yes deletes nothing.
        var other = Id(await rig.Call("goals.save", new { name = "Other" }));
        var (noActions, noActionsErr) = await rig.Tools(withActions: false).CallToolAsync("goal_delete", new JsonObject { ["id"] = other }, Ct);
        Assert.True(noActionsErr);
        Assert.Contains("needs the person's yes", noActions);
    }

    [Fact]
    public void TheGoalTools_AreOfferedAndLevelled()
    {
        foreach (var name in GoalTools.Names)
        {
            Assert.Contains(name, ConciergeTools.ToolNames);
            Assert.Contains(ConciergeTools.Definitions(true).OfType<JsonObject>(), d => d["name"]!.GetValue<string>() == name);
        }
        Assert.All(GoalTools.Acting, n => Assert.Contains(n, ConciergeTools.Acting));
        Assert.DoesNotContain("goals_list", ConciergeTools.Acting);
        Assert.Equal(CompanionLevel.Read, CompanionTools.LevelOf("goals_list", []));
        Assert.Equal(CompanionLevel.Act, CompanionTools.LevelOf("goal_save", []));
        Assert.Equal(CompanionLevel.Act, CompanionTools.LevelOf("deliverable_goal_set", []));
        Assert.Equal(CompanionLevel.Destructive, CompanionTools.LevelOf("goal_delete", []));
        Assert.Contains("goal_delete", CompanionTools.SelfConfirming);
        // An external client is never offered the confirm parameter: the window approves.
        var delete = CompanionTools.Definitions(CompanionLevel.Destructive).OfType<JsonObject>().Single(d => d["name"]!.GetValue<string>() == "goal_delete");
        Assert.False(delete["inputSchema"]!["properties"]!.AsObject().ContainsKey("confirm"));
        Assert.DoesNotContain(CompanionTools.Definitions(CompanionLevel.Act).OfType<JsonObject>(), d => d["name"]!.GetValue<string>() == "goal_delete");
    }
}
