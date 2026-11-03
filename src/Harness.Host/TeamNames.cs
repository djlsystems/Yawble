using System.Text.Json;
using Harness.Contracts;

namespace Harness.Host;

/// <summary>
/// The team a NAME means, for an agent that named a team the way a person does.
///
/// A team's identifier never changes; its name does (<c>PUT /api/teams/{team}/name</c>). Agents are
/// told to address a team by its identifier, but a Concierge hears names from the person and keeps
/// them in its notes and its conversation, so a name reaching a tool is ordinary, and after a rename
/// an EARLIER name is too. This answers, in order: the identifier itself; the name a team is shown by
/// now; the name a team was shown by before its latest rename. Only the latest rename of each team is
/// read (one query, <see cref="ITenantLog.FindLatestBySubjectAsync"/>), and a rename written before the
/// team's own creation was its predecessor's and is passed over. A name nobody answers to is null, and
/// the caller goes on as before.
///
/// A match on an earlier name carries a <see cref="Found.Note"/> saying what the team is called now,
/// so the agent stops using the old name with the person.
/// </summary>
public sealed class TeamNames(TeamRegistry teams, ITenantLog tenantLog, ITeamStore store)
{
    /// <summary>A team found by name: its identifier, and a note when it was found by an earlier name.</summary>
    public sealed record Found(string Id, string? Note);

    public async Task<Found?> ResolveAsync(string? name, CancellationToken ct = default)
    {
        var trimmed = (name ?? "").Trim();
        if (trimmed.Length == 0) return null;

        if (teams.ExistingName(trimmed) is { } id) return new(id, null);
        if (teams.TeamAnsweringTo(trimmed) is { } named) return new(named, null);

        var renames = await tenantLog.FindLatestBySubjectAsync([TenantActions.TeamRelabelled], ct);

        foreach (var row in renames.OrderByDescending(row => row.Seq))
        {
            if (row.Subject is null || teams.ExistingName(row.Subject) is not { } owner) continue;
            if (!string.Equals(From(row.Detail), trimmed, StringComparison.OrdinalIgnoreCase)) continue;
            if (await store.CreatedAtAsync(owner, ct) is { } created && row.OccurredAt < created) continue;

            return new(owner, EarlierNameNote(trimmed, owner, teams.LabelFor(owner)));
        }

        return null;
    }

    /// <summary>What an agent is told when it named a team by its earlier name.</summary>
    public static string EarlierNameNote(string earlier, string id, string now) =>
        $"Note: '{earlier}' is an earlier name of team {id}, which is now called '{now}'. "
        + $"Call it '{now}' with the person, and address it as {id}.";

    private static string? From(string? detail)
    {
        if (string.IsNullOrWhiteSpace(detail)) return null;

        try
        {
            using var document = JsonDocument.Parse(detail);
            return document.RootElement.ValueKind == JsonValueKind.Object
                   && document.RootElement.TryGetProperty("from", out var from)
                   && from.ValueKind == JsonValueKind.String
                ? from.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
