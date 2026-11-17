using System.Globalization;
using System.Text.Json.Nodes;

namespace Yawble.Core.Link;

// B048: the facts org.summary is built from. Only what the app already tracks; by the person's rule there is no token,
// cost, usage, price or value figure anywhere in here, even though run records hold token counts.

/// <summary>This computer: its name, the name it has on the cloud site, the build, the OS, when the core started, and its run slots.</summary>
public sealed record OrgComputer(string Name, string? Label, string Version, string Platform, DateTimeOffset StartedAt, int SlotsUsed, int SlotsTotal, int QueuedRuns);

/// <summary>An agent that is running now: who, its role, what it is doing (its card, or its workflow), since when.</summary>
public sealed record OrgRunningAgent(string Member, string Role, string? Doing, DateTimeOffset? Since);

/// <summary>One agent of a team, as the summary counts it: state running | queued | idle, and whether its last run failed.</summary>
public sealed record OrgAgent(string Member, string Role, string State, bool LastRunFailed, string? Doing = null, DateTimeOffset? Since = null);

/// <summary>A team and its agents; <paramref name="WorkflowExpiryDays"/> is its setting (B085: an open workflow nobody works expires after it).</summary>
public sealed record OrgTeam(string Name, bool Paused, bool Archived, IReadOnlyList<OrgAgent> Agents,
    int WorkflowExpiryDays = Bus.BusEngine.DefaultWorkflowExpiryDays);

/// <summary>
/// A workflow. <paramref name="State"/> is one of <see cref="OrgSummary.WorkflowStates"/>; <paramref name="Item"/> is the
/// backlog item's label (B048) when the workflow came from one.
/// </summary>
public sealed record OrgWorkflow(long Id, string Team, string Title, string? Item, string? ItemTitle, string State, int CardsDone, int CardsTotal,
    DateTimeOffset StartedAt, DateTimeOffset LastActivity, string? Owner, DateTimeOffset? EndedAt = null, DateTimeOffset? ExpiredAt = null,
    DateTimeOffset? ExpiresAt = null);

/// <summary>Something waiting for the person: <paramref name="Kind"/> is decision | toolCall | network | tool.</summary>
public sealed record OrgAsk(string Kind, string? Team, string Title);

/// <summary>A warning or error log entry: <paramref name="Level"/> is warning | error.</summary>
public sealed record OrgProblem(string Source, string? Team, string? Member, DateTimeOffset At, string Level, string Message);

/// <summary>Everything one snapshot is built from.</summary>
public sealed record OrgSummaryFacts(OrgComputer Computer, IReadOnlyList<OrgTeam> Teams, IReadOnlyList<OrgWorkflow> Workflows,
    IReadOnlyList<OrgAsk> Asks, IReadOnlyList<OrgProblem> Problems);

/// <summary>Where the facts come from: the bus, the board, the asks and the logs in the app; fakes in the tests.</summary>
public interface IOrgSummarySource
{
    Task<OrgSummaryFacts> ReadAsync(CancellationToken ct);
}

/// <summary>
/// org.summary's snapshot, contract version 1 (docs/cloud-link.md): one JSON object with summaryVersion, at, and the
/// sections computer, teams, workflows, needsYou and problems. The builder applies the contract's windows (workflows:
/// open, or ended in the last 24 h; problems: the last 20 of the last hour) and redacts every free text the way the
/// Cloud Link's other reads do.
/// </summary>
public static class OrgSummary
{
    public const int Version = 1;

    /// <summary>The snapshot's sections, in order: a change push names the ones that changed.</summary>
    public static readonly IReadOnlyList<string> Sections = ["computer", "teams", "workflows", "needsYou", "problems"];

    public const string Running = "running", WaitingOnPerson = "waitingOnPerson", Blocked = "blocked", NobodyWorking = "nobodyWorking",
        Completed = "completed", Failed = "failed", Closed = "closed", Expired = "expired";

    /// <summary>Every workflow state. Only completed, closed and expired are ended; the rest, failed included, are open.</summary>
    public static readonly IReadOnlyList<string> WorkflowStates = [Running, WaitingOnPerson, Blocked, NobodyWorking, Completed, Failed, Closed, Expired];

    public static readonly TimeSpan EndedWorkflowsFor = TimeSpan.FromHours(24);
    public static readonly TimeSpan ProblemsFor = TimeSpan.FromHours(1);
    public const int MaxProblems = 20;
    public const int MaxTitles = 5;
    private const int MaxTitle = 100;
    private const int MaxMessage = 200;

    /// <summary>
    /// Only completed (declared done), closed (closed by a person) and expired (closed by the app, B085) are ended; every
    /// other state, failed included, is open and stays in the snapshot however old its last activity (docs/cloud-link.md).
    /// </summary>
    public static bool Ended(string state) => state is Completed or Closed or Expired;

    public static JsonObject Build(OrgSummaryFacts facts, DateTimeOffset now) => new()
    {
        ["summaryVersion"] = Version,
        ["at"] = Time(now),
        ["computer"] = ComputerOf(facts.Computer, now),
        ["teams"] = TeamsOf(facts.Teams),
        ["workflows"] = WorkflowsOf(facts.Workflows, now),
        ["needsYou"] = NeedsYouOf(facts.Asks),
        ["problems"] = ProblemsOf(facts.Problems, now),
    };

    private static JsonObject ComputerOf(OrgComputer c, DateTimeOffset now) => new()
    {
        ["name"] = c.Name,
        ["label"] = c.Label,
        ["yawbleVersion"] = c.Version,
        ["platform"] = c.Platform,
        ["startedAt"] = Time(c.StartedAt),
        ["uptimeSeconds"] = (long)Math.Max(0, (now - c.StartedAt).TotalSeconds),
        ["runSlots"] = new JsonObject { ["used"] = c.SlotsUsed, ["total"] = c.SlotsTotal },
        ["queuedRuns"] = c.QueuedRuns,
    };

    private static JsonArray TeamsOf(IReadOnlyList<OrgTeam> teams) => new(teams.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).Select(t =>
    {
        var running = t.Agents.Where(a => a.State == "running").ToList();
        var idle = t.Agents.Where(a => a.State is not ("running" or "queued")).ToList();
        return (JsonNode)new JsonObject
        {
            ["name"] = t.Name,
            ["paused"] = t.Paused,
            ["archived"] = t.Archived,
            ["workflowExpiryDays"] = t.WorkflowExpiryDays,
            ["agents"] = new JsonObject
            {
                ["total"] = t.Agents.Count,
                ["running"] = running.Count,
                ["queued"] = t.Agents.Count(a => a.State == "queued"),
                // An idle agent whose last run failed counts as errored, not idle.
                ["idle"] = idle.Count(a => !a.LastRunFailed),
                ["errored"] = idle.Count(a => a.LastRunFailed),
            },
            ["running"] = new JsonArray(running.Select(a => (JsonNode)new JsonObject
            {
                ["member"] = a.Member,
                ["role"] = a.Role,
                ["doing"] = a.Doing is null ? null : Title(a.Doing),
                ["since"] = a.Since is { } s ? Time(s) : null,
            }).ToArray()),
        };
    }).ToArray());

    private static JsonArray WorkflowsOf(IReadOnlyList<OrgWorkflow> workflows, DateTimeOffset now) => new(workflows
        .Where(w => !Ended(w.State) || now - w.LastActivity <= EndedWorkflowsFor)
        .OrderByDescending(w => w.LastActivity).ThenByDescending(w => w.Id)
        .Select(w => (JsonNode)new JsonObject
        {
            ["id"] = w.Id,
            ["team"] = w.Team,
            ["title"] = Title(w.Title),
            ["item"] = w.Item is null ? null : new JsonObject { ["id"] = w.Item, ["title"] = w.ItemTitle is null ? null : Title(w.ItemTitle) },
            ["state"] = w.State,
            ["cards"] = new JsonObject { ["done"] = w.CardsDone, ["total"] = w.CardsTotal },
            ["startedAt"] = Time(w.StartedAt),
            ["lastActivityAt"] = Time(w.LastActivity),
            ["owner"] = w.Owner,
            // B085: when it ended (an expired one: its last real activity, not the expiry) and when the app expired it.
            ["endedAt"] = w.EndedAt is { } e ? Time(e) : null,
            ["expiredAt"] = w.ExpiredAt is { } x ? Time(x) : null,
            // B085: when it expires if nothing happens, as on the bus.workflows item; null while it is worked, and once ended.
            ["expiresAt"] = w.ExpiresAt is { } d ? Time(d) : null,
        }).ToArray());

    /// <summary>needsYou: the counts across every team, and per team the counts and the first few short titles of each kind.</summary>
    private static JsonObject NeedsYouOf(IReadOnlyList<OrgAsk> asks)
    {
        string[] kinds = ["decision", "toolCall", "network", "tool"];
        string[] keys = ["decisions", "toolCalls", "networkRequests", "toolRequests"];
        JsonObject Counts(IEnumerable<OrgAsk> of, bool titles)
        {
            var list = of.ToList();
            var o = new JsonObject { ["total"] = list.Count };
            for (var i = 0; i < kinds.Length; i++)
            {
                var mine = list.Where(a => a.Kind == kinds[i]).ToList();
                o[keys[i]] = titles
                    ? new JsonObject { ["count"] = mine.Count, ["titles"] = new JsonArray(mine.Take(MaxTitles).Select(a => (JsonNode)Title(a.Title)).ToArray()) }
                    : mine.Count;
            }
            return o;
        }
        var all = Counts(asks, titles: false);
        all["teams"] = new JsonArray(asks.GroupBy(a => a.Team ?? "", StringComparer.OrdinalIgnoreCase).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var o = Counts(g, titles: true);
                o.Insert(0, "team", g.Key.Length == 0 ? null : g.First().Team);
                return (JsonNode)o;
            }).ToArray());
        return all;
    }

    private static JsonArray ProblemsOf(IReadOnlyList<OrgProblem> problems, DateTimeOffset now) => new(problems
        .Where(p => p.Level is "warning" or "error" && p.At <= now && now - p.At <= ProblemsFor)
        .OrderByDescending(p => p.At).Take(MaxProblems)
        .Select(p => (JsonNode)new JsonObject
        {
            ["source"] = p.Source,
            ["team"] = p.Team,
            ["member"] = p.Member,
            ["at"] = Time(p.At),
            ["level"] = p.Level,
            ["message"] = Line(p.Message, MaxMessage),
        }).ToArray());

    /// <summary>
    /// The sections of <paramref name="next"/> that differ from <paramref name="last"/> (all of them when there is no
    /// last). The computer's uptime ticks every second: it alone is not a change.
    /// </summary>
    public static IReadOnlyList<string> Changed(JsonObject? last, JsonObject next) =>
        Sections.Where(s => last is null || Comparable(s, last[s]) != Comparable(s, next[s])).ToList();

    private static string Comparable(string section, JsonNode? node)
    {
        if (section != "computer" || node is not JsonObject o) return node?.ToJsonString() ?? "null";
        var copy = o.DeepClone().AsObject();
        copy.Remove("uptimeSeconds");
        return copy.ToJsonString();
    }

    /// <summary>org.summaryChanged's data: the version, the time, which sections changed, and those sections in full.</summary>
    public static JsonObject ChangeOf(JsonObject next, IReadOnlyList<string> changed)
    {
        var sections = new JsonObject();
        foreach (var s in changed) sections[s] = next[s]?.DeepClone();
        return new JsonObject
        {
            ["summaryVersion"] = Version,
            ["at"] = next["at"]?.DeepClone(),
            ["full"] = changed.Count == Sections.Count,
            ["changed"] = new JsonArray(changed.Select(s => (JsonNode)s).ToArray()),
            ["sections"] = sections,
        };
    }

    /// <summary>A free text as the summary shows it: redacted like the Cloud Link's other reads, one line, cut short.</summary>
    public static string Line(string text, int max)
    {
        var scrubbed = Connections.SecretScrubber.Scrub(Concierge.ConciergeReadTools.Scrub(text));
        var line = scrubbed.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";
        return line.Length <= max ? line : line[..(max - 1)] + "…";
    }

    private static string Title(string text) => Line(text, MaxTitle);

    private static string Time(DateTimeOffset t) => t.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
}
