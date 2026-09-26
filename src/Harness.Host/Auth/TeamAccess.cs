using Harness.Contracts;

namespace Harness.Host.Auth;

/// <summary>
/// What a caller may reach, answered from the DATABASE on every request and never cached.
///
/// Every person reaches every team that exists. What this class answers is which team a MACHINE
/// principal is bound to. A container's credential belongs to one team and a Concierge's to one
/// team or to the whole tenant; that is identity, not a permission somebody handed out, and it is
/// what keeps a member credential from acting on another team.
/// </summary>
public sealed class TeamAccess(
    IUserStore users,
    IPrincipalStore principals,
    TeamRegistry teams)
{
    public async Task<bool> MayAdministerBacklogAsync(
        Principal principal, CancellationToken ct = default)
    {
        if (principal.Kind == PrincipalKind.User) return true;

        // A tenant-wide Concierge acts for a person - provided that person still exists.
        if (principal.Kind == PrincipalKind.TenantConcierge
            && principal.OwnerUserId is { } owner)
        {
            return await users.FindByIdAsync(owner, ct) is not null;
        }

        return false;
    }

    /// <summary>
    /// The teams this caller reaches, intersected with what TeamRegistry currently holds.
    ///
    /// A PERSON REACHES EVERYTHING THAT EXISTS. A machine principal reaches what its identity says:
    /// a tenant-wide Concierge reaches everything, a team Concierge its one team, a container its
    /// own team, and an API key everything its owner does. Because this is an intersection with
    /// what exists, a team that does not exist is absent from it for EVERYONE - correct for a set,
    /// and the reason <see cref="TeamGate"/> lets a person through to the handler's own 404 rather
    /// than asking this first.
    /// </summary>
    public async Task<IReadOnlySet<string>> EffectiveTeamsAsync(
        Principal principal, CancellationToken ct = default)
    {
        var existing = teams.All().Select(t => t.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (principal.Kind == PrincipalKind.User) return existing;

        if (principal.OwnerUserId is { } owner)
        {
            // A credential that ACTS AS a person reaches what that person reaches, resolved NOW.
            // An owner who no longer exists reaches nothing. The foreign key cascades these rows
            // away, so this is reachable only through a Principal resolved before the deletion -
            // and it must fail CLOSED rather than fall through to the branch below, which would
            // silently hand an orphan its birth team's authority.
            if (await users.FindByIdAsync(owner, ct) is not { } _)
            {
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }

            // A person reaches every team that exists. Their key does too. It is still not a
            // person: HumansOnly refuses every non-User kind, which is what keeps user
            // administration closed to a credential pasted into a tool.
            //
            // A CONSOLE is bound to ONE team, so it stops there however far the person driving it
            // reaches. That is what keeps a Concierge on Alpha from reaching Beta. A tenant-wide
            // Concierge has no such bound. An API key is deliberately NOT bounded this way; its
            // `team` column records only where it was born, which is why this branches on KIND and
            // not on "has an owner".
            //
            // A future PrincipalKind that gains an owner needs its OWN arm here. Falling through
            // this `if` means full inheritance, which is silent and is a WIDENING.
            if (principal.Kind == PrincipalKind.TenantConcierge)
            {
                return existing;
            }

            if (principal.Kind == PrincipalKind.Concierge)
            {
                var bound = await principals.TeamForAsync(principal.Id, ct);

                existing.IntersectWith(bound is null ? [] : new[] { bound });
            }

            return existing;
        }

        // No owner: authority of its own, bounded by the team column. A container reaches its own
        // team and nothing else.
        var team = await principals.TeamForAsync(principal.Id, ct);
        if (team is null) return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        existing.IntersectWith([teams.ExistingName(team) ?? team]);

        return existing;
    }

    /// <summary>
    /// Membership of the effective set, and nothing more. A team that does not exist is not in
    /// anyone's effective set, so this answers false for a name that never existed - which is why
    /// <see cref="TeamGate"/> passes a person through to the handler's 404 rather than asking this.
    /// </summary>
    public async Task<bool> MayActOnAsync(
        Principal principal, string team, CancellationToken ct = default) =>
        (await EffectiveTeamsAsync(principal, ct)).Contains(team);
}
