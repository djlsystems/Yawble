import { fileName, type StepRow } from './steps';

/**
 * THE CODE MAP: one tree per team - the repository, its folders, their files - built up over the life of a workflow
 * from two sources.
 *   - The members' steps (monitor.steps) are immediate and provisional: a Write makes a file, an Edit lights it bright,
 *     a Read lights it faint. They are the only source for reads.
 *   - The code events (monitor.codeEvents, from the core) are the truth: a commit's files confirm, add or delete
 *     nodes and override what the steps said (a Write over a file the commit says was modified is an edit, not a new
 *     file; a file the commit deleted is gone, and a step older than that commit does not bring it back).
 * A file leaves when every workflow that touched it has ended, or when it has been idle for MapIdleMs (log time, so a
 * replay prunes as the past did), fading out over the last MapFadeMs of that (idleFade). A large map collapses to its busiest folders, each with a "+N" for the rest.
 * Everything here is plain data and arithmetic, so the scene can draw hundreds of files at 60 fps and the tests can
 * build it without a canvas.
 */

export type CodeEventKind = 'commit' | 'merge-team' | 'landed' | 'push';
/** A file's change in a commit: Added, Modified, Deleted or Renamed (git's C and T read as A and M). */
export type FileStatus = 'A' | 'M' | 'D' | 'R';
export interface CodeFile { path: string; status: FileStatus; from: string | null }

/** One event as monitor.codeEvents returns it (oldest first). */
export interface CodeEvent {
  kind: CodeEventKind; team: string; member: string | null; branch: string; sha: string; message: string;
  files: CodeFile[]; at: string; workflowId: number | null;
}

const Kinds: readonly CodeEventKind[] = ['commit', 'merge-team', 'landed', 'push'];

/** A path as the map keys it: forward slashes, no leading "./" or "/". */
export function normPath(p: string): string {
  return p.replace(/\\/g, '/').replace(/^(\.\/)+/, '').replace(/^\/+/, '');
}

function readStatus(s: unknown): FileStatus {
  const c = typeof s === 'string' ? s.trim().charAt(0).toUpperCase() : '';
  return c === 'A' || c === 'C' ? 'A' : c === 'D' ? 'D' : c === 'R' ? 'R' : 'M';
}

/** An event as the wire sent it, or null when it is not one: the Monitor never trusts the shape. */
export function readCodeEvent(raw: unknown): CodeEvent | null {
  if (typeof raw !== 'object' || raw === null) return null;
  const e = raw as Record<string, unknown>;
  const text = (v: unknown) => (typeof v === 'string' ? v : '');
  const kind = text(e.kind) as CodeEventKind;
  if (!Kinds.includes(kind) || !text(e.team) || !text(e.at) || !Number.isFinite(Date.parse(text(e.at)))) return null;
  const files: CodeFile[] = [];
  if (Array.isArray(e.files))
    for (const f of e.files) {
      if (typeof f !== 'object' || f === null) continue;
      const r = f as Record<string, unknown>;
      let path = text(r.path);
      let from = text(r.from) || text(r.oldPath) || null;
      // "old<TAB>new", as git prints a rename.
      if (!from && path.includes('\t')) [from, path] = path.split('\t', 2) as [string, string];
      if (!path) continue;
      files.push({ path: normPath(path), status: readStatus(r.status), from: from ? normPath(from) : null });
    }
  const wf = e.workflowId;
  return {
    kind, team: text(e.team), member: text(e.member) || null, branch: text(e.branch), sha: text(e.sha), message: text(e.message),
    files, at: text(e.at), workflowId: typeof wf === 'number' && Number.isFinite(wf) ? wf : null,
  };
}

/** The read's answer as events, oldest first: an array, or `{ events: [...] }`; anything else is none. */
export function readCodeEvents(res: unknown): CodeEvent[] {
  const list = Array.isArray(res) ? res : typeof res === 'object' && res !== null && Array.isArray((res as { events?: unknown }).events) ? (res as { events: unknown[] }).events : [];
  const out: CodeEvent[] = [];
  for (const raw of list) { const e = readCodeEvent(raw); if (e) out.push(e); }
  return out.sort((a, b) => Date.parse(a.at) - Date.parse(b.at));
}

/** What tells two events apart (the read is polled with an overlapping `since`). */
export const codeEventKey = (e: Pick<CodeEvent, 'kind' | 'team' | 'branch' | 'sha' | 'at'>) => `${e.kind}|${e.team.toLowerCase()}|${e.branch}|${e.sha}|${e.sha ? '' : e.at}`;

export const shortSha = (sha: string) => sha.slice(0, 7);

// --- the map --------------------------------------------------------------------------------------------------------

/** How long a file stays on the map after its last touch (log time): two minutes, the last MapFadeMs of it fading out. */
export const MapIdleMs = 2 * 60_000;
export const MapFadeMs = 20_000;

/** How much of a file is still shown, 1 down to 0 over the last MapFadeMs before it goes idle (log time). */
export function idleFade(f: { lastAt: number }, logNow: number, idleMs = MapIdleMs): number {
  const left = idleMs - (logNow - f.lastAt);
  return Math.max(0, Math.min(1, left / Math.min(MapFadeMs, idleMs)));
}

export type TouchKind = 'read' | 'edit' | 'create';

/** A member's touches of one file. */
export interface MemberTouch { member: string; kind: TouchKind; at: number }

export interface MapFile {
  team: string; path: string; name: string; folder: string;
  /** Log times (ms): first seen, last touched, last changed, last read. */
  firstAt: number; lastAt: number; editAt: number; readAt: number;
  /** Display times (performance.now()) of its birth and of its last change and read, for the pop and the light; -Infinity when applied quietly. */
  bornShown: number; editShown: number; readShown: number;
  /** A new file (a Write, or a commit's A). */
  created: boolean;
  /** A commit has said it exists: the steps no longer decide whether it is new. */
  confirmed: boolean;
  sha: string | null;
  /** Who touched it, by member name in lower case. */
  members: Map<string, MemberTouch>;
  /** The workflows it was touched in (it leaves when they have all ended). */
  workflows: Set<number>;
  touches: number;
}

interface TeamTree { name: string; files: Map<string, MapFile>; deleted: Map<string, number>; ended: Set<number> }

/** The folder of a path ("" for the repository's root). */
export function folderOf(path: string): string {
  const i = path.lastIndexOf('/');
  return i < 0 ? '' : path.slice(0, i);
}

const lower = (s: string) => s.toLowerCase();
const Rank: Record<TouchKind, number> = { read: 0, edit: 1, create: 2 };

export class CodeMap {
  private teams = new Map<string, TeamTree>();
  /** Moves whenever a file comes or goes or a node's look changes; the scene re-lays the map only then. */
  rev = 0;
  /** How long a file stays after its last touch, log time. */
  idleMs = MapIdleMs;

  private tree(team: string): TeamTree {
    const k = lower(team);
    let t = this.teams.get(k);
    if (!t) this.teams.set(k, (t = { name: team, files: new Map(), deleted: new Map(), ended: new Set() }));
    return t;
  }

  private file(t: TeamTree, path: string, at: number, shown: number): { f: MapFile; fresh: boolean } {
    const known = t.files.get(path);
    if (known) return { f: known, fresh: false };
    const f: MapFile = {
      team: t.name, path, name: fileName(path), folder: folderOf(path),
      firstAt: at, lastAt: at, editAt: -Infinity, readAt: -Infinity, bornShown: shown, editShown: -Infinity, readShown: -Infinity,
      created: false, confirmed: false, sha: null, members: new Map(), workflows: new Set(), touches: 0,
    };
    t.files.set(path, f);
    this.rev++;
    return { f, fresh: true };
  }

  private touchMember(f: MapFile, member: string | null, kind: TouchKind, at: number): void {
    if (!member) return;
    const k = lower(member);
    const was = f.members.get(k);
    // A member's touch keeps its strongest kind: having changed a file, a later read does not make it a reader.
    const strongest = was && Rank[was.kind] > Rank[kind] ? was.kind : kind;
    if (!was) this.rev++;
    f.members.set(k, { member, kind: strongest, at: Math.max(at, was?.at ?? -Infinity) });
  }

  /**
   * A step: a Write makes the file (new unless a commit has already said it exists), an Edit lights it bright, a Read
   * faint. `shown` is the display time when it is played, null when applied quietly (no pop, no light).
   */
  touchStep(step: Pick<StepRow, 'team' | 'member' | 'kind' | 'target' | 'at'>, workflow: number | null, shown: number | null): MapFile | null {
    if (!step.target || (step.kind !== 'read' && step.kind !== 'edit' && step.kind !== 'create')) return null;
    const path = normPath(step.target);
    if (!path || path.includes('…')) return null; // the core clips a very long path: it would never meet its commit
    const at = Date.parse(step.at);
    const t = this.tree(step.team);
    // A commit deleted it after this step: the commit wins.
    const gone = t.deleted.get(path);
    if (gone !== undefined) { if (at <= gone) return null; t.deleted.delete(path); }
    // A read of a file nobody changed is only drawn while it lives; it never makes a node "new".
    const { f, fresh } = this.file(t, path, at, shown ?? -Infinity);
    if (fresh && step.kind === 'create') f.created = true;
    f.lastAt = Math.max(f.lastAt, at);
    f.touches++;
    if (step.kind === 'read') { f.readAt = Math.max(f.readAt, at); if (shown !== null) f.readShown = shown; }
    else {
      if (f.editAt === -Infinity) this.rev++; // it now draws as a changed file
      f.editAt = Math.max(f.editAt, at);
      if (shown !== null) f.editShown = shown;
    }
    this.touchMember(f, step.member, step.kind, at);
    if (workflow !== null && !f.workflows.has(workflow)) { f.workflows.add(workflow); }
    return f;
  }

  /**
   * A code event. A commit's (and a merge's) files are the truth: A adds or confirms a new file, M confirms a changed
   * one (no longer "new" if only a step said so), D removes it, R removes the old path and adds the new. A push
   * changes no file. Returns the files it lit, for the commit's dot to gather.
   */
  applyCode(ev: CodeEvent, shown: number | null): MapFile[] {
    if (ev.kind === 'push') return [];
    const t = this.tree(ev.team);
    const at = Date.parse(ev.at);
    const lit: MapFile[] = [];
    const remove = (path: string) => {
      t.deleted.set(path, Math.max(at, t.deleted.get(path) ?? -Infinity));
      if (t.files.delete(path)) this.rev++;
    };
    for (const c of ev.files) {
      if (c.status === 'D') { remove(c.path); continue; }
      if (c.status === 'R' && c.from && c.from !== c.path) remove(c.from);
      t.deleted.delete(c.path);
      const { f, fresh } = this.file(t, c.path, at, shown ?? -Infinity);
      // A merge's files confirm what is there; only a commit says who made it.
      if (ev.kind === 'commit' || fresh) {
        const created = c.status === 'A' || (c.status === 'R' && fresh);
        if (f.created !== created) this.rev++;
        f.created = created;
      }
      if (!f.confirmed) this.rev++;
      f.confirmed = true;
      f.sha = ev.sha || f.sha;
      f.lastAt = Math.max(f.lastAt, at);
      if (f.editAt === -Infinity) this.rev++;
      f.editAt = Math.max(f.editAt, at);
      if (shown !== null) f.editShown = shown;
      f.touches++;
      if (ev.kind === 'commit') this.touchMember(f, ev.member, f.created ? 'create' : 'edit', at);
      if (ev.workflowId !== null) f.workflows.add(ev.workflowId);
      lit.push(f);
    }
    return lit;
  }

  /** A workflow ended: the files only it touched leave the map. */
  endWorkflow(team: string, workflow: number): void {
    const t = this.teams.get(lower(team));
    if (!t) return;
    t.ended.add(workflow);
    for (const [path, f] of t.files) {
      if (!f.workflows.size || !f.workflows.has(workflow)) continue;
      if ([...f.workflows].every((w) => t.ended.has(w))) { t.files.delete(path); this.rev++; }
    }
  }

  /** Forgets the files idle for longer than the idle time at log time `logNow`, and the deletions that old. */
  prune(logNow: number): void {
    for (const [k, t] of this.teams) {
      for (const [path, f] of t.files) if (logNow - f.lastAt > this.idleMs) { t.files.delete(path); this.rev++; }
      for (const [path, at] of t.deleted) if (logNow - at > this.idleMs) t.deleted.delete(path);
      if (!t.files.size && !t.deleted.size) this.teams.delete(k);
    }
  }

  /** The file at a path, if it is on the map. */
  get(team: string, path: string): MapFile | undefined { return this.teams.get(lower(team))?.files.get(normPath(path)); }

  files(team: string): MapFile[] { return [...(this.teams.get(lower(team))?.files.values() ?? [])]; }

  /** The teams (as named) that have files on the map. */
  teamNames(): string[] { return [...this.teams.values()].filter((t) => t.files.size).map((t) => t.name); }

  get size(): number {
    let n = 0;
    for (const t of this.teams.values()) n += t.files.size;
    return n;
  }

  clear(): void { this.teams.clear(); this.rev++; }

  /** A team's map as drawn: its busiest folders, each with its files and a "+N" for the rest. */
  view(team: string, limits: Partial<ViewLimits> = {}): TeamMapView {
    return collapse(team, this.files(team), { ...DefaultLimits, ...limits });
  }
}

// --- the collapsed view ---------------------------------------------------------------------------------------------

export interface ViewLimits { maxFiles: number; maxFolders: number; perFolder: number }
/** A team's map shows at most this many file nodes, in at most this many folders, at most perFolder each once it is large. */
export const DefaultLimits: Readonly<ViewLimits> = { maxFiles: 48, maxFolders: 8, perFolder: 8 };
/** All the teams' maps together show at most this many file nodes (split evenly between the teams with files). */
export const MaxMapNodes = 240;

export interface FolderView {
  path: string; label: string; files: MapFile[];
  /** The folder's files not drawn, counted on its "+N". */
  more: MapFile[];
  /** 0..1: how warm it glows (its share of the team's busiest folder's activity). */
  heat: number;
}

export interface TeamMapView {
  team: string; folders: FolderView[];
  /** Folders not drawn, and the files in them. */
  moreFolders: number; moreFiles: number;
  total: number;
}

/** How busy a file is: a change counts fully, a read a third. */
const weight = (f: MapFile) => (f.editAt > -Infinity ? 1 : 0.35) * (1 + Math.log2(1 + f.touches));

/** A folder's last part, or its last two when the last is generic ("src", "lib"...), for its label. */
export function folderLabel(path: string): string {
  if (!path) return '/';
  const parts = path.split('/');
  const last = parts.at(-1)!;
  return parts.length > 1 && /^(src|lib|tests?|unit|e2e|docs?|app|components?|core|main)$/i.test(last) ? parts.slice(-2).join('/') : last;
}

export function collapse(team: string, files: readonly MapFile[], limits: ViewLimits): TeamMapView {
  const byFolder = new Map<string, MapFile[]>();
  for (const f of files) {
    let list = byFolder.get(f.folder);
    if (!list) byFolder.set(f.folder, (list = []));
    list.push(f);
  }
  const scored = [...byFolder].map(([path, list]) => {
    let score = 0, last = -Infinity;
    for (const f of list) { score += weight(f); if (f.lastAt > last) last = f.lastAt; }
    return { path, list, score, last };
  });
  // Busiest first; a tie goes to the folder touched last, then by name so the order never flickers.
  scored.sort((a, b) => b.score - a.score || b.last - a.last || (a.path < b.path ? -1 : 1));
  const large = files.length > limits.maxFiles || scored.length > limits.maxFolders;
  const shownFolders = scored.slice(0, limits.maxFolders);
  const top = shownFolders[0]?.score ?? 1;
  let budget = limits.maxFiles;
  const folders: FolderView[] = shownFolders.map((s) => {
    const sorted = [...s.list].sort((a, b) => b.lastAt - a.lastAt || (a.path < b.path ? -1 : 1));
    const room = Math.max(0, Math.min(large ? limits.perFolder : sorted.length, budget));
    budget -= room;
    // A folder with room for all but one shows that one rather than a "+1".
    const take = sorted.length === room + 1 && budget > 0 ? (budget--, room + 1) : room;
    return { path: s.path, label: folderLabel(s.path), files: sorted.slice(0, take), more: sorted.slice(take), heat: Math.min(1, s.score / top) };
  });
  const rest = scored.slice(limits.maxFolders);
  return { team, folders, moreFolders: rest.length, moreFiles: rest.reduce((n, s) => n + s.list.length, 0), total: files.length };
}

// --- the layout -----------------------------------------------------------------------------------------------------

export interface PlacedFolder { id: string; view: FolderView; x: number; y: number; angle: number; moreX: number; moreY: number }
export interface PlacedFile { id: string; file: MapFile; folder: string; x: number; y: number }
export interface TeamMapLayout { team: string; folders: PlacedFolder[]; files: PlacedFile[]; rest: { x: number; y: number } | null }

/** How far past a team's member spokes its folders sit, and how far a folder's files sit from it. */
export const FolderPast = 120;
export const FileReach = 34;
/** How much further than FileReach every other file of a fan sits. */
export const FileStagger = 16;

/**
 * Where a team's map goes: its folders on an arc round the team's hub at `radius`, centred on the angle `out`
 * (pointing away from the other teams), and each folder's files on a small fan beyond it, its "+N" at the fan's end.
 */
export function layoutTeamMap(view: TeamMapView, hub: { x: number; y: number }, out: number, radius: number): TeamMapLayout {
  const n = view.folders.length + (view.moreFolders ? 1 : 0);
  const step = arcStep(n);
  const tk = lower(view.team);
  const folders: PlacedFolder[] = [];
  const files: PlacedFile[] = [];
  view.folders.forEach((fv, i) => {
    const a = out + (i - (n - 1) / 2) * step;
    const x = hub.x + Math.cos(a) * radius, y = hub.y + Math.sin(a) * radius;
    const k = fv.files.length + (fv.more.length ? 1 : 0);
    const fan = Math.min(0.42, 2.6 / Math.max(1, k));
    let mx = x, my = y;
    for (let j = 0; j < k; j++) {
      const fa = a + (j - (k - 1) / 2) * fan;
      const d = FileReach + (j % 2) * FileStagger;
      const px = x + Math.cos(fa) * d, py = y + Math.sin(fa) * d;
      const f = fv.files[j];
      if (f) files.push({ id: `${tk}|${f.path}`, file: f, folder: fv.path, x: px, y: py });
      else { mx = px; my = py; }
    }
    folders.push({ id: `${tk}|${fv.path}/`, view: fv, x, y, angle: a, moreX: mx, moreY: my });
  });
  const rest = view.moreFolders
    ? { x: hub.x + Math.cos(out + (n - 1 - (n - 1) / 2) * step) * radius, y: hub.y + Math.sin(out + (n - 1 - (n - 1) / 2) * step) * radius }
    : null;
  return { team: view.team, folders, files, rest };
}

/** The angle between two folders on a map's arc, for n places on it (the folders and a "+N folders"). */
function arcStep(n: number): number {
  return n <= 1 ? 0 : Math.min(0.62, (Math.PI * 1.7) / n);
}

/** How far past its members a map's folders sit (the scene lays the arc at the members' reach plus this). */
export const MapPastMembers = 70;
/** How far a team's members usually reach from its hub (the scene lays the map's arc at their reach + MapPastMembers). */
export const MapMembersReach = 80;
/** A map node with its label, as half a box round the node (world px): as the scene's clusters() counts it. */
export const MapNodeHalfW = 40;
export const MapNodeHalfH = 10;
/** How far a team's cluster reaches from its hub toward the others before its code map (the scene's ClusterCore). */
export const MapCore = 130;

/**
 * How much room a team's map needs toward the other teams past `core`, in world px (0 for no map, or one that stays
 * within it). The map lies on an arc pointing away from the others, so only the part of it that wraps back toward
 * them counts: the map is laid out as the scene lays it (layoutTeamMap at MapMembersReach + MapPastMembers), and its
 * nodes with their labels are measured along the directions to the neighbours, which lie `spread` radians either side
 * of straight back (0 for two teams; neighbourSpread in the scene). It used to count the arc's sideways spread
 * (depth × sin(half-span)), which lies across the line between the teams, not on it, so the default spacing left the
 * middle of the wall empty.
 */
export function mapRoomOf(view: TeamMapView, core = MapCore, spread = 0): number {
  const n = view.folders.length + (view.moreFolders ? 1 : 0);
  if (n === 0) return 0;
  const laid = layoutTeamMap(view, { x: 0, y: 0 }, 0, MapMembersReach + MapPastMembers);
  const points: { x: number; y: number }[] = [...laid.folders, ...laid.files, ...laid.folders.map((f) => ({ x: f.moreX, y: f.moreY }))];
  if (laid.rest) points.push(laid.rest);
  let reach = 0;
  for (const side of spread ? [-1, 1] : [1]) {
    const ux = Math.cos(Math.PI + side * spread), uy = Math.sin(Math.PI + side * spread);
    const pad = MapNodeHalfW * Math.abs(ux) + MapNodeHalfH * Math.abs(uy);
    for (const p of points) reach = Math.max(reach, p.x * ux + p.y * uy + pad);
  }
  return Math.max(0, reach - core);
}

// --- how the nodes look over time -----------------------------------------------------------------------------------

export const PopMs = 420;
export const LightMs = 2_400;

/** A born file's size over its pop: up past full size and back; full size under reduced motion or when applied quietly. */
export function popScale(bornShown: number, now: number, reduced: boolean): number {
  const age = now - bornShown;
  if (reduced || !(age >= 0) || age >= PopMs) return 1;
  const t = age / PopMs;
  return t < 0.6 ? 0.2 + (t / 0.6) * 1.05 : 1.25 - ((t - 0.6) / 0.4) * 0.25;
}

/** How brightly a file shows: a changed file stays bright, a read one faint; a fresh touch lights it more for a moment. */
export function fileLight(f: Pick<MapFile, 'editAt' | 'editShown' | 'readShown'>, now: number): number {
  const base = f.editAt > -Infinity ? 0.72 : 0.38;
  const edit = Math.max(0, 1 - (now - f.editShown) / LightMs);
  const read = Math.max(0, 1 - (now - f.readShown) / LightMs) * 0.45;
  return Math.min(1, base + Math.max(edit, read) * (1 - base));
}

/** A commit's dot over its life: its files gather to the member, it travels the spoke to the hub, then rests there. */
export const CommitGatherMs = 450;
export const CommitTravelMs = 1_300;
export const CommitLifeMs = 12_000;
export function commitPhase(age: number, reduced: boolean): { gather: number | null; travel: number; resting: boolean } {
  if (reduced) return { gather: null, travel: 1, resting: true };
  if (age < CommitGatherMs) return { gather: age / CommitGatherMs, travel: 0, resting: false };
  const u = (age - CommitGatherMs) / CommitTravelMs;
  return u >= 1 ? { gather: null, travel: 1, resting: true } : { gather: null, travel: 1 - Math.pow(1 - u, 2), resting: false };
}
