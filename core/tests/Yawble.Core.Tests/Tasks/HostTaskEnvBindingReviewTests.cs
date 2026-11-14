using System.Text.Json.Nodes;
using Yawble.Core.Connections;
using Yawble.Core.Ipc;
using Yawble.Core.Provenance;
using Yawble.Core.Tasks;
using Xunit;

namespace Yawble.Core.Tests.Tasks;

/// <summary>
/// B023 review fixes: masking of short, nested and multi-line values; the canonical Connection id; no Cloud Link probing of
/// Connections through a preview. Fakes only: the in-memory credential store of <see cref="TaskFixture"/>; nothing runs.
/// </summary>
public class HostTaskEnvBindingReviewTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static long AddConnection(TaskFixture f, string value, string name = "Release") =>
        f.Conns!.Save(null, name, [new ConnectionField("token", "Token", true, null)], new Dictionary<string, string> { ["token"] = value }).Id;

    [Fact]
    public void ALongerSecretIsMaskedWhole_EvenWhenAShorterOneSitsInsideIt()
    {
        const string inner = "abcdefgh12";
        const string outer = "prefix-abcdefgh12-suffix-token";
        Assert.Equal("token=[redacted]", TaskProcess.Redact($"token={outer}", [inner, outer]));
    }

    [Fact]
    public void AMultiLineValue_IsMaskedLineByLine()
    {
        const string key = "-----BEGIN KEY-----\nMIIEvQIBADANBgkqhkiG9w0BAQEFAASC\nAnotherLongKeyLine0123456789\n-----END KEY-----";
        var parts = TaskProcess.MaskedParts(key).ToList();
        Assert.Contains("MIIEvQIBADANBgkqhkiG9w0BAQEFAASC", parts);
        Assert.Equal("[redacted]", TaskProcess.Redact("MIIEvQIBADANBgkqhkiG9w0BAQEFAASC", parts));
        Assert.Equal("[redacted]", TaskProcess.Redact("AnotherLongKeyLine0123456789", parts));
        Assert.DoesNotContain("", parts);
        Assert.Equal(["a-single-line-token"], TaskProcess.MaskedParts("a-single-line-token"));
    }

    [Fact]
    public void AValueTooShortToMask_StopsTheRun_WithoutShowingIt()
    {
        using var f = new TaskFixture(withConnections: true);
        var id = AddConnection(f, "short1");
        var (values, problems) = TaskProcess.ReadBindings([new EnvBinding("GH_TOKEN", id.ToString(), "token", "Release")], f.Conns);
        Assert.Empty(values);
        var problem = Assert.Single(problems);
        Assert.Contains("shorter than 8 characters", problem);
        Assert.DoesNotContain("short1", problem);
    }

    [Fact]
    public void TheConnectionIdIsStoredInOneCanonicalForm()
    {
        using var f = new TaskFixture(withConnections: true);
        var id = AddConnection(f, "a-long-enough-token-value");
        var d = f.Def("release") with { EnvFromConnections = [new EnvBinding("GH_TOKEN", "0" + id, "token", "")] };
        var named = HostTaskRules.ValidateBindings(d, f.Conns);
        Assert.Equal(id.ToString(), named.EnvFromConnections[0].ConnectionId);
        Assert.Equal("Release", named.EnvFromConnections[0].ConnectionName);
    }

    [Fact]
    public async Task ACloudLinkPreviewWithANewBinding_IsRefused_AndNamesNoConnection()
    {
        using var f = new TaskFixture(withConnections: true);
        var id = AddConnection(f, "a-long-enough-token-value", "Secret Corp Deploy Key");
        var rpc = new HostTaskRpc(f.Svc, _ => ["main"]);
        JsonObject Preview(string connectionId) => new()
        {
            ["teamId"] = f.TeamId,
            ["def"] = new JsonObject
            {
                ["name"] = "probe", ["program"] = f.Win ? "powershell" : "sh",
                ["args"] = new JsonArray(f.Def().Args.Select(a => (JsonNode?)a).ToArray()), ["timeoutSeconds"] = 60,
                ["envFromConnections"] = new JsonArray(new JsonObject { ["name"] = "GH_TOKEN", ["connectionId"] = connectionId, ["field"] = "nope" }),
            },
        };
        using (Actor.Use(Actor.CloudViewer("remote")))
        {
            var e = await Assert.ThrowsAsync<RpcException>(() => rpc.DispatchAsync("tasks.preview", Preview(id.ToString()), Ct));
            Assert.Contains("only be defined or changed on the computer", e.Message);
            Assert.DoesNotContain("Secret Corp Deploy Key", e.Message);
        }
    }
}
