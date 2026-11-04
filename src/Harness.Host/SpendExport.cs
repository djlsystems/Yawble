using System.Globalization;
using System.Text;
using Harness.Contracts;

namespace Harness.Host;

/// <summary>
/// THE SPEND PAGE'S CSV FILES for one period <c>[from, to)</c>, the period <see cref="SpendOverview.PeriodOf"/>
/// reads. Each file is built from the rows and figures the existing reads already compute - nothing is
/// priced, converted or inferred here:
///
/// <list type="bullet">
/// <item><b>Billing accounts</b>: every account, active and inactive, as stored.</item>
/// <item><b>Expenses</b>: every row of every correction chain one of whose rows' service period starts in
/// the period - current, voided and superseded alike - with its status and the ids either side of it, so the
/// chain can be rebuilt, and whether it counts in Actual Spend (<see cref="SpendOverview.Counts"/> and
/// <see cref="SpendOverview.InPeriod"/>, the overview's own rule).</item>
/// <item><b>Period summary</b>: <see cref="SpendOverview.Overview"/> for the period, row by row - Actual
/// Spend and Projected per currency, each billing type per currency, and Estimated Execution Cost with
/// Agent Hours. The same object <c>GET /api/spend/overview</c> answers, never a second calculation.</item>
/// <item><b>Estimated Execution Cost by team and by outcome</b>: each run's part inside the period priced by
/// <see cref="ExecutionCost.Tally"/>, the one pricing path; a team's runs read as every team figure reads them
/// (<see cref="TeamCost.RunsByTeam(IEnumerable{ExecutionCost.Run}, IReadOnlyDictionary{string, long}, DateTimeOffset, DateTimeOffset)"/>),
/// an outcome's as <see cref="ExecutionCost.CostsOf"/> attributes them, Unattributed included. One row per
/// team or outcome and rate source, agent and plugin apart.</item>
/// </list>
///
/// <para>
/// THE FORMAT: a header row always, even with no data rows; snake_case column names that do not change;
/// RFC 4180 (comma-separated, CRLF line ends, a field holding a comma, quote, CR or LF in double quotes with
/// its quotes doubled); timestamps as ISO-8601 UTC (<c>yyyy-MM-ddTHH:mm:ss.fffZ</c>); currencies as ISO 4217
/// codes; ids as stored (GUIDs for accounts, expenses and outcomes); amounts as plain decimal text - stored
/// amounts exactly as stored, estimates to the cent - with no symbol and no thousands separator.
/// </para>
///
/// <para>
/// FORMULA INJECTION: a text cell whose first character is <c>=</c>, <c>+</c>, <c>-</c>, <c>@</c>, a tab or a
/// carriage return is written with a single quote (<c>'</c>) before it, so a spreadsheet reads it as text and
/// never as a formula (<see cref="Neutral"/>). A plain number - an optional minus, digits and an optional
/// fraction, such as <c>-12.50</c> - is a number and is written as it is.
/// </para>
/// </summary>
public static class SpendExport
{
    /// <summary>The files, by the name the route and the download carry.</summary>
    public const string Accounts = "billing-accounts";
    public const string Expenses = "expenses";
    public const string Summary = "period-summary";
    public const string Estimates = "estimated-execution-cost";

    public static readonly IReadOnlyList<string> Files = [Accounts, Expenses, Summary, Estimates];

    public static readonly IReadOnlyList<string> AccountColumns =
    [
        "instance_id", "period_start", "period_end", "billing_account_id", "name", "vendor", "billing_type",
        "fixed_or_variable", "currency", "billing_period", "recurring_amount", "effective_from", "effective_to",
        "is_active", "description", "created_by", "created_at", "updated_at",
    ];

    public static readonly IReadOnlyList<string> ExpenseColumns =
    [
        "instance_id", "period_start", "period_end", "expense_id", "billing_account_id", "billing_account_name", "vendor",
        "kind", "status", "amount", "currency", "service_period_start", "service_period_end", "supersedes_expense_id",
        "superseded_by_expense_id", "is_current", "counts_in_actual_spend", "reference", "note", "recorded_by",
        "recorded_at",
    ];

    public static readonly IReadOnlyList<string> SummaryColumns =
    [
        "instance_id", "period_start", "period_end", "measure", "billing_type", "fixed_or_variable", "currency",
        "amount", "item_count", "agent_seconds", "agent_hours", "agent_rate", "rate_source",
    ];

    public static readonly IReadOnlyList<string> EstimateColumns =
    [
        "instance_id", "period_start", "period_end", "rollup", "team_id", "team_name", "outcome_id", "outcome_name",
        "rate_source", "currency", "estimated_agent_cost", "estimated_plugin_cost", "estimated_execution_cost",
        "agent_seconds", "agent_hours", "agent_rate",
    ];

    /// <summary>The summary's <c>measure</c> values.</summary>
    public const string MeasureActual = "actual_spend";
    public const string MeasureProjected = "projected";
    public const string MeasureActualByType = "actual_spend_by_billing_type";
    public const string MeasureProjectedByType = "projected_by_billing_type";
    public const string MeasureEstimate = "estimated_execution_cost";

    /// <summary>The estimates file's <c>rollup</c> values.</summary>
    public const string RollupTeam = "team";
    public const string RollupOutcome = "outcome";

    /// <summary>The order rate sources are listed in: a snapshot's own first, then the rate's history, then none.</summary>
    public static readonly IReadOnlyList<string> RateSourceOrder =
    [
        Harness.Messaging.ExecutionSnapshotFacts.RateSourceMember,
        Harness.Messaging.ExecutionSnapshotFacts.RateSourceTenant,
        Harness.Messaging.RateEpochFacts.RateSourceReconstructed,
        Harness.Messaging.RateEpochFacts.RateSourceFallback,
        Harness.Messaging.ExecutionSnapshotFacts.RateSourceNone,
    ];

    public sealed record Period(DateTimeOffset From, DateTimeOffset To, string InstanceId);

    public sealed record TeamName(string Id, string Name);

    /// <summary>The download's file name: <c>spend-&lt;file&gt;-&lt;from&gt;-to-&lt;last day&gt;.csv</c>.</summary>
    public static string FileName(string file, DateTimeOffset from, DateTimeOffset to) =>
        $"spend-{file}-{Date(from)}-to-{Date(to.AddDays(-1))}.csv";

    // ---- the files -------------------------------------------------------------------------------------

    public static string AccountsCsv(Period period, IReadOnlyList<BillingAccount> accounts)
    {
        var csv = new Writer(AccountColumns);
        foreach (var a in accounts.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ThenBy(a => a.Id, StringComparer.Ordinal))
        {
            csv.Row(
                Text(a.InstanceId), Stamp(period.From), Stamp(period.To), Text(a.Id), Text(a.Name), Text(a.Vendor),
                Text(a.BillingType), Text(SpendOverview.GroupOf(a.BillingType)), Text(a.Currency), Text(a.BillingPeriod),
                Number(a.RecurringAmount), Stamp(a.EffectiveFrom), Stamp(a.EffectiveTo), Bool(a.Active), Text(a.Description),
                Text(a.CreatedBy), Stamp(a.CreatedAt), Stamp(a.UpdatedAt));
        }

        return csv.ToString();
    }

    /// <summary>
    /// THE EXPENSES IN THE PERIOD WITH THEIR CHAINS: every row of each correction chain (a row and every row that
    /// supersedes it, or that it supersedes, transitively) that has a row whose service period starts in the
    /// period - so a replace that moved the service period out still shows where it came from. In the order
    /// they were recorded.
    /// </summary>
    public static IReadOnlyList<Expense> ExpensesIn(IReadOnlyList<Expense> expenses, DateTimeOffset from, DateTimeOffset to)
    {
        var byId = expenses.ToDictionary(e => e.Id, StringComparer.Ordinal);

        string Root(Expense e)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            while (e.Supersedes is { } older && byId.TryGetValue(older, out var parent) && seen.Add(older)) e = parent;
            return e.Id;
        }

        var chains = expenses.GroupBy(Root, StringComparer.Ordinal).ToList();
        var shown = chains
            .Where(chain => chain.Any(e => SpendOverview.InPeriod(e, from, to)))
            .SelectMany(chain => chain)
            .Select(e => e.Id)
            .ToHashSet(StringComparer.Ordinal);

        return expenses.Where(e => shown.Contains(e.Id)).ToList();
    }

    public static string ExpensesCsv(Period period, IReadOnlyList<BillingAccount> accounts, IReadOnlyList<Expense> expenses)
    {
        var byId = accounts.ToDictionary(a => a.Id, StringComparer.Ordinal);
        var csv = new Writer(ExpenseColumns);

        foreach (var e in ExpensesIn(expenses, period.From, period.To))
        {
            var account = byId.GetValueOrDefault(e.AccountId);
            csv.Row(
                Text(period.InstanceId), Stamp(period.From), Stamp(period.To), Text(e.Id), Text(e.AccountId), Text(account?.Name),
                Text(account?.Vendor), Text(e.Kind), Text(e.Status), Number(e.Amount), Text(e.Currency),
                Stamp(e.ServicePeriodStart), Stamp(e.ServicePeriodEnd), Text(e.Supersedes), Text(e.SupersededBy),
                Bool(e.Current), Bool(SpendOverview.Counts(e) && SpendOverview.InPeriod(e, period.From, period.To)),
                Text(e.Reference), Text(e.Note), Text(e.RecordedBy), Stamp(e.RecordedAt));
        }

        return csv.ToString();
    }

    /// <summary>
    /// THE PERIOD SUMMARY: <paramref name="overview"/>'s period figures, one row each - Actual Spend per currency,
    /// Projected per currency, each billing type's actual and projected per currency, and Estimated Execution Cost
    /// with Agent Hours. <paramref name="rateSources"/> are the rate sources of the runs priced into the estimate.
    /// </summary>
    public static string SummaryCsv(Period period, SpendOverview.Overview overview, IReadOnlyList<string> rateSources)
    {
        var csv = new Writer(SummaryColumns);
        string[] Head(string measure, string? type, string? group, string? currency) =>
            [Text(period.InstanceId), Stamp(period.From), Stamp(period.To), measure, Text(type), Text(group), Text(currency)];

        foreach (var a in overview.Actual)
        {
            csv.Row([.. Head(MeasureActual, null, null, a.Currency), Money(a.Amount), Count(a.Count), "", "", "", ""]);
        }

        foreach (var p in overview.Projected)
        {
            csv.Row([.. Head(MeasureProjected, null, null, p.Currency), Money(p.Amount), Count(p.Count), "", "", "", ""]);
        }

        foreach (var t in overview.BillingTypes.Where(t => t.Actual is not null))
        {
            csv.Row([.. Head(MeasureActualByType, t.BillingType, t.Group, t.Currency), Money(t.Actual), "", "", "", "", ""]);
        }

        foreach (var t in overview.BillingTypes.Where(t => t.Projected is not null))
        {
            csv.Row([.. Head(MeasureProjectedByType, t.BillingType, t.Group, t.Currency), Money(t.Projected), "", "", "", "", ""]);
        }

        var estimate = overview.Estimate;
        csv.Row(
        [
            .. Head(MeasureEstimate, null, null, estimate.Currency.ToUpperInvariant()), Cents(estimate.Cost), "",
            Seconds(estimate.AgentSeconds), Hours(estimate.AgentSeconds), Cents(estimate.AgentRate), Text(string.Join(";", rateSources)),
        ]);

        return csv.ToString();
    }

    /// <summary>The rate sources of <paramref name="runs"/> with time inside <c>[from, to)</c>, in <see cref="RateSourceOrder"/>.</summary>
    public static IReadOnlyList<string> RateSourcesOf(IEnumerable<ExecutionCost.Run> runs, DateTimeOffset from, DateTimeOffset to) =>
        Ordered(runs.Where(r => r.SecondsIn(from, to) > 0).Select(SourceOf));

    /// <summary>
    /// ESTIMATED EXECUTION COST BY TEAM AND BY OUTCOME over the period. By team: each team in <paramref name="teams"/>
    /// (with its floor in <paramref name="floors"/>) that has runs in the period, by name. By outcome: every run in the
    /// period, under the outcome it serves (<see cref="ExecutionCost.Run.OutcomeId"/>), Unattributed first, then the
    /// outcomes in the order they were created. Each split by rate source, one row each; a row's money is its runs'
    /// parts in the period, each priced on its own, agent and plugin apart.
    /// </summary>
    public static string EstimatesCsv(
        Period period, IReadOnlyList<ExecutionCost.Run> runs, IReadOnlyList<Outcome> outcomes,
        IReadOnlyList<TeamName> teams, IReadOnlyDictionary<string, long> floors, string currency)
    {
        var csv = new Writer(EstimateColumns);
        var inPeriod = runs.Where(r => r.Overlaps(period.From, period.To)).ToList();

        var byTeam = TeamCost.RunsByTeam(inPeriod, floors, period.From, period.To);
        foreach (var team in teams.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ThenBy(t => t.Id, StringComparer.OrdinalIgnoreCase))
        {
            if (!byTeam.TryGetValue(team.Id, out var mine)) continue;
            foreach (var (source, part) in BySource(mine, period, currency))
            {
                csv.Row([Text(period.InstanceId), Stamp(period.From), Stamp(period.To), RollupTeam, Text(team.Id), Text(team.Name), "", "", .. part.Cells(source)]);
            }
        }

        var byId = outcomes.ToDictionary(o => o.Id, StringComparer.Ordinal);
        var served = inPeriod
            .GroupBy(r => r.OutcomeId)
            .Select(g => (Outcome: g.Key is null ? null : byId.GetValueOrDefault(g.Key), Id: g.Key, Runs: g.ToList()))
            .OrderBy(e => e.Id is null ? 0 : 1)
            .ThenBy(e => e.Outcome?.CreatedAt)
            .ThenBy(e => e.Id, StringComparer.Ordinal);

        foreach (var (outcome, id, mine) in served)
        {
            foreach (var (source, part) in BySource(mine, period, currency))
            {
                csv.Row(
                [
                    Text(period.InstanceId), Stamp(period.From), Stamp(period.To), RollupOutcome, "", "", Text(id),
                    Text(id is null ? OutcomeFigures.UnattributedName : outcome?.Name ?? id), .. part.Cells(source),
                ]);
            }
        }

        return csv.ToString();
    }

    /// <summary>One row's figures: the agent runs' and the plugin runs' parts in the period, each a <see cref="ExecutionCost.Tally"/>.</summary>
    private sealed class Part(string currency)
    {
        public ExecutionCost.Tally Agent { get; } = new(currency);
        public ExecutionCost.Tally Plugin { get; } = new(currency);

        public decimal? Execution => Agent.Cost is null && Plugin.Cost is null ? null : (Agent.Cost ?? 0) + (Plugin.Cost ?? 0);

        public string[] Cells(string source) =>
        [
            Text(source), Text(Agent.Currency.ToUpperInvariant()), Cents(Agent.Cost), Cents(Plugin.Cost), Cents(Execution),
            Seconds(Agent.AgentSeconds), Hours(Agent.AgentSeconds), Cents(Agent.AgentRate),
        ];
    }

    private static IEnumerable<(string Source, Part Part)> BySource(IEnumerable<ExecutionCost.Run> runs, Period period, string currency)
    {
        var parts = new Dictionary<string, Part>(StringComparer.Ordinal);
        foreach (var run in runs)
        {
            var seconds = run.SecondsIn(period.From, period.To);
            if (seconds <= 0) continue;

            var source = SourceOf(run);
            if (!parts.TryGetValue(source, out var part)) parts[source] = part = new Part(currency);
            (run.IsAgent ? part.Agent : part.Plugin).AddSpan(run, seconds);
        }

        return Ordered(parts.Keys).Select(s => (s, parts[s]));
    }

    private static string SourceOf(ExecutionCost.Run run) => run.RateSource ?? Harness.Messaging.ExecutionSnapshotFacts.RateSourceNone;

    private static IReadOnlyList<string> Ordered(IEnumerable<string> sources) =>
        sources.Distinct(StringComparer.Ordinal)
            .OrderBy(s => RateSourceOrder.IndexOf(s) is var i and >= 0 ? i : RateSourceOrder.Count)
            .ThenBy(s => s, StringComparer.Ordinal)
            .ToList();

    private static int IndexOf(this IReadOnlyList<string> list, string value)
    {
        for (var i = 0; i < list.Count; i++) if (list[i] == value) return i;
        return -1;
    }

    // ---- cells -----------------------------------------------------------------------------------------

    /// <summary>
    /// A TEXT CELL A SPREADSHEET CANNOT RUN: <paramref name="text"/> with a single quote before it when its first
    /// character is <c>=</c>, <c>+</c>, <c>-</c>, <c>@</c>, a tab or a carriage return - unless it is a plain
    /// number, which is a number. Null is an empty cell.
    /// </summary>
    public static string Neutral(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        if (IsPlainNumber(text)) return text;
        return text[0] is '=' or '+' or '-' or '@' or '\t' or '\r' ? "'" + text : text;
    }

    private static bool IsPlainNumber(string text)
    {
        var i = text[0] == '-' ? 1 : 0;
        var digits = 0;
        var dot = false;
        for (; i < text.Length; i++)
        {
            if (char.IsAsciiDigit(text[i])) digits++;
            else if (text[i] == '.' && !dot && digits > 0) dot = true;
            else return false;
        }

        return digits > 0 && text[^1] != '.';
    }

    /// <summary>RFC 4180: in double quotes, with its quotes doubled, when it holds a comma, a quote, a CR or an LF.</summary>
    public static string Quoted(string cell) =>
        cell.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + cell.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : cell;

    private static string Text(string? text) => Neutral(text);

    /// <summary>A stored amount exactly as stored, when it is a plain number; anything else as text.</summary>
    private static string Number(string? stored) => Neutral(stored);

    private static string Money(decimal? amount) => amount?.ToString(CultureInfo.InvariantCulture) ?? "";

    private static string Cents(decimal? amount) => amount?.ToString("0.00", CultureInfo.InvariantCulture) ?? "";

    private static string Count(int count) => count.ToString(CultureInfo.InvariantCulture);

    private static string Seconds(double seconds) => Math.Round(seconds, 3).ToString("0.###", CultureInfo.InvariantCulture);

    private static string Hours(double seconds) => Math.Round(seconds / 3600, 4).ToString("0.####", CultureInfo.InvariantCulture);

    private static string Bool(bool value) => value ? "true" : "false";

    private static string Stamp(DateTimeOffset at) =>
        at.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private static string Stamp(string? stored) =>
        stored is null ? "" : Instant.TryParse(stored, out var at) ? Stamp(at) : Neutral(stored);

    private static string Date(DateTimeOffset at) => at.ToUniversalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>The file: its header row, then a row each, every field quoted as RFC 4180 says, CRLF line ends.</summary>
    private sealed class Writer
    {
        private readonly StringBuilder _text = new();
        private readonly int _columns;

        public Writer(IReadOnlyList<string> columns)
        {
            _columns = columns.Count;
            Line(columns);
        }

        public void Row(params string[] cells)
        {
            if (cells.Length != _columns) throw new InvalidOperationException($"A row has {cells.Length} cells; the header has {_columns}.");
            Line(cells);
        }

        private void Line(IEnumerable<string> cells) => _text.Append(string.Join(',', cells.Select(Quoted))).Append("\r\n");

        public override string ToString() => _text.ToString();
    }
}
