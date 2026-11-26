// The Monitor's lasting code map, end to end, in headless Chrome against the
// phone-remote fake core (remote.html, tests/e2e/phone). The fake serves monitor.messages, monitor.steps and
// monitor.codeEvents (the core's wire shape: CodeEvent.ToJson) from a feed the test grows while the page polls.
//
// The wall is a canvas, so the spec reads what the scene draws: an init workflow wraps the monitor canvas's 2D context
// and keeps the last frames' arcs, paths and texts (world coordinates and the transform). A file node is a filled arc of
// radius 3.6 (times its pop) in its touch's colour, a confirmed file wears a ring at 5.8, a file two members touched a
// double ring (7.8 and 9.8, ink); a folder is a gradient glow of radius 16 + heat * 18 with its label in hsl(205 - heat*175);
// "+N" and "+N folders" are texts; a commit is a filled #fde047 dot (4.2 and more); a merge a #fbbf24 ring off the hub,
// a landing #34d399 rings (width 3), a push a #38bdf8 arc of 1.4 rad. Hovering a node gives the DOM tip.
//
//   npx playwright test code-map --workers=1      (E2E_SKIP_BUILD=1 reuses out/e2e/renderer)
// Screenshots and the frame times go to $YAWBLE_TEAM_DOCS/code-map (else test-results/code-map).
import { expect, test, type Browser, type Page } from './phone/fixtures';
import { mkdirSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { createFakeCore, type Access } from './phone/fake-core';
import { ORIGIN, buildRenderer, builtDir } from './phone/server';
import { existsSync } from 'node:fs';

test.use({ channel: 'chrome', headless: true, actionTimeout: 8_000 });
test.setTimeout(120_000);

const SHOTS = path.join(process.env.YAWBLE_TEAM_DOCS || path.resolve(__dirname, '..', '..', 'test-results'), 'code-map');
test.beforeAll(async () => { test.setTimeout(600_000); mkdirSync(SHOTS, { recursive: true }); await buildRenderer(); });

const TEAM = 'payments';
const ADA = 'DeveloperAda';
const BEN = 'TesterBen';
const C = { read: '#7dd3fc', edit: '#a3e635', create: '#2dd4bf', commit: '#fde047', handback: '#fbbf24', done: '#34d399', tell: '#38bdf8', ink: '#e2e8f0' };
const FILE_COLORS = [C.read, C.edit, C.create];

// ---------------------------------------------------------------------------------------------------------------------
// The feed behind the fake core.

type Row = { seq: number; kind: string; team: string; member: string | null; from: string; text: string | null; deliverable: number; at: string };
type Step = { id: number; at: string; team: string; member: string; run: number; kind: string; tool: string; target: string | null };
type Ev = { id: number; kind: string; team: string; member: string | null; branch: string; sha: string; message: string; files: { path: string; status: string; oldPath?: string }[]; at: string; recordedAt: string; deliverableId: number | null; into?: string; branches?: string[] };

class Feed {
  rows: Row[] = []; steps: Step[] = []; events: Ev[] = [];
  private seq = 100; private sid = 1000; private eid = 1;
  /** Methods asked, in order, with their params. */
  calls: { method: string; params: any }[] = [];
  /** With true, monitor.codeEvents without a team is refused (the view must ask per team). */
  refuseAllTeams = false;
  iso(msAgo = 0) { return new Date(Date.now() - msAgo).toISOString(); }
  row(kind: string, member: string | null, from: string, deliverable = 10, msAgo = 0, text: string | null = null, team = TEAM) {
    this.rows.push({ seq: ++this.seq, kind, team, member, from, text, deliverable, at: this.iso(msAgo) });
  }
  step(member: string, kind: string, target: string | null, msAgo = 0, team = TEAM) {
    const tool = kind === 'read' ? 'Read' : kind === 'edit' ? 'Edit' : kind === 'create' ? 'Write' : 'Bash';
    this.steps.push({ id: ++this.sid, at: this.iso(msAgo), team, member, run: 41, kind, tool, target });
  }
  event(e: Partial<Ev> & { kind: string; sha: string }, msAgo = 0) {
    const at = this.iso(msAgo);
    this.events.push({ id: this.eid++, team: TEAM, member: null, branch: 'members/payments/developerada', message: '', files: [], at, recordedAt: at, deliverableId: 10, ...e });
  }
  answer(method: string, p: any): unknown | undefined {
    if (method.startsWith('monitor.')) this.calls.push({ method, params: p });
    if (method === 'monitor.messages') {
      const after = p?.after ?? 0;
      const list = this.rows.filter((r) => r.seq > after);
      return { end: Math.max(after, ...this.rows.map((r) => r.seq), 0), messages: list };
    }
    if (method === 'monitor.steps') {
      const after = p?.after ?? 0;
      const list = this.steps.filter((s) => s.id > after);
      return { end: Math.max(after, ...this.steps.map((s) => s.id), 0), pending: false, steps: list };
    }
    if (method === 'monitor.codeEvents') {
      if (this.refuseAllTeams && !p?.team) throw Object.assign(new Error('bad_params'), { code: 'bad_params' });
      const since = p?.since ? Date.parse(p.since) : -Infinity;
      return this.events.filter((e) => Date.parse(e.at) >= since && (!p?.team || e.team.toLowerCase() === String(p.team).toLowerCase()));
    }
    return undefined;
  }
}

// ---------------------------------------------------------------------------------------------------------------------
// The canvas recorder (runs in the page).

function recorder() {
  const w = window as any;
  w.__rec = true;
  w.__frames = [];
  w.__perf = null;
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
      if (cur) { w.__frames.push({ at: performance.now(), ops: cur }); if (w.__frames.length > 150) w.__frames.shift(); }
      cur = [];
    }
  });
  wrap('beginPath', (c) => { paths.set(c, []); });
  wrap('arc', (c, a) => { (paths.get(c) ?? (paths.set(c, []), paths.get(c)!)).push({ t: 'arc', x: a[0], y: a[1], r: a[2], a0: a[3], a1: a[4] }); });
  wrap('moveTo', (c, a) => { paths.get(c)?.push({ t: 'm', x: a[0], y: a[1] }); });
  wrap('lineTo', (c, a) => { paths.get(c)?.push({ t: 'l', x: a[0], y: a[1] }); });
  const draw = (kind: string) => (c: any) => {
    if (!cur) return;
    const items = paths.get(c) ?? [];
    cur.push({ op: kind, style: style(kind === 'fill' ? c.fillStyle : c.strokeStyle), alpha: c.globalAlpha, lw: c.lineWidth, dash: c.getLineDash().length > 0, comp: c.globalCompositeOperation, items: items.slice(0, 40), m: m(c) });
  };
  wrap('fill', draw('fill'));
  wrap('stroke', draw('stroke'));
  wrap('fillText', (c, a) => { cur?.push({ op: 'text', text: String(a[0]), x: a[1], y: a[2], style: style(c.fillStyle), alpha: c.globalAlpha, m: m(c) }); });

  // Frame times: every animation-frame callback's start (the frame's timestamp) and how long it ran.
  const raf = window.requestAnimationFrame.bind(window);
  window.requestAnimationFrame = (cb: FrameRequestCallback) => raf((t) => {
    const s = performance.now();
    try { cb(t); } finally { const p = w.__perf; if (p) p.push({ t, s, d: performance.now() - s }); }
  });
}

type Op = { op: 'fill' | 'stroke' | 'text'; style: string; alpha: number; lw?: number; dash?: boolean; items?: any[]; text?: string; x?: number; y?: number; m: number[] };
type Frame = { at: number; ops: Op[] };

const frames = (page: Page): Promise<Frame[]> => page.evaluate(() => (window as any).__frames);
const lastFrame = (page: Page): Promise<Frame> => page.evaluate(() => (window as any).__frames.at(-1));
const screen = (o: Op, x: number, y: number) => ({ x: o.m[0] * x + o.m[2], y: o.m[1] * y + o.m[3] });

/** The member's world position from its name label (drawn at y + r + 5 + 6 under its disc). */
function memberAt(f: Frame, name: string): { x: number; y: number } | null {
  const spaced = name.replace(/([a-z])([A-Z])/g, '$1 $2');
  const t = f.ops.find((o) => o.op === 'text' && o.text === spaced);
  if (!t) return null;
  const r = name === 'Manager' ? 17 : 14;
  return { x: t.x!, y: t.y! - r - 11 };
}
/** The team's hub (its pill's text sits 22.5 above it). */
function hubAt(f: Frame, team = TEAM): { x: number; y: number } | null {
  const t = f.ops.find((o) => o.op === 'text' && o.text === team && o.style === C.ink);
  return t ? { x: t.x!, y: t.y! + 22.5 } : null;
}
type Dot = { x: number; y: number; r: number; color: string; alpha: number; op: Op };
/** The step badges this frame (their icons are not file nodes). */
function badges(f: Frame) {
  return f.ops.filter((o) => o.op === 'fill' && o.style.startsWith('rgba(7, 11, 20, 0.88)')).flatMap((o) => o.items!.filter((i) => i.t === 'arc' && Math.abs(i.r - 8.5) < 0.01));
}
/** The file nodes: a filled full arc of radius <= 4.6 in a touch's colour, not inside a step badge. */
function fileDots(f: Frame): Dot[] {
  const b = badges(f);
  const out: Dot[] = [];
  for (const o of f.ops) {
    if (o.op !== 'fill' || !FILE_COLORS.includes(o.style) || o.items?.length !== 1) continue;
    const a = o.items[0];
    if (a.t !== 'arc' || a.r > 4.6 || a.r < 0.5 || Math.abs(a.a1 - a.a0 - Math.PI * 2) > 0.01) continue;
    if (b.some((k) => Math.hypot(k.x - a.x, k.y - a.y) < 12)) continue;
    out.push({ x: a.x, y: a.y, r: a.r, color: o.style, alpha: o.alpha, op: o });
  }
  return out;
}
/** Stroked full rings at radius r (+- 0.05) round (x, y). */
function ringsAt(f: Frame, x: number, y: number, r: number, color?: string) {
  return f.ops.filter((o) => o.op === 'stroke' && (!color || o.style === color) && o.items?.some((i) => i.t === 'arc' && Math.hypot(i.x - x, i.y - y) < 0.01 && Math.abs(i.r - r) < 0.05)).length;
}
const texts = (f: Frame) => f.ops.filter((o) => o.op === 'text').map((o) => o.text!);
/** The hue (degrees) of a #rrggbb colour (the canvas hands an hsl() style back as hex). */
function hexHue(hex: string): number {
  const m = /^#([0-9a-f]{2})([0-9a-f]{2})([0-9a-f]{2})$/i.exec(hex);
  if (!m) return NaN;
  const [r, g, b] = [m[1], m[2], m[3]].map((x) => parseInt(x!, 16) / 255) as [number, number, number];
  const max = Math.max(r, g, b), min = Math.min(r, g, b), d = max - min;
  if (!d) return 0;
  const h = max === r ? ((g - b) / d) % 6 : max === g ? (b - r) / d + 2 : (r - g) / d + 4;
  return (h * 60 + 360) % 360;
}
/** Folder glows: gradient fills of one arc, radius 16..34 (heat = (r - 16) / 18), with the label drawn 13 above. */
function folders(f: Frame) {
  // A folder's glow is followed by its square (filled rgba(12, 19, 34, 0.92)); a member's body (also a gradient disc) is not.
  const glows = f.ops.filter((o, i) => o.op === 'fill' && o.style === 'gradient' && o.items?.length === 1 && o.items[0].t === 'arc' && o.items[0].r >= 16 && o.items[0].r <= 34.01
    && f.ops.slice(i + 1).find((n) => n.op === 'fill')?.style === 'rgba(12, 19, 34, 0.92)');
  return glows.map((g) => {
    const a = g.items![0];
    const i = f.ops.indexOf(g);
    // The square's outline: hsl(205 - heat * 175 ...), blue when cool, amber when hot. (Labels only show zoomed in.)
    const edge = f.ops.slice(i + 1).find((n) => n.op === 'stroke');
    const label = f.ops.find((o) => o.op === 'text' && Math.abs(o.x! - a.x) < 0.01 && Math.abs(o.y! - (a.y - 13)) < 0.01);
    return { x: a.x, y: a.y, heat: (a.r - 16) / 18, hue: hexHue(edge?.style ?? ''), edge: edge?.style ?? null, label: label?.text ?? null, op: g };
  });
}
function commitDots(f: Frame) {
  return f.ops.filter((o) => o.op === 'fill' && o.style === C.commit && o.items?.length === 1 && o.items[0].t === 'arc' && o.items[0].r >= 4.19).map((o) => ({ x: o.items![0].x, y: o.items![0].y, r: o.items![0].r, op: o }));
}
function gatherDots(f: Frame) {
  return f.ops.filter((o) => o.op === 'fill' && o.style === C.commit && o.items?.length === 1 && Math.abs(o.items[0].r - 2.2) < 0.01).length;
}
/** Merge / landing rings (stroked full arcs in the burst colour, centred on the hub) and push arcs (1.4 rad). */
function bursts(f: Frame, hub: { x: number; y: number }) {
  const rings = (color: string) => f.ops.filter((o) => o.op === 'stroke' && o.style === color && o.items?.some((i) => i.t === 'arc' && Math.hypot(i.x - hub.x, i.y - hub.y) < 0.5 && i.r >= 10 && Math.abs(i.a1 - i.a0 - Math.PI * 2) < 0.01)).map((o) => ({ r: o.items!.find((i) => i.t === 'arc')!.r, lw: o.lw }));
  const push = f.ops.filter((o) => o.op === 'stroke' && o.style === C.tell && o.items?.some((i) => i.t === 'arc' && Math.abs(i.a1 - i.a0 - 1.4) < 0.01)).length;
  return { merge: rings(C.handback), landed: rings(C.done), push };
}
/** Lines from a member to a point (the member's lines to the files it touched). */
function linesBetween(f: Frame, a: { x: number; y: number }, b: { x: number; y: number }, tol = 1.5) {
  return f.ops.filter((o) => o.op === 'stroke' && o.items?.length === 2 && o.items[0].t === 'm' && o.items[1].t === 'l'
    && Math.hypot(o.items[0].x - a.x, o.items[0].y - a.y) < tol && Math.hypot(o.items[1].x - b.x, o.items[1].y - b.y) < tol);
}

/** Hovers a world point of the wall, returns the tip's text. */
async function hoverWorld(page: Page, o: Op, x: number, y: number): Promise<string> {
  const box = (await page.locator('[data-test=monitor-canvas]').boundingBox())!;
  const s = screen(o, x, y);
  await page.mouse.move(box.x + s.x, box.y + s.y);
  const tip = page.locator('[data-test=monitor-tip]');
  await expect(tip).toBeVisible({ timeout: 4000 });
  return (await tip.innerText()).replace(/\s+/g, ' ');
}
async function hoverFile(page: Page, match: (f: Frame) => Dot | undefined): Promise<string> {
  // The graph still drifts a little: read the place, move there, and retry until the tip is about a file.
  for (let i = 0; i < 6; i++) {
    await still(page);
    const f = await freshFrame(page);
    const d = match(f);
    if (d) {
      const t = await hoverWorld(page, d.op, d.x, d.y).catch(() => '');
      if (t) return t;
    }
  }
  return '';
}
/** Moves the pointer off the wall: the tip goes, and a frame is drawn without the hover. */
async function unhover(page: Page) {
  await page.mouse.move(2, 2);
  await expect(page.locator('[data-test=monitor-tip]')).toHaveCount(0);
  await freshFrame(page);
}

/**
 * Waits for the wall to come to rest: the canvas's data-settled (the forces cooled, the hubs home, the map in place) and
 * data-camera-idle (the camera where it aims), both set by the scene.
 */
async function still(page: Page, timeout = 40_000) {
  // Two frames first: the scene sets the attributes in its frame, so one drawn since the last action is read.
  await page.evaluate(() => new Promise((done) => requestAnimationFrame(() => requestAnimationFrame(() => done(true)))));
  const wall = page.locator('[data-test=monitor-canvas]');
  await expect(wall).toHaveAttribute('data-settled', 'true', { timeout });
  await expect(wall).toHaveAttribute('data-camera-idle', 'true', { timeout });
}
/**
 * A frame drawn after now. The recorder keeps a frame when the next one starts, so the first kept after now may have
 * been drawn before it: the second is the first surely drawn after.
 */
async function freshFrame(page: Page): Promise<Frame> {
  const t0 = await page.evaluate(() => performance.now());
  await page.waitForFunction((t) => (window as any).__frames.filter((f: Frame) => f.at > t).length >= 2, t0, { timeout: 5000 });
  return page.evaluate(() => (window as any).__frames.at(-1));
}
/** Waits until the frames kept (the last 2.5 s) show what `ok` wants (polled); returns them. */
async function untilFrames(page: Page, ok: (fs: Frame[]) => unknown, timeout = 10_000): Promise<Frame[]> {
  // Each poll fetches only the frames kept since the last (fetching all of them each time slows the page).
  const fs: Frame[] = [];
  await expect.poll(async () => {
    fs.push(...await page.evaluate((t) => (window as any).__frames.filter((f: Frame) => f.at > t), fs.at(-1)?.at ?? -Infinity));
    return !!ok(fs);
  }, { timeout, intervals: [100] }).toBe(true).catch(() => {});
  return fs;
}

// ---------------------------------------------------------------------------------------------------------------------

type OpenOpts = { reduced?: boolean; access?: Access; feed?: Feed; w?: number; h?: number; extraTeams?: number };

async function open(browser: Browser, o: OpenOpts = {}) {
  const feed = o.feed ?? new Feed();
  const core = createFakeCore();
  core.setAsks(false);
  core.setNeedsYou(false);
  core.setAccess(o.access ?? 'full');
  const extra = Array.from({ length: o.extraTeams ?? 0 }, (_, i) => ({ id: 3 + i, name: `team-${3 + i}`, repoPath: `C:/repos/t${3 + i}`, members: 2, running: 1, queued: 0 }));
  const ctx = await browser.newContext({ viewport: { width: o.w ?? 1280, height: o.h ?? 800 }, reducedMotion: o.reduced ? 'reduce' : 'no-preference', colorScheme: 'dark' });
  await ctx.addInitScript(() => { try { localStorage.setItem('yawble.monitor.windowMinutes', '15'); localStorage.setItem('yawble.monitor.activity', 'open'); } catch { /* */ } });
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
        if (extra.length && msg.method === 'app.overview') {
          const base = JSON.parse(core.answer(String(m))!).result;
          return ws.send(JSON.stringify({ id: msg.id, result: { ...base, teams: [...base.teams, ...extra] } }));
        }
        if (extra.length && msg.method === 'teams.agents' && (msg.params?.teamId ?? 0) >= 3) {
          const t = extra.find((x) => x.id === msg.params.teamId)!;
          const card = (id: number, name: string, role: string) => ({ member: { id, teamId: t.id, team: t.name, name, role, cli: 'claude', instructions: '', model: '', credential: 'default' }, state: 'running', queuePosition: null, queueLength: 0, queuedInstructions: 0, statusLine: null, runId: 1, runStartedAt: feed.iso(60_000), lastRunState: 'completed', lastRunReason: null, attention: null, net: 'allowlist', wallTimeout: '2h', loosened: [], policyProblems: [], hasClone: true });
          return ws.send(JSON.stringify({ id: msg.id, result: [card(100 + t.id * 2, 'Manager', 'manager'), card(101 + t.id * 2, 'DeveloperZed', 'member')] }));
        }
        if (msg.method === 'bus.deliverables') return ws.send(JSON.stringify({ id: msg.id, result: [] }));
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
  await page.locator('[data-test=sidebar] [data-test=nav-monitor]').first().click();
  await expect(page.locator('[data-test=monitor-canvas]')).toBeVisible({ timeout: 30_000 });
  await expect(page.locator('[data-test=monitor-loading]')).toHaveCount(0, { timeout: 30_000 });
  return { page, ctx, feed, core, errors };
}

/** Waits until the frames show what `ok` wants (the page polls every 1.2 s). */
async function until<T>(page: Page, get: (f: Frame) => T, ok: (v: T) => boolean, timeout = 10_000): Promise<T> {
  let v: T;
  await expect.poll(async () => {
    const f = await lastFrame(page);
    v = f ? get(f) : (undefined as T);
    return !!f && ok(v);
  }, { timeout, intervals: [100] }).toBe(true).catch(() => {});
  return v!;
}

/** A screenshot once the fonts are in and the layout is at rest (data-settled). */
async function shot(page: Page, name: string) {
  await page.evaluate(() => document.fonts.ready.then(() => true));
  if (await page.locator('[data-test=monitor-canvas]').count()) await expect(page.locator('[data-test=monitor-canvas]')).toHaveAttribute('data-settled', 'true', { timeout: 20_000 });
  await page.screenshot({ path: path.join(SHOTS, `${name}.png`) });
}

/** The deliverable's instructions, read at the start: the members' touches belong to deliverable 10. */
function seedDeliverable(feed: Feed, msAgo = 120_000, wf = 10) {
  feed.row('instruction', ADA, 'Manager', wf, msAgo, 'Card 1: partial refunds');
  feed.row('instruction', BEN, 'Manager', wf, msAgo - 1000, 'Card 2: test partial refunds');
}

const result: Record<string, unknown> = {};
test.afterAll(() => { writeFileSync(path.join(SHOTS, 'code-map-results.json'), JSON.stringify(result, null, 2)); });

// ---------------------------------------------------------------------------------------------------------------------

test('map: the tree grows over a deliverable, a Write pops, edits bright, reads faint, folders warmer, member lines, a two-member overlap; files go when the deliverable ends', async ({ browser }) => {
  const feed = new Feed();
  seedDeliverable(feed);
  const { page, ctx, errors } = await open(browser, { feed });
  let f = await lastFrame(page);
  expect(fileDots(f), 'no map before any step').toHaveLength(0);

  // 1. A Write: a new file pops in (its radius goes below and past 3.6 within the 420 ms pop).
  feed.step(ADA, 'create', 'src/refund/partial.ts');
  await until(page, (x) => fileDots(x).length, (n) => n >= 1);
  // Until the pop is over: the new file drawn at its size (3.6) again after its peak; the kept frames cover the 420 ms pop.
  const createdR = (x: Frame) => fileDots(x).filter((d) => d.color === C.create).map((d) => d.r);
  const popped = await untilFrames(page, (all) => { const rs = all.flatMap(createdR); return rs.some((r) => r > 3.62) && createdR(all.at(-1)!).every((r) => Math.abs(r - 3.6) < 0.001); }, 5000);
  const radii = popped.flatMap(createdR);
  result.popRadii = { min: Math.min(...radii), max: Math.max(...radii) };
  expect(Math.min(...radii), 'the new file pops in small').toBeLessThan(3.2);
  expect(Math.max(...radii), 'and overshoots its size (1.25x at its peak, back to 3.6 by 420 ms)').toBeGreaterThan(3.62);

  // 2. The tree grows: edits in src/refund (busier) and a read in docs, Ben reads the file Ada edits.
  feed.step(ADA, 'edit', 'src/refund/model.ts');
  feed.step(ADA, 'edit', 'src/refund/model.ts');
  feed.step(ADA, 'edit', 'src/refund/limits.ts');
  feed.step(BEN, 'read', 'src/refund/model.ts');
  feed.step(BEN, 'read', 'docs/refunds.md');
  const grew = await until(page, (x) => fileDots(x).length, (n) => n === 4);
  expect(grew, 'four files on the map').toBe(4);
  // Until the fresh-touch light has faded (LightMs 2.4 s): every file's alpha the same in two frames 200 ms apart.
  let alphas = '';
  await expect.poll(async () => {
    const now = fileDots(await lastFrame(page)).map((d) => d.alpha.toFixed(3)).sort().join(',');
    const same = now === alphas;
    alphas = now;
    return same;
  }, { timeout: 8000, intervals: [200] }).toBe(true);
  await still(page);
  f = await freshFrame(page);
  const dots = fileDots(f);
  const byColor = (c: string) => dots.filter((d) => d.color === c);
  result.dots = dots.map((d) => ({ color: d.color, alpha: +d.alpha.toFixed(3), r: d.r }));
  expect(byColor(C.create), 'partial.ts new (teal)').toHaveLength(1);
  expect(byColor(C.edit), 'model.ts and limits.ts changed (green)').toHaveLength(2);
  expect(byColor(C.read), 'refunds.md only read (blue)').toHaveLength(1);
  const editAlpha = Math.min(...byColor(C.edit).map((d) => d.alpha)), readAlpha = byColor(C.read)[0]!.alpha;
  result.light = { edit: editAlpha, read: readAlpha };
  expect(editAlpha, 'edits bright').toBeGreaterThan(0.65);
  expect(readAlpha, 'reads faint').toBeLessThan(0.45);
  // Folders: src/refund glows warmer and wider than docs.
  const fl = folders(f);
  expect(fl, 'two folders').toHaveLength(2);
  const [hot, cool] = [...fl].sort((a, b) => b.heat - a.heat);
  // Which is which: the folder's tip names it.
  const hotTip = await hoverWorld(page, hot!.op, hot!.x, hot!.y);
  await unhover(page);
  const coolTip = await hoverWorld(page, cool!.op, cool!.x, cool!.y);
  await unhover(page);
  result.folders = fl.map(({ op: _op, ...x }) => x).concat([{ hotTip, coolTip } as any]);
  expect(hotTip, 'the hot folder is src/refund (3 files, 5 touches)').toMatch(/^src\/refund 3 files touched/);
  expect(coolTip, 'the cool one docs (1 read)').toMatch(/^docs 1 file touched/);
  expect(hot!.heat).toBeCloseTo(1, 5);
  expect(cool!.heat, 'the busier folder is hotter').toBeLessThan(0.3);
  expect(hot!.hue, 'warmer: amber outline').toBeLessThan(60);
  expect(cool!.hue, 'cool: blue outline').toBeGreaterThan(150);
  // Member lines: Ada to her three files, Ben to his two.
  const ada = memberAt(f, ADA)!, ben = memberAt(f, BEN)!;
  expect(ada && ben).toBeTruthy();
  const linesFrom = (m: { x: number; y: number }) => dots.filter((d) => linesBetween(f, m, d).length > 0).length;
  result.lines = { ada: linesFrom(ada), ben: linesFrom(ben) };
  expect(linesFrom(ada), 'a line from Ada to each file she touched').toBe(3);
  expect(linesFrom(ben), 'a line from Ben to each file he read').toBe(2);
  // The overlap: model.ts (Ada edited, Ben read) wears the double ring; the others do not.
  const doubles = dots.filter((d) => ringsAt(f, d.x, d.y, d.r + 4.2, C.ink) && ringsAt(f, d.x, d.y, d.r + 6.2, C.ink));
  expect(doubles, 'one file with two members').toHaveLength(1);
  const tip = await hoverFile(page, (x) => fileDots(x).find((d) => ringsAt(x, d.x, d.y, d.r + 6.2, C.ink) > 0));
  result.overlapTip = tip;
  expect(tip).toContain('src/refund/model.ts');
  expect(tip).toMatch(/by (Developer Ada, Tester Ben|Tester Ben, Developer Ada)/);
  expect(tip).toContain('not committed yet');
  await shot(page, '01-map-overlap-hover');
  await unhover(page);
  const readTip = await hoverFile(page, (x) => fileDots(x).find((d) => d.color === C.read));
  expect(readTip).toMatch(/docs\/refunds\.md read/);
  await unhover(page);
  await shot(page, '02-map-grown');

  // 3. The deliverable ends ('done' row): its files leave the map.
  feed.row('done', null, 'Manager', 10, 0, 'Partial refunds delivered');
  const left = await until(page, (x) => fileDots(x).length, (n) => n === 0);
  expect(left, 'files go when the deliverable ends').toBe(0);
  expect(folders(await lastFrame(page))).toHaveLength(0);
  await shot(page, '03-map-after-done');
  expect(errors).toEqual([]);
  await ctx.close();
});

test('map: a large map collapses to its busiest 8 folders, "+N" per folder and "+N folders"', async ({ browser }) => {
  const feed = new Feed();
  seedDeliverable(feed, 300_000);
  // 12 folders x 10 files, touched about a minute ago (quiet, and well inside the two minutes a file stays on the map), each folder busier than the next.
  for (let d = 0; d < 12; d++) for (let i = 0; i < 10; i++) for (let k = 0; k <= (11 - d) % 3; k++) feed.step(ADA, 'edit', `src/mod${String(d).padStart(2, '0')}/file${i}.ts`, 60_000 - d * 1000 - i * 10 - k);
  const { page, ctx, errors } = await open(browser, { feed });
  await until(page, (x) => fileDots(x).length, (n) => n > 0);
  await still(page); // the camera settles on the whole map (off-screen nodes are culled)
  let f = await freshFrame(page);
  // "+N" nodes: dashed rings of radius 7 (a folder's rest) and 9 (the team's "+N folders"); a folder's "+N" text shows
  // only zoomed in (the scene's labels, cam.k > 0.55), the "+N folders" text always.
  const dashed = (fr: Frame, r: number) => fr.ops.filter((o) => o.op === 'stroke' && o.dash && o.items?.length === 1 && o.items[0].t === 'arc' && o.items[0].r === r);
  const fl = folders(f);
  const restLabel = texts(f).find((s) => /^\+\d+ folders?$/.test(s));
  result.collapse = { files: fileDots(f).length, folders: fl.length, more: dashed(f, 7).length, rest: dashed(f, 9).length, restLabel };
  expect(fl.length, 'at most 8 folders drawn').toBe(8);
  expect(restLabel, 'the rest as "+4 folders"').toBe('+4 folders');
  expect(dashed(f, 9), 'one "+N folders" node').toHaveLength(1);
  expect(fileDots(f).length, 'at most 48 file nodes for the team').toBe(48);
  expect(dashed(f, 7), 'a "+N" on each of the 8 folders (each has more files than drawn)').toHaveLength(8);
  await shot(page, '04-map-collapsed');
  // Zoom in (the wheel) so the labels show, and read the "+N" texts: drawn + counted = the 8 folders' 80 files.
  const box = (await page.locator('[data-test=monitor-canvas]').boundingBox())!;
  const ring = dashed(f, 7)[0]!;
  const s0 = screen(ring, ring.items![0].x, ring.items![0].y);
  await page.mouse.move(box.x + s0.x, box.y + s0.y);
  for (let i = 0; i < 2; i++) { await page.mouse.wheel(0, -150); await freshFrame(page); }
  await unhover(page);
  await still(page); // the zoom applied and the map at rest
  f = await freshFrame(page);
  const k = f.ops.find((o) => o.op === 'text')!.m[0];
  const plus = texts(f).filter((s) => /^\+\d+$/.test(s));
  result.collapse = { ...(result.collapse as object), zoom: k, plus };
  expect(k, 'zoomed in past the labels\' 0.55').toBeGreaterThan(0.55);
  expect(plus.length, 'the folders\' "+N" texts on screen').toBeGreaterThan(0);
  if (plus.length === 8 && fileDots(f).length === 48) {
    const counted = plus.map((x) => Number(x.slice(1))).reduce((a, b) => a + b, 0);
    expect(48 + counted, 'drawn + "+N" = 80').toBe(80);
  }
  // A "+N"'s tip lists the files it stands for.
  const more = dashed(f, 7).find((o) => { const p = screen(o, o.items![0].x, o.items![0].y); return p.x > 20 && p.y > 100 && p.x < box.width - 300 && p.y < box.height - 80; });
  if (more) {
    const tip = await hoverWorld(page, more, more.items![0].x, more.items![0].y);
    result.collapse = { ...(result.collapse as object), moreTip: tip };
    expect(tip).toMatch(/^\+\d+ in src\/mod\d\d \d+ files? touched src\/mod\d\d\/file\d\.ts/);
  }
  await shot(page, '04b-map-collapsed-zoomed');
  expect(errors).toEqual([]);
  await ctx.close();
});

test('events: a commit dot travels to the hub (hover: message + sha) and corrects the map; merge-team, landed and push draw', async ({ browser }) => {
  const feed = new Feed();
  seedDeliverable(feed);
  feed.step(ADA, 'create', 'src/refund/partial.ts', 60_000);
  feed.step(ADA, 'create', 'src/refund/model.ts', 59_000); // a Write over a file the commit will call M
  feed.step(ADA, 'edit', 'src/refund/old.ts', 58_000);
  feed.step(ADA, 'edit', 'src/refund/name.ts', 57_000);
  const { page, ctx, errors } = await open(browser, { feed });
  let f = await until(page, (x) => x, (x) => fileDots(x).length === 4);
  expect(fileDots(f).filter((d) => d.color === C.create), 'two "new" from the steps').toHaveLength(2);

  // The member syncs with a new commit.
  const sha = 'a1b2c3d4e5f60718293a4b5c6d7e8f9012345678';
  feed.event({ kind: 'commit', member: ADA, sha, message: 'Add partial refunds\n\nThe long body.', files: [
    { path: 'src/refund/partial.ts', status: 'A' }, { path: 'src/refund/model.ts', status: 'M' }, { path: 'src/refund/old.ts', status: 'D' },
    { path: 'src/refund/renamed.ts', status: 'R', oldPath: 'src/refund/name.ts' },
  ] });
  // Follow the dot: gathers (2.2 dots), then a dot from the member to the hub, then resting by the hub.
  const track: { t: number; x: number; y: number; dm: number; dh: number }[] = [];
  let gathered = 0;
  // Until the dot has been followed for 2.5 s (it gathers, travels 1.3 s and rests).
  await expect.poll(async () => {
    for (const fr of await frames(page)) {
      gathered = Math.max(gathered, gatherDots(fr));
      const cd = commitDots(fr)[0];
      const m = memberAt(fr, ADA), h = hubAt(fr);
      if (cd && m && h && !track.some((p) => p.t === fr.at)) track.push({ t: fr.at, x: cd.x, y: cd.y, dm: Math.hypot(cd.x - m.x, cd.y - m.y), dh: Math.hypot(cd.x - h.x, cd.y - h.y) });
    }
    return track.length > 0 && Math.max(...track.map((p) => p.t)) - Math.min(...track.map((p) => p.t)) > 2500;
  }, { timeout: 9000, intervals: [150] }).toBe(true).catch(() => {});
  track.sort((a, b) => a.t - b.t);
  result.commitTrack = { gathered, first: track[0], last: track.at(-1), n: track.length };
  expect(gathered, 'the files gather to the member').toBeGreaterThan(0);
  expect(track.length).toBeGreaterThan(5);
  expect(track[0]!.dm, 'the dot starts at the member').toBeLessThan(8);
  expect(track.at(-1)!.dh, 'and rests by the hub (46 off it)').toBeLessThan(50);
  expect(Math.abs(track.at(-1)!.dh - 46)).toBeLessThan(1);
  await shot(page, '05-commit-dot');
  f = await lastFrame(page);
  const cd = commitDots(f)[0]!;
  const tip = await hoverWorld(page, cd.op, cd.x, cd.y);
  result.commitTip = tip;
  expect(tip).toContain('Add partial refunds');
  expect(tip).toContain('a1b2c3d');
  expect(tip).not.toContain('a1b2c3d4');
  // The tip counts the files the dot gathered (3: the deleted old.ts is not on the map); the Activity line says 4.
  expect(tip).toMatch(/a1b2c3d · 3 files/);
  await shot(page, '06-commit-hover');
  await unhover(page);

  // The map takes the commit as the truth: old.ts gone, name.ts moved to renamed.ts, model.ts no longer "new", all ringed.
  f = await until(page, (x) => x, (x) => fileDots(x).length === 3);
  const dots = fileDots(f);
  result.afterCommit = dots.map((d) => ({ color: d.color, confirmed: ringsAt(f, d.x, d.y, d.r + 2.2) > 0 }));
  expect(dots).toHaveLength(3);
  expect(dots.filter((d) => d.color === C.create), 'partial.ts new (A), renamed.ts new at its path').toHaveLength(2);
  expect(dots.filter((d) => d.color === C.edit), 'model.ts changed (M wins over the Write)').toHaveLength(1);
  expect(dots.every((d) => ringsAt(f, d.x, d.y, d.r + 2.2) > 0), 'each committed file wears a ring').toBe(true);
  const tips: string[] = [];
  for (let i = 0; i < 3; i++) { tips.push(await hoverFile(page, (x) => fileDots(x)[i])); await unhover(page); }
  result.afterCommitTips = tips;
  expect(tips.find((t) => t.includes('src/refund/renamed.ts')), 'the renamed file at its new path').toMatch(/new .*in commit a1b2c3d/);
  expect(tips.find((t) => t.includes('src/refund/model.ts')), 'model.ts changed').toMatch(/changed .*in commit a1b2c3d/);
  expect(tips.join(' ')).not.toMatch(/old\.ts|refund\/name\.ts/);
  const act = page.locator('[data-test=monitor-row]');
  await expect(act.filter({ hasText: 'committed' }).first()).toContainText('a1b2c3d Add partial refunds (4 files)');

  // A merge into the team branch, a landing on main and a push, each a few seconds apart.
  const hub = hubAt(await lastFrame(page))!;
  const seen = { merge: [] as number[], landedLw: [] as number[], landedRings: 0, push: 0 };
  /** Collects what the kept frames show until `done` (an effect seen and over), each frame once. */
  const counted = new Set<number>();
  const watch = (done: (last: ReturnType<typeof bursts>) => boolean) => expect.poll(async () => {
    const fs = await frames(page);
    for (const fr of fs) {
      if (counted.has(fr.at)) continue;
      counted.add(fr.at);
      const b = bursts(fr, hubAt(fr) ?? hub);
      seen.merge.push(...b.merge.map((r) => r.r));
      seen.landedLw.push(...b.landed.map((r) => r.lw!));
      seen.landedRings = Math.max(seen.landedRings, b.landed.length);
      seen.push += b.push;
    }
    const last = fs.at(-1);
    return !!last && done(bursts(last, hubAt(last) ?? hub));
  }, { timeout: 8000, intervals: [120] }).toBe(true).catch(() => {});
  feed.event({ kind: 'merge-team', member: ADA, branch: 'members/payments/developerada', sha: 'b2c3d4e5f6a7b8c9d0e1f2a3b4c5d6e7f8a9b0c1', message: "Merge DeveloperAda's work", files: [{ path: 'src/refund/partial.ts', status: 'A' }], into: 'team/payments' });
  await watch((last) => seen.merge.length > 3 && last.merge.length === 0); // the merge's rings have run out
  const mergeSeen = [...seen.merge];
  await shot(page, '07-merge');
  feed.event({ kind: 'landed', member: null, branch: 'team/payments', sha: 'c3d4e5f6a7b8c9d0e1f2a3b4c5d6e7f8a9b0c1d2', message: 'Land team/payments on main', files: [{ path: 'src/refund/partial.ts', status: 'A' }], into: 'main', deliverableId: null });
  await watch((last) => seen.landedRings > 0 && last.landed.length === 0);
  feed.event({ kind: 'push', member: null, branch: 'main', sha: 'c3d4e5f6a7b8c9d0e1f2a3b4c5d6e7f8a9b0c1d2', message: 'Pushed main to origin', files: [], branches: ['main'], deliverableId: null });
  await watch((last) => seen.push > 3 && last.push === 0);
  result.bursts = { mergeRadii: [Math.min(...mergeSeen), Math.max(...mergeSeen)], landedRings: seen.landedRings, landedLw: [...new Set(seen.landedLw)], push: seen.push };
  expect(mergeSeen.length, 'the merge rings off the hub').toBeGreaterThan(3);
  expect(Math.max(...mergeSeen) - Math.min(...mergeSeen), 'and grows').toBeGreaterThan(30);
  expect(seen.landedRings, 'a landing: two rings').toBe(2);
  expect(new Set(seen.landedLw)).toEqual(new Set([3]));
  expect(seen.push, 'a push pulses off the edge').toBeGreaterThan(3);
  await expect(act.filter({ hasText: 'merged into the team' }).first()).toContainText('b2c3d4e');
  await expect(act.filter({ hasText: 'landed on main' }).first()).toContainText('c3d4e5f Land team/payments on main');
  await expect(act.filter({ hasText: 'pushed' }).first()).toContainText('main');
  await shot(page, '08-events-activity');
  // The Timeline marks them: hovering its right end lists the code events there.
  const tl = page.locator('[data-test=monitor-timeline]');
  const tb = (await tl.boundingBox())!;
  await page.mouse.move(tb.x + tb.width - 6, tb.y + tb.height / 2);
  const codeRows = page.locator('[data-test=monitor-timeline-code]');
  await expect(codeRows.first()).toBeVisible();
  const words = (await codeRows.allInnerTexts()).map((s) => s.trim());
  result.timelineCode = words;
  expect(words.join(' | ')).toMatch(/pushed: main/);
  expect(words.join(' | ')).toMatch(/landed on main: c3d4e5f/);
  await shot(page, '09-timeline-code');
  expect(errors).toEqual([]);
  await ctx.close();
});

test('replay: the timeline plays the map and the events in order', async ({ browser }) => {
  const feed = new Feed();
  const min = 60_000;
  seedDeliverable(feed, 9 * min);
  feed.step(ADA, 'create', 'src/refund/partial.ts', 6.5 * min);
  feed.step(ADA, 'edit', 'src/refund/model.ts', 6 * min);
  feed.step(BEN, 'read', 'src/refund/model.ts', 5.5 * min);
  const sha = (c: string) => c.repeat(40);
  feed.event({ kind: 'commit', member: ADA, sha: sha('1'), message: 'R1 commit', files: [{ path: 'src/refund/partial.ts', status: 'A' }, { path: 'src/refund/model.ts', status: 'M' }] }, 5 * min);
  feed.event({ kind: 'merge-team', member: ADA, sha: sha('2'), message: 'R2 merge', files: [{ path: 'src/refund/partial.ts', status: 'A' }], into: 'team/payments' }, 4 * min);
  feed.event({ kind: 'landed', member: null, branch: 'team/payments', sha: sha('3'), message: 'R3 landed', files: [], into: 'main' }, 3 * min);
  feed.event({ kind: 'push', member: null, branch: 'main', sha: sha('3'), message: 'Pushed main to origin', files: [], branches: ['main'] }, 2 * min);
  const { page, ctx, errors } = await open(browser, { feed });
  await expect(page.locator('[data-test=monitor-window-15m]')).toHaveClass(/on/);
  await until(page, (x) => fileDots(x).length, (n) => n === 2);
  // Click the timeline 8 minutes back (the 15 minute window), then 30x.
  const tl = page.locator('[data-test=monitor-timeline]');
  const tb = (await tl.boundingBox())!;
  await page.mouse.click(tb.x + tb.width * (1 - 8 / 15), tb.y + tb.height / 2);
  await expect(page.locator('[data-test=monitor-replaying]')).toBeVisible();
  await page.locator('[data-test=monitor-timebar] button', { hasText: /^30×$/ }).click();
  const order: string[] = [];
  const mapSizes: { t: number; n: number }[] = [];
  let commitDotSeen = false, mergeSeen = false, landedSeen = false, pushSeen = false;
  const t0 = Date.now();
  // Watched until the replay ends (it reaches live): the Activity rows' order, the effects drawn and the map's size.
  await expect.poll(async () => {
    const rows = await page.locator('[data-test=monitor-row]').allInnerTexts();
    for (const r of [...rows].reverse()) {
      const k = /committed|merged into the team|landed on main|pushed|wrote files|edited files/.exec(r)?.[0];
      if (k && !order.includes(k)) order.push(k);
    }
    for (const fr of await frames(page)) {
      const h = hubAt(fr);
      if (commitDots(fr).length) commitDotSeen = true;
      if (h) { const b = bursts(fr, h); if (b.merge.length) mergeSeen = true; if (b.landed.length) landedSeen = true; if (b.push) pushSeen = true; }
    }
    const lf = await lastFrame(page);
    mapSizes.push({ t: Date.now() - t0, n: fileDots(lf).length });
    if (!(await page.locator('[data-test=monitor-replaying]').isVisible())) return true;
    // Mid-replay shots are taken as they come (the replay moves on; waiting for the wall to rest would miss what it draws).
    if (order.length === 0 && mapSizes.length === 1) await page.screenshot({ path: path.join(SHOTS, '10-replay-start.png') });
    if (order.includes('committed') && !order.includes('pushed')) await page.screenshot({ path: path.join(SHOTS, '11-replay-commit.png') });
    return false;
  }, { timeout: 25_000, intervals: [150] }).toBe(true).catch(() => {});
  result.replay = { order, commitDotSeen, mergeSeen, landedSeen, pushSeen, mapStart: mapSizes[0], mapMax: Math.max(...mapSizes.map((m) => m.n)), ms: Date.now() - t0 };
  expect(mapSizes[0]!.n, 'the replay starts before the files existed').toBe(0);
  expect(Math.max(...mapSizes.map((m) => m.n)), 'and grows the map').toBe(2);
  expect(order.filter((k) => ['committed', 'merged into the team', 'landed on main', 'pushed'].includes(k)), 'the events in time order').toEqual(['committed', 'merged into the team', 'landed on main', 'pushed']);
  expect(order.indexOf('wrote files'), 'the steps before the commit').toBeLessThan(order.indexOf('committed'));
  expect(commitDotSeen && mergeSeen && landedSeen && pushSeen, 'each event drawn in the replay').toBe(true);
  await expect(page.locator('[data-test=monitor-replaying]')).toHaveCount(0, { timeout: 20_000 });
  expect(errors).toEqual([]);
  await ctx.close();
});

test('reduced motion (emulated): nothing travels or pops, no bursts; the map just updates', async ({ browser }) => {
  const feed = new Feed();
  seedDeliverable(feed);
  const { page, ctx, errors } = await open(browser, { feed, reduced: true });
  expect(await page.evaluate(() => matchMedia('(prefers-reduced-motion: reduce)').matches)).toBe(true);
  feed.step(ADA, 'create', 'src/refund/partial.ts');
  feed.step(ADA, 'edit', 'src/refund/model.ts');
  await until(page, (x) => fileDots(x).length, (n) => n === 2);
  // Until 30 frames (half a second) have drawn both files: a pop (420 ms) would show in them.
  const both = await untilFrames(page, (all) => all.filter((x) => fileDots(x).length === 2).length >= 30, 5000);
  const radii = both.flatMap((x) => fileDots(x).map((d) => d.r));
  result.reducedRadii = [Math.min(...radii), Math.max(...radii)];
  expect(new Set(radii), 'no pop: every file node at 3.6 in every frame').toEqual(new Set([3.6]));
  feed.event({ kind: 'commit', member: ADA, sha: 'd'.repeat(40), message: 'Reduced commit', files: [{ path: 'src/refund/partial.ts', status: 'A' }, { path: 'src/refund/model.ts', status: 'M' }] });
  const pos: { dh: number }[] = [];
  let gather = 0, seenFrames = 0;
  const b = { merge: 0, landed: 0, push: 0 };
  /** Counts what the kept frames show, each frame once, until `done`. */
  const counted = new Set<number>();
  const watch = (done: () => boolean) => expect.poll(async () => {
    for (const fr of await frames(page)) {
      if (counted.has(fr.at)) continue;
      counted.add(fr.at); seenFrames++;
      const h = hubAt(fr);
      gather += gatherDots(fr);
      for (const d of commitDots(fr)) if (h) pos.push({ dh: Math.hypot(d.x - h.x, d.y - h.y) });
      if (h) { const x = bursts(fr, h); b.merge += x.merge.length; b.landed += x.landed.length; b.push += x.push; }
    }
    return done();
  }, { timeout: 10_000, intervals: [120] }).toBe(true).catch(() => {});
  await watch(() => pos.length > 30); // the commit shown in half a second of frames: travelling, it would have moved
  feed.event({ kind: 'merge-team', member: ADA, sha: 'e'.repeat(40), message: 'Merge', files: [{ path: 'src/refund/partial.ts', status: 'A' }], into: 'team/payments' });
  feed.event({ kind: 'landed', member: null, branch: 'team/payments', sha: 'f'.repeat(40), message: 'Land', files: [], into: 'main' });
  feed.event({ kind: 'push', member: null, branch: 'main', sha: 'f'.repeat(40), message: 'Pushed main to origin', files: [] });
  // The events read (in Activity), then a second of frames in which their bursts would be drawn.
  await expect(page.locator('[data-test=monitor-row]').filter({ hasText: 'pushed' }).first()).toBeVisible({ timeout: 8000 });
  const after = seenFrames;
  await watch(() => seenFrames - after >= 60);
  result.reduced = { commitFrames: pos.length, dh: [Math.min(...pos.map((p) => p.dh)), Math.max(...pos.map((p) => p.dh))], gather, bursts: b };
  expect(pos.length, 'the commit shows').toBeGreaterThan(3);
  expect(gather, 'no files gather').toBe(0);
  expect(Math.min(...pos.map((p) => p.dh)), 'the dot is at the hub from the first frame, never on the spoke').toBeGreaterThan(45);
  expect(Math.max(...pos.map((p) => p.dh))).toBeLessThan(47);
  expect(b, 'no merge, landing or push burst').toEqual({ merge: 0, landed: 0, push: 0 });
  const f = await lastFrame(page);
  expect(fileDots(f).every((d) => ringsAt(f, d.x, d.y, d.r + 2.2) > 0), 'the map updated: both committed').toBe(true);
  await expect(page.locator('[data-test=monitor-row]').filter({ hasText: 'landed on main' })).toHaveCount(1);
  await shot(page, '12-reduced-motion');
  expect(errors).toEqual([]);
  await ctx.close();
});

test('remote view: a Visitor (read access) gets the same map through monitor.* reads; per-team ask when the all-teams read is refused', async ({ browser }) => {
  const feed = new Feed();
  seedDeliverable(feed);
  feed.step(ADA, 'create', 'src/refund/partial.ts', 30_000);
  feed.step(BEN, 'read', 'src/refund/partial.ts', 29_000);
  feed.event({ kind: 'commit', member: ADA, sha: '9'.repeat(40), message: 'Visitor sees this', files: [{ path: 'src/refund/partial.ts', status: 'A' }] }, 20_000);
  feed.refuseAllTeams = true;
  const { page, ctx, errors } = await open(browser, { feed, access: 'read' });
  const f = await until(page, (x) => x, (x) => fileDots(x).length === 1);
  const d = fileDots(f)[0]!;
  expect(d.color).toBe(C.create);
  expect(ringsAt(f, d.x, d.y, d.r + 2.2), 'confirmed by the commit').toBeGreaterThan(0);
  expect(ringsAt(f, d.x, d.y, d.r + 6.2, C.ink), 'two members').toBeGreaterThan(0);
  await expect(page.locator('[data-test=monitor-row]').filter({ hasText: 'committed' })).toContainText('Visitor sees this');
  // The next poll after the refusal: per team again, not the all-teams form.
  const perTeam = () => feed.calls.filter((c) => c.method === 'monitor.codeEvents' && typeof c.params?.team === 'string').length;
  const before = perTeam();
  await expect.poll(perTeam, { timeout: 8000 }).toBeGreaterThan(before);
  const asked = feed.calls.filter((c) => c.method === 'monitor.codeEvents');
  result.remote = { methods: [...new Set(feed.calls.map((c) => c.method))], codeEventsAsks: asked.slice(0, 4).map((c) => c.params) };
  expect(new Set(feed.calls.map((c) => c.method))).toEqual(new Set(['monitor.messages', 'monitor.steps', 'monitor.codeEvents']));
  expect(asked[0]!.params.team, 'first asked for all teams').toBeUndefined();
  expect(asked.slice(1).every((c) => typeof c.params.team === 'string'), 'then per team').toBe(true);
  expect(asked.filter((c) => !c.params.team), 'the refused form is not asked again').toHaveLength(1);
  await shot(page, '13-remote-visitor');
  expect(errors).toEqual([]);
  await ctx.close();
});

test('performance: a several-hundred-file fixture (4 teams x 150 files, live edits and commits) holds about 60 fps', async ({ browser }) => {
  test.setTimeout(150_000);
  const feed = new Feed();
  const teams = [TEAM, 'checkout-web-with-a-rather-long-team-name', 'team-3', 'team-4'];
  const who = (t: string) => (t === TEAM ? ADA : t === teams[1] ? 'DeveloperCleo' : 'DeveloperZed');
  for (const t of teams) {
    feed.row('instruction', who(t), 'Manager', 10, 300_000, 'Busy', t);
    for (let i = 0; i < 150; i++) feed.step(who(t), i % 4 === 0 ? 'read' : 'edit', `src/area${i % 12}/sub/file${i}.ts`, 240_000 - i * 50, t);
  }
  const { page, ctx, errors } = await open(browser, { feed, extraTeams: 2 });
  await page.evaluate(() => { (window as any).__rec = false; });
  // Live load: a few edits per team per poll, a commit every few seconds.
  let n = 0;
  const live = setInterval(() => {
    n++;
    for (const t of teams) for (let k = 0; k < 3; k++) feed.step(who(t), 'edit', `src/area${(n + k) % 12}/sub/file${(n * 7 + k) % 150}.ts`, 0, t);
    if (n % 3 === 0) feed.event({ kind: 'commit', team: teams[n % 4]!, member: who(teams[n % 4]!), sha: n.toString(16).padStart(40, '0'), message: `Load commit ${n}`, files: Array.from({ length: 6 }, (_, k) => ({ path: `src/area${k}/sub/file${(n + k) % 150}.ts`, status: 'M' })) });
  }, 700);
  // A warm-up before the timed window (first paint, the layout's forces). Not the settled attribute: the live load
  // touches new files all the time, so the maps keep growing and the wall does not come to rest.
  await page.waitForTimeout(4000);
  await page.evaluate(() => { (window as any).__perf = []; });
  await page.waitForTimeout(10_000); // a measurement window: the frame rate over 10 s of live load
  const perf: { t: number; s: number; d: number }[] = await page.evaluate(() => (window as any).__perf);
  clearInterval(live);
  await page.evaluate(() => { (window as any).__rec = true; });
  const f = await freshFrame(page);
  const drawn = fileDots(f).length;
  // Frame intervals from the frames' timestamps; JS time per frame = the sum of that frame's callbacks.
  const byFrame = new Map<number, number>();
  for (const p of perf) byFrame.set(p.t, (byFrame.get(p.t) ?? 0) + p.d);
  const ts = [...byFrame.keys()].sort((a, b) => a - b);
  const gaps = ts.slice(1).map((t, i) => t - ts[i]!);
  const work = [...byFrame.values()];
  const q = (a: number[], p: number) => { const s = [...a].sort((x, y) => x - y); return +s[Math.min(s.length - 1, Math.floor(p * s.length))]!.toFixed(2); };
  const mean = (a: number[]) => +(a.reduce((x, y) => x + y, 0) / a.length).toFixed(2);
  const stats = {
    files: 600, filesDrawn: drawn, teams: 4, seconds: +((ts.at(-1)! - ts[0]!) / 1000).toFixed(2), frames: ts.length,
    fps: +((ts.length - 1) / ((ts.at(-1)! - ts[0]!) / 1000)).toFixed(1),
    intervalMs: { mean: mean(gaps), p50: q(gaps, 0.5), p95: q(gaps, 0.95), p99: q(gaps, 0.99), max: +Math.max(...gaps).toFixed(2), over20: gaps.filter((g) => g > 20).length, over33: gaps.filter((g) => g > 33.4).length },
    jsPerFrameMs: { mean: mean(work), p50: q(work, 0.5), p95: q(work, 0.95), p99: q(work, 0.99), max: +Math.max(...work).toFixed(2) },
  };
  result.perf = stats;
  console.log('PERF', JSON.stringify(stats));
  writeFileSync(path.join(SHOTS, 'perf-frames.json'), JSON.stringify({ stats, gaps: gaps.map((g) => +g.toFixed(2)), work: work.map((w) => +w.toFixed(2)) }));
  await shot(page, '14-perf-600-files');
  expect(drawn, 'the 240-node budget across the maps').toBeLessThanOrEqual(240);
  expect(drawn).toBeGreaterThan(150);
  expect(stats.fps, 'about 60 fps').toBeGreaterThan(55);
  expect(stats.intervalMs.p95, 'p95 frame interval within a 60 Hz frame and a bit').toBeLessThan(20);
  expect(stats.jsPerFrameMs.p95, 'the frame\'s work leaves room in 16.7 ms').toBeLessThan(10);
  expect(errors).toEqual([]);
  await ctx.close();
});
