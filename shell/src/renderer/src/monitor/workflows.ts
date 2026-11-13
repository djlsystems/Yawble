import type { WorkflowSummary } from '../../../shared/api';

/**
 * THE WORKFLOWS ON THE WALL: each team's last few pieces of work (bus.workflows) sit in one panel off its hub, a
 * row each, last updated first, coloured and marked by how they stand: done (a check), being worked (a play mark in a turning ring), waiting with
 * nobody on it (a pause), closed (a cross). Live only: a replay would have to fake their history, so it shows none.
 */

/** How many workflows each team shows. */
export const WorkflowsShown = 5;

export type WorkflowStatus = 'completed' | 'working' | 'waiting' | 'closed';
export type WorkflowIcon = 'check' | 'play' | 'pause' | 'cross';
/** A Palette key (scene.ts) for each state's colour. */
export type WorkflowTone = 'done' | 'start' | 'handback' | 'inkDim';
export interface WorkflowLook { status: WorkflowStatus; tone: WorkflowTone; icon: WorkflowIcon; label: string }

/** The four states in the legend's order. */
export const WorkflowLooks: readonly WorkflowLook[] = [
  { status: 'completed', tone: 'done', icon: 'check', label: 'Completed' },
  { status: 'working', tone: 'start', icon: 'play', label: 'Being worked' },
  { status: 'waiting', tone: 'handback', icon: 'pause', label: 'Waiting, nobody on it' },
  { status: 'closed', tone: 'inkDim', icon: 'cross', label: 'Closed' },
];
const byStatus = Object.fromEntries(WorkflowLooks.map((l) => [l.status, l])) as Record<WorkflowStatus, WorkflowLook>;

export function workflowStatus(w: Pick<WorkflowSummary, 'state' | 'busy'>): WorkflowStatus {
  if (w.state === 'completed') return 'completed';
  if (w.state === 'closed') return 'closed';
  return w.busy.length > 0 ? 'working' : 'waiting';
}

export function workflowLook(w: Pick<WorkflowSummary, 'state' | 'busy'>): WorkflowLook {
  return byStatus[workflowStatus(w)];
}

/** The `n` workflows with the latest last entry, newest first. */
export function latestWorkflows(list: readonly WorkflowSummary[], n = WorkflowsShown): WorkflowSummary[] {
  return [...list].sort((a, b) => Date.parse(b.lastAt) - Date.parse(a.lastAt) || b.id - a.id).slice(0, n);
}

/** "#12 Add a README", the title cut to 28 characters. */
export function workflowLabel(w: Pick<WorkflowSummary, 'id' | 'title'>, max = 28): string {
  const t = w.title.replace(/\s+/g, ' ').trim();
  return `#${w.id} ${t.length <= max ? t : t.slice(0, max - 1) + '…'}`;
}

const Months = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];

/** A time compactly: "12:41 PM" today, "Nov 10 12:41 PM" another day (local time). */
export function entryTime(iso: string, now = Date.now()): string {
  const ms = Date.parse(iso);
  if (Number.isNaN(ms)) return '';
  const d = new Date(ms), today = new Date(now);
  const h = d.getHours();
  const clock = `${h % 12 || 12}:${String(d.getMinutes()).padStart(2, '0')} ${h < 12 ? 'AM' : 'PM'}`;
  const sameDay = d.getFullYear() === today.getFullYear() && d.getMonth() === today.getMonth() && d.getDate() === today.getDate();
  return sameDay ? clock : `${Months[d.getMonth()]} ${d.getDate()} ${clock}`;
}

// ---------- the team's workflow panel ----------

/** A team's workflow panel at a scale (world units): its size, padding, header and row heights, and row count. */
export interface PanelLayout { w: number; h: number; pad: number; headerH: number; rowH: number; rows: number }

const PanelW = 236, PanelPad = 7, PanelHeader = 17, PanelRow = 17;
/** The panel's words are drawn this size (world px at scale 1); they are never shown smaller than PanelMinPx on screen. */
export const PanelFontPx = 10;
const PanelMinPx = 8;
const PanelMaxScale = 2.4;

export function panelLayout(rows: number, scale = 1): PanelLayout {
  const n = Math.max(0, rows);
  const pad = PanelPad * scale, headerH = PanelHeader * scale, rowH = PanelRow * scale;
  return { w: PanelW * scale, h: pad * 2 + headerH + n * rowH, pad, headerH, rowH, rows: n };
}

/** The radius of the one body the panel is in the layout: the circle round it. */
export function panelRadius(l: PanelLayout): number {
  return Math.hypot(l.w, l.h) / 2;
}

/** What is under a point of the panel (from its top-left): its header, a row (by index), the rest of it, or null. */
export function panelHit(l: PanelLayout, x: number, y: number): 'header' | 'panel' | number | null {
  if (x < 0 || y < 0 || x > l.w || y > l.h) return null;
  if (y < l.pad + l.headerH) return 'header';
  const i = Math.floor((y - l.pad - l.headerH) / l.rowH);
  return i >= 0 && i < l.rows ? i : 'panel';
}

/** How much larger than its world size the panel is drawn at a zoom, so its words stay readable zoomed out. */
export function panelScale(camK: number): number {
  if (!(camK > 0)) return PanelMaxScale;
  return Math.min(PanelMaxScale, Math.max(1, PanelMinPx / (PanelFontPx * camK)));
}
