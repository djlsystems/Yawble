using System.Text.Json;
using Harness.Contracts;
using Harness.Host;
using Harness.Identity;
using Harness.Kanban;
using Harness.Messaging;
using Microsoft.Data.Sqlite;

namespace Harness.Tests;

/// <summary>
/// The folder-change trigger, driven by hand: a real <see cref="Harness.Containers.ContainerHost"/>
/// and message log, the real SQLite trigger store, real files on disk, and a clock the test passes in.
/// The poll is <see cref="FolderWatch.PollAsync"/>, exactly what <see cref="TriggerSweep"/> calls for a
/// due folder row; delivery is the ordinary event-trigger arm of the pump.
/// </summary>
public sealed class FolderChangeTriggerTests : IAsyncLifetime
{
    private const string Team = "Alpha";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"harness-folder-{Guid.NewGuid():N}");
    private readonly ContainerId _worker = new(Team, "Worker");
    private readonly DateTimeOffset _t0 = new(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);

    private ContainerTestBed _bed = null!;
    private SqliteTriggerStore _triggers = null!;
    private FolderWatch _watch = null!;

    private string Documents => Path.Combine(_directory, "documents");

    private string Inbox => Path.Combine(Documents, "inbox");

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(Inbox);

        var database = Path.Combine(_directory, "harness.db");
        await new SchemaMigrator(database).ApplyAsync(AuthSchema.Steps, ct: Ct);

        await using (var connection = new SqliteConnection($"Data Source={database};Pooling=False"))
        {
            connection.Open();
            await using var insert = connection.CreateCommand();
            insert.CommandText = "INSERT INTO teams (id, created_utc) VALUES ('Alpha', '2026-09-23T00:00:00Z')";
            await insert.ExecuteNonQueryAsync(Ct);
        }

        _triggers = new SqliteTriggerStore(database);
        _bed = new ContainerTestBed(triggers: _triggers);

        var container = await _bed.AddAsync(_worker);

        // What EffectiveSubscriptions writes for a container holding an enabled folder trigger: its
        // own set plus `file.changed`, which is NOT in its base set - so the trigger governs it.
        await _bed.Store.SetAsync(_worker, [.. container.Snapshot().Subscribes, MessageTypes.FileChanged], Ct);

        _watch = new FolderWatch(
            _triggers, _bed.Store, _bed.Host, pending: null,
            documentsRootFor: _ => Documents,
            roots: [],
            dataRoot: Path.Combine(_directory, "data"));
    }

    public async ValueTask DisposeAsync()
    {
        await _bed.DisposeAsync();
        SqliteConnection.ClearAllPools();

        try { Directory.Delete(_directory, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task Three_files_dropped_within_ten_seconds_fire_once_with_count_three_after_the_quiet_period()
    {
        var id = await CreateAsync();

        await PollAsync(id, _t0); // baseline: an empty inbox is not news

        Drop("a.txt", "one");
        await PollAsync(id, _t0.AddSeconds(15)); // the change starts the quiet period

        Drop("b.txt", "two");
        Drop("c.txt", "three");
        await PollAsync(id, _t0.AddSeconds(20)); // a new shape restarts it

        await PollAsync(id, _t0.AddSeconds(35)); // held 15 of 30 seconds: not yet
        Assert.Empty(await _bed.OfTypeAsync(MessageTypes.FileChanged));

        await PollAsync(id, _t0.AddSeconds(50)); // held 30: fires

        var published = Assert.Single(await _bed.OfTypeAsync(MessageTypes.FileChanged));
        using (var payload = JsonDocument.Parse(published.Payload))
        {
            Assert.Equal(3, payload.RootElement.GetProperty(PayloadFields.Count).GetInt32());
            Assert.Equal(
                ["inbox/a.txt", "inbox/b.txt", "inbox/c.txt"],
                payload.RootElement.GetProperty(PayloadFields.Changed).EnumerateArray()
                    .Select(e => e.GetString()!).Order(StringComparer.Ordinal).ToArray());
            Assert.Equal(Team, payload.RootElement.GetProperty(PayloadFields.Team).GetString());
            Assert.Equal("inbox", payload.RootElement.GetProperty(PayloadFields.Path).GetString());
        }

        Assert.Equal($"trigger:{id}", published.Source);

        Assert.True(await _bed.PumpUntilAsync(() => _bed.Agent.RunsFor(_worker) == 1));
        await _bed.SettleAsync();

        var instruction = Assert.Single(await _bed.OfTypeAsync(MessageTypes.InstructionFor(_worker)));
        Assert.Equal($"trigger:{id}", instruction.Source);
        Assert.Equal(published.Seq, instruction.CausationSeq);
        Assert.Equal(1, _bed.Agent.RunsFor(_worker));

        var everything = await _bed.Store.ReadAfterAsync(
            0,
            [MessageTypes.FileChanged, MessageTypes.InstructionFor(_worker), MessageTypes.Started, MessageTypes.Completed],
            int.MaxValue,
            Ct);
        Assert.Single(KanbanProjector.Project(everything).Cards);

        // Nothing more happens to the folder: nothing more is published.
        await PollAsync(id, _t0.AddSeconds(200));
        Assert.Single(await _bed.OfTypeAsync(MessageTypes.FileChanged));

        var row = (await _triggers.FindAsync(id, Ct))!;
        Assert.Equal(_t0.AddSeconds(50), row.LastChangeAt);
        Assert.Equal(3, row.LastPollEntries);
        Assert.NotNull(row.LastPollMs);
        Assert.Equal(_t0.AddSeconds(200), row.LastPollAt);
        Assert.Null(row.LastPollError);
    }

    [Fact]
    public async Task A_touch_with_no_content_change_does_not_fire()
    {
        var file = Drop("report.txt", "unchanged");
        var id = await CreateAsync();

        await PollAsync(id, _t0);
        var baseline = (await _triggers.FindAsync(id, Ct))!.LastFingerprint;

        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddHours(1));

        await PollAsync(id, _t0.AddSeconds(15));
        await PollAsync(id, _t0.AddSeconds(50));
        await PollAsync(id, _t0.AddSeconds(120));

        Assert.Empty(await _bed.OfTypeAsync(MessageTypes.FileChanged));

        // The touch moved the fingerprint (newest mtime) and was taken as the new baseline.
        Assert.NotEqual(baseline, (await _triggers.FindAsync(id, Ct))!.LastFingerprint);
    }

    [Fact]
    public async Task The_woken_member_writing_into_the_same_folder_does_not_fire_again_within_the_minimum_interval()
    {
        var id = await CreateAsync(quietSeconds: 0);

        // The member writes into the watched folder while it runs, as a woken agent would.
        _bed.Agent.Behaviour = _ =>
        {
            Drop("answer.txt", "the member's reply");
            return Task.FromResult(new AgentResult(0, "done"));
        };

        await PollAsync(id, _t0);
        Drop("question.txt", "from a person");
        await PollAsync(id, _t0.AddSeconds(15));

        Assert.Single(await _bed.OfTypeAsync(MessageTypes.FileChanged));
        Assert.True(await _bed.PumpUntilAsync(() => _bed.Agent.RunsFor(_worker) == 1));
        await _bed.SettleAsync();
        Assert.True(File.Exists(Path.Combine(Inbox, "answer.txt")));

        await PollAsync(id, _t0.AddSeconds(30));
        await PollAsync(id, _t0.AddSeconds(45));
        await PollAsync(id, _t0.AddSeconds(200));
        await _bed.SettleAsync();

        Assert.Single(await _bed.OfTypeAsync(MessageTypes.FileChanged));
        Assert.Equal(1, _bed.Agent.RunsFor(_worker));
    }

    [Fact]
    public async Task The_minimum_interval_holds_a_second_change_back_and_then_lets_it_fire()
    {
        var id = await CreateAsync(quietSeconds: 0, minIntervalSeconds: 60);

        await PollAsync(id, _t0);
        Drop("one.txt", "1");
        await PollAsync(id, _t0.AddSeconds(15));
        Assert.Single(await _bed.OfTypeAsync(MessageTypes.FileChanged));

        // No member ran (nothing was pumped), so this change is a person's, not a loop.
        Drop("two.txt", "2");
        await PollAsync(id, _t0.AddSeconds(30));
        await PollAsync(id, _t0.AddSeconds(60));
        Assert.Single(await _bed.OfTypeAsync(MessageTypes.FileChanged));

        var held = (await _triggers.FindAsync(id, Ct))!;
        Assert.Equal(_t0.AddSeconds(75), held.NextDueAt);

        await PollAsync(id, _t0.AddSeconds(75));
        var fired = await _bed.OfTypeAsync(MessageTypes.FileChanged);
        Assert.Equal(2, fired.Count);
        Assert.Contains("inbox/two.txt", fired[1].Payload);
        Assert.DoesNotContain("inbox/one.txt", fired[1].Payload);
    }

    [Fact]
    public async Task An_announced_upload_fires_at_once_and_the_next_poll_does_not_fire_it_again()
    {
        var id = await CreateAsync(quietSeconds: 0);
        await PollAsync(id, _t0);

        Drop("upload.pdf", "bytes");
        await _watch.AnnounceAsync(Team, "inbox", ["inbox/upload.pdf"], "person-1", Ct);

        var announced = Assert.Single(await _bed.OfTypeAsync(MessageTypes.FileChanged));
        Assert.Equal("person-1", announced.Source);

        // Polled BEFORE anything is pumped, so no member has run and the loop guard cannot be what
        // keeps the poll quiet: only the announcement can.
        await PollAsync(id, _t0.AddSeconds(200));
        await PollAsync(id, _t0.AddSeconds(400));

        Assert.Single(await _bed.OfTypeAsync(MessageTypes.FileChanged));

        // And the pump delivers the announced row to the folder trigger that covers the file.
        Assert.True(await _bed.PumpUntilAsync(() => _bed.Agent.RunsFor(_worker) == 1));
        await _bed.SettleAsync();
        Assert.Single(await _bed.OfTypeAsync(MessageTypes.InstructionFor(_worker)));
    }

    [Fact]
    public async Task A_poll_published_row_wakes_only_the_trigger_that_polled()
    {
        // A second member watching the SAME folder. Without the rule both would wake on either
        // poll's row, and every change would wake each of them twice.
        var neighbour = new ContainerId(Team, "Neighbour");
        var added = await _bed.AddAsync(neighbour);
        await _bed.Store.SetAsync(neighbour, [.. added.Snapshot().Subscribes, MessageTypes.FileChanged], Ct);

        var mine = await CreateAsync(quietSeconds: 0);
        await CreateAsync(quietSeconds: 0, container: neighbour.Name);

        await PollAsync(mine, _t0);
        Drop("x.txt", "x");
        await PollAsync(mine, _t0.AddSeconds(15));

        Assert.True(await _bed.PumpUntilAsync(() => _bed.Agent.RunsFor(_worker) == 1));
        await _bed.SettleAsync();

        var instruction = Assert.Single(await _bed.OfTypeAsync(MessageTypes.InstructionFor(_worker)));
        Assert.Equal($"trigger:{mine}", instruction.Source);
        Assert.Empty(await _bed.OfTypeAsync(MessageTypes.InstructionFor(neighbour)));
        Assert.Equal(0, _bed.Agent.RunsFor(neighbour));
    }

    [Fact]
    public async Task Ignored_names_and_the_glob_decide_what_a_watch_counts()
    {
        Drop("keep.pdf", "p");
        Drop("skip.txt", "t");
        Drop("draft.pdf~", "e");
        Drop(".#lock.pdf", "e");
        Drop("x.tmp", "t");
        Drop(".x.swp", "s");
        Directory.CreateDirectory(Path.Combine(Inbox, "node_modules"));
        File.WriteAllText(Path.Combine(Inbox, "node_modules", "dep.pdf"), "d");
        Directory.CreateDirectory(Path.Combine(Inbox, ".git"));
        File.WriteAllText(Path.Combine(Inbox, ".git", "HEAD.pdf"), "g");
        Directory.CreateDirectory(Path.Combine(Inbox, "deep"));
        File.WriteAllText(Path.Combine(Inbox, "deep", "nested.pdf"), "n");

        var target = _watch.Resolve(Team, "documents", "inbox", out var refusal)!;
        Assert.Null(refusal);

        var listing = FolderWatch.List(target, "*.pdf");
        Assert.Equal(
            ["inbox/deep/nested.pdf", "inbox/keep.pdf"],
            listing.Entries.Select(e => e.Path).ToArray());

        Assert.Equal(
            ["inbox/deep/nested.pdf"],
            FolderWatch.List(target, "deep/**/*.pdf").Entries.Select(e => e.Path).ToArray());
    }

    [Fact]
    public void A_path_outside_the_allowed_roots_is_refused_with_a_sentence()
    {
        Assert.Null(_watch.Resolve(Team, "documents", "../../etc", out var escape));
        Assert.Equal("That path leaves the team's documents folder.", escape);

        Assert.Null(_watch.Resolve(Team, "documents", "/etc", out var rooted));
        Assert.Equal("That path leaves the team's documents folder.", rooted);

        Assert.Null(_watch.Resolve(Team, "root:Nowhere", "", out var unknown));
        Assert.Equal("No file-browser root is named \"Nowhere\".", unknown);

        Assert.Null(_watch.Resolve(Team, "/etc", "", out var noRoot));
        Assert.Equal("A folder trigger needs a root: \"documents\" or \"root:<name>\".", noRoot);

        Assert.Null(_watch.Resolve(Team, "documents", "missing", out var missing));
        Assert.Equal("There is no folder at \"missing\" in the team's documents.", missing);
    }

    [Fact]
    public void A_root_without_allowWatch_the_data_root_and_a_symlink_are_refused()
    {
        var share = Path.Combine(_directory, "share");
        Directory.CreateDirectory(Path.Combine(share, "in"));
        var data = Path.Combine(_directory, "data");
        Directory.CreateDirectory(data);

        Directory.CreateSymbolicLink(Path.Combine(Inbox, "escape"), "/tmp");

        var watch = new FolderWatch(
            _triggers, _bed.Store, _bed.Host, pending: null,
            documentsRootFor: _ => Documents,
            roots:
            [
                new FileBrowserRoot("Harness (data)", data),
                new FileBrowserRoot("Locked", share),
                new FileBrowserRoot("Share", share, AllowWatch: true),
            ],
            dataRoot: data);

        Assert.Null(watch.Resolve(Team, "root:Locked", "in", out var locked));
        Assert.Equal(
            "The file-browser root \"Locked\" does not allow watching. Set \"allowWatch\": true on it under FileBrowser:Roots.",
            locked);

        Assert.Null(watch.Resolve(Team, "root:Harness (data)", "", out var dataRoot));
        Assert.Equal("The platform's own data folder cannot be watched.", dataRoot);

        Assert.Null(watch.Resolve(Team, "documents", "inbox/escape", out var link));
        Assert.Equal("\"inbox/escape\" is a symbolic link, and a watched folder may not be one.", link);

        Assert.Null(watch.Resolve(Team, "root:Share", "../..", out var leaves));
        Assert.Equal("That path leaves the file-browser root \"Share\".", leaves);

        var target = watch.Resolve(Team, "root:Share", "in/", out var fine);
        Assert.Null(fine);
        Assert.Equal("root:Share", target!.Root);
        Assert.Equal("in", target.Folder);
        Assert.Equal("Share/in", target.Display);

        Assert.Equal(
            ["documents", "root:Share"],
            watch.RootOptions().Select(o => o.Value).ToArray());
    }

    [Fact]
    public void A_folder_over_the_entry_cap_is_refused()
    {
        var big = Path.Combine(Inbox, "big");
        Directory.CreateDirectory(big);

        for (var i = 0; i <= FolderWatch.MaximumEntries; i++)
        {
            File.WriteAllText(Path.Combine(big, $"{i}.txt"), "");
        }

        var listing = FolderWatch.List(_watch.Resolve(Team, "documents", "inbox/big", out _)!, null);

        Assert.Equal("The folder holds more than 10,000 entries. Watch a smaller folder.", listing.Refusal);
    }

    private async Task<string> CreateAsync(int quietSeconds = 30, int minIntervalSeconds = 60, string? container = null)
    {
        var id = Guid.NewGuid().ToString("N");

        await _triggers.SaveAsync(
            new TriggerRow(
                id, Team, container ?? _worker.Name, "Inbox", "Look at the inbox: {event.changed}",
                "folderChange", null, null, null, null,
                IdleOnly: true, Enabled: true, NextDueAt: _t0, null, null, null, 0, _t0, "person-1",
                EventType: MessageTypes.FileChanged)
            {
                WatchRoot = "documents",
                WatchPath = "inbox",
                PollSeconds = 15,
                QuietSeconds = quietSeconds,
                MinIntervalSeconds = minIntervalSeconds,
            },
            Ct);

        return id;
    }

    private async Task PollAsync(string id, DateTimeOffset at) =>
        await _watch.PollAsync((await _triggers.FindAsync(id, Ct))!, at, Ct);

    private string Drop(string name, string content)
    {
        var path = Path.Combine(Inbox, name);
        File.WriteAllText(path, content);
        return path;
    }
}
