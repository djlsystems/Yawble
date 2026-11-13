using System.Text;
using System.Text.Json;

namespace Yawble.Core.Bus;

/// <summary>
/// Runs one batch for one member: one agent CLI in one sandbox (or a fake, in tests).
/// The runner calls the bus back through <see cref="RunContext"/>, whose identity comes from the run and never
/// from arguments (the gateway stamps member, run and causation).
/// </summary>
public interface IAgentRunner
{
    Task<RunResult> RunAsync(RunContext run, CancellationToken ct);
}

/// <summary>
/// A run's outcome. FailureClass (timeout, idle, stopped, credential, start_failed) goes into the
/// agent.failed payload in place of "failed"; Details (exit code, kill latency, guardrail events) goes into the
/// agent.completed / agent.failed payload as "run".
/// </summary>
public sealed record RunResult(bool Succeeded, string? Reason = null, string? FailureClass = null, IReadOnlyDictionary<string, object?>? Details = null)
{
    public static RunResult Ok { get; } = new(true);
    public static RunResult Fail(string reason) => new(false, reason);
}

/// <summary>Status of a member in its team, as the status tool reports it.</summary>
public sealed record MemberStatus(string Name, string Role, string State, int? QueuePosition, int QueuedInstructions, string? StatusLine, string? HeavyLease = null);

/// <summary>One run's view of the bus: its batch, and the tools its agent may call.</summary>
public sealed class RunContext
{
    private readonly BusEngine _engine;

    internal RunContext(BusEngine engine, long runId, MemberRef member, IReadOnlyList<Message> items)
    {
        _engine = engine;
        RunId = runId;
        Member = member;
        Items = items;
    }

    public long RunId { get; }
    public MemberRef Member { get; }

    /// <summary>The batch: up to 16 deliveries of one workflow, oldest first. Item N in the prompt is Items[N-1].</summary>
    public IReadOnlyList<Message> Items { get; }

    /// <summary>The message that woke this run (the first item). Every message the run publishes is caused by it.</summary>
    public long Causation => Items[0].Seq;
    public long Correlation => Items[0].CorrelationId;

    /// <summary>
    /// The member's clone, workspace and branch for this run, set before the runner starts when the core has team
    /// repositories (<see cref="Teams.TeamRepoRunner"/>); null otherwise.
    /// </summary>
    public Teams.MemberCheckout? Checkout { get; internal set; }

    /// <summary>Set on a workflow's first run when the branch sync before it conflicted: said at the top of the prompt.</summary>
    public string? SyncNote { get; internal set; }

    /// <summary>What the run reported, carried in its agent.completed / agent.failed notice to the Manager.</summary>
    internal bool HandedBack { get; set; }
    internal List<BlockedItem> Blocked { get; } = [];

    /// <summary>The prompt text for the batch: a single item as is, several as numbered items.</summary>
    public string Prompt => BatchText.Render(Items);

    public Task<Message> TellAsync(string member, string instruction, CancellationToken ct = default) =>
        _engine.TellAsync(this, member, instruction, ct);

    /// <summary>Tell, for a board card (claims a planned card for the member).</summary>
    public Task<Message> TellAsync(string member, string instruction, long? card, CancellationToken ct = default) =>
        _engine.TellAsync(this, member, instruction, ct, card is { } id ? new System.Text.Json.Nodes.JsonObject { ["card"] = id } : null);

    internal BusEngine Engine => _engine;

    /// <summary>The workflow this run is part of (its first item's).</summary>
    public long Workflow => Items.Count > 0 ? Items[0].CorrelationId : 0;

    /// <summary>wake_me: be told the note again in this workflow after <paramref name="minutes"/>.</summary>
    public Task<DateTimeOffset> WakeMeAsync(int minutes, string note, CancellationToken ct = default) =>
        _engine.WakeMeAsync(this, minutes, note, ct);

    public Task<Message> ProgressAsync(string status, CancellationToken ct = default) =>
        _engine.ReportAsync(this, MessageTypes.Progress, new { status }, ct, statusLine: status);

    public Task<Message> HandbackAsync(string delivered, CancellationToken ct = default) =>
        _engine.HandbackAsync(this, delivered, ct);

    public Task<Message> BlockedAsync(string reason, int? item = null, CancellationToken ct = default) =>
        _engine.BlockedAsync(this, reason, item, ct);

    public Task<Message> NeedsDecisionAsync(string question, CancellationToken ct = default) =>
        _engine.ReportAsync(this, MessageTypes.NeedsDecision, new { question }, ct, statusLine: "needs decision: " + question);

    public Task<Message> WorkflowCompleteAsync(string delivered, CancellationToken ct = default) =>
        _engine.WorkflowCompleteAsync(this, delivered, ct);

    /// <summary>
    /// The run's idle watchdog, set by the launcher (null for a fake runner): the heavy-work lease holds it while the
    /// run waits in the lease's queue.
    /// </summary>
    public IRunIdleClock? IdleClock { get; internal set; }

    /// <summary>lease: the computer's heavy-work slots (acquire, release, status), with a queue.</summary>
    public Task<string> LeaseAsync(string action, string? what, CancellationToken ct = default) => _engine.LeaseAsync(this, action, what, ct);

    /// <summary>workflow_show: one of the team's workflows, every message oldest first (none when it is not the team's).</summary>
    public Task<IReadOnlyList<Message>> WorkflowShowAsync(long correlation, CancellationToken ct = default) =>
        _engine.WorkflowShowAsync(this, correlation, ct);

    public Task<IReadOnlyList<MemberStatus>> StatusAsync(CancellationToken ct = default) =>
        _engine.TeamStatusAsync(Member.TeamId, ct);

    /// <summary>The Manager adds a member to its team (refused for anyone else).</summary>
    public Task<MemberRow> HireAsync(string name, string? cli, string? instructions, CancellationToken ct = default) =>
        _engine.HireAsync(this, name, cli, instructions, ct);

    /// <summary>The agent CLIs the Manager may hire, and whether each can run on this computer now.</summary>
    public Task<IReadOnlyList<AgentChoice>> HiringAsync(CancellationToken ct = default) => _engine.HiringAsync(ct);

    /// <summary>The skills offered to this member (skills_list), and one of them by name (skills_get).</summary>
    public IReadOnlyList<Skills.Skill> Skills() => _engine.SkillsFor(Member);

    public Skills.Skill Skill(string name) => _engine.SkillFor(Member, name);

    /// <summary>The member's standing instructions (its job on the team), set by the person or by the Manager's hire.</summary>
    public string Instructions { get; internal set; } = "";

    /// <summary>What the person approved for the team once and for all (0024); given to the Manager's runs only.</summary>
    public IReadOnlyList<string> Approvals { get; internal set; } = [];

    /// <summary>The member's questions to the person in this workflow that are not answered yet (seq, question).</summary>
    public IReadOnlyList<(long Seq, string Question)> OpenQuestions { get; internal set; } = [];
}

/// <summary>A run's idle watchdog as the bus sees it: held while the run waits for the heavy-work lease.</summary>
public interface IRunIdleClock
{
    /// <summary>Stop counting idle time (the run waits for the lease, not for itself).</summary>
    void Hold();

    /// <summary>Count again, from a full window now.</summary>
    void Resume();
}

/// <summary>An item of a batch the member gave up on with blocked (item is null when it gave up the whole run).</summary>
public sealed record BlockedItem(int? Item, string? Instruction, string Reason);

/// <summary>
/// The prompt text of a batch. Instructions show their text; the notices a Manager is woken by
/// (a hand-back, a run that completed or failed, with the items its member blocked) are shown as one sentence each,
/// naming the member, so a Manager woken by several at once reads them as numbered items.
/// </summary>
public static class BatchText
{
    public static string InstructionPayload(string text) => JsonSerializer.Serialize(new { text });

    /// <summary>An instruction with more than its text: the board card it is for (card), or what a note to the Manager
    /// is about (about: a card; item: a backlog item).</summary>
    public static string InstructionPayload(string text, System.Text.Json.Nodes.JsonObject? extra)
    {
        if (extra is null) return InstructionPayload(text);
        var o = new System.Text.Json.Nodes.JsonObject { ["text"] = text };
        foreach (var (k, v) in extra) o[k] = v?.DeepClone();
        return o.ToJsonString();
    }

    public static string TextOf(Message message)
    {
        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(message.Payload);
            root = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return $"[{message.Type} from {message.Source}] {message.Payload}";
        }
        string? S(string name) => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        var who = message.Source == MessageTypes.PersonSource ? "The person"
            : message.Source.Contains('/') ? message.Source[(message.Source.IndexOf('/') + 1)..] : message.Source;

        if (message.Type.StartsWith(MessageTypes.InstructionPrefix, StringComparison.Ordinal))
        {
            var card = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("card", out var c) && c.TryGetInt64(out var id) ? id : (long?)null;
            return card is { } n ? $"[card {n}] {S("text") ?? ""}" : S("text") ?? message.Payload;
        }
        switch (message.Type)
        {
            case MessageTypes.Handback:
                return $"{who} handed back: {S("delivered")}";
            case MessageTypes.Progress:
                return $"{who}: {S("status")}";
            case MessageTypes.NeedsDecision:
                return $"{who} asks the person: {S("question")}";
            case MessageTypes.Blocked:
                return $"{who} is blocked{ItemPart(root)}: {S("reason")}";
            case MessageTypes.WorkflowCompleted:
                return $"{who} declared the workflow complete: {S("delivered")}";
            case MessageTypes.Completed:
            case MessageTypes.Failed:
            {
                var sb = new StringBuilder();
                if (message.Type == MessageTypes.Completed) sb.Append(who).Append("'s run ended.");
                else if (S("failureClass") == FailureClasses.Interrupted)
                    sb.Append(who).Append("'s run was interrupted: the app stopped while it ran, so its work is unfinished. Tell it again if the work is still needed.");
                else sb.Append(who).Append("'s run failed: ").Append(S("reason")).Append('.');
                if (root.TryGetProperty("handedBack", out var hb) && hb.ValueKind == JsonValueKind.False)
                {
                    sb.Append(" It did not hand back.");
                    if (root.TryGetProperty("wake", out var wk) && wk.ValueKind == JsonValueKind.Object)
                        sb.Append(" It is not stuck: it asked to be woken at ").Append(wk.GetProperty("at").GetString())
                          .Append(" to \"").Append(wk.GetProperty("note").GetString()).Append("\". Wait for that; do not tell it again.");
                }
                // The host's push to the team repository's origin after the run (TeamRepoRunner.PushDetail).
                if (root.TryGetProperty("run", out var details) && details.ValueKind == JsonValueKind.Object
                    && details.TryGetProperty("push", out var push) && push.ValueKind == JsonValueKind.Object)
                {
                    string Names(string key) => push.TryGetProperty(key, out var a) && a.ValueKind == JsonValueKind.Array
                        ? string.Join(", ", a.EnumerateArray().Select(x => x.GetString())) : "";
                    var reason = push.TryGetProperty("reason", out var pr) && pr.ValueKind == JsonValueKind.String ? pr.GetString() : null;
                    switch (push.TryGetProperty("result", out var res) ? res.GetString() : null)
                    {
                        case "Published":
                            sb.Append(" The app pushed ").Append(Names("branches")).Append(" to origin.");
                            break;
                        case "Rejected":
                            sb.Append(" Origin refused ").Append(Names("branches"))
                                .Append(" (rewritten after it was pushed; never force-pushed, the person must sort it out)");
                            if (Names("published") is { Length: > 0 } went) sb.Append("; pushed ").Append(went);
                            sb.Append('.');
                            break;
                        case "PushFailed":
                            sb.Append(" The push to origin failed (it is tried again after the next run): ").Append(Shorten(reason ?? "")).Append('.');
                            break;
                    }
                }
                if (root.TryGetProperty("blocked", out var blocked) && blocked.ValueKind == JsonValueKind.Array)
                {
                    foreach (var b in blocked.EnumerateArray())
                    {
                        var reason = b.TryGetProperty("reason", out var r) ? r.GetString() : null;
                        var instruction = b.TryGetProperty("instruction", out var ins) && ins.ValueKind == JsonValueKind.String ? ins.GetString() : null;
                        sb.Append(" Blocked").Append(ItemPart(b));
                        if (instruction is not null) sb.Append(" (\"").Append(Shorten(instruction)).Append("\")");
                        sb.Append(": ").Append(reason).Append('.');
                    }
                }
                return sb.ToString();
            }
            default:
                return $"[{message.Type} from {message.Source}] {message.Payload}";
        }
    }

    private static string ItemPart(JsonElement e) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty("item", out var i) && i.ValueKind == JsonValueKind.Number ? $" on item {i.GetInt32()}" : "";

    private static string Shorten(string text)
    {
        var line = text.ReplaceLineEndings(" ").Trim();
        return line.Length <= 80 ? line : line[..77] + "...";
    }

    public static string Render(IReadOnlyList<Message> items)
    {
        if (items.Count == 1) return TextOf(items[0]);
        var sb = new StringBuilder();
        sb.Append("Instructions sent while you were busy arrive as ONE run with ").Append(items.Count).Append(" numbered items. ")
          .Append("Each item is yours: do it, or give up just that one with blocked and its item number.\n");
        for (var i = 0; i < items.Count; i++) sb.Append('\n').Append(i + 1).Append(". ").Append(TextOf(items[i])).Append('\n');
        return sb.ToString();
    }
}

/// <summary>A request for an interactive session: its queue position, and when it is admitted and ends.</summary>
public sealed class SessionTicket
{
    private readonly BusEngine _engine;
    private readonly TaskCompletionSource<bool> _admitted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal SessionTicket(BusEngine engine, MemberRef member)
    {
        _engine = engine;
        Member = member;
    }

    public MemberRef Member { get; }

    /// <summary>True once admitted (holds a slot); false if withdrawn before that.</summary>
    public Task<bool> Admission => _admitted.Task;

    /// <summary>Completes when the session has ended and given its slot back (or was withdrawn).</summary>
    public Task Completion => _ended.Task;

    /// <summary>"waiting for a run slot (position N)"; null once admitted or withdrawn.</summary>
    public Task<int?> PositionAsync() => _engine.SessionPositionAsync(this);

    /// <summary>The person closes the window before the session started.</summary>
    public Task WithdrawAsync() => _engine.WithdrawSessionAsync(this);

    internal void Admitted() => _admitted.TrySetResult(true);

    internal void Withdrawn()
    {
        _admitted.TrySetResult(false);
        _ended.TrySetResult();
    }

    internal void Ended() => _ended.TrySetResult();
}
