using Yawble.Core.Bus;
using Yawble.Core.Teams;
using Xunit;

namespace Yawble.Core.Tests.Teams;

/// <summary>
/// Card 172 (verify B027 item 6): an old-style member's start when a blocker differs from its new branch only in case.
/// The B016 move's own clash check (Clash in TeamRepos.Rename.cs) still compares case-sensitively, but it is only reached
/// when the member's clone is on disk (reading the clone needs local transport, so that path is host-only). Without a
/// clone the move only records the new name, and the B020 check after it (ignoring case) gives the plain refusal that says
/// what moved. Temporary repositories only; nothing here reaches local transport.
/// </summary>
public class MemberBranchCaseClashTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static void RecordClone(TeamsFixture f, MemberRef member, string team, string branch)
    {
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection(f.Db.ConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT OR REPLACE INTO member_clones (member_id, path, branch, created_at) VALUES ($m, $p, $b, '2026-11-14T00:00:00Z');";
        cmd.Parameters.AddWithValue("$m", member.Id);
        cmd.Parameters.AddWithValue("$p", Path.Combine(f.Repos.MemberRoot(team, member.Name), "repo"));
        cmd.Parameters.AddWithValue("$b", branch);
        Assert.Equal(1, cmd.ExecuteNonQuery());
    }

    /// <summary>
    /// members/ada fetched back into the repository, and a blocker Members/Docs (loose or packed). The person gets the plain
    /// message naming the blocker and the move, never git's "cannot lock ref", and the repository's branches are unchanged
    /// (the move with no clone on disk records the new name only).
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnOldStyleMembersStart_WithABlockerInAnotherCase_GetsThePlainMessageThatSaysWhatMoved(bool packed)
    {
        using var f = new TeamsFixture();
        var (_, ada, _) = f.Team("Docs");
        var repo = await f.Repos.EnsureTeamRepoAsync(ada.TeamId, "Docs", Ct);
        AgentGit.Host(repo.Path, "branch", "members/ada", "main");
        AgentGit.Host(repo.Path, "branch", "Members/Docs", "main");
        if (packed) AgentGit.Host(repo.Path, "pack-refs", "--all");
        RecordClone(f, ada, "Docs", "members/ada");
        var before = AgentGit.Host(repo.Path, "for-each-ref", "--format=%(refname) %(objectname)");

        var e = await Assert.ThrowsAsync<MemberBranchBlockedException>(() => f.Repos.PrepareAsync(ada, Ct));

        // Git lists the blocker as the file system has it (members/Docs here, as members/ already existed): any case.
        Assert.Contains("The branch members/Docs in the team's repository", e.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Only this was changed before it was found: Ada's branch in the team's repository moved from members/ada to members/docs/ada", e.Message);
        Assert.DoesNotContain("cannot lock ref", e.Message);
        Assert.Equal(before, AgentGit.Host(repo.Path, "for-each-ref", "--format=%(refname) %(objectname)"));
        Assert.Equal("members/docs/ada", f.Repos.Row(ada.Id)!.Branch);
    }
}
