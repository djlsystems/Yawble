using System.ComponentModel;
using Harness.Contracts;
using Harness.Host.Auth;

namespace Harness.Host;

/// <summary>Who the ledger belongs to: <c>instance.id</c> and <c>ledger.startedAt</c>, as the start
/// read or wrote them (see <see cref="LedgerStart"/>). Null when that start step did not finish.</summary>
public sealed record LedgerIdentity(string? InstanceId, string? LedgerStartedAt);

/// <summary>
/// <c>GET /api/ledger/export</c>: the accounting, for a person to take to a spreadsheet or another
/// tool. A person's only (<c>HumansOnly</c>): it names every team's spend.
/// </summary>
public static class LedgerEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/ledger/export", async (
                [Description("Only runs that ended, and workflows that closed, at or after this UTC instant. "
                    + "Omit it for the whole ledger.")] DateTimeOffset? since,
                [Description("Only rows with a seq below this one - the previous page's `nextBefore`. Omit it "
                    + "for the newest page.")] long? before,
                [Description("How many rows (runs and workflows together) to return. Default 50, clamped to "
                    + "1..200.")] int? take,
                IUsageLedger ledger, IOutcomeStore outcomes, LedgerIdentity identity, CancellationToken ct) =>
            {
                var page = await ledger.ReadPageAsync(
                    since ?? DateTimeOffset.MinValue, before, Math.Clamp(take ?? 50, 1, ITenantLog.MaxTake), ct);

                // THE OUTCOMES AND EVERY LINK, whole, on the first page only: they are definitions and
                // an append-only history, not rows of the seq cursor the pages walk.
                var first = before is null;

                return Results.Ok(new
                {
                    instanceId = identity.InstanceId,
                    ledgerStartedAt = identity.LedgerStartedAt,
                    runs = page.Runs,
                    workflows = page.Workflows,
                    outcomes = first ? await outcomes.ListAsync(ct) : null,
                    links = first ? await outcomes.ReadLinksAsync(ct) : null,
                    nextBefore = page.NextBefore,
                });
            })
            .WithTags("Tenant")
            .HumansOnly()
            .WithSummary("Export the usage ledger")
            .WithDescription(
                "Every finished run's time and tokens (`runs`, one per run: `usage_ledger`) and every "
                + "workflow's completion or close (`workflows`: `workflow_ledger`), newest first, with "
                + "`instanceId` (a GUID written once for this instance) and `ledgerStartedAt` (when the "
                + "ledger began; rows before it were recovered from the log and carry `backfilled`).\n\n"
                + "Unmeasured is null, never 0: a run with `measured` false has null tokens and "
                + "`billable`. `endedAt - startedAt` is agent time, `startedAt - queuedAt` time waiting "
                + "for a slot. A workflow's `closedAt - rootAt` is its elapsed time; it is never summed "
                + "across workflows.\n\n"
                + "Paged by a seq cursor shared by both lists: pass `nextBefore` as `before` for the "
                + "next, older page; `nextBefore` is null on the last page.\n\n"
                + "Nothing ever updates or deletes a ledger row: Reset, team deletion and log retention "
                + "leave it, and a deleted team's rows keep its `teamName`.\n\n"
                + "The first page (no `before`) also answers `outcomes`, every outcome with its status and "
                + "`mergedInto`, and `links`, every workflow-to-outcome link oldest first: a workflow's "
                + "outcome is its newest link, and a workflow's `outcomeIdAtClose` is the outcome it served "
                + "when it closed. Later pages carry null for both.\n\n"
                + "**A person's action.**");
    }
}
