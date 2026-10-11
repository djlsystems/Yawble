using System.Text.Json;
using Harness.Contracts;
using Harness.Skills;

namespace Harness.Host;

/// <summary>
/// TEAM SKILLS: custom skills that belong to one team (<see cref="Skill.Team"/>), offered only to
/// that team's members of their roles - in <c>skills_search</c>, in the prompt's skill list, and by
/// <c>skills_get</c>, which refuses one to anyone else. Team settings → Skills writes them through
/// here, and so does anything that installs a team (a solution package's skills are team skills).
///
/// Every write appends its <c>tenant_events</c> row (<c>skill.created</c>, <c>skill.changed</c>,
/// <c>skill.deleted</c>, subject the team) in the SAME transaction as the change, then refreshes
/// <see cref="SkillDirectory"/> and re-prompts the team, so its members' "Available skills" list
/// follows at once. A team deletion removes the team's skills (<see cref="DeleteTeamAsync"/>).
/// </summary>
public sealed class TeamSkills(ISkillStore store, SkillDirectory directory, TeamRegistry teams)
{
    /// <summary>The team's stored id, or null when there is no such team.</summary>
    public string? TeamOf(string team) => teams.ExistingName(team);

    /// <summary>Team <paramref name="team"/>'s own skills by name; null when there is no such team.</summary>
    public async Task<IReadOnlyList<Skill>?> ListAsync(string team, CancellationToken ct = default) =>
        TeamOf(team) is { } stored ? await store.ListTeamAsync(stored, ct) : null;

    /// <summary>
    /// REGISTERS A TEAM SKILL - the call an installer makes. Creates skill
    /// <paramref name="draft"/> on <paramref name="team"/>, or rewrites the team's skill of that
    /// name when it already has one, so installing the same package again (or a newer version of
    /// it) is not refused. <paramref name="by"/> is who is recorded as writing it (a person's email,
    /// or null for the platform), <paramref name="audit"/> the tenant row that lands with it.
    /// <para>
    /// Throws <see cref="KeyNotFoundException"/> for a team that does not exist,
    /// <see cref="SkillRefusedException"/> for a name a built-in, a plugin or an instance-wide custom
    /// skill holds (or one beginning <c>plugin-</c>), and <see cref="ArgumentException"/> for an
    /// illegal name, a missing description or body, or no or an unknown role - each with a sentence.
    /// A skill file (<c>SKILL.md</c> with name, description and roles in its front matter) is
    /// registered by <see cref="RegisterFileAsync"/>.
    /// </para>
    /// </summary>
    public Task<Skill> RegisterAsync(
        string team, SkillDraft draft, string? by, TriggerAudit? audit = null, CancellationToken ct = default) =>
        PutAsync(team, draft, replace: true, by, audit, ct);

    /// <summary>
    /// <see cref="RegisterAsync"/> for a skill file's text: its front matter's name, description
    /// and roles, and the rest as the body. A file with no roles is refused, as a draft is.
    /// </summary>
    public Task<Skill> RegisterFileAsync(
        string team, string markdown, string? by, TriggerAudit? audit = null, CancellationToken ct = default)
    {
        var parsed = SkillFile.Parse(markdown);
        return RegisterAsync(team, new SkillDraft(parsed.Name, parsed.Description, parsed.Roles ?? [], parsed.Body), by, audit, ct);
    }

    /// <summary>A person's create from Team settings: refused when the team already has the name.</summary>
    public Task<Skill> CreateAsync(
        string team, SkillDraft draft, string? by, TriggerAudit? audit = null, CancellationToken ct = default) =>
        PutAsync(team, draft, replace: false, by, audit, ct);

    /// <summary>Rewrites (and may rename) the team's skill <paramref name="name"/>; null when it has none.</summary>
    public async Task<Skill?> UpdateAsync(
        string team, string name, SkillDraft draft, string? by, TriggerAudit? audit = null, CancellationToken ct = default)
    {
        var stored = TeamOf(team) ?? throw NoTeam(team);

        var updated = await store.UpdateTeamSkillAsync(
            stored, name, draft, by,
            audit ?? Row(TenantActions.SkillChanged, stored, name, new { team = stored, name, to = draft.Name }), ct);

        if (updated is not null) await AfterWriteAsync(stored, ct);
        return updated;
    }

    /// <summary>Deletes the team's skill <paramref name="name"/>; false when it has none.</summary>
    public async Task<bool> DeleteAsync(
        string team, string name, TriggerAudit? audit = null, CancellationToken ct = default)
    {
        var stored = TeamOf(team) ?? throw NoTeam(team);

        var deleted = await store.DeleteTeamSkillAsync(
            stored, name, audit ?? Row(TenantActions.SkillDeleted, stored, name, new { team = stored, name }), ct);

        if (deleted) await AfterWriteAsync(stored, ct);
        return deleted;
    }

    /// <summary>
    /// A team deletion's step: every skill of <paramref name="team"/> (its STORED id), one tenant
    /// row per skill, in one transaction. Returns the names removed.
    /// </summary>
    public async Task<IReadOnlyList<string>> DeleteTeamAsync(string team, CancellationToken ct = default)
    {
        var names = await store.DeleteTeamSkillsAsync(
            team, name => Row(TenantActions.SkillDeleted, team, name, new { team, name, reason = "team deleted" }), ct);

        if (names.Count > 0) await directory.RefreshAsync(store, ct);
        return names;
    }

    /// <summary>A tenant row for a team skill write, for a caller with no person behind it.</summary>
    public static TriggerAudit Row(string action, string team, string name, object detail) =>
        new(null, null, action, team, name, JsonSerializer.Serialize(detail));

    private async Task<Skill> PutAsync(
        string team, SkillDraft draft, bool replace, string? by, TriggerAudit? audit, CancellationToken ct)
    {
        var stored = TeamOf(team) ?? throw NoTeam(team);

        var skill = await store.PutTeamSkillAsync(
            stored, draft, replace, by,
            audit ?? Row(TenantActions.SkillCreated, stored, draft.Name, new { team = stored, name = draft.Name, roles = draft.Roles }), ct);

        await AfterWriteAsync(stored, ct);
        return skill;
    }

    private async Task AfterWriteAsync(string team, CancellationToken ct)
    {
        await directory.RefreshAsync(store, ct);
        await teams.RepromptTeamAsync(team, ct);
    }

    private static KeyNotFoundException NoTeam(string team) => new($"There is no team named '{team}'.");
}
