import { nextTick } from 'vue';
import { focusMemberTerminal } from './memberPaneView';

/**
 * A member's terminal lives in that member's pane on its team page (memberPaneView.ts, the pane's Activity | Terminal
 * toggle), never in the window's right-hand terminal pane (terminalPane.ts keeps that for channel terminals: Settings >
 * Agents sessions). Every entry that opens a member's terminal (the Monitor, the remote view's openTerminal, an older
 * showMemberTerminal caller) comes here: App opens the team page, then the member's pane is
 * switched to Terminal and scrolled into view by focusMemberTerminal(team, member).
 */
export interface MemberRef { teamId: number; memberId: number }

/** memberPaneView.ts's focusMemberTerminal: the team's NAME and the member's name (what the team page keys panes by). */
type Focus = (team: string, member: string) => void;
/** How App opens a team's page (registered by App while it is mounted). */
type OpenTeam = (teamId: number) => void | Promise<void>;
/** A member as the team page names it. */
export interface MemberNames { teamId: number; team: string; member: string }
/**
 * Finds a member by id: in the given team when it is known (the Monitor), else in each team (remoteApi's
 * openTerminal, showMemberTerminal). App asks the core. Null: no such member (there).
 */
type FindMember = (memberId: number, teamId?: number) => Promise<MemberNames | null>;

let focus: Focus = focusMemberTerminal;
let openTeam: OpenTeam | null = null;
let findMember: FindMember | null = null;

export function registerTeamPage(open: OpenTeam, find: FindMember): () => void {
  openTeam = open;
  findMember = find;
  return () => { if (openTeam === open) { openTeam = null; findMember = null; } };
}

/** Tests: a fake focusMemberTerminal (null puts back the real one). */
export function setMemberTerminalFocus(f: Focus | null) { focus = f ?? focusMemberTerminal; }

/** Opens the team page with that member's pane on Terminal, scrolled into view. Not a member of that team: nothing. */
export function openMemberTerminal(teamId: number, memberId: number): Promise<boolean> {
  return open(memberId, teamId);
}

/** The same when only the member is known: its team is looked up first. Unknown member: nothing happens. */
export function openMemberTerminalById(memberId: number): Promise<boolean> {
  return open(memberId);
}

async function open(memberId: number, teamId?: number): Promise<boolean> {
  const m = await findMember?.(memberId, teamId).catch(() => null) ?? null;
  if (!m) return false;
  // Set first: a pane mounting with the page reads it; again after the page is there, so the scroll finds its pane.
  focus(m.team, m.member);
  await openTeam?.(m.teamId);
  await nextTick();
  focus(m.team, m.member);
  return true;
}

