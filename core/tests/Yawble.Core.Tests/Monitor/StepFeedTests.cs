using System.Text;
using Yawble.Core.Monitor;
using Yawble.Core.Tests.Bus;
using Yawble.Core.Transcripts;
using Xunit;

namespace Yawble.Core.Tests.Monitor;

/// <summary>The Monitor's step feed: what a transcript line is as a step, and reading transcripts on incrementally.</summary>
public class StepFeedTests
{
    private const string Clone = @"C:\Users\p\AppData\Roaming\yawble\teams\Alpha\members\Dev\repo";

    private static string Init() =>
        $$"""{"type":"system","subtype":"init","cwd":"{{Clone.Replace(@"\", @"\\")}}","tools":["Read"]}""";

    private static string Tool(string name, string input) =>
        "{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"tool_use\",\"id\":\"t1\",\"name\":\"" + name + "\",\"input\":" + input + "}]}}";

    private static IReadOnlyList<ParsedStep> Parse(string line, TranscriptContext? context = null) =>
        TranscriptSteps.Parse(line, context ?? new TranscriptContext("Alpha", "Dev") { Cwd = Clone }, out _);

    [Fact]
    public void Claude_tools_are_steps_by_kind_with_their_targets()
    {
        var file = (Clone + @"\src\app\main.ts").Replace(@"\", @"\\");
        Assert.Equal(new ParsedStep("edit", "Edit", "src/app/main.ts"), Parse(Tool("Edit", $$"""{"file_path":"{{file}}","old_string":"a","new_string":"b"}""")).Single());
        Assert.Equal(new ParsedStep("create", "Write", "src/app/main.ts"), Parse(Tool("Write", $$"""{"file_path":"{{file}}","content":"x"}""")).Single());
        Assert.Equal(new ParsedStep("read", "Read", "src/app/main.ts"), Parse(Tool("Read", $$"""{"file_path":"{{file}}"}""")).Single());
        Assert.Equal(new ParsedStep("run", "Bash", "npm test"), Parse(Tool("Bash", """{"command":"\n  npm test\nnpm run build"}""")).Single());
        Assert.Equal(new ParsedStep("search", "Grep", "Cli\\w+"), Parse(Tool("Grep", """{"pattern":"Cli\\w+"}""")).Single());
        Assert.Equal(new ParsedStep("web", "WebFetch", "https://example.com/a"), Parse(Tool("WebFetch", """{"url":"https://example.com/a"}""")).Single());
        Assert.Equal(new ParsedStep("bus", "tell", "TesterWren · card 16"), Parse(Tool("mcp__bus__tell", """{"member":"TesterWren","card":16,"instruction":"go"}""")).Single());
        Assert.Equal(new ParsedStep("bus", "progress", null), Parse(Tool("mcp__bus__progress", """{"text":"half way"}""")).Single());
        Assert.Equal(new ParsedStep("tool", "github/create_pr", null), Parse(Tool("mcp__github__create_pr", "{}")).Single());
        Assert.Equal(new ParsedStep("tool", "Mystery", null), Parse(Tool("Mystery", "{}")).Single());
    }

    [Fact]
    public void Thinking_and_text_are_steps_without_words_and_other_lines_are_none()
    {
        var line = """{"type":"assistant","message":{"content":[{"type":"thinking","thinking":"secret"},{"type":"text","text":"Done."}]}}""";
        Assert.Equal([new ParsedStep("think", "", null), new ParsedStep("say", "", null)], Parse(line));
        Assert.Empty(Parse("""{"type":"user","message":{"content":[{"type":"tool_result","content":"assistant"}]}}"""));
        Assert.Empty(Parse("\u001b[2J\u001b[H plain terminal output"));
        Assert.Empty(Parse("""{"type":"assistant", broken"""));
        Assert.Empty(Parse(""));
    }

    [Fact]
    public void Paths_read_relative_to_the_clone_else_the_team_folder_else_their_last_parts()
    {
        var context = new TranscriptContext("Alpha", "Dev") { Cwd = Clone };
        Assert.Equal("core/a.cs", TranscriptSteps.Relative(Clone + @"\core\a.cs", context));
        Assert.Equal("documents/plan.md", TranscriptSteps.Relative(@"C:\Users\p\AppData\Roaming\yawble\teams\Alpha\documents\plan.md", context));
        Assert.Equal("web/b.ts", TranscriptSteps.Relative(@"C:\Users\p\AppData\Roaming\yawble\teams\Alpha\members\Tester\repo\web\b.ts", context));
        Assert.Equal("lib/c.ts", TranscriptSteps.Relative("/somewhere/else/lib/c.ts", context));
        Assert.Equal("src/d.ts", TranscriptSteps.Relative("./src/d.ts", context));
    }

    [Fact]
    public void The_init_line_gives_the_clone_and_a_timestamp_wins()
    {
        var context = new TranscriptContext("Alpha", "Dev");
        Assert.Empty(TranscriptSteps.Parse(Init(), context, out _));
        Assert.Equal(Clone, context.Cwd);
        var line = """{"type":"assistant","timestamp":"2026-11-07T14:37:14.836Z","message":{"content":[{"type":"tool_use","name":"Glob","input":{"pattern":"**/*.cs"}}]}}""";
        var steps = TranscriptSteps.Parse(line, context, out var at);
        Assert.Equal("search", steps.Single().Kind);
        Assert.Equal(new DateTimeOffset(2026, 11, 7, 14, 37, 14, 836, TimeSpan.Zero), at);
    }

    [Fact]
    public void Codex_items_are_read_as_far_as_they_go()
    {
        var context = new TranscriptContext("Alpha", "Dev") { Cwd = Clone };
        Assert.Equal(new ParsedStep("run", "shell", "ls -la"), Parse("""{"type":"item.completed","item":{"id":"i1","type":"command_execution","command":"bash -lc 'ls -la'"}}""", context).Single());
        var change = Clone.Replace(@"\", @"\\") + @"\\x.ts";
        Assert.Equal([new ParsedStep("create", "apply_patch", "x.ts"), new ParsedStep("edit", "apply_patch", "y.ts")],
            Parse("{\"type\":\"item.completed\",\"item\":{\"type\":\"file_change\",\"changes\":[{\"path\":\"" + change + "\",\"kind\":\"add\"},{\"path\":\"y.ts\",\"kind\":\"update\"}]}}", context));
        Assert.Equal(new ParsedStep("bus", "handback", null), Parse("""{"type":"item.completed","item":{"type":"mcp_tool_call","server":"bus","tool":"handback"}}""", context).Single());
        Assert.Empty(Parse("""{"type":"item.started","item":{"type":"command_execution","command":"ls"}}""", context));
    }

    // ---------------------------------------------------------------- the incremental reading

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public async Task Reads_each_transcript_on_from_where_it_stopped_and_waits_for_a_whole_line()
    {
        using var db = new TempDb();
        var clock = new Clock(DateTimeOffset.UtcNow);
        var store = new TranscriptStore(Path.Combine(db.Dir, "data"), db.ConnectionString, clock);
        var feed = new StepFeed(db.ConnectionString, clock);
        await using var writer = store.Open(7, "Alpha", "Dev");
        await writer.WriteAsync(Init() + "\n" + Tool("Edit", $$"""{"file_path":"{{(Clone + @"\a.ts").Replace(@"\", @"\\")}}"}""") + "\n", TestContext.Current.CancellationToken);

        var (end, steps, pending) = feed.Read(0);
        var step = Assert.Single(steps);
        Assert.False(pending);
        Assert.Equal(("Alpha", "Dev", 7L, "edit", "a.ts"), (step.Team, step.Member, step.Run, step.Kind, step.Target));
        Assert.Equal(step.Id, end);

        // Nothing new: nothing again. Half a line: it waits for the rest.
        Assert.Empty(feed.Read(end).Steps);
        var bash = Tool("Bash", """{"command":"dotnet test"}""");
        await writer.WriteAsync(bash[..20], TestContext.Current.CancellationToken);
        Assert.Empty(feed.Read(end).Steps);
        await writer.WriteAsync(bash[20..] + "\n" + Tool("Read", """{"file_path":"b.ts"}""") + "\n", TestContext.Current.CancellationToken);
        var next = feed.Read(end);
        Assert.Equal(["run", "read"], next.Steps.Select(s => s.Kind));
        Assert.Equal("dotnet test", next.Steps[0].Target);
        Assert.All(next.Steps, s => Assert.True(s.Id > end));

        // A cursor from before returns them again; a window that ended before them returns none.
        Assert.Equal(3, feed.Read(0).Steps.Count);
        clock.Now = clock.Now.AddHours(2);
        Assert.Empty(feed.Read(0, sinceMinutes: 30).Steps);
    }

    [Fact]
    public async Task An_ended_runs_steps_are_spread_over_its_run_and_its_last_line_need_no_newline()
    {
        using var db = new TempDb();
        var start = new DateTimeOffset(2026, 11, 9, 10, 0, 0, TimeSpan.Zero);
        var clock = new Clock(start);
        var store = new TranscriptStore(Path.Combine(db.Dir, "data"), db.ConnectionString, clock);
        var writer = store.Open(3, "Alpha", "Dev");
        var lines = new StringBuilder(Init() + "\n");
        for (var i = 0; i < 10; i++) lines.Append(Tool("Grep", $$"""{"pattern":"p{{i}}"}""")).Append('\n');
        lines.Append(Tool("Glob", """{"pattern":"last"}"""));
        await writer.WriteAsync(lines.ToString(), TestContext.Current.CancellationToken);
        clock.Now = start.AddMinutes(10);
        await writer.DisposeAsync();

        clock.Now = start.AddMinutes(20);
        var steps = new StepFeed(db.ConnectionString, clock).Read(0, sinceMinutes: 60).Steps;
        Assert.Equal(11, steps.Count);
        Assert.Equal("last", steps[^1].Target);
        Assert.All(steps, s => Assert.InRange(s.At, start, start.AddMinutes(10)));
        Assert.True(steps[^1].At > steps[0].At.AddMinutes(5), "the steps are spread over the run, not stamped at the read");
        for (var i = 1; i < steps.Count; i++) Assert.True(steps[i].At >= steps[i - 1].At);
    }

    [Fact]
    public async Task A_big_backlog_is_read_over_several_calls_and_the_apps_own_runs_are_skipped()
    {
        using var db = new TempDb();
        var clock = new Clock(DateTimeOffset.UtcNow);
        var store = new TranscriptStore(Path.Combine(db.Dir, "data"), db.ConnectionString, clock);
        await using (var concierge = store.Open(1, "_concierge", "c-1"))
            await concierge.WriteAsync(Tool("Read", """{"file_path":"x"}""") + "\n", TestContext.Current.CancellationToken);
        await using var writer = store.Open(2, "Alpha", "Dev");
        // A filler user event of ~1 KB per line, so the run is far past one file's budget per call.
        var filler = "{\"type\":\"user\",\"message\":{\"content\":[{\"type\":\"tool_result\",\"content\":\"" + new string('x', 1000) + "\"}]}}\n";
        var chunk = new StringBuilder();
        for (var i = 0; i < 3000; i++) chunk.Append(filler).Append(Tool("Glob", $$"""{"pattern":"g{{i}}"}""")).Append('\n');
        await writer.WriteAsync(chunk.ToString(), TestContext.Current.CancellationToken);

        var feed = new StepFeed(db.ConnectionString, clock);
        var first = feed.Read(0);
        Assert.True(first.Pending);
        Assert.InRange(first.Steps.Count, 1, 2999);
        long end = first.End;
        var all = first.Steps.ToList();
        for (var calls = 0; calls < 10; calls++)
        {
            var page = feed.Read(end);
            all.AddRange(page.Steps);
            end = page.End;
            if (!page.Pending) break;
        }
        Assert.Equal(3000, all.Count);
        Assert.All(all, s => Assert.Equal("Alpha", s.Team));
        Assert.Equal(all.Count, all.Select(s => s.Id).Distinct().Count());
    }
}
