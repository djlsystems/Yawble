using Microsoft.Data.Sqlite;
using Yawble.Core.Sandboxing;
using Yawble.Core.Policy;
using Yawble.Core.Storage;
using Xunit;

namespace Yawble.Core.Tests.Policy;

public sealed class PolicyStoreTests : IDisposable
{
    private readonly string dir = Directory.CreateTempSubdirectory("yawble-policy-").FullName;
    private readonly string cs;

    public PolicyStoreTests()
    {
        cs = $"Data Source={Path.Combine(dir, "t.db")};Pooling=False";
        SchemaMigrator.Migrate(cs);
    }

    public void Dispose() => Directory.Delete(dir, true);

    private static RunLayout Linux => PolicyFixtures.Layout(TargetOs.Linux, AgentCli.Claude);

    [Fact]
    public void Migration0003CreatesThePolicyTables()
    {
        Assert.Contains("0003_policy", SchemaMigrator.Available);
        using var c = new SqliteConnection(cs);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name IN ('policy_global', 'member_policy') ORDER BY name;";
        using var r = cmd.ExecuteReader();
        var names = new List<string>();
        while (r.Read()) names.Add(r.GetString(0));
        Assert.Equal(["member_policy", "policy_global"], names);
    }

    [Fact]
    public void TheGlobalPolicyDefaultsAndRoundTrips()
    {
        var store = new PolicyStore(cs);
        Assert.Equal(GlobalPolicy.Default.ToJson().ToJsonString(), store.GetGlobal().ToJson().ToJsonString());
        store.SaveGlobal(PolicyFixtures.Global, Linux);
        Assert.Equal(["github.com", "registry.npmjs.org"], store.GetGlobal().Network.AllowHosts);
        Assert.Throws<PolicyException>(() => store.SaveGlobal(PolicyFixtures.Global with { Network = new NetworkPolicy(["api.x.ai"]) }, Linux));
        Assert.Throws<PolicyException>(() => store.SaveGlobal(PolicyFixtures.Global with { App = new AppPolicy(0, "default") }));
    }

    [Fact]
    public void SavingAWideningOverrideIsRefused()
    {
        var store = new PolicyStore(cs);
        store.SaveGlobal(PolicyFixtures.Global with { Compute = PolicyFixtures.Global.Compute with { MemoryMb = 4096 } });
        var hosts = Assert.Throws<PolicyException>(() => store.SaveOverride(7, PolicyPatch.Parse("""{"network":{"allowHosts":["evil.example.com"]}}""", PatchKind.Override), Linux));
        Assert.Contains(hosts.Errors, e => e.Contains("loosen block"));
        var memory = Assert.Throws<PolicyException>(() => store.SaveOverride(7, PolicyPatch.Parse("""{"compute":{"memoryMb":8192}}""", PatchKind.Override), Linux));
        Assert.Contains(memory.Errors, e => e.Contains("memoryMb"));
        Assert.True(store.GetMember(7).Override.IsEmpty);

        store.SaveOverride(7, PolicyFixtures.Override, Linux);
        Assert.Equal(900, store.Effective(7, Linux).Policy.Compute.WallTimeoutSec);
    }

    [Fact]
    public void TheLoosenBlockNeedsTheExactConfirmation()
    {
        var store = new PolicyStore(cs);
        store.SaveGlobal(PolicyFixtures.Global);
        store.SaveOverride(7, PolicyFixtures.Override, Linux);
        var preview = store.PreviewLoosen(7, PolicyFixtures.Loosen, Linux);
        Assert.Equal(["network.allowHosts + pypi.org", "compute.wallTimeoutSec 900 -> 14400"], preview);

        Assert.Throws<PolicyException>(() => store.SaveLoosen(7, PolicyFixtures.Loosen, Linux, ["network.allowHosts + pypi.org"]));
        Assert.True(store.GetMember(7).Loosen.IsEmpty);

        store.SaveLoosen(7, PolicyFixtures.Loosen, Linux, preview);
        var m = store.GetMember(7);
        Assert.Equal(preview, m.LoosenSummary);
        Assert.NotNull(m.LoosenApprovedAt);
        var effective = store.Effective(7, Linux).Policy;
        Assert.True(effective.IsLoosened);
        Assert.Equal(["registry.npmjs.org", "pypi.org"], effective.AllowHosts);

        Assert.Throws<PolicyException>(() => store.SaveLoosen(7, PolicyPatch.Parse("""{"network":{"allowHosts":["platform.claude.com"]}}""", PatchKind.Loosen), Linux, []));

        store.SaveLoosen(7, PolicyPatch.Empty, Linux, []);
        Assert.False(store.Effective(7, Linux).Policy.IsLoosened);
        Assert.Null(store.GetMember(7).LoosenApprovedAt);
    }

    [Fact]
    public void EverySaveIsDryRunThroughTheValidator()
    {
        var calls = 0;
        var store = new PolicyStore(cs, (g, o, l, layout) => { calls++; return calls == 3 ? ["the sandbox said no"] : []; });
        store.SaveGlobal(PolicyFixtures.Global, Linux);
        store.SaveOverride(1, PolicyFixtures.Override, Linux);
        var e = Assert.Throws<PolicyException>(() => store.SaveLoosen(1, PolicyFixtures.Loosen, Linux, store.PreviewLoosen(1, PolicyFixtures.Loosen, Linux)));
        Assert.Equal(["the sandbox said no"], e.Errors);
        Assert.Equal(3, calls);
    }

    // ------------------------------------------------------------------ B021 (card 87): AddTeamHost in one transaction

    private const string NoHostsOverride = """{"network":{"allowHosts":[]}}""";

    /// <summary>A fake sandbox dry run: refuses a member whose override narrows the network to nothing.</summary>
    private static IReadOnlyList<string> RefuseNarrowed(GlobalPolicy g, PolicyPatch? o, PolicyPatch? l, RunLayout layout) =>
        o?.Network?.AllowHosts is { Count: 0 } ? ["the sandbox refused the compiled request: no hosts for this member"] : [];

    private (long Team, long Ada, long Ben) SeedTeam()
    {
        Exec(cs, "INSERT INTO teams (id, name, created_at) VALUES (5, 'payments', 't');" +
                 "INSERT INTO members (id, team_id, name, role, created_at) VALUES (51, 5, 'Ada', 'member', 't'), (52, 5, 'Ben', 'member', 't');");
        return (5, 51, 52);
    }

    private static int Exec(string connectionString, string sql)
    {
        using var c = new SqliteConnection(connectionString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteNonQuery();
    }

    private static string SetOverrideSql(long memberId) =>
        $"INSERT INTO member_policy (member_id, override_json, updated_at) VALUES ({memberId}, '{NoHostsOverride}', 't') " +
        "ON CONFLICT (member_id) DO UPDATE SET override_json = excluded.override_json, updated_at = excluded.updated_at;";

    private long TeamRows(long team)
    {
        using var c = new SqliteConnection(cs);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT count(*) FROM team_network_allows WHERE team_id = {team};";
        return (long)cmd.ExecuteScalar()!;
    }

    [Fact]
    public void AddTeamHost_OneMembersRunRefuses_TheWholeAddIsRefused_NothingWritten()
    {
        var (team, ada, ben) = SeedTeam();
        Exec(cs, SetOverrideSql(ben));
        var store = new PolicyStore(cs, (g, o, l, layout) => RefuseNarrowed(g, o, l, layout));

        var e = Assert.Throws<PolicyException>(() => store.AddTeamHost(team, "pypi.org", [(ada, Linux), (ben, Linux)], "pat", "app"));

        Assert.Equal(["the sandbox refused the compiled request: no hosts for this member"], e.Errors);
        Assert.Equal(0, TeamRows(team));
        Assert.Empty(store.TeamHosts(team));
        Assert.True(store.GetMember(ada).Loosen.IsEmpty); // no member half-loosened either
        // The refusal left no transaction or lock behind: a write goes straight through.
        Assert.Equal(1, Exec(cs, "INSERT INTO team_network_allows (team_id, host, added_by, added_at) VALUES (5, 'example.org', 'pat', 't');"));
    }

    [Fact]
    public void AddTeamHost_EveryMemberAccepts_WritesExactlyOneRow_AndARepeatWritesNone()
    {
        var (team, ada, ben) = SeedTeam();
        var calls = 0;
        var store = new PolicyStore(cs, (_, _, _, _) => { calls++; return []; });

        Assert.True(store.AddTeamHost(team, "pypi.org", [(ada, Linux), (ben, Linux)], "pat", "app"));

        Assert.Equal(2, calls); // each member's run checked
        Assert.Equal(1, TeamRows(team));
        var row = Assert.Single(store.TeamHosts(team));
        Assert.Equal(("pypi.org", "pat", "app"), (row.Host, row.AddedBy, row.AddedVia));
        Assert.False(store.AddTeamHost(team, "pypi.org", [(ada, Linux), (ben, Linux)], "pat", "app"));
        Assert.Equal(1, TeamRows(team));
        Assert.Contains("pypi.org", store.Effective(ben, Linux).Policy.AllowHosts);
    }

    [Fact]
    public void AddTeamHost_AConcurrentPolicyChangeBetweenCheckAndWrite_CannotLeaveARowAMembersCheckWouldRefuse()
    {
        var (team, ada, ben) = SeedTeam();
        // The concurrent change, forced through the fake dry run: while the add is checking Ben's run (his policy read and
        // accepted, the row not yet written), another connection narrows Ben's network to nothing - which his check
        // refuses. Before B021 the add then wrote the row anyway: a row Ben's check refuses.
        var calls = 0;
        var store = new PolicyStore(cs, (g, o, l, layout) =>
        {
            var answer = RefuseNarrowed(g, o, l, layout);
            if (++calls == 2) Exec(cs, SetOverrideSql(ben)); // Ben's first check, after it was answered
            return answer;
        });

        var e = Assert.Throws<PolicyException>(() => store.AddTeamHost(team, "pypi.org", [(ada, Linux), (ben, Linux)], "pat", "app"));

        // The write's transaction saw Ben's policy was not what was checked, checked again on the new state, and refused.
        Assert.Equal(4, calls);
        Assert.Equal(["the sandbox refused the compiled request: no hosts for this member"], e.Errors);
        Assert.Equal(0, TeamRows(team));
        Assert.Empty(store.GetMember(ben).Override.Network!.AllowHosts!); // the change itself stands
    }

    [Fact]
    public void AddTeamHost_AHarmlessConcurrentChange_IsCheckedAgain_AndOneThatKeepsChangingIsRefused()
    {
        var (team, ada, ben) = SeedTeam();
        string Loosen(int n) => "{\"network\":{\"allowHosts\":[\"mirror" + n + ".example.com\"]}}";
        void ChangeAda(int n) => Exec(cs, $"INSERT INTO member_policy (member_id, loosen_json, updated_at) VALUES ({ada}, '{Loosen(n)}', 't') " +
                                          "ON CONFLICT (member_id) DO UPDATE SET loosen_json = excluded.loosen_json;");
        // One change during the first check: the second check sees it, and the row is written against it.
        var calls = 0;
        var once = new PolicyStore(cs, (_, _, _, _) => { if (++calls == 1) ChangeAda(1); return []; });
        Assert.True(once.AddTeamHost(team, "pypi.org", [(ada, Linux), (ben, Linux)], "pat", "app"));
        Assert.Equal(4, calls);
        Assert.Equal(1, TeamRows(team));

        // A change during every check: refused after the last attempt, nothing written.
        var n = 1;
        var always = new PolicyStore(cs, (_, _, _, _) => { ChangeAda(++n); return []; });
        var e = Assert.Throws<PolicyException>(() => always.AddTeamHost(team, "registry.npmjs.org", [(ada, Linux), (ben, Linux)], "pat", "app"));
        Assert.Contains("kept changing", Assert.Single(e.Errors));
        Assert.Equal(["pypi.org"], always.TeamHosts(team).Select(h => h.Host));
    }

    // ------------------------------------------------------------------ B022 (card 127): the team's members are what was checked

    [Fact]
    public void AddTeamHost_AMemberHiredAfterTheSnapshot_WritesNothingAgainstTheOldList_AndTheirRefusalStopsTheAdd()
    {
        var (team, ada, ben) = SeedTeam();
        const long cleo = 53;
        // The hire, forced through the fake dry run: after the snapshot read and the checks of Ada and Ben, before the
        // write, Cleo joins the team with a policy whose check refuses the host.
        var calls = 0;
        var store = new PolicyStore(cs, (g, o, l, layout) =>
        {
            var answer = RefuseNarrowed(g, o, l, layout);
            if (++calls == 2)
                Exec(cs, $"INSERT INTO members (id, team_id, name, role, created_at) VALUES ({cleo}, 5, 'Cleo', 'member', 't');" + SetOverrideSql(cleo));
            return answer;
        });

        var changed = Assert.Throws<TeamMembersChangedException>(() => store.AddTeamHost(team, "pypi.org", [(ada, Linux), (ben, Linux)], "pat", "app"));

        Assert.Equal(2, calls); // the transaction saw Cleo and stopped: no write, no check against the old list again
        Assert.Equal([ada, ben], changed.Given);
        Assert.Equal([ada, ben, cleo], changed.Current);
        Assert.Contains("members changed", Assert.Single(changed.Errors));
        Assert.Equal(0, TeamRows(team));
        // The refusal left no lock behind, and the list with Cleo in it is refused by Cleo's check.
        var e = Assert.Throws<PolicyException>(() => store.AddTeamHost(team, "pypi.org", [(ada, Linux), (ben, Linux), (cleo, Linux)], "pat", "app"));
        Assert.IsNotType<TeamMembersChangedException>(e);
        Assert.Equal(["the sandbox refused the compiled request: no hosts for this member"], e.Errors);
        Assert.Equal(0, TeamRows(team));
    }

    [Fact]
    public void AddTeamHost_AMemberRemovedBeforeTheWrite_IsAChangeToo_NothingWritten()
    {
        var (team, ada, ben) = SeedTeam();
        var calls = 0;
        var store = new PolicyStore(cs, (_, _, _, _) => { if (++calls == 2) Exec(cs, $"DELETE FROM members WHERE id = {ben};"); return []; });

        var changed = Assert.Throws<TeamMembersChangedException>(() => store.AddTeamHost(team, "pypi.org", [(ada, Linux), (ben, Linux)], "pat", "app"));

        Assert.Equal([ada], changed.Current);
        Assert.Equal(0, TeamRows(team));
        Assert.True(store.AddTeamHost(team, "pypi.org", [(ada, Linux)], "pat", "app"));
    }

    [Fact]
    public void AddTeamHost_AListThatIsNotTheTeamsMembers_IsRefusedBeforeAnyCheck_InEitherDirection()
    {
        var (team, ada, ben) = SeedTeam();
        var calls = 0;
        var store = new PolicyStore(cs, (_, _, _, _) => { calls++; return []; });

        Assert.Throws<TeamMembersChangedException>(() => store.AddTeamHost(team, "pypi.org", [], "pat", "app"));            // missing both
        Assert.Throws<TeamMembersChangedException>(() => store.AddTeamHost(team, "pypi.org", [(ada, Linux)], "pat", "app")); // missing one
        Assert.Throws<TeamMembersChangedException>(() => store.AddTeamHost(team, "pypi.org", [(ada, Linux), (ben, Linux), (99, Linux)], "pat", "app")); // one not there

        Assert.Equal(0, calls);
        Assert.Equal(0, TeamRows(team));
    }

    [Fact]
    public void TheStandardDryRunAsksTheRealSandbox()
    {
        // The host's own OS: on this machine the request goes to the native parser (and, where the backend
        // is installed, a no-op launch under the exact policy).
        var layout = PolicyFixtures.Layout(TargetOsExtensions.Host, AgentCli.Claude);
        Assert.Empty(PolicyStore.ThroughSandbox(PolicyFixtures.Global, PolicyFixtures.Override, null, layout));
        Assert.NotEmpty(PolicyStore.ThroughSandbox(PolicyFixtures.Global, null, PolicyPatch.Parse("""{"network":{"allowHosts":["api.anthropic.com"]}}""", PatchKind.Loosen), layout));
    }
}
