// The livelier Monitor (with its code map) end to end, in headless Chrome
// against the phone-remote fake core (remote.html, tests/e2e/phone). The fake serves monitor.messages, monitor.steps
// (with StepRow.exit when the test gives one), monitor.codeEvents and bus.workflows from a feed the test changes while
// the page polls, and patches teams.agents with the attention the test sets (a member that needs the person).
//
// The wall is a canvas, so the spec reads what the scene draws (the code-map spec's recorder): an init script wraps the
// monitor canvas's 2D context and keeps the last frames' arcs, paths and texts with their transform and alpha. What
// the scene draws (scene.ts, rhythm.ts):
//   a workflow node   - a rounded square (moveTo x-r+4, y-r; r 10) filled rgba(7, 11, 20, 0.95), stroked in its tone
//                       (working #22d3ee, waiting #fbbf24, blocked #fb923c, width 2.2), "#id" under it; dashed lines
//                       to the members working it (to its hub while nobody is); completed: a #34d399 flash ring and the
//                       square drifts out; closed: a #94a3b8 square with a cross, fading.
//   the rhythm        - a member's body (a gradient disc, r 14 or 17) grows up to 7% with its pulse; a command's ring
//                       is a #e879f9 arc at r + 12 from -pi/2, full and green / red when its exit is known, with a
//                       burst (8 or 14 sparks); thinking a glint rgba(226, 232, 240, 0.35) at r + 3.
//   the You node      - in screen space at (w - 46, max(120, h / 2)), r 24, "You", its count and "NEEDS YOU".
//   comets            - heads are #38bdf8 (tell) arcs of r 3.2, labels "#card", hand-backs "#card ✓" / "#card ✗".
//
//   npx playwright test monitor --workers=1      (E2E_SKIP_BUILD=1 reuses out/e2e/renderer)
// Screenshots and the results go to $YAWBLE_TEAM_DOCS/monitor (else test-results/monitor).
import { expect, test, type Browser, type Page } from './phone/fixtures';
import { existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { createFakeCore, type Access } from './phone/fake-core';
import { ORIGIN, buildRenderer, builtDir } from './phone/server';

test.use({ channel: 'chrome', headless: true, actionTimeout: 8_000 });
test.setTimeout(150_000);

const SHOTS = path.join(process.env.YAWBLE_TEAM_DOCS || path.resolve(__dirname, '..', '..', 'test-results'), 'monitor');
test.beforeAll(async () => { test.setTimeout(600_000); mkdirSync(path.join(SHOTS, 'screens'), { recursive: true }); await buildRenderer(); });

const TEAM = 'payments';
const TEAM2 = 'checkout-web-with-a-rather-long-team-name';
const ADA = 'DeveloperAda';
const BEN = 'TesterBen';
const C = {
  read: '#7dd3fc', edit: '#a3e635', create: '#2dd4bf', commit: '#fde047', handback: '#fbbf24', done: '#34d399', tell: '#38bdf8', ink: '#e2e8f0', inkDim: '#94a3b8',
  start: '#22d3ee', blocked: '#fb923c', fail: '#f43f5e', decision: '#c4b5fd', run: '#e879f9',
};
const FILE_COLORS = [C.read, C.edit, C.create];

// ---------------------------------------------------------------------------------------------------------------------
// The feed behind the fake core.

type Row = { seq: number; kind: string; team: string; member: string | null; from: string; text: string | null; workflow: number; at: string };
type Step = { id: number; at: string; team: string; member: string; run: number; kind: string; tool: string; target: string | null; exit?: number | null };
type Ev = { id: number; kind: string; team: string; member: string | null; branch: string; sha: string; message: string; files: { path: string; status: string }[]; at: string; recordedAt: string; workflowId: number | null };
type Wf = { id: number; title: string; owner: string; state: 'open' | 'completed' | 'closed'; undeclared: boolean; busy: string[]; ending: null; startedAt: string; lastAt: string; messages: number };

class Feed {
  rows: Row[] = []; steps: Step[] = []; events: Ev[] = [];
  /** Each team's workflows (bus.workflows), by team id. */
  wfs = new Map<number, Wf[]>();
  /** A member's attention (teams.agents), by "teamId/name". */
  attn = new Map<string, string>();
  private seq = 100; private sid = 1000; private eid = 1;
  calls: { method: string; params: any }[] = [];
  netAsks: any[] | null = null;
  toolAsks: any[] | null = null;
  netAsk(id: number, member: string, host: string, state = 'pending', team = TEAM) {
    return { id, at: this.iso(60_000), teamId: 1, team, member: `${team}/${member}`, memberName: member, host, reason: 'restore', method: 'CONNECT', pathSample: null, count: 1, lastSeen: this.iso(30_000), hint: null, workflow: 10, state, scope: null, decidedBy: null, decidedAt: null, plainHttp: false, choices: ['agent', 'team', 'everywhere', 'deny'] };
  }
  toolAsk(id: number, member: string, tool: string, displayName: string, state = 'pending', team = TEAM) {
    return { id, at: this.iso(60_000), teamId: 1, team, member: `${team}/${member}`, memberName: member, tool, displayName, publisher: 'Microsoft', wingetId: null, scope: 'machine', reason: 'build', count: 1, lastSeen: this.iso(30_000), workflow: 10, state, attempts: 0, error: null, decidedBy: null, decidedVia: null, decidedAt: null };
  }
  iso(msAgo = 0) { return new Date(Date.now() - msAgo).toISOString(); }
  row(kind: string, member: string | null, from: string, workflow = 10, msAgo = 0, text: string | null = null, team = TEAM) {
    this.rows.push({ seq: ++this.seq, kind, team, member, from, text, workflow, at: this.iso(msAgo) });
  }
  step(member: string, kind: string, target: string | null, msAgo = 0, team = TEAM, exit?: number | null) {
    const tool = kind === 'read' ? 'Read' : kind === 'edit' ? 'Edit' : kind === 'create' ? 'Write' : kind === 'think' ? 'thinking' : 'Bash';
    const s: Step = { id: ++this.sid, at: this.iso(msAgo), team, member, run: 41, kind, tool, target };
    if (exit !== undefined) s.exit = exit;
    this.steps.push(s);
  }
  event(e: Partial<Ev> & { kind: string; sha: string }, msAgo = 0) {
    const at = this.iso(msAgo);
    this.events.push({ id: this.eid++, team: TEAM, member: null, branch: 'members/payments/developerada', message: '', files: [], at, recordedAt: at, workflowId: 10, ...e });
  }
  wf(teamId: number, id: number, state: Wf['state'], busy: string[], title = `Workflow ${id}`) {
    const list = this.wfs.get(teamId) ?? [];
    const old = list.find((w) => w.id === id);
    const w: Wf = { id, title, owner: 'Manager', state, undeclared: false, busy, ending: null, startedAt: this.iso(600_000), lastAt: this.iso(0), messages: 3 };
    if (old) Object.assign(old, w); else list.push(w);
    this.wfs.set(teamId, list);
  }
  answer(method: string, p: any): unknown | undefined {
    if (method.startsWith('monitor.') || method === 'network.asks' || method === 'tools.asks' || method === 'bus.workflows') this.calls.push({ method, params: p });
    if (method === 'monitor.messages') {
      const after = p?.after ?? 0;
      return { end: Math.max(after, ...this.rows.map((r) => r.seq), 0), messages: this.rows.filter((r) => r.seq > after) };
    }
    if (method === 'monitor.steps') {
      const after = p?.after ?? 0;
      return { end: Math.max(after, ...this.steps.map((s) => s.id), 0), pending: false, steps: this.steps.filter((s) => s.id > after) };
    }
    if (method === 'monitor.codeEvents') {
      const since = p?.since ? Date.parse(p.since) : -Infinity;
      return this.events.filter((e) => Date.parse(e.at) >= since && (!p?.team || e.team.toLowerCase() === String(p.team).toLowerCase()));
    }
    if (method === 'bus.workflows') return JSON.parse(JSON.stringify(this.wfs.get(p?.teamId) ?? []));
    // The waiting network and tool asks, when the test sets them (else the fake core answers).
    if (method === 'network.asks' && this.netAsks) return JSON.parse(JSON.stringify(this.netAsks));
    if (method === 'tools.asks' && this.toolAsks) return JSON.parse(JSON.stringify(this.toolAsks));
    return undefined;
  }
}

// ---------------------------------------------------------------------------------------------------------------------
// The canvas recorder (runs in the page), and a count of the sound's oscillators.

function recorder() {
  const w = window as any;
  w.__rec = true;
  w.__frames = [];
  w.__perf = null;
  w.__osc = 0;
  w.__audio = 0;
  const P = CanvasRenderingContext2D.prototype as any;
  const paths = new WeakMap<object, any[]>();
  let cur: any[] | null = null;
  const mine = (c: any) => c.canvas?.dataset?.test === 'monitor-canvas';
  const m = (c: any) => { const t = c.getTransform(); return [t.a, t.d, t.e, t.f]; };
  const style = (s: any) => (typeof s === 'string' ? s : 'gradient');
  const wrap = (name: string, f: (c: any, args: any[]) => void) => {
    const orig = P[name];
    P[name] = function (this: any, ...args: any[]) { if (w.__rec && mine(this)) f(this, args); return orig.apply(this, args); };
  };
  wrap('fillRect', (c, a) => {
    if (a[0] === 0 && a[1] === 0 && c.fillStyle === '#070b14') {
      if (cur) { w.__frames.push({ at: performance.now(), ops: cur }); if (w.__frames.length > 320) w.__frames.shift(); }
      cur = [];
    }
  });
  wrap('beginPath', (c) => { paths.set(c, []); });
  wrap('arc', (c, a) => { (paths.get(c) ?? (paths.set(c, []), paths.get(c)!)).push({ t: 'arc', x: a[0], y: a[1], r: a[2], a0: a[3], a1: a[4] }); });
  wrap('moveTo', (c, a) => { paths.get(c)?.push({ t: 'm', x: a[0], y: a[1] }); });
  wrap('lineTo', (c, a) => { paths.get(c)?.push({ t: 'l', x: a[0], y: a[1] }); });
  wrap('quadraticCurveTo', (c, a) => { paths.get(c)?.push({ t: 'q', x: a[2], y: a[3] }); });
  const draw = (kind: string) => (c: any) => {
    if (!cur) return;
    const items = paths.get(c) ?? [];
    cur.push({ op: kind, style: style(kind === 'fill' ? c.fillStyle : c.strokeStyle), alpha: c.globalAlpha, lw: c.lineWidth, dash: c.getLineDash().length > 0, items: items.slice(0, 40), m: m(c) });
  };
  wrap('fill', draw('fill'));
  wrap('stroke', draw('stroke'));
  wrap('fillText', (c, a) => { cur?.push({ op: 'text', text: String(a[0]), x: a[1], y: a[2], style: style(c.fillStyle), alpha: c.globalAlpha, m: m(c) }); });

  const raf = window.requestAnimationFrame.bind(window);
  window.requestAnimationFrame = (cb: FrameRequestCallback) => raf((t) => {
    const s = performance.now();
    try { cb(t); } finally { const p = w.__perf; if (p) p.push({ t, s, d: performance.now() - s }); }
  });
  // The sound: how many audio contexts and oscillators the page makes.
  const AC = (window as any).AudioContext;
  if (AC) {
    const orig = AC.prototype.createOscillator;
    AC.prototype.createOscillator = function (this: any, ...a: any[]) { w.__osc++; return orig.apply(this, a); };
    (window as any).AudioContext = class extends AC { constructor(...a: any[]) { super(...a); w.__audio++; } };
  }
  // Each oscillator started, with its frequency (a needs-you tone starts at 660 Hz, then 880).
  w.__tones = [];
  const ON = (window as any).OscillatorNode;
  if (ON) {
    const start = ON.prototype.start;
    ON.prototype.start = function (this: any, ...a: any[]) { w.__tones.push({ f: this.frequency.value, at: performance.now() }); return start.apply(this, a); };
  }
}

type Op = { op: 'fill' | 'stroke' | 'text'; style: string; alpha: number; lw?: number; dash?: boolean; items?: any[]; text?: string; x?: number; y?: number; m: number[] };
type Frame = { at: number; ops: Op[] };
type Pt = { x: number; y: number };

const frames = (page: Page): Promise<Frame[]> => page.evaluate(() => (window as any).__frames);
async function lastFrame(page: Page): Promise<Frame> {
  await page.waitForFunction(() => (window as any).__frames.length > 0, null, { timeout: 5000 });
  return page.evaluate(() => (window as any).__frames.at(-1));
}
/** The newest n frames (cheaper than all of them). */
const recent = (page: Page, n = 12): Promise<Frame[]> => page.evaluate((k) => (window as any).__frames.slice(-k), n);
const screen = (o: { m: number[] }, x: number, y: number) => ({ x: o.m[0] * x + o.m[2], y: o.m[1] * y + o.m[3] });
const isScreen = (o: Op) => o.m[2] === 0 && o.m[3] === 0 && o.m[0] === 1;
const near = (a: Pt, b: Pt, tol = 0.5) => Math.hypot(a.x - b.x, a.y - b.y) < tol;
const full = (i: any) => Math.abs(i.a1 - i.a0 - Math.PI * 2) < 0.01;

function memberAt(f: Frame, name: string): (Pt & { r: number; op: Op }) | null {
  const spaced = name.replace(/([a-z])([A-Z])/g, '$1 $2');
  const t = f.ops.find((o) => o.op === 'text' && o.text === spaced);
  if (!t) return null;
  const r = name === 'Manager' ? 17 : 14;
  return { x: t.x!, y: t.y! - r - 11, r, op: t };
}
function hubAt(f: Frame, team = TEAM): (Pt & { op: Op }) | null {
  const t = f.ops.find((o) => o.op === 'text' && o.text === team && o.style === C.ink && !isScreen(o));
  return t ? { x: t.x!, y: t.y! + 22.5, op: t } : null;
}
const texts = (f: Frame) => f.ops.filter((o) => o.op === 'text').map((o) => o.text!);
/** Filled arcs of one item: centre, radius, colour. */
function discs(f: Frame, pred: (o: Op, a: any) => boolean) {
  return f.ops.filter((o) => o.op === 'fill' && o.items?.length === 1 && o.items[0].t === 'arc' && pred(o, o.items[0])).map((o) => ({ ...o.items![0], style: o.style, alpha: o.alpha, op: o }));
}
/** The member's body radius this frame (its gradient disc at its centre). */
function bodyR(f: Frame, name: string): number | null {
  const m = memberAt(f, name);
  if (!m) return null;
  const d = discs(f, (o, a) => o.style === 'gradient' && near(a, m, 0.01) && a.r >= m.r - 0.01 && a.r <= m.r * 1.071);
  return d[0]?.r ?? null;
}
/** Stroked arcs centred on p at radius r (+- tol). */
function arcsAt(f: Frame, p: Pt, r: number, style?: string, tol = 0.05) {
  return f.ops.filter((o) => o.op === 'stroke' && (!style || o.style === style)).flatMap((o) => (o.items ?? []).filter((i) => i.t === 'arc' && near(i, p, 0.05) && Math.abs(i.r - r) < tol).map((i) => ({ ...i, alpha: o.alpha, lw: o.lw, style: o.style })));
}
/** Small filled sparks of a colour round a node (a burst's). */
function sparks(f: Frame, p: Pt, color: string) {
  return discs(f, (o, a) => o.style === color && a.r <= 2.01 && Math.hypot(a.x - p.x, a.y - p.y) > 10 && Math.hypot(a.x - p.x, a.y - p.y) < 90).length;
}
/** The workflow node squares (and leaving ones): a fill rgba(7, 11, 20, 0.95) with one moveTo, then its stroke. */
function wfBoxes(f: Frame) {
  const out: { x: number; y: number; color: string; lw: number; alpha: number; at: number }[] = [];
  f.ops.forEach((o, i) => {
    if (o.op !== 'fill' || o.style !== 'rgba(7, 11, 20, 0.95)' || o.items?.length !== 1 || o.items[0].t !== 'm') return;
    const s = f.ops[i + 1];
    if (!s || s.op !== 'stroke' || s.items?.[0]?.t !== 'm') return;
    out.push({ x: o.items[0].x + 6, y: o.items[0].y + 10, color: s.style, lw: s.lw!, alpha: o.alpha, at: i });
  });
  return out;
}
/**
 * A workflow node by its "#id" label: the square drawn last before the label (scene drawWorkflowNode draws the square, its
 * icon, then its label). The label is not always 13 under the centre - placeWorkflowLabels moves a label above or
 * beside its node when the place under it is taken (by #11's or #12's label, as the graph drifts), so finding the square by
 * the label's place missed about half the runs of 1b.
 */
function wfNode(f: Frame, id: number) {
  // The label is "#id title" (or "#id" on a phone's wall zoomed out).
  const at = f.ops.findIndex((o) => o.op === 'text' && (o.text === `#${id}` || o.text!.startsWith(`#${id} `)) && (o.style === C.inkDim || o.style === C.blocked));
  if (at < 0) return null;
  const box = wfBoxes(f).filter((b) => b.at < at).at(-1);
  return box ? { ...box, label: f.ops[at]!.style, op: f.ops[at]! } : null;
}
/** Dashed lines from a to b (either way). */
function dashedBetween(f: Frame, a: Pt, b: Pt, tol = 1.5) {
  return f.ops.filter((o) => o.op === 'stroke' && o.dash && o.items?.length === 2 && o.items[0].t === 'm' && o.items[1].t === 'l'
    && ((near(o.items[0], a, tol) && near(o.items[1], b, tol)) || (near(o.items[0], b, tol) && near(o.items[1], a, tol))));
}
function badges(f: Frame) {
  return f.ops.filter((o) => o.op === 'fill' && o.style.startsWith('rgba(7, 11, 20, 0.88)')).flatMap((o) => o.items!.filter((i) => i.t === 'arc' && Math.abs(i.r - 8.5) < 0.01));
}
function fileDots(f: Frame) {
  const b = badges(f);
  return discs(f, (o, a) => FILE_COLORS.includes(o.style) && a.r <= 4.6 && a.r >= 0.5 && full(a)).filter((d) => !b.some((k) => Math.hypot(k.x - d.x, k.y - d.y) < 12));
}
const commitDots = (f: Frame) => discs(f, (o, a) => o.style === C.commit && a.r >= 4.19);
/** Comet heads (world space): the first, largest dot of a comet's tail. */
const cometHeads = (f: Frame, color = C.tell) => discs(f, (o, a) => o.style === color && Math.abs(a.r - 3.2) < 0.01 && !isScreen(o));
/** The You node (screen space). */
function youNode(f: Frame) {
  const you = f.ops.find((o) => o.op === 'text' && o.text === 'You' && isScreen(o));
  if (!you) return null;
  const disc = f.ops.find((o) => o.op === 'fill' && isScreen(o) && o.items?.length === 1 && o.items[0].t === 'arc' && Math.abs(o.items[0].r - 24) < 0.01);
  const around = f.ops.filter((o) => o.op === 'text' && isScreen(o) && Math.abs(o.x! - you.x!) < 30 && Math.abs(o.y! - you.y!) < 40).map((o) => ({ text: o.text!, style: o.style }));
  const count = around.find((t) => /^\d+\+?$/.test(t.text))?.text ?? null;
  return { x: you.x!, y: you.y! - 0.5, fill: disc?.style ?? null, count, needsYou: around.some((t) => t.text === 'NEEDS YOU'), around };
}
const youFlyers = (f: Frame) => discs(f, (o, a) => isScreen(o) && o.style === C.decision && a.r <= 3.41 && a.r > 1);

async function canvasBox(page: Page) { return (await page.locator('[data-test=monitor-canvas]').boundingBox())!; }
/** Moves to a world point, lets the scene see the pointer there, then presses and lets go (a bare click from afar missed). */
async function clickWorld(page: Page, o: { m: number[] }, p: Pt) {
  const box = await canvasBox(page);
  const s = screen(o, p.x, p.y);
  await page.mouse.move(box.x + s.x, box.y + s.y);
  await page.waitForTimeout(150);
  if (process.env.DBG_CLICK) await page.screenshot({ path: 'test-results/dbg-click.png' });
  if (process.env.DBG_CLICK) console.log('CLICK at', s.x.toFixed(1), s.y.toFixed(1), 'tip:', (await page.locator('[data-test=monitor-tip]').count()) ? (await page.locator('[data-test=monitor-tip]').innerText()).replace(/\s+/g, ' ') : 'none');
  await page.mouse.down();
  await page.mouse.up();
}
/**
 * Focuses a team by a click (a tap on a phone) on its hub, reading its place afresh each try: the hub's reach is 14
 * world units, a few pixels zoomed out, and the graph drifts. Returns the tries it took (0: it never focused).
 */
async function focusTeam(page: Page, team = TEAM, tap = false): Promise<number> {
  for (let i = 1; i <= 8; i++) {
    const f = await lastFrame(page);
    const h = hubAt(f, team);
    if (h) {
      const box = await canvasBox(page);
      const s = screen(h.op, h.x, h.y);
      if (s.x > 0 && s.y > 0 && s.x < box.width && s.y < box.height) {
        // At once: the hub drifts several pixels in 150 ms and reaches only 14 world units.
        if (tap) await page.touchscreen.tap(box.x + s.x, box.y + s.y);
        else { await page.mouse.move(box.x + s.x, box.y + s.y); await page.mouse.down(); await page.mouse.up(); }
        await page.waitForTimeout(100);
        if (process.env.DBG_CLICK) {
          const g = await lastFrame(page); const h2 = hubAt(g, team)!; const s2 = screen(h2.op, h2.x, h2.y);
          console.log('TRY', i, 'k', h.op.m[0].toFixed(3), 'at', s.x.toFixed(1), s.y.toFixed(1), 'hub after', s2.x.toFixed(1), s2.y.toFixed(1), 'picked', await page.locator('[data-test=monitor-unpick]').count(), 'tip', (await page.locator('[data-test=monitor-tip]').count()) ? (await page.locator('[data-test=monitor-tip]').innerText()).replace(/\s+/g, ' ').slice(0, 60) : 'none');
        }
        if (await page.locator('[data-test=monitor-focus]').count()) return i;
      } else if (process.env.DBG_CLICK) console.log('SKIP', i, 'hub off the wall: k', h.op.m[0].toFixed(3), 'at', s.x.toFixed(1), s.y.toFixed(1), 'wall', box.width.toFixed(0), box.height.toFixed(0));
    } else if (process.env.DBG_CLICK) {
      const inks = f.ops.filter((o) => o.op === 'text' && o.text === team).map((o) => `${o.style}${isScreen(o) ? ' screen' : ''}`);
      console.log('SKIP', i, 'no hub label', f.ops.length, 'ops; texts', team, ':', JSON.stringify(inks));
    }
    await page.waitForTimeout(400);
  }
  return 0;
}
async function unhover(page: Page) { await page.mouse.move(2, 2); await page.waitForTimeout(150); }

/** Waits until the frames show what `ok` wants. */
async function until<T>(page: Page, get: (f: Frame) => T, ok: (v: T) => boolean, timeout = 10_000): Promise<T> {
  const end = Date.now() + timeout;
  let v: T;
  do {
    const f = await lastFrame(page);
    v = f ? get(f) : (undefined as T);
    if (f && ok(v)) return v;
    await page.waitForTimeout(80);
  } while (Date.now() < end);
  return v!;
}
/** Waits for a frame that shows what `ok` wants, and returns that frame (the last one if it never does). */
async function untilFrame(page: Page, ok: (f: Frame) => unknown, timeout = 10_000): Promise<Frame> {
  const end = Date.now() + timeout;
  let f: Frame;
  do {
    f = await lastFrame(page);
    if (ok(f)) return f;
    await page.waitForTimeout(80);
  } while (Date.now() < end);
  return f;
}
/** Over the frames of the last `ms`, the most of what `get` counts. */
async function most(page: Page, get: (f: Frame) => number, ms: number): Promise<number> {
  let best = 0;
  const end = Date.now() + ms;
  while (Date.now() < end) {
    for (const f of await recent(page)) best = Math.max(best, get(f));
    await page.waitForTimeout(120);
  }
  return best;
}

// ---------------------------------------------------------------------------------------------------------------------

type OpenOpts = { reduced?: boolean; access?: Access; feed?: Feed; w?: number; h?: number; extraTeams?: number; theme?: 'light' | 'dark'; asks?: boolean; density?: string; sound?: boolean };

async function open(browser: Browser, o: OpenOpts = {}) {
  const feed = o.feed ?? new Feed();
  const core = createFakeCore();
  core.setAsks(o.asks ?? false);
  core.setNeedsYou(false);
  core.setAccess(o.access ?? 'full');
  const extra = Array.from({ length: o.extraTeams ?? 0 }, (_, i) => ({ id: 3 + i, name: `team-${3 + i}`, repoPath: `C:/repos/t${3 + i}`, members: 2, running: 1, queued: 0 }));
  const phone = (o.w ?? 1280) < 700;
  const ctx = await browser.newContext({ viewport: { width: o.w ?? 1280, height: o.h ?? (phone ? 812 : 800) }, reducedMotion: o.reduced ? 'reduce' : 'no-preference', colorScheme: o.theme ?? 'dark', hasTouch: phone, isMobile: false });
  await ctx.addInitScript((a: { theme: string; density: string | null; sound: boolean | null }) => {
    try {
      if (sessionStorage.getItem('__seeded')) return;
      sessionStorage.setItem('__seeded', '1');
      localStorage.setItem('yawble.monitor.windowMinutes', '15');
      localStorage.setItem('yawble.monitor.activity', 'open');
      localStorage.setItem('yawble.remoteTheme', a.theme);
      if (a.density) localStorage.setItem('yawble.monitor.density', a.density);
      if (a.sound !== null) localStorage.setItem('yawble.monitor.sound', a.sound ? 'on' : 'off');
    } catch { /* */ }
  }, { theme: o.theme ?? 'dark', density: o.density ?? null, sound: o.sound ?? null });
  await ctx.addInitScript(recorder);
  await ctx.route(`${ORIGIN}/**`, async (route) => {
    const rel = decodeURIComponent(new URL(route.request().url()).pathname).replace(/^\/+/, '');
    const file = path.resolve(builtDir, rel || 'remote.html');
    if (!file.startsWith(builtDir) || !existsSync(file)) return route.fulfill({ status: 404, body: 'not found' });
    return route.fulfill({ path: file });
  });
  const sockets: { send: (s: string) => void }[] = [];
  await ctx.routeWebSocket(/\/rpc$/, (ws) => {
    sockets.push(ws);
    ws.onMessage((m) => {
      const msg = JSON.parse(String(m));
      try {
        if (msg.method === 'app.overview' && extra.length) {
          const base = JSON.parse(core.answer(String(m))!).result;
          return ws.send(JSON.stringify({ id: msg.id, result: { ...base, teams: [...base.teams, ...extra] } }));
        }
        if (msg.method === 'teams.agents' && (msg.params?.teamId ?? 0) >= 3) {
          const t = extra.find((x) => x.id === msg.params.teamId)!;
          const card = (id: number, name: string, role: string) => ({ member: { id, teamId: t.id, team: t.name, name, role, cli: 'claude', instructions: '', model: '', credential: 'default' }, state: 'running', queuePosition: null, queueLength: 0, queuedInstructions: 0, statusLine: null, runId: 1, runStartedAt: feed.iso(60_000), lastRunState: 'completed', lastRunReason: null, attention: null, net: 'allowlist', wallTimeout: '2h', loosened: [], policyProblems: [], hasClone: true });
          return ws.send(JSON.stringify({ id: msg.id, result: [card(100 + t.id * 2, 'Manager', 'manager'), card(101 + t.id * 2, 'DeveloperZed', 'member')] }));
        }
        if (msg.method === 'teams.agents') {
          const base = JSON.parse(core.answer(String(m))!);
          for (const c of base.result ?? []) { const a = feed.attn.get(`${msg.params.teamId}/${c.member.name}`); if (a) c.attention = a; }
          return ws.send(JSON.stringify(base));
        }
        const got = feed.answer(msg.method, msg.params);
        if (got !== undefined) return ws.send(JSON.stringify({ id: msg.id, result: got }));
      } catch (e: any) {
        return ws.send(JSON.stringify({ id: msg.id, error: { code: e.code ?? 'fake_error', message: String(e.message) } }));
      }
      const out = core.answer(String(m));
      if (out) ws.send(out);
    });
  });
  const page = await ctx.newPage();
  const errors: string[] = [];
  page.on('pageerror', (e) => errors.push(e.message));
  await page.goto(`${ORIGIN}/remote.html`);
  if (phone) {
    await page.locator('[data-test=drawer-toggle]').click();
    await expect(page.locator('[data-test=sidebar]')).toHaveAttribute('data-drawer', 'open');
  }
  await page.locator('[data-test=sidebar] [data-test=nav-monitor]').first().click();
  await expect(page.locator('[data-test=monitor-canvas]')).toBeVisible({ timeout: 30_000 });
  await expect(page.locator('[data-test=monitor-loading]')).toHaveCount(0, { timeout: 30_000 });
  /** A core event to the page (as the phone server pushes them: { event, data }). */
  const push = (event: string, data: unknown = {}) => { for (const ws of sockets) ws.send(JSON.stringify({ event, data })); };
  return { page, ctx, feed, core, errors, push };
}

async function shot(page: Page, name: string) { await page.screenshot({ path: path.join(SHOTS, 'screens', `${name}.png`) }); }

const result: Record<string, unknown> = {};
// Merged into what is there: a --grep group run keeps the other groups' results instead of leaving only its own.
test.afterAll(() => {
  const file = path.join(SHOTS, 'monitor-results.json');
  let old: Record<string, unknown> = {};
  try { old = JSON.parse(readFileSync(file, 'utf8')); } catch { /* none yet */ }
  writeFileSync(file, JSON.stringify({ ...old, ...result }, null, 2));
});

/** The usual scene: payments with three workflows (10 worked by Ada, 11 nobody on, 12 worked by Ben). */
function seed(feed: Feed) {
  feed.row('instruction', ADA, 'Manager', 10, 120_000, 'Card 1: partial refunds');
  feed.row('instruction', BEN, 'Manager', 12, 119_000, 'Card 2: test partial refunds');
  feed.wf(1, 10, 'open', [`${ADA} is running`], 'Partial refunds');
  feed.wf(1, 11, 'open', [], 'Refund report');
  feed.wf(1, 12, 'open', [`${BEN} is queued`], 'Refund tests');
}

// ---------------------------------------------------------------------------------------------------------------------
// 1. Workflow nodes

/** A workflow node click opens the Workflows page (no dialog): the page shown, filtered to the team, the workflow's pane open. */
const wfPaneOpen = (page: Page, id: number) => page.locator(`[data-test=workflows-view] [data-test=workflow-pane][data-id="${id}"]`);
async function expectWorkflowsPage(page: Page, id: number, team = '1') {
  await expect(page.locator('[data-test=workflows-view]')).toBeVisible();
  await expect(wfPaneOpen(page, id)).toBeVisible();
  await expect(page.locator('[data-test=workflows-team-filter]')).toHaveValue(team);
  await expect(page.locator('[role=dialog]')).toHaveCount(0);
}

test('1 workflow nodes: open ones tied to their members (or hub), blocked look, completed flashes green and drifts, closed fades with a cross; a click opens it', async ({ browser }) => {
  const feed = new Feed();
  seed(feed);
  const { page, ctx, errors } = await open(browser, { feed });
  const r: Record<string, unknown> = {};
  await untilFrame(page, (x) => [10, 11, 12].every((id) => wfNode(x, id)), 12_000);
  await page.waitForTimeout(2500); // the forces settle
  let f = await lastFrame(page);
  const n10 = wfNode(f, 10)!, n11 = wfNode(f, 11)!, n12 = wfNode(f, 12)!;
  const ada = memberAt(f, ADA)!, ben = memberAt(f, BEN)!, hub = hubAt(f)!;
  r.nodes = { n10: { color: n10.color, lw: n10.lw }, n11: { color: n11.color }, n12: { color: n12.color } };
  expect(n10.color, '#10 being worked: cyan').toBe(C.start);
  expect(n11.color, '#11 nobody on it: amber').toBe(C.handback);
  expect(n12.color, '#12 worked by Ben').toBe(C.start);
  r.lines = { adaTo10: dashedBetween(f, n10, ada).length, benTo12: dashedBetween(f, n12, ben).length, hubTo11: dashedBetween(f, n11, hub).length, adaTo11: dashedBetween(f, n11, ada).length };
  expect(dashedBetween(f, n10, ada).length, '#10 tied to Ada').toBeGreaterThan(0);
  expect(dashedBetween(f, n12, ben).length, '#12 tied to Ben').toBeGreaterThan(0);
  expect(dashedBetween(f, n11, hub).length, '#11 (nobody on it) tied to the hub').toBeGreaterThan(0);
  expect(dashedBetween(f, n11, ada).length).toBe(0);
  await shot(page, 'wf-01-open-nodes');

  // Blocked: a "blocked" row in workflow 12.
  feed.row('blocked', BEN, BEN, 12, 0, 'Needs the refund limit');
  f = await untilFrame(page, (x) => wfNode(x, 12)?.color === C.blocked);
  const b12 = wfNode(f, 12)!;
  r.blocked = { color: b12.color, lw: b12.lw, label: b12.label };
  expect(b12.color, '#12 blocked: orange').toBe(C.blocked);
  expect(b12.lw, 'thicker edge').toBeCloseTo(2.2, 5);
  expect(b12.label, 'its "#12" in the blocked colour').toBe(C.blocked);
  await shot(page, 'wf-02-blocked');

  // Completed: #10 flashes green and drifts away from the hub; closed: #11 fades as a grey cross.
  await page.evaluate(() => { (window as any).__frames = []; });
  feed.wf(1, 10, 'completed', []);
  feed.wf(1, 11, 'closed', []);
  await until(page, (x) => !wfNode(x, 10) && !wfNode(x, 11), Boolean, 12_000);
  await page.waitForTimeout(2800);
  const fs = await frames(page);
  const flash = fs.filter((x) => { const h = hubAt(x); return h && arcsAt(x, h, 0, C.done, 1e9).length >= 0 && x.ops.some((o) => o.op === 'stroke' && o.style === C.done && o.lw === 2.5 && o.items?.some((i) => i.t === 'arc' && full(i) && i.r >= 13 && i.r <= 39.1)); }).length;
  const greens = fs.map((x) => ({ h: hubAt(x), b: wfBoxes(x).filter((b) => b.color === C.done) })).filter((x) => x.h && x.b.length);
  const dist = greens.map((x) => Math.hypot(x.b[0]!.x - x.h!.x, x.b[0]!.y - x.h!.y));
  const greys = fs.map((x) => wfBoxes(x).filter((b) => b.color === C.inkDim)).filter((b) => b.length).map((b) => b[0]!.alpha);
  r.ends = { flashFrames: flash, greenFrames: greens.length, driftFrom: dist[0], driftTo: dist.at(-1), greyFrames: greys.length, greyAlphaFirst: greys[0], greyAlphaLast: greys.at(-1) };
  expect(flash, 'a green flash ring opens out').toBeGreaterThan(3);
  expect(greens.length, 'the completed node is drawn green while it leaves').toBeGreaterThan(10);
  expect(dist.at(-1)! - dist[0]!, 'and drifts away from its hub').toBeGreaterThan(30);
  expect(greys.length, 'the closed node fades as a grey square').toBeGreaterThan(10);
  expect(greys.at(-1)!, 'fading').toBeLessThan(greys[0]! - 0.2);
  await shot(page, 'wf-03-after-ends');

  // A click on the workflow node #12 opens that workflow.
  await unhover(page);
  for (let i = 0; i < 4; i++) {
    f = await lastFrame(page);
    const n = wfNode(f, 12)!;
    await clickWorld(page, n.op, n);
    if (await wfPaneOpen(page, 12).count()) break;
    await page.waitForTimeout(300);
  }
  await expectWorkflowsPage(page, 12);
  r.clickOpens = 'the Workflows page with workflow 12 open (team payments)';
  await shot(page, 'wf-04-click-opens-workflow');
  result.workflowNodes = r;
  expect(errors).toEqual([]);
  await ctx.close();
});

test('1b a workflow node click with a team focused: the workflow opens; is the focus left?', async ({ browser }) => {
  const feed = new Feed();
  seed(feed);
  const { page, ctx, errors } = await open(browser, { feed });
  await until(page, (x) => !!wfNode(x, 10), Boolean, 12_000);
  await page.waitForTimeout(2000);
  let f = await lastFrame(page);
  await focusTeam(page);
  await expect(page.locator('[data-test=monitor-focus]')).toBeVisible();
  await page.waitForTimeout(2200); // the zoom
  await unhover(page);
  for (let i = 0; i < 4; i++) {
    f = await lastFrame(page);
    const n = wfNode(f, 10)!;
    await clickWorld(page, n.op, n);
    if (await wfPaneOpen(page, 10).count()) break;
    await page.waitForTimeout(300);
  }
  await expectWorkflowsPage(page, 10);
  const focusAfter = await page.locator('[data-test=monitor-focus]').count();
  result.wfClickFocused = { workflowOpened: true, focusChipAfterClick: focusAfter };
  await shot(page, 'wf-05-click-while-focused');
  // Recorded, not asserted either way: the card expects the focus to be left.
  console.log('FOCUS after a workflow-node click:', focusAfter ? 'still focused' : 'left');
  expect(errors).toEqual([]);
  await ctx.close();
});

// ---------------------------------------------------------------------------------------------------------------------
// 2. The work rhythm

test('2 rhythm: the pulse follows the step rate, a command ring fills then bursts green / red only with an exit, thinking shimmers', async ({ browser }) => {
  const feed = new Feed();
  seed(feed);
  const { page, ctx, errors } = await open(browser, { feed });
  await until(page, (x) => !!memberAt(x, ADA), Boolean);
  await page.waitForTimeout(1500);
  const r: Record<string, unknown> = {};

  // Pulse: a period from the body radius over ~5 s (its peaks).
  const period = async (name: string) => {
    await page.evaluate(() => { (window as any).__frames = []; });
    await page.waitForTimeout(5200);
    const fs = await frames(page);
    const rs = fs.map((x) => ({ t: x.at, r: bodyR(x, name) })).filter((x) => x.r !== null) as { t: number; r: number }[];
    const lo = Math.min(...rs.map((x) => x.r)), hi = Math.max(...rs.map((x) => x.r));
    const mid = (lo + hi) / 2;
    const ups = rs.filter((x, i) => i && rs[i - 1]!.r < mid && x.r >= mid).map((x) => x.t);
    const p = ups.length > 1 ? (ups.at(-1)! - ups[0]!) / (ups.length - 1) : null;
    return { lo: +lo.toFixed(3), hi: +hi.toFixed(3), period: p && +p.toFixed(0), frames: rs.length };
  };
  r.quietBen = await period(BEN);
  expect((r.quietBen as any).hi - (r.quietBen as any).lo, 'a quiet member does not pulse').toBeLessThan(0.01);
  for (let i = 0; i < 2; i++) feed.step(ADA, 'read', `src/a${i}.ts`);
  await page.waitForTimeout(1500);
  r.slowAda = await period(ADA);
  for (let i = 0; i < 60; i++) feed.step(ADA, 'read', `src/b${i}.ts`);
  await page.waitForTimeout(1500);
  r.fastAda = await period(ADA);
  const slow = r.slowAda as any, fast = r.fastAda as any;
  expect(slow.hi - slow.lo, 'a working member pulses').toBeGreaterThan(0.5);
  expect(fast.period, 'faster with more steps').toBeLessThan(slow.period * 0.6);

  // A command with no exit: a ring fills while it runs, full when the next step ends it, no burst.
  await page.evaluate(() => { (window as any).__frames = []; });
  feed.step(BEN, 'run', 'npm test -- refunds');
  let f = await untilFrame(page, (x) => { const b = memberAt(x, BEN); return b ? arcsAt(x, b, b.r + 12, C.run).length >= 2 : false; });
  const ringFill = (x: Frame) => { const b = memberAt(x, BEN)!; const a = arcsAt(x, b, b.r + 12, C.run).filter((i) => Math.abs(i.a0 + Math.PI / 2) < 0.001 && !full(i)); return a.length ? (a[0]!.a1 - a[0]!.a0) / (Math.PI * 2) : null; };
  const fill1 = ringFill(f);
  await page.waitForTimeout(2500);
  const fill2 = ringFill(await lastFrame(page));
  r.runNoExit = { fill1, fill2 };
  expect(fill1, 'the ring is drawn while it runs').not.toBeNull();
  expect(fill2!, 'and fills').toBeGreaterThan(fill1!);
  await shot(page, 'rhythm-01-run-ring');
  await page.evaluate(() => { (window as any).__frames = []; });
  feed.step(BEN, 'read', 'src/refund.ts');
  await page.waitForTimeout(2600);
  let fs = await frames(page);
  const fullRun = fs.filter((x) => { const b = memberAt(x, BEN); return b && arcsAt(x, b, b.r + 12, C.run).some((i) => Math.abs(i.a1 - i.a0 - Math.PI * 2) < 0.01 && i.a0 + Math.PI / 2 < 0.001); }).length;
  const noExitBursts = Math.max(0, ...fs.map((x) => { const b = memberAt(x, BEN); return b ? sparks(x, b, C.done) + sparks(x, b, C.fail) : 0; }));
  r.runEnded = { fullRingFrames: fullRun, burstSparks: noExitBursts };
  expect(fullRun, 'the ring is full when the command ends').toBeGreaterThan(0);
  expect(noExitBursts, 'no exit code: no burst').toBe(0);

  // exit 0: a green ring and a green burst (8 sparks); exit 2: red, a big burst (14).
  for (const [exit, color, n] of [[0, C.done, 8], [2, C.fail, 14]] as const) {
    await page.evaluate(() => { (window as any).__frames = []; });
    feed.step(BEN, 'run', exit ? 'npm run lint' : 'npm test', 0, TEAM, exit);
    await page.waitForTimeout(1800);
    fs = await frames(page);
    const ring = fs.some((x) => { const b = memberAt(x, BEN); return b && arcsAt(x, b, b.r + 12, color).some((i) => full(i) || Math.abs(i.a1 - i.a0 - Math.PI * 2) < 0.01); });
    const sp = Math.max(0, ...fs.map((x) => { const b = memberAt(x, BEN); return b ? sparks(x, b, color) : 0; }));
    r[`exit${exit}`] = { ring, sparks: sp };
    expect(ring, `exit ${exit}: the ring in ${color}`).toBe(true);
    expect(sp, `exit ${exit}: a burst of ${n} sparks`).toBe(n);
    if (exit === 2) await shot(page, 'rhythm-02-exit-red');
    await page.waitForTimeout(800);
  }

  // Thinking: a glint at r + 3 that slides round (its start moves), and the soft halo.
  feed.step(BEN, 'think', null);
  f = await untilFrame(page, (x) => { const b = memberAt(x, BEN); return b ? arcsAt(x, b, b.r + 3, 'rgba(226, 232, 240, 0.35)').length > 0 : false; });
  const g1 = (() => { const b = memberAt(f, BEN)!; return arcsAt(f, b, b.r + 3, 'rgba(226, 232, 240, 0.35)')[0]?.a0; })();
  await page.waitForTimeout(600);
  f = await lastFrame(page);
  const g2 = (() => { const b = memberAt(f, BEN)!; return arcsAt(f, b, b.r + 3, 'rgba(226, 232, 240, 0.35)')[0]?.a0; })();
  const halo = (() => { const b = memberAt(f, BEN)!; return discs(f, (o, a) => o.style === 'gradient' && near(a, b, 0.01) && Math.abs(a.r - (b.r + 12)) < 0.01).length; })();
  r.thinking = { glintA0: [g1, g2], halo };
  expect(g1).toBeDefined();
  expect(g2).not.toBe(g1);
  expect(halo, 'the soft halo').toBe(1);
  await shot(page, 'rhythm-03-thinking');
  result.rhythm = r;
  expect(errors).toEqual([]);
  await ctx.close();
});

// ---------------------------------------------------------------------------------------------------------------------
// 3. The You node, and 4. cards on comets

test('3 You node: a question flies in, it glows with the count; a click opens Needs you and picks nothing', async ({ browser }) => {
  const feed = new Feed();
  seed(feed);
  // No network or tool asks here (they count too; test 3b checks them).
  const { page, ctx, errors } = await open(browser, { feed, asks: false });
  await until(page, (x) => !!youNode(x), Boolean);
  await page.waitForTimeout(1500);
  const r: Record<string, unknown> = {};
  let f = await lastFrame(page);
  const box = await canvasBox(page);
  let y = youNode(f)!;
  r.place = { x: y.x, y: y.y, canvas: { w: box.width, h: box.height }, expected: { x: box.width - 46, y: Math.max(120, box.height / 2) } };
  expect(y.x).toBeCloseTo(box.width - 46, 0);
  expect(y.y).toBeCloseTo(Math.max(120, box.height / 2), 0);
  r.nothingWaiting = { youCount: y.count, fill: y.fill };
  expect(y.count, 'nothing waits: no count').toBeNull();
  await shot(page, 'you-01-nothing-waiting');

  // A question: the decision row flies to the You node; with the member's attention it counts 1 and glows.
  await page.evaluate(() => { (window as any).__frames = []; });
  feed.row('decision', BEN, BEN, 12, 0, 'Which refund limit?');
  feed.attn.set(`1/${BEN}`, 'decision');
  await page.waitForTimeout(3200); // the next poll (1.2 s), then the flight (1.5 s, its ring in the last quarter)
  const fs = await frames(page);
  const flying = fs.filter((x) => youFlyers(x).length >= 4).length;
  const ring = fs.some((x) => x.ops.some((o) => o.op === 'stroke' && isScreen(o) && o.style === C.decision && o.lw === 2 && o.items?.some((i) => i.t === 'arc' && i.r > 26)));
  f = await untilFrame(page, (x) => youNode(x)?.count === '1');
  y = youNode(f)!;
  r.question = { flyingFrames: flying, arrivalRing: ring, count: y.count, fill: y.fill, needsYou: y.needsYou };
  expect(flying, 'the ask flies in').toBeGreaterThan(5);
  expect(ring, 'a ring opens on arrival').toBe(true);
  expect(y.count).toBe('1');
  expect(y.fill, 'glowing').toBe('rgba(46, 26, 84, 0.95)');
  expect(y.needsYou).toBe(true);
  await shot(page, 'you-02-question-counted');

  // A press on the You node picks nothing (no pick panel, no tip), the release opens Needs you (the Board).
  await page.mouse.move(box.x + y.x, box.y + y.y);
  await page.mouse.down();
  await page.waitForTimeout(150);
  const pickedOnPress = await page.locator('[data-test=monitor-unpick]').count();
  const tip = await page.locator('[data-test=monitor-tip]').count();
  await page.mouse.up();
  await expect(page.locator('[data-test=board]')).toBeVisible({ timeout: 5000 });
  r.click = { pickedOnPress, tip, opened: 'board' };
  expect(pickedOnPress, 'picks nothing').toBe(0);
  expect(tip).toBe(0);
  await shot(page, 'you-03-click-opened-board');
  result.you = r;
  expect(errors).toEqual([]);
  await ctx.close();
});

// The view reads network.asks / tools.asks and hands them to live.setAsks.
test('3b You node: pending network and tool asks are counted (waiting at open: no flight); one added later flies in; a decided one leaves', async ({ browser }) => {
  const feed = new Feed();
  seed(feed);
  // Waiting at open: Ada's two asks on one domain (one Needs-you item), a decided one (not counted), Ben's tool ask
  // (pending) and an installed tool (not counted). Two items.
  feed.netAsks = [feed.netAsk(7, ADA, 'api.nuget.org'), feed.netAsk(8, ADA, 'api.nuget.org'), feed.netAsk(9, BEN, 'pypi.org', 'allowed')];
  feed.toolAsks = [feed.toolAsk(4, BEN, 'dotnet-sdk-10', '.NET SDK 10'), feed.toolAsk(5, ADA, 'python-3', 'Python 3', 'installed')];
  const { page, ctx, errors, push } = await open(browser, { feed });
  const r: Record<string, unknown> = {};
  let f = await untilFrame(page, (x) => youNode(x)?.count === '2', 12_000);
  r.atOpen = { count: youNode(f)?.count ?? null, fill: youNode(f)?.fill ?? null, asksReads: feed.calls.filter((c) => c.method === 'network.asks' || c.method === 'tools.asks').length };
  expect(youNode(f)?.count, 'a network ask and a tool ask: 2').toBe('2');
  expect(youNode(f)?.fill, 'glowing').toBe('rgba(46, 26, 84, 0.95)');
  expect(youNode(f)?.needsYou).toBe(true);
  // Those waiting at open are counted, not flown in.
  await page.evaluate(() => { (window as any).__frames = []; });
  await page.waitForTimeout(2000);
  r.atOpen = { ...(r.atOpen as object), flyingFramesAfter: (await frames(page)).filter((x) => youFlyers(x).length >= 4).length };
  expect((r.atOpen as any).flyingFramesAfter, 'nothing flies for those waiting at open').toBe(0);
  await shot(page, 'you-04-asks-at-open');

  // A network ask added later, with its change event: it flies in and the count goes to 3.
  await page.evaluate(() => { (window as any).__frames = []; });
  const t0 = Date.now();
  feed.netAsks.push(feed.netAsk(10, BEN, 'registry.npmjs.org'));
  push('network.asksChanged');
  f = await untilFrame(page, (x) => youNode(x)?.count === '3', 6000);
  const countedMs = Date.now() - t0;
  await page.waitForTimeout(1800); // the flight (1.5 s)
  let fs = await frames(page);
  const netFlying = fs.filter((x) => youFlyers(x).length >= 4).length;
  const netRing = fs.some((x) => x.ops.some((o) => o.op === 'stroke' && isScreen(o) && o.style === C.decision && o.lw === 2 && o.items?.some((i) => i.t === 'arc' && i.r > 26)));
  const netLabel = fs.some((x) => texts(x).includes('registry.npmjs.org'));
  r.networkAdded = { count: youNode(f)?.count ?? null, countedMs, flyingFrames: netFlying, arrivalRing: netRing, labelSeen: netLabel };
  expect(youNode(f)?.count, 'a new network ask: 3').toBe('3');
  expect(countedMs, 'on the event, not the 5 s poll').toBeLessThan(3000);
  expect(netFlying, 'the new network ask flies in').toBeGreaterThan(5);
  expect(netRing, 'a ring opens on arrival').toBe(true);
  await shot(page, 'you-05-network-ask-added');

  // A tool ask added later (installing counts too), with its change event: it flies in, 4.
  await page.evaluate(() => { (window as any).__frames = []; });
  feed.toolAsks.push(feed.toolAsk(6, ADA, 'node-22', 'Node.js 22', 'installing'));
  push('tools.asksChanged');
  f = await untilFrame(page, (x) => youNode(x)?.count === '4', 6000);
  await page.waitForTimeout(1800);
  fs = await frames(page);
  r.toolAdded = { count: youNode(f)?.count ?? null, flyingFrames: fs.filter((x) => youFlyers(x).length >= 4).length };
  expect(youNode(f)?.count, 'a new tool ask: 4').toBe('4');
  expect((r.toolAdded as any).flyingFrames, 'the new tool ask flies in').toBeGreaterThan(5);

  // Decided: Ben's network ask allowed, his tool installed: they leave the count (2), nothing flies.
  await page.evaluate(() => { (window as any).__frames = []; });
  feed.netAsks = feed.netAsks.map((a) => (a.id === 10 ? { ...a, state: 'allowed' } : a));
  feed.toolAsks = feed.toolAsks.map((a) => (a.id === 4 ? { ...a, state: 'installed' } : a));
  push('network.asksChanged');
  push('tools.asksChanged');
  f = await untilFrame(page, (x) => youNode(x)?.count === '2', 6000);
  await page.waitForTimeout(1200);
  r.decided = { count: youNode(f)?.count ?? null, flyingFrames: (await frames(page)).filter((x) => youFlyers(x).length >= 4).length };
  expect(youNode(f)?.count, 'the decided ones leave: 2').toBe('2');
  expect((r.decided as any).flyingFrames).toBe(0);

  // Without an event, the slow poll (5 s) picks one up.
  const t1 = Date.now();
  feed.netAsks.push(feed.netAsk(11, ADA, 'github.com'));
  f = await untilFrame(page, (x) => youNode(x)?.count === '3', 9000);
  r.slowPoll = { count: youNode(f)?.count ?? null, ms: Date.now() - t1 };
  expect(youNode(f)?.count, 'the slow poll: 3').toBe('3');
  await shot(page, 'you-06-asks-after-poll');
  result.youAsks = r;
  console.log('YOU-ASKS', JSON.stringify(r));
  expect(errors).toEqual([]);
  await ctx.close();
});

// ---------------------------------------------------------------------------------------------------------------------
// 3c. One need-you list. The header's "N need you" and the You node count the same
// items (questions, network and tool asks, blocked and failed members; a member with a question and a network ask is 2);
// the You node's hover (tap on a phone) lists them; each new item flies in once (its head: a screen-space disc of r 3.4
// in its tone), the node's arrival ring opens once, and the needs-you tone (an oscillator at 660 Hz) sounds once.

const NEED_YOU_SHOTS = path.join(process.env.YAWBLE_TEAM_DOCS || path.resolve(__dirname, '..', '..', 'test-results'), 'monitor-need-you');
const needYouResult: Record<string, unknown> = {};
test.afterAll(() => {
  mkdirSync(NEED_YOU_SHOTS, { recursive: true });
  const file = path.join(NEED_YOU_SHOTS, 'need-you-results.json');
  let old: Record<string, unknown> = {};
  try { old = JSON.parse(readFileSync(file, 'utf8')); } catch { /* none yet */ }
  if (Object.keys(needYouResult).length) writeFileSync(file, JSON.stringify({ ...old, ...needYouResult }, null, 2));
});
async function needYouShot(page: Page, name: string) { mkdirSync(path.join(NEED_YOU_SHOTS, 'screens'), { recursive: true }); await page.screenshot({ path: path.join(NEED_YOU_SHOTS, 'screens', `${name}.png`) }); }

const FLY = [C.decision, C.blocked, C.fail];
/** The heads of the flights to the You node this frame, by colour. */
const youHeads = (f: Frame) => discs(f, (o, a) => isScreen(o) && FLY.includes(o.style) && Math.abs(a.r - 3.4) < 0.01).map((d) => d.style);
/** The You node's arrival rings this frame that have just opened (r 26 to 29). */
const youRingsNew = (f: Frame, y: Pt) => arcsAt(f, y, 27.5, C.decision, 1.5).filter((a) => a.lw === 2).length;
/** How many flights (and arrival rings) started over these frames: each rise in the number drawn. */
function starts(fs: Frame[], count: (f: Frame) => number): number {
  let n = 0, prev = 0;
  for (const f of fs) { const c = count(f); if (c > prev) n += c - prev; prev = c; }
  return n;
}
async function headerNeed(page: Page): Promise<number | null> {
  const t = await page.locator('[data-test=monitor-counts]').innerText();
  const m = /(\d+)\s*need you/.exec(t);
  return m ? Number(m[1]) : null;
}
const tonesNeedYou = (page: Page): Promise<number> => page.evaluate(() => (window as any).__tones.filter((t: any) => t.f === 660).length);

for (const W of [1280, 375]) {
  test(`3c need you at ${W}: header = You node for questions, asks, blocked / failed, a mix; the tip lists them; each new item flies and sounds once`, async ({ browser }) => {
    test.setTimeout(240_000);
    const phone = W < 700;
    const tag = `w${W}`;
    const feed = new Feed();
    seed(feed);
    feed.netAsks = []; feed.toolAsks = [];
    // Waiting at open: DeveloperCleo (the second team) blocked, and TesterBen's question (its row is history).
    feed.attn.set('2/DeveloperCleo', 'blocked');
    feed.attn.set(`1/${BEN}`, 'decision');
    feed.row('decision', BEN, BEN, 12, 90_000, 'Which refund limit?');
    const { page, ctx, errors, push } = await open(browser, { feed, w: W, sound: true });
    const r: Record<string, any> = { steps: [] as any[] };
    const box = await canvasBox(page);
    let you: Pt = { x: box.width - 46, y: Math.max(120, box.height / 2) };
    const mismatches: string[] = [];

    /** Waits until the header and the You node both say n; returns what each said. */
    async function both(n: number, timeout = 9000) {
      const end = Date.now() + timeout;
      let node: string | null = null, head: number | null = null;
      do {
        const f = await lastFrame(page);
        const y = youNode(f);
        if (y) you = { x: y.x, y: y.y };
        node = y?.count ?? null; head = await headerNeed(page);
        if ((n === 0 ? node === null : node === String(n)) && head === n) break;
        await page.waitForTimeout(100);
      } while (Date.now() < end);
      return { node, header: head };
    }
    /** One change: clears the frames and tones' mark, applies it, waits for the count, then for the flights to end. */
    async function step(name: string, change: () => void, n: number, want: { flights: number; tones: number }, settle = 2600) {
      await page.evaluate(() => { (window as any).__frames = []; });
      const tones0 = await tonesNeedYou(page);
      change();
      const said = await both(n);
      await page.waitForTimeout(settle);
      const fs = await frames(page);
      const s = {
        name, want: n, ...said, flights: starts(fs, (f) => youHeads(f).length), flightColors: [...new Set(fs.flatMap(youHeads))],
        rings: starts(fs, (f) => youRingsNew(f, you)), tones: (await tonesNeedYou(page)) - tones0, frames: fs.length,
      };
      r.steps.push(s);
      if (s.node !== (n === 0 ? null : String(n)) || s.header !== n) mismatches.push(`${name}: header ${s.header}, node ${s.node}, want ${n}`);
      expect.soft(s.header, `${name}: header`).toBe(n);
      expect.soft(s.node, `${name}: You node`).toBe(n === 0 ? null : String(n));
      expect.soft(s.flights, `${name}: flights`).toBe(want.flights);
      expect.soft(s.rings, `${name}: arrival rings`).toBe(want.flights);
      expect.soft(s.tones, `${name}: needs-you tones`).toBe(want.tones);
      return s;
    }
    /** The You node's tip: hovered (desktop) or tapped (phone). */
    async function tip() {
      const b = await canvasBox(page);
      if (phone) {
        if (!(await page.locator('[data-test=monitor-you-tip]').count())) {
          await page.touchscreen.tap(b.x + you.x, b.y + you.y);
          if (process.env.DBG_YOU) {
            await page.waitForTimeout(200);
            const y = youNode(await lastFrame(page));
            console.log('TAP you at', you.x.toFixed(1), you.y.toFixed(1), 'node now', y?.x.toFixed(1), y?.y.toFixed(1), 'tip', await page.locator('[data-test=monitor-you-tip]').count(), 'board', await page.locator('[data-test=board]').count(), 'hit', await page.evaluate((q) => (document.elementFromPoint(q.x, q.y) as HTMLElement | null)?.dataset?.test ?? (document.elementFromPoint(q.x, q.y) as HTMLElement | null)?.className, { x: b.x + you.x, y: b.y + you.y }));
            await needYouShot(page, `${tag}-dbg-tap-${Date.now() % 100000}`);
          }
        }
      } else { await page.mouse.move(b.x + you.x, b.y + you.y); }
      await expect(page.locator('[data-test=monitor-you-tip]')).toBeVisible({ timeout: 4000 });
      await page.waitForTimeout(300);
      // The panel sits beside the You node, centred on it (translateY(-50%) at the node's y).
      const tb = (await page.locator('[data-test=monitor-you-tip]').boundingBox())!;
      const cb = await canvasBox(page);
      const offsetY = Math.round(tb.y + tb.height / 2 - (cb.y + you.y));
      expect.soft(Math.abs(offsetY), `the panel is centred on the You node (canvas ${Math.round(cb.height)} px high, node at ${you.y})`).toBeLessThan(30);
      return {
        offsetY, canvasH: Math.round(cb.height), nodeY: you.y,
        summary: await page.locator('[data-test=monitor-you-summary]').innerText(),
        items: (await page.locator('[data-test=monitor-you-item]').allInnerTexts()).map((s) => s.replace(/\s+/g, ' ').trim()),
        kinds: await page.locator('[data-test=monitor-you-item]').evaluateAll((els) => els.map((e) => e.getAttribute('data-kind'))),
        more: (await page.locator('[data-test=monitor-you-tip] .dim').allInnerTexts()).find((s) => /^\+\d+ more$/.test(s.trim())) ?? null,
        button: await page.locator('[data-test=monitor-you-open]').count(),
      };
    }
    async function untip() {
      if (phone) {
        const b = await canvasBox(page);
        // The bare wall at its right edge, above or below the You node (the graph keeps to the middle), on the canvas
        // itself and away from the open panel (a tap beside its button is snapped onto it by Chrome's touch adjustment).
        const t = (await page.locator('[data-test=monitor-you-tip]').boundingBox())!;
        const tries = [-80, 80, -120, 120, -50, 50].map((d) => ({ x: b.x + b.width - 14, y: b.y + you.y + d }));
        let at = tries[0], onCanvas: string | null = null;
        for (const p of tries) {
          if (p.y < b.y + 4 || p.y > b.y + b.height - 4 || (p.y > t.y - 30 && p.y < t.y + t.height + 30 && p.x > t.x - 30 && p.x < t.x + t.width + 30)) continue;
          const hit = await page.evaluate((q) => (document.elementFromPoint(q.x, q.y) as HTMLElement | null)?.dataset?.test ?? null, p);
          if (hit === 'monitor-canvas') { at = p; onCanvas = hit; break; }
        }
        await page.touchscreen.tap(at.x, at.y);
        await page.waitForTimeout(300);
        return { tappedOn: onCanvas, tipAfter: await page.locator('[data-test=monitor-you-tip]').count() };
      }
      await unhover(page);
      await page.waitForTimeout(150);
      return { tappedOn: null, tipAfter: await page.locator('[data-test=monitor-you-tip]').count() };
    }

    // Open: what waits is counted (2), nothing flies in, nothing sounds.
    await until(page, (x) => !!youNode(x), Boolean);
    const atOpen = await both(2, 12_000);
    await page.evaluate(() => { (window as any).__frames = []; });
    await page.waitForTimeout(2500);
    let fs = await frames(page);
    r.atOpen = { ...atOpen, flights: starts(fs, (f) => youHeads(f).length), tones: await tonesNeedYou(page), soundOn: await page.evaluate(() => localStorage.getItem('yawble.monitor.sound')) };
    expect(r.atOpen.header).toBe(2);
    expect(r.atOpen.node).toBe('2');
    expect(r.atOpen.flights, 'nothing flies for those waiting at open').toBe(0);
    expect(r.atOpen.tones, 'nothing sounds for those waiting at open').toBe(0);
    expect(r.atOpen.soundOn).toBe('on');
    r.atOpen.tip = await tip();
    expect(r.atOpen.tip.summary).toBe('1 question, 1 blocked');
    if (process.env.DBG_YOU) { console.log('AFTER TIP board', await page.locator('[data-test=board]').count(), 'tip', await page.locator('[data-test=monitor-you-tip]').count()); await needYouShot(page, `${tag}-dbg-after-tip`); }
    r.atOpen.untip = await untip();
    if (process.env.DBG_YOU) { console.log('AFTER UNTIP', JSON.stringify(r.atOpen.untip), 'board', await page.locator('[data-test=board]').count()); await needYouShot(page, `${tag}-dbg-after-untip`); }
    expect(r.atOpen.untip.tipAfter, 'a tap elsewhere / leaving hides the tip').toBe(0);
    // Both resolve: 0, nothing flies.
    await step('open items resolve', () => { feed.attn.delete('2/DeveloperCleo'); feed.attn.delete(`1/${BEN}`); }, 0, { flights: 0, tones: 0 }, 1500);
    await needYouShot(page, `${tag}-01-nothing`);

    // Only blocked / failed (attention only, no feed rows): each flies in once in its tone, one tone each.
    let s = await step('Ada failed', () => feed.attn.set(`1/${ADA}`, 'failed'), 1, { flights: 1, tones: 1 });
    expect(s.flightColors).toEqual([C.fail]);
    s = await step('Cleo blocked', () => feed.attn.set('2/DeveloperCleo', 'blocked'), 2, { flights: 1, tones: 1 });
    expect(s.flightColors).toEqual([C.blocked]);
    r.onlyBlockedFailed = await tip();
    expect(r.onlyBlockedFailed.summary).toBe('1 blocked, 1 failed');
    expect(r.onlyBlockedFailed.kinds).toEqual(['blocked', 'failed']);
    expect(r.onlyBlockedFailed.items[0]).toContain(`${TEAM2} / Developer Cleo`);
    expect(r.onlyBlockedFailed.items[1]).toContain(`${TEAM} / Developer Ada`);
    await needYouShot(page, `${tag}-02-blocked-failed-tip`);
    await untip();
    // Repeated polls fire nothing new.
    await step('polls only (blocked / failed)', () => {}, 2, { flights: 0, tones: 0 }, 3000);
    await step('blocked / failed resolve', () => { feed.attn.delete(`1/${ADA}`); feed.attn.delete('2/DeveloperCleo'); }, 0, { flights: 0, tones: 0 }, 1500);

    // Only questions: Ben's with its row and his attention (one item), the other team's Manager by attention alone.
    await step('Ben asks (row + attention)', () => { feed.row('decision', BEN, BEN, 12, 0, 'Which refund limit, 50 or 100?'); feed.attn.set(`1/${BEN}`, 'decision'); }, 1, { flights: 1, tones: 1 });
    await step('Manager (team 2) asks', () => feed.attn.set('2/Manager', 'decision'), 2, { flights: 1, tones: 1 });
    await step('Ben asks again while open (the same item)', () => feed.row('decision', BEN, BEN, 12, 0, 'And the currency?'), 2, { flights: 0, tones: 0 });
    r.onlyQuestions = await tip();
    expect(r.onlyQuestions.summary).toBe('2 questions');
    expect(r.onlyQuestions.kinds).toEqual(['question', 'question']);
    await needYouShot(page, `${tag}-03-questions-tip`);
    await untip();
    await step('questions resolve', () => { feed.attn.delete(`1/${BEN}`); feed.attn.delete('2/Manager'); }, 0, { flights: 0, tones: 0 }, 1500);

    // Only asks: Ada's two asks on one host (one item), Ben's tool ask; repeated asksChanged events fire nothing new.
    await step('Ada network ask (2 asks, 1 host)', () => { feed.netAsks!.push(feed.netAsk(7, ADA, 'api.nuget.org'), feed.netAsk(8, ADA, 'api.nuget.org')); push('network.asksChanged'); }, 1, { flights: 1, tones: 1 });
    await step('Ben tool ask', () => { feed.toolAsks!.push(feed.toolAsk(4, BEN, 'dotnet-sdk-10', '.NET SDK 10')); push('tools.asksChanged'); }, 2, { flights: 1, tones: 1 });
    await step('asksChanged x3, nothing new', () => { push('network.asksChanged'); push('tools.asksChanged'); push('network.asksChanged'); }, 2, { flights: 0, tones: 0 });
    r.onlyAsks = await tip();
    expect(r.onlyAsks.summary).toBe('1 network ask, 1 tool ask');
    expect(r.onlyAsks.items[0]).toContain(`${TEAM} / Developer Ada`);
    expect(r.onlyAsks.items[0]).toContain('api.nuget.org');
    expect(r.onlyAsks.items[1]).toContain('.NET SDK 10');
    await needYouShot(page, `${tag}-04-asks-tip`);
    await untip();

    // A mix: Ada now has a question too (her network ask stays: 2 for her), Ben blocked, Cleo failed. 1 + 1 + 1 + 1 + 1 = 5.
    await step('Ada asks a question (she has a network ask too)', () => feed.attn.set(`1/${ADA}`, 'decision'), 3, { flights: 1, tones: 1 });
    await step('Ben blocked', () => feed.attn.set(`1/${BEN}`, 'blocked'), 4, { flights: 1, tones: 1 });
    await step('Cleo failed', () => feed.attn.set('2/DeveloperCleo', 'failed'), 5, { flights: 1, tones: 1 });
    r.mix = await tip();
    expect(r.mix.summary).toBe('1 question, 1 network ask, 1 tool ask, 1 blocked, 1 failed');
    expect(r.mix.kinds).toEqual(['question', 'network', 'tool', 'blocked', 'failed']);
    expect(r.mix.items.filter((t: string) => t.includes(`${TEAM} / Developer Ada`)).length, 'Ada: a question and a network ask, 2 lines').toBe(2);
    expect(r.mix.more).toBeNull();
    await needYouShot(page, `${tag}-05-mix-tip`);
    await untip();
    await step('polls only (mix)', () => {}, 5, { flights: 0, tones: 0 }, 3000);

    // Resolving, one at a time: the count goes down, nothing flies.
    await step('Ada answered (network ask stays)', () => feed.attn.delete(`1/${ADA}`), 4, { flights: 0, tones: 0 }, 1200);
    await step('Ada network ask allowed', () => { feed.netAsks = feed.netAsks!.map((a) => ({ ...a, state: 'allowed' })); push('network.asksChanged'); }, 3, { flights: 0, tones: 0 }, 1200);
    await step('tool installed', () => { feed.toolAsks = feed.toolAsks!.map((a) => ({ ...a, state: 'installed' })); push('tools.asksChanged'); }, 2, { flights: 0, tones: 0 }, 1200);
    await step('Ben unblocked', () => feed.attn.delete(`1/${BEN}`), 1, { flights: 0, tones: 0 }, 1200);
    // A resolved item that returns is new again.
    await step('Ben blocked again (returns)', () => feed.attn.set(`1/${BEN}`, 'blocked'), 2, { flights: 1, tones: 1 });
    await needYouShot(page, `${tag}-06-resolved-and-returned`);

    // More than ten: 11 network asks on distinct hosts (13 in all): ten lines, then "+3 more".
    await step('11 network asks at once', () => {
      for (let i = 0; i < 11; i++) feed.netAsks!.push(feed.netAsk(100 + i, i % 2 ? ADA : BEN, `h${i}.example${i}.com`));
      push('network.asksChanged');
    }, 13, { flights: 11, tones: 1 });
    r.many = await tip();
    expect(r.many.items.length).toBe(10);
    expect(r.many.more?.trim()).toBe('+3 more');
    expect(r.many.summary).toBe('11 network asks, 1 blocked, 1 failed');
    await needYouShot(page, `${tag}-07-more-than-ten`);

    // The wall changes size: a shorter window, then the panel shown again (hover / tap) follows the You node;
    // then a taller window: desktop re-hovers, on the phone the tapped panel stays open through the resize and follows.
    const H0 = page.viewportSize()!.height;
    await untip();
    // 375 x 640: the 12 asks' banners leave a wall of about 110 px; the You node stays on it, clear of the
    // controls row with Legend, so the tap below reaches it (before, at y 120, it sat under Legend, out of reach).
    await page.setViewportSize({ width: W, height: phone ? 640 : 600 });
    await page.waitForTimeout(1200);
    let yn = youNode(await lastFrame(page));
    if (yn) you = { x: yn.x, y: yn.y };
    r.resized = await tip();
    await needYouShot(page, `${tag}-08-after-resize-tip`);
    if (!phone) await unhover(page);
    await page.setViewportSize({ width: W, height: H0 });
    await page.waitForTimeout(1200);
    yn = youNode(await lastFrame(page));
    if (yn) you = { x: yn.x, y: yn.y };
    const stayedOpen = (await page.locator('[data-test=monitor-you-tip]').count()) > 0;
    if (phone) expect.soft(stayedOpen, 'the tapped panel stays open through the resize').toBe(true);
    r.grown = { ...(await tip()), stayedOpen };
    await needYouShot(page, `${tag}-09-after-grow-tip`);
    if (!phone) await unhover(page);

    // Opening Needs you: a desktop click on the You node; a phone tap shows the panel with its button, which opens it.
    if (phone) {
      expect(r.many.button, 'the phone panel has its button').toBe(1);
      await page.locator('[data-test=monitor-you-open]').click();
    } else {
      expect(r.many.button).toBe(0);
      const b = await canvasBox(page);
      await page.mouse.move(b.x + you.x, b.y + you.y);
      await page.mouse.down(); await page.mouse.up();
    }
    await expect(page.locator('[data-test=board]')).toBeVisible({ timeout: 5000 });
    r.opened = 'board';
    r.mismatches = mismatches;
    needYouResult[tag] = r;
    console.log(`need-you-${tag}`, JSON.stringify(r));
    expect(mismatches).toEqual([]);
    expect(errors).toEqual([]);
    await ctx.close();
  });
}

// At 375 x 640 with 12 asks the banners leave a short wall (about 110 px with 3c's members
// blocked and failed too, 169 px with the asks alone; 600, 580 and 556 make it 145, 133 and 119); the You node stays on it, clear of the
// controls row with Legend, and a tap on it opens its panel (whose button opens Needs you).
for (const H of [640, 600, 580, 556, 546, 530])
test(`3d You node on a short phone wall (375 x ${H}, 12 asks): inside the wall, clear of Legend, a tap opens its panel`, async ({ browser }) => {
  const feed = new Feed();
  seed(feed);
  feed.toolAsks = [];
  feed.netAsks = Array.from({ length: 12 }, (_, i) => feed.netAsk(200 + i, i % 2 ? ADA : BEN, `s${i}.example${i}.com`));
  const { page, ctx, errors } = await open(browser, { feed, w: 375, h: H });
  const you = await until(page, youNode, (y) => !!y && y.count === '12', 15_000);
  await page.waitForTimeout(600);
  const f = youNode(await lastFrame(page))!;
  const cb = await canvasBox(page);
  const lb = (await page.locator('[data-test=monitor-legend-toggle]').boundingBox())!;
  const rb = (await page.locator('.controls').boundingBox())!;
  const node = { x: cb.x + f.x, y: cb.y + f.y, r: 24 };
  const clear = (b: { x: number; y: number; width: number; height: number }) =>
    Math.hypot(Math.max(b.x - node.x, 0, node.x - (b.x + b.width)), Math.max(b.y - node.y, 0, node.y - (b.y + b.height))) >= node.r;
  const r: Record<string, unknown> = {
    wallH: Math.round(cb.height), node: { x: f.x, y: f.y }, count: you?.count ?? null,
    legend: { x: Math.round(lb.x - cb.x), y: Math.round(lb.y - cb.y), w: Math.round(lb.width), h: Math.round(lb.height) },
  };
  console.log('short-wall-at', H, JSON.stringify({ ...r, controls: { y: Math.round(rb.y - cb.y), h: Math.round(rb.height) } }));
  await shot(page, `you-375x${H}-before-tap`);
  // Short enough that the old place (y 120) lay on the controls row (its top 52 px above the wall's bottom).
  expect(cb.height, 'the 12 asks leave a short wall').toBeLessThan(120 + 24 + 52);
  expect(f.y - 24, 'the node is inside the wall (top)').toBeGreaterThanOrEqual(0);
  expect(f.y + 24, 'the node is inside the wall (bottom)').toBeLessThanOrEqual(cb.height);
  expect(f.x + 24).toBeLessThanOrEqual(cb.width);
  expect.soft(clear(lb), 'the node is clear of the Legend button').toBe(true);
  // A 48 px node can clear the controls row only if the wall is at least node height + the row's
  // distance from the wall's bottom (~48 + 56 = 104 px). Below that no placement can, so there the node must instead
  // keep its centre off the row (with inside the wall, clear of Legend and the tap, checked above and below).
  const rowFromBottom = cb.y + cb.height - rb.y;
  r.rowFromBottom = Math.round(rowFromBottom);
  if (cb.height >= 2 * node.r + rowFromBottom) {
    expect.soft(clear(rb), 'the node is clear of the controls row').toBe(true);
  } else {
    r.controlsCheck = 'relaxed: centre off the row';
    expect(clear(lb), 'too short to clear the row: the node is still clear of Legend').toBe(true);
    const onRow = node.x >= rb.x && node.x <= rb.x + rb.width && node.y >= rb.y && node.y <= rb.y + rb.height;
    expect(onRow, "too short to clear the row: the node's centre is off the controls row").toBe(false);
  }
  // What is under the node's centre is the wall itself, not a control.
  r.hit = await page.evaluate((q) => (document.elementFromPoint(q.x, q.y) as HTMLElement | null)?.dataset?.test ?? null, node);
  expect(r.hit).toBe('monitor-canvas');
  await page.touchscreen.tap(node.x, node.y);
  await expect(page.locator('[data-test=monitor-you-tip]')).toBeVisible({ timeout: 4000 });
  r.summary = await page.locator('[data-test=monitor-you-summary]').innerText();
  expect(r.summary).toBe('12 network asks');
  await shot(page, `you-375x${H}-12-asks`);
  // The panel's button opens Needs you.
  await page.locator('[data-test=monitor-you-open]').click();
  await expect(page.locator('[data-test=board]')).toBeVisible({ timeout: 5000 });
  result[`3d-short-wall-${H}`] = r;
  console.log('short-wall', JSON.stringify(r));
  expect(errors).toEqual([]);
  await ctx.close();
});

test('4 cards: an instruction carries "#card", the hand-back comes home with a check, a failed run with a cross', async ({ browser }) => {
  const feed = new Feed();
  seed(feed);
  const { page, ctx, errors } = await open(browser, { feed });
  await until(page, (x) => !!memberAt(x, ADA), Boolean);
  await page.waitForTimeout(1500);
  const r: Record<string, unknown> = {};
  const label = async (text: string) => {
    const end = Date.now() + 4000;
    while (Date.now() < end) {
      for (const x of await frames(page)) { const o = x.ops.find((k) => k.op === 'text' && k.text === text); if (o) return { style: o.style, frame: x }; }
      await page.waitForTimeout(100);
    }
    return null;
  };
  await page.evaluate(() => { (window as any).__frames = []; });
  feed.row('instruction', ADA, 'Manager', 10, 0, 'Card 4373 (workflow nodes): do it');
  const out = await label('#4373');
  r.instruction = out && { style: out.style, heads: cometHeads(out.frame).length };
  expect(out?.style, 'the card number rides the comet in the tell colour').toBe(C.tell);
  await shot(page, 'cards-01-instruction');
  await page.waitForTimeout(1500);
  await page.evaluate(() => { (window as any).__frames = []; });
  feed.row('handback', ADA, ADA, 10, 0, 'Done');
  const back = await label('#4373 ✓');
  const mark = back && back.frame.ops.filter((o) => o.op === 'fill' && o.style === 'rgba(7, 11, 20, 0.92)' && o.items?.some((i) => i.t === 'arc' && i.r === 7)).length;
  r.handback = back && { style: back.style, markDiscs: mark };
  expect(back?.style, 'the hand-back in the hand-back colour').toBe(C.handback);
  expect(mark, 'the check rides a small disc').toBeGreaterThan(0);
  await page.waitForTimeout(400);
  await shot(page, 'cards-02-handback-check');
  await page.waitForTimeout(1200);
  feed.row('instruction', BEN, 'Manager', 12, 0, '[card 4374] test it');
  await label('#4374');
  await page.waitForTimeout(1300);
  feed.row('failed', BEN, BEN, 12, 0, 'Run failed');
  const cross = await label('#4374 ✗');
  r.failed = cross && { style: cross.style };
  expect(cross?.style, 'a failed run comes home red with a cross').toBe(C.fail);
  await page.waitForTimeout(1300);
  feed.row('instruction', ADA, 'Manager', 10, 0, 'Card 4376: more');
  await label('#4376');
  await page.waitForTimeout(1300);
  feed.row('blocked', ADA, ADA, 10, 0, 'Blocked on a host');
  const blk = await label('#4376 ✗');
  r.blocked = blk && { style: blk.style };
  expect(blk?.style, 'a blocked run: a cross in the blocked colour').toBe(C.blocked);
  result.cards = r;
  expect(errors).toEqual([]);
  await ctx.close();
});

// ---------------------------------------------------------------------------------------------------------------------
// 5. Focus

test('5 focus: a hub click zooms and dims the rest, its code events only; Esc, the empty wall and the chip leave', async ({ browser }) => {
  const feed = new Feed();
  seed(feed);
  feed.row('instruction', 'DeveloperCleo', 'Manager', 20, 100_000, 'Card 9: checkout', TEAM2);
  for (let i = 0; i < 6; i++) { feed.step(ADA, 'edit', `src/refund/f${i}.ts`, 90_000 - i); feed.step('DeveloperCleo', 'edit', `web/cart/c${i}.ts`, 90_000 - i, TEAM2); }
  const { page, ctx, errors } = await open(browser, { feed });
  await until(page, (x) => fileDots(x).length >= 12, Boolean, 12_000);
  await page.waitForTimeout(3000);
  await unhover(page);
  const r: Record<string, unknown> = {};
  const pillAlpha = (x: Frame, t: string) => x.ops.find((o) => o.op === 'text' && o.text === t && o.style === C.ink)?.alpha;
  let f = await lastFrame(page);
  const k0 = hubAt(f)!.op.m[0];
  r.before = { k: k0, payments: pillAlpha(f, TEAM), other: pillAlpha(f, TEAM2) };
  expect(pillAlpha(f, TEAM2)).toBe(1);
  await shot(page, 'focus-01-off');

  // The fit with two small maps (finding: zoomed far out, the hub near or past the left edge): its zoom and hub over 6 s.
  const fitTrace: { k: number; hubX: number; hubY: number }[] = [];
  for (let i = 0; i < 12; i++) { const x = await lastFrame(page); const h = hubAt(x)!; const s = screen(h.op, h.x, h.y); fitTrace.push({ k: +h.op.m[0].toFixed(3), hubX: Math.round(s.x), hubY: Math.round(s.y) }); await page.waitForTimeout(500); }
  r.fitWithMaps = fitTrace;
  // Focus payments.
  r.focusTries = await focusTeam(page);
  await expect(page.locator('[data-test=monitor-focus]')).toContainText(TEAM);
  await page.waitForTimeout(2500);
  await unhover(page);
  f = await lastFrame(page);
  const k1 = hubAt(f)!.op.m[0];
  const other = hubAt(f, TEAM2);
  r.focused = { k: k1, payments: pillAlpha(f, TEAM), other: pillAlpha(f, TEAM2) };
  expect(k1, 'zoomed in').toBeGreaterThan(k0 * 1.15);
  expect(pillAlpha(f, TEAM)).toBe(1);
  expect(pillAlpha(f, TEAM2), 'the other team dimmed (0.16)').toBeCloseTo(0.16, 2);
  // The other team's files dim too.
  const otherDots = fileDots(f).filter((d) => other && Math.hypot(d.x - other.x, d.y - other.y) < 400);
  r.otherFileAlpha = otherDots.map((d) => +d.alpha.toFixed(3)).slice(0, 4);
  await shot(page, 'focus-02-on');

  // Code events: commits for both teams; only payments' are drawn. Merges likewise.
  await page.evaluate(() => { (window as any).__frames = []; });
  feed.event({ kind: 'commit', team: TEAM, member: ADA, sha: 'a'.repeat(40), message: 'Refund commit', files: [{ path: 'src/refund/f0.ts', status: 'M' }] });
  feed.event({ kind: 'commit', team: TEAM2, member: 'DeveloperCleo', sha: 'b'.repeat(40), message: 'Cart commit', files: [{ path: 'web/cart/c0.ts', status: 'M' }] });
  feed.event({ kind: 'merge-team', team: TEAM2, member: null, sha: 'c'.repeat(40), message: 'Merge', files: [] });
  // The instruction text has no "#10": focused, the zoom passes 0.5 and payments' own workflow #10 is labelled "#10",
  // so a text with "#10" in it would match that label.
  feed.row('instruction', 'DeveloperCleo', 'Manager', 20, 0, 'Card 77: other team comet', TEAM2);
  await page.waitForTimeout(3500);
  const fs = await frames(page);
  const dotsByTeam = (x: Frame) => {
    const a = hubAt(x), b = hubAt(x, TEAM2);
    const ds = commitDots(x);
    return { mine: ds.filter((d) => a && (!b || Math.hypot(d.x - a.x, d.y - a.y) < Math.hypot(d.x - b.x, d.y - b.y))).length, other: ds.filter((d) => b && a && Math.hypot(d.x - b.x, d.y - b.y) < Math.hypot(d.x - a.x, d.y - a.y)).length };
  };
  const mine = Math.max(...fs.map((x) => dotsByTeam(x).mine)), oth = Math.max(...fs.map((x) => dotsByTeam(x).other));
  const otherRings = Math.max(0, ...fs.map((x) => { const b = hubAt(x, TEAM2); return b ? x.ops.filter((o) => o.op === 'stroke' && o.style === C.handback && o.items?.some((i) => i.t === 'arc' && near(i, b, 0.5) && i.r >= 10 && full(i))).length : 0; }));
  const otherComet = Math.max(0, ...fs.map((x) => texts(x).filter((t) => t === '#77').length));
  r.codeEvents = { payCommitDots: mine, otherCommitDots: oth, otherMergeRings: otherRings, otherCometLabel: otherComet };
  expect(mine, "payments' commit drawn").toBeGreaterThan(0);
  expect(oth, "the other team's commit not drawn").toBe(0);
  expect(otherRings, "the other team's merge not drawn").toBe(0);
  expect(otherComet, "the other team's comet not drawn").toBe(0);

  // Esc leaves.
  await page.keyboard.press('Escape');
  await expect(page.locator('[data-test=monitor-focus]')).toHaveCount(0);
  await page.waitForTimeout(400);
  f = await lastFrame(page);
  r.afterEsc = { other: pillAlpha(f, TEAM2) };
  expect(pillAlpha(f, TEAM2)).toBe(1);
  // The chip's × leaves.
  await focusTeam(page);
  await expect(page.locator('[data-test=monitor-focus]')).toBeVisible();
  await page.locator('[data-test=monitor-focus-leave]').click();
  await expect(page.locator('[data-test=monitor-focus]')).toHaveCount(0);
  // A click on the empty wall leaves: a point far from anything drawn.
  await page.waitForTimeout(2000);
  await focusTeam(page);
  await expect(page.locator('[data-test=monitor-focus]')).toBeVisible();
  await page.waitForTimeout(2500);
  f = await lastFrame(page);
  const box = await canvasBox(page);
  const pts = f.ops.filter((o) => !isScreen(o)).flatMap((o) => (o.op === 'text' ? [screen(o, o.x!, o.y!)] : (o.items ?? []).filter((i) => i.x !== undefined).map((i) => screen(o, i.x, i.y))));
  let empty: Pt | null = null;
  for (let yy = 140; yy < box.height - 60 && !empty; yy += 20) for (let xx = 30; xx < box.width - 120 && !empty; xx += 20) if (pts.every((p) => Math.hypot(p.x - xx, p.y - yy) > 60)) empty = { x: xx, y: yy };
  r.emptyPoint = empty;
  expect(empty).not.toBeNull();
  await page.mouse.click(box.x + empty!.x, box.y + empty!.y);
  await expect(page.locator('[data-test=monitor-focus]')).toHaveCount(0);
  result.focus = r;
  expect(errors).toEqual([]);
  await ctx.close();
});

// ---------------------------------------------------------------------------------------------------------------------
// 6. Density

test('6 density: calm / normal / lively cap the step icons, the comets, the map nodes (72 / 240) and the commits per team (3 / 8)', async ({ browser }) => {
  test.setTimeout(240_000);
  const feed = new Feed();
  const teams = [TEAM, TEAM2, 'team-3', 'team-4', 'team-5'];
  const who = (t: string) => (t === TEAM ? ADA : t === TEAM2 ? 'DeveloperCleo' : 'DeveloperZed');
  for (const t of teams) {
    feed.row('instruction', who(t), 'Manager', 10, 300_000, 'Busy', t);
    for (let i = 0; i < 64; i++) feed.step(who(t), 'edit', `src/area${i % 8}/file${i}.ts`, 240_000 - i * 50, t);
  }
  const { page, ctx, errors } = await open(browser, { feed, extraTeams: 3 });
  await until(page, (x) => fileDots(x).length > 0, Boolean, 15_000);
  await page.waitForTimeout(4000);
  await unhover(page);
  const r: Record<string, any> = {};
  await page.locator('[data-test=monitor-layout-toggle]').click(); // the density sits in the cog's settings panel
  expect(await page.locator('[data-test=monitor-density-lively]').first().getAttribute('aria-pressed'), 'lively by default').toBe('true');
  let shaN = 0;
  for (const level of ['calm', 'normal', 'lively'] as const) {
    await page.locator(`[data-test=monitor-density-${level}]`).first().click();
    await page.waitForTimeout(1500);
    const map = await most(page, (x) => fileDots(x).length, 1500);
    // Six steps at once on Ada: the step icons on her.
    await page.evaluate(() => { (window as any).__frames = []; });
    for (let i = 0; i < 6; i++) feed.step(ADA, 'read', `docs/r${i}.md`);
    const steps = await most(page, (x) => badges(x).length, 3000);
    // Twenty instructions at once: the comets.
    await page.waitForTimeout(800);
    for (let i = 0; i < 20; i++) feed.row('instruction', who(teams[i % 5]!), 'Manager', 10, 0, `Card ${500 + i}`, teams[i % 5]!);
    const comets = await most(page, (x) => cometHeads(x).length, 3000); // the next poll (1.2 s), then a comet's 1.1 s
    // Ten commits for payments at once: its commit dots.
    await page.waitForTimeout(800);
    const callsBefore = feed.calls.length;
    const pushedAt = Date.now();
    for (let i = 0; i < 10; i++) feed.event({ kind: 'commit', team: TEAM, member: ADA, sha: (++shaN).toString(16).padStart(40, '0'), message: `c${shaN}`, files: [{ path: `src/area1/file${i}.ts`, status: 'M' }] });
    // Against test 6's flake: sampled for 8 s, keeping when the dots first show and what the old 3 s window saw.
    const trace: { ms: number; dots: number }[] = [];
    while (Date.now() - pushedAt < 8000) {
      let best = 0;
      for (const x of await recent(page)) best = Math.max(best, commitDots(x).length);
      trace.push({ ms: Date.now() - pushedAt, dots: best });
      await page.waitForTimeout(120);
    }
    const commits = Math.max(0, ...trace.map((t) => t.dots));
    const in3s = Math.max(0, ...trace.filter((t) => t.ms <= 3000).map((t) => t.dots));
    const firstDotMs = trace.find((t) => t.dots > 0)?.ms ?? null;
    const codeReads = feed.calls.slice(callsBefore).filter((c) => c.method === 'monitor.codeEvents').length;
    r[level] = { mapFileNodes: map, stepIconsOnAda: steps, cometHeads: comets, paymentsCommitDots: commits, commitDotsIn3s: in3s, firstCommitDotMs: firstDotMs, codeReadsIn8s: codeReads, stored: await page.evaluate(() => localStorage.getItem('yawble.monitor.density')) };
    await shot(page, `density-${level}`);
    await page.waitForTimeout(12_500); // the commits fade (12 s) before the next level
  }
  result.density = r;
  console.log('DENSITY', JSON.stringify(r));
  expect(r.calm.mapFileNodes, 'calm: at most 72 map nodes').toBeLessThanOrEqual(72);
  expect(r.normal.mapFileNodes, 'normal: more, at most 240').toBeGreaterThan(72);
  expect(r.normal.mapFileNodes).toBeLessThanOrEqual(240);
  expect(r.lively.mapFileNodes).toBeLessThanOrEqual(240);
  expect(r.calm.stepIconsOnAda).toBe(1);
  expect(r.normal.stepIconsOnAda).toBe(3);
  expect(r.lively.stepIconsOnAda).toBe(4);
  expect(r.calm.cometHeads, 'calm: at most 12 effects a frame').toBeLessThanOrEqual(12);
  expect(r.normal.cometHeads).toBeGreaterThan(12);
  expect(r.calm.paymentsCommitDots).toBeLessThanOrEqual(3);
  expect(r.normal.paymentsCommitDots).toBe(8);
  expect(r.lively.paymentsCommitDots).toBe(8);
  expect(r.lively.stored).toBe('lively');
  // The dots show 2.4-3.0 s after the commits (a 1.2 s poll, then their entry); the old 3 s window missed one
  // at 3033 ms. Sampled for 8 s now; they must still show within 6 s.
  for (const level of ['calm', 'normal', 'lively'] as const) expect(r[level].firstCommitDotMs, `${level}: commit dots within 6 s`).toBeLessThanOrEqual(6000);
  expect(errors).toEqual([]);
  await ctx.close();
});

// ---------------------------------------------------------------------------------------------------------------------
// 7. Reduced motion

test('7 reduced motion: comets are still lines with their label, no pulse, no travel, steady rings', async ({ browser }) => {
  const feed = new Feed();
  seed(feed);
  feed.attn.set(`1/${BEN}`, 'decision');
  const { page, ctx, errors } = await open(browser, { feed, reduced: true });
  await until(page, (x) => !!memberAt(x, ADA), Boolean);
  await page.waitForTimeout(2000);
  const r: Record<string, unknown> = {};
  await page.evaluate(() => { (window as any).__frames = []; });
  for (let i = 0; i < 30; i++) feed.step(ADA, 'read', `src/x${i}.ts`);
  feed.row('instruction', BEN, 'Manager', 12, 0, 'Card 4377: still');
  feed.row('decision', ADA, ADA, 10, 0, 'A question');
  feed.event({ kind: 'commit', team: TEAM, member: ADA, sha: 'd'.repeat(40), message: 'Still commit', files: [{ path: 'src/x1.ts', status: 'M' }] });
  feed.event({ kind: 'landed', team: TEAM, member: null, sha: 'e'.repeat(40), message: 'Landed', files: [] });
  await page.waitForTimeout(2500);
  const fs = await frames(page);
  const radii = fs.map((x) => bodyR(x, ADA)).filter((x): x is number => x !== null);
  const heads = Math.max(0, ...fs.map((x) => cometHeads(x).length));
  const still = fs.filter((x) => x.ops.some((o) => o.op === 'stroke' && o.dash && o.style === C.tell && o.items?.some((i) => i.t === 'q'))).length;
  const label = fs.some((x) => texts(x).includes('#4377'));
  const flyers = Math.max(0, ...fs.map((x) => youFlyers(x).length));
  const youLine = fs.some((x) => x.ops.some((o) => o.op === 'stroke' && isScreen(o) && o.dash && o.style === C.decision));
  const commitAt = fs.map((x) => { const h = hubAt(x); const d = commitDots(x)[0]; return h && d ? Math.hypot(d.x - h.x, d.y - h.y) : null; }).filter((x): x is number => x !== null);
  const landedRings = Math.max(0, ...fs.map((x) => { const h = hubAt(x); return h ? x.ops.filter((o) => o.op === 'stroke' && o.style === C.done && o.lw === 3 && o.items?.some((i) => i.t === 'arc' && near(i, h, 0.5))).length : 0; }));
  // Ada runs: her three state arcs start at the same angle every frame.
  const starts = new Set(fs.map((x) => { const a = memberAt(x, ADA); return a ? arcsAt(x, a, a.r + 5).filter((i) => !full(i)).map((i) => i.a0.toFixed(3)).sort().join(',') : ''; }).filter(Boolean));
  r.reduced = { bodyRadius: { min: Math.min(...radii), max: Math.max(...radii) }, cometHeads: heads, stillLineFrames: still, label, youFlyers: flyers, youStillLine: youLine, commitDistFromHub: { first: commitAt[0], last: commitAt.at(-1) }, landedRings, runningRingStarts: [...starts] };
  result.reduced = r.reduced;
  expect(Math.max(...radii) - Math.min(...radii), 'no pulse').toBeLessThan(0.001);
  expect(heads, 'no comet travels').toBe(0);
  expect(still, 'a still dashed line instead').toBeGreaterThan(0);
  expect(label, 'with its label').toBe(true);
  expect(flyers, 'nothing flies to the You node').toBe(0);
  expect(youLine, 'a still line to it').toBe(true);
  expect(commitAt.length).toBeGreaterThan(0);
  expect(Math.max(...commitAt) - Math.min(...commitAt), 'the commit does not travel: it is at its resting place from the first frame').toBeLessThan(0.5);
  expect(landedRings, 'no landing burst').toBe(0);
  expect(starts.size, 'the running ring does not turn').toBe(1);
  await shot(page, 'reduced-motion-1280');
  expect(errors).toEqual([]);
  await ctx.close();
});

// ---------------------------------------------------------------------------------------------------------------------
// 8. Sound

test('8 sound: off by default (no oscillator for events), on when turned on and kept', async ({ browser }) => {
  const feed = new Feed();
  seed(feed);
  const { page, ctx, errors } = await open(browser, { feed });
  await page.waitForTimeout(1500);
  const btn = page.locator('[data-test=monitor-sound]').first();
  const r: Record<string, unknown> = { default: await btn.getAttribute('aria-pressed'), stored: await page.evaluate(() => localStorage.getItem('yawble.monitor.sound')) };
  feed.row('handback', ADA, ADA, 10, 0, 'Done');
  feed.event({ kind: 'commit', team: TEAM, member: ADA, sha: 'f'.repeat(40), message: 'x', files: [] });
  await page.waitForTimeout(2500);
  r.offOscillators = await page.evaluate(() => (window as any).__osc);
  r.offContexts = await page.evaluate(() => (window as any).__audio);
  await btn.click();
  r.on = await btn.getAttribute('aria-pressed');
  r.storedOn = await page.evaluate(() => localStorage.getItem('yawble.monitor.sound'));
  feed.row('instruction', ADA, 'Manager', 10, 0, 'Card 4380');
  await page.waitForTimeout(1300);
  feed.row('handback', ADA, ADA, 10, 0, 'Done');
  await page.waitForTimeout(2500);
  r.onOscillators = await page.evaluate(() => (window as any).__osc);
  await page.reload();
  await page.locator('[data-test=sidebar] [data-test=nav-monitor]').first().click();
  await expect(page.locator('[data-test=monitor-canvas]')).toBeVisible();
  r.afterReload = await page.locator('[data-test=monitor-sound]').first().getAttribute('aria-pressed');
  result.sound = r;
  expect(r.default).toBe('false');
  expect(r.offOscillators, 'off: silent').toBe(0);
  expect(r.on).toBe('true');
  expect(r.onOscillators as number, 'on: a tone for the hand-back').toBeGreaterThan(0);
  expect(r.afterReload, 'kept').toBe('true');
  expect(errors).toEqual([]);
  await ctx.close();
});

// ---------------------------------------------------------------------------------------------------------------------
// 9. The screenshot matrix, and the remote view at 375 and 1280

function busyFeed(): Feed {
  const feed = new Feed();
  seed(feed);
  feed.row('instruction', 'DeveloperCleo', 'Manager', 20, 100_000, 'Card 9: checkout', TEAM2);
  feed.wf(2, 20, 'open', ['DeveloperCleo is running'], 'Cart rework');
  for (let i = 0; i < 10; i++) { feed.step(ADA, 'edit', `src/refund/f${i}.ts`, 90_000 - i); feed.step('DeveloperCleo', 'edit', `web/cart/c${i}.ts`, 90_000 - i, TEAM2); }
  feed.attn.set(`1/${BEN}`, 'decision');
  return feed;
}
function liveLoad(feed: Feed) {
  let n = 0;
  return setInterval(() => {
    n++;
    feed.step(ADA, n % 3 ? 'read' : 'edit', `src/refund/f${n % 10}.ts`);
    feed.step('DeveloperCleo', 'edit', `web/cart/c${n % 10}.ts`, 0, TEAM2);
    if (n % 4 === 0) feed.row('instruction', ADA, 'Manager', 10, 0, `Card ${400 + n}: next`);
    if (n % 5 === 0) feed.step(BEN, 'run', 'npm test', 0, TEAM, n % 10 ? 0 : 1);
    if (n % 6 === 0) feed.event({ kind: 'commit', team: TEAM, member: ADA, sha: n.toString(16).padStart(40, '0'), message: `Live ${n}`, files: [{ path: `src/refund/f${n % 10}.ts`, status: 'M' }] });
  }, 600);
}

for (const theme of ['light', 'dark'] as const)
  for (const w of [1280, 375]) {
    test(`9 screens ${theme} ${w}: focus off and on, each density${theme === 'dark' ? ', reduced motion' : ''}`, async ({ browser }) => {
      test.setTimeout(180_000);
      const feed = busyFeed();
      const { page, ctx, errors } = await open(browser, { feed, theme, w });
      const load = liveLoad(feed);
      await page.waitForTimeout(4500);
      await unhover(page);
      const tag = `${theme}-${w}`;
      const r: Record<string, unknown> = { theme: await page.evaluate(() => document.documentElement.dataset.theme) };
      const box = await canvasBox(page);
      const f = await lastFrame(page);
      const y = youNode(f);
      r.youNode = y && { x: y.x, y: y.y, inside: y.x + 24 <= box.width && y.x - 24 >= 0, count: y.count };
      expect(y, 'the You node is drawn').not.toBeNull();
      expect(y!.x + 24, 'inside the wall').toBeLessThanOrEqual(box.width);
      await shot(page, `${tag}-focus-off`);
      // Focus payments (on the phone, a tap on its hub).
      r.focusTries = await focusTeam(page, TEAM, w < 700);
      await expect(page.locator('[data-test=monitor-focus]')).toBeVisible();
      const chip = (await page.locator('[data-test=monitor-focus]').boundingBox())!;
      const leave = (await page.locator('[data-test=monitor-focus-leave]').boundingBox())!;
      r.focusChip = { right: chip.x + chip.width, viewport: w, leaveButton: { w: leave.width, h: leave.height } };
      expect(chip.x + chip.width, 'the chip fits').toBeLessThanOrEqual(w);
      if (w < 700) expect(Math.min(leave.width, leave.height), 'a 44 px target on the phone').toBeGreaterThanOrEqual(44);
      await page.waitForTimeout(2500);
      await unhover(page);
      await shot(page, `${tag}-focus-on`);
      await page.locator('[data-test=monitor-focus-leave]').click();
      // Each density: on the phone it sits at the top of the cog's panel.
      if (w < 700) {
        r.densityOutsidePanel = await page.locator('[data-test=monitor-density-calm]').count();
        await page.locator('[data-test=monitor-layout-toggle]').click();
        await expect(page.locator('[data-test=monitor-density-calm]')).toBeVisible();
        const b = (await page.locator('[data-test=monitor-density-calm]').boundingBox())!;
        r.phoneDensityButton = { w: b.width, h: b.height, right: b.x + b.width };
        expect(b.x + b.width).toBeLessThanOrEqual(w);
        await shot(page, `${tag}-cog-panel-density-sound`);
      }
      for (const level of ['calm', 'normal', 'lively']) {
        await page.locator(`[data-test=monitor-density-${level}]`).first().click();
        if (w < 700) await page.keyboard.press('Escape');
        await page.waitForTimeout(2200);
        await shot(page, `${tag}-density-${level}`);
        if (w < 700 && level !== 'lively') await page.locator('[data-test=monitor-layout-toggle]').click();
      }
      clearInterval(load);
      result[`screens-${tag}`] = r;
      expect(errors).toEqual([]);
      await ctx.close();
    });
  }

// With the 22 px reach floor, at 1280 each hub and member still answers for itself, and of two close
// nodes a point nearer one never names the other. The hover tip is read: hover and a click both go through nodeAt.
for (const theme of ['light', 'dark'] as const)
  test(`9d node picks ${theme} 1280: hubs and members hit their own node; a close neighbour does not steal`, async ({ browser }) => {
    test.setTimeout(120_000);
    const feed = busyFeed();
    const { page, ctx, errors } = await open(browser, { feed, theme, w: 1280 });
    await page.waitForTimeout(4500);
    await unhover(page);
    const box = await canvasBox(page);
    const tip = async () => (await page.locator('[data-test=monitor-tip]').count()) ? (await page.locator('[data-test=monitor-tip]').innerText()).replace(/\s+/g, ' ') : '';
    type Nd = { name: string; label: string; at: (f: Frame) => Pt | null };
    const nodes: Nd[] = [
      { name: `hub ${TEAM}`, label: TEAM, at: (f) => { const h = hubAt(f, TEAM); return h && screen(h.op, h.x, h.y); } },
      { name: `hub ${TEAM2}`, label: TEAM2, at: (f) => { const h = hubAt(f, TEAM2); return h && screen(h.op, h.x, h.y); } },
      ...[ADA, BEN, 'DeveloperCleo'].map((m) => ({ name: m, label: m.replace(/([a-z])([A-Z])/g, '$1 $2'), at: (f: Frame) => { const p = memberAt(f, m); return p && screen(p.op, p.x, p.y); } })),
    ];
    const hoverAt = async (p: Pt) => { await page.mouse.move(box.x + p.x, box.y + p.y); await page.waitForTimeout(60); return tip(); };
    const names = (t: string) => nodes.filter((n) => n.label !== TEAM && n.label !== TEAM2 && t.includes(n.label)).map((n) => n.name);
    const r: Record<string, unknown> = {};
    // Each node at its centre.
    for (const n of nodes) {
      const p = n.at(await lastFrame(page));
      expect(p, `${n.name} is drawn`).not.toBeNull();
      const t = await hoverAt(p!);
      r[n.name] = t;
      expect(t, `${n.name}: the tip names it`).toContain(n.label);
      if (n.name.startsWith('hub')) expect(names(t), `${n.name}: no member named`).toEqual([]);
      else expect(names(t), `${n.name}: only it is named`).toEqual([n.name]);
      await unhover(page);
    }
    // The closest pair on screen: a point 35% of the way (at most 15 px) from each toward the other.
    const f = await lastFrame(page);
    const at = nodes.map((n) => ({ n, p: n.at(f)! }));
    let pair: [typeof at[0], typeof at[0]] = [at[0], at[1]], best = Infinity;
    for (let i = 0; i < at.length; i++) for (let j = i + 1; j < at.length; j++) { const d = Math.hypot(at[i].p.x - at[j].p.x, at[i].p.y - at[j].p.y); if (d < best) { best = d; pair = [at[i], at[j]]; } }
    const picks: Record<string, string> = {};
    for (const [a, b] of [pair, [pair[1], pair[0]]] as const) {
      const g = await lastFrame(page);
      const pa = a.n.at(g)!, pb = b.n.at(g)!;
      const d = Math.hypot(pb.x - pa.x, pb.y - pa.y), s = Math.min(0.35 * d, 15) / d;
      const t = await hoverAt({ x: pa.x + (pb.x - pa.x) * s, y: pa.y + (pb.y - pa.y) * s });
      picks[`near ${a.n.name}`] = t;
      expect(t, `near ${a.n.name}, not ${b.n.name}`).toContain(a.n.label);
      if (!b.n.name.startsWith('hub')) expect(names(t), `near ${a.n.name}: ${b.n.name} does not steal it`).not.toContain(b.n.name);
      await unhover(page);
    }
    r.closestPair = { a: pair[0].n.name, b: pair[1].n.name, px: +best.toFixed(1), picks };
    result[`node-picks-${theme}-1280-${Date.now()}`] = r;
    expect(errors).toEqual([]);
    await ctx.close();
  });

for (const w of [1280, 375])
  test(`9b reduced motion screens ${w}`, async ({ browser }) => {
    const feed = busyFeed();
    const { page, ctx, errors } = await open(browser, { feed, w, reduced: true, theme: w < 700 ? 'light' : 'dark' });
    const load = liveLoad(feed);
    await page.waitForTimeout(4500);
    await unhover(page);
    await shot(page, `reduced-${w}`);
    clearInterval(load);
    expect(errors).toEqual([]);
    await ctx.close();
  });

test('9c remote view as a Visitor (read access) at 375 and 1280: the wall draws with monitor.* reads only', async ({ browser }) => {
  for (const w of [1280, 375]) {
    const feed = busyFeed();
    const { page, ctx, errors } = await open(browser, { feed, w, access: 'read' });
    const load = liveLoad(feed);
    await page.waitForTimeout(4000);
    const f = await lastFrame(page);
    const r = { you: !!youNode(f), wfNode10: wfBoxes(f).some((b) => b.color === C.start), wfCalls: feed.calls.filter((c) => c.method === 'bus.workflows').length, comets: (await frames(page)).some((x) => cometHeads(x).length > 0), files: fileDots(f).length };
    result[`visitor-${w}`] = r;
    await shot(page, `visitor-${w}`);
    clearInterval(load);
    expect(r.you).toBe(true);
    expect(r.wfNode10).toBe(true);
    expect(r.files).toBeGreaterThan(0);
    expect(errors).toEqual([]);
    await ctx.close();
  }
});

// ---------------------------------------------------------------------------------------------------------------------
// 10. A busy multi-team scene stays smooth

for (const level of ['normal', 'lively'] as const)
  test(`10 performance (${level}): 6 teams with workflow nodes, steps, commands, comets, commits and asks, live`, async ({ browser }) => {
    test.setTimeout(150_000);
    const feed = new Feed();
    const teams = [TEAM, TEAM2, 'team-3', 'team-4', 'team-5', 'team-6'];
    const ids = [1, 2, 3, 4, 5, 6];
    const who = (t: string) => (t === TEAM ? ADA : t === TEAM2 ? 'DeveloperCleo' : 'DeveloperZed');
    teams.forEach((t, i) => {
      feed.row('instruction', who(t), 'Manager', 10, 300_000, 'Card 1: busy', t);
      for (let k = 0; k < 80; k++) feed.step(who(t), k % 4 ? 'edit' : 'read', `src/area${k % 10}/f${k}.ts`, 240_000 - k * 50, t);
      for (let k = 0; k < 4; k++) feed.wf(ids[i]!, 10 + k, 'open', k % 2 ? [] : [`${who(t)} is running`], `W${k}`);
    });
    const { page, ctx, errors } = await open(browser, { feed, extraTeams: 4, density: level });
    await page.evaluate(() => { (window as any).__rec = false; });
    let n = 0;
    const live = setInterval(() => {
      n++;
      for (const t of teams) {
        feed.step(who(t), 'edit', `src/area${n % 10}/f${(n * 7) % 80}.ts`, 0, t);
        feed.step(who(t), n % 2 ? 'think' : 'run', n % 2 ? null : 'npm test', 0, t, n % 2 ? undefined : n % 3 ? 0 : 1);
      }
      const t = teams[n % 6]!;
      feed.row('instruction', who(t), 'Manager', 10, 0, `Card ${600 + n}`, t);
      if (n % 2 === 0) feed.row('handback', who(t), who(t), 10, 0, 'done', t);
      if (n % 3 === 0) feed.event({ kind: 'commit', team: t, member: who(t), sha: n.toString(16).padStart(40, '0'), message: `c${n}`, files: [{ path: `src/area1/f${n % 80}.ts`, status: 'M' }] });
      if (n % 7 === 0) feed.row('decision', who(t), who(t), 10, 0, 'Question', t);
    }, 600);
    await page.waitForTimeout(5000);
    await page.evaluate(() => { (window as any).__perf = []; });
    await page.waitForTimeout(10_000);
    const perf: { t: number; d: number }[] = await page.evaluate(() => (window as any).__perf);
    clearInterval(live);
    await page.evaluate(() => { (window as any).__rec = true; });
    await page.waitForTimeout(400);
    const byFrame = new Map<number, number>();
    for (const p of perf) byFrame.set(p.t, (byFrame.get(p.t) ?? 0) + p.d);
    const ts = [...byFrame.keys()].sort((a, b) => a - b);
    const gaps = ts.slice(1).map((t, i) => t - ts[i]!);
    const work = [...byFrame.values()];
    const q = (a: number[], p: number) => { const s = [...a].sort((x, y) => x - y); return +s[Math.min(s.length - 1, Math.floor(p * s.length))]!.toFixed(2); };
    const f = await lastFrame(page);
    const stats = {
      density: level, teams: 6, wfNodes: wfBoxes(f).length, filesDrawn: fileDots(f).length, seconds: +((ts.at(-1)! - ts[0]!) / 1000).toFixed(2), frames: ts.length,
      fps: +((ts.length - 1) / ((ts.at(-1)! - ts[0]!) / 1000)).toFixed(1),
      intervalMs: { p50: q(gaps, 0.5), p95: q(gaps, 0.95), p99: q(gaps, 0.99), max: +Math.max(...gaps).toFixed(2), over33: gaps.filter((g) => g > 33.4).length },
      jsPerFrameMs: { p50: q(work, 0.5), p95: q(work, 0.95), p99: q(work, 0.99), max: +Math.max(...work).toFixed(2) },
    };
    result[`perf-${level}`] = stats;
    console.log('PERF', JSON.stringify(stats));
    await shot(page, `perf-${level}-6-teams`);
    expect(stats.fps, 'about 60 fps').toBeGreaterThan(55);
    expect(stats.intervalMs.p95).toBeLessThan(20);
    expect(stats.jsPerFrameMs.p95).toBeLessThan(10);
    expect(errors).toEqual([]);
    await ctx.close();
  });
