using Microsoft.Data.Sqlite;
using Yawble.Core.Policy;
using Yawble.Core.Tests.Bus;
using Xunit;

namespace Yawble.Core.Tests.Policy;

/// <summary>
/// A team's folder list (team_folders): a folder on it is in every member's effective policy, read-only or read-write,
/// one hired later too, and never another team's; a deny still wins; every refusal writes nothing; Remove takes it away;
/// a workflow member's file steps get the same folders. Fakes only: a temporary database and a fixed fictional Windows
/// machine (PolicyFixtures: the person "alice", teams alpha and beta).
/// </summary>
public sealed class TeamFolderTests : IDisposable
{
    private const string Sessions = @"Z:\smokesessions";
    private const string BetaRepo = @"D:\repos\beta-app";

    private readonly TempDb _db = new();
    private readonly PolicyStore _store;
    private readonly Dictionary<long, (string Team, string Name)> _who = [];
    private readonly long _alpha, _beta, _manager, _dev1, _dev2, _betaManager;

    public TeamFolderTests()
    {
        _store = new PolicyStore(_db.ConnectionString);
        _alpha = Insert("INSERT INTO teams (name, created_at) VALUES ('alpha', '2026-11-20T00:00:00Z') RETURNING id;");
        _beta = Insert("INSERT INTO teams (name, created_at) VALUES ('beta', '2026-11-20T00:00:00Z') RETURNING id;");
        _manager = Member(_alpha, "alpha", "manager", "manager");
        _dev1 = Member(_alpha, "alpha", "dev1", "member");
        _dev2 = Member(_alpha, "alpha", "dev2", "member");
        _betaManager = Member(_beta, "beta", "manager", "manager");
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

    private long Member(long teamId, string team, string name, string role)
    {
        var id = Insert($"INSERT INTO members (team_id, name, role, created_at) VALUES ({teamId}, '{name}', '{role}', '2026-11-20T00:00:00Z') RETURNING id;");
        _who[id] = (team, name);
        return id;
    }

    private RunLayout Layout(long id) => PolicyFixtures.Layout(TargetOs.Windows, AgentCli.Claude, new MemberRef(_who[id].Team, _who[id].Name));

    private List<(long, RunLayout)> Alpha => [.. _store.TeamMemberIds(_alpha).Select(id => (id, Layout(id)))];

    private MergeResult Merged(long id) => _store.Effective(id, Layout(id));

    private string? Add(string path, string access = "readwrite", PolicyStore? store = null) =>
        (store ?? _store).AddTeamFolder(_alpha, path, access, Alpha, [BetaRepo], "alice", "app");

    [Fact]
    public void ATeamFolder_IsInEveryMembersPolicy_OneHiredLaterToo_AndNeverAnotherTeams()
    {
        Assert.Equal(Sessions, Add(Sessions));

        foreach (var id in new[] { _manager, _dev1, _dev2 })
        {
            var e = Merged(id);
            Assert.Empty(e.Violations);
            Assert.Contains(Sessions, e.Policy.Readwrite);
            Assert.DoesNotContain(Sessions, e.Policy.Readonly);
            // The card's "loosened" chip and the Policy tab say where it comes from.
            Assert.Contains($"filesystem.readwrite + {Sessions} (team)", e.Policy.Loosened);
            Assert.True(e.Policy.IsLoosened);
        }
        var hired = Member(_alpha, "alpha", "dev3", "member");
        Assert.Contains(Sessions, Merged(hired).Policy.Readwrite);
        Assert.DoesNotContain(Sessions, Merged(_betaManager).Policy.Readwrite);
        Assert.Empty(_store.TeamFoldersOfMember(_betaManager));
        // The team-wide read (teams.agents) gives the same answer as the member's own.
        Assert.Contains(Sessions, _store.Effective(_dev1, Layout(_dev1), null, _store.SharedOf(_alpha)).Policy.Readwrite);

        var row = Assert.Single(_store.TeamFolders(_alpha));
        Assert.Equal((Sessions, "readwrite", "alice", "app"), (row.Path, row.Access, row.AddedBy, row.AddedVia));
        Assert.Empty(_store.TeamFolders(_beta));
        // Nothing is written into any member's own loosen block.
        Assert.True(_store.GetMember(_dev1).Loosen.IsEmpty);
    }

    [Fact]
    public void AReadOnlyFolder_IsReadable_NotWritable_AndAddingAgainChangesOnlyTheAccess()
    {
        Add(Sessions, "read");
        var e = Merged(_dev1).Policy;
        Assert.Contains(Sessions, e.Readonly);
        Assert.DoesNotContain(Sessions, e.Readwrite);
        Assert.Contains($"filesystem.readonly + {Sessions} (team)", e.Loosened);

        Assert.Null(Add(@"z:\SmokeSessions\", "read")); // the same folder, the same access: nothing to do
        Assert.Equal(Sessions, Add(@"z:\SmokeSessions", "readwrite")); // the row that is there gets the new access
        Assert.Equal("readwrite", Assert.Single(_store.TeamFolders(_alpha)).Access);
        Assert.Contains(Sessions, Merged(_dev1).Policy.Readwrite);
        Assert.Equal(Sessions, Add(Sessions, "read"));
        Assert.Contains(Sessions, Merged(_dev1).Policy.Readonly);
        Assert.Throws<PolicyException>(() => Add(Sessions, "everything"));
    }

    [Fact]
    public void ADeny_StillWins_AndNeverStopsTheTeamFromLaunching()
    {
        Add(Sessions);
        var global = _store.GetGlobal();
        _store.SaveGlobal(global with { Filesystem = global.Filesystem with { Denied = [.. global.Filesystem.Denied, @"Z:\"] } });

        var e = Merged(_dev1);
        Assert.Empty(e.Violations);
        Assert.DoesNotContain(Sessions, e.Policy.Readwrite.Concat(e.Policy.Readonly));
        Assert.DoesNotContain(e.Policy.Loosened, l => l.Contains(Sessions));
        Assert.Contains(e.Policy.Notes, n => n.Contains(Sessions) && n.Contains("dropped") && n.Contains("denied"));

        // A member's own deny (an override) does the same for that member only.
        _store.SaveGlobal(global);
        _store.SaveOverride(_dev2, new PolicyPatch(Filesystem: new FilesystemPatch(Denied: [Sessions])), Layout(_dev2));
        Assert.DoesNotContain(Sessions, Merged(_dev2).Policy.Readwrite);
        Assert.Contains(Sessions, Merged(_dev1).Policy.Readwrite);
    }

    [Theory]
    [InlineData(@"data\sessions", "read", "not an absolute path")]
    [InlineData(@"D:\", "read", "root")]
    [InlineData(@"C:\Users\alice", "read", "user profile folder")]
    [InlineData(@"C:\Users\alice\.ssh", "read", "secrets")]
    [InlineData(@"C:\Users\alice\Documents\notes", "read", "policy denies")]
    [InlineData(@"C:\Users\alice\AppData\Local\Yawble\data\credentials", "read", "Yawble's own data")]
    [InlineData(@"C:\Users\alice\AppData\Local\Yawble\teams\beta\documents", "read", "another team")]
    [InlineData(@"D:\repos\beta-app\src", "readwrite", "another team")]
    [InlineData(@"C:\Users\alice\AppData\Local\Yawble\teams\alpha\members\dev2\repo", "read", "another member")]
    [InlineData(@"C:\Windows\System32", "read", "Windows system folder")]
    [InlineData(@"C:\Program Files\SomeTool", "readwrite", "never read-write")]
    public void AFolderThatMayNeverBeGranted_IsRefused_AndNothingIsWritten(string path, string access, string why)
    {
        var e = Assert.Throws<PolicyException>(() => Add(path, access));
        Assert.Contains(e.Errors, x => x.Contains(why, StringComparison.OrdinalIgnoreCase));
        Assert.Empty(_store.TeamFolders(_alpha));
        Assert.Empty(Merged(_dev1).Policy.Loosened);
    }

    [Fact]
    public void AFolderOneMemberCannotHave_IsRefusedForTheWholeTeam()
    {
        // dev2's own policy denies it: the team's list would not give it to every member, so nothing is written.
        _store.SaveOverride(_dev2, new PolicyPatch(Filesystem: new FilesystemPatch(Denied: [Sessions])), Layout(_dev2));
        var e = Assert.Throws<PolicyException>(() => Add(Sessions));
        Assert.Contains(e.Errors, x => x.Contains("not be readable for dev2"));
        Assert.Empty(_store.TeamFolders(_alpha));

        // The global policy grants it read-only: a read-write team folder would stay read-only.
        _store.SaveOverride(_dev2, PolicyPatch.Empty, Layout(_dev2));
        var global = _store.GetGlobal();
        _store.SaveGlobal(global with { Filesystem = global.Filesystem with { Readonly = [Sessions] } });
        Assert.Contains(Assert.Throws<PolicyException>(() => Add(Sessions)).Errors, x => x.Contains("stay read-only"));
        Assert.Null(_store.TeamFolders(_alpha).FirstOrDefault());
    }

    [Fact]
    public void TheSandboxDryRun_SeesTheFolder_AndItsRefusalWritesNothing()
    {
        var seen = new List<PolicyPatch?>();
        var refuse = false;
        var store = new PolicyStore(_db.ConnectionString, (_, _, loosen, _) =>
        {
            seen.Add(loosen);
            return refuse ? ["the sandbox refused the compiled request: no"] : [];
        });
        Add(Sessions, "read", store);
        Assert.Equal(3, seen.Count); // once per member of the team
        Assert.All(seen, l => Assert.Contains(Sessions, l!.Filesystem!.Readonly!));

        refuse = true;
        var e = Assert.Throws<PolicyException>(() => Add(@"Z:\output", "readwrite", store));
        Assert.Contains("the sandbox refused the compiled request: no", e.Errors);
        Assert.Equal(Sessions, Assert.Single(store.TeamFolders(_alpha)).Path);
    }

    [Fact]
    public void AMemberHiredOrRemovedSinceTheListWasBuilt_WritesNothing()
    {
        var stale = Alpha;
        Member(_alpha, "alpha", "dev3", "member");
        Assert.Throws<TeamMembersChangedException>(() => _store.AddTeamFolder(_alpha, Sessions, "read", stale, [], "alice", "app"));
        Assert.Throws<TeamMembersChangedException>(() => _store.AddTeamFolder(_alpha, Sessions, "read", [.. Alpha.Take(2)], [], "alice", "app"));
        Assert.Empty(_store.TeamFolders(_alpha));
        Assert.Equal(Sessions, Add(Sessions, "read"));
    }

    [Fact]
    public void Remove_TakesTheFolderFromEveryMember()
    {
        Add(Sessions);
        Add(@"Z:\output", "read");
        Assert.True(_store.RemoveTeamFolder(_alpha, Sessions));
        Assert.False(_store.RemoveTeamFolder(_alpha, Sessions));
        Assert.False(_store.RemoveTeamFolder(_beta, @"Z:\output")); // another team's list is not touched
        var e = Merged(_dev1).Policy;
        Assert.DoesNotContain(Sessions, e.Readwrite);
        Assert.Contains(@"Z:\output", e.Readonly);
        Assert.Equal(@"Z:\output", Assert.Single(_store.TeamFolders(_alpha)).Path);
    }

    // ------------------------------------------------------------------ the merger alone

    private static MergeResult Merge(PolicyPatch? overridePatch, PolicyPatch? loosen, params TeamFolderGrant[] folders) =>
        PolicyMerger.Merge(GlobalPolicy.Default, overridePatch, loosen, PolicyFixtures.Layout(TargetOs.Windows, AgentCli.Claude), teamFolders: folders);

    [Fact]
    public void TheMerger_AddsTeamFoldersAsTheLoosenBlockDoes_OutsideTheGrantableRoots()
    {
        var none = Merge(null, null);
        var merged = Merge(null, null, new(Sessions, Write: true), new(@"Z:\reference", Write: false));
        Assert.Empty(merged.Violations);
        Assert.Empty(merged.Widening);
        Assert.Equal([.. none.Policy.Readwrite, Sessions], merged.Policy.Readwrite);
        Assert.Equal([.. none.Policy.Readonly, @"Z:\reference"], merged.Policy.Readonly);
        Assert.Equal([$"filesystem.readwrite + {Sessions} (team)", @"filesystem.readonly + Z:\reference (team)"], merged.Policy.Loosened);
        // Without the list the merge is what it is today.
        Assert.Empty(none.Policy.Loosened);
    }

    [Fact]
    public void TheMerger_DropsATeamFolderTheFloorRefuses_WithANote_NotAViolation()
    {
        var merged = Merge(null, null,
            new(@"C:\Users\alice\AppData\Local\Yawble\teams\beta", Write: false),
            new(@"C:\Users\alice\AppData\Local\Yawble", Write: true),
            new(@"C:\Users\alice\.ssh\keys", Write: false),
            new("relative", Write: false));
        Assert.Empty(merged.Violations);
        Assert.Empty(merged.Policy.Loosened);
        Assert.Equal(4, merged.Policy.Notes.Count(n => n.Contains("the team's folder") && n.Contains("dropped")));
        Assert.DoesNotContain(merged.Policy.Readonly.Concat(merged.Policy.Readwrite), p => p.EndsWith(@"\Yawble") || p.Contains(".ssh") || p.EndsWith(@"teams\beta"));
    }

    [Fact]
    public void TheMerger_AReadWriteTeamFolderWinsOverTheMembersOwnReadOnlyGrant_NeverOverAnOverride()
    {
        var own = new PolicyPatch(Filesystem: new FilesystemPatch(Readonly: [Sessions]));
        var merged = Merge(null, own, new TeamFolderGrant(Sessions, Write: true));
        Assert.Contains(Sessions, merged.Policy.Readwrite);
        Assert.DoesNotContain(Sessions, merged.Policy.Readonly);
        Assert.Equal([$"filesystem.readwrite + {Sessions} (team)"], merged.Policy.Loosened);

        // The member's own wider grant stays when the team's is read-only.
        var wider = Merge(null, new PolicyPatch(Filesystem: new FilesystemPatch(Readwrite: [Sessions])), new TeamFolderGrant(Sessions, Write: false));
        Assert.Contains(Sessions, wider.Policy.Readwrite);
        Assert.DoesNotContain(Sessions, wider.Policy.Readonly);

        // An override's deny is a tightening: it wins.
        var denied = Merge(new PolicyPatch(Filesystem: new FilesystemPatch(Denied: [Sessions])), null, new TeamFolderGrant(Sessions, Write: true));
        Assert.DoesNotContain(Sessions, denied.Policy.Readwrite.Concat(denied.Policy.Readonly));
        Assert.Empty(denied.Violations);
    }

    // ------------------------------------------------------------------ workflow members

    [Fact]
    public void AWorkflowMembersFolders_AreItsOwnGrantsAndItsTeams_ReadOnlyOnesNotWritable()
    {
        var paths = new PolicyPaths(TargetOs.Windows, @"C:\Users\alice");
        Assert.Empty(_store.WorkflowFolders(_dev1, paths));

        var own = new PolicyPatch(Filesystem: new FilesystemPatch(Readonly: [@"Z:\own-read"], Readwrite: [@"Z:\own-write"]));
        _store.SaveLoosen(_dev1, own, Layout(_dev1), _store.PreviewLoosen(_dev1, own, Layout(_dev1)));
        Add(Sessions, "readwrite");
        Add(@"Z:\reference", "read");
        Add(@"Z:\own-read", "readwrite"); // the team's wider grant of a folder the member has read-only

        var folders = _store.WorkflowFolders(_dev1, paths);
        Assert.Contains((Sessions, true), folders);
        Assert.Contains((@"Z:\reference", false), folders);
        Assert.Contains((@"Z:\own-write", true), folders);
        // Of one folder granted both ways the writable grant comes first: a file step finds it first.
        Assert.True(folders.ToList().IndexOf((@"Z:\own-read", true)) < folders.ToList().IndexOf((@"Z:\own-read", false)));
        // A member of the team without grants of its own has the team's only; another team's member has none.
        Assert.Equal([(Sessions, true), (@"Z:\reference", false), (@"Z:\own-read", true)], _store.WorkflowFolders(_dev2, paths).OrderByDescending(f => f.Path == Sessions).ThenBy(f => f.Path == @"Z:\own-read"));
        Assert.Empty(_store.WorkflowFolders(_betaManager, paths));

        // A folder under a path the global policy denies is left out.
        var global = _store.GetGlobal();
        _store.SaveGlobal(global with { Filesystem = global.Filesystem with { Denied = [.. global.Filesystem.Denied, @"Z:\reference"] } });
        Assert.DoesNotContain(_store.WorkflowFolders(_dev1, paths), f => f.Path == @"Z:\reference");
    }
}
