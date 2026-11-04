using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using Harness.Contracts;
using Harness.Host.Auth;
using ModelContextProtocol.Server;

namespace Harness.Host;

public sealed partial class PlatformMcpTools
{
    public const string ReportActions = "teams, team, workflows, outcomes, human, spend or accounts";

    /// <summary>The sentence shown wherever Actual Spend and Estimated Execution Cost appear together, word for
    /// word as the Spend page shows it (<c>SpendSentence</c> in <c>web/src/lib/spend.ts</c>).</summary>
    public const string SpendSentence =
        "Actual Spend is what you paid vendors. Estimated Execution Cost is agent and plugin time at your estimated "
        + "hourly rates. They are different measurements and need not reconcile.";

    private const string EstimateWords =
        "an estimate of agent and plugin time at your estimated hourly rates, never Actual Spend. `cost` null is "
        + "Estimate unavailable (nothing priced), never 0; `legacyRates` names a reconstructed or fallback-at-upgrade "
        + "rate; money is in `money.currency`, never converted.";

    /// <summary>
    /// READ-ONLY REPORTING over the figures the screens and the Spend page's files show: every action is one
    /// GET a person's screen makes, sent with the caller's own credential, its answer passed on unchanged under
    /// one heading line in the product's words. So a figure here is the route's figure - there is no second
    /// calculation - and the team gate bounds it exactly as it bounds the route.
    /// </summary>
    [McpServerTool(Name = "report"), Description(
        "Read-only figures for a report, the same ones the screens and the Spend page's files show. action is "
        + ReportActions + ". teams: Estimated Execution Cost and Agent Hours for each team you reach; team: one "
        + "team's, by outcome, member and workflow; workflows: each of a team's workflows, all time; outcomes: by "
        + "outcome and Unattributed, with Human Interventions and Human Attention Time; human: a team's Human "
        + "Interventions and Human Attention Time over its last eight weeks; spend: Actual Spend and Projected for "
        + "the period; accounts: the billing accounts. spend and accounts need the Spend permit, which an agent "
        + "holds only while a person has turned on reporting.agentsMayReadSpend. The period is from and to "
        + "(YYYY-MM-DD, UTC, to exclusive), the current UTC month when neither is sent. Nothing here writes. "
        + "This is harness report. Do not request /api yourself.")]
    public async Task<string> Report(
        [Description(ReportActions + ".")] string action,
        [Description("Team id, for team, workflows and human. Required for a Concierge; a member's own team when omitted.")]
        string? team = null,
        [Description("The period's first day, YYYY-MM-DD (midnight UTC). Sent with to; neither sent is the current UTC month.")]
        string? from = null,
        [Description("The day after the period's last, YYYY-MM-DD (midnight UTC, exclusive).")] string? to = null,
        [Description("For teams and team: day (the default) or month.")] string? bucket = null,
        CancellationToken cancellationToken = default)
    {
        var verb = (action ?? "").Trim().ToLowerInvariant();
        if (verb is not ("teams" or "team" or "workflows" or "outcomes" or "human" or "spend" or "accounts"))
        {
            return $"Refused: the report tool's action is {ReportActions}.";
        }

        if (http.HttpContext is not { } context || PrincipalClaims.From(context.User) is not { } principal)
        {
            return "Refused: the report tool needs a signed-in caller.";
        }

        if (verb is "spend" or "accounts" && principal.Kind != PrincipalKind.User && !principal.May(Permits.Spend))
        {
            return "Refused: the report tool's " + verb + ": " + SpendReading.Refused;
        }

        if (WindowOf(from, to, DateTimeOffset.UtcNow) is not { } window)
        {
            return "Refused: the report tool's period is from and to, both YYYY-MM-DD with to after from, or neither "
                + "for the current UTC month.";
        }

        var (start, end) = (Day(window.From), Day(window.To));
        var period = $"{start} to {end} (UTC, to exclusive)";

        string? resolved = null;
        if (verb is "team" or "workflows" or "human")
        {
            resolved = await TeamAsync(team, cancellationToken);
            if (resolved is null) return "Refused: the report tool's " + verb + " needs a team. A Concierge has no default team.";
        }

        var teamPath = resolved is null ? "" : "/api/teams/" + Uri.EscapeDataString(resolved);
        var instant = (DateTimeOffset at) => at.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

        var (path, heading) = verb switch
        {
            "teams" => (
                "/api/teams/cost/period" + Query(("from", start), ("to", end), ("bucket", bucket)),
                $"Estimated Execution Cost and Agent Hours for each team you reach, {period}: " + EstimateWords),
            "team" => (
                teamPath + "/cost/period" + Query(("from", start), ("to", end), ("bucket", bucket)),
                $"Estimated Execution Cost and Agent Hours for team {resolved}, {period}, by outcome (Unattributed for "
                + "work serving none), by member (agent and plugin members apart; a plugin member's `unpriced` is "
                + "`not-priced` or `unavailable`) and by workflow: " + EstimateWords),
            "workflows" => (
                teamPath + "/cost/workflows",
                $"Agent Hours and Estimated Execution Cost for each workflow of team {resolved}, all time: " + EstimateWords),
            "outcomes" => (
                "/api/outcomes" + Query(("from", instant(window.From)), ("to", instant(window.To))),
                $"Estimated Execution Cost, Agent Hours, Human Interventions and Human Attention Time by outcome, and "
                + $"Unattributed (`noOutcome`) for work serving none, {period}, across every team: " + EstimateWords
                + " A Human Attention Time of null is no measured time."),
            "human" => (
                teamPath + "/cost",
                $"Human Interventions and Human Attention Time, with Estimated Execution Cost and Agent Hours, for team "
                + $"{resolved} over its last eight weeks (the period is not read): " + EstimateWords
                + " A Human Attention Time of null is no measured time."),
            "spend" => (
                "/api/spend/overview" + Query(("from", start), ("to", end)),
                $"Actual Spend (confirmed expenses, per currency, never converted or added across currencies) and "
                + $"Projected (recurring charges not yet invoiced, never in Actual Spend), {period}; `estimate` is "
                + "Estimated Execution Cost and Agent Hours for the same period, across every team. " + SpendSentence),
            _ => (
                "/api/billing-accounts",
                "The billing accounts, each in its own currency: what Actual Spend is recorded against. Nothing here "
                + "is an estimate."),
        };

        var answer = await SendAsync(HttpMethod.Get, path, null, cancellationToken);
        var split = answer.Split(Environment.NewLine, 2);
        if (!split[0].StartsWith("HTTP 2", StringComparison.Ordinal)) return ReportRefusal(verb, split);

        return split[0] + Environment.NewLine + heading + Environment.NewLine + (split.Length == 2 ? split[1] : "");
    }

    /// <summary>The period an action reads: both days, or neither for the current UTC month.</summary>
    internal static (DateTimeOffset From, DateTimeOffset To)? WindowOf(string? from, string? to, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(from) && string.IsNullOrWhiteSpace(to))
        {
            var first = new DateTimeOffset(now.UtcDateTime.Year, now.UtcDateTime.Month, 1, 0, 0, 0, TimeSpan.Zero);
            return (first, first.AddMonths(1));
        }

        if (!DateOnly.TryParseExact((from ?? "").Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var a)
            || !DateOnly.TryParseExact((to ?? "").Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var b)
            || b <= a)
        {
            return null;
        }

        return (new DateTimeOffset(a.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero), new DateTimeOffset(b.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero));
    }

    private static string Day(DateTimeOffset at) => at.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string ReportRefusal(string verb, string[] split)
    {
        var sentence = split.Length == 2 ? split[1] : split[0];
        try
        {
            using var document = JsonDocument.Parse(sentence);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.String)
            {
                sentence = error.GetString()!;
            }
        }
        catch (JsonException)
        {
        }

        return $"Refused: the report tool's {verb}: {(string.IsNullOrWhiteSpace(sentence) ? split[0] : sentence)}";
    }
}
