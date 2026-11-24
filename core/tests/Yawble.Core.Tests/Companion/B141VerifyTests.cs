using System.Text.Json.Nodes;
using Yawble.Core.Bus;
using Yawble.Core.Companion;
using Yawble.Core.Concierge;
using Yawble.Core.Ipc;
using Yawble.Core.Provenance;
using Yawble.Core.Storage;
using Yawble.Core.Teams;
using Yawble.Core.Tests.Bus;
using Yawble.Core.Ui;
using Xunit;

namespace Yawble.Core.Tests.Companion;

/// <summary>
/// Card 619 (B141 verify), the tester's own checks: a real CompanionService on a manual clock, with no git (the git-backed
/// CompanionServiceTests cannot clone a local bare repository in a sandbox). The lists are the Concierge's team_delete and
/// team_archive, whose keys move without git: deleting team alpha and making it again gives it a new id.
/// </summary>
public class B141VerifyTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly string _root = Directory.CreateTempSubdirectory("yawble-b141-").FullName;
    private readonly TempDb _db = new();

    public void Dispose()
    {
        _db.Dispose();
        try { TestFiles.DeleteTree(_root); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Rig
    {
        public required BusEngine Bus;
        public required CompanionClientStore Clients;
        public required AuditLog Audit;
        public required ManualClock Clock;
        public required ConciergeGit Git;
        public CompanionService Service = null!;
        public List<(string Method, JsonObject? P)> Ui { get; } = [];
        /// <summary>Every turn the service made Concierge actions for, in order (plans and runs).</summary>
        public List<ConciergeTurn> Turns { get; } = [];
        /// <summary>Runs each time the service makes its Concierge actions, with the turn and the companion's book.</summary>
        public Action<ConciergeTurn, ConfirmationBook>? OnActions;

        public IEnumerable<string> Methods => Ui.Select(u => u.Method);

        public Task<JsonNode?> RouteAsync(string method, JsonObject? p, CancellationToken ct)
        {
            lock (Ui) Ui.Add((method, p?.DeepClone().AsObject()));
            return Task.FromResult<JsonNode?>(new JsonObject());
        }

        public ConciergeActions Actions(ConciergeTurn turn, ConfirmationBook book)
        {
            lock (Turns) Turns.Add(turn);
            OnActions?.Invoke(turn, book);
            return new ConciergeActions(Bus, RouteAsync,
                (_, _, _) => Task.FromResult<JsonNode?>(new JsonObject { ["label"] = "B006" }),
                Git, (_, _) => Task.FromResult<IReadOnlyList<ActionRepo>>([]), book,
                (_, _, _) => Task.FromResult(new GitResult(0, "[]", "")), Audit, turn);
        }

        public CompanionClient Client(string name) => Clients.Add(name, CompanionLevel.Destructive, "tester", out _);

        public async Task<ActionStatus> Approve(string id, bool approve = true)
        {
            ActionStatus s;
            using (Actor.Use(Actor.Window)) s = await Service.DecideActionAsync(id, approve, Ct);
            return s.State == ActionState.Running && Service.RunOf(id) is { } run ? await run.WaitAsync(TimeSpan.FromSeconds(30), Ct) : s;
        }

        public JsonObject List()
        {
            using (Actor.Use(Actor.Window)) return Service.ListForWindow();
        }
    }

    private async Task<Rig> MakeRigAsync()
    {
        var bus = new BusEngine(_db.OpenStore(), new UnavailableRunner(), new BusOptions { IdleOffer = false });
        await bus.StartAsync(Ct);
        await bus.CreateTeamAsync("alpha", "Manager", "claude", null, Ct);
        var clock = new ManualClock(new DateTimeOffset(2026, 11, 24, 9, 0, 0, TimeSpan.Zero));
        File.WriteAllText(Path.Combine(_root, "empty.gitconfig"), "");
        var rig = new Rig
        {
            Bus = bus,
            Clients = new CompanionClientStore(_db.ConnectionString, clock),
            Audit = new AuditLog(_db.ConnectionString, clock),
            Clock = clock,
            Git = new ConciergeGit(new HostGit(Path.Combine(_root, "empty.gitconfig")), new PersonGitPush(globalConfig: Path.Combine(_root, "empty.gitconfig"))),
        };
        rig.Service = new CompanionService(rig.Bus, new ConciergeStore(_db.ConnectionString), rig.RouteAsync, rig.Actions, rig.Clients, rig.Audit, new AppEvents(), clock);
        return rig;
    }

    private static JsonObject Alpha => new() { ["team"] = "alpha" };

    private async Task<long> AlphaIdAsync(Rig rig) => (await rig.Bus.TeamsAsync(Ct)).Single(t => t.Name == "alpha").Id;

    [Fact]
    public void TheCapIsSevenDays_AndPairingsStillEndAtFiveMinutes()
    {
        Assert.Equal(TimeSpan.FromDays(7), CompanionService.ActionCap);
        Assert.Equal(TimeSpan.FromMinutes(5), CompanionService.PairingLife);
        Assert.Equal("expired: not decided within 7 days", CompanionService.ExpiredResult);
        // The cap is not tied to the confirmation token's life (10 minutes).
        Assert.True(CompanionService.ActionCap > ConfirmationBook.Life);
    }

    [Theory]
    [InlineData(10)]          // +10 minutes: past a token's 10-minute life
    [InlineData(2 * 24 * 60)] // +2 days
    [InlineData(7 * 24 * 60 - 1)] // a minute before the cap
    public async Task ALateApprovalRunsWhenNothingChanged(int minutes)
    {
        var rig = await MakeRigAsync();
        var cc = rig.Client("cc");
        var del = await rig.Service.CallAsync(cc, "team_delete", Alpha, Ct);
        var arch = await rig.Service.CallAsync(cc, "team_archive", Alpha, Ct);
        Assert.NotNull(del.RequestId);
        Assert.NotNull(arch.RequestId);
        rig.Clock.Now += TimeSpan.FromMinutes(minutes);

        Assert.Equal(2, rig.Service.PendingCount);
        var deleted = await rig.Approve(del.RequestId!);
        Assert.Equal(ActionState.Done, deleted.State);
        Assert.Equal("ok: deleted alpha.", deleted.Result);
        var call = Assert.Single(rig.Ui, u => u.Method == "teams.delete");
        Assert.Equal(await AlphaIdAsync(rig), call.P!["teamId"]!.GetValue<long>());
        Assert.Equal("alpha", call.P["confirm"]!.GetValue<string>());

        var archived = await rig.Approve(arch.RequestId!);
        Assert.Equal(ActionState.Done, archived.State);
        Assert.Single(rig.Ui, u => u.Method == "teams.archive");
        Assert.Equal(0, rig.Service.PendingCount);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(2 * 24 * 60)]
    public async Task ALateApprovalAfterTheStateMovedRunsNothing_ItBecomesANewRequest(int minutes)
    {
        var rig = await MakeRigAsync();
        var cc = rig.Client("cc");
        var oldId = await AlphaIdAsync(rig);
        var r = await rig.Service.CallAsync(cc, "team_delete", Alpha, Ct);
        var oldKey = rig.Service.ListKeyOf(r.RequestId!);
        Assert.Contains(oldId.ToString(), oldKey);
        rig.Clock.Now += TimeSpan.FromMinutes(minutes);

        // The state moves while the request waits: alpha is another team now (a new id under the same name).
        await rig.Bus.DeleteTeamAsync("alpha", Ct);
        await rig.Bus.CreateTeamAsync("alpha", "Manager", "claude", null, Ct);
        var newId = await AlphaIdAsync(rig);
        Assert.NotEqual(oldId, newId);

        var changed = await rig.Approve(r.RequestId!);
        Assert.Equal(ActionState.Changed, changed.State);
        Assert.NotNull(changed.NextId);
        Assert.Equal($"the list changed before approval; request {changed.NextId} shows the new list", changed.Result);
        Assert.DoesNotContain(rig.Methods, m => m == "teams.delete"); // nothing ran
        var next = rig.Service.Status(cc, changed.NextId!)!;
        Assert.Equal(ActionState.Pending, next.State);
        Assert.NotEqual(oldKey, rig.Service.ListKeyOf(changed.NextId!));
        Assert.Contains(rig.Audit.Recent(100), x => x.Method == "companion.team_delete" && x.Outcome.Contains($"the list changed; new request {changed.NextId}"));

        // The new request, approved later with nothing moved, runs once, on the new team.
        rig.Clock.Now += TimeSpan.FromMinutes(minutes);
        Assert.Equal(ActionState.Done, (await rig.Approve(changed.NextId!)).State);
        var call = Assert.Single(rig.Ui, u => u.Method == "teams.delete");
        Assert.Equal(newId, call.P!["teamId"]!.GetValue<long>());
    }

    [Fact]
    public async Task TheSevenDayCapEndsTheRequest_AndItCannotBeApprovedAfter()
    {
        var rig = await MakeRigAsync();
        var cc = rig.Client("cc");
        var r = await rig.Service.CallAsync(cc, "team_delete", Alpha, Ct);
        rig.Clock.Now += CompanionService.ActionCap - TimeSpan.FromSeconds(1);
        Assert.Equal(ActionState.Pending, rig.Service.Status(cc, r.RequestId!)!.State);
        Assert.Single(rig.List()["actions"]!.AsArray());

        rig.Clock.Now += TimeSpan.FromSeconds(1);
        var s = rig.Service.Status(cc, r.RequestId!)!;
        Assert.Equal(ActionState.Expired, s.State);
        Assert.Equal("expired: not decided within 7 days", s.Result);
        Assert.Equal(0, rig.Service.PendingCount);
        Assert.Empty(rig.List()["actions"]!.AsArray());
        var status = await rig.Service.CallAsync(cc, "request_status", new JsonObject { ["id"] = r.RequestId }, Ct);
        Assert.Contains("expired: not decided within 7 days", status.Text);

        Assert.Equal(ActionState.Expired, (await rig.Approve(r.RequestId!)).State);
        Assert.DoesNotContain(rig.Methods, m => m == "teams.delete");
    }

    [Fact]
    public async Task TheTokenIsIssuedAtApproval_AndAReplayedTokenRunsNothing()
    {
        var rig = await MakeRigAsync();
        var cc = rig.Client("cc");
        var r = await rig.Service.CallAsync(cc, "team_delete", Alpha, Ct);
        var key = rig.Service.ListKeyOf(r.RequestId!)!;
        // The plan's token was withdrawn when the request was stored: no turn so far holds one.
        Assert.All(rig.Turns, t => Assert.Empty(rig.Service.Book.IssuedIn(t)));
        var planTurns = rig.Turns.Count;

        rig.Clock.Now += TimeSpan.FromDays(2);
        // When the run's turn is made, the token issued at this approval is pending in the approval's plan turn: catch it.
        string? issuedAtApproval = null;
        ConciergeTurn? approvalPlan = null;
        rig.OnActions = (turn, book) =>
        {
            if (approvalPlan is not null || rig.Turns.Count < planTurns + 2) return;
            approvalPlan = rig.Turns[^2];
            issuedAtApproval = book.IssuedIn(approvalPlan).SingleOrDefault()?.Token;
        };
        Assert.Equal(ActionState.Done, (await rig.Approve(r.RequestId!)).State);
        Assert.NotNull(issuedAtApproval);
        Assert.Single(rig.Ui, u => u.Method == "teams.delete");
        // Spent: the book has no token left, and the one issued at approval is unknown now.
        Assert.All(rig.Turns, t => Assert.Empty(rig.Service.Book.IssuedIn(t)));
        Assert.Null(rig.Service.Book.KeyOf(issuedAtApproval!));
        var later = new ConciergeTurn(approvalPlan!.ConversationId, byPerson: true, remote: false);
        Assert.Equal(ConfirmationBook.Outcome.Unknown, rig.Service.Book.Redeem(later, "team_delete", key, issuedAtApproval!));

        // The client replays it: a new request, never a run.
        var replay = await rig.Service.CallAsync(cc, "team_delete", new JsonObject { ["team"] = "alpha", ["confirm"] = issuedAtApproval }, Ct);
        Assert.NotNull(replay.RequestId);
        Assert.NotEqual(r.RequestId, replay.RequestId);
        Assert.DoesNotContain(issuedAtApproval!, replay.Text);
        Assert.Single(rig.Ui, u => u.Method == "teams.delete");
        // Approving the old request again runs nothing more.
        Assert.Equal(ActionState.Done, (await rig.Approve(r.RequestId!)).State);
        Assert.Single(rig.Ui, u => u.Method == "teams.delete");
    }

    [Fact]
    public async Task ASecondIdenticalRequestReplacesTheFirst_AndOnlyTheNewerRuns()
    {
        var rig = await MakeRigAsync();
        var cc = rig.Client("cc");
        var first = await rig.Service.CallAsync(cc, "team_delete", Alpha, Ct);
        rig.Clock.Now += TimeSpan.FromDays(3);
        var second = await rig.Service.CallAsync(cc, "team_delete", Alpha, Ct);

        var old = rig.Service.Status(cc, first.RequestId!)!;
        Assert.Equal(ActionState.Changed, old.State);
        Assert.Equal($"replaced by {second.RequestId}", old.Result);
        Assert.Equal(second.RequestId, old.NextId);
        Assert.Equal(1, rig.Service.PendingCount);
        var listed = Assert.Single(rig.List()["actions"]!.AsArray());
        Assert.Equal(second.RequestId, listed!["id"]!.GetValue<string>());

        Assert.Equal(ActionState.Changed, (await rig.Approve(first.RequestId!)).State);
        Assert.DoesNotContain(rig.Methods, m => m == "teams.delete");
        // The newer one has its own 7 days from when it was asked: at 3 days + 6 days it still runs.
        rig.Clock.Now += TimeSpan.FromDays(6);
        Assert.Equal(ActionState.Done, (await rig.Approve(second.RequestId!)).State);
        Assert.Single(rig.Ui, u => u.Method == "teams.delete");
    }

    [Fact]
    public async Task ReplacingAtTheCapIsTaken_ANewTargetIsRefused()
    {
        var rig = await MakeRigAsync();
        var cc = rig.Client("cc");
        Assert.NotNull((await rig.Service.CallAsync(cc, "team_delete", Alpha, Ct)).RequestId);
        for (var i = 1; i < CompanionService.MaxPendingPerClient; i++)
            Assert.NotNull((await rig.Service.CallAsync(cc, "team_create", new JsonObject { ["name"] = $"t{i}" }, Ct)).RequestId);
        Assert.Equal(CompanionService.MaxPendingPerClient, rig.Service.PendingCount);

        var refused = await rig.Service.CallAsync(cc, "team_create", new JsonObject { ["name"] = "one-more" }, Ct);
        Assert.True(refused.IsError);
        Assert.Contains("request_cancel", refused.Text);
        var replacing = await rig.Service.CallAsync(cc, "team_delete", Alpha, Ct);
        Assert.False(replacing.IsError, replacing.Text);
        Assert.Equal(CompanionService.MaxPendingPerClient, rig.Service.PendingCount);
    }

    [Fact]
    public async Task CancelEndsTheRequestDenied_AndAnUnknownIdSaysTheAppRestarted()
    {
        var rig = await MakeRigAsync();
        var cc = rig.Client("cc");
        var r = await rig.Service.CallAsync(cc, "team_delete", Alpha, Ct);
        rig.Clock.Now += TimeSpan.FromDays(1);
        var cancelled = await rig.Service.CallAsync(cc, "request_cancel", new JsonObject { ["id"] = r.RequestId }, Ct);
        Assert.False(cancelled.IsError, cancelled.Text);
        var s = rig.Service.Status(cc, r.RequestId!)!;
        Assert.Equal(ActionState.Denied, s.State);
        Assert.Equal("cancelled by the client", s.Result);
        Assert.Equal(ActionState.Denied, (await rig.Approve(r.RequestId!)).State);
        Assert.DoesNotContain(rig.Methods, m => m == "teams.delete");

        foreach (var tool in new[] { "request_cancel", "request_status" })
        {
            var gone = await rig.Service.CallAsync(cc, tool, new JsonObject { ["id"] = "r000000000000" }, Ct);
            Assert.True(gone.IsError);
            Assert.Equal(CompanionService.NoSuchRequest("r000000000000"), gone.Text);
            Assert.Contains("ended: the app restarted", gone.Text);
        }
    }

    [Fact]
    public async Task CompanionListCarriesAskedAndExpires_OverTheRpc()
    {
        var rig = await MakeRigAsync();
        var cc = rig.Client("cc");
        var asked = rig.Clock.Now;
        var r = await rig.Service.CallAsync(cc, "team_delete", Alpha, Ct);
        rig.Clock.Now += TimeSpan.FromDays(2);

        JsonObject listed;
        using (Actor.Use(Actor.Window)) listed = (JsonObject)(await new CompanionRpc(rig.Service, () => []).DispatchAsync("companion.list", null, Ct))!;
        var a = Assert.Single(listed["actions"]!.AsArray())!;
        Assert.Equal(r.RequestId, a["id"]!.GetValue<string>());
        Assert.Equal("pending", a["state"]!.GetValue<string>());
        var askedText = a["asked"]!.GetValue<string>();
        Assert.EndsWith("Z", askedText.Replace("+00:00", "Z"));
        Assert.Equal(asked, DateTimeOffset.Parse(askedText, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(asked + TimeSpan.FromDays(7), DateTimeOffset.Parse(a["expires"]!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture));
        Assert.DoesNotMatch("\\b[0-9a-f]{16}\\b", listed.ToJsonString());
    }

    [Fact]
    public async Task APairingStillEndsAtFiveMinutes()
    {
        var rig = await MakeRigAsync();
        var ticket = rig.Service.BeginPairing("phone", CompanionLevel.Read);
        Assert.Equal(rig.Clock.Now + TimeSpan.FromMinutes(5), ticket.Expires);
        rig.Clock.Now += TimeSpan.FromMinutes(5) - TimeSpan.FromSeconds(1);
        Assert.Single(rig.List()["pairings"]!.AsArray());
        rig.Clock.Now += TimeSpan.FromSeconds(1);
        Assert.Empty(rig.List()["pairings"]!.AsArray());
        var outcome = await rig.Service.WaitPairingAsync(ticket.Id, Ct).WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.False(outcome.Approved);
        Assert.Equal("expired: the pairing was not approved within five minutes", outcome.Reason);
    }
}
