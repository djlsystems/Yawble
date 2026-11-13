import type { CompanionLevel } from '../../shared/api';

/** The companion's levels, lowest first (cumulative: each includes the ones before it). */
export const COMPANION_LEVELS: readonly CompanionLevel[] = ['read', 'act', 'destructive'];

/** What a level lets a paired program (such as Claude Code) do, in plain words. */
export const COMPANION_LEVEL_TEXT: Record<CompanionLevel, string> = {
  read: 'Read only: it can see your teams, their status and activity, documents, the backlog and the board. It changes nothing.',
  act: 'Read and act: it can also start work (message teams and answer their questions, dispatch a backlog item to an existing team, nudge a workflow), pause or resume a team, unarchive a team, change a member’s agent, and edit the backlog (including archiving and restoring items) and the board. These run right away, without asking you.',
  destructive: 'Everything, with your approval: it can also ask to do things like push, sync, merge or delete branches, prune, open or merge pull requests, create, archive or delete a team, change a team’s environment or repos, take or restore a backup, run the doctor’s fixes, or close a workflow. Each of those waits in this window until you approve it.',
};

/** The levels a pairing may be granted: its requested level or lower, never higher. */
export function levelsUpTo(requested: CompanionLevel): CompanionLevel[] {
  const i = COMPANION_LEVELS.indexOf(requested);
  return COMPANION_LEVELS.slice(0, i < 0 ? 1 : i + 1);
}
