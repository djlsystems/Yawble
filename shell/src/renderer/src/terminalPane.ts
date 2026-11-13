import { reactive } from 'vue';

/**
 * The main window's terminal pane (View → Terminal): one terminal at a time, on the right. Every terminal button
 * (an agent's card, Settings → Agents → Test) shows its terminal here, replacing what was shown, instead of opening a
 * window of its own.
 */
export type PaneTarget =
  | { kind: 'member'; memberId: number; title: string }
  | { kind: 'channel'; channel: string; title: string; stop?: () => Promise<void> | void };

export const terminalPane = reactive({
  open: false,
  target: null as PaneTarget | null,
  width: readWidth(),
  /** A member session's end failed when ✕ closed the pane: App shows it (the pane is gone by then). */
  stopError: null as string | null,
});

export function showMemberTerminal(memberId: number, title: string) {
  terminalPane.target = { kind: 'member', memberId, title };
  terminalPane.open = true;
}

/** An agent's Terminal / Test session from Settings > Agents (core AgentTestSessions.ChannelPrefix). */
export const AGENT_SESSION_PREFIX = 'agent-test:';
export const isAgentSession = (channel: string) => channel.startsWith(AGENT_SESSION_PREFIX);

/** Shows a channel's terminal. A session it replaces keeps running (the person may want both): an agent session stays
 *  reachable from its tile in Settings > Agents (Running · Open / Stop). */
export function showChannelTerminal(channel: string, title: string, stop?: () => Promise<void> | void) {
  terminalPane.target = { kind: 'channel', channel, title, stop };
  terminalPane.open = true;
}

export function toggleTerminalPane() { terminalPane.open = !terminalPane.open; }
/** Hides the pane. TerminalPane's ✕ also stops an agent session first; Ctrl+` / View → Terminal only hide it. */
export function closeTerminalPane() { terminalPane.open = false; }

export function setTerminalPaneWidth(px: number) {
  terminalPane.width = Math.max(320, Math.min(1400, Math.round(px)));
  try { localStorage.setItem('yawble.terminalPaneWidth', String(terminalPane.width)); } catch { /* ignore */ }
}

function readWidth(): number {
  try {
    const n = Number(localStorage.getItem('yawble.terminalPaneWidth'));
    return Number.isFinite(n) && n >= 320 ? n : 560;
  } catch { return 560; }
}
