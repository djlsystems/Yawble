import type { Skill } from '../../../shared/api';

// Settings > Skills' origin filter. A skill's origin is what its tag says (SkillsDialog's scope()):
//   builtin  - comes with the app ("built-in")
//   team     - a custom skill for one team only ("team <name>"), e.g. that team's team_retros
//   allTeams - a custom skill the person added for every team ("all teams"; it may replace a built-in of the same name)
// The filter offers All, Built-in and Team skills, plus "All teams" only while such a skill exists, so a person-added
// skill never vanishes: it shows under All and under its own filter.
export type SkillOrigin = 'builtin' | 'team' | 'allTeams';
export type SkillFilter = 'all' | SkillOrigin;

type SkillLike = Pick<Skill, 'kind' | 'teamId' | 'team'>;

export function skillOrigin(s: SkillLike): SkillOrigin {
  if (s.kind === 'builtin') return 'builtin';
  return s.teamId !== null ? 'team' : 'allTeams';
}

/** The tag shown beside a skill: "built-in", "team <name>" or "all teams". */
export function skillTag(s: SkillLike): string {
  const o = skillOrigin(s);
  return o === 'builtin' ? 'built-in' : o === 'team' ? `team ${s.team ?? ''}`.trim() : 'all teams';
}

export function filterSkills<T extends SkillLike>(skills: readonly T[], filter: SkillFilter): T[] {
  return filter === 'all' ? [...skills] : skills.filter((s) => skillOrigin(s) === filter);
}

/** The filter's choices: All, Built-in, Team skills, and All teams only when there is one (or it is the current filter). */
export function filterChoices(skills: readonly SkillLike[], current: SkillFilter = 'all'): { id: SkillFilter; label: string }[] {
  const out: { id: SkillFilter; label: string }[] = [
    { id: 'all', label: 'All' },
    { id: 'builtin', label: 'Built-in' },
    { id: 'team', label: 'Team skills' },
  ];
  if (current === 'allTeams' || skills.some((s) => skillOrigin(s) === 'allTeams')) out.push({ id: 'allTeams', label: 'All teams' });
  return out;
}

/** What the empty detail pane says when a filter leaves no skill. */
export function emptyFilterText(filter: SkillFilter): string {
  return filter === 'builtin' ? 'No built-in skills.' : filter === 'team' ? 'No team skills.' : filter === 'allTeams' ? 'No skills for all teams.' : 'No skills.';
}

/**
 * The selection after the list is filtered: the selected skill if it is still visible, else the first visible one, else
 * null (nothing to show: the pane says so). `same` compares two skills (SkillsDialog's key).
 */
export function selectionAfterFilter<T>(visible: readonly T[], selected: T | null, same: (a: T, b: T) => boolean): T | null {
  if (selected !== null && visible.some((s) => same(s, selected))) return selected;
  return visible[0] ?? null;
}
