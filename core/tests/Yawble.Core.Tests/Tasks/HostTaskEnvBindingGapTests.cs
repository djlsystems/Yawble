using System.Text.Json.Nodes;
using Yawble.Core.Connections;
using Yawble.Core.Provenance;
using Yawble.Core.Tasks;
using Xunit;

namespace Yawble.Core.Tests.Tasks;

/// <summary>
/// B023 verify (card 114): gap tests on card 112's core. Fakes only: the in-memory credential store and the temp checkout of
/// <see cref="TaskFixture"/>; no real gh and no network.
/// </summary>
public class HostTaskEnvBindingGapTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const string Token = "gap-release-token-value-0123456789";
    private const string ConnName = "GitHub release (Yawble)";

    private static long AddConnection(TaskFixture f) =>
        f.Conns!.Save(null, ConnName, [new ConnectionField("token", "Token", true, null)], new Dictionary<string, string> { ["token"] = Token }).Id;

    /// <summary>Card 112 rewrote HostTaskStore.Clip's "…" as mis-decoded bytes ("â€¦"): a clipped reason must end in the one ellipsis character and keep the cap.</summary>
    [Fact]
    public void ClipEndsInAnEllipsisAndKeepsTheCap()
    {
        var clipped = HostTaskStore.Clip(new string('a', 600), 500);
        Assert.Equal(500, clipped.Length);
        Assert.EndsWith("…", clipped);
        Assert.DoesNotContain("â", clipped);
    }

    [Fact]
    public void ABindingNamedLikeAPassedOnNameInAnotherCaseIsRefused()
    {
        using var f = new TaskFixture(withConnections: true);
        var d = f.Def("release") with { EnvNames = ["gh_token"], EnvFromConnections = [new EnvBinding("GH_TOKEN", "1", "token", "")] };
        Assert.Contains("already passed", Assert.Throws<ArgumentException>(() => HostTaskRules.Validate(d)).Message);
    }

    /// <summary>The Tasks panel over the Cloud Link sends the stored bindings back as tasks.list gave them (connectionId a string): another edit stays allowed.</summary>
    [Fact]
    public async Task ACloudLinkEditThatSendsTheStoredBindingBackAsTasksListGaveItIsAllowed()
    {
        using var f = new TaskFixture(withConnections: true);
        var id = AddConnection(f);
        var rpc = new HostTaskRpc(f.Svc, _ => ["main"]);
        JsonObject Save(JsonNode connectionId, int timeout) => new()
        {
            ["teamId"] = f.TeamId,
            ["def"] = new JsonObject
            {
                ["name"] = "release", ["program"] = f.Win ? "powershell" : "sh",
                ["args"] = new JsonArray(f.Def().Args.Select(a => (JsonNode?)a).ToArray()), ["timeoutSeconds"] = timeout,
                ["envFromConnections"] = new JsonArray(new JsonObject { ["name"] = "GH_TOKEN", ["connectionId"] = connectionId, ["field"] = "token", ["connectionName"] = ConnName }),
            },
        };
        using (Actor.Use(Actor.Window)) await rpc.DispatchAsync("tasks.save", Save(id, 60), Ct);
        JsonNode listed;
        using (Actor.Use(Actor.CloudViewer("remote"))) listed = (await rpc.DispatchAsync("tasks.list", new JsonObject { ["teamId"] = f.TeamId }, Ct))!;
        var storedId = listed["tasks"]![0]!["def"]!["envFromConnections"]![0]!["connectionId"]!.DeepClone();
        Assert.Equal(id.ToString(), storedId.GetValue<string>()); // out: a string

        using (Actor.Use(Actor.CloudViewer("remote")))
        {
            await rpc.DispatchAsync("tasks.save", Save(storedId, 90), Ct);
            await rpc.DispatchAsync("tasks.save", Save(id, 120), Ct); // in: a number is the same binding
        }
        Assert.Equal(120, f.Svc.Store.Task(f.TeamId, "release")!.Def.TimeoutSeconds);
    }

    /// <summary>The ask as the shell gets it (integrations asks): the binding line is in the detail, the value nowhere.</summary>
    [Fact]
    public async Task TheAskJsonCarriesTheBindingLineAndNoValue()
    {
        using var f = new TaskFixture(withConnections: true);
        var id = AddConnection(f);
        await f.SaveAsync(f.Def("release") with { EnvFromConnections = [new EnvBinding("GH_TOKEN", id.ToString(), "token", "")] });
        var ask = await f.Request("release");
        Assert.Equal(RequestKind.Asked, ask.Kind);
        var rpc = new HostTaskRpc(f.Svc, _ => ["main"]);
        var asks = rpc.Asks(f.TeamId);
        var json = asks.ToJsonString();
        Assert.Contains("env_from_connections", json);
        Assert.Contains($"GH_TOKEN ← Connection '{ConnName}' (token)", asks[0]!["detail"]!["env_from_connections"]![0]!.GetValue<string>());
        Assert.DoesNotContain(Token, json);
        Assert.DoesNotContain(Token, ask.Text);
    }
}
