using System.Collections.Concurrent;
using System.Text;
using System.Text.Json.Nodes;
using Yawble.Core.Bus;
using Yawble.Core.Gateway;
using Yawble.Core.Link;
using Yawble.Core.Provenance;
using Yawble.Core.Skills;
using Yawble.Core.Teams;
using Yawble.Core.Tests.Bus;
using Xunit;

namespace Yawble.Core.Tests.Teams;

/// <summary>
/// B100 sections 4-5 (card 468): the team's lessons. The Manager alone writes its own team's team_retros with
/// team_retros_write (size cap, every write a version, a person's edit never overwritten unread); every run of that team,
/// and no other, gets the lessons in its prompt. Fakes only: scripted runs, no agent CLI.
/// </summary>
public class TeamRetrosTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<(string Text, bool Error)> Call(RunContext run, string tool, object args)
    {
        var body = new JsonObject
        {
            ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "tools/call",
            ["params"] = new JsonObject { ["name"] = tool, ["arguments"] = System.Text.Json.JsonSerializer.SerializeToNode(args) },
        };
        var reply = await new McpBusServer(run).HandleAsync(Encoding.UTF8.GetBytes(body.ToJsonString()), Ct);
        var result = JsonNode.Parse(reply.Json!)!["result"]!;
        return (result["content"]![0]!["text"]!.GetValue<string>(), result["isError"]!.GetValue<bool>());
    }

    /// <summary>
    /// Teams Alpha (Manager, Ada) and Beta (Manager). Each run's script gets its context; the person's tells start them.
    /// Waits until every script given has run once.
    /// </summary>
    private sealed class Kit : IAsyncDisposable
    {
        public readonly TempDb Db = new();
        public readonly SkillCatalog Skills;
        public readonly ScriptedRunner Runner = new();
        public BusEngine Engine = null!;
        public readonly ConcurrentDictionary<string, string> Prompts = new(StringComparer.OrdinalIgnoreCase);

        public Kit() => Skills = new SkillCatalog(Db.ConnectionString);

        public long Team(string name)
        {
            using var store = Db.OpenStore();
            return store.Teams().Single(t => t.Name == name).Id;
        }

        public async Task StartAsync()
        {
            Engine = new BusEngine(Db.OpenStore(), Runner, new BusOptions { SafetyPoll = TimeSpan.FromMilliseconds(200), Skills = Skills, IdleOffer = false });
            await Engine.CreateTeamAsync("Alpha", ct: Ct);
            await Engine.AddMemberAsync("Alpha", "Ada", ct: Ct);
            await Engine.CreateTeamAsync("Beta", ct: Ct);
            await Engine.StartAsync(Ct);
        }

        /// <summary>One run of <paramref name="member"/> (team/name) doing <paramref name="script"/>; returns when it ran.</summary>
        public async Task RunAsync(string team, string member, Func<RunContext, Task> script)
        {
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Runner.On($"{team}/{member}", async (run, _) =>
            {
                try
                {
                    Prompts[$"{team}/{member}"] = TeamPrompt.For(run);
                    await script(run);
                    done.TrySetResult();
                }
                catch (Exception e) { done.TrySetException(e); }
                return RunResult.Ok;
            });
            await Engine.PersonTellAsync(team, member, "work", ct: Ct);
            await done.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
            // The script ran once; any later run (a member's ending notice waking the Manager) does nothing.
            Runner.On($"{team}/{member}", (_, _) => Task.FromResult(RunResult.Ok));
            await Wait.UntilAsync(() => Engine.IsQuiescentAsync(Ct), "quiescent", TimeSpan.FromSeconds(30));
        }

        public async ValueTask DisposeAsync()
        {
            if (Engine is not null) await Engine.DisposeAsync();
            Db.Dispose();
        }
    }

    private const string Lessons = "## Building and testing\n- Run `npm run build` in shell before any e2e spec: specs load shell/out (2026-11-18, workflow 12)";

    [Fact]
    public async Task OnlyTheManagerWrites_AndOnlyItsOwnTeamsTeamRetros()
    {
        await using var kit = new Kit();
        await kit.StartAsync();
        var alpha = kit.Team("Alpha");
        var beta = kit.Team("Beta");
        kit.Skills.Save(TeamRetros.Name, "Beta's lessons.", ["any"], "- Beta's own lesson", beta);
        kit.Skills.Save("release-notes", "How to write release notes.", ["any"], "Write them short.", alpha);

        (string Text, bool Error) member = default;
        await kit.RunAsync("Alpha", "Ada", async run => member = await Call(run, "team_retros_write", new { text = "- a member's lesson" }));
        Assert.True(member.Error);
        Assert.Contains("only the Manager", member.Text);
        Assert.Null(kit.Skills.TeamRetrosSkill(alpha));

        (string Text, bool Error) manager = default;
        long workflow = 0;
        await kit.RunAsync("Alpha", "Manager", async run => { workflow = run.Workflow; manager = await Call(run, "team_retros_write", new { text = Lessons }); });
        Assert.False(manager.Error, manager.Text);
        Assert.Contains("team_retros version 1 saved", manager.Text);

        var written = kit.Skills.TeamRetrosSkill(alpha)!;
        Assert.Equal(Lessons, written.Body);
        Assert.Equal(["any"], written.Roles); // created on the first write, for every role
        Assert.Equal("- Beta's own lesson", kit.Skills.TeamRetrosSkill(beta)!.Body); // the other team's is untouched
        Assert.Equal("Write them short.", kit.Skills.All().Single(s => s.Name == "release-notes").Body); // nothing else in the skills table
        Assert.DoesNotContain(kit.Skills.All(), s => s.Name == TeamRetros.Name && s.TeamId is null);
        var v = Assert.Single(kit.Skills.Versions(alpha, TeamRetros.Name));
        Assert.Equal(("Manager", (long?)workflow), (v.Writer, v.Workflow));
        Assert.Single(kit.Skills.Versions(beta, TeamRetros.Name)); // Beta's: only the person's save
    }

    [Fact]
    public async Task AWriteAboveTheCapIsRefusedWithItsSize()
    {
        await using var kit = new Kit();
        await kit.StartAsync();
        var tooLong = "- " + new string('x', TeamRetros.MaxChars - 1); // 4001 characters
        var justFits = "- " + new string('y', TeamRetros.MaxChars - 2);
        (string Text, bool Error) refused = default, fits = default;
        await kit.RunAsync("Alpha", "Manager", async run =>
        {
            refused = await Call(run, "team_retros_write", new { text = tooLong });
            fits = await Call(run, "team_retros_write", new { text = justFits });
        });
        Assert.True(refused.Error);
        Assert.Contains("too_long", refused.Text);
        Assert.Contains($"at most {TeamRetros.MaxChars} characters; this text is {TeamRetros.MaxChars + 1}", refused.Text);
        Assert.False(fits.Error, fits.Text);
        Assert.Equal(justFits, kit.Skills.TeamRetrosSkill(kit.Team("Alpha"))!.Body);
        Assert.Single(kit.Skills.Versions(kit.Team("Alpha"), TeamRetros.Name)); // the refused write left no version
    }

    [Fact]
    public async Task EveryWriteIsKeptAsAVersion_AndThePersonRestoresOne()
    {
        await using var kit = new Kit();
        await kit.StartAsync();
        var alpha = kit.Team("Alpha");
        await kit.RunAsync("Alpha", "Manager", async run =>
        {
            Assert.False((await Call(run, "team_retros_write", new { text = "- first lesson" })).Error);
            var second = await Call(run, "team_retros_write", new { text = "- first lesson\n- second lesson" }); // its own write counts as read
            Assert.False(second.Error, second.Text);
        });
        var versions = kit.Skills.Versions(alpha, TeamRetros.Name);
        Assert.Equal([2, 1], versions.Select(v => v.Version));
        Assert.All(versions, v => Assert.Equal("Manager", v.Writer));
        Assert.Equal("- first lesson", versions[1].Body);

        var restored = kit.Skills.Restore(versions[1].Id);
        Assert.Equal("- first lesson", restored.Body);
        var now = kit.Skills.Versions(alpha, TeamRetros.Name)[0];
        Assert.Equal((3, TeamRetros.PersonWriter, (long?)1, (long?)null), (now.Version, now.Writer, now.RestoredFrom, now.Workflow));

        var lessons = kit.Skills.Lessons(alpha);
        Assert.Equal((1, 3, TeamRetros.PersonWriter), (lessons.Count, lessons.Version, lessons.Writer));
        Assert.Equal(restored.Id, lessons.SkillId);
    }

    [Fact]
    public async Task APersonsEditIsNeverOverwrittenUnread_TheNextWriteStartsFromIt()
    {
        await using var kit = new Kit();
        await kit.StartAsync();
        var alpha = kit.Team("Alpha");
        await kit.RunAsync("Alpha", "Manager", async run => Assert.False((await Call(run, "team_retros_write", new { text = "- the Manager's lesson" })).Error));

        (string Text, bool Error) stale = default, again = default, merged = default;
        string? loaded = null;
        await kit.RunAsync("Alpha", "Manager", async run =>
        {
            // The run's prompt carried version 1; the person edits it in Settings > Skills meanwhile.
            var current = kit.Skills.TeamRetrosSkill(alpha)!;
            kit.Skills.Save(TeamRetros.Name, current.Description, current.Roles, "- the Manager's lesson\n- the person's own lesson", alpha);
            stale = await Call(run, "team_retros_write", new { text = "- the Manager's lesson\n- a new lesson" });
            again = await Call(run, "team_retros_write", new { text = "- the Manager's lesson\n- a new lesson" }); // still unread
            loaded = (await Call(run, "skills_get", new { name = TeamRetros.Name })).Text;
            merged = await Call(run, "team_retros_write", new { text = loaded + "\n- a new lesson" });
        });
        Assert.True(stale.Error);
        Assert.Contains("changed", stale.Text);
        Assert.Contains("the person (in Settings > Skills)", stale.Text);
        Assert.True(again.Error);
        Assert.Contains("the person's own lesson", loaded);
        Assert.False(merged.Error, merged.Text);

        Assert.Equal("- the Manager's lesson\n- the person's own lesson\n- a new lesson", kit.Skills.TeamRetrosSkill(alpha)!.Body);
        Assert.Equal(["Manager", TeamRetros.PersonWriter, "Manager"], kit.Skills.Versions(alpha, TeamRetros.Name).Select(v => v.Writer));
    }

    [Fact]
    public async Task ARunThatNeverReadTheLessonsCannotReplaceThem()
    {
        await using var kit = new Kit();
        await kit.StartAsync();
        var alpha = kit.Team("Alpha");
        kit.Skills.Save(TeamRetros.Name, "Our lessons.", ["member"], "- the person's lesson", alpha);
        (string Text, bool Error) unread = default;
        await kit.RunAsync("Alpha", "Manager", async run =>
        {
            run.RetrosSeenVersion = null; // as if no prompt had been rendered
            unread = await Call(run, "team_retros_write", new { text = "- something else" });
        });
        Assert.True(unread.Error);
        Assert.Contains("have not read it", unread.Text);
        var skill = kit.Skills.TeamRetrosSkill(alpha)!;
        Assert.Equal("- the person's lesson", skill.Body);
        Assert.Equal("Our lessons.", skill.Description);
        Assert.Equal(["member"], skill.Roles);
    }

    [Fact]
    public async Task TheLessonsAreInEveryRunPromptOfThatTeam_AndNoOther_AndAbsentWhenNone()
    {
        await using var kit = new Kit();
        await kit.StartAsync();
        var alpha = kit.Team("Alpha");

        await kit.RunAsync("Alpha", "Manager", _ => Task.CompletedTask);
        Assert.DoesNotContain(TeamRetros.PromptHeading, kit.Prompts["Alpha/Manager"]); // none yet

        kit.Skills.Save(TeamRetros.Name, TeamRetros.Description, ["any"], Lessons, alpha);
        await kit.RunAsync("Alpha", "Manager", _ => Task.CompletedTask);
        await kit.RunAsync("Alpha", "Ada", _ => Task.CompletedTask);
        await kit.RunAsync("Beta", "Manager", _ => Task.CompletedTask);

        foreach (var who in new[] { "Alpha/Manager", "Alpha/Ada" })
        {
            var prompt = kit.Prompts[who];
            Assert.Contains(TeamRetros.PromptHeading, prompt);
            Assert.Contains(Lessons, prompt);
            Assert.DoesNotContain($"{TeamRetros.Name} (", prompt); // in full, not offered by name
            Assert.True(prompt.IndexOf(Lessons, StringComparison.Ordinal) < prompt.IndexOf("Your instruction:", StringComparison.Ordinal));
        }
        Assert.DoesNotContain(TeamRetros.PromptHeading, kit.Prompts["Beta/Manager"]);
        Assert.DoesNotContain("npm run build", kit.Prompts["Beta/Manager"]);

        // An emptied text (the person cleared it) is no lessons.
        Assert.Empty(TeamPrompt.LessonLines("  \n "));
    }

    [Fact]
    public void TheToolIsListedForTheManager_AndTheNewCallsAreClassified()
    {
        Assert.Contains("team_retros_write", McpBusServer.ToolNames);
        var tool = McpBusServer.ToolList().Select(n => n!.AsObject()).Single(o => o["name"]!.GetValue<string>() == "team_retros_write");
        var description = tool["description"]!.GetValue<string>();
        Assert.StartsWith("The Manager:", description);
        Assert.Contains("- <lesson> (<date>, workflow <id>)", description);
        Assert.Contains($"{TeamRetros.MaxChars} characters", description);
        Assert.Equal(["text"], tool["inputSchema"]!["required"]!.AsArray().Select(n => n!.GetValue<string>()));

        Assert.True(AuditLog.IsReadOnly("skills.versions"));
        Assert.True(AuditLog.IsReadOnly("teams.lessons"));
        Assert.False(AuditLog.IsReadOnly("skills.restore"));
        Assert.Equal(RemoteAccess.Kind.Reads, RemoteAccess.Kinds["skills.versions"]);
        Assert.Equal(RemoteAccess.Kind.Reads, RemoteAccess.Kinds["teams.lessons"]);
        Assert.Equal(RemoteAccess.Kind.Changes, RemoteAccess.Kinds["skills.restore"]);
    }

    [Fact]
    public void TheLessonsNameIsAcceptedAndLessonsAreListLines()
    {
        Assert.Equal(TeamRetros.Name, SkillCatalog.CheckName(" team_retros "));
        Assert.Throws<ArgumentException>(() => SkillCatalog.CheckName("other_name"));
        Assert.Equal(3, TeamRetros.CountLessons("## Git\n- one\n- two\n\n## Build\n* three\nnot a lesson"));
        Assert.Equal(0, TeamRetros.CountLessons(null));
    }
}
