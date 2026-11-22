import { reactive, type InjectionKey } from 'vue';
import { openMemberTerminalById } from './memberTerminal';

/**
 * The main window's terminal pane: one terminal at a time, on the right, for the terminals that are no member's:
 * Settings → Agents Terminal / Test sessions and other channels. It opens when one of them is shown and closes with its ✕;
 * there is no menu item or shortcut for it. A member's terminal is in its own pane on
 * the team page (memberTerminal.ts), never here.
 */
export type PaneTarget = { kind: 'channel'; channel: string; title: string; stop?: () => Promise<void> | void };

export const terminalPane = reactive({
  open: false,
  target: null as PaneTarget | null,
  width: readWidth(),
});

/** An older entry for a member's terminal: it opens on the team page (that member's pane, on Terminal) instead. */
export function showMemberTerminal(memberId: number, _title?: string) {
  void openMemberTerminalById(memberId);
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

/** Hides the pane. TerminalPane's ✕ also stops an agent session first. */
export function closeTerminalPane() { terminalPane.open = false; }

/**
 * The pane's one header row on a phone (B057): TerminalPane (a channel terminal) and MemberPane (a member's terminal,
 * card 307) provide where the shown terminal puts its Formatted / Raw
 * tabs (`tabs`, in the header) and its bar's actions (`more`, the header's ⋯ menu), as element ids; `on` is false at
 * desktop width, and then each stays where it is. Outside the pane nothing is provided and nothing moves.
 */
export interface PaneHeaderSlots { readonly on: boolean; readonly tabs: string; readonly more: string }
export const PaneHeaderKey: InjectionKey<PaneHeaderSlots> = Symbol('terminal-pane-header');

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

/**
 * The terminal screen (xterm) keeps xterm's own default colours, light theme or dark: black, white text and the standard
 * ANSI colours, which is what the agent CLIs draw for, as in any terminal. Only the chrome around it (terminal-theme.css)
 * follows the window's theme. This is that background, for the screen's padding to match.
 */
export const XTERM_SCREEN_BG = '#000000';