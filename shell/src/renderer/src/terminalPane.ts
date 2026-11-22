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
 * The terminals' xterm palettes (the chrome around them is in terminal-theme.css). Light: dark ink on near-white, with
 * ANSI colours deep enough to read on it (xterm's defaults are made for a dark screen). Dark: the terminal as it always was.
 */
export interface XtermPalette {
  background: string; foreground: string; cursor: string; cursorAccent: string; selectionBackground: string;
  black?: string; red?: string; green?: string; yellow?: string; blue?: string; magenta?: string; cyan?: string; white?: string;
  brightBlack?: string; brightRed?: string; brightGreen?: string; brightYellow?: string; brightBlue?: string;
  brightMagenta?: string; brightCyan?: string; brightWhite?: string;
}
export const XTERM_PALETTES: Record<'light' | 'dark', XtermPalette> = {
  light: {
    background: '#fbfbfc', foreground: '#1d2330', cursor: '#1d2330', cursorAccent: '#fbfbfc', selectionBackground: '#c9d6f2',
    black: '#1d2330', red: '#b42318', green: '#1d6b2f', yellow: '#8a5200', blue: '#2457c5', magenta: '#8a3fa8', cyan: '#0e6e7a', white: '#6b7384',
    brightBlack: '#5b6475', brightRed: '#c9372c', brightGreen: '#2a7d3c', brightYellow: '#9a6200', brightBlue: '#3b6ad6',
    brightMagenta: '#9b4fb8', brightCyan: '#13808d', brightWhite: '#3d4555',
  },
  dark: { background: '#101418', foreground: '#d8dee9', cursor: '#d8dee9', cursorAccent: '#101418', selectionBackground: '#3a4658' },
};
export const xtermTheme = (theme: 'light' | 'dark'): XtermPalette => XTERM_PALETTES[theme];