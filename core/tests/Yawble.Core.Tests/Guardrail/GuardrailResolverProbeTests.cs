using System.Text.Json.Nodes;
using Yawble.Core.Guardrail;
using Yawble.Core.Launch;
using Yawble.Core.Policy;
using Yawble.Core.Sandboxing;
using Yawble.Core.Tests.Launch;
using Xunit;

namespace Yawble.Core.Tests.Guardrail;

/// <summary>
/// B035: the guardrail check connects to the host resolver's own sockets from inside (systemd-resolved's varlink first),
/// around the name-service switch, and each must be BLOCKED. Scripted sandbox: no real sandbox, no socket.
/// </summary>
public class GuardrailResolverProbeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Line(string id, string outcome) =>
        $"{{\"id\":\"{id}\",\"category\":\"network\",\"outcome\":\"{outcome}\",\"evidence\":\"x\"}}";

    private static IEnumerable<string> Ids(JsonObject plan, string key) =>
        (plan[key]?.AsArray() ?? []).Select(n => n!["id"]!.GetValue<string>());

    /// <summary>Every attempt the plan names reported, the given ids allowed, the rest blocked (the clone write reaches the host).</summary>
    private static string Report(JsonObject plan, params string[] allowed)
    {
        var lines = new List<string>();
        foreach (var r in plan["reads"]!.AsArray())
            lines.Add(r!["mode"]!.GetValue<string>() == "list"
                ? $"{{\"id\":\"{r["id"]}\",\"category\":\"t\",\"outcome\":\"blocked\",\"evidence\":\"x\",\"entries\":0}}"
                : Line(r["id"]!.GetValue<string>(), "blocked"));
        foreach (var w in plan["writes"]!.AsArray())
        {
            var id = w!["id"]!.GetValue<string>();
            if (id == "write.clone") File.WriteAllText(w["path"]!.GetValue<string>(), plan["nonce"]!.GetValue<string>());
            lines.Add($"{{\"id\":\"{id}\",\"category\":\"t\",\"outcome\":\"allowed\",\"evidence\":\"x\",\"sandboxWrote\":true}}");
        }
        foreach (var id in Ids(plan, "tcp").Concat(Ids(plan, "dns")).Concat(Ids(plan, "unix")))
            lines.Add(Line(id, allowed.Contains(id) ? "allowed" : "blocked"));
        return string.Join('\n', lines);
    }

    [Fact]
    public async Task TheVarlinkSocketAndTheOtherResolverSocketsAreTriedOnLinuxOnly()
    {
        await using var kit = GuardrailKit.Create();
        JsonObject? seen = null;
        var sandbox = new ScriptedSandbox((_, plan) => { seen = plan; return new SandboxOutcome(0, 0, false, Report(plan), "", null); });
        var report = await kit.Check(sandbox).RunAsync("payments", "ada", Ct);

        var unix = (seen!["unix"]?.AsArray() ?? []).Select(n => (Id: n!["id"]!.GetValue<string>(), Path: n["path"]!.GetValue<string>())).ToList();
        if (!OperatingSystem.IsLinux())
        {
            Assert.Empty(unix);
            Assert.DoesNotContain(report.Results, r => r.Id == "net.dns-varlink");
            return;
        }
        Assert.Contains(("net.dns-varlink", LinuxResolverMasks.ResolvedVarlinkSocket), unix);
        Assert.Equal(LinuxResolverMasks.Sockets.Select(s => s.Id), unix.Select(u => u.Id));
        foreach (var (id, _) in LinuxResolverMasks.Sockets)
        {
            var r = report.Results.Single(x => x.Id == id);
            Assert.Equal(Verdicts.Blocked, r.Expected);
            Assert.Equal(Verdicts.Blocked, r.Verdict);
            Assert.True(r.AsExpected);
        }
    }

    [Fact]
    public async Task AReachableVarlinkSocketFailsTheCheck()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "the resolver sockets are tried on Linux only");
        await using var kit = GuardrailKit.Create();
        var sandbox = new ScriptedSandbox((_, plan) => new SandboxOutcome(0, 0, false, Report(plan, "net.dns-varlink"), "", null));
        var report = await kit.Check(sandbox).RunAsync("payments", "ada", Ct);

        var r = report.Results.Single(x => x.Id == "net.dns-varlink");
        Assert.Equal(Verdicts.Allowed, r.Verdict);
        Assert.False(r.AsExpected);
        Assert.False(report.Passed);
    }
}
