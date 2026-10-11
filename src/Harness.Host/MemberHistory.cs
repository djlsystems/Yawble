using Harness.Contracts;
using Harness.Host.Auth;

namespace Harness.Host;

/// <summary>
/// The backward half of <c>GET /api/messages</c>: one member's history older than the
/// board's live window, walked with a seq cursor.
///
/// <para>
/// ONE MEMBER, NOT THE TEAM FEED BACKWARDS. The live window is filtered by team after its cap,
/// which is fine for "the present" and degrades to nothing for "further back": a quiet member on a
/// busy team would page through screens of its colleagues to find one of its own rows. Scoping the
/// read to one container puts the filter in the query, above the cap.
/// </para>
///
/// <para>
/// ACCESS IS DECIDED ONCE, ON THE MEMBER'S TEAM. Every row this read can return is an instruction
/// whose TYPE names the member or a row whose SOURCE is the member, so <see cref="MessageTeam.Of"/>
/// answers the member's team for all of them - which is what makes a per-row filter after the cap
/// unnecessary rather than merely skipped. A team the caller cannot see answers the same 404 an
/// unknown one does.
/// </para>
/// </summary>
internal static class MemberHistory
{
    public const int DefaultTake = 50;
    public const int MaxTake = 200;

    public static async Task<IResult> ReadAsync(
        Principal principal, long? after, long? before, string? team, string? member, int? take,
        IMessageLog log, TeamAccess access, TeamRegistry teams, CancellationToken ct)
    {
        if (before is null || string.IsNullOrWhiteSpace(team) || string.IsNullOrWhiteSpace(member))
        {
            return Results.BadRequest(new
            {
                error = "A member's history needs `before`, `team` and `member` together.",
            });
        }

        if (after is not null)
        {
            return Results.BadRequest(new
            {
                error = "`after` reads the live window forward and `before` reads one member "
                    + "backward; send one or the other.",
            });
        }

        PersistedMember found;

        try
        {
            found = await teams.MemberAsync(team, member, ct);
        }
        catch (InvalidOperationException exception)
        {
            return Results.NotFound(new { error = exception.Message });
        }

        if (principal.Kind != PrincipalKind.User
            && !(await access.EffectiveTeamsAsync(principal, ct)).Contains(found.Team))
        {
            return Results.NotFound(new { error = $"No team '{team}'." });
        }

        // THE FOUND CONTAINER'S SPELLING, never the caller's: the instruction half matches its type
        // under binary collation, and a type built from what was typed answers 200 over nothing.
        var id = new ContainerId(found.Team, found.Name);

        return Results.Ok(await log.ReadMemberBeforeAsync(
            id, found.FloorSeq, before.Value, FeedTypes.Explicit,
            Math.Clamp(take ?? DefaultTake, 1, MaxTake), ct));
    }
}
