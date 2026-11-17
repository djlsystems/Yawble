// Card 375 (verify B080, cards 373 + 374, with card 371's code map): the livelier Monitor end to end, in headless Chrome
// against the phone-remote fake core (remote.html, tests/e2e/phone). The fake serves monitor.messages, monitor.steps
// (with StepRow.exit when the test gives one), monitor.codeEvents and bus.workflows from a feed the test changes while
// the page polls, and patches teams.agents with the attention the test sets (a member that needs the person).
//
// The wall is a canvas, so the spec reads what the scene draws (the card 372 recorder): an init script wraps the
// monitor canvas's 2D context and keeps the last frames' arcs, paths and texts with their transform and alpha. What
// the scene draws for B080 (scene.ts, rhythm.ts):
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
//   npx playwright test card375-monitor --workers=1      (B037_SKIP_BUILD=1 reuses out/b037/renderer)
// Screenshots and the results go to $YAWBLE_TEAM_DOCS/b080-monitor (else test-results/b080-monitor).
import { expect, test, type Browser, type Page } from '@playwright/test';
import { existsSync, mkdirSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { createFakeCore, type Access } from './phone/fake-core';
import { ORIGIN, buildRenderer, builtDir } from './phone/server';

test.use({ channel: 'chrome', headless: true, actionTimeout: 8_000 });
test.setTimeout(150_000);

const SHOTS = path.join(process.env.YAWBLE_TEAM_DOCS || path.resolve(__dirname, '..', '..', 'test-results'), 'b080-monitor');
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
  const out: { x: number; y: number; color: string; lw: number; alpha: number }[] = [];
  f.ops.forEach((o, i) => {
    if (o.op !== 'fill' || o.style !== 'rgba(7, 11, 20, 0.95)' || o.items?.length !== 1 || o.items[0].t !== 'm') return;
    const s = f.ops[i + 1];
    if (!s || s.op !== 'stroke' || s.items?.[0]?.t !== 'm') return;
    out.push({ x: o.items[0].x + 6, y: o.items[0].y + 10, color: s.style, lw: s.lw!, alpha: o.alpha });
  });
  return out;
}
/** A workflow node by its "#id" label (drawn 13 under its centre). */
function wfNode(f: Frame, id: number) {
  const t = f.ops.find((o) => o.op === 'text' && o.text === `#${id}` && (o.style === C.inkDim || o.style === C.blocked));
  if (!t) return null;
  const c = { x: t.x!, y: t.y! - 13 };
  const box = wfBoxes(f).find((b) => near(b, c, 0.05));
  return box ? { ...box, label: t.style, op: t } : null;
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
      }
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
  await ctx.routeWebSocket(/\/rpc$/, (ws) => {
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
  return { page, ctx, feed, core, errors };
}

async function shot(page: Page, name: string) { await page.screenshot({ path: path.join(SHOTS, 'screens', `${name}.png`) }); }

const result: Record<string, unknown> = {};
test.afterAll(() => { writeFileSync(path.join(SHOTS, 'card375-results.json'), JSON.stringify(result, null, 2)); });

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
    if (await page.locator('[data-test=workflow-12]').count()) break;
    await page.waitForTimeout(300);
  }
  await expect(page.locator('[data-test=workflow-12]')).toHaveAttribute('aria-current', 'true');
  r.clickOpens = 'workflow-12 selected in the Workflows dialog';
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
    if (await page.locator('[data-test=workflow-10]').count()) break;
    await page.waitForTimeout(300);
  }
  await expect(page.locator('[data-test=workflow-10]')).toHaveAttribute('aria-current', 'true');
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

test('3 You node: a question flies in, it glows with the count; a click opens Needs you and picks nothing; network and tool asks do not reach it', async ({ browser }) => {
  const feed = new Feed();
  seed(feed);
  const { page, ctx, errors } = await open(browser, { feed, asks: true });
  await until(page, (x) => !!youNode(x), Boolean);
  await page.waitForTimeout(1500);
  const r: Record<string, unknown> = {};
  let f = await lastFrame(page);
  const box = await canvasBox(page);
  let y = youNode(f)!;
  r.place = { x: y.x, y: y.y, canvas: { w: box.width, h: box.height }, expected: { x: box.width - 46, y: Math.max(120, box.height / 2) } };
  expect(y.x).toBeCloseTo(box.width - 46, 0);
  expect(y.y).toBeCloseTo(Math.max(120, box.height / 2), 0);
  // The core has a network ask and a tool ask pending; the view never gives them to live.setAsks.
  const asked = feed.calls.filter((c) => c.method === 'network.asks' || c.method === 'tools.asks').length;
  r.asksPending = { networkAndToolAsksInCore: 2, youCount: y.count, fill: y.fill, asksCallsSeen: asked };
  await shot(page, 'you-01-asks-pending-no-count');

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
  feed.row('instruction', ADA, 'Manager', 10, 0, 'Card 373 (B080 workflow nodes): do it');
  const out = await label('#373');
  r.instruction = out && { style: out.style, heads: cometHeads(out.frame).length };
  expect(out?.style, 'the card number rides the comet in the tell colour').toBe(C.tell);
  await shot(page, 'cards-01-instruction');
  await page.waitForTimeout(1500);
  await page.evaluate(() => { (window as any).__frames = []; });
  feed.row('handback', ADA, ADA, 10, 0, 'Done');
  const back = await label('#373 ✓');
  const mark = back && back.frame.ops.filter((o) => o.op === 'fill' && o.style === 'rgba(7, 11, 20, 0.92)' && o.items?.some((i) => i.t === 'arc' && i.r === 7)).length;
  r.handback = back && { style: back.style, markDiscs: mark };
  expect(back?.style, 'the hand-back in the hand-back colour').toBe(C.handback);
  expect(mark, 'the check rides a small disc').toBeGreaterThan(0);
  await page.waitForTimeout(400);
  await shot(page, 'cards-02-handback-check');
  await page.waitForTimeout(1200);
  feed.row('instruction', BEN, 'Manager', 12, 0, '[card 374] test it');
  await label('#374');
  await page.waitForTimeout(1300);
  feed.row('failed', BEN, BEN, 12, 0, 'Run failed');
  const cross = await label('#374 ✗');
  r.failed = cross && { style: cross.style };
  expect(cross?.style, 'a failed run comes home red with a cross').toBe(C.fail);
  await page.waitForTimeout(1300);
  feed.row('instruction', ADA, 'Manager', 10, 0, 'Card 376: more');
  await label('#376');
  await page.waitForTimeout(1300);
  feed.row('blocked', ADA, ADA, 10, 0, 'Blocked on a host');
  const blk = await label('#376 ✗');
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
  feed.row('instruction', 'DeveloperCleo', 'Manager', 20, 0, 'Card 10: other team comet', TEAM2);
  await page.waitForTimeout(3500);
  const fs = await frames(page);
  const dotsByTeam = (x: Frame) => {
    const a = hubAt(x), b = hubAt(x, TEAM2);
    const ds = commitDots(x);
    return { mine: ds.filter((d) => a && (!b || Math.hypot(d.x - a.x, d.y - a.y) < Math.hypot(d.x - b.x, d.y - b.y))).length, other: ds.filter((d) => b && a && Math.hypot(d.x - b.x, d.y - b.y) < Math.hypot(d.x - a.x, d.y - a.y)).length };
  };
  const mine = Math.max(...fs.map((x) => dotsByTeam(x).mine)), oth = Math.max(...fs.map((x) => dotsByTeam(x).other));
  const otherRings = Math.max(0, ...fs.map((x) => { const b = hubAt(x, TEAM2); return b ? x.ops.filter((o) => o.op === 'stroke' && o.style === C.handback && o.items?.some((i) => i.t === 'arc' && near(i, b, 0.5) && i.r >= 10 && full(i))).length : 0; }));
  const otherComet = Math.max(0, ...fs.map((x) => texts(x).filter((t) => t === '#10').length));
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
  expect(await page.locator('[data-test=monitor-density-normal]').first().getAttribute('aria-pressed'), 'normal by default').toBe('true');
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
    for (let i = 0; i < 10; i++) feed.event({ kind: 'commit', team: TEAM, member: ADA, sha: (++shaN).toString(16).padStart(40, '0'), message: `c${shaN}`, files: [{ path: `src/area1/file${i}.ts`, status: 'M' }] });
    const commits = await most(page, (x) => commitDots(x).length, 3000);
    r[level] = { mapFileNodes: map, stepIconsOnAda: steps, cometHeads: comets, paymentsCommitDots: commits, stored: await page.evaluate(() => localStorage.getItem('yawble.monitor.density')) };
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
  feed.row('instruction', BEN, 'Manager', 12, 0, 'Card 377: still');
  feed.row('decision', ADA, ADA, 10, 0, 'A question');
  feed.event({ kind: 'commit', team: TEAM, member: ADA, sha: 'd'.repeat(40), message: 'Still commit', files: [{ path: 'src/x1.ts', status: 'M' }] });
  feed.event({ kind: 'landed', team: TEAM, member: null, sha: 'e'.repeat(40), message: 'Landed', files: [] });
  await page.waitForTimeout(2500);
  const fs = await frames(page);
  const radii = fs.map((x) => bodyR(x, ADA)).filter((x): x is number => x !== null);
  const heads = Math.max(0, ...fs.map((x) => cometHeads(x).length));
  const still = fs.filter((x) => x.ops.some((o) => o.op === 'stroke' && o.dash && o.style === C.tell && o.items?.some((i) => i.t === 'q'))).length;
  const label = fs.some((x) => texts(x).includes('#377'));
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
  feed.row('instruction', ADA, 'Manager', 10, 0, 'Card 380');
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

test('9c remote view as a Visitor (read access) at 375 and 1280: the B080 wall draws with monitor.* reads only', async ({ browser }) => {
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
