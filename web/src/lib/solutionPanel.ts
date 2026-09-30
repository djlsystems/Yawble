import type { SolutionPanelTrigger, SolutionState } from '../api/types';

/**
 * THE WORDS OF THE SOLUTIONS LAUNCHER AND CONTROL PANEL, kept out of the components so each can be
 * read and tested on its own: a state badge, a trigger's spend today against its cap, when a
 * schedule next fires, a file's size.
 *
 * Every string passed in from a package or its site data stays a string: nothing here builds markup.
 */

export interface StateBadge {
  /** What the badge says: "Running", "Blocked: upload a file to Resume/". Text plus icon, never colour alone. */
  text: string;
  icon: string;
  color: string;
  textColor: string;
}

/** A solution's state as its badge shows it. Blocked names why; capped says "today", and why when given. */
export function stateBadge(state: SolutionState | null | undefined): StateBadge | null {
  if (!state) return null;
  switch (state.kind) {
    case 'running':
      return { text: 'Running', icon: 'play_circle', color: 'positive', textColor: 'white' };
    case 'blocked':
      return {
        text: state.reason ? `Blocked: ${state.reason}` : 'Blocked',
        icon: 'block',
        color: 'warning',
        textColor: 'dark',
      };
    case 'paused':
      return { text: 'Paused', icon: 'pause_circle', color: 'grey-7', textColor: 'white' };
    case 'capped':
      return {
        text: state.reason ? `Capped today: ${state.reason}` : 'Capped today',
        icon: 'hourglass_top',
        color: 'orange-9',
        textColor: 'white',
      };
    default:
      return { text: 'Idle', icon: 'radio_button_unchecked', color: 'grey-4', textColor: 'dark' };
  }
}

const tokens = (count: number) => count.toLocaleString('en-US');

/**
 * Today's spend against the cap, MEASURED ONLY: "12,345 of 200,000 tokens today". Runs that reported
 * no usage are counted as such - "1 run not measured" - never added in as a guess, and a day with only
 * unmeasured runs does not claim zero.
 */
export function spendLine(trigger: Pick<SolutionPanelTrigger, 'spentToday' | 'dailyTokenCap' | 'capped'>): string {
  const spent = trigger.spentToday ?? { tokens: 0, measuredRuns: 0, unmeasuredRuns: 0 };
  const cap = trigger.dailyTokenCap;
  const measured =
    spent.measuredRuns === 0 && spent.unmeasuredRuns > 0
      ? 'nothing measured'
      : cap === null || cap === undefined
        ? `${tokens(spent.tokens)} tokens`
        : `${tokens(spent.tokens)} of ${tokens(cap)} tokens`;
  const noCap = cap === null || cap === undefined ? ', no cap' : '';
  const unmeasured =
    spent.unmeasuredRuns > 0
      ? ` (${spent.unmeasuredRuns} ${spent.unmeasuredRuns === 1 ? 'run' : 'runs'} not measured)`
      : '';
  const capped = trigger.capped ? ' - cap reached' : '';
  return `${measured} today${noCap}${unmeasured}${capped}`;
}

/** When a schedule next fires, as a person reads it; why it does not for the rest. */
export function nextFireLine(trigger: Pick<SolutionPanelTrigger, 'kind' | 'enabled' | 'nextFireAt'>, now = new Date()): string {
  if (!trigger.enabled) return 'Off';
  if (trigger.kind === 'event') return 'Fires on its event';
  if (trigger.kind === 'folder') return 'Fires when a file changes';
  if (!trigger.nextFireAt) return 'Not scheduled';
  return `Next ${whenWords(trigger.nextFireAt, now)}`;
}

/** "8:51 PM" today, "Thu 8:00 AM" on another day; the input itself when it is not a date. */
export function whenWords(at: string, now = new Date()): string {
  const when = new Date(at);
  if (Number.isNaN(when.getTime())) return at;
  const time = when.toLocaleTimeString('en-US', { hour: 'numeric', minute: '2-digit' }).replace(/\s+/g, ' ');
  if (when.toDateString() === now.toDateString()) return time;
  const day = when.toLocaleDateString('en-US', { weekday: 'short', month: 'short', day: 'numeric' });
  return `${day} ${time}`;
}

/** "2.0 KB", "512 B". */
export function sizeWords(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
}

/**
 * A daily cap as typed into its box: a whole number of tokens, or empty for no cap. `undefined` when
 * what was typed is neither, so the box can say so rather than send it.
 */
export function parseCap(typed: string | number | null | undefined): number | null | undefined {
  if (typed === null || typed === undefined) return null;
  const text = String(typed).replace(/[,\s_]/g, '');
  if (text === '') return null;
  if (!/^\d+$/.test(text)) return undefined;
  const value = Number(text);
  return value > 0 ? value : undefined;
}

/** The address of one solution's control panel, and of the launcher. */
export const panelPath = (team: string) => `/solutions/${encodeURIComponent(team)}`;
export const LauncherPath = '/solutions';
