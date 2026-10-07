import type {
  InstalledSolution,
  SolutionPanelMember,
  SolutionPanelRun,
  SolutionPanelTrigger,
  SolutionState,
  SolutionStateKind,
} from '../api/types';
import { filterWords, matchesWords } from './filterWords';
import { capWords } from './solutions';
import { triggerSentence } from './triggers';
import { clockWords, cronWords, rawCron } from './scheduleWords';

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

/**
 * "8:51 PM" today, "Thu, Oct 8 8:00 AM" on another day, in the browser's own zone and format; the
 * input itself when it is not a date.
 */
export function whenWords(at: string, now = new Date()): string {
  const when = new Date(at);
  if (Number.isNaN(when.getTime())) return at;
  const time = clockWords(when.getTime());
  if (when.toDateString() === now.toDateString()) return time;
  const day = when.toLocaleDateString(undefined, { weekday: 'short', month: 'short', day: 'numeric' });
  return `${day} ${time}`;
}

/**
 * What the daily cap is holding, on the panel's clock: a schedule asleep until the next day says
 * when it resumes in the reader's own zone; otherwise how many fires the cap skipped today. Null
 * when the cap is holding nothing.
 */
export function cappedWords(
  trigger: Pick<SolutionPanelTrigger, 'cappedUntil' | 'skippedToday'>,
  now = new Date(),
): string | null {
  if (trigger.cappedUntil && !Number.isNaN(Date.parse(trigger.cappedUntil))) return `Capped until ${whenWords(trigger.cappedUntil, now)}`;
  return trigger.skippedToday ? `skipped today: ${trigger.skippedToday}` : null;
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

// --- The launcher's filter ------------------------------------------------------------------------

/** What the launcher's filter holds: free words, one team (or all), one state (or all). */
export interface SolutionFilter {
  text: string | null;
  team: string | null;
  state: SolutionStateKind | null;
}

/**
 * Whether a tile is shown: every word found in its name, package id, version, team name or status
 * line, and its team and state the chosen ones when chosen. It only decides what is shown; the rows
 * themselves are never changed.
 */
export function solutionMatches(row: InstalledSolution, filter: SolutionFilter): boolean {
  if (filter.team && row.team !== filter.team) return false;
  if (filter.state && row.state?.kind !== filter.state) return false;
  return matchesWords(filterWords(filter.text), row.name, row.id, row.version, row.teamName, row.status);
}

/** The Team choices: each team that has a tile, by its name. */
export function teamChoices(rows: readonly InstalledSolution[]): { label: string; value: string }[] {
  return rows
    .map((row) => ({ label: row.teamName, value: row.team }))
    .sort((a, b) => a.label.localeCompare(b.label));
}

/** The State choices: only the states some tile is in, in the badge's order, each in the badge's words. */
export function stateChoices(rows: readonly InstalledSolution[]): { label: string; value: SolutionStateKind }[] {
  const order: SolutionStateKind[] = ['running', 'idle', 'blocked', 'paused', 'capped'];
  const present = new Set(rows.map((row) => row.state?.kind).filter((kind) => kind !== undefined));
  return order
    .filter((kind) => present.has(kind))
    .map((kind) => ({ label: stateBadge({ kind, reason: null })!.text, value: kind }));
}

// --- A trigger's Details ----------------------------------------------------------------------------

/** What a trigger's tile shows of its instruction: the text before the first line break, unchanged. */
export function instructionFirstLine(instruction: string | null | undefined): string {
  const text = instruction ?? '';
  const end = text.search(/\r?\n/);
  return end < 0 ? text : text.slice(0, end);
}

export interface TriggerFact {
  label: string;
  value: string;
}

/**
 * The facts a trigger's Details lists under its whole instruction, each a label and its text: what
 * fires it, its event filter as written, its timezone, on or off, its daily cap, when it last fired and how that went, and how
 * many fires it missed.
 */
export function triggerFacts(trigger: SolutionPanelTrigger, now = new Date()): TriggerFact[] {
  // A cron in words in the reader's own zone; the raw cron is its own fact, for whoever wants it.
  const cron = trigger.kind === 'cron' && trigger.expression ? trigger.expression : null;
  const fires = (cron && cronWords(cron, trigger.timezone, now)) || triggerSentence(trigger);
  const facts: TriggerFact[] = [{ label: 'Fires', value: fires }];
  if (cron) facts.push({ label: 'Cron', value: rawCron(cron, trigger.timezone) });
  if (trigger.filter) facts.push({ label: 'Filter', value: trigger.filter });
  if (trigger.timezone) facts.push({ label: 'Timezone', value: trigger.timezone });
  facts.push({ label: 'On', value: trigger.enabled ? 'Yes' : 'No, it is off' });
  facts.push({ label: 'Daily cap', value: capWords(trigger.dailyTokenCap ?? null) });
  facts.push({ label: 'Last fired', value: trigger.lastFiredAt ? whenWords(trigger.lastFiredAt, now) : 'Never' });
  if (trigger.lastOutcome) facts.push({ label: 'Last outcome', value: trigger.lastOutcome });
  if (trigger.missedCount > 0) facts.push({ label: 'Missed', value: `${trigger.missedCount}` });
  return facts;
}
