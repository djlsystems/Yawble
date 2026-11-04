using System.Collections.Concurrent;
using System.Text.Json;
using Harness.Containers;
using Harness.Contracts;
using Harness.Kanban;

namespace Harness.Host;

/// <summary>
/// THE TEAM PICTURE'S LIVE COLLECTIONS, recomputed from the platform's own figures - the Kanban's
/// cards, the usage ledger's runs, the log's workflows and the run slots - and never from anything an
/// agent wrote. Each pass writes only the documents that changed and deletes the ones that no longer
/// hold, so what the page reads is bounded however long the team runs:
/// <list type="bullet">
/// <item><c>tp-items</c>: at most <see cref="MaxItems"/> open items, <see cref="MaxCardsPerItem"/>
/// cards each (counts stay whole).</item>
/// <item><c>tp-runs</c>: runs ended in the last <see cref="RunDays"/> days (UTC, today included),
/// newest <see cref="MaxRuns"/>, plus one per run in flight.</item>
/// <item><c>tp-waiting</c>: at most <see cref="MaxWaiting"/>.</item>
/// <item><c>tp-history</c>: the newest <see cref="MaxHistory"/> finished items, each with the
/// picture it had when first seen finished; a picture whose item leaves the history is deleted.</item>
/// </list>
/// A figure the platform did not measure is null and says so (<c>measured: false</c>), never 0.
/// </summary>
public sealed class TeamPictureFigures(
    ISiteStore store,
    SiteService sites,
    Func<string, bool> pictureOn,
    Func<string, string> labelFor,
    Func<string, long> floorFor,
    KanbanStore kanban,
    IBacklogStore backlog,
    IMessageLog log,
    IUsageLedger usage,
    Func<WipView>? wip = null,
    TimeProvider? clock = null)
{
    public const int MaxItems = 50;
    public const int MaxCardsPerItem = 200;
    public const int MaxRuns = 400;
    public const int RunDays = 7;
    public const int MaxWaiting = 100;
    public const int MaxHistory = 20;

    public const string SpendWords =
        "Tokens the team's runs reported, input and output together, as the platform measured them.";

    public const string NoEstimatedCost =
        "Estimated Execution Cost is not available on this instance yet.";

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <summary>What a workflow's root is: an item (a backlog dispatch or told work) or the team's
    /// ongoing work (a schedule's, a trigger's or a site action's). Roots never change, so kept.</summary>
    private sealed record Root(string Kind, string Key, long? BacklogId, string? Title);

    private readonly ConcurrentDictionary<long, Root> _roots = new();

    /// <summary>One recompute per team at a time.</summary>
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.OrdinalIgnoreCase);

    public async Task RecomputeAsync(string team, CancellationToken ct = default)
    {
        if (!pictureOn(team)) return;
        if (await store.FindAsync(team, TeamPicture.SiteName, ct) is not { } site) return;

        var gate = _gates.GetOrAdd(team, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);

        try
        {
            await ComputeAndWriteAsync(site.Team, ct);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task ComputeAndWriteAsync(string team, CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        var today = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
        var board = await kanban.GetBoardAsync(new KanbanFilter(Team: team), ct);
        var cards = board.Cards.Where(c => string.Equals(c.Team, team, StringComparison.OrdinalIgnoreCase)).ToList();

        var workflows = cards.GroupBy(c => c.WorkflowSeq).ToDictionary(g => g.Key, g => g.ToList());
        var open = await log.OpenWorkflowsAmongAsync(workflows.Keys.ToList(), ct);

        var roots = new Dictionary<long, Root>();
        foreach (var (workflow, its) in workflows) roots[workflow] = await RootAsync(workflow, its, ct);

        // ---- items and history: one per key, the newest workflow of a key winning ----
        var openItems = workflows
            .Where(w => roots[w.Key].Kind != "ongoing" && open.Contains(w.Key))
            .GroupBy(w => roots[w.Key].Key)
            .Select(g => g.OrderByDescending(w => w.Key).First())
            .OrderByDescending(w => w.Value.Min(c => c.CreatedAt))
            .Take(MaxItems)
            .ToList();

        var openKeys = openItems.Select(w => roots[w.Key].Key).ToHashSet(StringComparer.Ordinal);

        var finished = workflows
            .Where(w => roots[w.Key].Kind != "ongoing" && !open.Contains(w.Key) && !openKeys.Contains(roots[w.Key].Key))
            .GroupBy(w => roots[w.Key].Key)
            .Select(g => g.OrderByDescending(w => w.Key).First())
            .OrderByDescending(w => w.Value.Max(c => c.UpdatedAt))
            .ToList();

        var kept = finished.Take(MaxHistory).ToList();
        var dropped = finished.Skip(MaxHistory).Select(w => roots[w.Key].Key).ToList();

        var items = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var (workflow, its) in openItems) items[roots[workflow].Key] = Item(roots[workflow], workflow, its);

        // ---- now ----
        var running = cards.Where(c => c.Status == "running" && c.Member is not null)
            .GroupBy(c => c.Member!, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(c => c.UpdatedAt).First())
            .ToList();

        var holds = wip?.Invoke().Running
            .Where(h => string.Equals(h.Team, team, StringComparison.OrdinalIgnoreCase))
            .ToDictionary(h => h.Member, h => h.Since, StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);

        // ---- runs and spend, from the usage ledger ----
        var since = today.AddDays(-(RunDays - 1));
        var itemSince = openItems.Count == 0 ? since : openItems.Min(w => new DateTimeOffset(Utc(w.Value.Min(c => c.CreatedAt))));
        var ledger = await usage.ReadTeamRunsEndedAsync(team, floorFor(team), itemSince < since ? itemSince : since, null, ct);
        var kinds = ledger.Where(r => r.MemberKind is not null)
            .GroupBy(r => r.Member, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(r => r.EndedAt).First().MemberKind!, StringComparer.OrdinalIgnoreCase);

        string? KeyOf(long correlation) => roots.TryGetValue(correlation, out var root) ? root.Key : null;

        var now_ = new Dictionary<string, object>(StringComparer.Ordinal);
        var runs = new Dictionary<string, object>(StringComparer.Ordinal);

        foreach (var card in running)
        {
            var member = card.Member!;
            var startedAt = holds.TryGetValue(member, out var held) ? held : new DateTimeOffset(Utc(card.UpdatedAt));
            var last = card.Progress.Count == 0 ? null : card.Progress[^1];
            var kind = kinds.GetValueOrDefault(member) ?? "agent";

            now_[DocId(member)] = new
            {
                member,
                kind,
                item = KeyOf(card.WorkflowSeq),
                workflow = card.WorkflowSeq,
                card = new { id = card.Id, title = card.Title },
                startedAt,
                lastProgress = last is null ? null : new { text = last.Text, at = new DateTimeOffset(Utc(last.At)) },
            };

            runs[DocId("now-" + member)] = new
            {
                id = DocId("now-" + member),
                member,
                kind,
                item = KeyOf(card.WorkflowSeq),
                workflow = card.WorkflowSeq,
                startedAt,
                endedAt = (DateTimeOffset?)null,
                outcome = "running",
                tokens = new { measured = false, input = (long?)null, output = (long?)null },
            };
        }

        foreach (var row in ledger.Where(r => r.EndedAt >= since).OrderByDescending(r => r.EndedAt).Take(MaxRuns))
        {
            var id = $"r{row.RunSeq}";
            runs[id] = new
            {
                id,
                member = row.Member,
                kind = row.MemberKind ?? "agent",
                item = KeyOf(row.Correlation),
                workflow = row.Correlation,
                startedAt = row.StartedAt,
                endedAt = (DateTimeOffset?)row.EndedAt,
                outcome = row.RunOutcome,
                tokens = row.Measured
                    ? new { measured = true, input = row.TokensIn, output = row.TokensOut }
                    : new { measured = false, input = (long?)null, output = (long?)null },
            };
        }

        var spend = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["team"] = new
            {
                scope = "team",
                words = SpendWords,
                today = Tokens(ledger.Where(r => r.EndedAt >= today)),
                last7Days = Tokens(ledger.Where(r => r.EndedAt >= since)),
                estimatedExecutionCost = new { available = false, text = NoEstimatedCost },
            },
        };

        foreach (var (workflow, _) in openItems)
        {
            var key = roots[workflow].Key;
            spend[key] = new
            {
                scope = "item",
                key,
                words = SpendWords,
                total = Tokens(ledger.Where(r => r.Correlation == workflow)),
                estimatedExecutionCost = new { available = false, text = NoEstimatedCost },
            };
        }

        // ---- what waits on a person ----
        var waiting = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var card in cards
                     .Where(c => c.Status is "needs-decision" or "blocked" or "failed" && open.Contains(c.WorkflowSeq))
                     .OrderByDescending(c => c.UpdatedAt)
                     .Take(MaxWaiting))
        {
            var kind = card.Status == "needs-decision" ? "decision" : card.Status;
            waiting[DocId($"{kind}-{card.Id}")] = new
            {
                kind,
                member = card.Member,
                item = KeyOf(card.WorkflowSeq),
                workflow = card.WorkflowSeq,
                card = card.Id,
                text = card.Progress.Count == 0 ? card.Title : card.Progress[^1].Text,
                since = new DateTimeOffset(Utc(card.UpdatedAt)),
            };
        }

        // ---- history: the picture it had when it was first seen finished ----
        var history = new Dictionary<string, object>(StringComparer.Ordinal);
        var existingHistory = (await store.ListDocumentsAsync(team, TeamPicture.SiteName, TeamPictureRules.History, ct))
            .ToDictionary(d => d.Id, d => d.Json, StringComparer.Ordinal);

        foreach (var (workflow, its) in kept)
        {
            var root = roots[workflow];
            JsonElement? picture = null;

            if (existingHistory.TryGetValue(root.Key, out var before)
                && JsonDocument.Parse(before).RootElement.TryGetProperty("picture", out var kept_))
            {
                picture = kept_.Clone();
            }

            if (picture is null or { ValueKind: JsonValueKind.Null }
                && await store.GetDocumentAsync(team, TeamPicture.SiteName, TeamPictureRules.Picture, root.Key, ct) is { } drawn)
            {
                picture = JsonDocument.Parse(drawn.Json).RootElement.Clone();
            }

            history[root.Key] = new
            {
                key = root.Key,
                kind = root.Kind,
                title = Title(root, its),
                startedAt = new DateTimeOffset(Utc(its.Min(c => c.CreatedAt))),
                finishedAt = new DateTimeOffset(Utc(its.Max(c => c.UpdatedAt))),
                progress = Progress(its),
                picture,
            };
        }

        var teamDoc = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["team"] = new
            {
                team,
                label = labelFor(team),
                teamPicture = true,
                generatedAt = now,
                template = (await LiveSourceAsync(team, ct)),
                openItems = items.Count,
                running = now_.Count,
            },
        };

        var writer = SiteActor.Platform(team);

        await SyncAsync(team, TeamPictureRules.Items, items, writer, ct);
        await SyncAsync(team, TeamPictureRules.Now, now_, writer, ct);
        await SyncAsync(team, TeamPictureRules.Runs, runs, writer, ct);
        await SyncAsync(team, TeamPictureRules.Spend, spend, writer, ct);
        await SyncAsync(team, TeamPictureRules.Waiting, waiting, writer, ct);
        await SyncAsync(team, TeamPictureRules.History, history, writer, ct);
        await SyncAsync(team, TeamPictureRules.Team, teamDoc, writer, ct);

        // A picture whose item has left the history goes with it: the platform's one write to tp-picture.
        foreach (var key in dropped)
        {
            await sites.DeleteDocumentAsync(team, TeamPicture.SiteName, TeamPictureRules.Picture, key, writer, ct);
        }
    }

    private async Task<string?> LiveSourceAsync(string team, CancellationToken ct)
    {
        if (await store.FindAsync(team, TeamPicture.SiteName, ct) is not { LiveVersion: { } live }) return null;
        return (await store.VersionsAsync(team, TeamPicture.SiteName, ct)).FirstOrDefault(v => v.Version == live)?.Source;
    }

    /// <summary>Writes each document whose JSON changed and deletes each the pass no longer holds.</summary>
    private async Task SyncAsync(
        string team, string collection, IReadOnlyDictionary<string, object> wanted, SiteActor writer, CancellationToken ct)
    {
        var existing = (await store.ListDocumentsAsync(team, TeamPicture.SiteName, collection, ct))
            .ToDictionary(d => d.Id, d => d.Json, StringComparer.Ordinal);

        foreach (var (id, doc) in wanted)
        {
            var json = JsonSerializer.Serialize(doc, TeamPicture.Json);
            if (existing.TryGetValue(id, out var was) && was == json) continue;

            var written = await sites.PutDocumentAsync(team, TeamPicture.SiteName, collection, id, json, writer, ct);
            if (!written.Ok) throw new InvalidOperationException($"The Team picture's {collection}/{id} was not written: {written.Refusal}");
        }

        foreach (var id in existing.Keys.Where(id => !wanted.ContainsKey(id)))
        {
            await sites.DeleteDocumentAsync(team, TeamPicture.SiteName, collection, id, writer, ct);
        }
    }

    private async Task<Root> RootAsync(long workflow, IReadOnlyList<KanbanCard> cards, CancellationToken ct)
    {
        if (_roots.TryGetValue(workflow, out var known)) return known;

        Root root;

        if (await backlog.DispatchForCorrelationAsync(workflow, ct) is { } dispatch)
        {
            var item = await backlog.GetAsync(dispatch.Item, ct);
            root = new Root("backlog", PlatformBacklogId.Format(dispatch.Item), dispatch.Item, item?.Title);
        }
        else if (await log.FindAsync(workflow, ct) is { } first && IsOngoing(first.Source))
        {
            root = new Root("ongoing", "ongoing", null, null);
        }
        else
        {
            var manager = cards.FirstOrDefault(c =>
                string.Equals(c.Member, TeamRegistry.DefaultManagerName, StringComparison.OrdinalIgnoreCase));
            root = new Root("workflow", $"w{workflow}", null, (manager ?? cards.OrderBy(c => c.CreatedAt).First()).Title);
        }

        return _roots.GetOrAdd(workflow, root);
    }

    /// <summary>A schedule's, a trigger's or a site action's workflow is the team's ongoing work, not an item.</summary>
    private static bool IsOngoing(string source) =>
        source.StartsWith("schedule:", StringComparison.Ordinal)
        || source.StartsWith("trigger:", StringComparison.Ordinal)
        || source.StartsWith("site:", StringComparison.Ordinal);

    private static string Title(Root root, IReadOnlyList<KanbanCard> cards) =>
        root.Title ?? cards.OrderBy(c => c.CreatedAt).First().Title;

    private static object Item(Root root, long workflow, IReadOnlyList<KanbanCard> cards) => new
    {
        key = root.Key,
        kind = root.Kind,
        title = Title(root, cards),
        workflow,
        backlogId = root.BacklogId,
        startedAt = new DateTimeOffset(Utc(cards.Min(c => c.CreatedAt))),
        updatedAt = new DateTimeOffset(Utc(cards.Max(c => c.UpdatedAt))),
        progress = Progress(cards),
        cards = cards.OrderBy(c => c.CreatedAt).Take(MaxCardsPerItem).Select(c => new
        {
            id = c.Id,
            title = c.Title,
            member = c.Member,
            lane = Lane(c.LaneId),
            status = c.Status,
            workflow = c.WorkflowSeq,
            startedAt = new DateTimeOffset(Utc(c.CreatedAt)),
            finishedAt = c.LaneId == "done" ? new DateTimeOffset(Utc(c.UpdatedAt)) : (DateTimeOffset?)null,
        }).ToList(),
        cardsTruncated = cards.Count > MaxCardsPerItem,
    };

    private static object Progress(IReadOnlyList<KanbanCard> cards)
    {
        var done = cards.Count(c => c.LaneId == "done");
        var running = cards.Count(c => c.LaneId != "done" && c.Status == "running");
        var failed = cards.Count(c => c.LaneId != "done" && c.Status == "failed");
        var blocked = cards.Count(c => c.LaneId == "blocked" && c.Status != "failed");
        var todo = cards.Count - done - running - failed - blocked;
        return new { total = cards.Count, done, running, todo, blocked, failed };
    }

    private static string Lane(string laneId) => laneId switch
    {
        "in-progress" => "doing",
        "blocked" => "blocked",
        "done" => "done",
        _ => "todo",
    };

    /// <summary>Tokens over <paramref name="rows"/>: measured runs only; null, never 0, when none was measured.</summary>
    private static object Tokens(IEnumerable<UsageLedgerRow> rows)
    {
        var list = rows.ToList();
        var measured = list.Where(r => r.Measured).ToList();
        var notMeasured = list.Count - measured.Count;

        if (measured.Count == 0)
        {
            return new
            {
                measured = false, tokens = (long?)null, input = (long?)null, output = (long?)null,
                runsMeasured = 0, runsNotMeasured = notMeasured,
            };
        }

        var input = measured.Sum(r => r.TokensIn ?? 0);
        var output = measured.Sum(r => r.TokensOut ?? 0);

        return new
        {
            measured = true, tokens = (long?)(input + output), input = (long?)input, output = (long?)output,
            runsMeasured = measured.Count, runsNotMeasured = notMeasured,
        };
    }

    private static DateTime Utc(DateTime at) => at.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(at, DateTimeKind.Utc) : at.ToUniversalTime();

    /// <summary>A member name as a document id (letters, digits, '.', '_', '-').</summary>
    private static string DocId(string text)
    {
        var chars = text.Select(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' ? c : '_').ToArray();
        var id = new string(chars);
        if (id.Length == 0 || id[0] == '.') id = "_" + id;
        return id.Length > 128 ? id[..128] : id;
    }
}
