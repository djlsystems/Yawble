using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Yawble.Core.Bus;

namespace Yawble.Core.Link;

/// <summary>
/// org.summary's facts from the running app: the bus (teams, agents, run slots, workflows, decisions), the board (cards and
/// the backlog item a workflow came from), the asks waiting for the person (tool-call approvals, network and tool requests)
/// and the logs (warnings and errors of the last hour). It reads; it never changes anything. Never read: the model usage
/// log and the token counts on run records (B048: no tokens, cost or usage in the summary).
/// </summary>
public sealed class AppOrgSummarySource(
    BusEngine bus,
    Board.BoardStore? board,
    Integrations.IntegrationService? integrations,
    Integrations.NetworkAskService? networkAsks,
    Integrations.ToolAskService? toolAsks,
    Logs.LogService? logs,
    Func<string?> label,
    DateTimeOffset startedAt,
    TimeProvider? clock = null) : IOrgSummarySource
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public async Task<OrgSummaryFacts> ReadAsync(CancellationToken ct)
    {
        var slots = await bus.RunSlotsAsync(ct).ConfigureAwait(false);
        var computer = new OrgComputer(Environment.MachineName, label(), CoreInfo.Version,
            System.Runtime.InteropServices.RuntimeInformation.OSDescription, startedAt, slots.Running, slots.Limit, slots.Queued);
        var teams = new List<OrgTeam>();
        var workflows = new List<OrgWorkflow>();
        var asks = new List<OrgAsk>();
        foreach (var t in await bus.TeamsAsync(ct).ConfigureAwait(false))
        {
            var cards = await bus.AgentCardsAsync(t.Id, ct).ConfigureAwait(false);
            teams.Add(new OrgTeam(t.Name, t.Paused, t.Archived, cards.Select(c => new OrgAgent(c.Member.Name, Role(c.Member), c.State,
                c.LastRunState == "failed", c.State == "running" ? Doing(t.Name, c) : null,
                c.State == "running" ? ParseTime(c.RunStartedAt) : null)).ToList(), t.WorkflowExpiryDays));

            var decisions = await bus.OpenDecisionsAsync(t.Name, ct).ConfigureAwait(false);
            asks.AddRange(decisions.Select(d => new OrgAsk("decision", t.Name, Field(d, "question") ?? Field(d, "text") ?? "a decision")));
            var reports = await bus.LatestReportsAsync(t.Name, ct).ConfigureAwait(false);
            var failedOwners = cards.Where(c => c.State != "running" && c.LastRunState == "failed").Select(c => c.Member.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var w in await bus.WorkflowsAsync(t.Name, 50, ct).ConfigureAwait(false))
            {
                var state = StateOf(w, decisions.Any(d => d.CorrelationId == w.Id),
                    reports.Values.Any(r => r.CorrelationId == w.Id && r.Type == MessageTypes.Blocked), w.Owner is { } o && failedOwners.Contains(o));
                var onBoard = board?.CardsOfWorkflow(w.Id) ?? [];
                var item = board?.ItemDispatchedAs(w.Id)
                    ?? (onBoard.Select(c => Board.BoardStore.ItemId(c.Item)).FirstOrDefault(i => i is not null) is { } id ? board?.Item(id) : null);
                workflows.Add(new OrgWorkflow(w.Id, t.Name, w.Title, item?.Label, item?.Title, state,
                    onBoard.Count(c => c.Lane == "done"), onBoard.Count, w.StartedAt, w.LastAt, w.Owner, w.EndedAt, w.ExpiredAt, w.ExpiresAt));
            }
            if (integrations is not null)
                asks.AddRange(integrations.Asks(t.Id, pendingOnly: true).Select(a => new OrgAsk("toolCall", t.Name, $"{a.Member}: {a.Integration} {string.Join(' ', a.Args)}".Trim())));
            if (networkAsks is not null)
                asks.AddRange(networkAsks.Asks(pendingOnly: true, teamId: t.Id).Select(a => new OrgAsk("network", t.Name, $"{a.MemberName}: {a.Host}")));
            if (toolAsks is not null)
                asks.AddRange(toolAsks.Asks(openOnly: true, teamId: t.Id).Select(a => new OrgAsk("tool", t.Name, $"{a.MemberName}: {a.Tool}")));
        }
        return new OrgSummaryFacts(computer, teams, workflows, asks, Problems());
    }

    /// <summary>Manager or Member, as the bus keeps it (the member's instructions are not sent).</summary>
    private static string Role(MemberRow m) =>
        string.Equals(m.Role, nameof(MemberRole.Manager), StringComparison.OrdinalIgnoreCase) ? nameof(MemberRole.Manager) : nameof(MemberRole.Member);

    /// <summary>What a running agent is doing: its open card on the board, else the status line it last gave.</summary>
    private string? Doing(string team, AgentCard c) =>
        board?.OpenCardOf(team, c.Member.Name) is { } card ? $"card {card.Id}: {card.Title}" : c.StatusLine;

    /// <summary>The summary's state of a workflow (OrgSummary.WorkflowStates).</summary>
    internal static string StateOf(WorkflowSummary w, bool askingThePerson, bool blocked, bool ownerFailed) => w.State switch
    {
        "completed" => OrgSummary.Completed,
        "closed" => OrgSummary.Closed,
        "expired" => OrgSummary.Expired,
        _ when askingThePerson => OrgSummary.WaitingOnPerson,
        _ when w.Busy.Count > 0 => OrgSummary.Running,
        _ when blocked => OrgSummary.Blocked,
        _ when w.Undeclared && ownerFailed => OrgSummary.Failed,
        _ when w.Undeclared => OrgSummary.NobodyWorking,
        // Open with a wake pending: someone is about to work it.
        _ => OrgSummary.Running,
    };

    /// <summary>The warnings and errors of the last hour, from the activity log and the app's own diagnostics.</summary>
    private List<OrgProblem> Problems()
    {
        var list = new List<OrgProblem>();
        if (logs is null) return list;
        var from = (_clock.GetUtcNow() - OrgSummary.ProblemsFor).ToString("O", CultureInfo.InvariantCulture);
        try
        {
            foreach (var row in Rows(logs.Query(new Logs.LogQuery(Logs.LogSources.Activity, From: from, Limit: Logs.LogService.MaxPage))))
            {
                var source = Str(row, "source") ?? "";
                var ev = Str(row, "event") ?? "";
                if (ProblemLevel(source, ev) is not { } level || ParseTime(Str(row, "at")) is not { } at) continue;
                list.Add(new OrgProblem(source, Str(row, "team"), Str(row, "member"), at, level, $"{ev}: {Str(row, "details")}".TrimEnd(' ', ':')));
            }
            foreach (var row in Rows(logs.Query(new Logs.LogQuery(Logs.FileLogs.Diagnostics.Id, From: from, Limit: Logs.LogService.MaxPage))))
            {
                var text = Str(row, "text") ?? "";
                if (DiagnosticLevel(text) is not { } level || ParseTime(Str(row, "at")) is not { } at) continue;
                list.Add(new OrgProblem("Diagnostics", null, null, at, level, $"{Str(row, "origin") ?? "core"}: {text}"));
            }
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or IOException or Microsoft.Data.Sqlite.SqliteException)
        {
            // The summary still answers without its problems.
        }
        return list;
    }

    /// <summary>An activity row's level: a run that failed is an error; one cut short, and a refused network call, a warning. Model usage is never read.</summary>
    internal static string? ProblemLevel(string source, string ev) => source switch
    {
        "Runs" when ev is "run failed" or "run start_failed" or "run credential" => "error",
        "Runs" when ev is "run timeout" or "run interrupted" => "warning",
        "Network" when Has(ev, "denied") || Has(ev, "refused") || Has(ev, "unauthorized") => "warning",
        _ => null,
    };

    /// <summary>A diagnostics line's level, from its words: error, exception or failed; else warning or "could not".</summary>
    internal static string? DiagnosticLevel(string text) =>
        Has(text, "error") || Has(text, "exception") || Has(text, "failed") ? "error"
        : Has(text, "warning") || Has(text, "warn:") || Has(text, "could not") ? "warning"
        : null;

    private static bool Has(string text, string word) => text.Contains(word, StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<JsonObject> Rows(JsonObject page) => (page["rows"] as JsonArray ?? []).OfType<JsonObject>();

    private static string? Str(JsonObject row, string key) => row[key] is JsonValue v ? v.ToString() : null;

    private static DateTimeOffset? ParseTime(string? text) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t) ? t : null;

    private static string? Field(Message m, string field)
    {
        try
        {
            return JsonNode.Parse(m.Payload) is JsonObject o && o[field] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
