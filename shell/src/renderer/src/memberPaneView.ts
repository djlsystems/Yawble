import { reactive, readonly } from 'vue';

/**
 * What each member's pane on the team page shows on its right (B061): its Activity list or its terminal. Kept per
 * member (team name + member name) in this app's storage, a convenience only. Reactive: the team page and any entry
 * point (the Monitor, the palette, the remote view) read and set the same state.
 *
 * focusMemberTerminal is the entry points' one call: it switches that member to Terminal and leaves a request the team
 * page takes when it shows that team (it scrolls the pane into view and clears the request with takeMemberFocus).
 * Opening the team page itself is the caller's part.
 *
 * A phone (B064, card 310) keeps its own, unstored choice: at most one member's terminal is open (it is the whole screen),
 * so opening one there sets every other member back to Activity, and the team page opens with every member on Activity
 * (TeamView forgets its team's phone choice when it closes). The remembered choice above applies at desktop widths only;
 * a phone neither reads nor changes it.
 */
export type MemberView = 'activity' | 'terminal';

const StorageKey = 'yawble.memberPaneView';
const keyOf = (team: string, member: string) => `${team}/${member}`;

/** A focus request: which member, and a sequence number so asking for the same member twice is seen twice. */
export interface MemberFocus { team: string; member: string; seq: number }

const state = reactive({
  views: readViews() as Record<string, MemberView>,
  focus: null as MemberFocus | null,
  /** A phone: the one member whose terminal is open, or null (not stored). */
  phoneOpen: null as { team: string; member: string } | null,
});
let seq = 0;

const phoneIs = (team: string, member: string) => state.phoneOpen?.team === team && state.phoneOpen.member === member;

/** The member's view; Activity unless Terminal was chosen (on a phone: unless it is the one terminal open there). */
export function getMemberView(team: string, member: string, phone = false): MemberView {
  if (phone) return phoneIs(team, member) ? 'terminal' : 'activity';
  return state.views[keyOf(team, member)] === 'terminal' ? 'terminal' : 'activity';
}

/** Sets the remembered view, or on a phone the one open terminal (any other member there goes back to Activity). */
export function setMemberView(team: string, member: string, view: MemberView, phone = false) {
  if (phone) {
    if (view === 'terminal') state.phoneOpen = { team, member };
    else if (phoneIs(team, member)) state.phoneOpen = null;
    return;
  }
  const k = keyOf(team, member);
  if (view === 'terminal') state.views[k] = 'terminal';
  else delete state.views[k]; // Activity is the default: only Terminal is stored.
  writeViews();
}

/** Shows the member's terminal in its pane and asks the team page to scroll that pane into view. */
export function focusMemberTerminal(team: string, member: string) {
  setMemberView(team, member, 'terminal');
  setMemberView(team, member, 'terminal', true);
  state.focus = { team, member, seq: ++seq };
}

/** The open focus request (reactive), or null. */
export const memberFocus = (): MemberFocus | null => readonly(state).focus;

/** The team page took the request for this team (it scrolled the pane into view): returns it and clears it. */
export function takeMemberFocus(team: string): MemberFocus | null {
  const f = state.focus;
  if (!f || f.team !== team) return null;
  state.focus = null;
  return f;
}

/** The team page closed (B064): on a phone its team's members are all on Activity the next time it opens. */
export function closePhoneTerminals(team: string) {
  if (state.phoneOpen?.team === team) state.phoneOpen = null;
}

/** Tests: forget every choice and request. */
export function resetMemberViews() {
  for (const k of Object.keys(state.views)) delete state.views[k];
  state.focus = null;
  state.phoneOpen = null;
  writeViews();
}

function readViews(): Record<string, MemberView> {
  try {
    const got = JSON.parse(localStorage.getItem(StorageKey) ?? '{}') as unknown;
    if (!got || typeof got !== 'object') return {};
    return Object.fromEntries(Object.entries(got as Record<string, unknown>).filter(([, v]) => v === 'terminal')) as Record<string, MemberView>;
  } catch { return {}; }
}

function writeViews() {
  try {
    if (Object.keys(state.views).length) localStorage.setItem(StorageKey, JSON.stringify(state.views));
    else localStorage.removeItem(StorageKey);
  } catch { /* storage refused */ }
}
