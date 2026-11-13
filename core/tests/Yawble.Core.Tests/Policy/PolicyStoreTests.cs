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
