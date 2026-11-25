using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Yawble.Core.Teams;

namespace Yawble.Core.Bus;

/// <summary>
/// The Manager's look back at a finished workflow. workflow_digest gives the Manager the facts of one of its team's
/// workflows (Teams/WorkflowDigest.cs builds the text; this gathers what it is built from). When a workflow_complete is
/// accepted, the team's Manager is given one retro item for it, unless the workflow was small and uneventful
/// (<see cref="DigestCounts.Eventful"/>): its own workflow, rooted by the app, so the completed one stays completed. A
/// workflow gets at most one retro item, ever; a closed or expired one gets none (only workflow_complete queues it); a
/// retro's own workflow gets none. A retro item is the lowest priority there is: it waits while any other run is queued,
/// and a Manager with other work does that first.
/// </summary>
public sealed partial class BusEngine
{
    /// <summary>The retro item's payload marker: the workflow it is for.</summary>
    public const string RetroMarker = "retroOf";

    public static string RetroText(long workflow) => $"Workflow {workflow} is complete: do its retrospective (skills_get retrospective).";

    /// <summary>A run's transcript bytes by run id (Transcripts.TranscriptStore.Read); null in tests and cores without one.</summary>
    public Func<long, byte[]?>? TranscriptOf { get; set; }

    /// <summary>
    /// workflow_digest: the Manager's only, for its own team's workflows. Without a workflow, the one this run works in, or,
    /// in a retro item's run, the workflow the retro is for.
    /// </summary>
    internal async Task<string> WorkflowDigestAsync(RunContext run, long? workflow, CancellationToken ct)
    {
        if (run.Member.Role != MemberRole.Manager)
            throw new BusRefusal("not_allowed", "workflow_digest is the Manager's tool; ask the Manager if you need to know how a workflow went");
        var correlation = workflow ?? run.Items.Select(BusStore.RetroOf).FirstOrDefault(r => r is not null) ?? run.Correlation;
        var input = await DigestInputAsync(run.Member.TeamId, correlation, ct)
            ?? throw new BusRefusal("no_such_workflow", $"workflow {correlation} is not your team's");
        return WorkflowDigest.Build(input).Text;
    }

    /// <summary>Everything the digest reads, or null when the workflow is not the team's. The bus under the gate; asks and transcripts outside it.</summary>
    private async Task<DigestInput?> DigestInputAsync(long teamId, long correlation, CancellationToken ct)
    {
        var bus = await WithGate(() =>
        {
            if (TeamOf(correlation) != teamId) return null;
            var thread = _store.Workflow(correlation);
            var rows = _store.MemberRows(teamId);
            var byId = rows.ToDictionary(m => m.Id);
            var endings = thread.Where(m => m.Type is MessageTypes.Completed or MessageTypes.Failed)
                .Select(m => (Run: Long(m, "runId"), Exit: ExitCodeOf(m))).Where(x => x.Run is not null)
                .GroupBy(x => x.Run!.Value).ToDictionary(g => g.Key, g => g.Last().Exit);
            var runs = _store.RunsOf(correlation).Select(r =>
            {
                var member = byId.GetValueOrDefault(r.MemberId);
                var name = member?.Name ?? "(a removed member)";
                return new DigestRun(r.Id, name, member?.Cli ?? "unknown", WorkflowDigest.CardOfRun(thread, name, r.CausationSeq),
                    Time(r.StartedAt), r.EndedAt is null ? null : Time(r.EndedAt), r.State, r.FailureClass, endings.GetValueOrDefault(r.Id), r.Reason, null);
            }).ToList();
            var ends = thread.LastOrDefault(m => MessageTypes.WorkflowEnds.Contains(m.Type));
            var state = !_store.WorkflowEnded(correlation) || ends is null ? "open"
                : ends.Type switch { MessageTypes.WorkflowCompleted => "completed", MessageTypes.WorkflowClosed => "closed", _ => "expired" };
            var team = rows.FirstOrDefault()?.Team ?? "";
            var manager = rows.FirstOrDefault(m => m.Role == "manager")?.Name ?? "Manager";
            var testers = rows.Where(Yawble.Core.Board.TesterRule.IsTester).Select(m => m.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return new DigestInput(correlation, team, state, thread, runs, [], LeaseUsesOf(correlation), testers, _store.Clock.GetUtcNow(), manager);
        }, ct);
        if (bus is null) return null;

        var asks = new List<DigestAsk>();
        try
        {
            foreach (var a in NetworkAsks?.Asks(pendingOnly: false, teamId: teamId) ?? [])
                if (a.Workflow == correlation) asks.Add(new DigestAsk("host", a.MemberName, a.Host, a.Reason, a.State, a.Count));
            foreach (var a in ToolAsks?.Asks(openOnly: false, teamId: teamId) ?? [])
                if (a.Workflow == correlation) asks.Add(new DigestAsk("tool", a.MemberName, a.Tool, a.Reason, a.State, a.Count));
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log($"workflow {correlation}: the digest could not read the host and tool asks: {e.Message}");
        }
        var runs = bus.Runs.Select(r =>
        {
            if (TranscriptOf is not { } read || !(WorkflowDigest.FullTranscript(r.Cli) || WorkflowDigest.PartTranscript(r.Cli))) return r;
            try { return r with { Transcript = read(r.RunId) }; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return r; }
        }).ToList();
        return bus with { Asks = asks, Runs = runs };
    }

    /// <summary>
    /// After a workflow_complete is accepted: one retro item for the team's Manager, when the workflow was eventful, it still
    /// stands completed, and none was ever queued for it. Never fails the declaration: a problem is logged.
    /// </summary>
    private async Task QueueRetroAsync(MemberRef declarer, Message completed, CancellationToken ct)
    {
        if (!_options.Retros) return;
        var correlation = completed.CorrelationId;
        try
        {
            var root = await WithGate(() => _store.Find(correlation), ct);
            if (root is not null && BusStore.RetroOf(root) is not null) return; // a retro's own workflow
            var input = await DigestInputAsync(declarer.TeamId, correlation, ct);
            if (input is null) return;
            var counts = WorkflowDigest.Build(input).Counts;
            if (!counts.Eventful)
            {
                Log($"workflow {correlation}: no retrospective, it was small and uneventful");
                return;
            }
            await WithGate(() =>
            {
                if (!_store.WorkflowEnded(correlation)) return; // reopened since: its next completion decides
                var end = _store.Workflow(correlation).LastOrDefault(m => MessageTypes.WorkflowEnds.Contains(m.Type));
                if (end?.Type != MessageTypes.WorkflowCompleted || _store.RetroQueued(correlation)) return;
                if (_store.TeamMembers(declarer.TeamId).SingleOrDefault(m => m.Role == MemberRole.Manager) is not { } manager) return;
                Publish(new NewMessage(MessageTypes.Instruction(manager),
                    BatchText.InstructionPayload(RetroText(correlation), new JsonObject { [RetroMarker] = correlation }), MessageTypes.HostSource));
                Log($"workflow {correlation}: retrospective queued for {manager.Qualified}");
            }, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log($"workflow {correlation}: could not queue its retrospective: {e.Message}");
        }
    }

    /// <summary>A delivery that is a retro item (the lowest priority: <see cref="EnqueueEligible"/>, <see cref="Admit"/>).</summary>
    private bool IsRetro(BusStore.PendingRow row) => _store.Find(row.Seq) is { } m && BusStore.RetroOf(m) is not null;

    private static DateTimeOffset Time(string iso) => DateTimeOffset.Parse(iso, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static long? Long(Message m, string field)
    {
        try
        {
            using var doc = JsonDocument.Parse(m.Payload);
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty(field, out var v) && v.TryGetInt64(out var n) ? n : null;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>The agent CLI's exit code from a run's ending row (payload run.exitCode).</summary>
    private static int? ExitCodeOf(Message m)
    {
        try
        {
            using var doc = JsonDocument.Parse(m.Payload);
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("run", out var run) && run.ValueKind == JsonValueKind.Object
                && run.TryGetProperty("exitCode", out var x) && x.TryGetInt32(out var code) ? code : null;
        }
        catch (JsonException) { return null; }
    }
}
