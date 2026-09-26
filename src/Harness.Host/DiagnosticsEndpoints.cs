using System.ComponentModel;
using Harness.Contracts;
using Harness.Host.Auth;

namespace Harness.Host;

/// <summary>
/// What Admin &gt; Diagnostics reads: one page of the third store, with the three-state answer to
/// "why is this empty" beside it.
///
/// <para>
/// <b>THE PAGE AND `hasAny` TRAVEL TOGETHER BECAUSE THE SCREEN CANNOT RENDER EITHER ALONE.</b> An
/// empty page has three meanings - nothing was ever captured, nothing matched this filter, and the
/// store could not be read - and they are told apart by a fact the page itself does not carry.
/// Splitting them across two routes would make the screen's most important sentence depend on two
/// requests agreeing about a moment.
/// </para>
///
/// <para>
/// <see cref="DiagnosticsPage"/> is nested rather than flattened so the store's own read shape
/// reaches the browser UNCHANGED. The alternative - copying `events` and `total` up a level - would
/// be a second description of one thing, and `AgentLaunchWireParityTests` would then be guarding the
/// copy instead of the contract.
/// </para>
/// </summary>
/// <param name="HasAny">Whether the store has EVER held a row, regardless of the filter.
/// <c>null</c> means it could not be read, which is an answer rather than a failure.</param>
/// <param name="Kinds">
/// THE CLOSED VOCABULARY, SERVED RATHER THAN COPIED - the screen's kind filter is a list, and this
/// is the list.
///
/// <para>
/// A second hand-maintained copy in the SPA is the mistake `lib/ribbon.ts` records having made with
/// the ribbon: a new kind would have to be added in two places or it half-exists, and nothing would
/// ever fail. <c>/api/events</c> already settled this exact question for the trigger picker -
/// "the catalog a trigger picker is built from" - and this is the same decision.
/// </para>
///
/// <para>
/// It rides on the page rather than taking a route of its own because it is wanted at exactly the
/// moment the page is, by exactly one screen. Twenty short strings is not a payload worth a second
/// request.
/// </para>
/// </param>
public sealed record DiagnosticsView(
    DiagnosticsPage Page, bool? HasAny, IReadOnlyList<string> Kinds);

/// <summary>
/// The read path Admin &gt; Diagnostics is built on.
///
/// <para>
/// <b>ONE ROUTE, AND IT IS A READ.</b> There is no write here and no delete: the store is written
/// from inside the failure paths themselves and trimmed by its own retention bound. A diagnostics
/// log with a clear button is one somebody empties on the morning they most need it.
/// </para>
/// </summary>
public static class DiagnosticsEndpoints
{
    public static void Map(WebApplication app)
    {
        // PEOPLE ONLY. This shows route names, exception types and internal paths from every
        // corner of the instance. The store has no team column to scope by, and adding one would be
        // a different item.
        //
        // HumansOnly rather than a permit: a permit is something an operator can widen, and there
        // is no configuration under which an agent should read this. An agent that can read the platform's internal failures is an agent
        // that can be told about other teams' work.
        app.MapGet("/api/diagnostics", ReadAsync)
            .WithTags("Diagnostics")
            .HumansOnly()
            .WithSummary("Read the diagnostics log")
            .WithDescription(
                "What this instance was doing when it went wrong, newest first - unhandled "
                + "exceptions and 500s with their route and exception type, refusals worth "
                + "knowing about, database trouble, process and launch failures, transport drops, "
                + "and the startup facts that answer \"what was this instance even configured "
                + "as\".\n\n"
                + "**Not the message log** - nothing subscribes to a diagnostic row and no "
                + "container is woken by one. **Not the tenant log** either: nobody DID these, "
                + "which is why no row names an actor.\n\n"
                + "`kind` and `severity` repeat to widen: `?severity=Error&severity=Warning` is "
                + "the union of the two. A `kind` outside the closed vocabulary is REFUSED rather "
                + "than matching nothing, because an empty page is this screen's most misleading "
                + "answer.\n\n"
                + "`search` is matched case-insensitively against the message, the detail, the "
                + "route and the exception type, with `%` and `_` taken literally. It deliberately "
                + "does NOT reach `kind` or `severity`: those have filters of their own, and a box "
                + "that also matched them would make the two disagree about one word.\n\n"
                + "Paged by a seq cursor: pass the last row's `seq` as `before` for the next, "
                + "older page; a page shorter than `take` is the end. `take` defaults to 50 and is "
                + "clamped to 1..200. `total` counts every row matching the filter, regardless of "
                + "the cursor - a caption, not a page count.\n\n"
                + "`hasAny` answers whether the store has EVER held a row, and it is what tells "
                + "an empty page apart from a broken one. `null` means the store could not be "
                + "read - which is a third state, not a failure.\n\n"
                + "**A person's action; no machine principal.**");

        // HUMANS ONLY for the reason the route above is: it names the tooling every member runs on.
        app.MapGet("/api/diagnostics/cli-versions", ReadCliVersionsAsync)
            .WithTags("Diagnostics")
            .HumansOnly()
            .WithSummary("Read which agent CLI versions each start of this volume had")
            .WithDescription(
                "One entry per container start, newest first: when it started and the version each "
                + "agent CLI (and git, gh, node, the headless browser) reported. A `null` version is "
                + "a CLI that was not installed at that start.\n\n"
                + "Recorded by the container's start script onto the data volume, so the history "
                + "follows the volume across image rebuilds. A host started outside the container "
                + "records nothing and answers an empty list.\n\n"
                + "`take` defaults to 20 and is clamped to 1..200.\n\n"
                + "**A person's action; no machine principal.**");
    }

    private static async Task<IResult> ReadCliVersionsAsync(
        CliVersionHistory history,
        [Description("How many starts to return, newest first. Default 20, clamped to 1..200.")]
        int? take,
        CancellationToken ct = default) =>
        Results.Ok(await history.ReadAsync(take ?? 20, ct));

    private static async Task<IResult> ReadAsync(
        IDiagnosticsLog diagnostics,
        [Description("Narrow to these kinds. Repeat to widen. Must be in the closed vocabulary.")]
        string[]? kind,
        [Description("Narrow to these severities: Error, Warning or Info. Repeat to widen.")]
        string[]? severity,
        [Description("Only rows at or after this instant (ISO 8601, or a plain date).")]
        string? from,
        [Description("Only rows at or before this instant (ISO 8601, or a plain date).")]
        string? to,
        [Description("Free text over message, detail, route and exception type.")]
        string? search,
        [Description("Only rows with a seq below this one - the last row's seq from the previous "
            + "page. Omit it for the newest page.")]
        long? before,
        [Description("How many to return. Default 50, clamped to 1..200.")]
        int? take,
        CancellationToken ct = default)
    {
        // THE CLOSED VOCABULARY IS ENFORCED AT THE DOOR, not left to the store.
        //
        // A kind the store has never written matches nothing, so accepting it would answer an empty
        // page - which this screen renders as "nothing went wrong". The screen offers the list
        // rather than a text box for the same reason; this is what a hand-written URL meets.
        if (kind is { Length: > 0 })
        {
            if (kind.FirstOrDefault(k => !DiagnosticKinds.All.Contains(k)) is { } unknown)
            {
                return Results.BadRequest(new
                {
                    error = $"There is no diagnostic kind '{unknown}'.",
                });
            }
        }

        var severities = new List<DiagnosticSeverity>();

        if (severity is { Length: > 0 })
        {
            foreach (var name in severity)
            {
                // Case-insensitively: the wire spells it `Error` because the host serialises enums
                // as their names, and a person typing a URL spells it `error`. One severity.
                //
                // THE NAME, NEVER THE NUMBER. `Enum.TryParse` accepts "1" and answers Warning, which
                // would put the one spelling this store deliberately does not use - see
                // `DiagnosticSeverity`, stored as the lowercase name because "a column of integers
                // is unreadable the day somebody renumbers" - back on the wire through the filter.
                if (!Enum.TryParse<DiagnosticSeverity>(name, ignoreCase: true, out var parsed)
                    || !Enum.GetName(parsed)!.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    return Results.BadRequest(new
                    {
                        error = $"There is no severity '{name}'. Use Error, Warning or Info.",
                    });
                }

                severities.Add(parsed);
            }
        }

        var filter = new DiagnosticsFilter(
            Kinds: kind is { Length: > 0 } ? kind : null,
            Severities: severities.Count > 0 ? severities : null,
            // Parsed the way `/api/kanban/board` parses its window, so the two screens' date
            // controls mean the same thing: an unparseable value narrows nothing rather than
            // refusing, because a half-typed date in a `type="date"` box is a keystroke and not a
            // mistake anybody has made yet.
            From: Instant(from),
            To: Instant(to),
            Search: string.IsNullOrWhiteSpace(search) ? null : search);

        // `hasAny` FIRST, because it is the one that never throws - and it is the answer the screen
        // most needs when the other one is about to fail.
        var hasAny = await diagnostics.HasAnyAsync(ct);

        try
        {
            var page = await diagnostics.ReadAsync(
                filter, before, Math.Clamp(take ?? 50, 1, IDiagnosticsLog.MaxTake), ct);

            return Results.Ok(new DiagnosticsView(page, hasAny, Vocabulary));
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // A DIAGNOSTICS SCREEN THAT 500s WHEN THE DIAGNOSTICS STORE IS BROKEN CANNOT REPORT THE
            // ONE FAILURE IT WAS ADDED TO REPORT.
            //
            // `HasAnyAsync` already swallows for this reason and answers null; the read does not,
            // because a store read is not on a failure path the way a store WRITE is. So the
            // swallowing lives here, at the screen's own door, and nowhere further in.
            //
            // `hasAny` is reported as null rather than whatever was read a moment ago: a store
            // whose read just threw cannot tell us whether it holds anything, and saying `true`
            // here would render as "nothing matched your filter" - a confident answer drawn from a
            // store that had just failed.
            //
            // The cancellation guard keeps a disconnected browser out of this: a cancelled read is
            // not an unreadable store, and turning one into a cheerful 200 would hide a real one.
            return Results.Ok(new DiagnosticsView(new DiagnosticsPage([], 0), null, Vocabulary));
        }
    }

    /// <summary>Sorted, because <see cref="DiagnosticKinds.All"/> is a set and a filter list whose
    /// order changed between reads would be a list nobody could find anything in twice. Ordinal, so
    /// the dotted prefixes group - every `db.` together, every `http.` together.</summary>
    private static readonly IReadOnlyList<string> Vocabulary =
        [.. DiagnosticKinds.All.OrderBy(kind => kind, StringComparer.Ordinal)];

    private static DateTimeOffset? Instant(string? value) =>
        DateTimeOffset.TryParse(value, out var parsed) ? parsed : null;
}
