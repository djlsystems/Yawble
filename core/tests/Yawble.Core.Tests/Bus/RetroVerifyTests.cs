using System.Text;
using System.Text.Json.Nodes;
using Yawble.Core.Bus;
using Yawble.Core.Gateway;
using Yawble.Core.Skills;
using Yawble.Core.Teams;
using Xunit;

namespace Yawble.Core.Tests.Bus;

/// <summary>
/// The tester's checks of the retro work (dcbbb75 and f85133f) merged. The gaps the developers' own
/// tests leave: the two cards working together (a retro run reads its lessons in its prompt, reads the digest and writes
/// team_retros; what Team settings then says), an expired deliverable gets no retro, and the digest's masking on the bus path.
/// Fakes only: scripted runs, a manual clock, no agent CLI.
/// </summary>
public class RetroVerifyTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Token = "ghp_abcdefghijklmnopqrstuvwxyz0123456789";

    private static async Task<(string Text, bool IsError)> Call(RunContext run, string tool, JsonObject args)
    {
        var body = new JsonObject
        {
            ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "tools/call",
            ["params"] = new JsonObject { ["name"] = tool, ["arguments"] = args },
        }.ToJsonString();
        var reply = await new McpBusServer(run).HandleAsync(Encoding.UTF8.GetBytes(body), Ct);
        var result = JsonNode.Parse(reply.Json!)!["result"]!;
        return (result["content"]![0]!["text"]!.GetValue<string>(), result["isError"]!.GetValue<bool>());
    }

    private static Message[] Retros(BusStore store) => [.. store.AllMessages().Where(m => BusStore.RetroOf(m) is not null)];

    /// <summary>
    /// The whole retro loop with both cards: two pieces of work, Ada blocked once (her reason carries a token), the Manager
    /// completes; the retro run gets the lessons in its prompt, reads the digest (no token in it) and writes team_retros.
    /// The version and Team settings' line ("by the retro of deliverable &lt;id&gt;") must name the completed deliverable, the one the
    /// retro was for, not the retro item's own deliverable (spec section 4: "Lessons: n, last updated &lt;date&gt; by the retro of
    /// deliverable &lt;id&gt;"; shell TeamManageDialog prints TeamLessons.deliverable there).
    /// </summary>
    [Fact]
    public async Task ARetroRun_ReadsTheDigest_WritesTeamRetros_AndTheVersionNamesTheCompletedDeliverable()
    {
        using var db = new TempDb();
        var skills = new SkillCatalog(db.ConnectionString);
        string? retroPrompt = null, laterPrompt = null;
        (string Text, bool IsError)? digest = null, write = null;
        long retroDeliverable = 0;
        var handbacks = 0;
        var adaRuns = 0;
        var runner = new ScriptedRunner()
            .On("Alpha/Manager", async (run, ct) =>
            {
                if (run.Items.Any(m => BusStore.RetroOf(m) is not null))
                {
                    retroDeliverable = run.Deliverable;
                    retroPrompt = TeamPrompt.For(run); // the real launcher builds the prompt; reading the lessons records the version
                    digest = await Call(run, "deliverable_digest", []);
                    write = await Call(run, "team_retros_write", new JsonObject
                    {
                        ["text"] = "## Building and testing\n- Fetch the bundle before merging: the branch moved (2026-11-18, deliverable 1)",
                    });
                    await run.DeliverableCompleteAsync("kept 1 lesson", ct);
                    return RunResult.Ok;
                }
                if (run.Items.Any(m => Payload.Field(m, "text") == "later")) { laterPrompt = TeamPrompt.For(run); return RunResult.Ok; }
                if (run.Items.Any(m => m.Source == MessageTypes.PersonSource))
                {
                    await run.TellAsync("Ada", "part one", ct);
                    await run.TellAsync("Bo", "part two", ct);
                    return RunResult.Ok;
                }
                handbacks += run.Items.Count(m => m.Type == MessageTypes.Handback);
                if (handbacks >= 2) await run.DeliverableCompleteAsync("both parts done", ct);
                return RunResult.Ok;
            })
            .On("Alpha/Ada", async (run, ct) =>
            {
                if (adaRuns++ == 0) await run.BlockedAsync($"the push needs {Token} which I must not use", ct: ct);
                await run.HandbackAsync("part one done", ct);
                return RunResult.Ok;
            })
            .On("Alpha/Bo", async (run, ct) => { await run.HandbackAsync("part two done", ct); return RunResult.Ok; });
        var engine = new BusEngine(db.OpenStore(), runner,
            new BusOptions { SafetyPoll = TimeSpan.FromMilliseconds(200), Skills = skills, IdleOffer = false });
        await using (engine)
        {
            await engine.CreateTeamAsync("Alpha", ct: Ct);
            await engine.AddMemberAsync("Alpha", "Ada", ct: Ct);
            await engine.AddMemberAsync("Alpha", "Bo", ct: Ct);
            await engine.StartAsync(Ct);
            var root = await engine.PersonTellAsync("Alpha", "Manager", "ship both parts", ct: Ct);
            await Wait.UntilAsync(() => engine.IsQuiescentAsync(Ct), "quiet", TimeSpan.FromSeconds(60));

            using var store = db.OpenStore();
            var retro = Assert.Single(Retros(store));
            Assert.Equal(root.Seq, BusStore.RetroOf(retro));
            Assert.NotEqual(root.Seq, retroDeliverable);

            // No lessons yet: the retro run's prompt has none.
            Assert.DoesNotContain(TeamRetros.PromptHeading, retroPrompt!);
            // The digest is of the completed deliverable, and the blocked reason's token is masked.
            Assert.False(digest!.Value.IsError, digest.Value.Text);
            Assert.StartsWith($"Deliverable {root.Seq} of team Alpha: completed,", digest.Value.Text);
            Assert.Contains("- blocked: «", digest.Value.Text);
            Assert.DoesNotContain(Token, digest.Value.Text);
            Assert.False(write!.Value.IsError, write.Value.Text);

            // A later run of the team gets the lesson in its prompt.
            await engine.PersonTellAsync("Alpha", "Manager", "later", ct: Ct);
            await Wait.UntilAsync(() => engine.IsQuiescentAsync(Ct), "quiet again", TimeSpan.FromSeconds(60));
            Assert.Contains(TeamRetros.PromptHeading, laterPrompt!);
            Assert.Contains("- Fetch the bundle before merging", laterPrompt!);

            // Team settings' line: "by the retro of deliverable <id>" must be the deliverable the retro looked back on.
            var team = store.Teams().Single(t => t.Name == "Alpha").Id;
            var lessons = skills.Lessons(team);
            Assert.Equal(1, lessons.Count);
            Assert.Equal("Manager", lessons.Writer);
            Assert.True(lessons.Deliverable == root.Seq,
                $"team_retros version says deliverable {lessons.Deliverable} (the retro item's own deliverable is {retroDeliverable}); " +
                $"the completed deliverable the retro was for is {root.Seq}");

            // Settings > Skills' version history (shell versionWriter: "the retro of deliverable <id>") reads the
            // same version row, so it names the completed deliverable too, and the retro item's own deliverable appears nowhere.
            var version = Assert.Single(skills.Versions(team, TeamRetros.Name));
            Assert.Equal("Manager", version.Writer);
            Assert.Equal(root.Seq, version.Deliverable);
            Assert.NotEqual(retroDeliverable, version.Deliverable);
        }
    }

    /// <summary>Spec section 2: not for expired deliverables. Two pieces of work and a failed run (eventful), then it expires.</summary>
    [Fact]
    public async Task AnExpiredDeliverable_GetsNoRetro_HoweverEventful()
    {
        using var db = new TempDb();
        var clock = new DeliverableExpiryTests.ManualClock(new DateTimeOffset(2026, 11, 17, 9, 0, 0, TimeSpan.Zero));
        var runner = new ScriptedRunner()
            .On("Alpha/Manager", async (run, ct) =>
            {
                if (run.Items.Any(m => m.Source == MessageTypes.PersonSource))
                {
                    await run.TellAsync("Ada", "part one", ct);
                    await run.TellAsync("Bo", "part two", ct);
                }
                return RunResult.Ok;
            })
            .On("Alpha/Ada", (_, _) => Task.FromResult(RunResult.Fail("crashed")));
        var engine = new BusEngine(new BusStore(db.ConnectionString, clock), runner,
            new BusOptions { SafetyPoll = TimeSpan.FromMilliseconds(100), IdleOffer = false });
        await using (engine)
        {
            await engine.CreateTeamAsync("Alpha", ct: Ct);
            await engine.AddMemberAsync("Alpha", "Ada", ct: Ct);
            await engine.AddMemberAsync("Alpha", "Bo", ct: Ct);
            await engine.StartAsync(Ct);
            var root = await engine.PersonTellAsync("Alpha", "Manager", "ship both parts", ct: Ct);
            await Wait.UntilAsync(() => engine.IsQuiescentAsync(Ct), "quiet");

            clock.Advance(TimeSpan.FromDays(30));
            Assert.Equal(1, await engine.SweepExpiryAsync(clock.Now, Ct));
            await Wait.UntilAsync(() => engine.IsQuiescentAsync(Ct), "quiet after expiry");

            using var store = db.OpenStore();
            Assert.Contains(store.Deliverable(root.Seq), m => m.Type == MessageTypes.DeliverableExpired);
            Assert.False(store.DeliverableDeclared(root.Seq));
            Assert.Empty(Retros(store));
        }
    }
}
