import type { WorkflowSummary } from '../../../shared/api';

/**
 * THE WORKFLOWS ON THE WALL: each team's last few pieces of work (bus.workflows), coloured and marked by how they
 * stand: done (a check), being worked (a play mark), waiting with nobody on it (a pause), closed (a cross). The open
 * ones are nodes on the wall (below); the legend and the screen reader's list name them all. Live only: a replay
 * would have to fake their history, so it shows none.
 */

/** How many workflows each team's poll asks for (bus.workflows limit). */
export const WorkflowsShown = 5;
/** A team's tile in the Teams list: up to this many newest workflows (and every open one, however old), scrolling inside the tile. */
export const TileWorkflowsShown = 50;

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

// ---------- the workflow nodes (B080) ----------

/**
 * Every open workflow is also a node on the wall, tied by lines to the members working it (its busy list), or to its
 * team's hub while nobody is on it, with "#id title" under it (workflowNodeLabel). A node whose workflow completes
 * flashes green and drifts away; one that is closed fades out with a cross; one whose last word was "blocked" wears
 * the blocked look until work on it goes on.
 */

/** A workflow node's label is cut to this many characters ("#id " and the title, with its "…"). */
export const NodeLabelMax = 30;
/** Its words are drawn this size in world px, and never smaller than NodeLabelMinPx on screen. */
export const NodeLabelPx = 9;
export const NodeLabelMinPx = 8;
/** On a phone's wall (narrower than this, as narrowScreen.ts) zoomed out below NodeIdOnlyZoom, a node says only "#id". */
export const PhoneWallPx = 760;
export const NodeIdOnlyZoom = 0.6;

/**
 * A workflow node's label: "#3848 B081 Remote UI: xterm's link…", the title's "Backlog item " dropped (a backlog
 * workflow is titled "Backlog item B081 ..."), cut to `max` characters in all. Only "#id" on a phone's wall zoomed out.
 */
export function workflowNodeLabel(w: Pick<WorkflowSummary, 'id' | 'title'>, opts: { zoom?: number; wallPx?: number; max?: number } = {}): string {
  const { zoom = 1, wallPx = Infinity, max = NodeLabelMax } = opts;
  const id = `#${w.id}`;
  if (wallPx < PhoneWallPx && zoom < NodeIdOnlyZoom) return id;
  const title = w.title.replace(/\s+/g, ' ').trim().replace(/^Backlog item\s+/i, '');
  if (!title) return id;
  const room = Math.max(1, max - id.length - 1);
  return `${id} ${title.length <= room ? title : title.slice(0, room - 1).trimEnd() + '…'}`;
}

/** The size (world px) a workflow node's label is drawn at a zoom: NodeLabelPx, larger when zoomed out so it stays NodeLabelMinPx on screen. */
export function nodeLabelFont(zoom: number): number {
  return zoom > 0 ? Math.max(NodeLabelPx, NodeLabelMinPx / zoom) : NodeLabelPx;
}

/** At most this many workflow nodes per team (the newest open ones). */
export const WorkflowNodesEach = 12;

export type WorkflowNodeStatus = 'working' | 'waiting' | 'blocked';
export interface WorkflowNodeLook { status: WorkflowNodeStatus; tone: WorkflowTone | 'blocked'; icon: WorkflowIcon | 'bang' }

export function workflowNodeLook(w: Pick<WorkflowSummary, 'state' | 'busy'>, blocked: boolean): WorkflowNodeLook {
  if (blocked) return { status: 'blocked', tone: 'blocked', icon: 'bang' };
  return workflowStatus(w) === 'working' ? { status: 'working', tone: 'start', icon: 'play' } : { status: 'waiting', tone: 'handback', icon: 'pause' };
}

/** A team's open workflows, newest first, at most WorkflowNodesEach. */
export function openWorkflows(list: readonly WorkflowSummary[], n = WorkflowNodesEach): WorkflowSummary[] {
  return latestWorkflows(list.filter((w) => w.state === 'open'), n);
}

/** The member names in a workflow's busy list ("DeveloperPriya is running" -> "DeveloperPriya"). */
export function busyMembers(w: Pick<WorkflowSummary, 'busy'>): string[] {
  return w.busy.map((b) => b.trim().split(/\s+/)[0] ?? '').filter((s) => s !== '');
}

/** The workflows that were open before and are completed or closed now (ids absent now are left alone). */
export function workflowEnds(before: readonly WorkflowSummary[], now: readonly WorkflowSummary[]): { id: number; status: 'completed' | 'closed' }[] {
  const was = new Set(before.filter((w) => w.state === 'open').map((w) => w.id));
  return now.filter((w) => was.has(w.id) && w.state !== 'open').map((w) => ({ id: w.id, status: w.state as 'completed' | 'closed' }));
}

const Months =['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];

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
