import { forceCollide, forceLink, forceManyBody, forceSimulation, forceX, forceY, type Simulation, type SimulationNodeDatum } from 'd3-force';
import { avatarSheet, initials, loadAvatarSheet, memberAvatar, tileSource, type MemberState } from '../faces/faces';
import { DoingMs, memberKey, teamKey, spaced, type Effect, type MonitorModel, type TeamSnapshot, type Tone } from './model';
import type { WorkflowSummary } from '../../../shared/api';
import { drawStepIcon, drawWorkflowIcon } from './icons';
import { fileAlpha, FilePulseMs, StepLook, type FileMark } from './steps';
import { entryTime, panelHit, panelLayout, panelRadius, panelScale, PanelFontPx, workflowLabel, workflowLook, type PanelLayout } from './workflows';

/**
 * THE MONITOR'S SCENE: one canvas on a dark wall. Teams are territories (a glow in the team's hue with its name),
 * members orbit their team on spokes, each a disc with its face and the ring that says its state:
 *   running  - three arcs of 75 degrees turning, a turn in 3.3 s;
 *   waiting  - a dashed ring creeping round;
 *   blocked, needs you, failed - a ring that throbs every 1.4 s, wider and brighter;
 *   idle     - a thin line.
 * The Manager wears an outer ring with three ticks; queued instructions orbit a member as small dots.
 * Effects (comets, bursts, ripples, floating lines) come from the model. So do the members' steps: each step's icon
 * rises off its member and fades (newest lowest, at most StepIconsEach at once), the files a member changes sit
 * beside it as small labelled nodes on lines, fanned away from its team and pulsing when touched again, until half a
 * minute after their last touch, and a line under the member's name says what it is doing now.
 * A team's newest workflows (live only) sit in one panel hung off its hub on a thin dashed line, past its members:
 * a header, then a row per workflow, last updated first: its mark in its state's colour (check, play in a turning
 * ring, pause, cross), "#id title" and the time of its last entry. The panel is one body in the layout, sized to its
 * rows, and drawn larger as the wall zooms out so its words stay readable; clicking it opens the team at its workflows.
 * The layout is a d3-force simulation
 * ticked once per frame; the camera fits everything unless the person has moved it in the last 12 s.
 * Under reduced motion nothing turns or throbs and the graph settles.
 */

interface Node extends SimulationNodeDatum {
  key: string; kind: 'team' | 'member' | 'workflows'; team: string; name: string; r: number; hue: number;
  role?: 'manager' | 'member'; state?: MemberState; queued?: number; statusLine?: string | null; homeX: number; homeY: number;
  /** A workflow panel's workflows, last updated first. */
  wfs?: WorkflowSummary[];
}
interface Link { source: Node; target: Node }

export interface SceneHover {
  key: string; kind: 'team' | 'member' | 'file' | 'workflow'; team: string; name: string; state?: MemberState; statusLine?: string | null; queued?: number; x: number; y: number;
  /** A file node's: its path, how often it was touched, how it was last touched, and the files a "+N" node counts. */
  path?: string; touches?: number; fileKind?: 'edit' | 'create'; more?: string[];
  /** The workflow row hovered on a team's workflow panel (none on its header). */
  workflow?: WorkflowSummary;
}

/** A file node as drawn this frame: where it is and how strongly it shows. `mark` is null for a "+N" node. */
interface ShownFile { id: string; member: Node; mark: FileMark | null; counted: FileMark[]; x: number; y: number; alpha: number; color: string; pulse: number }

const Palette = {
  ground: '#070b14', ink: '#e2e8f0', inkDim: '#94a3b8',
  tell: '#38bdf8', start: '#22d3ee', progress: '#a5b4fc', done: '#34d399', handback: '#fbbf24', fail: '#f43f5e',
  blocked: '#fb923c', decision: '#c4b5fd', person: '#f9a8d4', edit: '#a3e635',
} satisfies Record<Tone | 'ground' | 'ink' | 'inkDim', string>;

const Life = { comet: 1100, burst: 1400, ripple: 1100, float: 2600, step: 2600 } as const;
/** At most this many step icons on one member at once: a burst of steps keeps the newest. */
const StepIconsEach = 3;
const StepIconGap = 20;
/** Where a member's file nodes sit: the angle off its outward direction and the distance past its disc, by place. */
const FileFan = [{ a: 0, d: 60 }, { a: -0.62, d: 48 }, { a: 0.62, d: 48 }, { a: -1.2, d: 64 }, { a: 1.2, d: 64 }, { a: -1.75, d: 50 }];
const MoreFan = { a: 1.75, d: 50 };
const RefitAfterMs = 12_000;
/** How long a one-off Fit (pinned) keeps aiming at everything while the camera eases there. */
export const FitOnceMs = 1_500;

/**
 * Whether the camera aims at everything at `now`. Auto fit: unless the person moved the wall in the last 12 s. Pinned:
 * only during a one-off Fit (until `fitUntil`).
 */
export function aimsAtEverything(pinned: boolean, now: number, movedAt: number, fitUntil: number): boolean {
  return pinned ? now < fitUntil : now - movedAt > RefitAfterMs;
}
/** How much further from its hub than the members a workflow panel's edge sits. */
const WorkflowPast = 50;

/** The wall's state colours: the app's --state-* lifted toward white, read once. */
function stateColors(): Record<MemberState, string> {
  const css = getComputedStyle(document.documentElement);
  const pick = (s: MemberState, fallback: string) => lift(css.getPropertyValue(`--state-${s}`).trim() || fallback);
  return {
    running: pick('running', '#2e7d4f'), waiting: pick('waiting', '#5f7385'), decision: pick('decision', '#7a4fc4'),
    blocked: pick('blocked', '#b26a00'), failed: pick('failed', '#c62828'), idle: pick('idle', '#8a847c'),
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
  /** The hub-to-workflow lines (a force of their own: longer and looser than the members' spokes). */
  private wfLinks: Link[] = [];
  private teamList: TeamSnapshot[] = [];
  /** Which model's workflows, at which rev, the nodes were last built from. */
  private wfFrom: { model: MonitorModel | null; rev: number } = { model: null, rev: -1 };
  /** How much larger than its world size the workflow panels are drawn (panelScale of the zoom, changed in steps). */
  private panelK = 1;
  /** What of a workflow panel is under the pointer. */
  private hoverPanel: { key: string; hit: 'header' | 'panel' | number } | null = null;
  private sim: Simulation<Node, undefined>;
  private cam = { x: 0, y: 0, k: 1 };
  private target = { x: 0, y: 0, k: 1 };
  private movedAt = -Infinity;
  /** Pinned: the camera never fits on its own; Fit (or a double-click) fits once. */
  private pinned = false;
  private fitUntil = -Infinity;
  private raf = 0;
  private last = 0;
  private dpr = 1;
  private w = 0;
  private h = 0;
  private hover: Node | null = null;
  private picked: string | null = null;
  private drag: { node: Node | null; x: number; y: number; cx: number; cy: number; hit?: 'header' | 'panel' | number } | null = null;
  private colors = stateColors();
  private dt = 16;
  /** Where each file node is drawn (eased toward its place), and the ones drawn this frame, for the hover. */
  private filePos = new Map<string, { x: number; y: number }>();
  private shownFiles: ShownFile[] = [];
  private hoverFile: ShownFile | null = null;
  /** Each step icon's place in its member's stack, eased so the stack slides up rather than jumps. */
  private stepSlots = new WeakMap<Effect, number>();
  readonly reduced: boolean;
  effects: Effect[] = [];
  /** The model whose files and "now" lines are drawn (the live one, or a replay's). */
  model: MonitorModel | null = null;
  /** In a replay, the members' states as they were (the snapshots say how they are now); null live. */
  stateOf: ((key: string) => MemberState) | null = null;
  onHover: (h: SceneHover | null) => void = () => {};
  onPick: (key: string | null) => void = () => {};
  /** A team's workflow panel was clicked (not dragged): its header, or a row (with that workflow's id). */
  onOpenWorkflow: (team: string, id?: number) => void = () => {};

  constructor(private canvas: HTMLCanvasElement) {
    this.ctx = canvas.getContext('2d')!;
    this.reduced = typeof matchMedia === 'function' && matchMedia('(prefers-reduced-motion: reduce)').matches;
    this.sim = forceSimulation<Node>([])
      .alphaDecay(0.02).velocityDecay(0.32).alphaTarget(this.reduced ? 0 : 0.012)
      .force('charge', forceManyBody<Node>().strength((n) => (n.kind === 'team' ? -600 : n.kind === 'workflows' ? -320 : -280)))
      .force('link', forceLink<Node, Link>([]).distance((l) => this.spoke(l.source.team)).strength(0.55))
      .force('wflink', forceLink<Node, Link>([]).distance((l) => this.spoke(l.source.team) + WorkflowPast + l.target.r * 0.75).strength(0.35))
      .force('collide', forceCollide<Node>().radius((n) => n.r + (n.kind === 'team' ? 30 : n.kind === 'workflows' ? 12 : 24)))
      .force('x', forceX<Node>((n) => n.homeX).strength((n) => (n.kind === 'team' ? 0.12 : 0.02)))
      .force('y', forceY<Node>((n) => n.homeY).strength((n) => (n.kind === 'team' ? 0.12 : 0.02)))
      .stop();
    void loadAvatarSheet();
    this.listen();
    this.raf = requestAnimationFrame(this.frame);
  }

  destroy(): void {
    cancelAnimationFrame(this.raf);
    this.sim.stop();
    this.unlisten?.();
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
  private rebuild(): void {
    const teams = this.teamList;
    const workflows = this.model?.workflows;
    this.wfFrom = { model: this.model, rev: this.model?.workflowsRev ?? -1 };
    const keep = new Set<string>();
    const count = teams.length;
    const rx = Math.max(140, 150 + count * 34);
    const ry = rx * 0.62;
    teams.forEach((t, i) => {
      const angle = count === 1 ? 0 : (i / count) * Math.PI * 2 - Math.PI / 2;
      const homeX = count === 1 ? 0 : Math.cos(angle) * rx;
      const homeY = count === 1 ? 0 : Math.sin(angle) * ry;
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
      const wfs = workflows?.get(tk) ?? [];
      if (wfs.length) {
        const wk = `w:${t.name.toLowerCase()}`;
        keep.add(wk);
        // Born outward from the middle of the wall, past the members.
        const out = count === 1 ? -Math.PI / 4 : angle;
        this.upsert(wk, { kind: 'workflows', team: t.name, name: t.name, r: panelRadius(panelLayout(wfs.length, this.panelK)), hue, homeX, homeY, wfs },
          (team.x ?? homeX) + Math.cos(out) * 260, (team.y ?? homeY) + Math.sin(out) * 260);
      }
    });
    for (const k of [...this.nodes.keys()]) if (!keep.has(k)) this.nodes.delete(k);
    const spokeTo = (kind: Node['kind']) => [...this.nodes.values()].filter((n) => n.kind === kind).map((n) => ({ source: this.nodes.get(teamKey(n.team))!, target: n }));
    this.links = spokeTo('member');
    this.wfLinks = spokeTo('workflows');
    this.sim.nodes([...this.nodes.values()]);
    (this.sim.force('link') as ReturnType<typeof forceLink<Node, Link>>).links(this.links);
    (this.sim.force('wflink') as ReturnType<typeof forceLink<Node, Link>>).links(this.wfLinks);
    this.sim.alpha(Math.max(this.sim.alpha(), 0.3));
  }

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
  private frame = (now: number) => {
    this.raf = requestAnimationFrame(this.frame);
    const dt = this.last ? Math.min(100, now - this.last) : 16;
    this.last = now;
    this.dt = dt;
    this.model?.tidy(now);
    if (this.wfFrom.model !== this.model || this.wfFrom.rev !== (this.model?.workflowsRev ?? -1)) this.rebuild();
    this.rescalePanels();
    this.resize();
    if (this.sim.alpha() > 0.001 || !this.reduced) this.sim.tick();
    if (this.fitting(now)) this.aimFit();
    const ease = 1 - Math.pow(0.0025, dt / 1000);
    this.cam.x += (this.target.x - this.cam.x) * ease;
    this.cam.y += (this.target.y - this.cam.y) * ease;
    this.cam.k += (this.target.k - this.cam.k) * ease;
    this.effects = this.effects.filter((e) => now - e.at < Life[e.kind]);
    this.draw(now);
  };

  /** Follows the zoom with the workflow panels' size, in steps (each step re-sizes their bodies in the layout). */
  private rescalePanels(): void {
    const k = panelScale(this.cam.k);
    if (Math.abs(k - this.panelK) / this.panelK < 0.08) return;
    this.panelK = k;
    let any = false;
    for (const n of this.nodes.values()) if (n.kind === 'workflows') { n.r = panelRadius(panelLayout(n.wfs?.length ?? 0, k)); any = true; }
    if (!any) return;
    const collide = this.sim.force('collide') as ReturnType<typeof forceCollide<Node>>;
    collide.radius(collide.radius()); // re-reads the radii
    (this.sim.force('wflink') as ReturnType<typeof forceLink<Node, Link>>).links(this.wfLinks);
    this.sim.alpha(Math.max(this.sim.alpha(), 0.15));
  }

  private panelOf(n: Node): PanelLayout { return panelLayout(n.wfs?.length ?? 0, this.panelK); }

  private resize(): void {
    const dpr = window.devicePixelRatio || 1;
    const w = this.canvas.clientWidth, h = this.canvas.clientHeight;
    if (w === this.w && h === this.h && dpr === this.dpr) return;
    this.w = w; this.h = h; this.dpr = dpr;
    this.canvas.width = Math.max(1, Math.round(w * dpr));
    this.canvas.height = Math.max(1, Math.round(h * dpr));
  }

  private aimFit(): void {
    const ns = [...this.nodes.values()];
    if (!ns.length || !this.w) return;
    let x0 = Infinity, y0 = Infinity, x1 = -Infinity, y1 = -Infinity;
    for (const n of ns) {
      // A member's pad leaves room for the files fanned beside it.
      const pad = n.r + (n.kind === 'team' ? 70 : n.kind === 'workflows' ? 10 : 64);
      x0 = Math.min(x0, n.x! - pad); x1 = Math.max(x1, n.x! + pad);
      y0 = Math.min(y0, n.y! - pad); y1 = Math.max(y1, n.y! + pad);
    }
    const top = 96, bottom = 40; // room for the counts at the top and the legend at the bottom
    const k = Math.min(1.6, Math.max(0.2, Math.min(this.w / (x1 - x0), (this.h - top - bottom) / (y1 - y0))));
    this.target = { x: (x0 + x1) / 2, y: (y0 + y1) / 2 - (top - bottom) / 2 / k, k };
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
    const dim = (key: string) => (near && !near.has(key) ? 0.28 : 1);

    for (const n of this.nodes.values()) if (n.kind === 'team') this.drawTerritory(c, n, now, motion, dim(n.key));
    for (const l of this.links) {
      c.globalAlpha = 0.35 * Math.min(dim(l.source.key), dim(l.target.key));
      c.strokeStyle = `hsl(${l.source.hue} 70% 60%)`;
      c.lineWidth = 1.2;
      c.beginPath(); c.moveTo(l.source.x!, l.source.y!); c.lineTo(l.target.x!, l.target.y!); c.stroke();
    }
    // The workflows' lines: thinner, dashed and fainter than the members' spokes.
    c.setLineDash([3, 4]);
    c.lineWidth = 0.8;
    for (const l of this.wfLinks) {
      c.globalAlpha = 0.3 * Math.min(dim(l.source.key), dim(l.target.key));
      c.strokeStyle = `hsl(${l.source.hue} 50% 62%)`;
      c.beginPath(); c.moveTo(l.source.x!, l.source.y!); c.lineTo(l.target.x!, l.target.y!); c.stroke();
    }
    c.setLineDash([]);
    c.globalAlpha = 1;
    this.placeFiles(now);
    this.drawFileLines(c, motion, dim);
    c.globalCompositeOperation = 'lighter';
    for (const e of this.effects) this.drawEffect(c, e, now, motion);
    c.globalCompositeOperation = 'source-over';
    for (const n of this.nodes.values()) if (n.kind === 'member') this.drawMember(c, n, now, motion, dim(n.key));
    for (const n of this.nodes.values()) if (n.kind === 'workflows') this.drawPanel(c, n, now, motion, dim(n.key));
    this.drawFileNodes(c, motion, dim);
    for (const n of this.nodes.values()) if (n.kind === 'team') this.drawTeamPill(c, n, dim(n.key));
    const icons = this.drawSteps(c, now, motion, dim);
    // Floating lines on the same member stack, newest lowest, so two never overlap, above its step icons.
    const lanes = new Map<string, number>();
    for (let i = this.effects.length - 1; i >= 0; i--) {
      const e = this.effects[i]!;
      if (e.kind !== 'float') continue;
      const lane = lanes.get(e.node) ?? 0;
      lanes.set(e.node, lane + 1);
      this.drawFloat(c, e, now, motion, lane, (icons.get(e.node) ?? 0) * StepIconGap);
    }
  }

  // ---------- the members' files ----------

  /** The direction from a member's team out through the member, kept off straight up (its step icons) and down (its name). */
  private outward(member: Node): number {
    const team = this.nodes.get(teamKey(member.team));
    const dx = team ? member.x! - team.x! : 0, dy = team ? member.y! - team.y! : -1;
    let a = Math.hypot(dx, dy) < 1 ? 0 : Math.atan2(dy, dx);
    const up = -Math.PI / 2, down = Math.PI / 2, keep = 0.95;
    const off = (from: number) => Math.atan2(Math.sin(a - from), Math.cos(a - from));
    if (Math.abs(off(up)) < keep) a = up + Math.sign(off(up) || 1) * keep;
    if (Math.abs(off(down)) < keep) a = down + Math.sign(off(down) || 1) * keep;
    return a;
  }

  /** Where each member's files are this frame: a place on the fan for each placed file, one "+N" node for the rest. */
  private placeFiles(now: number): void {
    const shown: ShownFile[] = [];
    const ease = this.reduced ? 1 : 1 - Math.pow(0.0004, this.dt / 1000);
    const alive = new Set<string>();
    const place = (id: string, member: Node, spot: { a: number; d: number }, base: number) => {
      const a = base + spot.a, d = member.r + spot.d;
      const tx = member.x! + Math.cos(a) * d, ty = member.y! + Math.sin(a) * d;
      let p = this.filePos.get(id);
      if (!p) this.filePos.set(id, (p = { x: member.x!, y: member.y! }));
      p.x += (tx - p.x) * ease; p.y += (ty - p.y) * ease;
      alive.add(id);
      return p;
    };
    const files = this.model?.files;
    if (files) {
      for (const key of files.members()) {
        const member = this.nodes.get(key);
        if (!member) continue;
        const base = this.outward(member);
        const { placed, counted } = files.of(key);
        for (const f of placed) {
          const id = `${key}|${f.path}`;
          const p = place(id, member, FileFan[f.slot] ?? FileFan[0]!, base);
          shown.push({ id, member, mark: f, counted: [], x: p.x, y: p.y, alpha: fileAlpha(f, now, this.reduced), color: f.kind === 'create' ? StepLook.create.color : StepLook.edit.color, pulse: (now - f.last) / FilePulseMs });
        }
        if (counted.length) {
          const id = `${key}|+`;
          const p = place(id, member, MoreFan, base);
          const last = Math.max(...counted.map((f) => f.last));
          const first = Math.min(...counted.map((f) => f.first));
          shown.push({ id, member, mark: null, counted, x: p.x, y: p.y, alpha: fileAlpha({ first, last }, now, this.reduced), color: Palette.inkDim, pulse: (now - last) / FilePulseMs });
        }
      }
    }
    for (const id of [...this.filePos.keys()]) if (!alive.has(id)) this.filePos.delete(id);
    this.shownFiles = shown;
  }

  /** The lines from each member to its files, and on a fresh touch a dot that runs out along the line. */
  private drawFileLines(c: CanvasRenderingContext2D, motion: number, dim: (k: string) => number): void {
    for (const f of this.shownFiles) {
      const m = f.member;
      const a = f.alpha * dim(m.key);
      if (a <= 0.01) continue;
      const ang = Math.atan2(f.y - m.y!, f.x - m.x!);
      const sx = m.x! + Math.cos(ang) * (m.r + 6), sy = m.y! + Math.sin(ang) * (m.r + 6);
      c.strokeStyle = f.color;
      c.globalAlpha = a * (f.pulse < 1 ? 0.75 : 0.4);
      c.lineWidth = f.pulse < 1 ? 1.6 : 1;
      if (!f.mark) c.setLineDash([2, 3]);
      c.beginPath(); c.moveTo(sx, sy); c.lineTo(f.x, f.y); c.stroke();
      c.setLineDash([]);
      if (motion && f.pulse < 0.5) {
        const u = f.pulse / 0.5;
        c.fillStyle = f.color;
        c.globalAlpha = a * (1 - u * 0.5);
        c.beginPath(); c.arc(sx + (f.x - sx) * u, sy + (f.y - sy) * u, 2.4, 0, Math.PI * 2); c.fill();
      }
    }
    c.globalAlpha = 1;
  }

  /** The file nodes: a dot in the touch's colour (a ring round it for a new file), its name beside it, a ring on a touch. */
  private drawFileNodes(c: CanvasRenderingContext2D, motion: number, dim: (k: string) => number): void {
    const labels = this.cam.k > 0.45;
    c.font = `500 10px ${this.family()}`;
    c.textBaseline = 'middle';
    for (const f of this.shownFiles) {
      const a = f.alpha * dim(f.member.key);
      if (a <= 0.01) continue;
      const hovered = this.hoverFile?.id === f.id;
      if (f.pulse < 1) {
        const t = f.pulse;
        c.strokeStyle = f.color;
        c.globalAlpha = a * (1 - t) * 0.9;
        c.lineWidth = 1.5;
        c.beginPath(); c.arc(f.x, f.y, 5 + (motion ? t * 12 : 4), 0, Math.PI * 2); c.stroke();
      }
      c.globalAlpha = a;
      const r = f.mark ? 4.2 : 7;
      c.fillStyle = Palette.ground;
      c.beginPath(); c.arc(f.x, f.y, r + 1.5, 0, Math.PI * 2); c.fill();
      if (f.mark) {
        c.fillStyle = f.color;
        c.beginPath(); c.arc(f.x, f.y, f.mark.kind === 'create' ? 2.6 : r, 0, Math.PI * 2); c.fill();
        if (f.mark.kind === 'create') { c.strokeStyle = f.color; c.lineWidth = 1.3; c.beginPath(); c.arc(f.x, f.y, r, 0, Math.PI * 2); c.stroke(); }
      } else {
        c.strokeStyle = f.color; c.lineWidth = 1.2; c.setLineDash([2, 2]);
        c.beginPath(); c.arc(f.x, f.y, r, 0, Math.PI * 2); c.stroke();
        c.setLineDash([]);
      }
      if (hovered) { c.strokeStyle = Palette.ink; c.lineWidth = 1; c.beginPath(); c.arc(f.x, f.y, r + 3.5, 0, Math.PI * 2); c.stroke(); }
      if (!labels && !hovered) continue;
      const right = f.x >= f.member.x!;
      // Two files of one name beside a member (two README.md) say their folder too.
      const name = f.mark && this.shownFiles.some((o) => o !== f && o.member === f.member && o.mark?.name === f.mark!.name) ? f.mark.path.split('/').slice(-2).join('/') : f.mark?.name;
      const text = f.mark ? name + (f.mark.touches > 1 ? ` ×${f.mark.touches}` : '') : `+${f.counted.length} file${f.counted.length === 1 ? '' : 's'}`;
      const label = text.length > 28 ? text.slice(0, 27) + '…' : text;
      c.textAlign = right ? 'left' : 'right';
      const lx = f.x + (right ? r + 5 : -r - 5);
      c.lineWidth = 3; c.strokeStyle = 'rgba(7, 11, 20, 0.85)'; c.lineJoin = 'round';
      c.strokeText(label, lx, f.y + 0.5);
      c.fillStyle = f.pulse < 1 ? Palette.ink : f.mark ? 'rgba(226, 232, 240, 0.82)' : Palette.inkDim;
      c.fillText(label, lx, f.y + 0.5);
    }
    c.globalAlpha = 1;
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
    for (let i = this.effects.length - 1; i >= 0; i--) {
      const e = this.effects[i]!;
      if (e.kind !== 'step') continue;
      const lane = count.get(e.node) ?? 0;
      if (lane >= StepIconsEach) continue;
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
    const font = 13;
    c.font = `700 ${font}px ${this.family()}`;
    const text = n.name;
    const w = c.measureText(text).width + 18;
    const x = n.x! - w / 2, y = n.y! - 34;
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
    // Body: a disc in the team's hue that glows in the state's colour.
    c.save();
    c.shadowColor = color;
    c.shadowBlur = state === 'idle' ? 0 : 16;
    const body = c.createRadialGradient(x - r * 0.3, y - r * 0.3, 0, x, y, r);
    body.addColorStop(0, `hsl(${n.hue} 60% ${state === 'idle' ? 38 : 52}%)`);
    body.addColorStop(1, `hsl(${n.hue} 60% ${state === 'idle' ? 16 : 22}%)`);
    c.fillStyle = body;
    c.beginPath(); c.arc(x, y, r, 0, Math.PI * 2); c.fill();
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
   * A team's workflow panel: a header, then a row per workflow (last updated first) with its mark in its state's
   * colour (a turning ring round it while it is worked), "#id title" cut to fit, and the time of its last entry.
   */
  private drawPanel(c: CanvasRenderingContext2D, n: Node, now: number, motion: number, alpha: number): void {
    const wfs = n.wfs ?? [];
    const l = this.panelOf(n);
    const k = this.panelK;
    const x0 = n.x! - l.w / 2, y0 = n.y! - l.h / 2;
    const hover = this.hoverPanel?.key === n.key ? this.hoverPanel.hit : null;
    const family = this.family();
    c.globalAlpha = alpha;
    c.fillStyle = 'rgba(12, 19, 34, 0.92)';
    c.strokeStyle = hover !== null ? 'rgba(226, 232, 240, 0.7)' : `hsla(${n.hue}, 70%, 60%, 0.45)`;
    c.lineWidth = Math.max(1, k);
    roundRect(c, x0, y0, l.w, l.h, 8 * k); c.fill(); c.stroke();
    // The header.
    c.textBaseline = 'middle';
    c.textAlign = 'left';
    c.font = `700 ${9 * k}px ${family}`;
    c.fillStyle = hover === 'header' ? Palette.ink : Palette.inkDim;
    const hy = y0 + l.pad + l.headerH / 2 - 1 * k;
    c.fillText('WORKFLOWS', x0 + l.pad, hy);
    c.textAlign = 'right';
    c.font = `500 ${9 * k}px ${family}`;
    c.fillText(hover === 'header' ? 'open ›' : 'last updated', x0 + l.w - l.pad, hy);
    c.strokeStyle = 'rgba(148, 163, 184, 0.18)';
    c.lineWidth = Math.max(0.6, 0.8 * k);
    c.beginPath(); c.moveTo(x0 + l.pad, y0 + l.pad + l.headerH - 2 * k); c.lineTo(x0 + l.w - l.pad, y0 + l.pad + l.headerH - 2 * k); c.stroke();
    // The rows.
    wfs.forEach((w, i) => {
      const look = workflowLook(w);
      const color = Palette[look.tone];
      const ry = y0 + l.pad + l.headerH + i * l.rowH, cy = ry + l.rowH / 2;
      if (hover === i) {
        c.globalAlpha = alpha;
        c.fillStyle = 'rgba(56, 189, 248, 0.10)';
        roundRect(c, x0 + 3 * k, ry, l.w - 6 * k, l.rowH, 4 * k); c.fill();
      }
      c.globalAlpha = alpha * (look.status === 'closed' ? 0.7 : 1);
      const ix = x0 + l.pad + 6 * k, ir = 5.5 * k;
      if (look.status === 'working') {
        c.strokeStyle = color;
        c.lineWidth = 1.2 * k;
        c.lineCap = 'round';
        if (motion) {
          const turn = now / 700;
          for (let j = 0; j < 2; j++) { const a = turn + j * Math.PI; c.beginPath(); c.arc(ix, cy, ir + 2.2 * k, a, a + Math.PI * 0.55); c.stroke(); }
        } else {
          c.globalAlpha = alpha * 0.5;
          c.beginPath(); c.arc(ix, cy, ir + 2.2 * k, 0, Math.PI * 2); c.stroke();
          c.globalAlpha = alpha;
        }
      }
      c.fillStyle = 'rgba(7, 11, 20, 0.95)';
      c.strokeStyle = color;
      c.lineWidth = 1.2 * k;
      c.beginPath(); c.arc(ix, cy, ir, 0, Math.PI * 2); c.fill(); c.stroke();
      c.fillStyle = color;
      drawWorkflowIcon(c, look.icon, ix, cy, 6.5 * k);
      // The time on the right, the label in the room left.
      c.textAlign = 'right';
      c.font = `500 ${9 * k}px ${family}`;
      const when = entryTime(w.lastAt);
      const tw = c.measureText(when).width;
      c.fillStyle = Palette.inkDim;
      c.fillText(when, x0 + l.w - l.pad, cy + 0.5 * k);
      c.textAlign = 'left';
      c.font = `500 ${PanelFontPx * k}px ${family}`;
      const lx = ix + ir + 6 * k;
      c.fillStyle = look.status === 'closed' ? Palette.inkDim : hover === i ? Palette.ink : 'rgba(226, 232, 240, 0.88)';
      c.fillText(fitText(c, workflowLabel(w, 60), x0 + l.w - l.pad - tw - 8 * k - lx), lx, cy + 0.5 * k);
    });
    c.globalAlpha = 1;
  }

  private drawEffect(c: CanvasRenderingContext2D, e: Effect, now: number, motion: number): void {
    // Steps and floating lines are drawn above the members (drawSteps, drawFloat).
    if (e.kind === 'step' || e.kind === 'float') return;
    const t = (now - e.at) / Life[e.kind];
    const color = Palette[e.color];
    if (e.kind === 'comet') {
      const a = this.nodes.get(e.from), b = this.nodes.get(e.to);
      if (!a || !b) return;
      const ease = 1 - Math.pow(1 - t, 3);
      const mx = (a.x! + b.x!) / 2 - (b.y! - a.y!) * 0.18, my = (a.y! + b.y!) / 2 + (b.x! - a.x!) * 0.18;
      const at = (u: number): [number, number] => [(1 - u) * (1 - u) * a.x! + 2 * (1 - u) * u * mx + u * u * b.x!, (1 - u) * (1 - u) * a.y! + 2 * (1 - u) * u * my + u * u * b.y!];
      for (let i = 0; i < 8; i++) {
        const u = Math.max(0, ease - i * 0.035);
        const [px, py] = at(u);
        c.fillStyle = color;
        c.globalAlpha = (1 - i / 8) * (1 - Math.max(0, t - 0.85) / 0.15);
        c.beginPath(); c.arc(px, py, 3.2 - i * 0.3, 0, Math.PI * 2); c.fill();
      }
      c.globalAlpha = 1;
      if (e.label && t < 0.9) {
        const [px, py] = at(ease);
        c.globalCompositeOperation = 'source-over';
        c.fillStyle = color;
        c.font = `500 10px ${this.family()}`;
        c.textAlign = 'center'; c.textBaseline = 'bottom';
        c.globalAlpha = Math.min(1, (0.9 - t) * 4);
        c.fillText(e.label, px, py - 7);
        c.globalAlpha = 1;
        c.globalCompositeOperation = 'lighter';
      }
    } else if (e.kind === 'burst' || e.kind === 'ripple') {
      const n = this.nodes.get(e.node);
      if (!n) return;
      const big = e.kind === 'burst' && e.big;
      const r = n.r + 6 + t * (big ? 46 : 30);
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
    for (const l of [...this.links, ...this.wfLinks]) {
      if (l.source.key === key) set.add(l.target.key);
      if (l.target.key === key) set.add(l.source.key);
    }
    return set;
  }

  // ---------- the person's hand: hover, pick, drag, pan, zoom, fit ----------
  private unlisten: (() => void) | null = null;

  private nodeAt(sx: number, sy: number): Node | null {
    const [x, y] = this.toWorld(sx, sy);
    let best: Node | null = null, bestD = Infinity;
    for (const n of this.nodes.values()) {
      const d = Math.hypot(n.x! - x, n.y! - y);
      if (n.kind === 'workflows') continue; // panels are hit as rectangles (panelAt)
      const reach = n.kind === 'team' ? 14 : n.r + 8;
      if (d < reach && d < bestD) { best = n; bestD = d; }
    }
    return best;
  }

  /** The workflow panel under a screen point, and what of it (its header, a row, the rest). */
  private panelAt(sx: number, sy: number): { node: Node; hit: 'header' | 'panel' | number } | null {
    const [x, y] = this.toWorld(sx, sy);
    for (const n of this.nodes.values()) {
      if (n.kind !== 'workflows') continue;
      const l = this.panelOf(n);
      const hit = panelHit(l, x - (n.x! - l.w / 2), y - (n.y! - l.h / 2));
      if (hit !== null) return { node: n, hit };
    }
    return null;
  }

  /** The file node under a screen point, if any (they are small, so the reach is a few pixels on screen). */
  private fileAt(sx: number, sy: number): ShownFile | null {
    const [x, y] = this.toWorld(sx, sy);
    const reach = 9 / this.cam.k;
    let best: ShownFile | null = null, bestD = Infinity;
    for (const f of this.shownFiles) {
      if (f.alpha < 0.05) continue;
      const d = Math.hypot(f.x - x, f.y - y);
      if (d < reach && d < bestD) { best = f; bestD = d; }
    }
    return best;
  }

  /** Forgets every file node's place (a replay starts, or live comes back). */
  clearFiles(): void { this.filePos.clear(); this.shownFiles = []; this.hoverFile = null; }

  private listen(): void {
    const cv = this.canvas;
    const local = (e: PointerEvent | WheelEvent | MouseEvent) => {
      const b = cv.getBoundingClientRect();
      return [e.clientX - b.left, e.clientY - b.top] as const;
    };
    const move = (e: PointerEvent) => {
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
      const file = this.fileAt(sx, sy);
      this.hoverFile = file;
      if (file) {
        this.hover = file.member;
        cv.style.cursor = 'pointer';
        const [fx, fy] = this.toScreen(file.x, file.y);
        const m = file.member;
        this.onHover({
          key: m.key, kind: 'file', team: m.team, name: m.name, x: fx, y: fy - 8,
          path: file.mark?.path, touches: file.mark?.touches, fileKind: file.mark?.kind,
          more: file.mark ? undefined : file.counted.map((f) => f.path),
        });
        return;
      }
      const panel = this.panelAt(sx, sy);
      this.hoverPanel = panel ? { key: panel.node.key, hit: panel.hit } : null;
      if (panel) {
        const p = panel.node, l = this.panelOf(p);
        this.hover = p;
        cv.style.cursor = 'pointer';
        const row = typeof panel.hit === 'number' ? panel.hit : null;
        const top = p.y! - l.h / 2 + (row === null ? 0 : l.pad + l.headerH + row * l.rowH);
        const [hx, hy] = this.toScreen(p.x!, top);
        this.onHover({ key: p.key, kind: 'workflow', team: p.team, name: p.team, workflow: row === null ? undefined : p.wfs?.[row], x: hx, y: hy });
        return;
      }
      const n = this.nodeAt(sx, sy);
      this.hover = n;
      cv.style.cursor = n ? 'pointer' : 'grab';
      if (!n) { this.onHover(null); return; }
      const [hx, hy] = this.toScreen(n.x!, n.y!);
      this.onHover({ key: n.key, kind: n.kind === 'workflows' ? 'workflow' : n.kind, team: n.team, name: n.name, state: n.state, statusLine: n.statusLine, queued: n.queued, x: hx, y: hy - n.r * this.cam.k });
    };
    const down = (e: PointerEvent) => {
      const [sx, sy] = local(e);
      const file = this.fileAt(sx, sy);
      if (file) { this.picked = file.member.key; this.onPick(file.member.key); }
      const panel = file ? null : this.panelAt(sx, sy);
      const n = file ? null : panel ? panel.node : this.nodeAt(sx, sy);
      cv.setPointerCapture(e.pointerId);
      this.drag = { node: n, x: sx, y: sy, cx: this.target.x, cy: this.target.y, hit: panel?.hit };
      // A workflow panel opens on the click's release (a drag only moves it); it does not take the pick.
      if (n && n.kind !== 'workflows') { this.picked = n.key; this.onPick(n.key); }
    };
    const up = (e: PointerEvent) => {
      const node = this.drag?.node;
      if (node?.kind === 'workflows') {
        const [sx, sy] = local(e);
        const hit = this.drag!.hit;
        if (Math.hypot(sx - this.drag!.x, sy - this.drag!.y) < 5) this.onOpenWorkflow(node.team, typeof hit === 'number' ? node.wfs?.[hit]?.id : undefined);
      }
      if (this.drag?.node) { this.drag.node.fx = null; this.drag.node.fy = null; }
      this.drag = null;
    };
    const leave = () => { this.hover = null; this.hoverFile = null; this.hoverPanel = null; this.onHover(null); };
    const wheel = (e: WheelEvent) => {
      e.preventDefault();
      const [sx, sy] = local(e);
      const [wx, wy] = this.toWorld(sx, sy);
      const k = Math.min(4, Math.max(0.15, this.cam.k * Math.exp(-e.deltaY * 0.0015)));
      this.cam.k = this.target.k = k;
      this.cam.x = this.target.x = wx - (sx - this.w / 2) / k;
      this.cam.y = this.target.y = wy - (sy - this.h / 2) / k;
      this.moved();
    };
    const dbl = () => this.fit();
    cv.addEventListener('pointermove', move);
    cv.addEventListener('pointerdown', down);
    cv.addEventListener('pointerup', up);
    cv.addEventListener('pointerleave', leave);
    cv.addEventListener('wheel', wheel, { passive: false });
    cv.addEventListener('dblclick', dbl);
    this.unlisten = () => {
      cv.removeEventListener('pointermove', move);
      cv.removeEventListener('pointerdown', down);
      cv.removeEventListener('pointerup', up);
      cv.removeEventListener('pointerleave', leave);
      cv.removeEventListener('wheel', wheel);
      cv.removeEventListener('dblclick', dbl);
    };
  }
}

/** A line cut with an ellipsis to fit `max` px in the current font. */
function fitText(c: CanvasRenderingContext2D, text: string, max: number): string {
  if (max <= 0) return '';
  if (c.measureText(text).width <= max) return text;
  let lo = 0, hi = text.length;
  while (lo < hi) {
    const mid = Math.ceil((lo + hi) / 2);
    if (c.measureText(text.slice(0, mid).trimEnd() + '…').width <= max) lo = mid; else hi = mid - 1;
  }
  return text.slice(0, lo).trimEnd() + '…';
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
