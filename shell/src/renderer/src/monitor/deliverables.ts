import type { DeliverableSummary } from '../../../shared/api';

/**
 * THE DELIVERABLES ON THE WALL: each team's last few pieces of work (bus.deliverables), coloured and marked by how they
 * stand: done (a check), being worked (a play mark), waiting with nobody on it (a pause), closed (a cross), expired (a clock). The open
 * ones are nodes on the wall (below); the legend and the screen reader's list name them all. Live only: a replay
 * would have to fake their history, so it shows none.
 */

/** How many deliverables each team's poll asks for (bus.deliverables limit). */
export const DeliverablesShown = 5;
/** A team's tile in the Teams list: up to this many newest deliverables (and every open one, however old), scrolling inside the tile. */
export const TileDeliverablesShown = 50;

export type DeliverableStatus = 'completed' | 'working' | 'waiting' | 'closed' | 'expired';
export type DeliverableIcon = 'check' | 'flow' | 'pause' | 'cross' | 'clock';
/**
 * Each state's mark as SVG paths on a 16-unit box, drawn inside a ring of radius 6.6 (the Monitor legend) or cropped to
 * viewBox "3 3 10 10" (a tile's deliverable rows). 'flow', two linked steps, is the deliverable mark the Deliverables buttons
 * carry too (Icon.vue `deliverable`); the canvas draws the same shape (icons.ts).
 */
export const DeliverableIconPath: Record<DeliverableIcon, string> = {
  check: 'M4.5 8.5l2.5 2.5 4.5-5', flow: 'M4.5 4.5h2.8v2.8H4.5Z M8.7 8.7h2.8v2.8H8.7Z M5.9 7.3v2.8h2.8', pause: 'M6.5 5v6M9.5 5v6', cross: 'M5.5 5.5l5 5M10.5 5.5l-5 5',
  clock: 'M8 3.5a4.5 4.5 0 1 0 0.01 0M8 5.5V8l2 1.2',
};
/** A Palette key (scene.ts) for each state's colour. */
export type DeliverableTone = 'done' | 'start' | 'handback' | 'inkDim';
export interface DeliverableLook { status: DeliverableStatus; tone: DeliverableTone; icon: DeliverableIcon; label: string }

/** The five states in the legend's order (expired: closed by the app after the team's idle days). */
export const DeliverableLooks: readonly DeliverableLook[] = [
  { status: 'completed', tone: 'done', icon: 'check', label: 'Completed' },
  { status: 'working', tone: 'start', icon: 'flow', label: 'Being worked' },
  { status: 'waiting', tone: 'handback', icon: 'pause', label: 'Waiting, nobody on it' },
  { status: 'closed', tone: 'inkDim', icon: 'cross', label: 'Closed' },
  { status: 'expired', tone: 'inkDim', icon: 'clock', label: 'Expired' },
];
const byStatus = Object.fromEntries(DeliverableLooks.map((l) => [l.status, l])) as Record<DeliverableStatus, DeliverableLook>;

export function deliverableStatus(w: Pick<DeliverableSummary, 'state' | 'busy'>): DeliverableStatus {
  if (w.state === 'completed') return 'completed';
  if (w.state === 'closed') return 'closed';
  if (w.state === 'expired') return 'expired';
  return w.busy.length > 0 ? 'working' : 'waiting';
}

export function deliverableLook(w: Pick<DeliverableSummary, 'state' | 'busy'>): DeliverableLook {
  return byStatus[deliverableStatus(w)];
}

/** The `n` deliverables with the latest last entry, newest first. */
export function latestDeliverables(list: readonly DeliverableSummary[], n = DeliverablesShown): DeliverableSummary[] {
  return [...list].sort((a, b) => Date.parse(b.lastAt) - Date.parse(a.lastAt) || b.id - a.id).slice(0, n);
}

/** "#12 Add a README", the title cut to 28 characters. */
export function deliverableLabel(w: Pick<DeliverableSummary, 'id' | 'title'>, max = 28): string {
  const t = w.title.replace(/\s+/g, ' ').trim();
  return `#${w.id} ${t.length <= max ? t : t.slice(0, max - 1) + '…'}`;
}

// ---------- the deliverable nodes ----------

/**
 * Every open deliverable is also a node on the wall, tied by lines to the members working it (its busy list), or to its
 * team's hub while nobody is on it, with "#id title" under it (deliverableNodeLabel). A node whose deliverable completes
 * flashes green and drifts away; one that is closed fades out with a cross; one whose last word was "blocked" wears
 * the blocked look until work on it goes on.
 */

/** A deliverable node's label is cut to this many characters ("#id " and the title, with its "…"). */
export const NodeLabelMax = 30;
/** Its words are drawn this size in world px, and never smaller than NodeLabelMinPx on screen. */
export const NodeLabelPx = 9;
export const NodeLabelMinPx = 8;
/** On a phone zoomed out below this, a node says only "#id". */
export const NodeIdOnlyZoom = 0.6;

/**
 * A deliverable node's label: "#3848 B008 Remote UI: xterm's…", the title's "Backlog item " dropped (a backlog
 * deliverable is titled "Backlog item B008 ..."), cut to `max` characters in all. Only "#id" on a phone zoomed out:
 * `phone` is the window being phone-wide (narrowScreen.ts), not the wall's canvas, which is as narrow on a 1280 px
 * desktop with the sidebar and Activity open.
 */
export function deliverableNodeLabel(w: Pick<DeliverableSummary, 'id' | 'title'>, opts: { zoom?: number; phone?: boolean; max?: number } = {}): string {
  const { zoom = 1, phone = false, max = NodeLabelMax } = opts;
  const id = `#${w.id}`;
  if (phone && zoom < NodeIdOnlyZoom) return id;
  const title = w.title.replace(/\s+/g, ' ').trim().replace(/^Backlog item\s+/i, '');
  if (!title) return id;
  const room = Math.max(1, max - id.length - 1);
  return `${id} ${title.length <= room ? title : title.slice(0, room - 1).trimEnd() + '…'}`;
}

/** A label box (world px): its left, top, width and height. */
export interface LabelBox { x: number; y: number; w: number; h: number }
/** Something a label must not cover; one with an owner (a team) only keeps out the other teams' labels. */
export interface LabelObstacle extends LabelBox { owner?: string }

/**
 * A deliverable node whose label is to be placed: where it is, its full label and its short one ("#id"), its rank, and its
 * team (owner).
 */
export interface LabelAsk { key: string; x: number; y: number; r: number; full: string; short: string; rank: number; owner?: string }
export interface PlacedLabel extends LabelBox { text: string; above: boolean }

/** How much two labels (or a label and an obstacle) may touch, in world px, before they count as overlapping. */
const LabelSlack = 1;

function overlaps(a: LabelBox, b: LabelBox): boolean {
  return a.x < b.x + b.w - LabelSlack && b.x < a.x + a.w - LabelSlack && a.y < b.y + b.h - LabelSlack && b.y < a.y + a.h - LabelSlack;
}

/**
 * Where each deliverable node's label goes so that none covers another. The nodes go in rank order (the lower
 * the rank the first: the brighter and newer ones); each takes the first of these that is clear of the labels placed
 * before it, of every deliverable node's square and of the obstacles (the members' discs and names, and the other teams'
 * code maps, so a label does not run into the next cluster): its full label centred
 * under the node, above it, then running off to one side under or above it; then its short label the same ways. A node
 * with no clear place shows no label (its hover
 * still gives the whole title). `measure` gives a text's width in world px; `h` is a label's height.
 */
export function placeDeliverableLabels(asks: readonly LabelAsk[], measure: (text: string) => number, h: number, obstacles: readonly LabelObstacle[] = []): Map<string, PlacedLabel | null> {
  const squares = asks.map((a) => ({ key: a.key, box: { x: a.x - a.r, y: a.y - a.r, w: a.r * 2, h: a.r * 2 } }));
  const placed: LabelBox[] = [];
  const out = new Map<string, PlacedLabel | null>();
  for (const a of [...asks].sort((p, q) => p.rank - q.rank || (p.key < q.key ? -1 : p.key > q.key ? 1 : 0))) {
    const texts = a.short && a.short !== a.full ? [a.full, a.short] : [a.full];
    let got: PlacedLabel | null = null;
    for (const text of texts) {
      const w = measure(text);
      // Centred under the node, then above it; then from the node's left edge on (or to its right edge), under, then above.
      const spots: [number, boolean][] = [[a.x - w / 2, false], [a.x - w / 2, true], [a.x - a.r, false], [a.x + a.r - w, false], [a.x - a.r, true], [a.x + a.r - w, true]];
      for (const [x, above] of spots) {
        const box = { x, y: above ? a.y - a.r - 3 - h : a.y + a.r + 3, w, h };
        const clear = !placed.some((p) => overlaps(p, box))
          && !squares.some((s) => s.key !== a.key && overlaps(s.box, box))
          && !obstacles.some((o) => (o.owner === undefined || o.owner !== a.owner) && overlaps(o, box));
        if (clear) { got = { ...box, text, above }; break; }
      }
      if (got) break;
    }
    if (got) placed.push(got);
    out.set(a.key, got);
  }
  return out;
}

/** The size (world px) a deliverable node's label is drawn at a zoom: NodeLabelPx, larger when zoomed out so it stays NodeLabelMinPx on screen. */
export function nodeLabelFont(zoom: number): number {
  return zoom > 0 ? Math.max(NodeLabelPx, NodeLabelMinPx / zoom) : NodeLabelPx;
}

/** At most this many deliverable nodes per team (the newest open ones). */
export const DeliverableNodesEach = 12;

export type DeliverableNodeStatus = 'working' | 'waiting' | 'blocked';
export interface DeliverableNodeLook { status: DeliverableNodeStatus; tone: DeliverableTone | 'blocked'; icon: DeliverableIcon | 'bang' }

export function deliverableNodeLook(w: Pick<DeliverableSummary, 'state' | 'busy'>, blocked: boolean): DeliverableNodeLook {
  if (blocked) return { status: 'blocked', tone: 'blocked', icon: 'bang' };
  return deliverableStatus(w) === 'working' ? { status: 'working', tone: 'start', icon: 'flow' } : { status: 'waiting', tone: 'handback', icon: 'pause' };
}

/** A team's open deliverables, newest first, at most DeliverableNodesEach. */
export function openDeliverables(list: readonly DeliverableSummary[], n = DeliverableNodesEach): DeliverableSummary[] {
  return latestDeliverables(list.filter((w) => w.state === 'open'), n);
}

/** The member names in a deliverable's busy list ("DeveloperPriya is running" -> "DeveloperPriya"). */
export function busyMembers(w: Pick<DeliverableSummary, 'busy'>): string[] {
  return w.busy.map((b) => b.trim().split(/\s+/)[0] ?? '').filter((s) => s !== '');
}

/** The deliverables that were open before and are completed, closed or expired now (ids absent now are left alone). */
export function deliverableEnds(before: readonly DeliverableSummary[], now: readonly DeliverableSummary[]): { id: number; status: 'completed' | 'closed' | 'expired' }[] {
  const was = new Set(before.filter((w) => w.state === 'open').map((w) => w.id));
  return now.filter((w) => was.has(w.id) && w.state !== 'open').map((w) => ({ id: w.id, status: w.state as 'completed' | 'closed' | 'expired' }));
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
