using System.Text.Json.Nodes;
using Yawble.Core.Integrations;
using Yawble.Core.Ipc;
using Yawble.Core.Policy;
using Yawble.Core.Tests.Bus;
using Xunit;

namespace Yawble.Core.Tests.Integrations;

/// <summary>integrations.openLogin on every OS, with a fake terminal start: no real window ever opens.</summary>
public class OpenLoginTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const string Cli = "yawblenosuchcli";

    private static async Task<(TempDb Db, Yawble.Core.Bus.BusEngine Engine, IntegrationService Svc, List<TerminalStart> Started)> Rig(TargetOs os, Func<string, string?> find)
    {
        var db = new TempDb();
        var engine = new Yawble.Core.Bus.BusEngine(db.OpenStore(), new ScriptedRunner(), new Yawble.Core.Bus.BusOptions { IdleOffer = false });
        var started = new List<TerminalStart>();
        var svc = new IntegrationService(db.ConnectionString, engine) { TerminalOs = os, FindTerminal = find, StartTerminal = started.Add };
        await Task.CompletedTask;
        return (db, engine, svc, started);
    }

    [Theory]
    [InlineData(TargetOs.Windows)]
    [InlineData(TargetOs.MacOS)]
    [InlineData(TargetOs.Linux)]
    public async Task EveryOs_OpensATerminal(TargetOs os)
    {
        var (db, engine, svc, started) = await Rig(os, n => n == "xterm" ? "/usr/bin/xterm" : null);
        using (db) await using (engine) await using (svc)
        {
            var reply = await svc.DispatchAsync("integrations.openLogin", new JsonObject { ["name"] = Cli }, Ct);
            Assert.True((bool)reply!["ok"]!);
            var start = Assert.Single(started);
            Assert.Equal(os switch { TargetOs.Windows => "cmd.exe", TargetOs.MacOS => "open", _ => "xterm" }, Path.GetFileName(start.FileName.Replace('\\', '/')));
        }
    }

    [Fact]
    public async Task Linux_WithNoTerminal_SaysSoPlainly_AndStartsNothing()
    {
        var (db, engine, svc, started) = await Rig(TargetOs.Linux, _ => null);
        using (db) await using (engine) await using (svc)
        {
            var e = await Assert.ThrowsAsync<RpcException>(() => svc.DispatchAsync("integrations.openLogin", new JsonObject { ["name"] = Cli }, Ct));
            Assert.Contains("No terminal program was found", e.Message);
            Assert.Empty(started);
        }
    }

    [Fact]
    public async Task AStartThatFails_SaysWhatToRunInstead()
    {
        var (db, engine, svc, _) = await Rig(TargetOs.Linux, n => "/usr/bin/" + n);
        svc.StartTerminal = _ => throw new System.ComponentModel.Win32Exception("no such file");
        using (db) await using (engine) await using (svc)
        {
            var e = await Assert.ThrowsAsync<RpcException>(() => svc.DispatchAsync("integrations.openLogin", new JsonObject { ["name"] = Cli }, Ct));
            Assert.Contains("Could not open a terminal", e.Message);
            Assert.Contains("open your own terminal in", e.Message);
        }
    }
}
