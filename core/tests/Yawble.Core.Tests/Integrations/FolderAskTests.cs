using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Yawble.Core.Bus;
using Yawble.Core.Gateway;
using Yawble.Core.Integrations;
using Yawble.Core.Ipc;
using Yawble.Core.Policy;
using Yawble.Core.Provenance;
using Yawble.Core.Tests.Bus;
using Yawble.Core.Tests.Gateway;
using Yawble.Core.Tests.Policy;
using Xunit;
using MemberRef = Yawble.Core.Bus.MemberRef;

namespace Yawble.Core.Tests.Integrations;

/// <summary>
/// Folder asks: request_folder raises an ask the person answers in Needs you; an approval writes the
/// member's loosen block through the policy store and its effective policy has the folder; read narrows; deny changes
/// nothing and tells the asker; a repeat reuses the open ask; a Manager may ask for its own team's member only; each
/// refusal is said at once with no ask. An approval covers the member, the whole team (the team's folder list) or all
/// teams (the global policy); the Concierge raises the same ask and wakes no agent; only the person adds to or removes
/// from a team's folder list. Fakes only: a temporary database, a fixed fictional Windows machine
/// (PolicyFixtures: the person "alice", teams alpha and beta), a recording tell, a fake "exists".
/// </summary>
public sealed class FolderAskTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly TempDb _db = new();
    private readonly FakeClock _clock = new(new DateTimeOffset(2026, 11, 20, 9, 0, 0, TimeSpan.Zero));
    private readonly PolicyStore _policies;
    private readonly List<(string Team, string Member, string Text, long? Causation)> _told = [];
    private readonly HashSet<string> _existing = new(StringComparer.OrdinalIgnoreCase) { @"Z:\smokesessions" };
    private readonly Dictionary<long, string> _names = [];
    private readonly long _alpha, _beta, _manager, _dev1, _dev2, _betaManager;
    private readonly FolderAskService _asks;

    /// <summary>Another team's repository, the person's checkout outside the app's folders.</summary>
    private const string BetaRepo = @"D:\repos\beta-app";

    public FolderAskTests()
    {
        _policies = new PolicyStore(_db.ConnectionString);
        _alpha = Insert("INSERT INTO teams (name, created_at) VALUES ('alpha', '2026-11-20T00:00:00Z') RETURNING id;");
        _beta = Insert("INSERT INTO teams (name, created_at) VALUES ('beta', '2026-11-20T00:00:00Z') RETURNING id;");
        _manager = Member(_alpha, "manager", "manager");
        _dev1 = Member(_alpha, "dev1", "member");
        _dev2 = Member(_alpha, "dev2", "member");
        _betaManager = Member(_beta, "manager", "manager");
        _asks = new FolderAskService(_db.ConnectionString, _policies,
            (id, _) => Task.FromResult(PolicyFixtures.Layout(TargetOs.Windows, AgentCli.Claude,
                new Yawble.Core.Policy.MemberRef(id == _betaManager ? "beta" : "alpha", _names[id]))),
            (teamId, _) => Task.FromResult<IReadOnlyList<string>>(teamId == _alpha ? [BetaRepo] : []),
            (team, member, text, causation, _) =>
            {
                lock (_told) _told.Add((team, member, text, causation));
                return Task.CompletedTask;
            },
            exists: _existing.Contains, clock: _clock);
    }

    public void Dispose() => _db.Dispose();

    private long Insert(string sql)
    {
        using var c = new SqliteConnection(_db.ConnectionString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return (long)cmd.ExecuteScalar()!;
    }

    private long Member(long team, string name, string role)
    {
        var id = Insert($"INSERT INTO members (team_id, name, role, created_at) VALUES ({team}, '{name}', '{role}', '2026-11-20T00:00:00Z') RETURNING id;");
        _names[id] = name;
        return id;
    }

    private MemberRef Dev1 => new(_dev1, _alpha, "alpha", "dev1", MemberRole.Member);

    private MemberRef Manager => new(_manager, _alpha, "alpha", "manager", MemberRole.Manager);

    private Task<string> Ask(MemberRef who, string path, string access = "readwrite", string? member = null) =>
        _asks.RequestFromAgentAsync(who, path, access, "read the recorded smoke sessions the tests replay", member, deliverable: 70, causation: 71, Ct);

    private RunLayout Layout(long memberId) => PolicyFixtures.Layout(TargetOs.Windows, AgentCli.Claude, new Yawble.Core.Policy.MemberRef("alpha", _names[memberId]));

    private EffectivePolicy Effective(long memberId) => _policies.Effective(memberId, Layout(memberId)).Policy;

    // ------------------------------------------------------------------ ask, approve, deny

    [Fact]
    public async Task AskThenApprove_TheEffectivePolicyHasThePath_ReadWrite()
    {
        var text = await Ask(Dev1, @"Z:\smokesessions");
        Assert.StartsWith("asked (ask ", text);
        Assert.Contains("the person decides; it applies from your next run", text);
        var ask = Assert.Single(_asks.Asks());
        Assert.Equal(("alpha/dev1", "alpha/dev1", @"Z:\smokesessions", "readwrite"), (ask.Member, ask.AskedBy, ask.Path, ask.Access));
        Assert.True(ask.PathExisted);
        Assert.Equal((70L, 71L), (ask.Deliverable, ask.CausationSeq));
        Assert.Equal(1, _asks.PendingCount());

        var answer = await _asks.AnswerAsync(ask.Id, FolderAccess.ReadWrite, null, Ct);
        Assert.Equal("readwrite", answer["grantedAccess"]!.GetValue<string>());

        var e = Effective(_dev1);
        Assert.Contains(@"Z:\smokesessions", e.Readwrite);
        Assert.DoesNotContain(@"Z:\smokesessions", e.Readonly);
        // Written as policy.saveLoosen writes it: the confirmed summary and when it was approved.
        var stored = _policies.GetMember(_dev1);
        Assert.Contains(@"filesystem.readwrite + Z:\smokesessions", stored.LoosenSummary);
        Assert.NotNull(stored.LoosenApprovedAt);
        // The Manager is told to retry, in the ask's deliverable.
        var told = Assert.Single(_told);
        Assert.Equal(("alpha", "manager", 71L), (told.Team, told.Member, told.Causation));
        Assert.Contains(@"dev1 can now read and write Z:\smokesessions", told.Text);
        Assert.Equal(0, _asks.PendingCount());
        Assert.Equal("approved", Assert.Single(_asks.Asks(openOnly: false)).State);
    }

    [Fact]
    public async Task ApproveAsRead_NarrowsAReadWriteAsk()
    {
        await Ask(Dev1, @"Z:\smokesessions");
        var id = _asks.Asks().Single().Id;
        await _asks.AnswerAsync(id, FolderAccess.Read, null, Ct);

        var e = Effective(_dev1);
        Assert.Contains(@"Z:\smokesessions", e.Readonly);
        Assert.DoesNotContain(@"Z:\smokesessions", e.Readwrite);
        Assert.Equal("read", _asks.Asks(openOnly: false).Single().GrantedAccess);
        Assert.Contains("the person granted read-only", Assert.Single(_told).Text);
    }

    [Fact]
    public async Task AReadAsk_IsNeverGrantedReadWrite()
    {
        await Ask(Dev1, @"Z:\smokesessions", "read");
        var ask = _asks.Asks().Single();
        Assert.Equal(["read", "deny"], _asks.ToJson(ask)["choices"]!.AsArray().Select(n => n!.GetValue<string>()));
        await Assert.ThrowsAsync<ArgumentException>(() => _asks.AnswerAsync(ask.Id, FolderAccess.ReadWrite, null, Ct));
        Assert.DoesNotContain(@"Z:\smokesessions", Effective(_dev1).Readwrite);
        Assert.Single(_asks.Asks());
    }

    [Fact]
    public async Task Deny_LeavesThePolicyUnchanged_AndTellsTheAsker()
    {
        var before = _policies.GetMember(_dev1);
        await Ask(Dev1, @"Z:\smokesessions");
        var id = _asks.Asks().Single().Id;
        await _asks.AnswerAsync(id, FolderAccess.Deny, "not that one", Ct);

        var after = _policies.GetMember(_dev1);
        Assert.Equal(before.Loosen, after.Loosen);
        Assert.Null(after.LoosenApprovedAt);
        Assert.DoesNotContain(@"Z:\smokesessions", Effective(_dev1).Readwrite.Concat(Effective(_dev1).Readonly));
        var told = Assert.Single(_told);
        Assert.Equal(("alpha", "dev1", 71L), (told.Team, told.Member, told.Causation));
        Assert.Contains(@"The person denied read-write access to Z:\smokesessions for you (folder request", told.Text);
        Assert.Contains("not that one", told.Text);
        Assert.Equal("denied", _asks.Asks(openOnly: false).Single().State);
    }

    [Fact]
    public async Task TheSameAskWhileItWaits_ReturnsTheOpenAsk()
    {
        var first = await Ask(Dev1, @"Z:\smokesessions");
        var again = await Ask(Dev1, @"z:\SmokeSessions\");
        var ask = Assert.Single(_asks.Asks());
        Assert.Contains($"ask {ask.Id}", first);
        Assert.Contains($"ask {ask.Id}", again);
        Assert.Contains("already waiting", again);
        // A different access is another ask.
        await Ask(Dev1, @"Z:\smokesessions", "read");
        Assert.Equal(2, _asks.Asks().Count);
    }

    [Fact]
    public async Task OnlyThePersonAnswers_AndACloudViewerMay()
    {
        await Ask(Dev1, @"Z:\smokesessions");
        var id = _asks.Asks().Single().Id;
        foreach (var via in new[] { Actor.Concierge, Actor.Agent, Actor.External, Actor.Trigger })
            using (Actor.Use(new Actor(via, "someone")))
                await Assert.ThrowsAsync<ArgumentException>(() => _asks.AnswerAsync(id, FolderAccess.ReadWrite, null, Ct));
        Assert.Single(_asks.Asks());
        using (Actor.Use(Actor.CloudViewer("person@example.com")))
            await _asks.AnswerAsync(id, FolderAccess.Read, null, Ct);
        var closed = _asks.Asks(openOnly: false).Single();
        Assert.Equal(("person@example.com", Actor.Cloud), (closed.DecidedBy, closed.DecidedVia));
    }

    // ------------------------------------------------------------------ whom an approval covers

    [Fact]
    public async Task AWaitingAsk_OffersTheThreeScopes_AndAnAnsweredOneNone()
    {
        await Ask(Dev1, @"Z:\smokesessions");
        var ask = _asks.Asks().Single();
        Assert.Equal(["member", "team", "everywhere"], _asks.ToJson(ask)["scopes"]!.AsArray().Select(n => n!.GetValue<string>()));
        await Assert.ThrowsAsync<ArgumentException>(() => _asks.AnswerAsync(ask.Id, FolderAccess.Read, null, Ct, "the-world"));
        Assert.Single(_asks.Asks());

        // No scope is the ask's member only: nobody else on the team has the folder.
        var answer = await _asks.AnswerAsync(ask.Id, FolderAccess.ReadWrite, null, Ct);
        Assert.Equal("member", answer["scope"]!.GetValue<string>());
        Assert.DoesNotContain(@"Z:\smokesessions", Effective(_dev2).Readwrite);
        Assert.Empty(_policies.TeamFolders(_alpha));
        Assert.Empty(_asks.ToJson(_asks.Asks(openOnly: false).Single())["scopes"]!.AsArray());
    }

    [Fact]
    public async Task ApprovedForTheTeam_GoesOnTheTeamsFolderList_ForEveryMemberAndLaterHires()
    {
        await Ask(Dev1, @"Z:\smokesessions");
        var id = _asks.Asks().Single().Id;
        JsonObject answer;
        using (Actor.Use(new Actor(Actor.App, "alice")))
            answer = await _asks.AnswerAsync(id, FolderAccess.ReadWrite, null, Ct, FolderScope.Team);
        Assert.Equal(("team", "readwrite"), (answer["scope"]!.GetValue<string>(), answer["grantedAccess"]!.GetValue<string>()));

        var row = Assert.Single(_policies.TeamFolders(_alpha));
        Assert.Equal((@"Z:\smokesessions", "readwrite", "alice", Actor.App), (row.Path, row.Access, row.AddedBy, row.AddedVia));
        // The team's list, not each member's own loosen block.
        Assert.True(_policies.GetMember(_dev1).Loosen.IsEmpty);
        foreach (var member in new[] { _manager, _dev1, _dev2 }) Assert.Contains(@"Z:\smokesessions", Effective(member).Readwrite);
        var hired = Member(_alpha, "dev3", "member");
        Assert.Contains(@"Z:\smokesessions", Effective(hired).Readwrite);
        Assert.Empty(_policies.TeamFolders(_beta));

        Assert.Contains(@"Every member of the team can now read and write Z:\smokesessions", Assert.Single(_told).Text);
        Assert.Contains(@"read-write Z:\smokesessions for every member of team alpha", _asks.Events(id).Last().Detail);
        Assert.Equal("approved", _asks.Asks(openOnly: false).Single().State);
    }

    [Fact]
    public async Task ApprovedForTheTeam_IsNeverWiderThanAsked_AndNeverNarrowsWhatTheTeamHas()
    {
        await Ask(Dev1, @"Z:\smokesessions", "read");
        var read = _asks.Asks().Single().Id;
        await Assert.ThrowsAsync<ArgumentException>(() => _asks.AnswerAsync(read, FolderAccess.ReadWrite, null, Ct, FolderScope.Team));
        Assert.Empty(_policies.TeamFolders(_alpha));
        await _asks.AnswerAsync(read, FolderAccess.Read, null, Ct, FolderScope.Team);
        Assert.Equal("read", Assert.Single(_policies.TeamFolders(_alpha)).Access);
        Assert.Contains(@"Z:\smokesessions", Effective(_dev2).Readonly);

        // A read ask that waited while the person gave the team the folder read-write: approving it for the team
        // leaves the team's folder read-write.
        await Ask(Dev1, @"Z:\output", "read");
        await _asks.AddTeamFolderAsync(_alpha, @"Z:\output", "readwrite", Ct);
        await _asks.AnswerAsync(_asks.Asks().Single().Id, FolderAccess.Read, null, Ct, FolderScope.Team);
        Assert.Equal("readwrite", _policies.TeamFolders(_alpha).Single(f => f.Path == @"Z:\output").Access);
        // A folder inside one the team has is not asked for at all.
        Assert.Contains("already read-write", await _asks.RequestFromConciergeAsync("alpha", "dev2", @"Z:\output\reports", "read", "read the reports", Ct));
        Assert.Empty(_asks.Asks());
    }

    [Fact]
    public async Task ApprovedForTheTeam_WhenOneMemberCannotHaveIt_ChangesNothing_AndTheAskStaysWaiting()
    {
        _policies.SaveOverride(_dev2, new PolicyPatch(Filesystem: new FilesystemPatch(Denied: [@"Z:\smokesessions"])), Layout(_dev2));
        await Ask(Dev1, @"Z:\smokesessions");
        var id = _asks.Asks().Single().Id;
        var e = await Assert.ThrowsAsync<PolicyException>(() => _asks.AnswerAsync(id, FolderAccess.ReadWrite, null, Ct, FolderScope.Team));
        Assert.Contains(e.Errors, x => x.Contains("dev2"));
        Assert.Empty(_policies.TeamFolders(_alpha));
        Assert.DoesNotContain(@"Z:\smokesessions", Effective(_dev1).Readwrite);
        Assert.Single(_asks.Asks());
        Assert.Empty(_told);
        // The person can still approve it for the one member.
        await _asks.AnswerAsync(id, FolderAccess.ReadWrite, null, Ct, FolderScope.Member);
        Assert.Contains(@"Z:\smokesessions", Effective(_dev1).Readwrite);
    }

    [Fact]
    public async Task ApprovedForAllTeams_GoesInTheGlobalPolicy()
    {
        await Ask(Dev1, @"Z:\smokesessions");
        var id = _asks.Asks().Single().Id;
        var answer = await _asks.AnswerAsync(id, FolderAccess.Read, null, Ct, FolderScope.Everywhere);
        Assert.Equal("everywhere", answer["scope"]!.GetValue<string>());
        Assert.Contains(@"Z:\smokesessions", _policies.GetGlobal().Filesystem.Readonly);
        Assert.Empty(_policies.TeamFolders(_alpha));
        Assert.True(_policies.GetMember(_dev1).Loosen.IsEmpty);
        var beta = _policies.Effective(_betaManager, PolicyFixtures.Layout(TargetOs.Windows, AgentCli.Claude, new Yawble.Core.Policy.MemberRef("beta", "manager"))).Policy;
        Assert.Contains(@"Z:\smokesessions", beta.Readonly);
        Assert.Contains(@"Z:\smokesessions", Effective(_dev2).Readonly);
        Assert.Contains("Every agent can now read", Assert.Single(_told).Text);
        Assert.Contains("for every agent of every team", _asks.Events(id).Last().Detail);
    }

    [Fact]
    public async Task ADeny_HasNoScope()
    {
        await Ask(Dev1, @"Z:\smokesessions");
        var answer = await _asks.AnswerAsync(_asks.Asks().Single().Id, FolderAccess.Deny, null, Ct, FolderScope.Everywhere);
        Assert.Null(answer["scope"]);
        Assert.Empty(_policies.TeamFolders(_alpha));
        Assert.DoesNotContain(@"Z:\smokesessions", _policies.GetGlobal().Filesystem.Readonly.Concat(_policies.GetGlobal().Filesystem.Readwrite));
    }

    // ------------------------------------------------------------------ the Concierge's request

    private Task<string> ConciergeAsks(string team, string? member, string path, string access = "read") =>
        _asks.RequestFromConciergeAsync(team, member, path, access, "the nightly workflow reads the recorded sessions", Ct);

    [Fact]
    public async Task TheConciergeRaisesTheAsk_ThePersonAnswersIt_AndNoAgentIsWoken()
    {
        var text = await ConciergeAsks("Alpha", "DEV1", @"Z:\smokesessions", "readwrite");
        var ask = Assert.Single(_asks.Asks());
        Assert.StartsWith($"asked (folder request {ask.Id})", text);
        Assert.Contains(@"read-write access to Z:\smokesessions for alpha/dev1", text);
        Assert.Contains("this member, the whole team, or all teams", text);
        Assert.Contains("You cannot answer it for them", text);
        Assert.Equal(("alpha/dev1", FolderAskService.ConciergeAsker, _dev1, "readwrite"), (ask.Member, ask.AskedBy, ask.MemberId, ask.Access));
        Assert.Null(ask.Deliverable);
        Assert.Contains("(asked by the Concierge)", Assert.Single(_asks.PendingLines()));
        Assert.Contains("already waiting", await ConciergeAsks("alpha", "dev1", @"Z:\smokesessions", "readwrite"));
        Assert.Single(_asks.Asks());

        // Nothing is granted until the person answers; the Concierge can never answer.
        Assert.DoesNotContain(@"Z:\smokesessions", Effective(_dev1).Readwrite);
        using (Actor.Use(new Actor(Actor.Concierge, "alice")))
            await Assert.ThrowsAsync<ArgumentException>(() => _asks.AnswerAsync(ask.Id, FolderAccess.ReadWrite, null, Ct, FolderScope.Team));
        Assert.Empty(_policies.TeamFolders(_alpha));

        var answer = await _asks.AnswerAsync(ask.Id, FolderAccess.ReadWrite, null, Ct, FolderScope.Team);
        Assert.Null(answer["told"]);
        Assert.Empty(_told); // no run was blocked on it: the Manager is not woken
        Assert.Contains(@"Z:\smokesessions", Effective(_dev2).Readwrite);

        // Asked again once granted: it says so and raises nothing.
        var again = await ConciergeAsks("alpha", "dev2", @"Z:\smokesessions", "readwrite");
        Assert.Contains("already read-write for alpha/dev2", again);
        Assert.Contains("Nothing is asked.", again);
        Assert.Empty(_asks.Asks());
    }

    [Fact]
    public async Task AConciergeAskDenied_TellsNoAgent()
    {
        await ConciergeAsks("alpha", "dev1", @"Z:\smokesessions");
        var answer = await _asks.AnswerAsync(_asks.Asks().Single().Id, FolderAccess.Deny, "no", Ct);
        Assert.Null(answer["told"]);
        Assert.Empty(_told);
        Assert.Equal("denied", _asks.Asks(openOnly: false).Single().State);
    }

    [Fact]
    public async Task TheConciergesRequest_WithNoMember_IsForTheTeamsManager()
    {
        Assert.StartsWith("asked", await ConciergeAsks("alpha", null, @"Z:\smokesessions"));
        Assert.Equal(("alpha/manager", _manager), (_asks.Asks().Single().Member, _asks.Asks().Single().MemberId));
    }

    [Theory]
    [InlineData("gamma", null, "no team named 'gamma'")]
    [InlineData("alpha", "nobody", "no member named 'nobody' on team 'alpha'")]
    [InlineData("beta", "dev1", "no member named 'dev1' on team 'beta'")]
    public async Task TheConciergesRequest_ForAnUnknownTeamOrMember_IsRefused(string team, string? member, string why)
    {
        var text = await ConciergeAsks(team, member, @"Z:\smokesessions");
        Assert.StartsWith("refused:", text);
        Assert.Contains(why, text);
        Assert.Empty(_asks.Asks(openOnly: false));
    }

    [Theory]
    [InlineData(@"C:\Users\alice\.ssh", "read", "secrets")]
    [InlineData(@"D:\repos\beta-app", "read", "another team")]
    [InlineData(@"C:\Users\alice\AppData\Local\Yawble\data", "read", "Yawble's own data")]
    [InlineData(@"C:\Windows", "read", "Windows system folder")]
    [InlineData("sessions", "read", "not an absolute path")]
    public async Task TheConciergesRequest_ForAFolderNeverGranted_IsRefusedAtOnce(string path, string access, string why)
    {
        var text = await ConciergeAsks("alpha", "dev1", path, access);
        Assert.StartsWith("refused:", text);
        Assert.Contains(why, text, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(_asks.Asks(openOnly: false));
        await Assert.ThrowsAsync<ArgumentException>(() => _asks.RequestFromConciergeAsync("alpha", "dev1", @"Z:\x", "everything", "why", Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => _asks.RequestFromConciergeAsync("alpha", "dev1", @"Z:\x", "read", " ", Ct));
    }

    // ------------------------------------------------------------------ the team's folder list, as Team settings changes it

    [Fact]
    public async Task ThePersonAddsAndRemovesATeamFolder_AndNobodyElseCan()
    {
        foreach (var via in new[] { Actor.Concierge, Actor.Agent, Actor.External, Actor.Trigger, Actor.Host })
            using (Actor.Use(new Actor(via, "someone")))
            {
                await Assert.ThrowsAsync<ArgumentException>(() => _asks.AddTeamFolderAsync(_alpha, @"Z:\smokesessions", "readwrite", Ct));
                Assert.Throws<ArgumentException>(() => _asks.RemoveTeamFolder(_alpha, @"Z:\smokesessions"));
            }
        Assert.Empty(_policies.TeamFolders(_alpha));

        JsonObject listed;
        using (Actor.Use(Actor.CloudViewer("person@example.com")))
            listed = await _asks.AddTeamFolderAsync(_alpha, @"Z:\smokesessions\", "readwrite", Ct);
        var folder = Assert.Single(listed["folders"]!.AsArray())!;
        Assert.Equal((@"Z:\smokesessions", "readwrite", "person@example.com", Actor.Cloud),
            (folder["path"]!.GetValue<string>(), folder["access"]!.GetValue<string>(), folder["addedBy"]!.GetValue<string>(), folder["addedVia"]!.GetValue<string>()));
        Assert.Contains(@"Z:\smokesessions", Effective(_dev2).Readwrite);
        Assert.Contains(@"- Z:\smokesessions: read-write (added by person@example.com on ", Assert.Single(_asks.TeamFolderLines(_alpha)));

        // A refusal names the reason and writes nothing; another team's folder (the caller's list) is one.
        var refused = await Assert.ThrowsAsync<PolicyException>(() => _asks.AddTeamFolderAsync(_alpha, BetaRepo, "read", Ct));
        Assert.Contains(refused.Errors, x => x.Contains("another team"));
        await Assert.ThrowsAsync<ArgumentException>(() => _asks.AddTeamFolderAsync(_alpha, @"Z:\x", "everything", Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => _asks.AddTeamFolderAsync(_alpha, " ", "read", Ct));
        Assert.Single(_policies.TeamFolders(_alpha));

        Assert.Empty(_asks.RemoveTeamFolder(_alpha, @"Z:\smokesessions")!["folders"]!.AsArray());
        Assert.Null(_asks.RemoveTeamFolder(_alpha, @"Z:\smokesessions"));
        Assert.DoesNotContain(@"Z:\smokesessions", Effective(_dev2).Readwrite);
    }

    // ------------------------------------------------------------------ who may ask for whom

    [Fact]
    public async Task AManager_MayAskForAMemberOfItsOwnTeam()
    {
        var text = await Ask(Manager, @"Z:\smokesessions", member: "dev2");
        Assert.StartsWith("asked", text);
        var ask = Assert.Single(_asks.Asks());
        Assert.Equal(("alpha/dev2", "alpha/manager", _dev2), (ask.Member, ask.AskedBy, ask.MemberId));
        await _asks.AnswerAsync(ask.Id, FolderAccess.ReadWrite, null, Ct);
        Assert.Contains(@"Z:\smokesessions", Effective(_dev2).Readwrite);
        Assert.DoesNotContain(@"Z:\smokesessions", Effective(_dev1).Readwrite);
    }

    [Fact]
    public async Task AManagersDeny_TellsTheManagerWhoAsked()
    {
        await Ask(Manager, @"Z:\smokesessions", member: "alpha/dev2");
        await _asks.AnswerAsync(_asks.Asks().Single().Id, FolderAccess.Deny, null, Ct);
        var told = Assert.Single(_told);
        Assert.Equal("manager", told.Member);
        Assert.Contains("for dev2", told.Text);
    }

    [Theory]
    [InlineData("beta/manager")]
    [InlineData("nobody")]
    public async Task AManager_IsRefusedForAnotherTeamOrAnUnknownMember(string member)
    {
        Assert.StartsWith("refused:", await Ask(Manager, @"Z:\smokesessions", member: member));
        Assert.Empty(_asks.Asks(openOnly: false));
    }

    [Fact]
    public async Task AMember_MayNotAskForAnotherMember()
    {
        var text = await Ask(Dev1, @"Z:\smokesessions", member: "dev2");
        Assert.StartsWith("refused: only your Manager", text);
        Assert.Empty(_asks.Asks(openOnly: false));
        // Its own name is itself.
        Assert.StartsWith("asked", await Ask(Dev1, @"Z:\smokesessions", member: "dev1"));
    }

    // ------------------------------------------------------------------ refused at ask time, one per rule

    [Theory]
    [InlineData(@"data\sessions", "readwrite", "not an absolute path")]
    [InlineData(@"D:\", "read", "root")]
    [InlineData(@"\\fileserver\share", "read", "root")]
    [InlineData(@"C:\Users\alice", "read", "user profile folder")]
    [InlineData(@"C:\Users", "read", "contains the person's user profile")]
    [InlineData(@"C:\Users\alice\.azure", "read", "secrets")]
    [InlineData(@"C:\Users\alice\.claude", "read", "never granted")]
    [InlineData(@"C:\Users\alice\Documents\notes", "read", "policy denies")]
    [InlineData(@"C:\Users\alice\AppData\Local\Yawble\data\credentials", "read", "Yawble's own data")]
    [InlineData(@"C:\Users\alice\AppData\Local\Yawble\grok-home", "read", "Yawble's own data")]
    [InlineData(@"C:\Users\alice\AppData\Local\Yawble\backups", "read", "Yawble's own data folder")]
    [InlineData(@"C:\Users\alice\AppData\Local\Yawble\teams\beta\documents", "read", "another team")]
    [InlineData(@"D:\repos\beta-app\src", "readwrite", "another team")]
    [InlineData(@"C:\Users\alice\AppData\Local\Yawble\teams\alpha\members\dev2\repo", "read", "another member")]
    [InlineData(@"C:\Windows\System32", "read", "Windows system folder")]
    [InlineData(@"C:\Program Files\SomeTool", "readwrite", "never read-write")]
    [InlineData(@"C:\Program Files (x86)\SomeTool", "readwrite", "never read-write")]
    public async Task EachRefusal_IsSaidAtOnce_AndNoAskIsRaised(string path, string access, string why)
    {
        var text = await Ask(Dev1, path, access);
        Assert.StartsWith("refused:", text);
        Assert.Contains(why, text, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(_asks.Asks(openOnly: false));
        Assert.Empty(_told);
    }

    [Fact]
    public async Task ProgramFiles_MayBeAskedForReadOnly()
    {
        Assert.StartsWith("asked", await Ask(Dev1, @"C:\Program Files\SomeTool", "read"));
        await _asks.AnswerAsync(_asks.Asks().Single().Id, FolderAccess.Read, null, Ct);
        Assert.Contains(@"C:\Program Files\SomeTool", Effective(_dev1).Readonly);
    }

    [Fact]
    public async Task AFolderTheMemberAlreadyHas_IsNotAsked()
    {
        var text = await Ask(Dev1, @"C:\Users\alice\AppData\Local\Yawble\teams\alpha\members\dev1\repo\data");
        Assert.Contains("already read-write", text);
        Assert.Empty(_asks.Asks(openOnly: false));
    }

    [Fact]
    public async Task ABadAccessOrNoReason_IsAnError()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _asks.RequestFromAgentAsync(Dev1, @"Z:\x", "everything", "why", null, 1, 1, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => _asks.RequestFromAgentAsync(Dev1, @"Z:\x", "read", " ", null, 1, 1, Ct));
        Assert.Empty(_asks.Asks(openOnly: false));
    }

    // ------------------------------------------------------------------ the RPCs, the audit, the bus tool, the remote rule

    [Fact]
    public async Task TheAskListAndTheAnswer_AsTheWindowCallsThem()
    {
        await Ask(Dev1, @"Z:\smokesessions");
        await Ask(Dev1, @"Z:\not-yet", "read");
        var list = (await _asks.DispatchAsync("folder.asks", null, Ct))!.AsArray();
        Assert.Equal(2, list.Count);
        var item = list[0]!.AsObject();
        Assert.Equal("alpha/dev1", item["member"]!.GetValue<string>());
        Assert.Equal("alpha/dev1", item["askedBy"]!.GetValue<string>());
        Assert.Equal(@"Z:\smokesessions", item["path"]!.GetValue<string>());
        Assert.Equal("readwrite", item["access"]!.GetValue<string>());
        Assert.True(item["exists"]!.GetValue<bool>());
        Assert.False(list[1]!["exists"]!.GetValue<bool>());
        Assert.Equal("pending", item["state"]!.GetValue<string>());
        Assert.Null(item["grantedAccess"]);
        Assert.Equal(70, item["deliverable"]!.GetValue<long>());
        Assert.Equal(["readwrite", "read", "deny"], item["choices"]!.AsArray().Select(n => n!.GetValue<string>()));

        var bad = await Assert.ThrowsAsync<RpcException>(() => _asks.DispatchAsync("folder.answer", new JsonObject { ["id"] = item["id"]!.GetValue<long>(), ["choice"] = "everything" }, Ct));
        Assert.Equal("bad_params", bad.Code);
        var answer = (await _asks.DispatchAsync("folder.answer", new JsonObject { ["id"] = item["id"]!.GetValue<long>(), ["choice"] = "readwrite" }, Ct))!.AsObject();
        Assert.Equal("alpha/manager", answer["told"]!.GetValue<string>());
        Assert.Single((await _asks.DispatchAsync("folder.asks", null, Ct))!.AsArray());
        var all = (await _asks.DispatchAsync("folder.asks", new JsonObject { ["all"] = true }, Ct))!.AsArray();
        Assert.Equal("approved", all[0]!["state"]!.GetValue<string>());
        Assert.Empty(all[0]!["choices"]!.AsArray());
    }

    [Fact]
    public async Task EachAskAndAnswer_IsRecorded_AndTheAnswerCallIsAudited()
    {
        await Ask(Dev1, @"Z:\smokesessions");
        await Ask(Dev1, @"Z:\smokesessions");
        var id = _asks.Asks().Single().Id;
        var dispatcher = new RpcDispatcher(new Yawble.Core.Sandboxing.SandboxPlatformInfo(), () => [], audit: new AuditLog(_db.ConnectionString, _clock), folderAsks: _asks);
        await dispatcher.DispatchAsAsync(new Actor(Actor.App, "alice"), "folder.answer", new JsonObject { ["id"] = id, ["choice"] = "read" }, Ct);
        await dispatcher.DispatchAsAsync(new Actor(Actor.App, "alice"), "folder.asks", null, Ct);

        Assert.Equal(["asked", "asked again", "approved"], _asks.Events(id).Select(e => e.Kind));
        var approved = _asks.Events(id)[2];
        Assert.Equal(("alice", Actor.App), (approved.By, approved.Via));
        Assert.Contains(@"read-only Z:\smokesessions for alpha/dev1", approved.Detail);

        var audit = new AuditLog(_db.ConnectionString).Recent();
        var row = Assert.Single(audit); // folder.asks is a read: not audited
        Assert.Equal(("folder.answer", "done"), (row.Method, row.Outcome));
        Assert.Contains("member=alpha/dev1", row.Target);
    }

    [Fact]
    public void TheBusOffersRequestFolder_WithPathAccessAndReason()
    {
        Assert.Contains("request_folder", McpBusServer.ToolNames);
        var tool = McpBusServer.ToolList().Select(n => n!.AsObject()).Single(o => o["name"]!.GetValue<string>() == "request_folder");
        Assert.Equal(["access", "path", "reason"], tool["inputSchema"]!["required"]!.AsArray().Select(n => n!.GetValue<string>()).Order());
        Assert.NotNull(tool["inputSchema"]!["properties"]!["member"]);
        Assert.Contains("never ask the person to edit the policy by hand", tool["description"]!.GetValue<string>());
        Assert.Contains("request_folder", Yawble.Core.Teams.RoleTexts.Member);
        Assert.Contains("never ask the person to edit policy by hand", Yawble.Core.Teams.RoleTexts.Member.Replace("\r\n", " ").Replace("\n", " "), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("request_folder", Yawble.Core.Teams.RoleTexts.Manager);
        Assert.Contains("never ask\nthe person to edit policy by hand", Yawble.Core.Teams.RoleTexts.Manager.Replace("\r\n", "\n"));
    }

    [Fact]
    public void TheCloudLink_ListsAndAnswers_AsItDoesNetworkAsks()
    {
        Assert.Null(Yawble.Core.Link.RemoteAccess.Refusal("folder.asks", null, allowHostTerminals: false));
        Assert.Null(Yawble.Core.Link.RemoteAccess.Refusal("folder.answer", new JsonObject { ["id"] = 1, ["choice"] = "read" }, allowHostTerminals: false));
        Assert.Equal(Yawble.Core.Link.RemoteAccess.Kind.Reads, Yawble.Core.Link.RemoteAccess.Kinds["folder.asks"]);
        Assert.Equal(Yawble.Core.Link.RemoteAccess.Kind.Changes, Yawble.Core.Link.RemoteAccess.Kinds["folder.answer"]);
        Assert.Equal(Yawble.Core.Link.RemoteAccess.Kinds["network.answer"], Yawble.Core.Link.RemoteAccess.Kinds["folder.answer"]);
        Assert.False(AuditLog.IsReadOnly("folder.answer"));
        Assert.True(AuditLog.IsReadOnly("folder.asks"));
    }

    [Fact]
    public async Task AMemberRemoved_HasItsWaitingAsksClosed()
    {
        await Ask(Dev1, @"Z:\smokesessions");
        using (var c = new SqliteConnection(_db.ConnectionString))
        {
            c.Open();
            Assert.Equal(1, FolderAskService.CloseForRemovedMember(c, null, _dev1, "2026-11-20T10:00:00Z"));
        }
        Assert.Empty(_asks.Asks());
        Assert.Equal("closed", _asks.Asks(openOnly: false).Single().State);
        Assert.Equal(0, _asks.PendingCount());
    }
}
