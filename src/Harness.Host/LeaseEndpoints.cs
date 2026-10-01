using System.ComponentModel;
using Harness.Containers;
using Harness.Contracts;
using Harness.Host.Auth;

namespace Harness.Host;

/// <summary>
/// The caller's own lease: what the MCP tool <c>lease</c> calls. The owner is read from the
/// credential, never from anything the agent says - a member's container, or a Concierge's
/// principal - so nobody takes or releases a lease for somebody else.
///
/// Gated by <see cref="Permits.Skills"/>, the permit every agent principal holds (members,
/// Managers and the Concierge) and that, like this, reaches only the caller's own things.
/// </summary>
public static class LeaseEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapPost("/api/me/lease", async (
            LeaseRequest request,
            HttpContext context, ContainerHost host, LeaseActions leases, CancellationToken ct) =>
        {
            if (PrincipalClaims.From(context.User) is not { } principal) return Results.Unauthorized();

            if (InstanceLeases.UnknownName(request.Name) is { } unknown) return Results.BadRequest(new { error = unknown });

            LeaseOwner owner;
            switch (principal.Kind)
            {
                case PrincipalKind.Container when ContainerId.TryParse(principal.Id, out var id):
                    if (host.Find(id) is not { } container)
                    {
                        return Results.NotFound(new { error = $"No member '{id}'." });
                    }

                    owner = LeaseOwner.For(container.Id);

                    // A LEASE BELONGS TO A RUN: the run's end is what releases it, so a process
                    // that outlived its run is refused one rather than given a lease nothing frees.
                    if (IsAcquire(request) && container.CurrentCausation is null)
                    {
                        return Results.Conflict(new
                        {
                            error = "You are not in a run, so there is no run to hold a lease for: a lease is released when its run ends.",
                        });
                    }

                    break;
                case PrincipalKind.Concierge or PrincipalKind.TenantConcierge:
                    owner = LeaseOwner.ForConcierge(principal.Id);
                    break;
                default:
                    return Results.Json(
                        new { error = "A lease belongs to an agent's run, and this credential is not an agent." },
                        statusCode: StatusCodes.Status403Forbidden);
            }

            LeaseAnswer answer;
            if (IsAcquire(request))
            {
                answer = await leases.AcquireAsync(request.Name ?? "", owner, ct);
            }
            else if (string.Equals(request.Action?.Trim(), "release", StringComparison.OrdinalIgnoreCase))
            {
                answer = await leases.ReleaseAsync(request.Name ?? "", owner, ct);
            }
            else
            {
                return Results.BadRequest(new { error = $"Say acquire or release, not '{request.Action}'." });
            }

            if (answer.Outcome == LeaseOutcome.Unknown) return Results.BadRequest(new { error = answer.Sentence });

            return Results.Ok(new
            {
                outcome = answer.Outcome switch
                {
                    LeaseOutcome.Granted => "granted",
                    LeaseOutcome.Queued => "queued",
                    LeaseOutcome.Released => "released",
                    _ => "not-held",
                },
                message = answer.Sentence,
                position = answer.Position,
                heldBy = answer.HeldBy?.Select(h => new { team = h.Team, member = h.Member, since = h.Since }),
            });
        })
            .WithTags("Members")
            .RequirePermit(Permits.Skills)
            .WithSummary("Take or release an instance-wide lease")
            .WithDescription(
                "What the MCP tool `lease` calls. `action` is `acquire` or `release`; `name` is the lease, "
                + "and the only one is `heavy`, which at most `leases.heavy.holders` runs hold at once across "
                + "every team. Acquire answers `granted`, or `queued` with the caller's `position` and "
                + "`heldBy` (team and member); a queued member's silence clock is paused and its card reads "
                + "\"waiting for a heavy-work slot\", and calling again is how it waits. A lease is released "
                + "when its run ends, however it ends. 400 for an unknown name, with a sentence; 409 for a "
                + "member that is not in a run; 403 for a person.");
    }

    private static bool IsAcquire(LeaseRequest request) =>
        string.Equals(request.Action?.Trim(), "acquire", StringComparison.OrdinalIgnoreCase);
}

/// <summary>The body of <c>POST /api/me/lease</c>.</summary>
public sealed record LeaseRequest(
    [property: Description("acquire or release.")] string? Action,
    [property: Description("The lease's name. The only one is heavy.")] string? Name);
