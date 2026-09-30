import type { SolutionPanelMember, SolutionPanelRun, SolutionPanelTrigger, SolutionState } from '../api/types';

/**
 * THE WORDS OF THE SOLUTIONS LAUNCHER AND CONTROL PANEL, kept out of the components so each can be
 * read and tested on its own: a state badge, when a schedule next fires, a member's state, a file's
 * size. A trigger's spend today is the Triggers dialog's own line (`spentTodayLine`), so the two
 * screens say it the same way.
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

/** When a schedule next fires, as a person reads it; why it does not for the rest. */
export function nextFireLine(trigger: Pick<SolutionPanelTrigger, 'packageKind' | 'enabled' | 'nextDueAt'>, now = new Date()): string {
  if (!trigger.enabled) return 'Off';
  if (trigger.packageKind === 'event') return 'Fires on its event';
  if (trigger.packageKind === 'folder') return 'Fires when a file changes';
  if (!trigger.nextDueAt) return 'Not scheduled';
  return `Next ${whenWords(trigger.nextDueAt, now)}`;
}

/**
 * A member's state in words, with what its snapshot says when it has something to say: blocked,
 * failed, waiting on a decision, or work queued.
 */
export function memberStateLine(member: Pick<SolutionPanelMember, 'state' | 'blocked' | 'failed' | 'needsDecision' | 'queueDepth'>): string {
  const state = member.state === 'missing' ? 'no longer on the team' : member.state;
  const notes = [
    member.failed ? `failed: ${member.failed}` : '',
    member.blocked ? `blocked: ${member.blocked}` : '',
    member.needsDecision ? `waiting for a decision: ${member.needsDecision}` : '',
    member.queueDepth ? `${member.queueDepth} queued` : '',
  ].filter((note) => note !== '');
  return [state, ...notes].join(' · ');
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

/**
 * AFTER RUN NOW, THE PANEL WATCHES FOR THE RUN TO FINISH: the run route answers as soon as the run
 * is queued, so the panel reads itself again every `RunWatchEveryMs` until a run it had not seen
 * shows in Recent runs - bounded to `RunWatchTries` reads, after which Refresh is still there.
 */
export const RunWatchEveryMs = 2000;
export const RunWatchTries = 90;

export const runKeys = (runs: readonly Pick<SolutionPanelRun, 'member' | 'seq'>[]) =>
  new Set(runs.map((run) => `${run.member}/${run.seq}`));

/** True once `runs` holds a run that is not in `seen`. */
export const hasNewRun = (seen: ReadonlySet<string>, runs: readonly Pick<SolutionPanelRun, 'member' | 'seq'>[]) =>
  runs.some((run) => !seen.has(`${run.member}/${run.seq}`));
