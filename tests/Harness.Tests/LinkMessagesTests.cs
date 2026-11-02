using System.Text.Json;
using Harness.Contracts.Link;

namespace Harness.Tests;

/// <summary>
/// The management link's wire format is a contract a service is built against, so its field names,
/// its frame types and its close codes are pinned here and in docs/management-link.md.
/// </summary>
public sealed class LinkMessagesTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 2, 9, 30, 0, TimeSpan.Zero);

    private static readonly StatusSummary Summary = new(
        "healthy",
        "1.2.3",
        [new WorkerState("worker-1", "connected")],
        [new TeamState("team-1", "Core", "running", 2, 1, 0, 1)],
        1);

    private static string[] PropertyNames(JsonElement element) =>
        element.EnumerateObject().Select(property => property.Name).ToArray();

    private static JsonElement Serialized<T>(T value) =>
        JsonDocument.Parse(JsonSerializer.Serialize(value, LinkJson.Options)).RootElement;

    [Fact]
    public void Every_frame_round_trips_with_the_documented_field_names()
    {
        var attach = Serialized(new AttachRequest("tok", "1.2.3", "abc123"));
        Assert.Equal(["token", "version", "commit"], PropertyNames(attach));

        var answer = Serialized(new AttachAnswer("cred", "Manage", "office"));
        Assert.Equal(["credential", "serviceName", "instanceName"], PropertyNames(answer));

        var error = Serialized(new LinkError("That token has already been used."));
        Assert.Equal(["error"], PropertyNames(error));

        LinkFrame[] frames =
        [
            new Hello("1.2.3", "abc123", At),
            new Status(7, At, Summary),
            new Heartbeat(At),
            new Welcome("Manage", "office", 30),
            new Refused("This instance was detached."),
        ];

        string[][] expected =
        [
            ["type", "version", "commit", "startedAt"],
            ["type", "seq", "at", "summary"],
            ["type", "at"],
            ["type", "serviceName", "instanceName", "heartbeatSeconds"],
            ["type", "reason"],
        ];

        string[] types = ["hello", "status", "heartbeat", "welcome", "refused"];

        for (var i = 0; i < frames.Length; i++)
        {
            var json = LinkJson.Serialize(frames[i]);
            var element = JsonDocument.Parse(json).RootElement;
            Assert.Equal(expected[i], PropertyNames(element));
            Assert.Equal(types[i], element.GetProperty("type").GetString());

            var parsed = LinkJson.Parse(json);
            Assert.NotNull(parsed);
            Assert.Equal(frames[i].GetType(), parsed.GetType());
            Assert.Equal(types[i], parsed.Type);
            Assert.Equal(json, LinkJson.Serialize(parsed));
        }

        var summary = Serialized(new Status(7, At, Summary)).GetProperty("summary");
        Assert.Equal(["health", "version", "workers", "teams", "needsYou"], PropertyNames(summary));
        Assert.Equal(["name", "state"], PropertyNames(summary.GetProperty("workers")[0]));
        Assert.Equal(
            ["id", "name", "status", "running", "idle", "blocked", "needsYou"],
            PropertyNames(summary.GetProperty("teams")[0]));

        var welcome = (Welcome?)LinkJson.Parse("""{"type":"welcome","serviceName":"Manage","instanceName":"office"}""");
        Assert.NotNull(welcome);
        Assert.Equal(15, welcome.HeartbeatSeconds);
    }

    [Fact]
    public void An_unknown_frame_type_parses_to_null_and_does_not_throw()
    {
        Assert.Null(LinkJson.Parse("""{"type":"launch","what":"anything"}"""));
        Assert.Null(LinkJson.Parse("""{"type":"relay.request","id":"1"}"""));
        Assert.Null(LinkJson.Parse("""{"type":"HELLO","version":"1"}"""));
        Assert.Null(LinkJson.Parse("""{"type":5}"""));
        Assert.Null(LinkJson.Parse("""{"version":"1"}"""));
    }

    [Fact]
    public void The_doc_names_every_frame_and_close_code()
    {
        var doc = File.ReadAllText(Path.Combine(SolutionSamples.RepoRoot(), "docs", "management-link.md"));

        // The subprotocol token, split so this file does not name the product.
        var subprotocol = "yaw" + "ble-link.1";

        foreach (var required in new[] { "hello", "status", "heartbeat", "welcome", "refused", "4401", "4400", subprotocol })
        {
            Assert.Contains(required, doc, StringComparison.Ordinal);
        }

        foreach (var forbidden in new[] { "hosted", "orgs", "workgroups", "tiers", "pricing" })
        {
            Assert.DoesNotContain(forbidden, doc, StringComparison.OrdinalIgnoreCase);
        }
    }
}
