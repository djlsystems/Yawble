using System.ComponentModel;
using System.Security.Claims;
using System.Text.Json;
using Harness.Contracts;
using Harness.Host.Auth;

namespace Harness.Host;

/// <summary>
/// WHERE A TEAM'S ACTIVITY VIEW BEGINS: <c>PUT /api/teams/{team}/activity-from</c>. A person says
/// "from now" (the Host's clock) or clears it; <c>teams.activity_from</c> and
/// <c>activity_from_by</c> keep the instant and the person's email.
///
/// <para>
/// ONLY TWO READS TAKE IT: the team activity read and the team's token runs, whose floor is the
/// later of the team's creation and this instant (<see cref="ScopeAsync"/>). Nothing is deleted or
/// rewritten: spend, budgets, trigger caps, a member's recent cost, outcomes, backlog stats, the
/// ledger export and admission holds read every row, and clearing it brings the whole
/// history back.
/// </para>
///
/// <para>
/// THE ROW AND ITS TENANT ROW LAND TOGETHER: <c>team.activity-from-set</c> or
/// <c>team.activity-from-cleared</c> is written in the same transaction, and when it cannot be,
/// nothing changes (500 with a sentence). A clone does not copy it; a deletion forgets it with the
/// team's row.
/// </para>
/// </summary>
public static class TeamActivityFrom
{
    /// <summary>The refusal of a body that is neither <c>"now"</c> nor null.</summary>
    public const string BodyRefusal = "Give from: \"now\" to start the team's activity view now, or null for all of its history.";

    /// <summary>
    /// The scope the activity and token-runs reads take: <see cref="Floor"/>, the log position the
    /// later of the team's creation and its activity-from fell at; <see cref="Since"/>, that instant
    /// (null when neither is known); and <see cref="From"/>, the team's activity-from when set.
    /// </summary>
    public sealed record Scope(long Floor, DateTimeOffset? Since, ActivityFrom? From)
    {
        /// <summary>Whether the activity-from is the floor, rather than the team's creation.</summary>
        public bool FromIsFloor => From is { } from && Since == from.At;

        /// <summary>
        /// Where the default window starts: the stretch's own start, or - when a workflow was rooted
        /// before the activity-from - the activity-from itself, so that workflow is drawn from there.
        /// </summary>
        public DateTimeOffset WindowStart(TeamWorkflowStretch stretch) =>
            FromIsFloor && stretch.RootedBeforeFloor && Since is { } since ? since : stretch.From;
    }

    /// <summary>The floor of <paramref name="team"/>'s activity and token-runs reads; see
    /// <see cref="Scope"/>.</summary>
    public static async Task<Scope> ScopeAsync(string team, ITeamStore store, IMessageLog log, CancellationToken ct)
    {
        var created = await store.CreatedAtAsync(team, ct);
        var from = await store.ActivityFromAsync(team, ct);

        DateTimeOffset? since = (created, from) switch
        {
            ({ } c, { } f) => f.At > c ? f.At : c,
            ({ } c, null) => c,
            (null, { } f) => f.At,
            _ => null,
        };

        var floor = since is { } at ? await log.LastSeqBeforeAsync(at, ct) : 0;
        return new Scope(floor, since, from);
    }

    public static void Map(WebApplication app)
    {
        app.MapPut("/api/teams/{team}/activity-from", async (
                [Description(Describe.Team)] string team,
                JsonElement body,
                TeamRegistry teams, ITeamStore store, TimeProvider clock, HttpContext context, CancellationToken ct) =>
            {
                if (teams.ExistingName(team) is not { } stored)
                {
                    return Refusals.NoTeam(team);
                }

                if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty("from", out var asked)
                    || !(asked.ValueKind == JsonValueKind.Null
                         || (asked.ValueKind == JsonValueKind.String && asked.GetString() == "now")))
                {
                    return Results.BadRequest(new { error = BodyRefusal });
                }

                var email = context.User.FindFirstValue(ClaimTypes.Email) ?? "a person";
                var label = teams.LabelFor(stored);
                ActivityFrom? from = asked.ValueKind == JsonValueKind.Null ? null : new ActivityFrom(clock.GetUtcNow(), email);

                var audit = new TriggerAudit(
                    context.User.FindFirstValue(ClaimTypes.NameIdentifier),
                    email,
                    from is null ? TenantActions.TeamActivityFromCleared : TenantActions.TeamActivityFromSet,
                    stored,
                    label,
                    JsonSerializer.Serialize(new { activityFrom = from }, JsonSerializerOptions.Web));

                try
                {
                    await store.SetActivityFromAsync(stored, from, audit, ct);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    return Results.Json(
                        new { error = $"'{label}' activity view was not changed: its tenant log row could not be written ({exception.Message}). Nothing was changed." },
                        statusCode: StatusCodes.Status500InternalServerError);
                }

                return Results.Ok(new ActivityFromAnswer(from));
            })
            .WithTags("Activity")
            .HumansOnly()
            .WithSummary("Start this team's activity view now, or show all of its history again")
            .WithDescription(
                "`{ \"from\": \"now\" }` starts the team's activity view at the Host's clock and records "
                + "who said so; `{ \"from\": null }` clears it. Answers `{ \"activityFrom\": { \"at\", \"by\" } }` "
                + "or `{ \"activityFrom\": null }`.\n\n"
                + "Only `GET /api/teams/{team}/activity` and `GET /api/teams/{team}/tokens/runs` read it: "
                + "their floor is the later of the team's creation and this instant. Nothing is deleted "
                + "or rewritten, and spend, budgets, trigger caps, a member's recent cost, outcomes, "
                + "backlog stats, the ledger export and admission holds are unchanged.\n\n"
                + "The change and its `team.activity-from-set` or `team.activity-from-cleared` tenant row "
                + "land together, or nothing changes (500 with a sentence). A person's action.");
    }
}

/// <summary>The answer of <c>PUT /api/teams/{team}/activity-from</c>: the activity-from as it now
/// stands, null for none.</summary>
public sealed record ActivityFromAnswer(ActivityFrom? ActivityFrom);
