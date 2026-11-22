import { shallowRef } from 'vue';
import { WORKFLOWS_LIST, type ItemTime, type WorkflowCounts, type WorkflowItem, type WorkflowRow, type WorkflowRowStatus, type WorkflowsListAnswer, type YawbleApi } from '../../../shared/api';
import { displayName } from '../faces/faces';
import { categoryLabel, categoryVar, shares } from '../timeline/time';
import { waitSays } from '../waiting';

// B104: the Workflows page's pure parts - the status looks, the text filter and its highlight, the chips, the default
// order and the column sorts, the time bar, the summary line, the filters in the hash and in localStorage, and the
// requests other screens make to open the page (or the Backlog at an item). The core (workflows.list / workflows.get,
// YAWBLE_TEAM_DOCS/B104/contract.md) does the reading; nothing here calls it.

// ---- the Status column ----
export interface StatusLook { status: WorkflowRowStatus; label: string; icon: string; tone: string; open: boolean }
/** Each state's mark on a 16-unit box (drawn with a round stroke), its word and its colour token. */
export const StatusLooks: Record<WorkflowRowStatus, StatusLook> = {
  running: { status: 'running', label: 'Running', icon: 'M6 4.6v6.8l5.4-3.4Z', tone: 'var(--state-running)', open: true },
  'needs-you': { status: 'needs-you', label: 'Needs you', icon: 'M6.2 6.2a1.8 1.8 0 1 1 2.6 1.6c-.6.3-.8.7-.8 1.3 M8 11.3h.01 M8 2.4a5.6 5.6 0 1 0 .01 0', tone: 'var(--state-decision)', open: true },
  waiting: { status: 'waiting', label: 'Waiting', icon: 'M5 2.8h6 M5 13.2h6 M5.6 2.8c0 3 4.8 3 4.8 5.2s-4.8 2.2-4.8 5.2 M10.4 2.8c0 3-4.8 3-4.8 5.2s4.8 2.2 4.8 5.2', tone: 'var(--state-waiting)', open: true },
  idle: { status: 'idle', label: 'Idle', icon: 'M6.4 5v6 M9.6 5v6', tone: 'var(--state-blocked)', open: true },
  stopped: { status: 'stopped', label: 'Stopped', icon: 'M5.4 5.4h5.2v5.2H5.4Z', tone: 'var(--state-stopped)', open: true },
  completed: { status: 'completed', label: 'Completed', icon: 'M4.5 8.5l2.5 2.5 4.5-5', tone: 'var(--ok)', open: false },
  closed: { status: 'closed', label: 'Closed', icon: 'M5.5 5.5l5 5 M10.5 5.5l-5 5', tone: 'var(--state-idle)', open: false },
  expired: { status: 'expired', label: 'Expired', icon: 'M8 3.4a4.6 4.6 0 1 0 .01 0 M8 5.4V8l2 1.2', tone: 'var(--state-idle)', open: false },
};
/** A row's look; a status this build does not know reads as Idle when open, else Closed. */
export function statusLook(w: Pick<WorkflowRow, 'status' | 'state'>): StatusLook {
  return StatusLooks[w.status] ?? (w.state === 'open' ? StatusLooks.idle : StatusLooks.closed);
}
const WaitingFor: Record<string, string> = {
  slot: 'Waiting for a run slot', lease: 'Waiting for the heavy-work lease', wake: 'Waiting for a wake-up',
  // B126 (core 564): undelivered work that is in no run queue says what it waits for, in the waiting.ts words.
  held: 'Waiting: team paused', pending: 'Has work waiting',
};
/** waits-for-run: the sender from the row's waiting worker when one names it, else "another member's run". */
function waitsForRunSays(members: WorkflowRow['members'] | undefined): string {
  const sender = (members ?? []).find((m) => m.state === 'waits-for-run' && m.waitsFor?.trim())?.waitsFor?.trim();
  return `Waiting for ${sender ? `${displayName(sender)}'s` : "another member's"} run to end`;
}
/**
 * B126: a waiting row whose deliveries are not in the run queue waits for what its workers wait for ("Waiting: Developer
 * Ada waits for Tester Ben's run to end"), not for a run slot; null when a worker is queued or none says otherwise.
 */
function workersWait(members: WorkflowRow['members'] | undefined): string | null {
  const ws = members ?? [];
  if (!ws.length || ws.some((m) => m.state === 'queued' || m.state === 'running' || m.state === 'lease')) return null;
  return `Waiting: ${ws.map((m) => `${displayName(m.name)} ${waitSays(m.state, m.waitsFor)}`).join('; ')}`;
}
/** The Status cell's tooltip: what it waits for, or the expiry countdown of an idle one. */
export function statusTip(w: Pick<WorkflowRow, 'status' | 'state' | 'waitingFor' | 'expiresAt' | 'working'> & Partial<Pick<WorkflowRow, 'members'>>, now = Date.now()): string {
  const look = statusLook(w);
  if (w.status === 'waiting') return (w.waitingFor !== 'lease' && w.waitingFor !== 'wake' ? workersWait(w.members) : null)
    ?? (w.waitingFor === 'waits-for-run' ? waitsForRunSays(w.members) : WaitingFor[w.waitingFor ?? ''] ?? 'Waiting');
  if (w.status === 'idle') {
    if (!w.expiresAt) return 'Open, nobody is working it';
    const left = Date.parse(w.expiresAt) - now;
    return `Open, nobody is working it. Expires ${left > 0 ? `in ${countdown(left)}` : 'soon'} if nothing happens in it`;
  }
  if (w.status === 'needs-you') return w.working ? 'Waits on your answer (a run is on it too)' : 'Waits on your answer';
  if (w.status === 'stopped') return 'You stopped its last run';
  return look.label;
}
/** "2d 4h", "5h 12m", "14m": a countdown for a tooltip. */
export function countdown(ms: number): string {
  const m = Math.max(0, Math.floor(ms / 60_000));
  const d = Math.floor(m / 1440), h = Math.floor((m % 1440) / 60), mm = m % 60;
  if (d) return `${d}d ${h}h`;
  if (h) return `${h}h ${mm}m`;
  return `${mm}m`;
}

// ---- the chips ----
export type Chip = 'open' | 'needs-you' | 'done' | 'closed' | 'all';
export const Chips: { id: Chip; label: string; count: keyof WorkflowCounts }[] = [
  { id: 'open', label: 'Open', count: 'open' }, { id: 'needs-you', label: 'Needs you', count: 'needsYou' },
  { id: 'done', label: 'Done', count: 'done' }, { id: 'closed', label: 'Closed/Expired', count: 'closed' }, { id: 'all', label: 'All', count: 'all' },
];
const isChip = (v: unknown): v is Chip => Chips.some((c) => c.id === v);
/** Whether a row belongs under a chip (the core's state filter, applied here too while an answer is on its way). */
export function inChip(w: Pick<WorkflowRow, 'status' | 'state'>, chip: Chip): boolean {
  switch (chip) {
    case 'all': return true;
    case 'open': return w.state === 'open';
    case 'needs-you': return w.status === 'needs-you';
    case 'done': return w.status === 'completed';
    case 'closed': return w.status === 'closed' || w.status === 'expired';
  }
}

// ---- the text filter (the core's rule: every word matches one field) and its highlight ----
export const words = (q: string): string[] => q.trim().toLowerCase().split(/\s+/).filter(Boolean);
function fields(w: WorkflowRow): string[] {
  return [`#${w.id}`, String(w.id), w.title, w.item?.label ?? '', w.item?.title ?? '', w.team, w.owner ?? '', ...w.members.map((m) => m.name)].map((s) => s.toLowerCase());
}
export function matchesText(w: WorkflowRow, q: string): boolean {
  const ws = words(q);
  if (!ws.length) return true;
  const fs = fields(w);
  return ws.every((x) => fs.some((f) => f.includes(x)));
}
/** A text cut into plain and matched parts for the words of `q` (case-insensitive; overlapping matches merged). */
export function highlight(text: string, q: string): { text: string; hit: boolean }[] {
  const ws = words(q);
  if (!ws.length || !text) return [{ text, hit: false }];
  const low = text.toLowerCase();
  const marks = new Array<boolean>(text.length).fill(false);
  for (const x of ws) for (let i = low.indexOf(x); i >= 0; i = low.indexOf(x, i + 1)) marks.fill(true, i, i + x.length);
  const out: { text: string; hit: boolean }[] = [];
  for (let i = 0; i < text.length; i++) {
    const last = out[out.length - 1];
    if (last && last.hit === marks[i]) last.text += text[i];
    else out.push({ text: text[i], hit: marks[i] });
  }
  return out;
}

// ---- the order ----
export type SortKey = 'default' | 'status' | 'id' | 'title' | 'item' | 'team' | 'elapsed' | 'last';
export interface Sort { key: SortKey; desc: boolean }
const SortKeys: SortKey[] = ['default', 'status', 'id', 'title', 'item', 'team', 'elapsed', 'last'];
/** The status column's order: what wants attention first. */
const StatusRank: Record<WorkflowRowStatus, number> = { 'needs-you': 0, running: 1, waiting: 2, idle: 3, stopped: 4, completed: 5, closed: 6, expired: 7 };
const t = (iso: string | null | undefined) => (iso ? Date.parse(iso) || 0 : 0);
/** Default group: needs you, then running, then the other open ones, then the ended ones. */
const group = (w: WorkflowRow) => (w.status === 'needs-you' ? 0 : w.status === 'running' ? 1 : w.state === 'open' ? 2 : 3);
/** An ended row's end (an expired one: its last activity), an open one's last activity. */
const endOf = (w: WorkflowRow) => t(w.state !== 'open' && w.endedAt ? w.endedAt : w.lastAt);
export function elapsedMs(w: Pick<WorkflowRow, 'time'>): number { return w.time?.overallMs ?? 0; }
export function sortRows(rows: readonly WorkflowRow[], sort: Sort): WorkflowRow[] {
  const cmp = (a: WorkflowRow, b: WorkflowRow): number => {
    switch (sort.key) {
      case 'default': return group(a) - group(b) || endOf(b) - endOf(a) || b.id - a.id;
      case 'status': return (StatusRank[a.status] ?? 9) - (StatusRank[b.status] ?? 9);
      case 'id': return a.id - b.id;
      case 'title': return a.title.localeCompare(b.title, undefined, { sensitivity: 'base' });
      case 'item': return !a.item || !b.item ? (a.item ? -1 : 0) + (b.item ? 1 : 0) : a.item.label.localeCompare(b.item.label, undefined, { numeric: true });
      case 'team': return a.team.localeCompare(b.team, undefined, { sensitivity: 'base' });
      case 'elapsed': return elapsedMs(a) - elapsedMs(b);
      case 'last': return t(a.lastAt) - t(b.lastAt);
    }
  };
  const sorted = [...rows].sort((a, b) => cmp(a, b) || b.id - a.id);
  return sort.key !== 'default' && sort.desc ? sorted.reverse() : sorted;
}
/** A header click: the same column flips its direction; a new one starts ascending (times and lengths: longest/newest first). */
export function nextSort(cur: Sort, key: SortKey): Sort {
  if (cur.key === key) return { key, desc: !cur.desc };
  return { key, desc: key === 'last' || key === 'elapsed' || key === 'id' };
}

// ---- the time bar under Elapsed ----
export interface BarSegment { category: string; label: string; ms: number; pct: number; color: string }
/** The breakdown as bar segments in the core's order, each its whole-percent share (adding up to 100); empty for no time. */
export function timeBar(time: ItemTime | null | undefined): BarSegment[] {
  const parts = (time?.breakdown ?? []).filter((b) => b.ms > 0);
  const pct = shares(parts.map((b) => b.ms));
  return parts.map((b, i) => ({ category: b.category, label: categoryLabel(b.category), ms: b.ms, pct: pct[i], color: categoryVar(b.category) }));
}

// ---- the summary line ----
/** "5 open · 1 needs you · 12 completed today". */
export function summaryLine(c: WorkflowCounts | null | undefined): string {
  if (!c) return '';
  return `${c.open} open · ${c.needsYou} need${c.needsYou === 1 ? 's' : ''} you · ${c.completedToday} completed today`;
}
/** Today's workflows per hour (0..23, local time): how many started and how many completed. For the sparkline. */
export function todayByHour(rows: readonly WorkflowRow[], now = Date.now()): { started: number[]; completed: number[] } {
  const day = new Date(now); day.setHours(0, 0, 0, 0);
  const from = day.getTime(), to = from + 86_400_000;
  const started = new Array<number>(24).fill(0), completed = new Array<number>(24).fill(0);
  for (const w of rows) {
    const s = t(w.startedAt);
    if (s >= from && s < to) started[new Date(s).getHours()]++;
    const e = t(w.endedAt);
    if (w.state === 'completed' && e >= from && e < to) completed[new Date(e).getHours()]++;
  }
  return { started, completed };
}

// ---- the filters: in the hash (#workflows?t=3&q=..&s=open&sel=12) and remembered (localStorage) ----
// Keys never named "team" or "workflow": remoteHash.ts reads those as a link into the remote view.
export interface Filters { teamId: number | null; q: string; chip: Chip; sort: Sort }
export const DefaultFilters: Filters = { teamId: null, q: '', chip: 'open', sort: { key: 'default', desc: false } };
export const HashPrefix = '#workflows';
export const StorageKey = 'yawble.workflows';
const posInt = (v: string | null | undefined): number | null => (v && /^[1-9]\d{0,15}$/.test(v) ? Number(v) : null);

export function isWorkflowsHash(hash: string): boolean { return hash === HashPrefix || hash.startsWith(`${HashPrefix}?`); }
/** What a #workflows hash asks for (only the parts it names), or null for another hash. */
export function parseHash(hash: string): (Partial<Filters> & { selected?: number | null }) | null {
  if (!isWorkflowsHash(hash)) return null;
  const p = new URLSearchParams(hash.slice(HashPrefix.length + 1));
  const out: Partial<Filters> & { selected?: number | null } = {};
  if (p.has('t')) out.teamId = posInt(p.get('t'));
  if (p.has('q')) out.q = p.get('q') ?? '';
  const s = p.get('s');
  if (isChip(s)) out.chip = s;
  const o = p.get('o');
  if (o) { const [key, dir] = o.split('.'); if (SortKeys.includes(key as SortKey)) out.sort = { key: key as SortKey, desc: dir === 'desc' }; }
  if (p.has('sel')) out.selected = posInt(p.get('sel'));
  return out;
}
export function formatHash(f: Filters, selected: number | null): string {
  const p = new URLSearchParams();
  if (f.teamId !== null) p.set('t', String(f.teamId));
  if (f.q.trim()) p.set('q', f.q);
  if (f.chip !== DefaultFilters.chip) p.set('s', f.chip);
  if (f.sort.key !== 'default') p.set('o', `${f.sort.key}.${f.sort.desc ? 'desc' : 'asc'}`);
  if (selected !== null) p.set('sel', String(selected));
  const s = p.toString();
  return s ? `${HashPrefix}?${s}` : HashPrefix;
}
export function loadFilters(store: Pick<Storage, 'getItem'> | null = safeStorage()): Filters {
  try {
    const v = JSON.parse(store?.getItem(StorageKey) ?? '{}') as Partial<Filters>;
    return {
      teamId: typeof v.teamId === 'number' && v.teamId > 0 ? v.teamId : null,
      q: typeof v.q === 'string' ? v.q : '',
      chip: isChip(v.chip) ? v.chip : DefaultFilters.chip,
      sort: v.sort && SortKeys.includes(v.sort.key) ? { key: v.sort.key, desc: !!v.sort.desc } : { ...DefaultFilters.sort },
    };
  } catch { return { ...DefaultFilters, sort: { ...DefaultFilters.sort } }; }
}
export function saveFilters(f: Filters, store: Pick<Storage, 'setItem'> | null = safeStorage()): void {
  try { store?.setItem(StorageKey, JSON.stringify(f)); } catch { /* storage blocked: not remembered */ }
}
function safeStorage(): Storage | null { try { return localStorage; } catch { return null; } }

// ---- B098 on the other screens: the backlog item of each workflow (bus.workflows does not carry it) ----
export interface ItemsOf { items: Map<number, WorkflowItem>; backlog: boolean }
/**
 * The items of a team's workflows (or every team's), by workflow id, from one workflows.list; null when the core has no
 * workflows.list (an older core): the screens then show no item, as before.
 */
export async function workflowItems(api: Pick<YawbleApi, 'call'>, o: { teamId?: number; q?: string; state?: string; limit?: number } = {}): Promise<ItemsOf | null> {
  try {
    const a = await api.call<WorkflowsListAnswer>(WORKFLOWS_LIST, { ...o, limit: o.limit ?? 200 });
    const items = new Map<number, WorkflowItem>();
    for (const w of a?.workflows ?? []) if (w.item) items.set(w.id, w.item);
    return { items, backlog: a?.backlog !== false };
  } catch { return null; }
}

// ---- other screens ask to open the page, or the Backlog at an item (App.vue does it) ----
export interface WorkflowsRequest { teamId?: number | null; workflow?: number | null; seq: number }
export interface BacklogRequest { label: string; archived: boolean; seq: number }
let seq = 0;
export const workflowsRequest = shallowRef<WorkflowsRequest | null>(null);
export const backlogRequest = shallowRef<BacklogRequest | null>(null);
/** Opens the Workflows page: filtered to a team (null: all teams), with a workflow selected when given. */
export function openWorkflowsPage(o: { teamId?: number | null; workflow?: number | null } = {}): void { workflowsRequest.value = { ...o, seq: ++seq }; }
/** Opens the Backlog with this item selected (an archived one in the archive view). */
export function openBacklogItem(label: string, archived = false): void { backlogRequest.value = { label, archived, seq: ++seq }; }
