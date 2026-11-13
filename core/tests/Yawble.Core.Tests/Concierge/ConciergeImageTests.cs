using System.Text;
using System.Text.Json.Nodes;
using Yawble.Core.Bus;
using Yawble.Core.Concierge;
using Yawble.Core.Teams;
using Yawble.Core.Tests.Bus;
using Xunit;

namespace Yawble.Core.Tests.Concierge;

/// <summary>The Concierge forwards an image the person attached in its chat to a team, and nothing else.</summary>
public class ConciergeImageTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3];

    [Fact]
    public async Task TellForwardsAnImageFromTheChat_ButNotAFileFromElsewhere()
    {
        using var db = new TempDb();
        var appRoot = Directory.CreateTempSubdirectory("yawble-cimg-").FullName;
        try
        {
            var repos = new TeamRepos(appRoot, db.ConnectionString);
            await using var bus = new BusEngine(db.OpenStore(), new UnavailableRunner(), new BusOptions { Repos = repos, IdleOffer = false });
            await bus.StartAsync(Ct);
            await bus.CreateTeamAsync("alpha", "Manager", "claude", null, Ct);
            var store = new ConciergeStore(db.ConnectionString);
            store.Create("c1", "a bug", "claude", null);

            var attachments = Path.Combine(repos.MemberRoot(ConciergeService.Team, ConciergeService.MemberOf("c1")), "home", global::Yawble.Core.Ui.Attachments.Folder);
            Directory.CreateDirectory(attachments);
            var shot = Path.Combine(attachments, "20261109-bug.png");
            File.WriteAllBytes(shot, Png);
            var elsewhere = Path.Combine(appRoot, "secret.png");
            File.WriteAllBytes(elsewhere, Png);

            JsonObject? sent = null;
            var tools = new ConciergeTools(bus, store, (method, p, _) =>
            {
                sent = p;
                return Task.FromResult<JsonNode?>(new JsonObject { ["seq"] = 1L, ["workflow"] = 1L });
            }, () => null, "c1");

            var ok = await Call(tools, new JsonObject { ["team"] = "alpha", ["instruction"] = "Fix the layout bug in the screenshot", ["images"] = shot });
            Assert.StartsWith("ok:", ok);
            var image = Assert.Single(sent!["images"]!.AsArray());
            Assert.Equal("20261109-bug.png", image!["name"]!.GetValue<string>());
            Assert.Equal(Png, Convert.FromBase64String(image["base64"]!.GetValue<string>()));

            sent = null;
            var refused = await Call(tools, new JsonObject { ["team"] = "alpha", ["instruction"] = "Look", ["images"] = elsewhere });
            Assert.Contains("is not an image the person attached in this chat", refused);
            Assert.Null(sent); // nothing was told
        }
        finally { Directory.Delete(appRoot, recursive: true); }
    }

    private static async Task<string> Call(ConciergeTools tools, JsonObject args)
    {
        var body = new JsonObject
        {
            ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "tools/call", ["params"] = new JsonObject { ["name"] = "tell", ["arguments"] = args },
        };
        var reply = await tools.HandleAsync(Encoding.UTF8.GetBytes(body.ToJsonString()), Ct);
        var node = JsonNode.Parse(reply.Json!)!;
        return node["result"]!["content"]![0]!["text"]!.GetValue<string>();
    }
}
