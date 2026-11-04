using System.Text.Json;
using Harness.Contracts;
using Harness.Host.Auth;
using Harness.Messaging;

namespace Harness.Host;

/// <summary>
/// The billing accounts and their expenses, tenant-wide, not under a team: a person's to read and write
/// (<c>HumansOnly</c> on each route; there is no Spend permit, so a machine principal is refused every
/// one of them, whatever it holds, before a missing id can be told from a real one). There is no
/// delete: an account is deactivated, and the table refuses a delete. Expenses have no edit and no
/// delete either: a correction is a new row (void or replace) that supersedes the old.
///
/// <para>
/// A billing account is a named vendor account in its own currency. It is not a connection and holds
/// no credential, and nothing here computes Actual Spend: hours are never Actual Spend.
/// </para>
/// </summary>
public static class SpendEndpoints
{
    public const string NotAnObject = "The body must be a JSON object of the billing account's fields.";

    public const string ActiveOnEdit =
        "Whether a billing account is active is changed by deactivating or reactivating it, not by an edit.";

    public static string Fixed(string field) => $"A billing account's {field} cannot be changed.";

    public static string NotText(string field) => $"A billing account's {field} must be text.";

    /// <summary>The body's fields a person may send, by their JSON name.</summary>
    private static readonly string[] Editable =
        ["name", "vendor", "billingType", "currency", "billingPeriod", "recurringAmount", "effectiveFrom", "effectiveTo", "description", "scope"];

    private const string Fields =
        "`name` and `vendor` (required text, neither unique), `billingType` (`subscription`, `metered` or "
        + "`service`), `currency` (three letters, stored upper-case, the account's own and never converted), "
        + "`billingPeriod` (`monthly` or `annual`), `recurringAmount` (a number of zero or more, stored as "
        + "canonical text, or null; it is stored only, never projected and never added to an estimate), "
        + "`effectiveFrom` (an ISO-8601 date, read as midnight UTC, or instant), `effectiveTo` (null, or at "
        + "or after `effectiveFrom`), `description` (a short note, or null) and `scope` (only `tenant`).";

    private const string Answer =
        "An account answers `id` (a lower-case GUID the store assigns), its fields, `active`, `scope`, "
        + "`instanceId` (this instance's `instance.id`, copied at create), `createdBy`, `createdAt` and "
        + "`updatedAt`.";

    public static void Map(WebApplication app)
    {
        app.MapGet("/api/billing-accounts", async (ISpendStore spend, CancellationToken ct) =>
                Results.Ok(await spend.ListAccountsAsync(ct)))
            .WithTags("Billing")
            .HumansOnly()
            .WithSummary("List the billing accounts")
            .WithDescription(
                "Every billing account, active and deactivated, oldest created first, then by id; an empty "
                + "list when there is none. " + Answer + " A person's only: no permit lets an agent read one.");

        app.MapGet("/api/billing-accounts/{id}", async (string id, ISpendStore spend, CancellationToken ct) =>
                await spend.FindAccountAsync(id, ct) is { } account
                    ? Results.Ok(account)
                    : Results.NotFound(new { error = BillingAccountRules.Missing(id) }))
            .WithTags("Billing")
            .HumansOnly()
            .WithSummary("One billing account")
            .WithDescription(Answer + " 404, with one sentence, for an id that names no account.");

        app.MapPost("/api/billing-accounts", async (
                JsonElement body, HttpContext context, ISpendStore spend, CancellationToken ct) =>
            {
                var (fields, refusal) = Read(body);
                if (refusal is not null) return Refused(400, refusal);

                var (id, email) = ActorClaims.Of(context.User);
                var write = await spend.CreateAccountAsync(
                    fields!, email ?? id ?? "unknown",
                    TenantLogging.Row(context, TenantActions.BillingAccountCreated, null, null, null), ct);

                return write.Ok
                    ? Results.Created($"/api/billing-accounts/{write.Account!.Id}", write.Account)
                    : Refused(write.Status, write.Refusal!);
            })
            .WithTags("Billing")
            .HumansOnly()
            .WithSummary("Create a billing account")
            .WithDescription(
                "A named vendor account the instance is billed through: " + Fields + " A new account is "
                + "active and tenant-scoped; the client does not choose its id. Anything else is refused "
                + "(400) with one sentence, and a create on an instance with no `instance.id` is refused "
                + "(409). Appends `billing.account-created`; nothing is created when that row cannot be "
                + "written. It is not a connection and holds no credential. " + Answer);

        app.MapPost("/api/billing-accounts/{id}", async (
                string id, JsonElement body, HttpContext context, ISpendStore spend, CancellationToken ct) =>
            {
                if (await spend.FindAccountAsync(id, ct) is not { } current)
                {
                    return Refused(404, BillingAccountRules.Missing(id));
                }

                var (fields, refusal) = Read(body, current);
                if (refusal is not null) return Refused(400, refusal);

                var write = await spend.EditAccountAsync(
                    id, fields!, TenantLogging.Row(context, TenantActions.BillingAccountChanged, null, null, null), ct);

                return write.Ok ? Results.Ok(write.Account) : Refused(write.Status, write.Refusal!);
            })
            .WithTags("Billing")
            .HumansOnly()
            .WithSummary("Edit a billing account")
            .WithDescription(
                "Changes the fields sent; an absent field stays. Null clears only `description`, "
                + "`effectiveTo` and `recurringAmount`; the other fields cannot be cleared. `id`, `scope`, "
                + "`instanceId`, `createdBy` and `createdAt` cannot be changed, and `active` is changed by "
                + "deactivate and reactivate only. " + Fields + " An edit that changes nothing writes "
                + "nothing and answers the account as it is; any other appends `billing.account-changed`, "
                + "naming each field that moved as old and new, in the same transaction. 404 for an id that "
                + "names no account.");

        app.MapPost("/api/billing-accounts/{id}/deactivate", (string id, HttpContext context, ISpendStore spend, CancellationToken ct) =>
                SetActiveAsync(id, false, context, spend, ct))
            .WithTags("Billing")
            .HumansOnly()
            .WithSummary("Deactivate a billing account")
            .WithDescription(
                "Sets `active` false and appends `billing.account-deactivated`, in one transaction. An "
                + "account already inactive is answered as it is, with nothing written. Members who point "
                + "at it keep it, and no run's snapshot is rewritten. 404 for an id that names no account.");

        app.MapPost("/api/billing-accounts/{id}/reactivate", (string id, HttpContext context, ISpendStore spend, CancellationToken ct) =>
                SetActiveAsync(id, true, context, spend, ct))
            .WithTags("Billing")
            .HumansOnly()
            .WithSummary("Reactivate a billing account")
            .WithDescription(
                "Sets `active` true and appends `billing.account-reactivated`, in one transaction. An "
                + "account already active is answered as it is, with nothing written. 404 for an id that "
                + "names no account.");
        MapExpenses(app);
        MapOverview(app);
        MapExport(app);
    }

    private const string OverviewAnswer =
        "`from` and `to`: the period's instants. `actual`: Actual Spend, `{ currency, amount, count }` per currency "
        + "by code - the current confirmed expenses (status `confirmed`, superseded by nothing) whose service period "
        + "STARTS in the period, each counted whole there and never split by days; empty when there are none, and "
        + "never an estimate in their place. `projected`: the recurring charges projected for the period, per "
        + "currency, and `projections`, each one - never in `actual`, and dropped once a current confirmed expense "
        + "covers its account and period. `billingTypes`: `{ billingType, group, currency, actual, projected }`, "
        + "`group` `fixed` for a subscription and `variable` for metered and service. `estimate`: Estimated "
        + "Execution Cost and Agent Hours over the period - `{ currency, cost, agentSeconds, agentRate, legacyRates }`, "
        + "every run's part inside the period priced from its snapshot, the same sum `GET /api/outcomes` gives for "
        + "the window; `cost` null, never 0, when nothing was priced. `accounts` and `vendors`: the breakdown, "
        + "`actual` and `projected` apart, null when there is none. `months`: the trend, at least six months to the "
        + "period's last, each `{ monthStart, actual, estimate }`. Currencies are never converted or added "
        + "together, and Actual Spend is never inferred from hours.";

    private static void MapOverview(WebApplication app)
    {
        app.MapGet("/api/spend/overview", async (
                string? from, string? to, ISpendStore spend, IOutcomeStore outcomes, TenantSettings settings, CancellationToken ct) =>
            {
                if (SpendOverview.PeriodOf(from, to, DateTimeOffset.UtcNow) is not { } period)
                {
                    return Refused(400, SpendOverview.PeriodRefused);
                }

                return Results.Ok((await OverviewAsync(period, spend, outcomes, settings, ct)).Overview);
            })
            .WithTags("Billing")
            .HumansOnly()
            .WithSummary("Actual Spend, Projected and Estimated Execution Cost for a period")
            .WithDescription(
                "The Spend page's overview over `?from=YYYY-MM-DD&to=YYYY-MM-DD` (midnight UTC, `to` exclusive), the "
                + "current UTC month when neither is sent; anything else is refused (400) with one sentence. "
                + OverviewAnswer + " A person's only: no permit lets an agent read it.");

        app.MapGet("/api/spend/linked-members", async (
                HttpContext context, TeamRegistry teams, TeamAccess access, ITeamStore store, CancellationToken ct) =>
            {
                if (PrincipalClaims.From(context.User) is not { } principal) return Results.Unauthorized();

                var effective = await access.EffectiveTeamsAsync(principal, ct);
                var names = teams.All().Where(t => effective.Contains(t.Id)).ToDictionary(t => t.Id, t => t.Name, StringComparer.OrdinalIgnoreCase);

                var linked = (await store.MembersAsync(ct))
                    .Where(m => m.BillingAccountId is not null && names.ContainsKey(m.Team))
                    .OrderBy(m => m.BillingAccountId, StringComparer.Ordinal)
                    .ThenBy(m => names[m.Team], StringComparer.OrdinalIgnoreCase)
                    .ThenBy(m => m.Label ?? m.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(m => new
                    {
                        accountId = m.BillingAccountId,
                        team = m.Team,
                        teamName = names[m.Team],
                        member = m.Name,
                        label = m.Label ?? m.Name,
                    })
                    .ToList();

                return Results.Ok(linked);
            })
            .WithTags("Billing")
            .HumansOnly()
            .WithSummary("The members linked to each billing account")
            .WithDescription(
                "Every member, on the teams the caller reaches, whose billing account is set: `{ accountId, team, "
                + "teamName, member, label }`, by account, then team, then label. Read-only: a member's account is "
                + "set in its settings. An id with no account behind it is listed as it is stored. A person's only: "
                + "no permit lets an agent read it.");
    }

    private const string ExportFormat =
        "A CSV file (RFC 4180, UTF-8, CRLF line ends) with its header row always, even with no data rows; "
        + "snake_case columns that do not change; timestamps ISO-8601 UTC (`yyyy-MM-ddTHH:mm:ss.fffZ`); ISO 4217 "
        + "currency codes; ids as stored; amounts as plain decimal text with no symbol and no thousands separator "
        + "(stored amounts as stored, estimates to the cent); `instance_id`, `period_start` and `period_end` on every "
        + "row. A text cell starting with `=`, `+`, `-`, `@`, a tab or a carriage return is written with a single "
        + "quote before it, so a spreadsheet never runs it; a plain number is written as it is. The period is "
        + "`?from=YYYY-MM-DD&to=YYYY-MM-DD` (midnight UTC, `to` exclusive), the current UTC month when neither is sent, "
        + "as `GET /api/spend/overview` reads it; anything else is refused (400) with one sentence. Currencies are never "
        + "converted or added together. A person's only: no permit lets an agent read it.";

    private static IResult Csv(string file, string csv, (DateTimeOffset From, DateTimeOffset To) period) =>
        Results.File(System.Text.Encoding.UTF8.GetBytes(csv), "text/csv; charset=utf-8", SpendExport.FileName(file, period.From, period.To));

    private static void MapExport(WebApplication app)
    {
        app.MapGet("/api/spend/export/billing-accounts", async (
                string? from, string? to, ISpendStore spend, LedgerIdentity identity, CancellationToken ct) =>
            {
                if (SpendOverview.PeriodOf(from, to, DateTimeOffset.UtcNow) is not { } period) return Refused(400, SpendOverview.PeriodRefused);

                var csv = SpendExport.AccountsCsv(new(period.From, period.To, identity.InstanceId ?? ""), await spend.ListAccountsAsync(ct));
                return Csv(SpendExport.Accounts, csv, period);
            })
            .WithTags("Billing")
            .HumansOnly()
            .WithSummary("Every billing account as CSV")
            .WithDescription(
                "Every billing account, active and inactive, one row each: `" + string.Join("`, `", SpendExport.AccountColumns) + "`. "
                + "`instance_id` is the account's own, copied at create; `fixed_or_variable` is `fixed` for a subscription "
                + "and `variable` for metered and service. " + ExportFormat);

        app.MapGet("/api/spend/export/expenses", async (
                string? from, string? to, ISpendStore spend, LedgerIdentity identity, CancellationToken ct) =>
            {
                if (SpendOverview.PeriodOf(from, to, DateTimeOffset.UtcNow) is not { } period) return Refused(400, SpendOverview.PeriodRefused);

                var csv = SpendExport.ExpensesCsv(
                    new(period.From, period.To, identity.InstanceId ?? ""), await spend.ListAccountsAsync(ct), await spend.ListExpensesAsync(ct: ct));
                return Csv(SpendExport.Expenses, csv, period);
            })
            .WithTags("Billing")
            .HumansOnly()
            .WithSummary("The period's expenses, with every correction, as CSV")
            .WithDescription(
                "Every expense row of each correction chain with a row whose service period starts in the period - "
                + "current, voided and superseded alike - in the order recorded: `" + string.Join("`, `", SpendExport.ExpenseColumns) + "`. "
                + "`supersedes_expense_id` and `superseded_by_expense_id` rebuild the chain; `counts_in_actual_spend` is "
                + "true only for the current confirmed row whose service period starts in the period, so a voided or "
                + "replaced expense counts once, by its current row. " + ExportFormat);

        app.MapGet("/api/spend/export/period-summary", async (
                string? from, string? to, ISpendStore spend, IOutcomeStore outcomes, TenantSettings settings, LedgerIdentity identity,
                CancellationToken ct) =>
            {
                if (SpendOverview.PeriodOf(from, to, DateTimeOffset.UtcNow) is not { } period) return Refused(400, SpendOverview.PeriodRefused);

                var (overview, runs) = await OverviewAsync(period, spend, outcomes, settings, ct);
                var csv = SpendExport.SummaryCsv(
                    new(period.From, period.To, identity.InstanceId ?? ""), overview, SpendExport.RateSourcesOf(runs, period.From, period.To));
                return Csv(SpendExport.Summary, csv, period);
            })
            .WithTags("Billing")
            .HumansOnly()
            .WithSummary("The period's Actual Spend, Projected and Estimated Execution Cost as CSV")
            .WithDescription(
                "`GET /api/spend/overview`'s figures for the same period, the same object, one row each: `"
                + string.Join("`, `", SpendExport.SummaryColumns) + "`. `measure` is `actual_spend` (per currency), "
                + "`projected` (per currency, never in Actual Spend), `actual_spend_by_billing_type` and "
                + "`projected_by_billing_type` (per billing type and currency, with `fixed_or_variable`), and "
                + "`estimated_execution_cost` (Estimated Execution Cost with Agent Hours, `amount` empty when nothing was "
                + "priced; `rate_source` lists the rate sources priced into it, `;`-separated). " + ExportFormat);

        app.MapGet("/api/spend/export/estimated-execution-cost", async (
                string? from, string? to, HttpContext context, TeamRegistry teams, TeamAccess access, IOutcomeStore outcomes,
                TenantSettings settings, LedgerIdentity identity, CancellationToken ct) =>
            {
                if (PrincipalClaims.From(context.User) is not { } principal) return Results.Unauthorized();
                if (SpendOverview.PeriodOf(from, to, DateTimeOffset.UtcNow) is not { } period) return Refused(400, SpendOverview.PeriodRefused);

                var effective = await access.EffectiveTeamsAsync(principal, ct);
                var reached = teams.All().Where(t => effective.Contains(t.Id)).Select(t => new SpendExport.TeamName(t.Id, t.Name)).ToList();
                var floors = reached.ToDictionary(t => t.Id, t => teams.FloorFor(t.Id), StringComparer.OrdinalIgnoreCase);

                var all = await outcomes.ListAsync(ct);
                var currency = settings.OutcomesCurrency;
                var runs = ExecutionCost.Of(await outcomes.ReadExecutionRunsAsync(period.From, period.To, pricedOnly: false, ct), all, currency);

                var csv = SpendExport.EstimatesCsv(new(period.From, period.To, identity.InstanceId ?? ""), runs, all, reached, floors, currency);
                return Csv(SpendExport.Estimates, csv, period);
            })
            .WithTags("Billing")
            .HumansOnly()
            .WithSummary("Estimated Execution Cost by team and by outcome as CSV")
            .WithDescription(
                "Estimated Execution Cost and Agent Hours over the period, from the one pricing path every cost figure uses: "
                + "each run's part inside the period priced from its snapshot (or the rate's history, before snapshots) on "
                + "its own, to the cent. Columns: `" + string.Join("`, `", SpendExport.EstimateColumns) + "`. `rollup` `team`: "
                + "each team the caller reaches with runs in the period, its runs above its floor as every team figure "
                + "reads them. `rollup` `outcome`: every run in the period under the outcome it serves, followed through "
                + "`merged_into`; `Unattributed` (no `outcome_id`) for the runs serving none. One row per team or outcome and "
                + "`rate_source` (`member`, `tenant`, `reconstructed`, `fallback-at-upgrade`, `none`); a team's or an outcome's "
                + "rows add up to its figure. `estimated_agent_cost` and `estimated_plugin_cost` are the agent and plugin runs' "
                + "parts, `estimated_execution_cost` both; each empty, never 0, when nothing was priced. `agent_seconds` and "
                + "`agent_hours` are agent time only; `agent_rate` the priced agent time's weighted rate. " + ExportFormat);
    }

    /// <summary>THE OVERVIEW AS <c>GET /api/spend/overview</c> BUILDS IT, and the priced runs behind it: one code path for
    /// the page and its summary file.</summary>
    private static async Task<(SpendOverview.Overview Overview, IReadOnlyList<ExecutionCost.Run> Runs)> OverviewAsync(
        (DateTimeOffset From, DateTimeOffset To) period, ISpendStore spend, IOutcomeStore outcomes, TenantSettings settings, CancellationToken ct)
    {
        var months = SpendOverview.TrendOf(period.From, period.To);
        var readFrom = months[0] < period.From ? months[0] : period.From;
        var readTo = months[^1].AddMonths(1) > period.To ? months[^1].AddMonths(1) : period.To;

        var all = await outcomes.ListAsync(ct);
        var currency = settings.OutcomesCurrency;
        var runs = ExecutionCost.Of(await outcomes.ReadExecutionRunsAsync(readFrom, readTo, pricedOnly: false, ct), all, currency);

        return (SpendOverview.Of(await spend.ListAccountsAsync(ct), await spend.ListExpensesAsync(ct: ct), runs, currency, period.From, period.To), runs);
    }

    public const string ExpenseNotAnObject = "The body must be a JSON object of the expense's fields.";

    public static string ExpenseNotText(string field) => $"An expense's {field} must be text.";

    /// <summary>The body's fields a record or a replace may send, by their JSON name. Any other field -
    /// <c>id</c>, <c>status</c>, <c>supersedes</c>, <c>recordedBy</c>, <c>recordedAt</c> - is not read:
    /// the store sets those, as a billing account create sets its own.</summary>
    private static readonly string[] ExpenseEditable =
        ["accountId", "amount", "currency", "servicePeriodStart", "servicePeriodEnd", "kind", "reference", "note"];

    private const string ExpenseFieldsText =
        "`accountId` (the id of a billing account, a deactivated one included), `amount` (a number, stored "
        + "as canonical text; zero allowed, negative only for an adjustment), `currency` (three letters, and "
        + "the account's own: no conversion), `servicePeriodStart` and `servicePeriodEnd` (ISO-8601 dates, "
        + "read as midnight UTC, or instants; the end is exclusive and after the start), `kind` (`invoice`, "
        + "`subscription-charge` or `adjustment`), `reference` and `note` (short text, or null). The store "
        + "sets `id`, `status`, `supersedes`, `recordedBy` and `recordedAt`; a body's own are not read.";

    private const string ExpenseAnswer =
        "An expense answers its fields, `id`, `status` (`confirmed` or `void`), `supersedes` (the row it "
        + "corrects, or null), `recordedBy`, `recordedAt`, `supersededBy` (the row that corrects it, or null) "
        + "and `current` (true while nothing supersedes it). Expenses are append-only: a row is never "
        + "changed or deleted, and nothing here is a projection or an estimate.";

    private static void MapExpenses(WebApplication app)
    {
        app.MapGet("/api/expenses", async (string? accountId, ISpendStore spend, CancellationToken ct) =>
                Results.Ok(await spend.ListExpensesAsync(accountId, ct)))
            .WithTags("Billing")
            .HumansOnly()
            .WithSummary("List the expenses")
            .WithDescription(
                "Every expense row, current and superseded, oldest recorded first, then by id; only one "
                + "account's with `?accountId=`; an empty list when there is none. " + ExpenseAnswer
                + " A person's only: no permit lets an agent read one.");

        app.MapGet("/api/expenses/{id}", async (string id, ISpendStore spend, CancellationToken ct) =>
                await spend.FindExpenseAsync(id, ct) is { } expense
                    ? Results.Ok(expense)
                    : Results.NotFound(new { error = ExpenseRules.Missing(id) }))
            .WithTags("Billing")
            .HumansOnly()
            .WithSummary("One expense")
            .WithDescription(ExpenseAnswer + " 404, with one sentence, for an id that names no expense.");

        app.MapPost("/api/expenses", async (JsonElement body, HttpContext context, ISpendStore spend, CancellationToken ct) =>
            {
                var (fields, refusal) = ReadExpense(body);
                if (refusal is not null) return Refused(400, refusal);

                var write = await spend.RecordExpenseAsync(
                    fields!, Actor(context), TenantLogging.Row(context, TenantActions.BillingExpenseRecorded, null, null, null), ct);

                return write.Ok
                    ? Results.Created($"/api/expenses/{write.Expense!.Id}", write.Expense)
                    : Refused(write.Status, write.Refusal!);
            })
            .WithTags("Billing")
            .HumansOnly()
            .WithSummary("Record an expense")
            .WithDescription(
                "A confirmed charge on a billing account: " + ExpenseFieldsText + " Anything else is refused "
                + "(400) with one sentence: an account id that names no account, another currency than the "
                + "account's, a kind outside the set. Appends `billing.expense-recorded`; nothing is recorded "
                + "when that row cannot be written. " + ExpenseAnswer);

        app.MapPost("/api/expenses/{id}/void", async (string id, HttpContext context, ISpendStore spend, CancellationToken ct) =>
            {
                string? note = null;
                if (context.Request.ContentLength is > 0 || context.Request.HasJsonContentType())
                {
                    JsonElement body;
                    try
                    {
                        body = await context.Request.ReadFromJsonAsync<JsonElement>(ct);
                    }
                    catch (JsonException)
                    {
                        return Refused(400, ExpenseNotAnObject);
                    }

                    if (body.ValueKind is not (JsonValueKind.Object or JsonValueKind.Undefined or JsonValueKind.Null)) return Refused(400, ExpenseNotAnObject);
                    if (body.ValueKind == JsonValueKind.Object && body.TryGetProperty("note", out var sent))
                    {
                        if (sent.ValueKind == JsonValueKind.String) note = sent.GetString();
                        else if (sent.ValueKind != JsonValueKind.Null) return Refused(400, ExpenseNotText("note"));
                    }
                }

                var write = await spend.VoidExpenseAsync(
                    id, note, Actor(context), TenantLogging.Row(context, TenantActions.BillingExpenseVoided, null, null, null), ct);

                return write.Ok
                    ? Results.Created($"/api/expenses/{write.Expense!.Id}", write.Expense)
                    : Refused(write.Status, write.Refusal!);
            })
            .WithTags("Billing")
            .HumansOnly()
            .WithSummary("Void an expense")
            .WithDescription(
                "Records a new `void` row superseding the expense, carrying its account, amount, currency, "
                + "service period and kind, with an optional `note`; the voided row is kept, no longer current. "
                + "Appends `billing.expense-voided` in the same transaction and answers the void row (201). A "
                + "void row, or a row already voided or replaced, is refused (409) with one sentence and nothing "
                + "written. 404 for an id that names no expense.");

        app.MapPost("/api/expenses/{id}/replace", async (
                string id, JsonElement body, HttpContext context, ISpendStore spend, CancellationToken ct) =>
            {
                var (fields, refusal) = ReadExpense(body);
                if (refusal is not null) return Refused(400, refusal);

                var write = await spend.ReplaceExpenseAsync(
                    id, fields!, Actor(context), TenantLogging.Row(context, TenantActions.BillingExpenseReplaced, null, null, null), ct);

                return write.Ok
                    ? Results.Created($"/api/expenses/{write.Expense!.Id}", write.Expense)
                    : Refused(write.Status, write.Refusal!);
            })
            .WithTags("Billing")
            .HumansOnly()
            .WithSummary("Replace an expense")
            .WithDescription(
                "Records a new `confirmed` row superseding the expense: the fields sent over the old row's (an "
                + "absent field is the old row's; null clears only `reference` and `note`). " + ExpenseFieldsText
                + " It stays on the same account: another `accountId` is refused (400) - void it and record a new "
                + "one. Appends `billing.expense-replaced` in the same transaction and answers the new row (201). "
                + "A void row, or a row already voided or replaced, is refused (409) with one sentence and nothing "
                + "written. 404 for an id that names no expense.");
    }

    private static string Actor(HttpContext context)
    {
        var (id, email) = ActorClaims.Of(context.User);
        return email ?? id ?? "unknown";
    }

    /// <summary>The body's expense fields, each absent, null or its text, or the one sentence that
    /// refuses the body.</summary>
    internal static (ExpenseFields? Fields, string? Refusal) ReadExpense(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object) return (null, ExpenseNotAnObject);

        var sents = new Dictionary<string, Sent?>();
        foreach (var field in ExpenseEditable)
        {
            if (!body.TryGetProperty(field, out var value)) continue;

            switch (value.ValueKind)
            {
                case JsonValueKind.Null:
                    sents[field] = new Sent(null);
                    break;
                case JsonValueKind.String:
                    sents[field] = new Sent(value.GetString());
                    break;
                case JsonValueKind.Number when field == "amount":
                    sents[field] = new Sent(value.GetRawText());
                    break;
                default:
                    return (null, field == "amount" ? ExpenseRules.AmountRefused : ExpenseNotText(field));
            }
        }

        Sent? Of(string field) => sents.GetValueOrDefault(field);

        return (new ExpenseFields(
            Of("accountId"), Of("amount"), Of("currency"), Of("servicePeriodStart"), Of("servicePeriodEnd"), Of("kind"),
            Of("reference"), Of("note")), null);
    }

    private static async Task<IResult> SetActiveAsync(
        string id, bool active, HttpContext context, ISpendStore spend, CancellationToken ct)
    {
        var action = active ? TenantActions.BillingAccountReactivated : TenantActions.BillingAccountDeactivated;
        var write = await spend.SetAccountActiveAsync(id, active, TenantLogging.Row(context, action, null, null, null), ct);
        return write.Ok ? Results.Ok(write.Account) : Refused(write.Status, write.Refusal!);
    }

    private static IResult Refused(int status, string sentence) => Results.Json(new { error = sentence }, statusCode: status);

    /// <summary>
    /// The body's fields, each absent, null or its text, or the one sentence that refuses the body. On an
    /// edit (<paramref name="current"/> given) a fixed field may only be sent as it is stored.
    /// </summary>
    internal static (BillingAccountFields? Fields, string? Refusal) Read(JsonElement body, BillingAccount? current = null)
    {
        if (body.ValueKind != JsonValueKind.Object) return (null, NotAnObject);

        if (current is not null)
        {
            string? Stored(string field) => field switch
            {
                "id" => current.Id,
                "instanceId" => current.InstanceId,
                "createdBy" => current.CreatedBy,
                "createdAt" => current.CreatedAt,
                _ => current.UpdatedAt,
            };

            foreach (var field in new[] { "id", "instanceId", "createdBy", "createdAt", "updatedAt" })
            {
                if (body.TryGetProperty(field, out var sent)
                    && !(sent.ValueKind == JsonValueKind.String && string.Equals(sent.GetString(), Stored(field), StringComparison.OrdinalIgnoreCase)))
                {
                    return (null, Fixed(field));
                }
            }

            if (body.TryGetProperty("active", out var active)
                && !((active.ValueKind == JsonValueKind.True && current.Active) || (active.ValueKind == JsonValueKind.False && !current.Active)))
            {
                return (null, ActiveOnEdit);
            }
        }

        var sents = new Dictionary<string, Sent?>();
        foreach (var field in Editable)
        {
            if (!body.TryGetProperty(field, out var value)) continue;

            switch (value.ValueKind)
            {
                case JsonValueKind.Null:
                    sents[field] = new Sent(null);
                    break;
                case JsonValueKind.String:
                    sents[field] = new Sent(value.GetString());
                    break;
                case JsonValueKind.Number when field == "recurringAmount":
                    sents[field] = new Sent(value.GetRawText());
                    break;
                default:
                    return (null, field == "recurringAmount" ? BillingAccountRules.RecurringAmountRefused : NotText(field));
            }
        }

        Sent? Of(string field) => sents.GetValueOrDefault(field);

        return (new BillingAccountFields(
            Of("name"), Of("vendor"), Of("billingType"), Of("currency"), Of("billingPeriod"), Of("recurringAmount"),
            Of("effectiveFrom"), Of("effectiveTo"), Of("description"), Of("scope")), null);
    }
}
