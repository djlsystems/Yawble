import { forceCollide, forceLink, forceManyBody, forceSimulation, forceX, forceY, type Force, type Simulation, type SimulationNodeDatum } from 'd3-force';
import { LayoutDefaults, type LayoutSettings } from './layout';
import { avatarSheet, initials, loadAvatarSheet, memberAvatar, tileSource, type MemberState } from '../faces/faces';
import { DoingMs, memberKey, RunEndShowMs, teamKey, spaced, workflowKey, type Effect, type MonitorModel, type TeamSnapshot, type Tone } from './model';
import { workflowTime, type WorkflowSummary } from '../../../shared/api';
import { drawHandbackMark, drawStepIcon, drawWorkflowIcon } from './icons';
import { StepLook } from './steps';
import {
  commitPhase, CommitLifeMs, DefaultLimits, fileLight, idleFade, layoutTeamMap, LightMs, MapPastMembers, mapRoomOf, MaxMapNodes, popScale, shortSha,
  type MapFile, type TeamMapView,
} from './codemap';
import { busyMembers, nodeLabelFont, placeWorkflowLabels, workflowNodeLabel, workflowNodeLook, type LabelAsk, type LabelBox, type LabelObstacle, type PlacedLabel } from './workflows';
import { capEffects, DensityCaps, focusAlpha, isDensity, onYouNode, pulsePeriodMs, runFill, teamOfKey, youNodeAt, type Density, type KeepOut } from './rhythm';

/**
 * THE MONITOR'S SCENE: one canvas on a dark wall. Teams are territories (a glow in the team's hue with its name),
 * members orbit their team on spokes, each a disc with its face and the ring that says its state:
 *   running  - three arcs of 75 degrees turning, a turn in 3.3 s;
 *   waiting  - a dashed ring creeping round;
 *   blocked, needs you, failed - a ring that throbs every 1.4 s, wider and brighter;
 *   idle     - a thin line.
 * The Manager wears an outer ring with three ticks; queued instructions orbit a member as small dots.
 * Effects (comets, bursts, ripples, floating lines) come from the model. So do the members' steps: each step's icon
 * rises off its member and fades (newest lowest, at most the density's stepsEach at once, rhythm.ts), and a line under
 * the member's name says what it is doing now.
 * Each team's code map (codemap.ts) hangs round its hub past its members: the repository's folders on an arc, each
 * with its files fanned beyond it (a "+N" for the rest when the map is large), a folder glowing warmer the busier it
 * is. A new file pops in, an edit lights it bright, a read faint; a line runs from each member to every file it
 * touched, and a file two members touched wears a double ring. A commit's files gather into a dot that travels the
 * member's spoke to the hub and rests there (hover: its message and short sha); a merge into the team branch bursts
 * along the hub's spokes, a landing on main bursts larger, a push pulses out past the wall's edge. Map nodes off the
 * screen are not drawn, and all the maps together show at most MaxMapNodes files (fewer at a lower density).
 * The layout is a d3-force simulation
 * ticked once per frame while it is awake; it cools to rest once nothing changes, and so the camera, which fits
 * everything unless the person has moved it in the last 12 s, settles too (card 385).
 * Under reduced motion nothing turns or throbs.
 *
 * B080, livelier and easier to follow:
 *   workflow nodes - every open workflow is a small node tied by dashed lines to the members working it (to its hub
 *     while nobody is on it); completed, it flashes green and drifts away; closed, it fades with a cross; blocked, it
 *     wears the blocked colour and a "!". Under it, "#id title" (B090; the WORKFLOWS panel is gone); hover gives the
 *     whole title, a click opens the workflow.
 *   work rhythm - a member's disc pulses faster the more steps it takes; a command shows a ring filling round it
 *     while it runs, then a green or red burst by its exit code when that is known; thinking shimmers softly.
 *   the You node - at the right edge: what waits for the person flies to it and it glows with the count.
 *   cards - a Manager's instruction carries its card's number ("#373"); the hand-back comes home along the same line
 *     with a check, or a cross when the run failed or was blocked.
 *   setDensity caps the effects drawn per frame; setFocus zooms to one team and dims the rest; under reduced motion
 *     nothing travels or pulses: a comet is a still line with its label, the states are steady marks.
 */

interface Node extends SimulationNodeDatum {
  key: string; kind: 'team' | 'member' | 'wf'; team: string; name: string; r: number; hue: number;
  role?: 'manager' | 'member'; state?: MemberState; queued?: number; statusLine?: string | null; homeX: number; homeY: number;
  /** A workflow node's workflow. */
  wf?: WorkflowSummary;
}
/** A workflow node that has left (completed or closed): where it was, drawn by its wfdone effect. */
/** A workflow node that left: where it was, its way out from its hub, and (to follow the hub) its hub and offset from it. */
interface WfGhost { x: number; y: number; hue: number; id: number; outX: number; outY: number; gone: number; hub: string | null; dx: number; dy: number }
interface Link { source: Node; target: Node }
/** A world box by its corners. */
interface Box { x0: number; y0: number; x1: number; y1: number }

export interface SceneHover {
  key: string; kind: 'team' | 'member' | 'file' | 'folder' | 'commit' | 'workflow' | 'you'; team: string; name: string; state?: MemberState; statusLine?: string | null; queued?: number; x: number; y: number;
  /**
   * A map node's: a file's path, how often it was touched, whether it is new, changed or only read, whether a commit
   * confirmed it (its short sha) and who touched it; a folder's or a "+N" node's files.
   */
  path?: string; touches?: number; fileKind?: 'edit' | 'create' | 'read'; sha?: string | null; members?: string[]; more?: string[];
  /** A commit dot's message. */
  message?: string;
  /** The hovered workflow node's workflow. */
  workflow?: WorkflowSummary;
}

/** A map node as drawn this frame: a file, a folder, a folder's "+N" or a team's "+N folders". */
interface MapNode {
  id: string; kind: 'file' | 'folder' | 'more' | 'rest'; team: Node; x: number; y: number;
  file?: MapFile; folder?: string; label: string; heat: number; files: MapFile[];
}

const Palette = {
  ground: '#070b14', ink: '#e2e8f0', inkDim: '#94a3b8',
  tell: '#38bdf8', start: '#22d3ee', progress: '#a5b4fc', done: '#34d399', handback: '#fbbf24', fail: '#f43f5e', stopped: '#94a3b8',
  blocked: '#fb923c', decision: '#c4b5fd', person: '#f9a8d4', edit: '#a3e635', commit: '#fde047',
} satisfies Record<Tone | 'ground' | 'ink' | 'inkDim', string>;

const Life: Record<Effect['kind'], number> = {
  comet: 1100, burst: 1400, ripple: 1100, float: 2600, step: 2600, you: 1500, wfdone: 2600, commit: CommitLifeMs, merge: 1800, push: 1600,
};
/** Under reduced motion a comet does not travel: its still line and label stay this long. */
const StillCometMs = 2200;
/** A workflow node's radius, and how far a member's workflow line reaches. */
const WfR = 10;
const WfMemberDist = 58;
const StepIconGap = 20;
/** The wall's oval: its y radius is this much of its x radius. */
const OvalY = 0.62;
/**
 * How far a team's cluster reaches from its hub toward the other teams before its code map: the members, their names
 * and the workflow nodes with their labels; and the room left between two clusters at Team spacing 1. Card 409: 130
 * (was 100): with the map's room counted only toward the neighbours, two teams at 100 apart settled with their workflow
 * labels and members touching in 1 run of 8.
 */
export const ClusterCore = 130;
export const ClusterGap = 40;

/** The distance between the two nearest of count places on an oval of x radius 1 (teamAngle; the y radius OvalY). */
export function nearestPair(count: number): number {
  let d = Infinity;
  for (let a = 0; a < count; a++) for (let b = a + 1; b < count; b++) {
    const p = teamAngle(a, count), q = teamAngle(b, count);
    d = Math.min(d, Math.hypot(Math.cos(p) - Math.cos(q), OvalY * (Math.sin(p) - Math.sin(q))));
  }
  return d;
}

/**
 * Where team i of count sits on the wall's oval (its "home", in world px). The oval is as large as puts the two nearest
 * teams two clusters (ClusterCore and the room their code maps need, mapRoom, from the maps drawn) and a gap apart, times
 * the person's Team spacing; its y radius is OvalY of its x radius. A team alone sits in the middle.
 */
export function teamHome(i: number, count: number, mapRoom: number, spacing: number, measuredRx: number | null = null): { x: number; y: number } {
  if (count <= 1) return { x: 0, y: 0 };
  const rx = (measuredRx ?? estimatedRx(count, mapRoom)) * spacing, ry = rx * OvalY;
  const angle = teamAngle(i, count);
  return { x: Math.cos(angle) * rx, y: Math.sin(angle) * ry };
}

/** The oval's x radius at Team spacing 1 before the clusters are measured: from ClusterCore and the maps' room (the first guess). */
export function estimatedRx(count: number, mapRoom: number): number {
  return Math.max(MinOvalRx, (2 * (ClusterCore + mapRoom) + ClusterGap) / nearestPair(count));
}
const MinOvalRx = 140;
/** The least the measured radius may be: the clusters' own size keeps them apart; this only keeps the hubs off each other. */
const MinMeasuredRx = 60;
/**
 * settleSpacing: the nearest gap is widened when it is SpacingSlack (world px) under ClusterGap, and narrowed only when it
 * is SpacingShrink over; at most SpacingMoves moves per set of things.
 */
const SpacingSlack = 1;
const SpacingShrink = 16;
const SpacingMoves = 12;

/**
 * Card 411 (F6): the oval's x radius at Team spacing 1 that puts the nearest two clusters ClusterGap apart, from the
 * clusters as drawn now. For each pair of teams: their hubs' distance d, the least distance g between their drawn things
 * (negative when they overlap), so they would touch at d - g; the radius scales with the hubs' distance, so the pair
 * wants rx * (d - g + ClusterGap) / d. The widest want wins, never under MinMeasuredRx.
 */
export function measuredOvalRx(pairs: readonly { d: number; g: number }[], rxNow: number): number {
  let want = MinMeasuredRx;
  for (const { d, g } of pairs) if (d > 1) want = Math.max(want, rxNow * (d - g + ClusterGap) / d);
  return want;
}

/** The least distance between two sets of world boxes: negative (how deep) when two of them overlap. */
export function boxesGap(a: readonly Box[], b: readonly Box[]): number {
  let best = Infinity;
  for (const p of a) for (const q of b) {
    const dx = Math.max(q.x0 - p.x1, p.x0 - q.x1), dy = Math.max(q.y0 - p.y1, p.y0 - q.y1);
    best = Math.min(best, dx > 0 || dy > 0 ? Math.hypot(Math.max(0, dx), Math.max(0, dy)) : Math.max(dx, dy));
  }
  return best;
}

/**
 * A force that acts only between nodes of the same team (card 411, F6): one `make()` per team, each given only its
 * team's nodes. The teams' repulsion used to push whole clusters apart as well, so where they settled depended on the
 * start (the random birth spots and the order the teams, workflows and maps arrived in): 3 teams' nearest gap ran 4-248.
 * Now a cluster's own forces only shape it, and its place comes from its home alone (measuredOvalRx).
 */
function perTeam(make: () => Force<Node, undefined>): Force<Node, undefined> {
  let subs: Force<Node, undefined>[] = [];
  const force = (alpha: number) => { for (const s of subs) s(alpha); };
  force.initialize = (nodes: Node[], random: () => number) => {
    const by = new Map<string, Node[]>();
    for (const n of nodes) {
      const k = n.team.toLowerCase();
      const list = by.get(k);
      if (list) list.push(n); else by.set(k, [n]);
    }
    subs = [...by.values()].map((list) => { const f = make(); f.initialize?.(list, random); return f; });
  };
  return force;
}

/** The room the widest map drawn needs past its members (codemap mapRoomOf), in steps of this much, so it moves the teams seldom. */
const MapRoomStep = 10;
export function mapRoomFor(views: Iterable<TeamMapView>, count = 2): number {
  const spread = neighbourSpread(count);
  let room = 0;
  for (const v of views) room = Math.max(room, mapRoomOf(v, ClusterCore, spread));
  return Math.ceil(room / MapRoomStep) * MapRoomStep;
}

/**
 * How far (radians) the direction from a team to its nearest neighbour turns off straight back from the team's way out
 * (from the middle), at most, for count teams on the oval: 0 for two, which face each other; more for three or more.
 */
export function neighbourSpread(count: number): number {
  if (count <= 2) return 0;
  const near = nearestPair(count);
  const place = (i: number) => { const a = teamAngle(i, count); return { x: Math.cos(a), y: OvalY * Math.sin(a) }; };
  let worst = 0;
  for (let a = 0; a < count; a++) for (let b = 0; b < count; b++) {
    if (a === b) continue;
    const p = place(a), q = place(b);
    if (Math.hypot(p.x - q.x, p.y - q.y) > near + 1e-9) continue;
    const back = Math.atan2(p.y, p.x) + Math.PI;
    let dev = Math.atan2(q.y - p.y, q.x - p.x) - back;
    dev = Math.abs(Math.atan2(Math.sin(dev), Math.cos(dev)));
    worst = Math.max(worst, dev);
  }
  return worst;
}
/** How often a team's map view is worked out again when nothing came or went (its order follows the activity). */
const MapViewMs = 400;
const RefitAfterMs = 12_000;
/** How long a one-off Fit (pinned) keeps aiming at everything while the camera eases there. */
export const FitOnceMs = 1_500;

/**
 * Whether the camera aims at everything at `now`. Auto fit: unless the person moved the wall in the last 12 s. Pinned:
 * only during a one-off Fit (until `fitUntil`).
 */
/** The zoom limits, for the wheel and a pinch. */
export const MinZoom = 0.15;
export const MaxZoom = 4;

/**
 * The camera during a two-finger pinch: the zoom scales with the fingers' spread from where it started (within the limits),
 * and the world point that was between the fingers at the start stays between them, so moving both fingers pans.
 * The camera's x/y is the world point at the middle of the wall (w × h).
 */
export function pinchCamera(start: { dist: number; k: number; wx: number; wy: number }, dist: number, mid: readonly [number, number], w: number, h: number) {
  const k = Math.min(MaxZoom, Math.max(MinZoom, start.k * (Math.max(1, dist) / start.dist)));
  return { k, x: start.wx - (mid[0] - w / 2) / k, y: start.wy - (mid[1] - h / 2) / k };
}

/** The least reach a map node has for a tap or a click, in screen px, at any zoom (a fingertip at 375 px, card 398). */
export const MinHitPx = 22;

/**
 * The map node a world point (wx, wy) is on at zoom k, or null: each node reaches its own world reach (a team hub 14, others
 * their radius + 8) or MinHitPx on screen, whichever is more, and of the nodes in reach the nearest wins, so close nodes do
 * not steal each other's taps. Workflow nodes (B090) are hit like the rest; a 'workflows' panel (gone) is never picked.
 */
export function nearestNodeAt<N extends { kind: string; r: number; x?: number; y?: number }>(
  nodes: Iterable<N>, wx: number, wy: number, k: number,
): N | null {
  const floor = MinHitPx / Math.max(k, 1e-6);
  let best: N | null = null, bestD = Infinity;
  for (const n of nodes) {
    if (n.kind === 'workflows') continue;
    const d = Math.hypot(n.x! - wx, n.y! - wy);
    const reach = Math.max(n.kind === 'team' ? 14 : n.r + 8, floor);
    if (d < reach && d < bestD) { best = n; bestD = d; }
  }
  return best;
}

/**
 * Whether a map node mapD (world px) from the pointer at (wx, wy) takes the hover and the click over the workflow node
 * in reach there (nearestNodeAt over the workflow nodes, or null): only when the point is off the workflow's square and
 * the map node is the nearer (card 411, F4: a square under its map's folder could not be hovered or clicked).
 */
export function mapBeatsWorkflow(mapD: number, wf: { r: number; x?: number; y?: number } | null, wx: number, wy: number): boolean {
  if (!wf) return true;
  const d = Math.hypot(wf.x! - wx, wf.y! - wy);
  return d > wf.r && mapD < d;
}

/**
 * Where team i of count sits on the wall's oval (radians, 0 = right, clockwise on screen): the first on the left, the
 * rest evenly round from there, so two teams sit side by side across the wide axis rather than one above the other.
 */
/** What the forces depend on: each node's key, radius and home, in order. Equal shapes need no new forces and no wake. */
export function graphShape(nodes: Iterable<{ key: string; r: number; homeX: number; homeY: number }>): string {
  let s = '';
  for (const n of nodes) s += `${n.key}|${n.r}|${n.homeX.toFixed(1)}|${n.homeY.toFixed(1)};`;
  return s;
}

export function teamAngle(i: number, count: number): number {
  return count <= 1 ? 0 : Math.PI + (i / count) * Math.PI * 2;
}

export function aimsAtEverything(pinned: boolean, now: number, movedAt: number, fitUntil: number): boolean {
  return pinned ? now < fitUntil : now - movedAt > RefitAfterMs;
}

/** The wall's state colours: the app's --state-* lifted toward white, read once. */
function stateColors(): Record<MemberState, string> {
  const css = getComputedStyle(document.documentElement);
  const pick = (s: MemberState, fallback: string) => lift(css.getPropertyValue(`--state-${s}`).trim() || fallback);
  return {
    running: pick('running', '#2e7d4f'), waiting: pick('waiting', '#5f7385'), decision: pick('decision', '#7a4fc4'),
    blocked: pick('blocked', '#b26a00'), failed: pick('failed', '#c62828'), stopped: pick('stopped', '#6b7280'), idle: pick('idle', '#8a847c'),
  };
}

/** A colour 60% of the way from itself... toward white at 40% (color-mix(in srgb, c 60%, white)). */
function lift(hex: string): string {
  const m = /^#?([0-9a-f]{6})$/i.exec(hex);
  if (!m) return hex;
  const n = parseInt(m[1]!, 16);
  const ch = (shift: number) => Math.round(((n >> shift) & 255) * 0.6 + 255 * 0.4);
  return `rgb(${ch(16)}, ${ch(8)}, ${ch(0)})`;
}

export class MonitorScene {
  private ctx: CanvasRenderingContext2D;
  private nodes = new Map<string, Node>();
  private links: Link[] = [];
  private teamList: TeamSnapshot[] = [];
  /** Which model's workflows, at which rev, the nodes were last built from. */
  private wfFrom: { model: MonitorModel | null; rev: number } = { model: null, rev: -1 };
  private sim: Simulation<Node, undefined>;
  private cam = { x: 0, y: 0, k: 1 };
  private target = { x: 0, y: 0, k: 1 };
  private movedAt = -Infinity;
  /** Pinned: the camera never fits on its own; Fit (or a double-click) fits once. */
  private pinned = false;
  private fitUntil = -Infinity;
  /** The person's force multipliers (monitor/layout.ts); the scene's own values until setLayout. */
  private layout: LayoutSettings = { ...LayoutDefaults };
  private raf = 0;
  private last = 0;
  private dpr = 1;
  private w = 0;
  private h = 0;
  private hover: Node | null = null;
  private picked: string | null = null;
  private drag: { node: Node | null; x: number; y: number; cx: number; cy: number } | null = null;
  private colors = stateColors();
  private dt = 16;
  /** Where each map node is drawn (eased toward its place), and the ones drawn this frame, for the hover. */
  private mapPos = new Map<string, { x: number; y: number }>();
  private mapNodes: MapNode[] = [];
  private mapById = new Map<string, MapNode>();
  private hoverMap: MapNode | null = null;
  /** Each team's map layout as last worked out, with the map's rev and when (MapViewMs). */
  private mapLayouts: { rev: number; at: number; model: MonitorModel | null; list: { team: Node; view: TeamMapView }[] } = { rev: -1, at: -Infinity, model: null, list: [] };
  /** How much further apart the teams sit for their maps, as the homes were last placed (teamHome), and as the maps drawn now want it. */
  private mapRoom = 0;
  private wantedRoom = 0;
  /** The commit dot under the pointer. */
  private hoverCommit: Extract<Effect, { kind: 'commit' }> | null = null;
  /** Each step icon's place in its member's stack, eased so the stack slides up rather than jumps. */
  private stepSlots = new WeakMap<Effect, number>();
  /** The workflow nodes' lines to the members working them (or to their hub). */
  private wfmLinks: Link[] = [];
  /** Workflow nodes that have left, by key, so their wfdone effect knows where to flash and drift from. */
  private wfGhosts = new Map<string, WfGhost>();
  private reducedMotion: boolean;
  private density: Density = 'lively';
  /** The focused team's id (TeamSnapshot.id), or null. */
  private focusId: number | null = null;
  /** The effects drawn in the last frame (capped by density and focus), for the tests and the hover. */
  drawn: Effect[] = [];
  effects: Effect[] = [];
  /** The model whose files and "now" lines are drawn (the live one, or a replay's). */
  model: MonitorModel | null = null;
  /** In a replay, the members' states as they were (the snapshots say how they are now); null live. */
  stateOf: ((key: string) => MemberState) | null = null;
  onHover: (h: SceneHover | null) => void = () => {};
  onPick: (key: string | null) => void = () => {};
  /** A workflow node was clicked (not dragged): its team and its workflow's id. */
  onOpenWorkflow: (team: string, id?: number) => void = () => {};

  constructor(private canvas: HTMLCanvasElement) {
    this.ctx = canvas.getContext('2d')!;
    this.reducedMotion = typeof matchMedia === 'function' && matchMedia('(prefers-reduced-motion: reduce)').matches;
    this.sim = forceSimulation<Node>([])
      // The forces cool to rest (alpha target 0) once nothing changes: kept warm, two teams' hubs kept circling one
      // another and the camera's fit chased them (card 385).
      .alphaDecay(0.02).alphaTarget(0)
      .force('link', forceLink<Node, Link>([]))
      .force('wfmlink', forceLink<Node, Link>([]))
      .force('collide', forceCollide<Node>().radius((n) => n.r + (n.kind === 'team' ? 30 : n.kind === 'wf' ? 10 : 24)))
      .stop();
    this.applyLayout();
    void loadAvatarSheet();
    this.listen();
    this.raf = requestAnimationFrame(this.frame);  }

  destroy(): void {
    this.destroyed = true;
    cancelAnimationFrame(this.raf);
    this.sim.stop();
    this.unlisten?.();
  }

  /** Reduced motion (prefers-reduced-motion at the start): nothing travels, turns or pulses; states are steady marks. */
  get reduced(): boolean { return this.reducedMotion; }
  setReducedMotion(on: boolean): void {
    this.reducedMotion = on;
  }

  /** How many effects the wall draws per frame, and how many code-map files and commit dots: calm, normal or lively (the default). */
  setDensity(level: Density): void {
    if (!isDensity(level) || level === this.density) return;
    this.density = level;
    this.mapLayouts = { ...this.mapLayouts, at: -Infinity }; // the maps' views are worked out again with the new cap
  }
  get densityLevel(): Density { return this.density; }

  /**
   * Zooms to one team (its TeamSnapshot.id) and dims the rest; null leaves focus. Entering or leaving fits once, even
   * pinned; while focused, the camera's fit aims at that team only.
   */
  setFocus(teamId: number | null): void {
    const id = typeof teamId === 'number' && Number.isFinite(teamId) ? teamId : null;
    if (id === this.focusId) return;
    this.focusId = id;
    this.fit();
  }
  get focus(): number | null { return this.focusId; }

  /** The focused team's name in lower case (as in the keys), or null; a focus on a team that is gone is none. */
  focusTeam(): string | null {
    if (this.focusId === null) return null;
    const t = this.teamList.find((x) => x.id === this.focusId);
    return t ? t.name.toLowerCase() : null;
  }

  /** Whether a point in canvas CSS px (clientX - rect.left, clientY - rect.top) is on the You node. */
  hitYouNode(x: number, y: number): boolean {
    const w = this.w || this.canvas.clientWidth, h = this.h || this.canvas.clientHeight;
    return w > 0 && h > 0 && onYouNode(x, y, w, h, this.youKeepOut);
  }

  /** What the You node keeps clear of (canvas CSS px; the view measures its Legend button and controls, card 416). */
  private youKeepOut: KeepOut[] = [];
  setYouKeepOut(rects: readonly KeepOut[]): void { this.youKeepOut = rects.map((k) => ({ ...k })); }
  /** The You node's place now: inside the wall, clear of the view's controls. */
  private youNode(): { x: number; y: number; r: number } { return youNodeAt(this.w, this.h, this.youKeepOut); }

  /** The person's layout multipliers (the cog beside Pinned): from now on, and the graph is woken so it moves to them. */
  setLayout(layout: LayoutSettings): void {
    const moved = layout.spacing !== this.layout.spacing;
    this.layout = { ...layout };
    this.applyLayout();
    if (moved) this.rebuild(); // new homes: the teams move there, and a fit follows them
    this.sim.alpha(Math.max(this.sim.alpha(), 0.5));
  }

  /** The forces, at the scene's own values times the person's multipliers. Setting an accessor again makes d3 read it again. */
  private applyLayout(): void {
    const { distance, strength, repulsion, gravity, friction } = this.layout;
    const link = this.sim.force('link') as ReturnType<typeof forceLink<Node, Link>>;
    this.sim
      .velocityDecay(Math.min(0.9, Math.max(0.05, 0.32 * friction)))
      .force('charge', perTeam(() => forceManyBody<Node>().strength((n) => (n.kind === 'team' ? -600 : n.kind === 'wf' ? -90 : -280) * repulsion)))
      .force('x', forceX<Node>((n) => n.homeX).strength((n) => (n.kind === 'team' ? 0.12 : 0.02) * gravity))
      .force('y', forceY<Node>((n) => n.homeY).strength((n) => (n.kind === 'team' ? 0.12 : 0.02) * gravity));
    link.distance((l) => this.spoke(l.source.team) * distance).strength(Math.min(1, 0.55 * strength));
    const wfmlink = this.sim.force('wfmlink') as ReturnType<typeof forceLink<Node, Link>>;
    wfmlink.distance((l) => (l.target.kind === 'team' ? this.spoke(l.source.team) * 0.55 : WfMemberDist) * distance).strength(Math.min(1, 0.25 * strength));
  }

  /** How long a team's member spokes are. */
  private spoke(team: string): number { return 100 + Math.min(90, this.membersOf(team) * 9); }

  private membersOf(team: string): number {
    let n = 0;
    for (const node of this.nodes.values()) if (node.kind === 'member' && node.team === team) n++;
    return n;
  }

  /** Takes the teams as they are: new nodes are born near their team, gone ones are dropped. */
  setTeams(teams: TeamSnapshot[]): void {
    this.teamList = teams;
    this.rebuild();
  }

  /** The nodes from the teams and the model's workflows (none in a replay): new ones born near their hub. */
  private rebuild(wake = 0.3): void {
    const teams = this.teamList;
    this.wfFrom = { model: this.model, rev: this.model?.workflowsRev ?? -1 };
    const keep = new Set<string>();
    const count = teams.length;
    this.mapRoom = this.wantedRoom;
    if (this.measured && this.measured.count !== count) this.measured = null; // another oval: measure it again
    teams.forEach((t, i) => {
      const { x: homeX, y: homeY } = teamHome(i, count, this.mapRoom, this.layout.spacing, this.measured?.rx ?? null);
      const hue = (i * 137.508 + 190) % 360;
      const tk = teamKey(t.name);
      keep.add(tk);
      const team = this.upsert(tk, { kind: 'team', team: t.name, name: t.name, r: 22, hue, homeX, homeY });
      t.members.forEach((m, j) => {
        const mk = memberKey(t.name, m.name);
        keep.add(mk);
        const a = (j / Math.max(1, t.members.length)) * Math.PI * 2;
        this.upsert(mk, {
          kind: 'member', team: t.name, name: m.name, r: m.role === 'manager' ? 17 : 14, hue, homeX, homeY,
          role: m.role, state: m.state, queued: m.queued, statusLine: m.statusLine,
        }, (team.x ?? homeX) + Math.cos(a) * 60, (team.y ?? homeY) + Math.sin(a) * 60);
      });
      // The workflow nodes: each open workflow, born beside the first member working it (or its hub).
      for (const w of this.model?.openWfs.get(tk) ?? []) {
        const wk = workflowKey(t.name, w.id);
        keep.add(wk);
        const first = busyMembers(w).map((b) => this.nodes.get(memberKey(t.name, b))).find((n) => n);
        const near = first ?? team;
        this.upsert(wk, { kind: 'wf', team: t.name, name: `#${w.id}`, r: WfR, hue, homeX, homeY, wf: w },
          (near.x ?? homeX) + (Math.random() - 0.5) * 50, (near.y ?? homeY) + (Math.random() - 0.5) * 50);
      }
    });
    const now = typeof performance === 'object' ? performance.now() : 0;
    for (const [k, n] of [...this.nodes]) {
      if (keep.has(k)) continue;
      // A workflow node that leaves keeps where it was beside its hub a while, for its flash or fade (its wfdone
      // effect): it goes with the hub while the graph settles without it, so it drifts away from its own team.
      if (n.kind === 'wf') {
        const hub = this.nodes.get(teamKey(n.team));
        const dx = hub ? n.x! - hub.x! : 1, dy = hub ? n.y! - hub.y! : 0, d = Math.hypot(dx, dy) || 1;
        this.wfGhosts.set(k, { x: n.x!, y: n.y!, hue: n.hue, id: n.wf?.id ?? 0, outX: dx / d, outY: dy / d, gone: now, hub: hub ? hub.key : null, dx, dy });
      }
      this.nodes.delete(k);
    }
    for (const [k, g] of this.wfGhosts) if (now - g.gone > 10_000) this.wfGhosts.delete(k);
    // Which member works which workflow is part of the shape: a new line wakes the forces.
    const wfm: Link[] = [];
    for (const n of this.nodes.values()) {
      if (n.kind !== 'wf' || !n.wf) continue;
      const on = busyMembers(n.wf).map((b) => this.nodes.get(memberKey(n.team, b))).filter((m): m is Node => !!m);
      const hub = this.nodes.get(teamKey(n.team));
      if (on.length) for (const m of on) wfm.push({ source: n, target: m });
      else if (hub) wfm.push({ source: n, target: hub });
    }
    // A poll that changes no node, size or home (the usual case, every 1.2 s) only refreshes what the nodes say: waking
    // the forces each time made the graph jerk once a second.
    const shape = graphShape(this.nodes.values()) + wfm.map((l) => `${l.source.key}>${l.target.key}`).join(',');
    if (shape === this.shape) return;
    this.shape = shape;
    this.links = [...this.nodes.values()].filter((n) => n.kind === 'member').map((n) => ({ source: this.nodes.get(teamKey(n.team))!, target: n }));
    this.wfmLinks = wfm;
    this.sim.nodes([...this.nodes.values()]);
    (this.sim.force('link') as ReturnType<typeof forceLink<Node, Link>>).links(this.links);
    (this.sim.force('wfmlink') as ReturnType<typeof forceLink<Node, Link>>).links(this.wfmLinks);
    this.sim.alpha(Math.max(this.sim.alpha(), wake));
  }

  /** The oval's x radius at Team spacing 1 as measured from the clusters drawn (settleSpacing), for count teams; null: the estimate. */
  private measured: { rx: number; count: number } | null = null;
  /** What the clusters held when settleSpacing last looked, how many moves it made since that changed, and when it looked. */
  private spacingFor = '';
  private spacingMoves = 0;
  private spacingAt = -Infinity;

  /**
   * Card 411 (F6): once the forces have nearly cooled, the clusters as drawn are measured and the oval is resized so the
   * nearest two sit ClusterGap apart (measuredOvalRx), the same every run whatever the start. A small move is not made
   * (SpacingSlack, SpacingShrink), and at most SpacingMoves are made while the clusters hold the same things (twice as
   * many while two overlap), so it comes to rest. It measures only at Team spacing 1, under Auto fit; other spacings
   * scale the radius it found.
   */
  private settleSpacing(now: number): void {
    const count = this.teamList.length;
    if (count < 2 || this.sim.alpha() > 0.04 || this.drag?.node || this.shifts.size || now - this.spacingAt < 150) return;
    // Only under Auto fit while it aims at everything: the labels' size follows the zoom, so a wall the person zoomed or
    // pinned would move under their hand (and a pinned camera would not follow the teams).
    if (this.pinned || !this.fitting(now) || this.focusId !== null) return;
    // And only at Team spacing 1: wider, the camera zooms out, the labels grow, and the radius measured grew with them
    // (2x came out 4.7x). Other spacings scale the radius last measured at 1 (or the estimate).
    if (this.layout.spacing !== LayoutDefaults.spacing) return;
    this.spacingAt = now;
    const what = `${count}|${this.nodes.size}|${this.mapNodes.length}`;
    if (what !== this.spacingFor) { this.spacingFor = what; this.spacingMoves = 0; }
    for (const n of this.nodes.values()) if (n.kind === 'team' && Math.hypot(n.homeX - n.x!, n.homeY - n.y!) > 1) return; // still on its way (holdHubs)
    const parts = this.drawnParts();
    const teams = [...parts.values()];
    const pairs: { d: number; g: number }[] = [];
    for (let a = 0; a < teams.length; a++) for (let b = a + 1; b < teams.length; b++) {
      const p = teams[a]!, q = teams[b]!;
      pairs.push({ d: Math.hypot(p.hub.x - q.hub.x, p.hub.y - q.hub.y), g: boxesGap(p.boxes, q.boxes) });
    }
    if (pairs.length !== (count * (count - 1)) / 2) return; // a hub not placed yet
    const overlap = pairs.some((p) => p.g < 0);
    const rx1 = this.measured?.rx ?? estimatedRx(count, this.mapRoom);
    const want = measuredOvalRx(pairs, rx1);
    // The gap's error, about: grown when the nearest two are closer than ClusterGap - SpacingSlack, shrunk only when they are
    // over SpacingShrink too far apart. The labels grow as the camera zooms out to the wider oval, and jump from spot to
    // spot as things move, so a narrow band each way kept it moving.
    const err = (want - rx1) * nearestPair(count);
    if (err < SpacingSlack && err > -SpacingShrink) return;
    if (this.spacingMoves >= (overlap ? 2 * SpacingMoves : SpacingMoves)) return;
    this.spacingMoves++;
    this.measured = { rx: want, count };
    const homes = new Map<string, { x: number; y: number }>();
    for (const n of this.nodes.values()) if (n.kind === 'team') homes.set(n.team.toLowerCase(), { x: n.homeX, y: n.homeY });
    this.rebuild(0.1);
    // Each cluster slides whole to its new home (applyShifts), so its shape and so the gap measured hold: left to the forces,
    // the hubs lagged behind their homes as the forces cooled, and the next measure was off by that lag.
    for (const n of this.nodes.values()) {
      const was = n.kind === 'team' ? homes.get(n.team.toLowerCase()) : undefined;
      if (!was) continue;
      const s = this.shifts.get(n.team.toLowerCase()) ?? { dx: 0, dy: 0 };
      this.shifts.set(n.team.toLowerCase(), { dx: s.dx + n.homeX - was.x, dy: s.dy + n.homeY - was.y });
    }
  }

  /** How far each team's cluster (by team in lower case) has still to slide to its new home (settleSpacing). */
  private shifts = new Map<string, { dx: number; dy: number }>();

  /**
   * Card 411 (F6): each hub is held (fx, fy) and eases to its home; the person's drag moves it, and it eases back once let
   * go. Left to the forces, a hub stopped wherever it was when they cooled to rest, up to 90 px short of its home and
   * differently each run, so the teams' places and gaps changed from run to run.
   */
  private holdHubs(f: number): void {
    for (const n of this.nodes.values()) {
      if (n.kind !== 'team' || this.drag?.node === n) continue;
      const x = n.fx ?? n.x!, y = n.fy ?? n.y!;
      const dx = n.homeX - x, dy = n.homeY - y;
      const done = Math.hypot(dx, dy) * (1 - f) < 0.3;
      n.x = n.fx = done ? n.homeX : x + dx * f;
      n.y = n.fy = done ? n.homeY : y + dy * f;
    }
  }

  /** Slides each cluster part of its way to its new home this frame (all of it under reduced motion), and the hubs to theirs. */
  private applyShifts(dt: number): void {
    const f = this.reduced ? 1 : 1 - Math.pow(0.003, dt / 1000);
    this.holdHubs(f);
    if (!this.shifts.size) return;
    const step = new Map<string, { dx: number; dy: number }>();
    for (const [t, s] of this.shifts) {
      const done = Math.hypot(s.dx, s.dy) * (1 - f) < 0.3;
      const m = done ? s : { dx: s.dx * f, dy: s.dy * f };
      step.set(t, m);
      if (done) this.shifts.delete(t); else this.shifts.set(t, { dx: s.dx - m.dx, dy: s.dy - m.dy });
    }
    for (const n of this.nodes.values()) {
      const m = step.get(n.team.toLowerCase());
      if (!m || n.kind === 'team' || this.drag?.node === n) continue; // the hub eases itself; a dragged node stays under the pointer
      n.x! += m.dx; n.y! += m.dy;
    }
  }

  /**
   * Each team's things as drawn now, as world boxes (card 411): its hub and name pill, its members' discs and names, its
   * workflow squares (not their labels) and its code map's folders, files and "+N" nodes. With its hub, by team in lower case.
   */
  private drawnParts(): Map<string, { hub: { x: number; y: number }; boxes: Box[] }> {
    const out = new Map<string, { hub: { x: number; y: number }; boxes: Box[] }>();
    for (const n of this.nodes.values()) if (n.kind === 'team') out.set(n.team.toLowerCase(), { hub: { x: n.x!, y: n.y! }, boxes: [] });
    const add = (team: string, b: LabelBox) => out.get(team.toLowerCase())?.boxes.push({ x0: b.x, y0: b.y, x1: b.x + b.w, y1: b.y + b.h });
    const disc = (x: number, y: number, r: number): LabelBox => ({ x: x - r, y: y - r, w: r * 2, h: r * 2 });
    const { labels } = this.wfLabelPlan();
    for (const n of this.nodes.values()) {
      if (n.kind === 'team') { add(n.team, disc(n.x!, n.y!, n.r)); add(n.team, this.pillBox(n)); }
      else if (n.kind === 'member') {
        // A name counts as round, as wide as it is (as card 403's spec measures it), so names never meet across the gap.
        const b = this.memberNameBox(n);
        add(n.team, disc(n.x!, n.y!, n.r)); add(n.team, disc(b.x + b.w / 2, b.y + b.h / 2, Math.max(b.h / 2, b.w / 2)));
      }
      else {
        add(n.team, disc(n.x!, n.y!, n.r));
        const l = labels.get(n.key);
        if (l) add(n.team, { x: l.x - l.h / 2, y: l.y, w: l.w + l.h, h: l.h }); // its ends rounded, half its height past the text
      }
    }
    for (const m of this.mapNodes) add(m.team.team, disc(m.x, m.y, 6)); // a folder's box, a file's dot and its rings
    return out;
  }

  /** The least distance between each two teams' drawn things (drawnParts), as settleSpacing measures it, for the tests. */
  drawnGaps(): number[] {
    const t = [...this.drawnParts().values()];
    return t.flatMap((a, i) => t.slice(i + 1).map((b) => boxesGap(a.boxes, b.boxes)));
  }

  /** A team hub's name pill (drawTeamPill): 34 px above the hub, its name's width + 18 wide, 22 high. */
  private pillBox(n: Node): LabelBox {
    this.ctx.font = `700 13px ${this.family()}`;
    const w = this.ctx.measureText(n.name).width + 18;
    return { x: n.x! - w / 2, y: n.y! - 34, w, h: 22 };
  }

  /** A member's name under its disc (as drawMember writes it, at most 22 characters). */
  private memberNameBox(n: Node): LabelBox {
    this.ctx.font = `500 11px ${this.family()}`;
    const ring = n.r + 5;
    const name = spaced(n.name);
    const nw = this.ctx.measureText(name.length > 22 ? name.slice(0, 21) + '…' : name).width;
    return { x: n.x! - nw / 2, y: n.y! + ring + 6, w: nw, h: 13 };
  }

  /** The workflow nodes on the wall now (for the tests): key, its workflow's id, the members it is tied to, and its label as drawn now. */
  workflowNodes(): { key: string; id: number; to: string[]; status: string; label: string; box: LabelBox | null; x: number; y: number }[] {
    const { labels } = this.wfLabelPlan();
    return [...this.nodes.values()].filter((n) => n.kind === 'wf').map((n) => {
      const l = labels.get(n.key) ?? null;
      return {
        key: n.key, id: n.wf!.id, to: this.wfmLinks.filter((k) => k.source === n).map((k) => k.target.key),
        status: workflowNodeLook(n.wf!, !!this.model?.blockedWfs.has(n.key)).status, label: l?.text ?? '',
        box: l && { x: l.x, y: l.y, w: l.w, h: l.h }, x: n.x!, y: n.y!,
      };
    });
  }

  /**
   * Each team's cluster as drawn now (for the tests and Team spacing): its hub and home, and the world box round its hub,
   * members (with their name labels), workflow nodes (with their labels) and code map (with its labels), by team in lower case.
   */
  clusters(): { team: string; hub: { x: number; y: number }; home: { x: number; y: number }; box: Box; parts: Box[] }[] {
    const out = new Map<string, { team: string; hub: { x: number; y: number }; home: { x: number; y: number }; box: Box }>();
    const parts = new Map<string, Box[]>();
    for (const n of this.nodes.values()) {
      if (n.kind === 'team') out.set(n.team.toLowerCase(), { team: n.team.toLowerCase(), hub: { x: n.x!, y: n.y! }, home: { x: n.homeX, y: n.homeY }, box: { x0: Infinity, y0: Infinity, x1: -Infinity, y1: -Infinity } });
    }
    // Each thing drawn is a part (its own box); the cluster's box holds them all.
    const add = (team: string, x: number, y: number, hw: number, hh: number) => {
      const key = team.toLowerCase();
      const b = out.get(key)?.box;
      if (!b) return;
      b.x0 = Math.min(b.x0, x - hw); b.x1 = Math.max(b.x1, x + hw); b.y0 = Math.min(b.y0, y - hh); b.y1 = Math.max(b.y1, y + hh);
      const list = parts.get(key) ?? [];
      list.push({ x0: x - hw, y0: y - hh, x1: x + hw, y1: y + hh });
      parts.set(key, list);
    };
    const { labels } = this.wfLabelPlan();
    for (const n of this.nodes.values()) {
      if (n.kind === 'team') add(n.team, n.x!, n.y!, n.r + 30, n.r + 30); // its pill and ring
      else if (n.kind === 'member') add(n.team, n.x!, n.y!, n.r + 30, n.r + 18); // its name below it
      else {
        add(n.team, n.x!, n.y!, n.r + 4, n.r + 4);
        const l = labels.get(n.key);
        if (l) add(n.team, l.x + l.w / 2, l.y + l.h / 2, l.w / 2, l.h / 2);
      }
    }
    for (const m of this.mapNodes) add(m.team.team, m.x, m.y, 40, 10); // a node and its label
    return [...out.values()].map((c) => ({ ...c, parts: parts.get(c.team) ?? [] }));
  }

  /** The graph's shape at the last rebuild that woke the forces (graphShape). */
  private shape = '';

  private upsert(key: string, data: Omit<Node, 'key'>, x?: number, y?: number): Node {
    const node = this.nodes.get(key);
    if (node) { Object.assign(node, data); return node; }
    const fresh: Node = { key, ...data, x: x ?? data.homeX + (Math.random() - 0.5) * 40, y: y ?? data.homeY + (Math.random() - 0.5) * 40 };
    this.nodes.set(key, fresh);
    return fresh;
  }

  setPicked(key: string | null): void { this.picked = key; }

  /** Fits everything on the next frames (double-click, the Fit button); pinned, only this once. */
  fit(): void { this.movedAt = -Infinity; this.fitUntil = performance.now() + FitOnceMs; }

  /** Auto fit (false: fits everything unless the person moved the wall in the last 12 s) or pinned (true: never on its own). */
  setPinned(on: boolean): void { this.pinned = on; this.fitUntil = -Infinity; if (!on) this.movedAt = -Infinity; }

  /** Whether the camera aims at everything this frame. */
  private fitting(now: number): boolean { return aimsAtEverything(this.pinned, now, this.movedAt, this.fitUntil); }

  /** The person moved the wall: auto fit waits 12 s; a one-off fit under way stops. */
  private moved(): void { this.movedAt = performance.now(); this.fitUntil = -Infinity; }

  // ---------- the frame ----------
  /** Set by destroy(): no frame draws or asks for another after it. */
  private destroyed = false;
  private frame = (now: number) => {
    // One frame loop only: a frame called while another is already asked for (a test driving frames by hand) takes its place.
    cancelAnimationFrame(this.raf);
    if (this.destroyed) return;
    this.raf = requestAnimationFrame(this.frame);
    const dt = this.last ? Math.min(100, now - this.last) : 16;
    this.last = now;
    this.dt = dt;
    this.model?.tidy(now);
    if (this.wfFrom.model !== this.model || this.wfFrom.rev !== (this.model?.workflowsRev ?? -1) || this.mapRoom !== this.wantedRoom) this.rebuild();
    this.resize();
    this.applyShifts(dt);
    if (this.sim.alpha() > 0.001) this.sim.tick(); // at rest the nodes stay put (a wake: new nodes, a drag, new homes)
    if (this.fitting(now)) this.aimFit();
    const ease = 1 - Math.pow(0.0025, dt / 1000);
    this.cam.x += (this.target.x - this.cam.x) * ease;
    this.cam.y += (this.target.y - this.cam.y) * ease;
    this.cam.k += (this.target.k - this.cam.k) * ease;
    this.effects = this.effects.filter((e) => now - e.at < this.life(e));
    this.drawn = capEffects(this.effects, this.density, this.focusTeam());
    this.draw(now);
    this.settleSpacing(now);
  };

  /** How long an effect shows: under reduced motion a comet is a still line that stays a little longer. */
  private life(e: Effect): number {
    return this.reducedMotion && (e.kind === 'comet' || e.kind === 'you') ? StillCometMs : Life[e.kind];
  }

  /**
   * The workflow nodes' labels as drawn now: workflowNodeLabel at the zoom (only "#id" on a phone zoomed out), each
   * placed by placeWorkflowLabels so none covers another or a member (under its node, above it, shortened to "#id", or
   * none), and their font size: the camera's size, never under 8 px on screen.
   */
  private wfLabelPlan(): { font: number; labels: Map<string, PlacedLabel | null> } {
    const k = this.cam.k;
    const font = nodeLabelFont(k);
    const family = this.family();
    const c = this.ctx;
    const obstacles: LabelObstacle[] = [];
    // The other teams' code maps (a node and its label, as clusters() counts them): a label stays in its own cluster.
    // Card 411 (F5): every team's own map too, as drawn (a folder's box and, zoomed in, its name; a file's dot).
    const mapNames = k > 0.55;
    c.font = `600 10px ${family}`;
    for (const m of this.mapNodes) {
      obstacles.push({ x: m.x - 40, y: m.y - 10, w: 80, h: 20, owner: m.team.team.toLowerCase() });
      if (m.kind === 'folder') {
        obstacles.push({ x: m.x - 7, y: m.y - 6, w: 14, h: 12 });
        if (mapNames) {
          const fw = c.measureText(m.label.length > 24 ? m.label.slice(0, 23) + '…' : m.label).width;
          obstacles.push({ x: m.x - fw / 2, y: m.y - 19, w: fw, h: 12 });
        }
      } else {
        const r = m.kind === 'file' ? 5 : 7;
        obstacles.push({ x: m.x - r, y: m.y - r, w: r * 2, h: r * 2 });
      }
    }
    // The members' discs and names: a label must not run under them.
    for (const n of this.nodes.values()) {
      if (n.kind === 'member') {
        const ring = n.r + 5;
        obstacles.push({ x: n.x! - ring, y: n.y! - ring, w: ring * 2, h: ring * 2 }, this.memberNameBox(n));
      } else if (n.kind === 'team') obstacles.push(this.pillBox(n)); // card 411 (F3): the hub's name pill, drawn over the labels
    }
    const asks: LabelAsk[] = [];
    for (const n of this.nodes.values()) {
      if (n.kind !== 'wf' || !n.wf) continue;
      const status = workflowNodeLook(n.wf, !!this.model?.blockedWfs.has(n.key)).status;
      const order = status === 'blocked' ? 0 : status === 'working' ? 1 : 2;
      const at = Date.parse(n.wf.lastAt);
      asks.push({
        key: n.key, x: n.x!, y: n.y!, r: n.r, short: `#${n.wf.id}`, owner: n.team.toLowerCase(), rank: order * 1e14 - (Number.isFinite(at) ? at : 0),
        full: workflowNodeLabel(n.wf, { zoom: k, phone: this.phone }),
      });
    }
    c.font = `600 ${font}px ${family}`;
    return { font, labels: placeWorkflowLabels(asks, (t) => c.measureText(t).width, font * 1.2, obstacles) };
  }

  /** On a phone (the window phone-wide, narrowScreen.ts), zoomed out, the workflow nodes say only "#id". */
  private phone = false;
  setPhone(on: boolean): void { this.phone = on; }

  private resize(): void {
    const dpr = window.devicePixelRatio || 1;
    const w = this.canvas.clientWidth, h = this.canvas.clientHeight;
    if (w === this.w && h === this.h && dpr === this.dpr) return;
    this.w = w; this.h = h; this.dpr = dpr;
    this.canvas.width = Math.max(1, Math.round(w * dpr));
    this.canvas.height = Math.max(1, Math.round(h * dpr));
  }

  private aimFit(): void {
    const focus = this.focusTeam();
    const all = [...this.nodes.values()];
    const mine = focus === null ? all : all.filter((n) => n.team.toLowerCase() === focus);
    const ns = mine.length ? mine : all;
    if (!ns.length || !this.w) return;
    let x0 = Infinity, y0 = Infinity, x1 = -Infinity, y1 = -Infinity;
    for (const n of ns) {
      // A member's pad leaves room for the files fanned beside it.
      const pad = n.r + (n.kind === 'team' ? 70 : 64);
      x0 = Math.min(x0, n.x! - pad); x1 = Math.max(x1, n.x! + pad);
      y0 = Math.min(y0, n.y! - pad); y1 = Math.max(y1, n.y! + pad);
    }
    // The maps, with room for their labels: focused, that team's map only (all of them kept the focus from zooming in).
    for (const m of this.mapNodes) {
      if (mine.length && focus !== null && m.team.team.toLowerCase() !== focus) continue;
      x0 = Math.min(x0, m.x - 60); x1 = Math.max(x1, m.x + 60);
      y0 = Math.min(y0, m.y - 16); y1 = Math.max(y1, m.y + 16);
    }
    const top = 96, bottom = 40; // room for the counts at the top and the legend at the bottom
    const right = 2 * (youNodeAt(this.w, this.h).r + 22); // room for the You node at the right edge
    const k = Math.min(focus === null ? 1.6 : 2.2, Math.max(0.2, Math.min((this.w - right) / (x1 - x0), (this.h - top - bottom) / (y1 - y0))));
    this.target = { x: (x0 + x1) / 2 + right / 2 / k, y: (y0 + y1) / 2 - (top - bottom) / 2 / k, k };
  }

  private toScreen(x: number, y: number): [number, number] {
    return [(x - this.cam.x) * this.cam.k + this.w / 2, (y - this.cam.y) * this.cam.k + this.h / 2];
  }

  private toWorld(sx: number, sy: number): [number, number] {
    return [(sx - this.w / 2) / this.cam.k + this.cam.x, (sy - this.h / 2) / this.cam.k + this.cam.y];
  }

  private draw(now: number): void {
    const c = this.ctx;
    const motion = this.reduced ? 0 : 1;
    c.setTransform(this.dpr, 0, 0, this.dpr, 0, 0);
    c.fillStyle = Palette.ground;
    c.fillRect(0, 0, this.w, this.h);
    const glow = c.createRadialGradient(this.w / 2, this.h / 2, 0, this.w / 2, this.h / 2, Math.max(this.w, this.h) * 0.7);
    glow.addColorStop(0, 'rgba(56, 189, 248, 0.06)');
    glow.addColorStop(1, 'rgba(0, 0, 0, 0)');
    c.fillStyle = glow;
    c.fillRect(0, 0, this.w, this.h);

    // The world: a dot grid, then everything in world space.
    c.setTransform(this.dpr * this.cam.k, 0, 0, this.dpr * this.cam.k, this.dpr * (this.w / 2 - this.cam.x * this.cam.k), this.dpr * (this.h / 2 - this.cam.y * this.cam.k));
    this.drawGrid(c);
    const near = this.hover ? this.neighbours(this.hover.key) : null;
    const focus = this.focusTeam();
    const dim = (key: string) => (near && !near.has(key) ? 0.28 : 1) * focusAlpha(key, focus);

    for (const n of this.nodes.values()) if (n.kind === 'team') this.drawTerritory(c, n, now, motion, dim(n.key));
    this.drawWorkflowLines(c, now, motion, dim);
    for (const l of this.links) {
      c.globalAlpha = 0.35 * Math.min(dim(l.source.key), dim(l.target.key));
      c.strokeStyle = `hsl(${l.source.hue} 70% 60%)`;
      c.lineWidth = 1.2;
      c.beginPath(); c.moveTo(l.source.x!, l.source.y!); c.lineTo(l.target.x!, l.target.y!); c.stroke();
    }
    c.globalAlpha = 1;
    this.placeMap(now);
    const view = this.viewBox();
    this.drawMapLines(c, now, dim, view);
    c.globalCompositeOperation = 'lighter';
    for (const e of this.drawn) this.drawEffect(c, e, now, motion);
    c.globalCompositeOperation = 'source-over';
    for (const n of this.nodes.values()) if (n.kind === 'member') this.drawMember(c, n, now, motion, dim(n.key));
    this.drawMapNodes(c, now, dim, view);
    // The workflow nodes over the maps (card 411, F4): a square that sits on its map's folder still shows, and is hit first.
    const plan = this.wfLabelPlan();
    for (const n of this.nodes.values()) if (n.kind === 'wf') this.drawWorkflowNode(c, n, now, motion, dim(n.key), plan.font, plan.labels.get(n.key) ?? null);
    this.drawCommits(c, now);
    for (const n of this.nodes.values()) if (n.kind === 'team') this.drawTeamPill(c, n, dim(n.key));
    const icons = this.drawSteps(c, now, motion, dim);
    // Floating lines on the same member stack, newest lowest, so two never overlap, above its step icons.
    const lanes = new Map<string, number>();
    for (let i = this.drawn.length - 1; i >= 0; i--) {
      const e = this.drawn[i]!;
      if (e.kind !== 'float') continue;
      const lane = lanes.get(e.node) ?? 0;
      lanes.set(e.node, lane + 1);
      this.drawFloat(c, e, now, motion, lane, (icons.get(e.node) ?? 0) * StepIconGap);
    }
    // The screen: what flies to the You node, then the You node itself, at the wall's right edge.
    c.setTransform(this.dpr, 0, 0, this.dpr, 0, 0);
    for (const e of this.drawn) if (e.kind === 'you') this.drawToYou(c, e, now, motion);
    this.drawYouNode(c, now, motion);
  }

  // ---------- the workflow nodes (B080) ----------

  /** The dashed lines from each workflow node to the members working it (or its hub), a dot running along when worked. */
  private drawWorkflowLines(c: CanvasRenderingContext2D, now: number, motion: number, dim: (k: string) => number): void {
    c.lineWidth = 1;
    c.setLineDash([2, 4]);
    for (const l of this.wfmLinks) {
      const look = workflowNodeLook(l.source.wf!, !!this.model?.blockedWfs.has(l.source.key));
      const color = Palette[look.tone];
      c.globalAlpha = (l.target.kind === 'team' ? 0.22 : 0.5) * Math.min(dim(l.source.key), dim(l.target.key));
      c.strokeStyle = color;
      c.lineDashOffset = motion && look.status === 'working' ? -now / 90 : 0;
      c.beginPath(); c.moveTo(l.source.x!, l.source.y!); c.lineTo(l.target.x!, l.target.y!); c.stroke();
    }
    c.setLineDash([]);
    c.lineDashOffset = 0;
    c.globalAlpha = 1;
  }

  /** A workflow node: a rounded square in its state's colour with its mark, and its label where wfLabelPlan put it. */
  private drawWorkflowNode(c: CanvasRenderingContext2D, n: Node, now: number, motion: number, alpha: number, font: number, label: PlacedLabel | null): void {
    const look = workflowNodeLook(n.wf!, !!this.model?.blockedWfs.has(n.key));
    const color = Palette[look.tone];
    const x = n.x!, y = n.y!, r = n.r;
    c.globalAlpha = alpha;
    c.fillStyle = 'rgba(7, 11, 20, 0.95)';
    c.strokeStyle = color;
    c.lineWidth = look.status === 'blocked' ? 2.2 : 1.5;
    roundRect(c, x - r, y - r, r * 2, r * 2, 4); c.fill(); c.stroke();
    if (look.status === 'working' && motion) {
      const a = now / 700;
      c.lineWidth = 1.2;
      c.beginPath(); c.arc(x, y, r + 4, a, a + Math.PI * 0.6); c.stroke();
    }
    c.fillStyle = color; c.strokeStyle = color;
    drawWorkflowIcon(c, look.icon, x, y, r * 1.1);
    if (label) {
      c.font = `600 ${font}px ${this.family()}`;
      c.textAlign = 'center'; c.textBaseline = 'top';
      c.fillStyle = look.status === 'blocked' ? color : Palette.inkDim;
      c.fillText(label.text, label.x + label.w / 2, label.y);
    }
    c.globalAlpha = 1;
  }

  /** A workflow node leaving: completed, a green flash and it drifts away fading; closed, a grey cross that fades; expired, a grey clock. */
  private drawWorkflowEnd(c: CanvasRenderingContext2D, e: Extract<Effect, { kind: 'wfdone' }>, now: number, motion: number): void {
    const node = this.nodes.get(e.key);
    const ghost = this.wfGhosts.get(e.key);
    if (!node && !ghost) return;
    const t = (now - e.at) / Life.wfdone;
    const hub = node ? this.nodes.get(teamKey(node.team)) : null;
    let ox = ghost?.outX ?? 1, oy = ghost?.outY ?? 0;
    if (node && hub) { const d = Math.hypot(node.x! - hub.x!, node.y! - hub.y!) || 1; ox = (node.x! - hub.x!) / d; oy = (node.y! - hub.y!) / d; }
    const drift = motion && e.status === 'completed' ? (1 - Math.pow(1 - t, 2)) * 90 : 0;
    const ghostHub = !node && ghost?.hub ? this.nodes.get(ghost.hub) : undefined;
    const bx = node?.x ?? (ghostHub ? ghostHub.x! + ghost!.dx : ghost!.x), by = node?.y ?? (ghostHub ? ghostHub.y! + ghost!.dy : ghost!.y);
    const x = bx + ox * drift, y = by + oy * drift;
    const done = e.status === 'completed';
    const color = done ? Palette.done : Palette.inkDim;
    c.globalCompositeOperation = 'source-over';
    if (done && t < 0.45) {
      // The flash: a bright ring that opens out.
      const u = t / 0.45;
      c.globalAlpha = (1 - u) * 0.9;
      c.strokeStyle = color; c.lineWidth = 2.5;
      c.beginPath(); c.arc(x, y, WfR + 3 + (motion ? u * 26 : 6), 0, Math.PI * 2); c.stroke();
    }
    c.globalAlpha = Math.max(0, 1 - t) * (done ? 1 : 0.75);
    c.fillStyle = 'rgba(7, 11, 20, 0.95)';
    c.strokeStyle = color; c.lineWidth = 1.5;
    roundRect(c, x - WfR, y - WfR, WfR * 2, WfR * 2, 4); c.fill(); c.stroke();
    c.fillStyle = color;
    drawWorkflowIcon(c, done ? 'check' : e.status === 'expired' ? 'clock' : 'cross', x, y, WfR * 1.1);
    c.globalAlpha = 1;
    c.globalCompositeOperation = 'lighter';
  }

  // ---------- the You node (B080) ----------

  /** What waits for the person, flying from its member to the You node (screen space); reduced: a still line. */
  private drawToYou(c: CanvasRenderingContext2D, e: Extract<Effect, { kind: 'you' }>, now: number, motion: number): void {
    const from = this.nodes.get(e.from);
    if (!from) return;
    const [ax, ay] = this.toScreen(from.x!, from.y!);
    const you = this.youNode();
    const t = (now - e.at) / this.life(e);
    const color = Palette[e.color];
    c.globalAlpha = 1;
    if (!motion) {
      c.globalAlpha = 0.5 * (1 - Math.max(0, t - 0.7) / 0.3);
      c.strokeStyle = color; c.lineWidth = 1.2; c.setLineDash([3, 4]);
      c.beginPath(); c.moveTo(ax, ay); c.lineTo(you.x, you.y); c.stroke();
      c.setLineDash([]);
      c.globalAlpha = 1;
      return;
    }
    const ease = 1 - Math.pow(1 - Math.min(1, t / 0.8), 3);
    const mx = (ax + you.x) / 2, my = Math.min(ay, you.y) - 40;
    const at = (u: number): [number, number] => [(1 - u) * (1 - u) * ax + 2 * (1 - u) * u * mx + u * u * you.x, (1 - u) * (1 - u) * ay + 2 * (1 - u) * u * my + u * u * you.y];
    for (let i = 0; i < 8; i++) {
      const [px, py] = at(Math.max(0, ease - i * 0.035));
      c.fillStyle = color;
      c.globalAlpha = (1 - i / 8) * (1 - Math.max(0, t - 0.8) / 0.2);
      c.beginPath(); c.arc(px, py, 3.4 - i * 0.3, 0, Math.PI * 2); c.fill();
    }
    c.globalAlpha = 1;
  }

  /** The You node: a disc at the right edge saying "You", glowing with a count while something waits for the person. */
  private drawYouNode(c: CanvasRenderingContext2D, now: number, motion: number): void {
    if (!this.w || !this.h) return;
    const { x, y, r } = this.youNode();
    const count = this.model?.needYouItems().length ?? 0; // the header's "N need you" too (B088)
    const color = Palette.decision;
    // Something just arrived: a ring opens out from it.
    const arrived = this.drawn.filter((e) => e.kind === 'you' && (now - e.at) / this.life(e) > 0.75);
    c.save();
    if (count > 0) {
      const glow = motion ? 0.6 + 0.4 * Math.sin(now / 520) : 1;
      c.shadowColor = color;
      c.shadowBlur = 18 * glow;
    }
    c.fillStyle = count > 0 ? 'rgba(46, 26, 84, 0.95)' : 'rgba(12, 19, 34, 0.9)';
    c.strokeStyle = count > 0 ? color : 'rgba(148, 163, 184, 0.45)';
    c.lineWidth = count > 0 ? 2 : 1.2;
    c.beginPath(); c.arc(x, y, r, 0, Math.PI * 2); c.fill(); c.stroke();
    c.restore();
    for (const e of arrived) {
      const u = ((now - e.at) / this.life(e) - 0.75) / 0.25;
      c.globalAlpha = (1 - u) * 0.8;
      c.strokeStyle = color; c.lineWidth = 2;
      c.beginPath(); c.arc(x, y, r + 2 + (motion ? u * 18 : 4), 0, Math.PI * 2); c.stroke();
    }
    c.globalAlpha = 1;
    c.fillStyle = count > 0 ? Palette.ink : Palette.inkDim;
    c.font = `700 11px ${this.family()}`;
    c.textAlign = 'center'; c.textBaseline = 'middle';
    c.fillText('You', x, y + 0.5);
    if (count > 0) {
      const label = count > 99 ? '99+' : String(count);
      c.font = `700 10px ${this.family()}`;
      const bw = Math.max(16, c.measureText(label).width + 8);
      const bx = x + r * 0.7, by = y - r * 0.7;
      c.fillStyle = color;
      roundRect(c, bx - bw / 2, by - 8, bw, 16, 8); c.fill();
      c.fillStyle = Palette.ground;
      c.fillText(label, bx, by + 0.5);
    }
    if (count > 0 || this.hoverYou) {
      c.font = `600 9px ${this.family()}`;
      c.fillStyle = count > 0 ? color : Palette.inkDim;
      c.fillText(count > 0 ? 'NEEDS YOU' : 'nothing waits', x, y + r + 10);
    }
  }

  private hoverYou = false;

  // ---------- the code maps ----------

  /** The world rectangle on screen, a margin wider: map nodes and lines outside it are not drawn. */
  private viewBox(): { x0: number; y0: number; x1: number; y1: number } {
    const [x0, y0] = this.toWorld(-40, -40);
    const [x1, y1] = this.toWorld(this.w + 40, this.h + 40);
    return { x0, y0, x1, y1 };
  }

  /** Each team's direction away from the others (straight up for a team alone), by team key. */
  private teamOut = new Map<string, number>();

  private outwards(): void {
    let cx = 0, cy = 0, n = 0;
    for (const t of this.nodes.values()) if (t.kind === 'team') { cx += t.x!; cy += t.y!; n++; }
    if (n) { cx /= n; cy /= n; }
    this.teamOut.clear();
    for (const t of this.nodes.values()) {
      if (t.kind !== 'team') continue;
      const dx = t.x! - cx, dy = t.y! - cy;
      this.teamOut.set(t.key, n < 2 || Math.hypot(dx, dy) < 1 ? -Math.PI / 2 : Math.atan2(dy, dx));
    }
  }

  /** How far a team's members reach from its hub (its map's folders sit past them). */
  private reach(team: Node): number {
    let r = 60;
    for (const n of this.nodes.values()) if (n.kind === 'member' && n.team === team.team) r = Math.max(r, Math.hypot(n.x! - team.x!, n.y! - team.y!) + n.r);
    return r;
  }

  /**
   * Where every map node is this frame. Each team's collapsed view is worked out again when a file comes or goes, or
   * every MapViewMs; the layout follows the hub every frame, and each node eases to its place (snaps, under reduced motion).
   */
  private placeMap(now: number): void {
    this.outwards();
    const map = this.model?.map;
    if (!map || !map.size) {
      if (this.mapNodes.length) { this.mapNodes = []; this.mapById.clear(); this.mapPos.clear(); }
      this.wantedRoom = 0;
      return;
    }
    const L = this.mapLayouts;
    if (L.model !== this.model || L.rev !== map.rev || now - L.at > MapViewMs || now < L.at) {
      const names = map.teamNames();
      // The density caps the maps' file nodes in all (calm shows fewer), never above MaxMapNodes.
      const total = Math.min(MaxMapNodes, DensityCaps[this.density].mapNodes);
      const each = Math.max(6, Math.min(DefaultLimits.maxFiles, Math.floor(total / Math.max(1, names.length))));
      const list: { team: Node; view: TeamMapView }[] = [];
      for (const name of names) {
        const team = this.nodes.get(teamKey(name));
        if (team) list.push({ team, view: map.view(name, { maxFiles: each }) });
      }
      this.mapLayouts = { rev: map.rev, at: now, model: this.model, list };
      this.wantedRoom = mapRoomFor(list.map((l) => l.view), this.teamList.length); // the next frame moves the homes if it changed
    }
    const ease = this.reduced ? 1 : 1 - Math.pow(0.0004, this.dt / 1000);
    const nodes: MapNode[] = [];
    const byId = new Map<string, MapNode>();
    const put = (node: Omit<MapNode, 'x' | 'y'>, tx: number, ty: number, bornX: number, bornY: number) => {
      let p = this.mapPos.get(node.id);
      if (!p) this.mapPos.set(node.id, (p = { x: this.reduced ? tx : bornX, y: this.reduced ? ty : bornY }));
      p.x += (tx - p.x) * ease; p.y += (ty - p.y) * ease;
      const n: MapNode = { ...node, x: p.x, y: p.y };
      nodes.push(n);
      byId.set(n.id, n);
    };
    for (const { team, view: v } of this.mapLayouts.list) {
      const laid = layoutTeamMap(v, { x: team.x!, y: team.y! }, this.teamOut.get(team.key) ?? -Math.PI / 2, this.reach(team) + MapPastMembers);
      for (const f of laid.folders) {
        put({ id: f.id, kind: 'folder', team, folder: f.view.path, label: f.view.label, heat: f.view.heat, files: [...f.view.files, ...f.view.more] }, f.x, f.y, team.x!, team.y!);
        if (f.view.more.length)
          put({ id: `${f.id}+`, kind: 'more', team, folder: f.view.path, label: `+${f.view.more.length}`, heat: 0, files: f.view.more }, f.moreX, f.moreY, f.x, f.y);
      }
      for (const p of laid.files) {
        const folder = byId.get(`${team.key.slice(2)}|${p.folder}/`);
        put({ id: p.id, kind: 'file', team, file: p.file, folder: p.folder, label: p.file.name, heat: 0, files: [p.file] }, p.x, p.y, folder?.x ?? team.x!, folder?.y ?? team.y!);
      }
      if (laid.rest && v.moreFolders)
        put({ id: `${team.key}|+folders`, kind: 'rest', team, label: `+${v.moreFolders} folder${v.moreFolders === 1 ? '' : 's'}`, heat: 0, files: [] }, laid.rest.x, laid.rest.y, team.x!, team.y!);
    }
    for (const id of [...this.mapPos.keys()]) if (!byId.has(id)) this.mapPos.delete(id);
    this.mapNodes = nodes;
    this.mapById = byId;
  }

  /** How much of a map node is still shown as its files near their idle time: a file its own fade, a folder or "+N" its freshest file's. */
  private mapFade(m: MapNode): number {
    const map = this.model?.map;
    if (!map) return 1;
    const t = this.model!.logNow();
    if (m.file) return idleFade(m.file, t, map.idleMs);
    let most = m.files.length ? 0 : 1;
    for (const f of m.files) { most = Math.max(most, idleFade(f, t, map.idleMs)); if (most >= 1) break; }
    return most;
  }

  /** The tree's branches (hub to folder, folder to file) and a line from each member to every file it touched. */
  private drawMapLines(c: CanvasRenderingContext2D, now: number, dim: (k: string) => number, v: { x0: number; y0: number; x1: number; y1: number }): void {
    const off = (ax: number, ay: number, bx: number, by: number) =>
      (ax < v.x0 && bx < v.x0) || (ax > v.x1 && bx > v.x1) || (ay < v.y0 && by < v.y0) || (ay > v.y1 && by > v.y1);
    c.lineWidth = 1;
    for (const m of this.mapNodes) {
      const t = m.team;
      const a = dim(t.key) * this.mapFade(m);
      if (m.kind === 'folder' || m.kind === 'rest') {
        if (off(t.x!, t.y!, m.x, m.y)) continue;
        c.strokeStyle = `hsl(${t.hue} 55% 60%)`;
        c.globalAlpha = (0.14 + m.heat * 0.22) * a;
        c.beginPath(); c.moveTo(t.x!, t.y!); c.lineTo(m.x, m.y); c.stroke();
        continue;
      }
      const folder = this.mapById.get(`${t.key.slice(2)}|${m.folder}/`);
      if (folder && !off(folder.x, folder.y, m.x, m.y)) {
        c.strokeStyle = `hsl(${t.hue} 45% 62%)`;
        c.globalAlpha = 0.22 * a;
        c.beginPath(); c.moveTo(folder.x, folder.y); c.lineTo(m.x, m.y); c.stroke();
      }
      // The members' lines: one per member that touched it (to a "+N", one per member of the files it counts).
      const touched = new Map<string, { kind: string; light: number }>();
      for (const f of m.files) {
        const light = m.kind === 'file' ? fileLight(f, now) : 0.4;
        for (const [k, touch] of f.members) {
          const was = touched.get(k);
          if (!was || light > was.light) touched.set(k, { kind: touch.kind, light });
        }
      }
      for (const [k, t2] of touched) {
        const member = this.nodes.get(`m:${t.team.toLowerCase()}/${k}`);
        if (!member || off(member.x!, member.y!, m.x, m.y)) continue;
        const fresh = m.file ? Math.max(0, 1 - (now - Math.max(m.file.editShown, m.file.readShown)) / LightMs) : 0;
        c.strokeStyle = StepLook[t2.kind === 'read' ? 'read' : t2.kind === 'create' ? 'create' : 'edit'].color;
        c.globalAlpha = (t2.kind === 'read' ? 0.1 : 0.16) * (1 + fresh * 3) * Math.min(dim(member.key), a);
        c.lineWidth = fresh > 0 ? 1.4 : 0.8;
        if (m.kind === 'more') c.setLineDash([2, 3]);
        c.beginPath(); c.moveTo(member.x!, member.y!); c.lineTo(m.x, m.y); c.stroke();
        c.setLineDash([]);
        c.lineWidth = 1;
      }
    }
    c.globalAlpha = 1;
  }

  /**
   * The map's nodes. A folder: a rounded square that glows warmer (blue to amber) the busier it is, its name beside it.
   * A file: a dot in its touch's colour (teal new, green changed, blue only read), bright when changed and faint when
   * only read, lit up for a moment by a fresh touch and popping in when new; a ring once a commit confirmed it, a
   * double ring when two members touched it. "+N": a dashed ring with the count.
   */
  private drawMapNodes(c: CanvasRenderingContext2D, now: number, dim: (k: string) => number, v: { x0: number; y0: number; x1: number; y1: number }): void {
    const family = this.family();
    const labels = this.cam.k > 0.55;
    c.textBaseline = 'middle';
    for (const m of this.mapNodes) {
      if (m.x < v.x0 || m.x > v.x1 || m.y < v.y0 || m.y > v.y1) continue;
      const a = dim(m.team.key) * this.mapFade(m);
      const hovered = this.hoverMap?.id === m.id;
      if (m.kind === 'folder') {
        const hue = 205 - m.heat * 175; // cool blue to warm amber
        const g = c.createRadialGradient(m.x, m.y, 0, m.x, m.y, 16 + m.heat * 18);
        g.addColorStop(0, `hsla(${hue}, 90%, 58%, ${(0.16 + m.heat * 0.3) * a})`);
        g.addColorStop(1, `hsla(${hue}, 90%, 58%, 0)`);
        c.fillStyle = g;
        c.beginPath(); c.arc(m.x, m.y, 16 + m.heat * 18, 0, Math.PI * 2); c.fill();
        c.globalAlpha = a;
        c.fillStyle = 'rgba(12, 19, 34, 0.92)';
        c.strokeStyle = `hsl(${hue} 85% ${60 + m.heat * 8}%)`;
        c.lineWidth = hovered ? 2 : 1.3;
        roundRect(c, m.x - 6, m.y - 5, 12, 10, 2.5); c.fill(); c.stroke();
        if (labels || hovered) {
          c.font = `600 10px ${family}`;
          c.textAlign = 'center';
          const label = m.label.length > 24 ? m.label.slice(0, 23) + '…' : m.label;
          c.lineWidth = 3; c.strokeStyle = 'rgba(7, 11, 20, 0.85)'; c.lineJoin = 'round';
          c.strokeText(label, m.x, m.y - 13);
          c.fillStyle = `hsl(${hue} 80% 78%)`;
          c.fillText(label, m.x, m.y - 13);
        }
      } else if (m.kind === 'file' && m.file) {
        const f = m.file;
        const light = fileLight(f, now);
        const pop = popScale(f.bornShown, now, this.reduced);
        const color = f.editAt === -Infinity ? StepLook.read.color : f.created ? StepLook.create.color : StepLook.edit.color;
        const r = 3.6 * pop;
        const flash = Math.max(0, 1 - (now - Math.max(f.editShown, f.readShown)) / LightMs);
        if (flash > 0) {
          c.globalAlpha = flash * 0.5 * a * (f.editShown >= f.readShown ? 1 : 0.5);
          c.fillStyle = color;
          c.beginPath(); c.arc(m.x, m.y, r + 4 + flash * 4, 0, Math.PI * 2); c.fill();
        }
        c.globalAlpha = light * a;
        c.fillStyle = color;
        c.beginPath(); c.arc(m.x, m.y, r, 0, Math.PI * 2); c.fill();
        c.strokeStyle = color;
        c.lineWidth = 1;
        if (f.confirmed) { c.beginPath(); c.arc(m.x, m.y, r + 2.2, 0, Math.PI * 2); c.stroke(); }
        if (f.members.size > 1) { c.strokeStyle = Palette.ink; c.globalAlpha = 0.7 * a; c.beginPath(); c.arc(m.x, m.y, r + 4.2, 0, Math.PI * 2); c.stroke(); c.beginPath(); c.arc(m.x, m.y, r + 6.2, 0, Math.PI * 2); c.stroke(); }
        if (hovered) { c.strokeStyle = Palette.ink; c.globalAlpha = a; c.beginPath(); c.arc(m.x, m.y, r + 8, 0, Math.PI * 2); c.stroke(); }
        if (hovered || (labels && (this.cam.k > 0.9 || flash > 0))) {
          c.font = `500 9.5px ${family}`;
          const right = Math.cos(Math.atan2(m.y - m.team.y!, m.x - m.team.x!)) >= 0;
          c.textAlign = right ? 'left' : 'right';
          const label = m.label.length > 26 ? m.label.slice(0, 25) + '…' : m.label;
          const lx = m.x + (right ? r + 5 : -r - 5);
          c.globalAlpha = a * Math.max(light, 0.6);
          c.lineWidth = 3; c.strokeStyle = 'rgba(7, 11, 20, 0.85)'; c.lineJoin = 'round';
          c.strokeText(label, lx, m.y + 0.5);
          c.fillStyle = flash > 0 || hovered ? Palette.ink : 'rgba(226, 232, 240, 0.78)';
          c.fillText(label, lx, m.y + 0.5);
        }
      } else {
        c.globalAlpha = a * 0.9;
        c.strokeStyle = Palette.inkDim;
        c.lineWidth = hovered ? 1.6 : 1.1;
        c.setLineDash([2, 2]);
        const r = m.kind === 'rest' ? 9 : 7;
        c.fillStyle = Palette.ground;
        c.beginPath(); c.arc(m.x, m.y, r, 0, Math.PI * 2); c.fill(); c.stroke();
        c.setLineDash([]);
        if (labels || hovered || m.kind === 'rest') {
          c.font = `600 9.5px ${family}`;
          c.textAlign = 'left';
          c.fillStyle = Palette.inkDim;
          c.fillText(m.label, m.x + r + 4, m.y + 0.5);
        }
      }
    }
    c.globalAlpha = 1;
  }

  /** Where each commit dot is this frame, for the hover. */
  private commitSpots: { e: Extract<Effect, { kind: 'commit' }>; x: number; y: number }[] = [];

  /**
   * The commits: the files gather into the member, the dot travels its spoke to the hub, then rests there with the
   * team's other recent commits in a small arc above it until it fades. Under reduced motion it is simply at the hub.
   * The density caps how many a team shows (capEffects); with a team focused only its commits are drawn.
   */
  private drawCommits(c: CanvasRenderingContext2D, now: number): void {
    const spots: typeof this.commitSpots = [];
    const resting = new Map<string, number>();
    for (let i = this.drawn.length - 1; i >= 0; i--) {
      const e = this.drawn[i]!;
      if (e.kind !== 'commit') continue;
      const hub = this.nodes.get(teamKey(e.team));
      if (!hub) continue;
      const member = e.node ? this.nodes.get(e.node) : undefined;
      const age = now - e.at;
      const phase = commitPhase(age, this.reduced);
      const fade = Math.min(1, (Life.commit - age) / 2000);
      if (fade <= 0) continue;
      let x: number, y: number;
      if (phase.resting || !member) {
        const slot = resting.get(hub.key) ?? 0;
        resting.set(hub.key, slot + 1);
        if (slot >= 8) continue;
        const a = -Math.PI / 2 + (slot - 3.5) * 0.36;
        x = hub.x! + Math.cos(a) * 46; y = hub.y! + Math.sin(a) * 46;
      } else {
        x = member.x! + (hub.x! - member.x!) * phase.travel;
        y = member.y! + (hub.y! - member.y!) * phase.travel;
      }
      if (phase.gather !== null && member) {
        // The files, drawn in toward the member.
        c.fillStyle = Palette.commit;
        for (const id of e.files.slice(0, 24)) {
          const f = this.mapById.get(id);
          if (!f) continue;
          const u = 1 - Math.pow(1 - phase.gather, 2);
          c.globalAlpha = 0.85;
          c.beginPath(); c.arc(f.x + (member.x! - f.x) * u, f.y + (member.y! - f.y) * u, 2.2, 0, Math.PI * 2); c.fill();
        }
      }
      if (phase.gather === null || !member) {
        const hovered = this.hoverCommit === e;
        const r = 4.2 + Math.min(3, Math.log2(1 + e.files.length));
        c.globalAlpha = fade;
        c.save();
        if (!this.reduced) { c.shadowColor = Palette.commit; c.shadowBlur = phase.resting ? 6 : 14; }
        c.fillStyle = Palette.commit;
        c.beginPath(); c.arc(x, y, r, 0, Math.PI * 2); c.fill();
        c.restore();
        if (hovered) { c.strokeStyle = Palette.ink; c.lineWidth = 1.2; c.beginPath(); c.arc(x, y, r + 3, 0, Math.PI * 2); c.stroke(); }
        spots.push({ e, x, y });
      }
    }
    c.globalAlpha = 1;
    this.commitSpots = spots;
  }

  // ---------- the members' steps ----------

  /**
   * Each step's icon in a badge of its kind's colour, stacked above its member (newest lowest) and rising as it
   * fades, with its note (the command, the pattern, the host...) beside it when there is room. Returns how many icons
   * each member has up, so its floating lines go above them.
   */
  private drawSteps(c: CanvasRenderingContext2D, now: number, motion: number, dim: (k: string) => number): Map<string, number> {
    const count = new Map<string, number>();
    const ease = this.reduced ? 1 : 1 - Math.pow(0.0005, this.dt / 1000);
    const captions = this.cam.k > 0.6;
    const each = DensityCaps[this.density].stepsEach;
    for (let i = this.drawn.length - 1; i >= 0; i--) {
      const e = this.drawn[i]!;
      if (e.kind !== 'step') continue;
      const lane = count.get(e.node) ?? 0;
      if (lane >= each) continue;
      count.set(e.node, lane + 1);
      const n = this.nodes.get(e.node);
      if (!n) continue;
      const t = (now - e.at) / Life.step;
      const was = this.stepSlots.get(e) ?? lane;
      const slot = was + (lane - was) * ease;
      this.stepSlots.set(e, slot);
      const alpha = (this.reduced ? (t < 0.85 ? 1 : (1 - t) / 0.15) : t < 0.1 ? t / 0.1 : 1 - Math.max(0, (t - 0.55) / 0.45)) * dim(n.key);
      if (alpha <= 0.01) continue;
      const rise = motion ? (1 - Math.pow(1 - t, 2)) * 16 : 0;
      const look = StepLook[e.step];
      const x = n.x!, y = n.y! - n.r - 16 - slot * StepIconGap - rise;
      c.globalAlpha = alpha;
      c.fillStyle = 'rgba(7, 11, 20, 0.88)';
      c.strokeStyle = look.color;
      c.lineWidth = 1.2;
      c.beginPath(); c.arc(x, y, 8.5, 0, Math.PI * 2); c.fill(); c.stroke();
      c.strokeStyle = look.color; c.fillStyle = look.color;
      drawStepIcon(c, e.step, x, y, 11);
      if (captions && e.caption) {
        c.font = `500 10px ${this.family()}`;
        c.textAlign = 'left'; c.textBaseline = 'middle';
        const w = c.measureText(e.caption).width;
        c.fillStyle = 'rgba(7, 11, 20, 0.78)';
        roundRect(c, x + 11, y - 7, w + 10, 14, 7); c.fill();
        c.fillStyle = look.color;
        c.fillText(e.caption, x + 16, y + 0.5);
      }
    }
    c.globalAlpha = 1;
    return count;
  }

  private drawGrid(c: CanvasRenderingContext2D): void {
    const [x0, y0] = this.toWorld(0, 0);
    const [x1, y1] = this.toWorld(this.w, this.h);
    const step = 36;
    c.fillStyle = 'rgba(148, 163, 184, 0.09)';
    for (let x = Math.floor(x0 / step) * step; x < x1; x += step)
      for (let y = Math.floor(y0 / step) * step; y < y1; y += step) c.fillRect(x - 0.8, y - 0.8, 1.6, 1.6);
  }

  private drawTerritory(c: CanvasRenderingContext2D, n: Node, now: number, motion: number, alpha: number): void {
    const breathe = 1 + 0.03 * Math.sin(now / 1800) * motion;
    const r = (140 + this.membersOf(n.team) * 14) * breathe;
    const g = c.createRadialGradient(n.x!, n.y!, 0, n.x!, n.y!, r);
    g.addColorStop(0, `hsla(${n.hue}, 70%, 55%, ${0.16 * alpha})`);
    g.addColorStop(1, `hsla(${n.hue}, 70%, 55%, 0)`);
    c.fillStyle = g;
    c.beginPath(); c.arc(n.x!, n.y!, r, 0, Math.PI * 2); c.fill();
    c.fillStyle = `hsla(${n.hue}, 70%, 60%, ${0.9 * alpha})`;
    c.beginPath(); c.arc(n.x!, n.y!, 5, 0, Math.PI * 2); c.fill();
  }

  private drawTeamPill(c: CanvasRenderingContext2D, n: Node, alpha: number): void {
    const text = n.name;
    const { x, y, w } = this.pillBox(n);
    c.globalAlpha = alpha;
    c.fillStyle = 'rgba(12, 19, 34, 0.86)';
    c.strokeStyle = `hsla(${n.hue}, 70%, 60%, 0.7)`;
    c.lineWidth = 1;
    roundRect(c, x, y, w, 22, 11); c.fill(); c.stroke();
    c.fillStyle = Palette.ink;
    c.textAlign = 'center'; c.textBaseline = 'middle';
    c.fillText(text, n.x!, y + 11.5);
    c.globalAlpha = 1;
  }

  private drawMember(c: CanvasRenderingContext2D, n: Node, now: number, motion: number, alpha: number): void {
    const state = this.stateOf ? this.stateOf(n.key) : n.state ?? 'idle';
    const color = this.colors[state];
    const x = n.x!, y = n.y!, r = n.r;
    c.globalAlpha = alpha;
    // The work rhythm: the disc pulses faster the more steps the member takes (not in a replay's past or reduced).
    const period = motion ? pulsePeriodMs(this.model?.stepRate(n.key, now) ?? 0) : null;
    const pulse = period ? 0.5 + 0.5 * Math.sin((now / period) * Math.PI * 2) : 0;
    // Body: a disc in the team's hue that glows in the state's colour.
    c.save();
    c.shadowColor = color;
    c.shadowBlur = state === 'idle' ? 0 : 16 + pulse * 8;
    const pr = r * (1 + 0.07 * pulse);
    const body = c.createRadialGradient(x - r * 0.3, y - r * 0.3, 0, x, y, pr);
    body.addColorStop(0, `hsl(${n.hue} 60% ${state === 'idle' ? 38 : 52}%)`);
    body.addColorStop(1, `hsl(${n.hue} 60% ${state === 'idle' ? 16 : 22}%)`);
    c.fillStyle = body;
    c.beginPath(); c.arc(x, y, pr, 0, Math.PI * 2); c.fill();
    c.restore();
    // The face, or initials until the sheet loads.
    const tile = avatarSheet.image && avatarSheet.cols ? memberAvatar(n.team, n.name) : -1;
    c.save();
    c.beginPath(); c.arc(x, y, r - 1, 0, Math.PI * 2); c.clip();
    if (tile >= 0 && avatarSheet.image) {
      const s = tileSource(tile, { cols: avatarSheet.cols, rows: avatarSheet.rows, width: avatarSheet.image.naturalWidth, height: avatarSheet.image.naturalHeight });
      c.globalAlpha = alpha * (state === 'idle' ? 0.8 : 1);
      c.drawImage(avatarSheet.image, s.sx, s.sy, s.sw, s.sh, x - r, y - r, r * 2, r * 2);
    } else {
      c.fillStyle = Palette.ink;
      c.font = `700 ${Math.round(r * 0.9)}px ${this.family()}`;
      c.textAlign = 'center'; c.textBaseline = 'middle';
      c.fillText(initials(n.name), x, y + 1);
    }
    c.restore();
    c.globalAlpha = alpha;

    // The state ring.
    const ring = r + 5;
    c.strokeStyle = color;
    c.lineCap = 'round';
    if (state === 'running') {
      const turn = (now / 520) * motion;
      c.lineWidth = 2.5;
      for (let i = 0; i < 3; i++) {
        const a = turn + (i * Math.PI * 2) / 3;
        c.beginPath(); c.arc(x, y, ring, a, a + Math.PI / 2.4); c.stroke();
      }
    } else if (state === 'waiting') {
      c.lineWidth = 2;
      c.setLineDash([3, 5]);
      c.lineDashOffset = (-now / 60) * motion;
      c.beginPath(); c.arc(x, y, ring, 0, Math.PI * 2); c.stroke();
      c.setLineDash([]);
    } else if (state === 'idle') {
      c.globalAlpha = alpha * 0.7;
      c.lineWidth = 1.2;
      c.beginPath(); c.arc(x, y, ring - 1, 0, Math.PI * 2); c.stroke();
      c.globalAlpha = alpha;
    } else {
      const throb = motion ? 0.5 + 0.5 * Math.sin(now / 220) : 1;
      c.globalAlpha = alpha * (0.5 + 0.5 * throb);
      c.lineWidth = 2.5 + 1.5 * throb;
      c.beginPath(); c.arc(x, y, ring + 3 * throb, 0, Math.PI * 2); c.stroke();
      c.globalAlpha = alpha;
    }
    // The Manager: an outer ring with three ticks.
    if (n.role === 'manager') {
      c.strokeStyle = `hsla(${n.hue}, 70%, 70%, 0.55)`;
      c.lineWidth = 1;
      c.beginPath(); c.arc(x, y, ring + 6, 0, Math.PI * 2); c.stroke();
      for (let i = 0; i < 3; i++) {
        const a = -Math.PI / 2 + (i * Math.PI * 2) / 3;
        c.beginPath(); c.moveTo(x + Math.cos(a) * (ring + 4), y + Math.sin(a) * (ring + 4)); c.lineTo(x + Math.cos(a) * (ring + 9), y + Math.sin(a) * (ring + 9)); c.stroke();
      }
    }
    this.drawRhythm(c, n, now, motion, alpha, ring);
    // Queued instructions orbit as small dots.
    const queued = Math.min(8, n.queued ?? 0);
    for (let i = 0; i < queued; i++) {
      const a = (now / 1400) * motion + (i * Math.PI * 2) / queued;
      c.fillStyle = Palette.tell;
      c.beginPath(); c.arc(x + Math.cos(a) * (ring + 9), y + Math.sin(a) * (ring + 9), 2.4, 0, Math.PI * 2); c.fill();
    }
    // The picked member wears a halo.
    if (this.picked === n.key) {
      c.strokeStyle = 'rgba(226, 232, 240, 0.8)';
      c.lineWidth = 1.5;
      c.setLineDash([2, 3]);
      c.beginPath(); c.arc(x, y, ring + 13, 0, Math.PI * 2); c.stroke();
      c.setLineDash([]);
    }
    // The name, and what it needs when it needs something.
    c.fillStyle = state === 'idle' ? Palette.inkDim : Palette.ink;
    c.font = `500 11px ${this.family()}`;
    c.textAlign = 'center'; c.textBaseline = 'top';
    const label = spaced(n.name);
    c.fillText(label.length > 22 ? label.slice(0, 21) + '…' : label, x, y + ring + 6);
    let below = y + ring + 20;
    if (this.cam.k > 0.6 && (state === 'decision' || state === 'blocked' || state === 'failed')) {
      c.fillStyle = color;
      c.font = `700 9px ${this.family()}`;
      c.fillText(state === 'decision' ? 'NEEDS YOU' : state === 'blocked' ? 'BLOCKED' : 'FAILED', x, below);
      below += 12;
    }
    // What it is doing now ("editing parser.ts"), fading out over the last seconds of its half minute.
    const doing = this.model?.doing.get(n.key);
    if (doing && this.cam.k > 0.5) {
      const age = now - doing.at;
      const words = doing.text.length > 30 ? doing.text.slice(0, 29) + '…' : doing.text;
      c.globalAlpha = alpha * Math.max(0, Math.min(1, (DoingMs - age) / 6000)) * (age < 3000 ? 1 : 0.75);
      c.fillStyle = StepLook[doing.kind].color;
      c.font = `italic 500 9.5px ${this.family()}`;
      c.fillText(words, x, below);
      c.globalAlpha = alpha;
    }
    c.globalAlpha = 1;
  }

  /**
   * A command's ring filling round the member while it runs (full when it ends), and a soft shimmer while it thinks.
   * Reduced motion: the ring is a steady mark of how far it got, the shimmer a steady faint halo.
   */
  private drawRhythm(c: CanvasRenderingContext2D, n: Node, now: number, motion: number, alpha: number, ring: number): void {
    const run = this.model?.runs.get(n.key);
    if (run) {
      const fill = run.ended !== null ? 1 : runFill(now - run.at);
      const fade = run.ended !== null ? Math.max(0, 1 - (now - run.ended) / RunEndShowMs) : 1;
      const color = run.exit === null ? StepLook.run.color : run.exit === 0 ? Palette.done : Palette.fail;
      const rr = ring + (n.role === 'manager' ? 12 : 7);
      c.globalAlpha = alpha * 0.25 * fade;
      c.strokeStyle = color; c.lineWidth = 2;
      c.beginPath(); c.arc(n.x!, n.y!, rr, 0, Math.PI * 2); c.stroke();
      c.globalAlpha = alpha * 0.95 * fade;
      c.lineWidth = 2.4; c.lineCap = 'round';
      c.beginPath(); c.arc(n.x!, n.y!, rr, -Math.PI / 2, -Math.PI / 2 + fill * Math.PI * 2); c.stroke();
    }
    if (this.model?.thinking(n.key, now)) {
      const rr = n.r + 2;
      const g = c.createRadialGradient(n.x!, n.y!, rr * 0.6, n.x!, n.y!, rr + 10);
      const a = motion ? 0.16 + 0.12 * Math.sin(now / 380) : 0.18;
      g.addColorStop(0, 'rgba(203, 213, 225, 0)');
      g.addColorStop(0.6, `rgba(203, 213, 225, ${a})`);
      g.addColorStop(1, 'rgba(203, 213, 225, 0)');
      c.globalAlpha = alpha;
      c.fillStyle = g;
      c.beginPath(); c.arc(n.x!, n.y!, rr + 10, 0, Math.PI * 2); c.fill();
      if (motion) {
        // A glint that slides round the rim.
        const s = now / 900;
        c.strokeStyle = 'rgba(226, 232, 240, 0.35)'; c.lineWidth = 1.5;
        c.beginPath(); c.arc(n.x!, n.y!, rr + 1, s, s + 0.7); c.stroke();
      }
    }
    c.globalAlpha = alpha;
  }

  private drawEffect(c: CanvasRenderingContext2D, e: Effect, now: number, motion: number): void {
    // Steps and floating lines are drawn above the members (drawSteps, drawFloat).
    // Commits are drawn by drawCommits, what waits for the person by drawToYou.
    if (e.kind === 'step' || e.kind === 'float' || e.kind === 'you' || e.kind === 'commit') return;
    if (e.kind === 'wfdone') { this.drawWorkflowEnd(c, e, now, motion); return; }
    const t = (now - e.at) / this.life(e);
    if (e.kind === 'merge' || e.kind === 'push') { if (motion) this.drawCodeBurst(c, e, t); return; }
    const color = Palette[e.color];
    if (e.kind === 'comet') {
      const a = this.nodes.get(e.from), b = this.nodes.get(e.to);
      if (!a || !b) return;
      // A comet's curve; a hand-back (with a mark) comes home along the same curve as the instruction went out.
      const [p, q] = e.mark ? [b, a] : [a, b];
      const mx = (p.x! + q.x!) / 2 - (q.y! - p.y!) * 0.18, my = (p.y! + q.y!) / 2 + (q.x! - p.x!) * 0.18;
      const at = (u: number): [number, number] => [(1 - u) * (1 - u) * a.x! + 2 * (1 - u) * u * mx + u * u * b.x!, (1 - u) * (1 - u) * a.y! + 2 * (1 - u) * u * my + u * u * b.y!];
      // Reduced motion: nothing travels; a still line with the label (and the mark) at its end, fading.
      const ease = motion ? 1 - Math.pow(1 - t, 3) : 1;
      if (motion) {
        for (let i = 0; i < 8; i++) {
          const u = Math.max(0, ease - i * 0.035);
          const [px, py] = at(u);
          c.fillStyle = color;
          c.globalAlpha = (1 - i / 8) * (1 - Math.max(0, t - 0.85) / 0.15);
          c.beginPath(); c.arc(px, py, 3.2 - i * 0.3, 0, Math.PI * 2); c.fill();
        }
      } else {
        c.globalAlpha = 0.45 * (1 - Math.max(0, t - 0.7) / 0.3);
        c.strokeStyle = color; c.lineWidth = 1.2; c.setLineDash([3, 4]);
        c.beginPath(); c.moveTo(a.x!, a.y!); c.quadraticCurveTo(mx, my, b.x!, b.y!); c.stroke();
        c.setLineDash([]);
      }
      c.globalAlpha = 1;
      const shown = motion ? t < 0.9 : true;
      if ((e.label || e.mark) && shown) {
        const [px, py] = at(ease);
        c.globalCompositeOperation = 'source-over';
        c.globalAlpha = motion ? Math.min(1, (0.9 - t) * 4) : 1 - Math.max(0, t - 0.7) / 0.3;
        if (e.mark) {
          // The check or cross rides the head in a small dark disc.
          c.fillStyle = 'rgba(7, 11, 20, 0.92)';
          c.strokeStyle = color; c.lineWidth = 1.3;
          c.beginPath(); c.arc(px, py, 7, 0, Math.PI * 2); c.fill(); c.stroke();
          c.fillStyle = color; c.strokeStyle = color;
          drawHandbackMark(c, e.mark, px, py, 9);
        }
        if (e.label) {
          c.fillStyle = color;
          c.font = `${e.card != null ? 700 : 500} 10px ${this.family()}`;
          c.textAlign = 'center'; c.textBaseline = 'bottom';
          c.fillText(e.label, px, py - (e.mark ? 10 : 7));
        }
        c.globalAlpha = 1;
        c.globalCompositeOperation = 'lighter';
      }
    } else if (e.kind === 'burst' || e.kind === 'ripple') {
      const n = this.nodes.get(e.node);
      if (!n) return;
      const big = e.kind === 'burst' && e.big;
      const r = n.r + 6 + (motion ? t : 0.2) * (big ? 46 : 30);
      c.strokeStyle = color;
      c.globalAlpha = (1 - t) * 0.9;
      c.lineWidth = e.kind === 'ripple' ? 1.5 : 2.5;
      c.beginPath(); c.arc(n.x!, n.y!, r, 0, Math.PI * 2); c.stroke();
      if (e.kind === 'burst' && motion)
        for (let i = 0; i < (big ? 14 : 8); i++) {
          const a = (i / (big ? 14 : 8)) * Math.PI * 2 + e.at;
          const d = n.r + 4 + t * (big ? 60 : 38);
          c.fillStyle = color;
          c.beginPath(); c.arc(n.x! + Math.cos(a) * d, n.y! + Math.sin(a) * d, 2 * (1 - t), 0, Math.PI * 2); c.fill();
        }
      c.globalAlpha = 1;
    }
  }

  /**
   * A merge: a ring off the hub and sparks running out along every spoke (a landing on main: two rings, wider, more
   * sparks, in the finished colour). A push: a pulse that runs from the hub out past the wall's edge, away from the
   * other teams. Not drawn under reduced motion: the map just updates.
   */
  private drawCodeBurst(c: CanvasRenderingContext2D, e: Extract<Effect, { kind: 'merge' | 'push' }>, t: number): void {
    const hub = this.nodes.get(teamKey(e.team));
    if (!hub) return;
    const x = hub.x!, y = hub.y!;
    if (e.kind === 'push') {
      const out = this.teamOut.get(hub.key) ?? -Math.PI / 2;
      const far = (Math.hypot(this.w, this.h) / Math.max(0.05, this.cam.k)) * 0.9;
      const d = 20 + t * far;
      const px = x + Math.cos(out) * d, py = y + Math.sin(out) * d;
      c.strokeStyle = Palette.tell;
      c.globalAlpha = (1 - t) * 0.8;
      c.lineWidth = 2;
      c.beginPath(); c.moveTo(x + Math.cos(out) * Math.max(20, d - 120), y + Math.sin(out) * Math.max(20, d - 120)); c.lineTo(px, py); c.stroke();
      c.beginPath(); c.arc(px, py, 6 + t * 10, out - 0.7, out + 0.7); c.stroke();
      c.globalAlpha = 1;
      return;
    }
    const color = e.big ? Palette.done : Palette.handback;
    const rings = e.big ? 2 : 1;
    c.strokeStyle = color;
    for (let i = 0; i < rings; i++) {
      const u = Math.max(0, t - i * 0.18);
      if (u <= 0 || u >= 1) continue;
      c.globalAlpha = (1 - u) * 0.9;
      c.lineWidth = e.big ? 3 : 2.2;
      c.beginPath(); c.arc(x, y, 10 + u * (e.big ? 140 : 90), 0, Math.PI * 2); c.stroke();
    }
    c.fillStyle = color;
    for (const l of this.links) {
      if (l.source !== hub) continue;
      const m = l.target;
      for (let k = 0; k < (e.big ? 3 : 2); k++) {
        const u = Math.min(1, t * 1.6 - k * 0.15);
        if (u <= 0) continue;
        c.globalAlpha = (1 - u) * 0.95;
        c.beginPath(); c.arc(x + (m.x! - x) * u, y + (m.y! - y) * u, e.big ? 3 : 2.4, 0, Math.PI * 2); c.fill();
      }
    }
    c.globalAlpha = 1;
  }

  private drawFloat(c: CanvasRenderingContext2D, e: Extract<Effect, { kind: 'float' }>, now: number, motion: number, lane: number, lift = 0): void {
    const n = this.nodes.get(e.node);
    if (!n) return;
    const t = (now - e.at) / Life.float;
    c.globalAlpha = t < 0.15 ? t / 0.15 : 1 - Math.max(0, t - 0.6) / 0.4;
    c.fillStyle = Palette[e.color];
    c.font = `600 10.5px ${this.family()}`;
    c.textAlign = 'center'; c.textBaseline = 'bottom';
    const fy = n.y! - n.r - 10 - lift - lane * 13 - t * 26 * (motion || 0.4);
    // A dark pill under the words, so they read over the step icons and file lines.
    const w = c.measureText(e.text).width;
    const a = c.globalAlpha;
    c.globalAlpha = a * 0.75;
    c.fillStyle = Palette.ground;
    roundRect(c, n.x! - w / 2 - 5, fy - 13, w + 10, 15, 6); c.fill();
    c.globalAlpha = a;
    c.fillStyle = Palette[e.color];
    c.fillText(e.text, n.x!, fy);
    c.globalAlpha = 1;
  }

  private family(): string {
    return getComputedStyle(this.canvas).fontFamily || 'system-ui, sans-serif';
  }

  private neighbours(key: string): Set<string> {
    const set = new Set([key]);
    for (const l of [...this.links, ...this.wfmLinks]) {
      if (l.source.key === key) set.add(l.target.key);
      if (l.target.key === key) set.add(l.source.key);
    }
    return set;
  }

  // ---------- the person's hand: hover, pick, drag, pan, zoom, fit ----------
  private unlisten: (() => void) | null = null;

  private nodeAt(sx: number, sy: number): Node | null {
    const [x, y] = this.toWorld(sx, sy);
    return nearestNodeAt(this.nodes.values(), x, y, this.cam.k);
  }

  /**
   * The map node under a screen point, if any (they are small, so the reach is a few pixels on screen). Card 411 (F4): not
   * when a workflow node in reach is nearer the point, so a square that sits on its map's folder can still be hovered and
   * clicked (mapBeatsWorkflow).
   */
  private mapAt(sx: number, sy: number): MapNode | null {
    const [x, y] = this.toWorld(sx, sy);
    const reach = 9 / this.cam.k;
    let best: MapNode | null = null, bestD = Infinity;
    for (const m of this.mapNodes) {
      const d = Math.hypot(m.x - x, m.y - y);
      if (d < reach && d < bestD) { best = m; bestD = d; }
    }
    if (!best) return null;
    const wfs = [...this.nodes.values()].filter((n) => n.kind === 'wf');
    return mapBeatsWorkflow(bestD, nearestNodeAt(wfs, x, y, this.cam.k), x, y) ? best : null;
  }

  /** The commit dot under a screen point, if any. */
  private commitAt(sx: number, sy: number): (typeof this.commitSpots)[number] | null {
    const [x, y] = this.toWorld(sx, sy);
    const reach = 10 / this.cam.k;
    let best: (typeof this.commitSpots)[number] | null = null, bestD = Infinity;
    for (const s of this.commitSpots) {
      const d = Math.hypot(s.x - x, s.y - y);
      if (d < reach && d < bestD) { best = s; bestD = d; }
    }
    return best;
  }

  /** What the tip says about a map node. */
  private mapHover(m: MapNode, sx: number, sy: number): SceneHover {
    const base = { key: m.team.key, team: m.team.team, x: sx, y: sy - 8 };
    if (m.kind === 'file' && m.file) {
      const f = m.file;
      const members = [...f.members.values()].sort((a, b) => b.at - a.at).map((t) => t.member);
      return {
        ...base, kind: 'file', name: members[0] ?? m.team.team, path: f.path, touches: f.touches,
        fileKind: f.editAt === -Infinity ? 'read' : f.created ? 'create' : 'edit', sha: f.sha ? shortSha(f.sha) : null, members,
      };
    }
    if (m.kind === 'rest') return { ...base, kind: 'folder', name: m.label, more: [] };
    return { ...base, kind: 'folder', name: m.kind === 'folder' ? m.folder || '/' : `${m.label} in ${m.folder || '/'}`, more: m.files.map((f) => f.path) };
  }

  /** Forgets every map node's place (a replay starts, or live comes back). */
  clearFiles(): void { this.mapPos.clear(); this.mapNodes = []; this.mapById.clear(); this.hoverMap = null; this.hoverCommit = null; this.commitSpots = []; this.mapLayouts = { rev: -1, at: -Infinity, model: null, list: [] }; }

  private listen(): void {
    const cv = this.canvas;
    const local = (e: PointerEvent | WheelEvent | MouseEvent) => {
      const b = cv.getBoundingClientRect();
      return [e.clientX - b.left, e.clientY - b.top] as const;
    };
    const move = (e: PointerEvent) => {
      if (touchMove(e)) return;
      const [sx, sy] = local(e);
      if (this.drag) {
        this.moved();
        if (this.drag.node) {
          const [wx, wy] = this.toWorld(sx, sy);
          this.drag.node.fx = wx; this.drag.node.fy = wy;
          this.sim.alpha(Math.max(this.sim.alpha(), 0.2));
        } else {
          this.target.x = this.drag.cx - (sx - this.drag.x) / this.cam.k;
          this.target.y = this.drag.cy - (sy - this.drag.y) / this.cam.k;
          this.cam.x = this.target.x; this.cam.y = this.target.y;
        }
        return;
      }
      // The You node sits over the wall: hovering it shows what waits for the person (the view's need-you list, B088);
      // the view opens Needs you on its click.
      this.hoverYou = this.hitYouNode(sx, sy);
      if (this.hoverYou) {
        this.hover = null; this.hoverMap = null; this.hoverCommit = null;
        cv.style.cursor = 'pointer';
        const you = this.youNode();
        this.onHover({ key: 'you', kind: 'you', team: '', name: 'You', x: you.x, y: you.y });
        return;
      }
      const commit = this.commitAt(sx, sy);
      this.hoverCommit = commit?.e ?? null;
      if (commit) {
        const e = commit.e;
        this.hover = null;
        cv.style.cursor = 'default';
        const [cx, cy] = this.toScreen(commit.x, commit.y);
        this.onHover({ key: teamKey(e.team), kind: 'commit', team: e.team, name: e.member ?? e.team, x: cx, y: cy - 8, message: e.message, sha: shortSha(e.sha), touches: e.files.length });
        return;
      }
      const mapNode = this.mapAt(sx, sy);
      this.hoverMap = mapNode;
      if (mapNode) {
        this.hover = mapNode.team;
        cv.style.cursor = 'default';
        const [fx, fy] = this.toScreen(mapNode.x, mapNode.y);
        this.onHover(this.mapHover(mapNode, fx, fy));
        return;
      }
      const n = this.nodeAt(sx, sy);
      this.hover = n;
      cv.style.cursor = n ? 'pointer' : 'grab';
      if (!n) { this.onHover(null); return; }
      const [hx, hy] = this.toScreen(n.x!, n.y!);
      if (n.kind === 'wf') { this.onHover({ key: n.key, kind: 'workflow', team: n.team, name: n.team, workflow: n.wf, x: hx, y: hy - n.r * this.cam.k }); return; }
      this.onHover({ key: n.key, kind: n.kind, team: n.team, name: n.name, state: n.state, statusLine: n.statusLine, queued: n.queued, x: hx, y: hy - n.r * this.cam.k });
    };
    const down = (e: PointerEvent) => {
      if (touchDown(e)) return;
      const [sx, sy] = local(e);
      // On the You node: no pick (the view sees a plain click there and opens Needs you); a drag from it pans.
      if (this.hitYouNode(sx, sy)) {
        cv.setPointerCapture(e.pointerId);
        this.drag = { node: null, x: sx, y: sy, cx: this.target.x, cy: this.target.y };
        return;
      }
      // A map node picks the member that touched it last (the team when nobody is known).
      const file = this.mapAt(sx, sy);
      if (file) {
        const last = [...(file.file?.members.values() ?? [])].sort((a, b) => b.at - a.at)[0];
        const key = last ? memberKey(file.team.team, last.member) : file.team.key;
        this.picked = key; this.onPick(key);
      }
      const n = file ? null : this.nodeAt(sx, sy);
      cv.setPointerCapture(e.pointerId);
      this.drag = { node: n, x: sx, y: sy, cx: this.target.x, cy: this.target.y };
      // A workflow node opens on the click's release (a drag only moves it); it does not take the pick.
      if (n && n.kind !== 'wf') { this.picked = n.key; this.onPick(n.key); }
    };
    const up = (e: PointerEvent) => {
      if (touchUp(e)) return;
      const node = this.drag?.node;
      if (node?.kind === 'wf') {
        const [sx, sy] = local(e);
        if (Math.hypot(sx - this.drag!.x, sy - this.drag!.y) < 5) this.onOpenWorkflow(node.team, node.wf?.id);
      }
      if (this.drag?.node) { this.drag.node.fx = null; this.drag.node.fy = null; }
      this.drag = null;
    };
    const leave = () => { this.hover = null; this.hoverMap = null; this.hoverCommit = null; this.hoverYou = false; this.onHover(null); };
    // Touch (an iPad, a touch screen): two fingers pinch to zoom and move together to pan, the point between them staying
    // under them; a double tap fits everything. One finger drags as the mouse does.
    const touches = new Map<number, readonly [number, number]>();
    let pinch: { dist: number; k: number; wx: number; wy: number } | null = null;
    let lastTap = { at: -Infinity, x: 0, y: 0 };
    const startPinch = () => {
      const [a, b] = [...touches.values()];
      const [mx, my] = [(a[0] + b[0]) / 2, (a[1] + b[1]) / 2];
      const [wx, wy] = this.toWorld(mx, my);
      pinch = { dist: Math.max(1, Math.hypot(a[0] - b[0], a[1] - b[1])), k: this.cam.k, wx, wy };
      if (this.drag?.node) { this.drag.node.fx = null; this.drag.node.fy = null; }
      this.drag = null;
    };
    const touchDown = (e: PointerEvent) => {
      if (e.pointerType !== 'touch') return false;
      touches.set(e.pointerId, local(e));
      if (touches.size === 2) { cv.setPointerCapture(e.pointerId); startPinch(); return true; }
      return touches.size > 2;
    };
    const touchMove = (e: PointerEvent) => {
      if (e.pointerType !== 'touch' || !touches.has(e.pointerId)) return false;
      touches.set(e.pointerId, local(e));
      if (!pinch || touches.size < 2) return false;
      const [a, b] = [...touches.values()];
      const cam = pinchCamera(pinch, Math.hypot(a[0] - b[0], a[1] - b[1]), [(a[0] + b[0]) / 2, (a[1] + b[1]) / 2], this.w, this.h);
      this.cam.k = this.target.k = cam.k;
      this.cam.x = this.target.x = cam.x;
      this.cam.y = this.target.y = cam.y;
      this.moved();
      return true;
    };
    const touchUp = (e: PointerEvent) => {
      if (e.pointerType !== 'touch') return false;
      const at = touches.get(e.pointerId);
      touches.delete(e.pointerId);
      if (pinch) {
        // Down to one finger: it pans from where it is; no fingers: the pinch is over.
        if (touches.size === 1) {
          const [[sx, sy]] = [...touches.values()];
          this.drag = { node: null, x: sx, y: sy, cx: this.target.x, cy: this.target.y };
        }
        if (touches.size < 2) pinch = null;
        return true;
      }
      if (at && e.type === 'pointerup') {
        const now = performance.now();
        if (now - lastTap.at < 320 && Math.hypot(at[0] - lastTap.x, at[1] - lastTap.y) < 30) { lastTap.at = -Infinity; this.fit(); }
        else lastTap = { at: now, x: at[0], y: at[1] };
      }
      return false;
    };
    const wheel = (e: WheelEvent) => {
      e.preventDefault();
      const [sx, sy] = local(e);
      const [wx, wy] = this.toWorld(sx, sy);
      const k = Math.min(MaxZoom, Math.max(MinZoom, this.cam.k * Math.exp(-e.deltaY * 0.0015)));
      this.cam.k = this.target.k = k;
      this.cam.x = this.target.x = wx - (sx - this.w / 2) / k;
      this.cam.y = this.target.y = wy - (sy - this.h / 2) / k;
      this.moved();
    };
    const dbl = () => this.fit();
    cv.addEventListener('pointermove', move);
    cv.addEventListener('pointerdown', down);
    cv.addEventListener('pointerup', up);
    cv.addEventListener('pointercancel', up);
    cv.addEventListener('pointerleave', leave);
    cv.addEventListener('wheel', wheel, { passive: false });
    cv.addEventListener('dblclick', dbl);
    this.unlisten = () => {
      cv.removeEventListener('pointermove', move);
      cv.removeEventListener('pointerdown', down);
      cv.removeEventListener('pointerup', up);
      cv.removeEventListener('pointercancel', up);
      cv.removeEventListener('pointerleave', leave);
      cv.removeEventListener('wheel', wheel);
      cv.removeEventListener('dblclick', dbl);
    };
  }
}

function roundRect(c: CanvasRenderingContext2D, x: number, y: number, w: number, h: number, r: number): void {
  c.beginPath();
  c.moveTo(x + r, y);
  c.arcTo(x + w, y, x + w, y + h, r);
  c.arcTo(x + w, y + h, x, y + h, r);
  c.arcTo(x, y + h, x, y, r);
  c.arcTo(x, y, x + w, y, r);
  c.closePath();
}

export { Palette };
