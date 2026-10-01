using System.ComponentModel;
using System.Security.Claims;
using System.Text.Json;
using Harness.Contracts;
using Harness.Host.Auth;

namespace Harness.Host;

/// <summary>A person's new outcome: active and confirmed at once.</summary>
internal sealed record CreateOutcome(
    [property: Description("The result the work produces, as a result, not an activity. Unique among active and proposed outcomes, ignoring case and spacing.")]
    string? Name,
    [property: Description("What the result means, in a sentence or two.")] string? Description = null,
    [property: Description("Optional text: what is measured, such as open roles tracked.")] string? TargetMetric = null,
    [property: Description("Optional text: its unit, such as roles.")] string? TargetUnit = null,
    [property: Description("Optional text: the target, such as 40. Nothing records a measured value against it.")] string? TargetValue = null);

/// <summary>A person's edit. Absent leaves a field alone; an empty target clears it.</summary>
internal sealed record EditOutcome(
    string? Name = null, string? Description = null,
    string? TargetMetric = null, string? TargetUnit = null, string? TargetValue = null);

internal sealed record MergeOutcome(
    [property: Description("The id of the active or proposed outcome this one's figures move to.")] string? Into);

/// <summary>An agent's proposal, linked to a workflow when it names one.</summary>
internal sealed record ProposeOutcome(
    [property: Description("The result, named as a result, not an activity.")] string? Name,
    [property: Description("What the result means.")] string? Description = null,
    [property: Description("The team whose workflow is linked. A member's credential may name only its own team.")] string? Team = null,
    [property: Description("The workflow to link: its correlation, or any of its rows' seq. Omit to propose without linking.")] long? Correlation = null);

internal sealed record SetWorkflowOutcome(
    [property: Description("An active or proposed outcome's id, or its exact name.")] string? Outcome);

/// <summary>
/// THE OUTCOME ROUTES. Reading is anyone's with <c>Read</c>; every change to an outcome itself is a
/// person's (<c>HumansOnly</c>); proposing, and linking a workflow, is the <c>Outcomes</c> permit's -
/// a Manager's and the Concierge's, never a member's - under the rule that an agent never moves a
/// link a person caused. Every person's write lands with its <c>tenant_events</c> row in the same
/// transaction (<see cref="IOutcomeStore"/>).
/// </summary>
public static class OutcomeEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/outcomes", async (
                [Description("Comma-separated statuses: proposed, active, retired, merged. Omit for every outcome not merged.")] string? status,
                [Description("Only work in the window from this UTC instant: runs that ended, workflows closed or linked. Omit for no lower bound.")] DateTimeOffset? from,
                [Description("Only work before this UTC instant. Omit for no upper bound.")] DateTimeOffset? to,
                [Description("A team id: the outcomes that team's workflows serve come first, each with `teamWorkflows`.")] string? team,
                IOutcomeStore outcomes, IMessageLog log, LedgerIdentity ledger, CancellationToken ct) =>
            {
                var statuses = (status ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                if (statuses.FirstOrDefault(s => !OutcomeStatus.All.Contains(s)) is { } unknown)
                {
                    return Results.BadRequest(new { error = $"'{unknown}' is not a status: proposed, active, retired or merged." });
                }

                var all = await outcomes.ListAsync(ct);
                var book = OutcomeFigures.Open(await outcomes.ReadLedgerAsync(from, to, ct), all, from, to);
                var open = await log.OpenWorkflowsAmongAsync([.. book.InWindow], ct);

                var shown = all.Where(o => statuses.Length == 0 ? o.Status != OutcomeStatus.Merged : statuses.Contains(o.Status));

                var teamCounts = team is null
                    ? new Dictionary<string, int>()
                    : book.LinkOf.Values
                        .Where(l => string.Equals(l.TeamId, team.Trim(), StringComparison.OrdinalIgnoreCase))
                        .GroupBy(l => book.OutcomeOf[l.Correlation])
                        .ToDictionary(g => g.Key, g => g.Count());

                var list = shown
                    .Select(o => new
                    {
                        outcome = o,
                        teamWorkflows = teamCounts.GetValueOrDefault(o.Id),
                    })
                    .OrderByDescending(e => e.teamWorkflows > 0)
                    .ThenByDescending(e => e.teamWorkflows)
                    .ThenBy(e => e.outcome.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(e => Shape(e.outcome, OutcomeFigures.For(book, e.outcome.Id, open), team is null ? null : e.teamWorkflows))
                    .ToList();

                return Results.Ok(new
                {
                    ledgerStartedAt = ledger.LedgerStartedAt,
                    outcomes = list,
                    noOutcome = new
                    {
                        name = OutcomeFigures.NoOutcomeName,
                        figures = OutcomeFigures.For(book, null, open),
                    },
                });
            })
            .WithTags("Outcomes")
            .RequirePermit(Permits.Read)
            .WithSummary("List outcomes with the figures of the work that served them")
            .WithDescription(
                "Each outcome with `figures` read from the usage ledger: `workflows` (open, completed, "
                + "closed, total), `agentSeconds` (the sum over runs; parallel runs add up), "
                + "`waitingSeconds` (the sum of time runs waited for a slot), `elapsed` (per workflow, "
                + "`medianSeconds` and `longestSeconds` of the closed ones; never summed), `tokens` "
                + "(`billable` over measured runs, and `unmeasuredRuns`, counted and never zero), "
                + "`runs`, `teams` (a team that is gone is named from the snapshot, with `deleted`) "
                + "and `lastWorkedAt`. A workflow counts toward the outcome its newest link names, "
                + "followed through `mergedInto`. `noOutcome` carries every workflow with no link. "
                + "`ledgerStartedAt` is when the ledger began: work before it was recovered from the log.\n\n"
                + "With `team`, the outcomes that team's workflows serve come first, each with "
                + "`teamWorkflows`.");

        app.MapGet("/api/outcomes/{id}", async (
                [Description("The outcome's id.")] string id,
                IOutcomeStore outcomes, IMessageLog log, LedgerIdentity ledger, CancellationToken ct) =>
            {
                if (await outcomes.FindAsync(id, ct) is not { } outcome) return Results.NotFound(new { error = $"No outcome '{id}'." });

                var all = await outcomes.ListAsync(ct);
                var resolution = OutcomeFigures.Resolution(all);
                var holder = resolution.GetValueOrDefault(outcome.Id, outcome.Id);
                var book = OutcomeFigures.Open(await outcomes.ReadLedgerAsync(null, null, ct), all, null, null);
                var open = await log.OpenWorkflowsAmongAsync([.. book.InWindow], ct);

                var lines = new List<OutcomeFigures.WorkflowLine>();
                foreach (var correlation in book.WorkflowsOf(holder).Order())
                {
                    var root = book.CloseOf.ContainsKey(correlation) ? null : await log.FindAsync(correlation, ct);
                    lines.Add(OutcomeFigures.Line(book, correlation, open.Contains(correlation), root?.OccurredAt));
                }

                // THE HISTORY: every link that named this outcome or one merged into it, oldest first,
                // with the name it was made under - a rename or merge never rewrote one - and every
                // unlink that left it: a person's "None" names no outcome, so it is shown under the
                // outcome its workflow served before it.
                var history = new List<OutcomeLink>();
                var served = new Dictionary<long, string?>();
                foreach (var link in await outcomes.ReadLinksAsync(ct))
                {
                    var named = link.OutcomeId ?? served.GetValueOrDefault(link.Correlation);
                    if (named is not null && resolution.GetValueOrDefault(named, named) == holder) history.Add(link);
                    served[link.Correlation] = link.OutcomeId;
                }

                // AND WHAT WAS DONE TO IT: the tenant rows naming this outcome or one merged into it -
                // created, renamed, changed, confirmed, retired, reactivated, merged - with who and when.
                // A rename's `from` is the name the row before it left.
                var family = all.Where(o => resolution.GetValueOrDefault(o.Id, o.Id) == holder).Select(o => o.Id)
                    .Append(outcome.Id).ToHashSet();
                var nameOf = new Dictionary<string, string?>();
                var events = (await outcomes.ReadEventsAsync(family, ct)).Select(e =>
                {
                    var from = e.Subject is null ? null : nameOf.GetValueOrDefault(e.Subject);
                    if (e.Subject is not null && e.SubjectName is not null) nameOf[e.Subject] = e.SubjectName;
                    return new
                    {
                        e.Seq,
                        At = e.OccurredAt,
                        e.Action,
                        OutcomeId = e.Subject,
                        Name = e.SubjectName,
                        From = e.Action == TenantActions.OutcomeRenamed ? from : null,
                        By = e.ActorEmail ?? e.ActorId,
                        Detail = e.Detail is null ? (JsonElement?)null : JsonSerializer.Deserialize<JsonElement>(e.Detail),
                    };
                }).ToList();

                return Results.Ok(new
                {
                    outcome = Shape(outcome, OutcomeFigures.For(book, holder, open), null),
                    resolvedTo = holder == outcome.Id ? null : holder,
                    mergedFrom = all.Where(o => o.Id != holder && resolution[o.Id] == holder).Select(o => new { o.Id, o.Name }),
                    workflows = lines,
                    history,
                    events,
                    ledgerStartedAt = ledger.LedgerStartedAt,
                });
            })
            .WithTags("Outcomes")
            .RequirePermit(Permits.Read)
            .WithSummary("One outcome, its workflows and its link history")
            .WithDescription(
                "The outcome with its `figures` (as the list gives them), each workflow it serves now - "
                + "team, state, when it started, elapsed, agent time, billable tokens over `measuredRuns`, "
                + "`unmeasuredRuns`, "
                + "and how and by whom it was linked - and `history`, every link row that named it or an "
                + "outcome merged into it, oldest first, each with the outcome name it was made under - and "
                + "`events`, every `outcome.*` tenant row about it or an outcome merged into it, oldest "
                + "first: `action`, `at`, `by` (the actor's email, or id), `name` (its name after the act), "
                + "`from` (a rename's previous name, when a row before it recorded one) and `detail`. "
                + "A merged outcome answers with `resolvedTo`, the outcome holding its figures now.");

        app.MapPost("/api/outcomes", async (
                CreateOutcome request, HttpContext context, IOutcomeStore outcomes, CancellationToken ct) =>
            {
                var person = PersonOf(context);
                var write = await outcomes.CreateAsync(
                    request.Name ?? "",
                    new OutcomeEdit(null, request.Description, request.TargetMetric, request.TargetUnit, request.TargetValue),
                    person,
                    TenantLogging.Row(context, TenantActions.OutcomeCreated, null, request.Name, new { status = OutcomeStatus.Active }),
                    ct);

                return write.Ok ? Results.Created($"/api/outcomes/{write.Outcome!.Id}", write.Outcome) : Refusal(write);
            })
            .WithTags("Outcomes")
            .HumansOnly()
            .WithSummary("Create an outcome")
            .WithDescription(
                "A person's outcome is `active` and confirmed at once. 409 when an active or proposed "
                + "outcome already has the name (case and spacing ignored); a retired one's name may be "
                + "used again. Appends `outcome.created`; nothing is created when that row cannot be written.");

        app.MapPatch("/api/outcomes/{id}", async (
                string id, EditOutcome request, HttpContext context, IOutcomeStore outcomes, CancellationToken ct) =>
            {
                var detail = new { request.Name, request.Description, request.TargetMetric, request.TargetUnit, request.TargetValue };
                var write = await outcomes.EditAsync(
                    id,
                    new OutcomeEdit(request.Name, request.Description, request.TargetMetric, request.TargetUnit, request.TargetValue),
                    TenantLogging.Row(context, TenantActions.OutcomeRenamed, id, request.Name, detail),
                    TenantLogging.Row(context, TenantActions.OutcomeChanged, id, null, detail),
                    ct);

                return write.Ok ? Results.Ok(write.Outcome) : Refusal(write);
            })
            .WithTags("Outcomes")
            .HumansOnly()
            .WithSummary("Rename or describe an outcome")
            .WithDescription(
                "Absent fields are left alone; an empty target clears it. A rename appends "
                + "`outcome.renamed`, any other change `outcome.changed`. A RENAME NEVER REWRITES A LINK: "
                + "each keeps the name it was made under. 409 for a name another live outcome holds, or "
                + "for a merged outcome.");

        MapTransition(app, "confirm", "Confirm a proposed outcome", TenantActions.OutcomeConfirmed,
            "A Manager's proposal becomes `active`. Only a person confirms.",
            (outcomes, id, context, audit, ct) => outcomes.ConfirmAsync(id, PersonOf(context), audit, ct));

        MapTransition(app, "retire", "Retire an outcome", TenantActions.OutcomeRetired,
            "An active or proposed outcome stops being offered; its links and figures stay, and its "
            + "name may be used again.",
            (outcomes, id, _, audit, ct) => outcomes.RetireAsync(id, audit, ct));

        MapTransition(app, "reactivate", "Reactivate a retired outcome", TenantActions.OutcomeReactivated,
            "A retired outcome is `active` again; 409 when a live outcome has taken its name since.",
            (outcomes, id, _, audit, ct) => outcomes.ReactivateAsync(id, audit, ct));

        app.MapPost("/api/outcomes/{id}/merge", async (
                string id,
                [Description("true answers what would move and changes nothing.")] bool? preview,
                MergeOutcome request, HttpContext context, IOutcomeStore outcomes, IMessageLog log, CancellationToken ct) =>
            {
                if (string.IsNullOrWhiteSpace(request.Into)) return Results.BadRequest(new { error = "Say which outcome to merge into (`into`)." });

                if (await outcomes.FindAsync(id, ct) is not { } from) return Results.NotFound(new { error = $"No outcome '{id}'." });
                if (await outcomes.FindAsync(request.Into.Trim(), ct) is not { } into)
                {
                    return Results.NotFound(new { error = $"No outcome '{request.Into.Trim()}'." });
                }

                if (preview == true)
                {
                    var all = await outcomes.ListAsync(ct);
                    var book = OutcomeFigures.Open(await outcomes.ReadLedgerAsync(null, null, ct), all, null, null);
                    var open = await log.OpenWorkflowsAmongAsync([.. book.InWindow], ct);
                    var resolution = OutcomeFigures.Resolution(all);
                    var links = (await outcomes.ReadLinksAsync(ct))
                        .Count(l => l.OutcomeId is { } named && resolution.GetValueOrDefault(named, named) == from.Id);

                    return Results.Ok(new
                    {
                        preview = true,
                        from = new { from.Id, from.Name, from.Status },
                        into = new { into.Id, into.Name, into.Status },
                        refusal = Harness.Messaging.SqliteOutcomeStore.MergeRefusal(from, into),
                        moves = new
                        {
                            links,
                            figures = OutcomeFigures.For(book, from.Id, open),
                        },
                    });
                }

                var write = await outcomes.MergeAsync(
                    id, into.Id,
                    TenantLogging.Row(context, TenantActions.OutcomeMerged, id, from.Name, new { into = into.Id, intoName = into.Name }),
                    ct);

                return write.Ok ? Results.Ok(write.Outcome) : Refusal(write);
            })
            .WithTags("Outcomes")
            .HumansOnly()
            .WithSummary("Merge an outcome into another")
            .WithDescription(
                "The outcome becomes `merged` with `mergedInto`, and reads follow it: its workflows and "
                + "figures count toward the other. NO LINK IS REWRITTEN. With `?preview=true` the answer "
                + "says what would move (`moves`: its links and its figures) and whether it would be "
                + "refused, and nothing changes. Appends `outcome.merged`.");

        app.MapDelete("/api/outcomes/{id}", async (
                string id, HttpContext context, IOutcomeStore outcomes, CancellationToken ct) =>
            {
                var write = await outcomes.RejectAsync(id, TenantLogging.Row(context, TenantActions.OutcomeRejected, id, null, null), ct);
                return write.Ok ? Results.NoContent() : Refusal(write);
            })
            .WithTags("Outcomes")
            .HumansOnly()
            .WithSummary("Reject a proposed outcome")
            .WithDescription(
                "Removes a proposed outcome no workflow was ever linked to. 409 while any link names "
                + "it: a link is never rewritten, so merge or retire it instead. Appends `outcome.rejected`.");

        app.MapPost("/api/outcomes/propose", async (
                ProposeOutcome request, HttpContext context, IOutcomeStore outcomes, TeamRegistry teams,
                IMessageLog log, CancellationToken ct) =>
            {
                if (PrincipalClaims.From(context.User) is not { } principal) return Results.Unauthorized();

                long? correlation = null;
                string? stored = null;

                if (request.Correlation is { } asked)
                {
                    if (string.IsNullOrWhiteSpace(request.Team) || teams.ExistingName(request.Team) is not { } found)
                    {
                        return Results.BadRequest(new { error = "Name the team whose workflow is linked (`team`)." });
                    }

                    stored = found;
                    var (workflow, refusal) = await WorkflowOfAsync(principal, stored, asked, log, ct);
                    if (refusal is not null) return refusal;
                    correlation = workflow;
                }

                var actor = ActorOf(context, principal);
                var write = await outcomes.ProposeAsync(
                    request.Name ?? "", request.Description, actor, correlation, stored,
                    AuditOf(context, principal, TenantActions.OutcomeCreated,
                        new { status = OutcomeStatus.Proposed, workflow = correlation, team = stored }),
                    ct);

                if (!write.Ok) return Refusal(write);

                return Results.Ok(new
                {
                    outcome = write.Outcome,
                    created = write.Created,
                    link = write.Link,
                    notice = write.Created
                        ? null
                        : $"An {write.Outcome!.Status} outcome is already named \"{write.Outcome.Name}\"; nothing was created"
                            + (write.Link is null ? "." : $", and workflow {correlation} is linked to it."),
                });
            })
            .WithTags("Outcomes")
            .RequirePermit(Permits.Outcomes)
            .WithSummary("Propose an outcome")
            .WithDescription(
                "A Manager's or the Concierge's proposal: a `proposed` outcome, usable at once, that only "
                + "a person confirms. With `correlation` and `team` it is linked to that workflow "
                + "(`how: manager`). When an active or proposed outcome already has the name, nothing is "
                + "created: the answer names it (`created: false`, `notice`) and links it. Refused (409) "
                + "when the workflow's link is a person's (a dispatch, a trigger or a person's choice), "
                + "and nothing is created. A member's credential may link only a workflow its own team is in.");

        app.MapPut("/api/teams/{team}/workflows/{correlation:long}/outcome", async (
                [Description(Describe.Team)] string team,
                [Description("The workflow: its correlation, or the seq of any of its rows.")] long correlation,
                SetWorkflowOutcome request, HttpContext context, IOutcomeStore outcomes, TeamRegistry teams,
                IMessageLog log, CancellationToken ct) =>
            {
                if (PrincipalClaims.From(context.User) is not { } principal) return Results.Unauthorized();
                if (teams.ExistingName(team) is not { } stored) return Results.NotFound(new { error = $"No team '{team}'." });
                if (string.IsNullOrWhiteSpace(request.Outcome)) return Results.BadRequest(new { error = "Name the outcome (`outcome`): its id or exact name." });

                var (workflow, refusal) = await WorkflowOfAsync(principal, stored, correlation, log, ct);
                if (refusal is not null) return refusal;

                if (await outcomes.ResolveLiveAsync(request.Outcome, ct) is not { } outcome)
                {
                    return Results.NotFound(new { error = TriggerCreation.OutcomeRefusal(request.Outcome.Trim()) });
                }

                var person = principal.Kind == PrincipalKind.User;
                var write = await outcomes.LinkAsync(
                    workflow!.Value, outcome.Id, stored, ActorOf(context, principal),
                    person ? OutcomeLinkHow.Person : OutcomeLinkHow.Manager,
                    agentRule: !person,
                    AuditOf(context, principal, TenantActions.WorkflowOutcomeChanged, new { team = stored, workflow }),
                    ct);

                return write.Ok ? Results.Ok(write.Link) : Refusal(write);
            })
            .WithTags("Outcomes")
            .RequirePermit(Permits.Outcomes)
            .WithSummary("Link a workflow to an outcome")
            .WithDescription(
                "Appends a link row; the workflow's outcome is its newest. A person always may "
                + "(`how: person`). A Manager or the Concierge (the `Outcomes` permit, `how: manager`) may "
                + "link a workflow with no outcome or move one an agent linked (`manager`, `tell`), and "
                + "is refused (409, with a sentence) a link a person caused: `dispatch`, `trigger` or "
                + "`person`. A member's credential reaches only workflows its own team is in. Appends "
                + "`workflow.outcome-changed`; nothing is linked when that row cannot be written.");

        MapUnlink(app);
    }

    private static void MapUnlink(WebApplication app) =>
        app.MapDelete("/api/teams/{team}/workflows/{correlation:long}/outcome", async (
                [Description(Describe.Team)] string team,
                [Description("The workflow: its correlation, or the seq of any of its rows.")] long correlation,
                HttpContext context, IOutcomeStore outcomes, TeamRegistry teams, IMessageLog log, CancellationToken ct) =>
            {
                if (PrincipalClaims.From(context.User) is not { } principal) return Results.Unauthorized();
                if (teams.ExistingName(team) is not { } stored) return Results.NotFound(new { error = $"No team '{team}'." });

                var (workflow, refusal) = await WorkflowOfAsync(principal, stored, correlation, log, ct);
                if (refusal is not null) return refusal;

                var write = await outcomes.UnlinkAsync(
                    workflow!.Value, stored, PersonOf(context),
                    TenantLogging.Row(context, TenantActions.WorkflowOutcomeChanged, null, null, null),
                    ct);

                return write.Ok ? Results.Ok(write.Link) : Refusal(write);
            })
            .WithTags("Outcomes")
            .HumansOnly()
            .WithSummary("Set a workflow to no outcome")
            .WithDescription(
                "A person's \"None\": appends a link row that names no outcome (`outcomeId` null, "
                + "`how: person`), so the workflow counts under No outcome. Nothing is deleted: the links "
                + "before it stay, and the outcome it served lists the unlink in its history. 409 when the "
                + "workflow serves no outcome. Appends `workflow.outcome-changed` with `from` (the outcome "
                + "it served) and `to: null`; nothing is unlinked when that row cannot be written.");

    private static void MapTransition(
        WebApplication app, string verb, string summary, string action, string description,
        Func<IOutcomeStore, string, HttpContext, TriggerAudit, CancellationToken, Task<OutcomeWrite>> write) =>
        app.MapPost($"/api/outcomes/{{id}}/{verb}", async (
                string id, HttpContext context, IOutcomeStore outcomes, CancellationToken ct) =>
            {
                var result = await write(outcomes, id, context, TenantLogging.Row(context, action, id, null, null), ct);
                return result.Ok ? Results.Ok(result.Outcome) : Refusal(result);
            })
            .WithTags("Outcomes")
            .HumansOnly()
            .WithSummary(summary)
            .WithDescription(description + $" Appends `{action}`; nothing changes when that row cannot be written.");

    /// <summary>
    /// The workflow <paramref name="asked"/> names (its correlation, or any of its rows), refused
    /// unless it exists - and, for a machine principal, unless its own team is in it.
    /// </summary>
    private static async Task<(long? Workflow, IResult? Refusal)> WorkflowOfAsync(
        Principal principal, string stored, long asked, IMessageLog log, CancellationToken ct)
    {
        if (await log.FindAsync(asked, ct) is not { } row)
        {
            return (null, Results.NotFound(new { error = $"No workflow {asked}." }));
        }

        var correlation = row.CorrelationId;

        if (principal.Kind == PrincipalKind.Container)
        {
            if (!ContainerId.TryParse(principal.Id, out var member)
                || !string.Equals(member.Team, stored, StringComparison.OrdinalIgnoreCase))
            {
                return (null, Results.Json(
                    new { error = $"Workflow {correlation} is not one your team is in; a Manager links only its own team's workflows." },
                    statusCode: StatusCodes.Status403Forbidden));
            }
        }

        if (principal.Kind != PrincipalKind.User)
        {
            var rows = await log.ReadCorrelationAsync(correlation, ct);
            if (!rows.Any(r => string.Equals(MessageTeam.Of(r), stored, StringComparison.OrdinalIgnoreCase)))
            {
                return (null, Results.Json(
                    new { error = $"Workflow {correlation} is not one team '{stored}' is in; a Manager links only its own team's workflows." },
                    statusCode: StatusCodes.Status403Forbidden));
            }
        }

        return (correlation, null);
    }

    private static OutcomeActor PersonOf(HttpContext context)
    {
        var email = context.User.FindFirstValue(ClaimTypes.Email);
        var id = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        return new OutcomeActor(email ?? id ?? "unknown", OutcomeActorKind.Person, id, email);
    }

    private static OutcomeActor ActorOf(HttpContext context, Principal principal) =>
        principal.Kind == PrincipalKind.User
            ? PersonOf(context)
            : new OutcomeActor(principal.Id, OutcomeActorKind.Member, principal.Id);

    /// <summary>A person's row names the person; an agent's names the member.</summary>
    private static TriggerAudit AuditOf(HttpContext context, Principal principal, string action, object detail) =>
        principal.Kind == PrincipalKind.User
            ? TenantLogging.Row(context, action, null, null, detail)
            : new TriggerAudit(principal.Id, null, action, null, null, JsonSerializer.Serialize(detail));

    private static IResult Refusal(OutcomeWrite write) =>
        Results.Json(new { error = write.Refusal }, statusCode: write.Status);

    private static object Shape(Outcome outcome, OutcomeFigures.Figures figures, int? teamWorkflows) => new
    {
        outcome.Id,
        outcome.Name,
        outcome.Description,
        outcome.Status,
        outcome.MergedInto,
        outcome.Source,
        outcome.TargetMetric,
        outcome.TargetUnit,
        outcome.TargetValue,
        outcome.CreatedBy,
        outcome.CreatedByKind,
        outcome.CreatedAt,
        outcome.UpdatedAt,
        outcome.ConfirmedBy,
        outcome.ConfirmedAt,
        teamWorkflows,
        figures,
    };
}
