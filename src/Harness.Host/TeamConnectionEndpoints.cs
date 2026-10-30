using System.ComponentModel;
using System.Data.Common;
using Harness.Contracts;
using Harness.Host.Auth;

namespace Harness.Host;

/// <summary>
/// Team settings → Connections: WHICH CONNECTIONS A TEAM MAY USE. A connection is set up once (Admin →
/// Connections) and assigned to as many teams as a person chooses; a plugin member binds only one
/// assigned to its own team. Assigning and removing are a person's acts only (<c>HumansOnly</c>): no
/// Manager, member or Concierge route or tool assigns one. Every assignment and removal lands with its
/// tenant row in one transaction, and a removal unbinds the team's members that used it in that same
/// transaction. No answer carries a token or any masked value. See <c>docs/connections.md</c>.
/// </summary>
public static class TeamConnectionEndpoints
{
    private const string Area = "Connections";

    public static void Map(WebApplication app)
    {
        app.MapGet("/api/teams/{team}/connections", async (
            [Description(Describe.Team)] string team,
            TeamRegistry teams, Connections connections, CancellationToken ct) =>
        {
            if (teams.ExistingName(team) is not { } stored) return Refusals.NoTeam(team);

            return Results.Ok(new { team = stored, connections = (await ListAsync(stored, teams, connections, ct)).Select(e => e.View) });
        })
            .WithTags(Area)
            .HumansOnly()
            .WithSummary("The connections assigned to a team")
            .WithDescription(
                "`{ team, connections: [{ assignedAt, connection }] }`, oldest assignment first. `connection` is as "
                + "`GET /api/connections` answers one - id, name, kind, provider, account, status, scopes - with "
                + "`usedBy` narrowed to this team's members (`member`, `label`, `slot`). A plugin member of the team "
                + "binds only one of these. Never a token or a masked value. A new team has none.");

        app.MapPost("/api/teams/{team}/connections", async (
            [Description(Describe.Team)] string team,
            AssignTeamConnection request, TeamRegistry teams, Connections connections, HttpContext context, CancellationToken ct) =>
        {
            if (teams.ExistingName(team) is not { } stored) return Refusals.NoTeam(team);

            var id = request.Connection?.Trim();
            if (string.IsNullOrEmpty(id)) return Results.BadRequest(new { error = "`connection` is required: the id of a connection." });

            var (status, error) = await AssignAsync(stored, id, "team settings", ConnectionEndpoints.Actor(context), connections, ct);
            if (error is not null) return Results.Json(new { error }, statusCode: status);

            var entry = (await ListAsync(stored, teams, connections, ct)).FirstOrDefault(e => e.Id == id);
            return Results.Json(entry?.View, statusCode: status);
        })
            .WithTags(Area)
            .HumansOnly()
            .WithSummary("Assign a connection to a team")
            .WithDescription(
                "Body `{ connection }`: a connection's id. Its plugin members may then bind it. 201 with the entry as "
                + "`GET` lists it, and a `team.connection-assigned` tenant row (team, connection id, name, kind - "
                + "never a value) in the same transaction; 200 with the same entry and nothing written when it was "
                + "already assigned. 404 for an unknown team or connection. A person's act only: no agent route or "
                + "tool assigns a connection.");

        app.MapDelete("/api/teams/{team}/connections/{id}", async (
            [Description(Describe.Team)] string team,
            [Description("The connection's id.")] string id,
            [Description("True once the person has seen which members use it and confirmed: they are unbound.")] bool? unbind,
            TeamRegistry teams, Connections connections, IMemberReports reports, PluginSetup setup, HttpContext context,
            CancellationToken ct) =>
        {
            if (teams.ExistingName(team) is not { } stored) return Refusals.NoTeam(team);

            var connection = await connections.Store.GetAsync(id, ct);
            var members = teams.All().SelectMany(t => t.Containers).ToList();

            // ASKED FIRST: a connection the team's members use answers 409 naming them, unless confirmed.
            var users = (await connections.Store.UsedByAsync(id, ct))
                .Where(u => string.Equals(u.Team, stored, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (users.Count > 0 && unbind != true)
            {
                return Results.Conflict(new
                {
                    error = $"Connection {connection?.Named ?? $"'{id}'"} is used by "
                        + string.Join(", ", users.GroupBy(u => u.Member).Select(m => $"{LabelOf(stored, m.Key, members)} (slot{(m.Count() == 1 ? "" : "s")} {string.Join(", ", m.Select(u => u.Slot))})"))
                        + ". Removing it from the team unbinds them, and their runs wait for a person until another is chosen; confirm to remove it.",
                    usedBy = users.Select(u => new { member = u.Member, label = LabelOf(stored, u.Member, members), slot = u.Slot }),
                });
            }

            var actor = ConnectionEndpoints.Actor(context);
            (bool Removed, IReadOnlyList<ConnectionUse> Unbound) result;

            try
            {
                result = await connections.Store.UnassignAsync(
                    stored, id,
                    actor.Row(TenantActions.TeamConnectionRemoved, id, connection?.Name ?? id, new
                    {
                        team = stored,
                        connection = id,
                        name = connection?.Name,
                        kind = connection?.Kind,
                        unbound = users.Select(u => u.Member).Distinct(StringComparer.OrdinalIgnoreCase).Select(m => LabelOf(stored, m, members)),
                    }),
                    unbound => [.. unbound.GroupBy(u => u.Member).Select(member => actor.Row(
                        TenantActions.MemberConnectionsChanged, $"{stored}/{member.Key}", LabelOf(stored, member.Key, members),
                        new
                        {
                            team = stored,
                            setBy = "person",
                            reason = "connection removed from the team",
                            slots = member.Select(u => new { slot = u.Slot, from = id, to = (string?)null }),
                        }))],
                    ct);
            }
            catch (DbException exception)
            {
                return Results.Json(
                    new { error = $"The connection was not removed from the team: its record could not be written ({exception.Message})." },
                    statusCode: StatusCodes.Status500InternalServerError);
            }

            if (!result.Removed) return Results.NotFound(new { error = $"Connection '{id}' is not assigned to team '{stored}'." });

            // EACH MEMBER UNBOUND NOW NEEDS A PERSON: Needs you lists it, and its card says so.
            foreach (var member in result.Unbound.GroupBy(u => u.Member))
            {
                var memberId = new ContainerId(stored, member.Key);
                var slots = string.Join(", ", member.Select(u => $"`{u.Slot}`"));
                await reports.NeedsPersonAsync(
                    memberId,
                    $"Connection {connection?.Named ?? $"'{id}'"} was removed from this team, so slot{(member.Count() == 1 ? "" : "s")} {slots} "
                    + "of this member has no connection. Its runs wait until a person chooses another in the member's settings.",
                    CancellationToken.None);
                await setup.RefreshAsync(memberId, CancellationToken.None);
            }

            return Results.NoContent();
        })
            .WithTags(Area)
            .HumansOnly()
            .WithSummary("Remove a connection from a team")
            .WithDescription(
                "Without `unbind=true`, a connection a member of the team binds answers 409 naming them (`usedBy`: "
                + "`member`, `label`, `slot`), so the person is asked first. With it, the assignment and every binding "
                + "of it on this team go in one transaction with a `team.connection-removed` row and a "
                + "`member.connections-changed` row per member, or none of it does; each such member is then marked "
                + "blocked and reads Needs you until a person binds another. The connection itself stays, and other "
                + "teams keep it. 204; 404 when it was not assigned.");
    }

    /// <summary>
    /// Assigns <paramref name="id"/> to <paramref name="team"/> (its stored id) with its row: (201, null),
    /// (200, null) when it already was, or a status and the sentence.
    /// </summary>
    public static async Task<(int Status, string? Error)> AssignAsync(
        string team, string id, string via, ConnectionActor actor, Connections connections, CancellationToken ct)
    {
        if (await connections.Store.GetAsync(id, ct) is not { } connection)
        {
            return (StatusCodes.Status404NotFound, $"There is no connection '{id}'.");
        }

        try
        {
            return await connections.Store.AssignAsync(
                team, id, actor.Row(TenantActions.TeamConnectionAssigned, id, connection.Name, Connections.AssignedDetail(team, connection, via)), ct) switch
            {
                AssignOutcome.Assigned => (StatusCodes.Status201Created, null),
                AssignOutcome.Already => (StatusCodes.Status200OK, null),
                _ => (StatusCodes.Status404NotFound, $"There is no connection '{id}'."),
            };
        }
        catch (DbException exception)
        {
            return (StatusCodes.Status500InternalServerError, $"The connection was not assigned: its record could not be written ({exception.Message}).");
        }
    }

    private sealed record Entry(string Id, object View);

    private static async Task<IReadOnlyList<Entry>> ListAsync(string team, TeamRegistry teams, Connections connections, CancellationToken ct)
    {
        var assigned = await connections.Store.AssignedAsync(team, ct);
        var records = (await connections.Store.ListAsync(ct)).ToDictionary(c => c.Id, StringComparer.Ordinal);
        var uses = await connections.Store.AllUsesAsync(ct);

        return [.. assigned
            .Where(a => records.ContainsKey(a.ConnectionId))
            .Select(a => new Entry(a.ConnectionId, new
            {
                assignedAt = a.AssignedAt,
                connection = ConnectionEndpoints.View(
                    records[a.ConnectionId],
                    uses[a.ConnectionId].Where(u => string.Equals(u.Team, team, StringComparison.OrdinalIgnoreCase)),
                    teams),
            }))];
    }

    private static string LabelOf(string team, string member, IEnumerable<ContainerSnapshot> members) =>
        members.FirstOrDefault(m => string.Equals(m.Team, team, StringComparison.OrdinalIgnoreCase)
            && string.Equals(m.Id, member, StringComparison.OrdinalIgnoreCase))?.Name ?? member;
}

/// <summary>Body of <c>POST /api/teams/{team}/connections</c>.</summary>
public sealed record AssignTeamConnection(string? Connection);
