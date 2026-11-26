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
/// refusal is said at once with no ask. Fakes only: a temporary database, a fixed fictional Windows machine
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

    private EffectivePolicy Effective(long memberId) =>
        _policies.Effective(memberId, PolicyFixtures.Layout(TargetOs.Windows, AgentCli.Claude, new Yawble.Core.Policy.MemberRef("alpha", _names[memberId]))).Policy;

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
