import { type ConciergeSettings } from '../api/types';

/**
 * WHAT A CONCIERGE RUNS, AND IT COMES FROM THE TENANT SETTING - NEVER FROM A TEAM.
 *
 * A team row's `team.concierge` is what that team RECORDED when it was created. Recording is not
 * choosing: answering from it would leave a panel with no team with NOTHING to say, and what it
 * did say would come from a row that is written once and is not the thing that decides. The
 * client's note on `concierge()` gives the same reason for the settings dialog: `GET
 * /api/concierge` names no team.
 *
 * THE SETTINGS RECORD IS THE ASSIGNMENT. There is no separate assignment type or translation
 * layer; there is one type for this, and it is the one the server sends.
 */

/**
 * Whether what a running session was launched with has been changed out from under it, so the
 * subtitle is describing a terminal that Reload would replace.
 *
 * It can change easily: the settings dialog opens from the panel's own toolbar, and what it saves
 * reaches the NEXT Concierge - the attached child keeps what it started with. So the two can
 * disagree while somebody is looking at both.
 *
 * ONLY `agent` IS COMPARED: what the Concierge is told is the built-in Concierge prompt, chosen by
 * role and never by a person, so the agent is the only thing a setting can move. COMPARED
 * RAW - a cleared agent under a running session counts as moved.
 *
 * FALSE WHENEVER THERE IS NOTHING TO COMPARE - no running session, or no setting read back. An
 * unknown is not a change, and a warning about a session that does not exist is worse than no
 * warning at all.
 */
export function assignmentMoved(
  running: ConciergeSettings | null,
  chosen: ConciergeSettings | null | undefined,
): boolean {
  if (!running || !chosen) return false;

  return running.agent !== chosen.agent;
}
