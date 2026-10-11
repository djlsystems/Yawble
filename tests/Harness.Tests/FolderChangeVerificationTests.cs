using System.Text.Json;
using Harness.Contracts;
using Harness.Host;
using Harness.Host.Auth;
using Harness.Identity;
using Harness.Messaging;
using Microsoft.Data.Sqlite;

namespace Harness.Tests;

/// <summary>
/// Folder-change trigger behaviour that <see cref="FolderChangeTriggerTests"/> covers
/// only through a hand-driven poll, here end to end. Same bed as that class: a
/// real ContainerHost and log, the real SQLite trigger store, real files, and a clock passed in.
/// </summary>
public sealed class FolderChangeVerificationTests : IAsyncLifetime
{
    private const string Team = "Alpha";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"harness-folder-verify-{Guid.NewGuid():N}");
    private readonly ContainerId _worker = new(Team, "Worker");
    private readonly DateTimeOffset _t0 = new(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);

    private ContainerTestBed _bed = null!;
    private SqliteTriggerStore _triggers = null!;
    private TriggerSweep _sweep = null!;
    private FolderWatch _watch = null!;

    private string Documents => Path.Combine(_directory, "documents");

    private string Inbox => Path.Combine(Documents, "inbox");

    private string Data => Path.Combine(_directory, "data");

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(Inbox);
        Directory.CreateDirectory(Data);

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
        await _bed.Store.SetAsync(_worker, [.. container.Snapshot().Subscribes, MessageTypes.FileChanged], Ct);

        _watch = new FolderWatch(
            _triggers, _bed.Store, _bed.Host, pending: null,
            documentsRootFor: _ => Documents,
            roots: [],
            dataRoot: Data);

        _sweep = new TriggerSweep(
            _triggers, _bed.Host, new SqlitePendingDeliveries(database), _bed.Store,
            new TenantLogging(new SqliteTenantLog(database)), _watch,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<TriggerSweep>.Instance);
    }

    public async ValueTask DisposeAsync()
    {
        await _bed.DisposeAsync();
        SqliteConnection.ClearAllPools();

        try { Directory.Delete(_directory, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>
    /// A test that calls PollAsync by hand never asks whether the RUNNER would poll
    /// the row again. It polls only rows with a non-null next_due_at (DueAsync), and the pump's
    /// event-trigger arm records an outcome with nextDueAt: null.
    /// </summary>
    [Fact]
    public async Task A_folder_trigger_is_still_due_for_polling_after_its_event_woke_the_member()
    {
        var id = await CreateAsync(quietSeconds: 0);

        await PollAsync(id, _t0);
        Drop("a.txt", "a");
        await PollAsync(id, _t0.AddSeconds(15));
        Assert.Single(await _bed.OfTypeAsync(MessageTypes.FileChanged));

        Assert.NotNull((await _triggers.FindAsync(id, Ct))!.NextDueAt);

        Assert.True(await _bed.PumpUntilAsync(() => _bed.Agent.RunsFor(_worker) == 1));
        await _bed.SettleAsync();

        var row = (await _triggers.FindAsync(id, Ct))!;
        Assert.Equal("fired", row.LastOutcome);
        Assert.NotNull(row.NextDueAt);
        Assert.Contains(await _triggers.DueAsync(_t0.AddDays(1), Ct), r => r.Id == id);
    }

    /// <summary>
    /// End to end: only the sweep polls - nothing calls PollAsync by hand - so a watch the pump
    /// had stopped scheduling would never see the second file.
    /// </summary>
    [Fact]
    public async Task The_sweep_picks_up_a_second_change_after_the_first_one_woke_the_member()
    {
        var id = await CreateAsync(quietSeconds: 0);

        await _sweep.FireDueAsync(_t0, Ct);
        Drop("a.txt", "a");
        await _sweep.FireDueAsync(_t0.AddSeconds(15), Ct);

        Assert.True(await _bed.PumpUntilAsync(() => _bed.Agent.RunsFor(_worker) == 1));
        await _bed.SettleAsync();

        // The member's run is folded by the next poll; the person's file after it is not.
        await _sweep.FireDueAsync(_t0.AddSeconds(30), Ct);
        Drop("b.txt", "b");
        await _sweep.FireDueAsync(_t0.AddSeconds(90), Ct);

        var fired = await _bed.OfTypeAsync(MessageTypes.FileChanged);
        Assert.Equal(2, fired.Count);
        Assert.Contains("inbox/b.txt", fired[1].Payload);
        Assert.DoesNotContain("inbox/a.txt", fired[1].Payload);

        Assert.True(await _bed.PumpUntilAsync(() => _bed.Agent.RunsFor(_worker) == 2));
        Assert.NotNull((await _triggers.FindAsync(id, Ct))!.NextDueAt);
    }

    /// <summary>Keeping a next due time is for the folder kind alone: a plain event trigger
    /// records "not scheduled" after it fires.</summary>
    [Fact]
    public async Task A_plain_event_trigger_still_records_no_next_due_time_when_it_fires()
    {
        var id = Guid.NewGuid().ToString("N");
        await _triggers.SaveAsync(
            new TriggerRow(
                id, Team, _worker.Name, "Any file", "Something changed: {event.changed}",
                "event", null, null, null, null,
                IdleOnly: false, Enabled: true, NextDueAt: _t0, null, null, null, 0, _t0, "person-1",
                EventType: MessageTypes.FileChanged),
            Ct);

        Drop("upload.pdf", "bytes");
        await _watch.AnnounceAsync(Team, "inbox", ["inbox/upload.pdf"], "person-1", Ct);

        Assert.True(await _bed.PumpUntilAsync(() => _bed.Agent.RunsFor(_worker) == 1));
        await _bed.SettleAsync();

        var row = (await _triggers.FindAsync(id, Ct))!;
        Assert.Equal("fired", row.LastOutcome);
        Assert.Null(row.NextDueAt);
    }

    /// <summary>The same, for a wake that came from a Documents-dialog announcement.</summary>
    [Fact]
    public async Task A_folder_trigger_is_still_due_for_polling_after_a_documents_upload_woke_the_member()
    {
        var id = await CreateAsync(quietSeconds: 0);
        await PollAsync(id, _t0);

        Drop("upload.pdf", "bytes");
        await _watch.AnnounceAsync(Team, "inbox", ["inbox/upload.pdf"], "person-1", Ct);

        Assert.True(await _bed.PumpUntilAsync(() => _bed.Agent.RunsFor(_worker) == 1));
        await _bed.SettleAsync();

        Assert.NotNull((await _triggers.FindAsync(id, Ct))!.NextDueAt);
    }

    [Fact]
    public async Task The_payload_and_the_instruction_carry_paths_and_counts_never_contents()
    {
        const string secret = "CONTENTS-MUST-NOT-LEAK-7f3a";
        var id = await CreateAsync(quietSeconds: 0, instruction: "Look at {event.path}: {event.changed} ({event.count})");

        await PollAsync(id, _t0);
        Drop("secret.txt", secret);
        await PollAsync(id, _t0.AddSeconds(15));

        var published = Assert.Single(await _bed.OfTypeAsync(MessageTypes.FileChanged));
        Assert.DoesNotContain(secret, published.Payload);

        using (var payload = JsonDocument.Parse(published.Payload))
        {
            Assert.Equal(
                ["changed", "count", "path", "root", "team"],
                payload.RootElement.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray());
        }

        Assert.True(await _bed.PumpUntilAsync(() => _bed.Agent.RunsFor(_worker) == 1));
        var instruction = Assert.Single(await _bed.OfTypeAsync(MessageTypes.InstructionFor(_worker)));
        Assert.DoesNotContain(secret, instruction.Payload);
        Assert.Contains("inbox/secret.txt", instruction.Payload);
    }

    [Fact]
    public async Task Changed_is_cut_at_one_hundred_and_count_is_the_whole_number()
    {
        var id = await CreateAsync(quietSeconds: 0);
        await PollAsync(id, _t0);

        for (var i = 0; i < 150; i++) Drop($"f{i:D3}.txt", "x");
        await PollAsync(id, _t0.AddSeconds(15));

        using var payload = JsonDocument.Parse(Assert.Single(await _bed.OfTypeAsync(MessageTypes.FileChanged)).Payload);
        Assert.Equal(150, payload.RootElement.GetProperty(PayloadFields.Count).GetInt32());
        Assert.Equal(100, payload.RootElement.GetProperty(PayloadFields.Changed).GetArrayLength());
    }

    /// <summary>Bullet 4 with the DEFAULT quiet period and minimum interval, not quiet 0.</summary>
    [Fact]
    public async Task The_woken_member_writing_into_the_folder_does_not_refire_with_default_settings()
    {
        var id = await CreateAsync(quietSeconds: 30, minIntervalSeconds: 60);

        _bed.Agent.Behaviour = _ =>
        {
            Drop("answer.txt", "the member's reply, a different size");
            return Task.FromResult(new AgentResult(0, "done"));
        };

        await PollAsync(id, _t0);
        Drop("question.txt", "from a person");
        await PollAsync(id, _t0.AddSeconds(15));
        await PollAsync(id, _t0.AddSeconds(45));
        Assert.Single(await _bed.OfTypeAsync(MessageTypes.FileChanged));

        Assert.True(await _bed.PumpUntilAsync(() => _bed.Agent.RunsFor(_worker) == 1));
        await _bed.SettleAsync();

        for (var s = 60; s <= 400; s += 15) await PollAsync(id, _t0.AddSeconds(s));
        await _bed.SettleAsync();

        Assert.Single(await _bed.OfTypeAsync(MessageTypes.FileChanged));
        Assert.Equal(1, _bed.Agent.RunsFor(_worker));
    }

    /// <summary>Bullet 4 where the poll lands WHILE the member is still running (TeamIsRunning).</summary>
    [Fact]
    public async Task A_poll_during_the_members_run_folds_its_write_into_the_baseline()
    {
        var id = await CreateAsync(quietSeconds: 0, minIntervalSeconds: 0);

        _bed.Agent.Behaviour = async _ =>
        {
            Drop("answer.txt", "written mid-run");
            await PollAsync(id, _t0.AddSeconds(30));
            return new AgentResult(0, "done");
        };

        await PollAsync(id, _t0);
        Drop("question.txt", "q");
        await PollAsync(id, _t0.AddSeconds(15));

        Assert.True(await _bed.PumpUntilAsync(() => _bed.Agent.RunsFor(_worker) == 1));
        await _bed.SettleAsync();

        await PollAsync(id, _t0.AddSeconds(45));
        await PollAsync(id, _t0.AddSeconds(60));
        await _bed.SettleAsync();

        Assert.Single(await _bed.OfTypeAsync(MessageTypes.FileChanged));
        Assert.Equal(1, _bed.Agent.RunsFor(_worker));
    }

    /// <summary>
    /// A person's change after the member has finished and a clean poll has passed DOES fire again:
    /// the loop guard folds only what happened around a run.
    /// </summary>
    [Fact]
    public async Task A_later_change_by_a_person_fires_again_once_the_run_is_over()
    {
        var id = await CreateAsync(quietSeconds: 0, minIntervalSeconds: 60);

        await PollAsync(id, _t0);
        Drop("one.txt", "1");
        await PollAsync(id, _t0.AddSeconds(15));
        Assert.True(await _bed.PumpUntilAsync(() => _bed.Agent.RunsFor(_worker) == 1));
        await _bed.SettleAsync();

        await PollAsync(id, _t0.AddSeconds(30)); // folds the run
        Drop("two.txt", "22");
        await PollAsync(id, _t0.AddSeconds(90));

        Assert.Equal(2, (await _bed.OfTypeAsync(MessageTypes.FileChanged)).Count);
    }

    [Fact]
    public async Task A_touch_during_the_quiet_period_restarts_it_and_a_pure_touch_after_a_fire_does_not_fire()
    {
        var id = await CreateAsync(quietSeconds: 30, minIntervalSeconds: 0);
        await PollAsync(id, _t0);

        var file = Drop("a.txt", "a");
        await PollAsync(id, _t0.AddSeconds(15)); // pending since 15
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(5));
        await PollAsync(id, _t0.AddSeconds(45)); // touched: new shape, pending since 45
        Assert.Empty(await _bed.OfTypeAsync(MessageTypes.FileChanged));

        await PollAsync(id, _t0.AddSeconds(75));
        Assert.Single(await _bed.OfTypeAsync(MessageTypes.FileChanged));

        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(10));
        for (var s = 90; s <= 300; s += 15) await PollAsync(id, _t0.AddSeconds(s));
        Assert.Single(await _bed.OfTypeAsync(MessageTypes.FileChanged));
    }

    [Fact]
    public async Task Every_ignored_name_is_left_out_of_a_poll()
    {
        var id = await CreateAsync(quietSeconds: 0, minIntervalSeconds: 0);
        await PollAsync(id, _t0);

        foreach (var folder in new[] { ".git", "node_modules", ".worktrees", ".harness-team", "sub/.git", "sub/node_modules" })
        {
            Directory.CreateDirectory(Path.Combine(Inbox, folder));
            File.WriteAllText(Path.Combine(Inbox, folder, "x.txt"), "x");
        }

        Directory.CreateDirectory(Path.Combine(Inbox, "sub2"));
        File.WriteAllText(Path.Combine(Inbox, "sub2", ".harness-team"), "marker"); // the marker as a file
        Drop("a.TMP", "t");
        Drop("b.tmp", "t");
        Drop(".c.swp", "s");
        Drop("d.SWP", "s");
        Drop("e.txt~", "e");
        Drop(".#f.txt", "f");

        await PollAsync(id, _t0.AddSeconds(15));
        await PollAsync(id, _t0.AddSeconds(30));
        Assert.Empty(await _bed.OfTypeAsync(MessageTypes.FileChanged));

        Drop("real.txt", "r");
        await PollAsync(id, _t0.AddSeconds(45));

        using var payload = JsonDocument.Parse(Assert.Single(await _bed.OfTypeAsync(MessageTypes.FileChanged)).Payload);
        Assert.Equal(["inbox/real.txt"],
            payload.RootElement.GetProperty(PayloadFields.Changed).EnumerateArray().Select(e => e.GetString()!).ToArray());
    }

    [Fact]
    public async Task The_entry_cap_is_ten_thousand_counts_folders_skips_ignored_and_a_poll_over_it_records_the_sentence()
    {
        var id = await CreateAsync(quietSeconds: 0, minIntervalSeconds: 0);

        // Ignored entries never count toward the cap.
        var modules = Path.Combine(Inbox, "node_modules");
        Directory.CreateDirectory(modules);
        for (var i = 0; i < 10_050; i++) File.WriteAllText(Path.Combine(modules, $"{i}.js"), "");

        // Exactly 10,000 entries: 1 folder + 9,999 files. Allowed.
        var big = Path.Combine(Inbox, "big");
        Directory.CreateDirectory(big);
        for (var i = 0; i < 9_999; i++) File.WriteAllText(Path.Combine(big, $"{i}.txt"), "");

        var target = _watch.Resolve(Team, "documents", "inbox", out _)!;
        var listing = FolderWatch.List(target, null);
        Assert.Null(listing.Refusal);
        Assert.Equal(9_999, listing.Entries.Count);

        await PollAsync(id, _t0);
        var baseline = (await _triggers.FindAsync(id, Ct))!.LastFingerprint;
        Assert.NotNull(baseline);

        // One more: 10,001. A poll records the sentence, keeps the baseline, and publishes nothing.
        Drop("one-too-many.txt", "");
        await PollAsync(id, _t0.AddSeconds(15));

        var row = (await _triggers.FindAsync(id, Ct))!;
        Assert.Equal("The folder holds more than 10,000 entries. Watch a smaller folder.", row.LastPollError);
        Assert.Equal(baseline, row.LastFingerprint);
        Assert.NotNull(row.NextDueAt);
        Assert.Empty(await _bed.OfTypeAsync(MessageTypes.FileChanged));

        Assert.Equal(
            "The folder holds more than 10,000 entries. Watch a smaller folder.",
            await _watch.RefusalForAsync(Team, "documents", "inbox", null, 60, 30, 60, counting: true, Ct));
    }

    [Fact]
    public async Task The_instance_watch_cap_refuses_a_new_watch_but_not_an_edit()
    {
        var capped = new FolderWatch(
            _triggers, _bed.Store, _bed.Host, pending: null,
            documentsRootFor: _ => Documents, roots: [], dataRoot: Data, maximumWatches: 2);

        await CreateAsync();
        Assert.Null(await capped.RefusalForAsync(Team, "documents", "inbox", null, 60, 30, 60, counting: true, Ct));
        await CreateAsync();

        Assert.Equal(
            "This instance already watches 2 folders, the most it allows.",
            await capped.RefusalForAsync(Team, "documents", "inbox", null, 60, 30, 60, counting: true, Ct));
        Assert.Null(await capped.RefusalForAsync(Team, "documents", "inbox", null, 60, 30, 60, counting: false, Ct));
    }

    [Fact]
    public void Dot_dot_escapes_are_refused_however_they_are_spelled()
    {
        foreach (var path in new[] { "..", "../", "inbox/../..", "inbox/../../data", "./../x", "inbox\\..\\..\\x", "inbox/./../../.." })
        {
            Assert.Null(_watch.Resolve(Team, "documents", path, out var refusal));
            Assert.Equal("That path leaves the team's documents folder.", refusal);
        }

        // A `..` that stays inside is fine and is stored resolved.
        var inside = _watch.Resolve(Team, "documents", "inbox/../inbox/", out var none);
        Assert.Null(none);
        Assert.Equal("inbox", inside!.Folder);
    }

    [Fact]
    public void A_symlink_anywhere_on_the_way_down_is_refused()
    {
        var outside = Path.Combine(_directory, "outside", "sub");
        Directory.CreateDirectory(outside);
        Directory.CreateSymbolicLink(Path.Combine(Inbox, "link"), Path.Combine(_directory, "outside"));

        Assert.Null(_watch.Resolve(Team, "documents", "inbox/link/sub", out var refusal));
        Assert.Equal("\"inbox/link\" is a symbolic link, and a watched folder may not be one.", refusal);

        // A symlink inside a watched folder is not followed by the listing.
        File.WriteAllText(Path.Combine(outside, "far.txt"), "far");
        var listing = FolderWatch.List(_watch.Resolve(Team, "documents", "inbox", out _)!, null);
        Assert.DoesNotContain(listing.Entries, e => e.Path.Contains("far.txt", StringComparison.Ordinal));
    }

    /// <summary>
    /// An allowWatch root whose configured path is a symbolic link to the data root. TouchesDataRoot
    /// compares Path.GetFullPath strings, which never resolve a link, and the per-segment symlink
    /// check starts BELOW the root.
    /// </summary>
    [Fact]
    public void An_allowWatch_root_that_is_a_symlink_to_the_data_root_is_refused()
    {
        File.WriteAllText(Path.Combine(Data, "harness.db"), "db");
        var sneaky = Path.Combine(_directory, "sneaky");
        Directory.CreateSymbolicLink(sneaky, Data);

        var watch = new FolderWatch(
            _triggers, _bed.Store, _bed.Host, pending: null,
            documentsRootFor: _ => Documents,
            roots: [new FileBrowserRoot("Sneaky", sneaky, AllowWatch: true)],
            dataRoot: Data);

        Assert.Null(watch.Resolve(Team, "root:Sneaky", "", out var refusal));
        Assert.Equal("The platform's own data folder cannot be watched.", refusal);
        Assert.DoesNotContain(watch.RootOptions(), option => option.Value == "root:Sneaky");
    }

    [Fact]
    public void An_allowWatch_root_that_is_a_symlink_into_the_data_root_or_under_a_linked_ancestor_is_refused()
    {
        Directory.CreateDirectory(Path.Combine(Data, "logs"));
        var into = Path.Combine(_directory, "into");
        Directory.CreateSymbolicLink(into, Path.Combine(Data, "logs"));

        // A plain folder path whose PARENT is the link: the last segment is no link at all.
        var parent = Path.Combine(_directory, "parent");
        Directory.CreateSymbolicLink(parent, Data);

        var watch = new FolderWatch(
            _triggers, _bed.Store, _bed.Host, pending: null,
            documentsRootFor: _ => Documents,
            roots:
            [
                new FileBrowserRoot("Into", into, AllowWatch: true),
                new FileBrowserRoot("Under", Path.Combine(parent, "logs"), AllowWatch: true),
            ],
            dataRoot: Data);

        Assert.Null(watch.Resolve(Team, "root:Into", "", out var intoRefusal));
        Assert.Equal("The platform's own data folder cannot be watched.", intoRefusal);
        Assert.Null(watch.Resolve(Team, "root:Under", "", out var underRefusal));
        Assert.Equal("The platform's own data folder cannot be watched.", underRefusal);
        Assert.DoesNotContain(watch.RootOptions(), option => option.Value is "root:Into" or "root:Under");
    }

    /// <summary>
    /// A RELATIVE link to the data root, and a data root that is itself
    /// configured through a link while the watch root names the real folder, are refused too. A
    /// plain root beside them is still offered and resolves, so the check does not refuse too much.
    /// </summary>
    [Fact]
    public void A_relative_link_and_a_data_root_reached_through_a_link_are_refused_and_a_plain_root_still_works()
    {
        var relative = Path.Combine(_directory, "relative");
        Directory.CreateSymbolicLink(relative, Path.GetRelativePath(_directory, Data));

        var dataLink = Path.Combine(_directory, "data-link");
        Directory.CreateSymbolicLink(dataLink, Data);

        var share = Path.Combine(_directory, "share");
        Directory.CreateDirectory(Path.Combine(share, "in"));

        var watch = new FolderWatch(
            _triggers, _bed.Store, _bed.Host, pending: null,
            documentsRootFor: _ => Documents,
            roots:
            [
                new FileBrowserRoot("Relative", relative, AllowWatch: true),
                new FileBrowserRoot("Real", Data, AllowWatch: true),
                new FileBrowserRoot("Share", share, AllowWatch: true),
            ],
            dataRoot: dataLink);

        Assert.Null(watch.Resolve(Team, "root:Relative", "", out var relativeRefusal));
        Assert.Equal("The platform's own data folder cannot be watched.", relativeRefusal);
        Assert.Null(watch.Resolve(Team, "root:Real", "", out var realRefusal));
        Assert.Equal("The platform's own data folder cannot be watched.", realRefusal);

        Assert.NotNull(watch.Resolve(Team, "root:Share", "in", out var shareRefusal));
        Assert.Null(shareRefusal);
        Assert.Equal(
            ["root:Share"],
            watch.RootOptions().Select(option => option.Value).Where(value => value.StartsWith("root:")));
    }

    [Fact]
    public void A_root_without_allowWatch_and_the_data_root_are_refused_and_not_offered()
    {
        var share = Path.Combine(_directory, "share");
        Directory.CreateDirectory(share);

        var watch = new FolderWatch(
            _triggers, _bed.Store, _bed.Host, pending: null,
            documentsRootFor: _ => Documents,
            roots:
            [
                new FileBrowserRoot("Browse", share, AllowCreate: true, AllowUpdate: true, AllowDelete: true),
                new FileBrowserRoot("Everything", _directory, AllowWatch: true), // CONTAINS the data root
                new FileBrowserRoot("Data", Data, AllowWatch: true),
            ],
            dataRoot: Data);

        Assert.Null(watch.Resolve(Team, "root:Browse", "", out var browse));
        Assert.Equal(
            "The file-browser root \"Browse\" does not allow watching. Set \"allowWatch\": true on it under FileBrowser:Roots.",
            browse);

        Assert.Null(watch.Resolve(Team, "root:Everything", "share", out var above));
        Assert.Equal("The platform's own data folder cannot be watched.", above);

        Assert.Null(watch.Resolve(Team, "root:data", "", out var data));
        Assert.Equal("The platform's own data folder cannot be watched.", data);

        Assert.Equal(["documents"], watch.RootOptions().Select(o => o.Value).ToArray());
    }

    private async Task<string> CreateAsync(
        int quietSeconds = 30, int minIntervalSeconds = 60, string instruction = "Look at the inbox: {event.changed}")
    {
        var id = Guid.NewGuid().ToString("N");

        await _triggers.SaveAsync(
            new TriggerRow(
                id, Team, _worker.Name, "Inbox", instruction,
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
