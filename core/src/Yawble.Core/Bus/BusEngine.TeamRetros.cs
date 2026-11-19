using Yawble.Core.Skills;

namespace Yawble.Core.Bus;

/// <summary>
/// B100: a team's lessons (its team_retros skill). Every run of the team reads them in its prompt; the Manager alone
/// writes them, for its own team only, with team_retros_write. A write that would replace a text the run has not read
/// (the person edited it in Settings meanwhile) is refused, so the person's edit is never lost.
/// </summary>
public sealed partial class BusEngine
{
    /// <summary>The team's lessons for a run's prompt (null: none), recording the version the run saw.</summary>
    internal string? LessonsFor(RunContext run)
    {
        if (_options.Skills is not { } catalog) return null;
        var now = catalog.TeamRetrosNow(run.Member.TeamId);
        run.RetrosSeenVersion = now.Version;
        return now.Body;
    }

    /// <summary>skills_get for a run: loading team_retros counts as reading it (the version a write starts from).</summary>
    internal Skill SkillForRun(RunContext run, string name)
    {
        var skill = SkillFor(run.Member, name);
        if (skill.Name == TeamRetros.Name && skill.TeamId == run.Member.TeamId && _options.Skills is { } catalog)
            run.RetrosSeenVersion = catalog.TeamRetrosNow(run.Member.TeamId).Version;
        return skill;
    }

    internal Task<string> TeamRetrosWriteAsync(RunContext run, string text, CancellationToken ct)
    {
        if (run.Member.Role != MemberRole.Manager) throw new BusRefusal("not_allowed", "only the Manager writes team_retros");
        if (_options.Skills is not { } catalog) throw new BusRefusal("unavailable", "skills are not available in this core");
        // In a retro run the version names the completed workflow the retro looks back on, not the retro item's own.
        var workflow = run.Items.Select(BusStore.RetroOf).FirstOrDefault(r => r is not null) ?? run.Workflow;
        SkillVersion v;
        try
        {
            v = catalog.WriteTeamRetros(run.Member.TeamId, text, run.Member.Name, workflow, run.RetrosSeenVersion);
        }
        catch (TeamRetrosRefusal refusal)
        {
            throw new BusRefusal(refusal.Code, refusal.Message);
        }
        run.RetrosSeenVersion = v.Version;
        Log($"team {run.Member.Team}: team_retros version {v.Version} written by {run.Member.Name} in workflow {run.Workflow}{(workflow != run.Workflow ? $" (retro of workflow {workflow})" : "")}");
        return Task.FromResult($"ok: team_retros version {v.Version} saved ({v.Body.Length} of {TeamRetros.MaxChars} characters, {TeamRetros.CountLessons(v.Body)} lessons). " +
                               $"Every later run of team {run.Member.Team} gets it in its prompt; the person can read, edit and restore it in Settings > Skills.");
    }
}
