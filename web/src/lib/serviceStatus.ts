/**
 * THE STATUS CONTROL'S WORDS: the live chip on the app bar and its dropdown. Pure, so a spec reads
 * them without a mount; the control (`LiveStatusControl.vue`) only renders them.
 *
 * Two different facts, kept apart on purpose. LIVE is this window's hub connection: whether the
 * board in front of the person is receiving pushes now. The SERVICE is the instance's own state from
 * `GET /api/service-status`: how long it has been up, whether it can serve, which build, which
 * workers. The chip's colour is the first alone - a healthy instance this window has lost reads
 * offline, because that is what the person's screen is - and the dropdown says both.
 */
import type { ServiceCheck, ServiceWorker } from '../api/types';

/** The chip's icon: a radiating dot while live, the same struck through when not. */
export const LiveIcons = { live: 'sensors', offline: 'sensors_off' } as const;

/** The chip's word, said on the chip or in its tooltip when the chip is a dot. */
export function liveWord(connected: boolean): 'live' | 'offline' {
  return connected ? 'live' : 'offline';
}

/**
 * The connection line in the dropdown. `established` is whether the hub has ever connected in this
 * window (`console.connectionEstablished`); `since` is when the connection last changed, null when
 * it never has.
 */
export function connectionWords(connected: boolean, established: boolean, since: string | null): string {
  if (connected) return since ? `Live: this window receives updates, since ${since}.` : 'Live: this window receives updates.';
  if (!established) return 'Connecting: this window has not received updates yet.';
  return since
    ? `Offline since ${since}: what is on screen may be out of date.`
    : 'Offline: what is on screen may be out of date.';
}

/**
 * How long the instance has been up, from its start to the Host's own `now` (never this browser's
 * clock): days and hours, hours and minutes, minutes, or "under a minute". A start the Host could
 * not read, or one after now, is "not known".
 */
export function upWords(startedAt: string | null, now: string): string {
  if (!startedAt) return 'not known';
  const ms = Date.parse(now) - Date.parse(startedAt);
  if (!Number.isFinite(ms) || ms < 0) return 'not known';
  const minutes = Math.floor(ms / 60_000);
  if (minutes < 1) return 'under a minute';
  const days = Math.floor(minutes / 1440);
  const hours = Math.floor((minutes % 1440) / 60);
  const rest = minutes % 60;
  const parts: string[] = [];
  if (days > 0) parts.push(plural(days, 'day'));
  if (hours > 0) parts.push(plural(hours, 'hour'));
  if (days === 0 && rest > 0) parts.push(plural(rest, 'minute'));
  return parts.join(' ');
}

function plural(n: number, unit: string): string {
  return `${n} ${unit}${n === 1 ? '' : 's'}`;
}

/** What a person reads for each of /healthz's checks, in place of its wire name. */
export const CheckNames: Record<string, string> = {
  database: 'Database',
  dataRoot: 'Data folder',
  pump: 'Delivery pump',
};

/** One check as a line: its name and the Host's own detail ("Database: open", "Delivery pump: no pass for over a minute"). */
export function checkWords(check: ServiceCheck): string {
  return `${CheckNames[check.name] ?? check.name}: ${check.detail}`;
}

/** One worker as a line, its times already in the reader's words. */
export function workerWords(worker: ServiceWorker, connectedSince: string, droppedAt: string | null): string {
  const build = worker.version ? `, ${worker.version}` : '';
  if (worker.state === 'dropped') return `${worker.id}: dropped${droppedAt ? ` at ${droppedAt}` : ''}${build}`;
  return `${worker.id}: connected since ${connectedSince}${build}`;
}

/** How often the dropdown re-reads the service while it is open. */
export const ReadEveryMs = 30_000;
