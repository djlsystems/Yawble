import { nextTick } from 'vue';

/**
 * A member's terminal lives in that member's pane on its team page (memberPaneView.ts, the pane's Activity | Terminal
 * toggle), never in the window's right-hand terminal pane (terminalPane.ts keeps that for channel terminals: Settings >
 * Agents sessions). Every entry that opens a member's terminal (the Monitor, View > Terminal / Ctrl+`, the remote view's
 * openTerminal, an older showMemberTerminal caller) comes here: App opens the team page, then the member's pane is
 * switched to Terminal and scrolled into view by focusMemberTerminal(team, member).
 */
export interface MemberRef { teamId: number; memberId: number }

type Focus = (team: number, member: number) => void;
/** How App opens a team's page (registered by App while it is mounted). */
type OpenTeam = (teamId: number) => void | Promise<void>;
/** Which team a member is on, when only the member is known (remoteApi.openTerminal): App asks the core. */
type FindTeam = (memberId: number) => Promise<number | null>;

// The team page's shared view state (memberPaneView.ts) is read through a glob, so this builds whether or not that
// module is in the tree yet: absent, the team page still opens, on the pane's own default view.
declare global {
  interface ImportMeta { glob<T>(pattern: string, options: { eager: true; import: string }): Record<string, T> }
}
const paneView = import.meta.glob<Focus>('./memberPaneView.ts', { eager: true, import: 'focusMemberTerminal' });
let focus: Focus | null = Object.values(paneView)[0] ?? null;
let openTeam: OpenTeam | null = null;
let findTeam: FindTeam | null = null;
/** The member terminal opened last: View > Terminal / Ctrl+` goes back to it when the right-hand pane has nothing. */
let last: MemberRef | null = null;

export function registerTeamPage(open: OpenTeam, find: FindTeam): () => void {
  openTeam = open;
  findTeam = find;
  return () => { if (openTeam === open) { openTeam = null; findTeam = null; } };
}

/** Tests: a fake focusMemberTerminal (null puts back the real one, if present). */
export function setMemberTerminalFocus(f: Focus | null) { focus = f ?? Object.values(paneView)[0] ?? null; }

export const lastMemberTerminal = (): MemberRef | null => last;

/** Opens the team page with that member's pane on Terminal, scrolled into view. */
export async function openMemberTerminal(teamId: number, memberId: number): Promise<void> {
  last = { teamId, memberId };
  // Set first: a pane mounting with the page reads it; again after the page is there, so the scroll finds its pane.
  focus?.(teamId, memberId);
  await openTeam?.(teamId);
  await nextTick();
  focus?.(teamId, memberId);
}

/** The same when only the member is known: its team is looked up first. Unknown member: nothing happens. */
export async function openMemberTerminalById(memberId: number): Promise<boolean> {
  const teamId = last?.memberId === memberId ? last.teamId : await findTeam?.(memberId).catch(() => null) ?? null;
  if (teamId === null) return false;
  await openMemberTerminal(teamId, memberId);
  return true;
}

/** Tests: forget the last member terminal. */
export function resetMemberTerminal() { last = null; }
