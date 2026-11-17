// Cards 403 (verify B089, Team spacing) and 405 (verify B090, the WORKFLOWS panel gone, "#id title" workflow node labels),
// end to end in headless Chrome against the phone-remote fake core (remote.html, tests/e2e/phone), as card375-monitor.
//
// The walls: 2 or 3 teams (alpha, bravo, charlie), each with 4 members (Manager + 3), a wide code map (6 folders of 4
// files from monitor.codeEvents) and 5 open workflows (one titled "Backlog item ..."). The wall is a canvas: an init
// script records what the scene draws (arcs, paths, texts with their font, transform and alpha), as in card375.
//
// What is measured (world px = the scene's own units, the camera's transform taken out):
//   hub distances      - between each pair of team hubs (the hub's label "team" is drawn 22.5 above its centre).
//   cluster gap        - the least distance between any two drawn things of different teams (member bodies and names,
//                        hubs, workflow squares and labels, map folders and files; map things belong to their nearest
//                        hub, checked by count: each team must get exactly its own 6 folders + 24 files).
//   empty middle       - the distance from the hubs' centroid to the nearest drawn thing.
//
//   npx playwright test card403-team-spacing --workers=1      (B037_SKIP_BUILD=1 reuses out/b037/renderer)
// Screenshots and results go to $YAWBLE_TEAM_DOCS/b089-team-spacing and b090-workflow-nodes.
import { expect, test, type Browser, type Page } from '@playwright/test';
import { existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { createFakeCore } from './phone/fake-core';
import { ORIGIN, buildRenderer, builtDir } from './phone/server';

test.use({ channel: 'chrome', headless: true, actionTimeout: 8_000 });
test.setTimeout(180_000);

const DOCS = process.env.YAWBLE_TEAM_DOCS || path.resolve(__dirname, '..', '..', 'test-results');
const S89 = path.join(DOCS, 'b089-team-spacing');
const S90 = path.join(DOCS, 'b090-workflow-nodes');
test.beforeAll(async () => {
  test.setTimeout(600_000);
  for (const d of [S89, S90]) mkdirSync(path.join(d, 'screens'), { recursive: true });
  await buildRenderer();
});

const INK = '#e2e8f0', INK_DIM = '#94a3b8', BLOCKED = '#fb923c', DONE = '#34d399';
const FILE_COLORS = ['#7dd3fc', '#a3e635', '#2dd4bf'];
const TEAMS = ['alpha', 'bravo', 'charlie'];
const MEMBERS: Record<string, string[]> = {
  alpha: ['Manager', 'DeveloperAmy', 'TesterAbe', 'ReviewerAva'],
  bravo: ['Manager', 'DeveloperBea', 'TesterBo', 'ReviewerBix'],
  charlie: ['Manager', 'DeveloperCy', 'TesterCal', 'ReviewerCat'],
};
const FOLDERS = 6, FILES = 4;
/** Each team's 5 open workflows: the first a backlog item with a long title. */
function wfsOf(ti: number): { id: number; title: string }[] {
  const base = 3848 + ti * 20;
  return [
    { id: base, title: "Backlog item B081 Remote UI: xterm link detection on phones and tablets" },
    { id: base + 1, title: 'Card 12: partial refunds for the merchant dashboard' },
    { id: base + 2, title: 'Short one' },
    { id: base + 3, title: 'Backlog item B090 Workflow node labels' },
    { id: base + 4, title: 'Verify the release notes for the November drop' },
  ];
}
const ALL_WFS = TEAMS.flatMap((_, i) => wfsOf(i));
const titleOf = (id: number) => ALL_WFS.find((w) => w.id === id)!.title;

/** The rule card 405 states, written out here to check what is drawn: "#id title", "Backlog item " dropped, 30 chars with "…". */
function expectLabel(id: number, zoom: number, wallPx: number): string {
  if (wallPx < 760 && zoom < 0.6) return `#${id}`;
  const t = titleOf(id).replace(/^Backlog item\s+/i, '');
  const room = 30 - `#${id}`.length - 1;
  return `#${id} ${t.length <= room ? t : t.slice(0, room - 1).trimEnd() + '…'}`;
}

// ---------------------------------------------------------------------------------------------------------------------
// The feed.

type Wf = { id: number; title: string; owner: string; state: 'open' | 'completed' | 'closed'; undeclared: boolean; busy: string[]; ending: null; startedAt: string; lastAt: string; messages: number };
class Feed {
  rows: any[] = []; events: any[] = []; wfs = new Map<number, Wf[]>();
  private seq = 100; private eid = 1;
  iso(msAgo = 0) { return new Date(Date.now() - msAgo).toISOString(); }
  row(kind: string, team: string, member: string | null, from: string, workflow: number, text: string | null = null, msAgo = 0) {
    this.rows.push({ seq: ++this.seq, kind, team, member, from, text, workflow, at: this.iso(msAgo) });
  }
  wf(teamId: number, id: number, state: Wf['state'], busy: string[], title: string) {
    const list = this.wfs.get(teamId) ?? [];
    const old = list.find((w) => w.id === id);
    const w: Wf = { id, title, owner: 'Manager', state, undeclared: false, busy, ending: null, startedAt: this.iso(600_000), lastAt: this.iso(0), messages: 3 };
    if (old) Object.assign(old, w); else list.push(w);
    this.wfs.set(teamId, list);
  }
  answer(method: string, p: any): unknown | undefined {
    if (method === 'monitor.messages') {
      const after = p?.after ?? 0;
      return { end: Math.max(after, ...this.rows.map((r) => r.seq), 0), messages: this.rows.filter((r) => r.seq > after) };
    }
    if (method === 'monitor.steps') return { end: p?.after ?? 0, pending: false, steps: [] };
    if (method === 'monitor.codeEvents') {
      const since = p?.since ? Date.parse(p.since) : -Infinity;
      return this.events.filter((e) => Date.parse(e.at) >= since && (!p?.team || e.team.toLowerCase() === String(p.team).toLowerCase()));
    }
    if (method === 'bus.workflows') return JSON.parse(JSON.stringify(this.wfs.get(p?.teamId) ?? []));
    if (method === 'network.asks' || method === 'tools.asks') return [];
    return undefined;
  }
}

function seedWall(feed: Feed, n: number) {
  for (let ti = 0; ti < n; ti++) {
    const team = TEAMS[ti]!, ms = MEMBERS[team]!;
    wfsOf(ti).forEach((w, j) => {
      const busy = j < 3 ? [`${ms[j + 1]} is running`] : [];
      feed.wf(ti + 1, w.id, 'open', busy, w.title);
      feed.row('instruction', team, ms[(j % 3) + 1]!, 'Manager', w.id, `Card ${w.id}: go`, 120_000 - j * 1000);
    });
    // A wide map: 6 folders of 4 files, one commit per folder.
    for (let f = 0; f < FOLDERS; f++) {
      const at = feed.iso(300_000 - f * 10_000);
      const sha = `${ti}${f}`.padEnd(40, 'a');
      feed.events.push({
        id: (feed as any).eid++, kind: 'commit', team, member: ms[(f % 3) + 1], branch: `members/${team}/x`, sha, message: `Work ${f}`,
        files: Array.from({ length: FILES }, (_, k) => ({ path: `src/${team}-mod${f}/file${k}.ts`, status: k === 0 ? 'A' : 'M' })),
        at, recordedAt: at, workflowId: wfsOf(ti)[f % 5]!.id,
      });
    }
  }
}

// ---------------------------------------------------------------------------------------------------------------------
// The canvas recorder (card375's, with each text's font).

function recorder() {
  const w = window as any;
  w.__frames = [];
  const P = CanvasRenderingContext2D.prototype as any;
  const paths = new WeakMap<object, any[]>();
  let cur: any[] | null = null;
  const mine = (c: any) => c.canvas?.dataset?.test === 'monitor-canvas';
  const m = (c: any) => { const t = c.getTransform(); return [t.a, t.d, t.e, t.f]; };
  const style = (s: any) => (typeof s === 'string' ? s : 'gradient');
  const wrap = (name: string, f: (c: any, args: any[]) => void) => {
    const orig = P[name];
    P[name] = function (this: any, ...args: any[]) { if (mine(this)) f(this, args); return orig.apply(this, args); };
  };
  wrap('fillRect', (c, a) => {
    if (a[0] === 0 && a[1] === 0 && c.fillStyle === '#070b14') {
      if (cur) { w.__frames.push({ at: performance.now(), ops: cur }); if (w.__frames.length > 200) w.__frames.shift(); }
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
    cur.push({ op: kind, style: style(kind === 'fill' ? c.fillStyle : c.strokeStyle), alpha: c.globalAlpha, lw: c.lineWidth, dash: c.getLineDash().length > 0, items: items.slice(0, 12), m: m(c) });
  };
  wrap('fill', draw('fill'));
  wrap('stroke', draw('stroke'));
  wrap('fillText', (c, a) => { cur?.push({ op: 'text', text: String(a[0]), x: a[1], y: a[2], style: style(c.fillStyle), alpha: c.globalAlpha, font: c.font, align: c.textAlign, m: m(c) }); });
}

type Op = { op: 'fill' | 'stroke' | 'text'; style: string; alpha: number; lw?: number; dash?: boolean; items?: any[]; text?: string; x?: number; y?: number; font?: string; align?: string; m: number[] };
type Frame = { at: number; ops: Op[] };
type Pt = { x: number; y: number };

async function lastFrame(page: Page): Promise<Frame> {
  await page.waitForFunction(() => (window as any).__frames.length > 0, null, { timeout: 8000 });
  return page.evaluate(() => (window as any).__frames.at(-1));
}
const recent = (page: Page, n = 12): Promise<Frame[]> => page.evaluate((k) => (window as any).__frames.slice(-k), n);
const isScreen = (o: Op) => o.m[2] === 0 && o.m[3] === 0 && o.m[0] === 1;
const toScreen = (o: { m: number[] }, x: number, y: number) => ({ x: o.m[0] * x + o.m[2], y: o.m[1] * y + o.m[3] });
const zoomOf = (f: Frame) => f.ops.find((o) => !isScreen(o))?.m[0] ?? 1;
const fontPx = (o: Op) => Number(/(\d+(?:\.\d+)?)px/.exec(o.font ?? '')?.[1] ?? NaN);

function hubAt(f: Frame, team: string): (Pt & { op: Op }) | null {
  const t = f.ops.find((o) => o.op === 'text' && o.text === team && o.style === INK && !isScreen(o));
  return t ? { x: t.x!, y: t.y! + 22.5, op: t } : null;
}
/** Member name labels (spaced CamelCase), each with its body centre (14 or 17 + 11 above the name). */
function membersOf(f: Frame): { name: string; x: number; y: number; r: number; tx: number; ty: number; w: number }[] {
  const out: any[] = [];
  const all = new Set(Object.values(MEMBERS).flat());
  for (const o of f.ops) {
    if (o.op !== 'text' || isScreen(o)) continue;
    const raw = o.text!.replace(/ /g, '');
    if (!all.has(raw)) continue;
    const r = raw === 'Manager' ? 17 : 14;
    out.push({ name: raw, x: o.x!, y: o.y! - r - 11, r, tx: o.x!, ty: o.y!, w: o.text!.length * 6 });
  }
  return out;
}
/** Workflow node labels: texts starting "#<id>" of a known workflow, drawn in the label tones. */
function wfLabels(f: Frame) {
  return f.ops.filter((o) => o.op === 'text' && !isScreen(o) && /^#\d+/.test(o.text!) && ALL_WFS.some((w) => o.text!.startsWith(`#${w.id}`) && (o.text!.length === `#${w.id}`.length || o.text![`#${w.id}`.length] === ' ')))
    .map((o) => ({ id: Number(/^#(\d+)/.exec(o.text!)![1]), text: o.text!, x: o.x!, y: o.y!, cx: o.x!, cy: o.y! - 13, font: fontPx(o), alpha: o.alpha, style: o.style, op: o }));
}
/** Map folders (a rgba(12, 19, 34, 0.92) rounded square, first moveTo at x - 6 + 2.5, y - 5) and file dots. */
function mapThings(f: Frame): Pt[] {
  const out: Pt[] = [];
  for (const o of f.ops) {
    if (o.op === 'fill' && o.style === 'rgba(12, 19, 34, 0.92)' && o.items?.[0]?.t === 'm') out.push({ x: o.items[0].x + 3.5, y: o.items[0].y + 5 });
    if (o.op === 'fill' && FILE_COLORS.includes(o.style) && o.items?.length === 1 && o.items[0].t === 'arc' && Math.abs(o.items[0].r - 3.6) < 0.05) out.push({ x: o.items[0].x, y: o.items[0].y });
  }
  return out;
}
const wfBoxCount = (f: Frame, color?: string) => f.ops.filter((o, i) => o.op === 'fill' && o.style === 'rgba(7, 11, 20, 0.95)' && o.items?.[0]?.t === 'm' && (!color || f.ops[i + 1]?.style === color)).length;

async function canvasBox(page: Page) { return (await page.locator('[data-test=monitor-canvas]').boundingBox())!; }

// ---------------------------------------------------------------------------------------------------------------------

type OpenOpts = { w?: number; theme?: 'light' | 'dark'; teams?: number; reduced?: boolean; layout?: string | null; feed?: Feed; activity?: 'open' | 'collapsed' };

async function open(browser: Browser, o: OpenOpts = {}) {
  const n = o.teams ?? 2;
  const feed = o.feed ?? new Feed();
  if (!o.feed) seedWall(feed, n);
  const core = createFakeCore();
  core.setAsks(false);
  core.setNeedsYou(false);
  core.setAccess('full');
  const w = o.w ?? 1280;
  const phone = w < 700;
  const teams = TEAMS.slice(0, n).map((name, i) => ({ id: i + 1, name, repoPath: `C:/repos/${name}`, members: 4, running: 3, queued: 0 }));
  const ctx = await browser.newContext({ viewport: { width: w, height: phone ? 812 : 800 }, reducedMotion: o.reduced ? 'reduce' : 'no-preference', colorScheme: o.theme ?? 'dark', hasTouch: phone, isMobile: false });
  await ctx.addInitScript((a: { theme: string; layout: string | null; activity: string }) => {
    try {
      if (sessionStorage.getItem('__seeded')) return;
      sessionStorage.setItem('__seeded', '1');
      localStorage.setItem('yawble.monitor.windowMinutes', '15');
      localStorage.setItem('yawble.monitor.activity', a.activity);
      localStorage.setItem('yawble.remoteTheme', a.theme);
      if (a.layout !== null) localStorage.setItem('yawble.monitor.layout', a.layout);
    } catch { /* */ }
  }, { theme: o.theme ?? 'dark', layout: o.layout ?? null, activity: o.activity ?? 'open' });
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
        if (msg.method === 'app.overview') {
          const base = JSON.parse(core.answer(String(m))!).result;
          return ws.send(JSON.stringify({ id: msg.id, result: { ...base, teams, needsYou: 0, networkAsks: 0, toolAsks: 0 } }));
        }
        if (msg.method === 'teams.agents') {
          const t = teams.find((x) => x.id === (msg.params?.teamId ?? msg.params?.id ?? 1));
          if (!t) return ws.send(JSON.stringify({ id: msg.id, result: [] }));
          const card = (id: number, name: string, role: string) => ({ member: { id, teamId: t.id, team: t.name, name, role, cli: 'claude', instructions: '', model: '', credential: 'default' }, state: 'running', queuePosition: null, queueLength: 0, queuedInstructions: 0, statusLine: null, runId: 1, runStartedAt: feed.iso(60_000), lastRunState: 'completed', lastRunReason: null, attention: null, net: 'allowlist', wallTimeout: '2h', loosened: [], policyProblems: [], hasClone: true });
          return ws.send(JSON.stringify({ id: msg.id, result: MEMBERS[t.name]!.map((nm, k) => card(t.id * 10 + k, nm, k === 0 ? 'manager' : 'member')) }));
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
  await gotoMonitor(page, phone);
  return { page, ctx, feed, errors, phone };
}

async function gotoMonitor(page: Page, phone: boolean) {
  if (phone) {
    await page.locator('[data-test=drawer-toggle]').click();
    await expect(page.locator('[data-test=sidebar]')).toHaveAttribute('data-drawer', 'open');
  }
  await page.locator('[data-test=sidebar] [data-test=nav-monitor]').first().click();
  await expect(page.locator('[data-test=monitor-canvas]')).toBeVisible({ timeout: 30_000 });
  await expect(page.locator('[data-test=monitor-loading]')).toHaveCount(0, { timeout: 30_000 });
}

/** Waits until every team's hub, its 5 workflow labels and its map are drawn and the hubs stop moving (under 1 px in 600 ms). */
async function settle(page: Page, n: number, timeout = 20_000): Promise<Frame> {
  const end = Date.now() + timeout;
  let prev: Pt[] | null = null;
  let f = await lastFrame(page);
  while (Date.now() < end) {
    f = await lastFrame(page);
    const hubs = TEAMS.slice(0, n).map((t) => hubAt(f, t));
    const ready = hubs.every(Boolean) && wfLabels(f).length >= n * 5 && mapThings(f).length >= n * (FOLDERS + FOLDERS * FILES);
    if (ready && prev && hubs.every((h, i) => Math.hypot(h!.x - prev![i]!.x, h!.y - prev![i]!.y) < 1)) return f;
    prev = ready ? (hubs as Pt[]) : null;
    await page.waitForTimeout(600);
  }
  return f;
}

/** The hub distances, the cluster gap and the empty middle (see the header). */
function measure(f: Frame, n: number) {
  const names = TEAMS.slice(0, n);
  const hubs = names.map((t) => hubAt(f, t)!);
  const dist: Record<string, number> = {};
  for (let a = 0; a < n; a++) for (let b = a + 1; b < n; b++) dist[`${names[a]}-${names[b]}`] = Math.round(Math.hypot(hubs[a]!.x - hubs[b]!.x, hubs[a]!.y - hubs[b]!.y));
  // Each team's things as circles (x, y, r).
  const things: { x: number; y: number; r: number }[][] = names.map(() => []);
  const near = (p: Pt) => { let bi = 0, bd = Infinity; hubs.forEach((h, i) => { const d = Math.hypot(h.x - p.x, h.y - p.y); if (d < bd) { bd = d; bi = i; } }); return bi; };
  hubs.forEach((h, i) => things[i]!.push({ x: h.x, y: h.y, r: 22 }));
  const memberTeam = (name: string, p: Pt) => (name === 'Manager' ? near(p) : names.findIndex((t) => MEMBERS[t]!.includes(name)));
  for (const m of membersOf(f)) {
    const i = memberTeam(m.name, m);
    if (i < 0) continue;
    things[i]!.push({ x: m.x, y: m.y, r: m.r });
    things[i]!.push({ x: m.tx, y: m.ty + 5, r: m.w / 2 });
  }
  for (const l of wfLabels(f)) {
    const i = names.findIndex((_, ti) => wfsOf(ti).some((w) => w.id === l.id));
    if (i < 0) continue;
    things[i]!.push({ x: l.cx, y: l.cy, r: 10 });
    const w = l.text.length * l.font * 0.55;
    for (let k = -0.5; k <= 0.5001; k += 0.25) things[i]!.push({ x: l.x + k * w, y: l.y + l.font * 0.6, r: l.font * 0.6 });
  }
  const mapCount = names.map(() => 0);
  for (const p of mapThings(f)) { const i = near(p); mapCount[i]!++; things[i]!.push({ x: p.x, y: p.y, r: 6 }); }
  let gap = Infinity;
  for (let a = 0; a < n; a++) for (let b = a + 1; b < n; b++) for (const p of things[a]!) for (const q of things[b]!) gap = Math.min(gap, Math.hypot(p.x - q.x, p.y - q.y) - p.r - q.r);
  const cx = hubs.reduce((s, h) => s + h.x, 0) / n, cy = hubs.reduce((s, h) => s + h.y, 0) / n;
  let middle = Infinity;
  for (const t of things) for (const p of t) middle = Math.min(middle, Math.hypot(p.x - cx, p.y - cy) - p.r);
  return { dist, gap: Math.round(gap), middle: Math.round(Math.max(0, middle)), mapCount, zoom: +zoomOf(f).toFixed(3) };
}

const results: Record<string, any> = { b089: {}, b090: {} };
test.afterAll(() => {
  for (const [k, dir] of [['b089', S89], ['b090', S90]] as const) {
    const file = path.join(dir, 'results.json');
    let old: Record<string, unknown> = {};
    try { old = JSON.parse(readFileSync(file, 'utf8')); } catch { /* */ }
    writeFileSync(file, JSON.stringify({ ...old, ...results[k] }, null, 2));
  }
});

async function sliderSet(page: Page, value: number) {
  await page.locator('[data-test=monitor-layout-spacing]').evaluate((el: HTMLInputElement, v) => {
    el.value = String(v);
    el.dispatchEvent(new Event('input', { bubbles: true }));
  }, value);
}
async function openCog(page: Page) {
  if (!(await page.locator('[data-test=monitor-layout]').count())) await page.locator('[data-test=monitor-layout-toggle]').click();
  await expect(page.locator('[data-test=monitor-layout]')).toBeVisible();
}
/** Every hub on the wall, in screen px inside the canvas. */
async function hubsOnScreen(page: Page, f: Frame, n: number) {
  const box = await canvasBox(page);
  return TEAMS.slice(0, n).map((t) => { const h = hubAt(f, t)!; const s = toScreen(h.op, h.x, h.y); return { team: t, x: Math.round(s.x), y: Math.round(s.y), inside: s.x > 0 && s.y > 0 && s.x < box.width && s.y < box.height }; });
}

// =====================================================================================================================
// Card 403 (B089): the default spacing at 2 and 3 teams.

for (const theme of ['dark', 'light'] as const) for (const w of [1280, 375]) for (const n of [2, 3]) {
  test(`403 spacing ${theme} ${w} ${n} teams: close, no overlap, no empty middle; hub distances`, async ({ browser }) => {
    const { page, ctx, errors } = await open(browser, { w, theme, teams: n });
    const f = await settle(page, n);
    const m = measure(f, n);
    const screen = await hubsOnScreen(page, f, n);
    results.b089[`${theme}-${w}-${n}`] = { ...m, hubsOnScreen: screen };
    console.log(`B089 ${theme} ${w} ${n} teams:`, JSON.stringify({ ...m }));
    await page.screenshot({ path: path.join(S89, 'screens', `spacing-${theme}-${w}-${n}teams.png`) });
    expect(m.mapCount, 'each team gets exactly its own map (6 folders + 24 files) by nearest hub').toEqual(Array(n).fill(FOLDERS * (FILES + 1)));
    expect(m.gap, 'clusters do not overlap').toBeGreaterThan(0);
    expect(m.gap, 'clusters sit close').toBeLessThan(160);
    expect(m.middle, 'no empty middle').toBeLessThan(160);
    expect(screen.every((s) => s.inside), 'every hub on the wall').toBe(true);
    expect(await page.getByText(/^workflows$/i).count(), 'no WORKFLOWS words in the page').toBe(0);
    expect(errors).toEqual([]);
    await ctx.close();
  });
}

// =====================================================================================================================
// Card 403: the Team spacing setting.

test('403 setting: the cog shows Team spacing (0.25-3, step 0.05, default 1); it moves the teams live; Auto fit / Fit refit; kept; Reset; clamped', async ({ browser }) => {
  const r: Record<string, any> = {};
  const { page, ctx, errors } = await open(browser, { w: 1280, theme: 'dark', teams: 3 });
  let f = await settle(page, 3);
  const at1 = measure(f, 3);
  await openCog(page);
  const label = page.locator('[data-test=monitor-layout] label.force').filter({ has: page.locator('[data-test=monitor-layout-spacing]') });
  const input = page.locator('[data-test=monitor-layout-spacing]');
  r.dialog = {
    name: await label.locator('.name').innerText(), help: await label.locator('.help').innerText(),
    min: await input.getAttribute('min'), max: await input.getAttribute('max'), step: await input.getAttribute('step'), value: await input.inputValue(), shown: await label.locator('.val').innerText(),
  };
  expect(r.dialog).toEqual({ name: 'Team spacing', help: 'How far apart the teams sit.', min: '0.25', max: '3', step: '0.05', value: '1', shown: '1.00×' });
  await page.screenshot({ path: path.join(S89, 'screens', 'cog-1280-dark.png') });

  // Live: 2x, under Auto fit.
  await expect(page.locator('[data-test=monitor-autofit]')).toHaveAttribute('aria-pressed', 'true');
  const t0 = Date.now();
  await sliderSet(page, 2);
  let moved = 0;
  while (Date.now() - t0 < 3000) {
    const g = await lastFrame(page);
    const d = Math.hypot(hubAt(g, 'alpha')!.x - hubAt(f, 'alpha')!.x, hubAt(g, 'alpha')!.y - hubAt(f, 'alpha')!.y);
    if (d > 50) { moved = Date.now() - t0; break; }
    await page.waitForTimeout(100);
  }
  r.liveMoveMs = moved;
  expect(moved, 'the teams move within 3 s of the slider').toBeGreaterThan(0);
  f = await settle(page, 3);
  const at2 = measure(f, 3);
  r.at1 = at1; r.at2 = at2;
  r.autoFitAfter2 = await hubsOnScreen(page, f, 3);
  expect(r.autoFitAfter2.every((h: any) => h.inside), 'Auto fit keeps every hub on the wall').toBe(true);
  expect(at2.zoom, 'Auto fit zoomed out').toBeLessThan(at1.zoom);
  for (const k of Object.keys(at1.dist)) expect(at2.dist[k]! / at1.dist[k]!, `${k} about twice as far`).toBeGreaterThan(1.6);
  r.stored2 = await page.evaluate(() => localStorage.getItem('yawble.monitor.layout'));
  expect(JSON.parse(r.stored2).spacing).toBe(2);
  await page.screenshot({ path: path.join(S89, 'screens', 'spacing-2x-autofit-1280-dark.png') });

  // Pinned (Fit once, then pinned): 3x moves the teams off; Fit brings them back.
  await page.locator('[data-test=monitor-fit]').click();
  await page.waitForTimeout(2000);
  await expect(page.locator('[data-test=monitor-autofit]')).toHaveAttribute('aria-pressed', 'false');
  const zPinned = zoomOf(await lastFrame(page));
  await sliderSet(page, 3);
  f = await settle(page, 3);
  r.pinned3 = { zoom: +zoomOf(f).toFixed(3), zoomBefore: +zPinned.toFixed(3), hubs: await hubsOnScreen(page, f, 3), ...measure(f, 3) };
  await page.screenshot({ path: path.join(S89, 'screens', 'spacing-3x-pinned-1280-dark.png') });
  await page.locator('[data-test=monitor-fit]').click();
  await page.waitForTimeout(2500);
  f = await lastFrame(page);
  r.fitAfter3 = { zoom: +zoomOf(f).toFixed(3), hubs: await hubsOnScreen(page, f, 3) };
  expect(r.fitAfter3.hubs.every((h: any) => h.inside), 'Fit refits to the 3x layout').toBe(true);
  expect(r.fitAfter3.zoom, 'Fit zoomed out for 3x').toBeLessThan(r.pinned3.zoomBefore);
  await page.screenshot({ path: path.join(S89, 'screens', 'spacing-3x-fit-1280-dark.png') });

  // 0.25: the tightest.
  await sliderSet(page, 0.25);
  f = await settle(page, 3);
  r.at025 = measure(f, 3);
  await page.locator('[data-test=monitor-fit]').click();
  await page.waitForTimeout(2500);
  await page.screenshot({ path: path.join(S89, 'screens', 'spacing-0.25x-fit-1280-dark.png') });

  // Kept over a reload.
  await sliderSet(page, 1.5);
  await page.waitForTimeout(300);
  await page.reload();
  await gotoMonitor(page, false);
  await openCog(page);
  r.afterReload = { value: await input.inputValue(), stored: await page.evaluate(() => localStorage.getItem('yawble.monitor.layout')) };
  expect(r.afterReload.value).toBe('1.5');
  f = await settle(page, 3);
  r.at15 = measure(f, 3);
  // Reset.
  await page.locator('[data-test=monitor-layout-reset]').click();
  r.afterReset = { value: await input.inputValue(), stored: await page.evaluate(() => localStorage.getItem('yawble.monitor.layout')), resetDisabled: await page.locator('[data-test=monitor-layout-reset]').isDisabled() };
  expect(r.afterReset.value).toBe('1');
  f = await settle(page, 3);
  r.afterResetMeasure = measure(f, 3);
  results.b089.setting = r;
  console.log('B089 setting:', JSON.stringify(r));
  expect(errors).toEqual([]);
  await ctx.close();
});

for (const [stored, want] of [['{"spacing":9}', '3'], ['{"spacing":0.01}', '0.25'], ['{"spacing":"abc"}', '1'], ['{"spacing":1.234}', '1.25'], ['{"spacing":-2}', '0.25'], ['not json', '1']] as const) {
  test(`403 clamp: stored ${stored} reads as ${want}`, async ({ browser }) => {
    const { page, ctx, errors } = await open(browser, { w: 1280, teams: 2, layout: stored });
    await openCog(page);
    const v = await page.locator('[data-test=monitor-layout-spacing]').inputValue();
    (results.b089.clamp ??= {})[stored] = v;
    expect(v).toBe(want);
    expect(errors).toEqual([]);
    await ctx.close();
  });
}

test('403 setting at 375 light: the phone cog shows Team spacing and moves the teams', async ({ browser }) => {
  const { page, ctx, errors } = await open(browser, { w: 375, theme: 'light', teams: 3 });
  let f = await settle(page, 3);
  const a = measure(f, 3);
  await openCog(page);
  await expect(page.locator('[data-test=monitor-layout] label.force').filter({ hasText: 'Team spacing' })).toBeVisible();
  await page.screenshot({ path: path.join(S89, 'screens', 'cog-375-light.png') });
  await sliderSet(page, 2);
  await page.locator('[data-test=monitor-layout-close]').click();
  f = await settle(page, 3);
  const b = measure(f, 3);
  const hubs = await hubsOnScreen(page, f, 3);
  results.b089.phone = { at1: a, at2: b, hubs };
  await page.screenshot({ path: path.join(S89, 'screens', 'spacing-2x-375-light.png') });
  expect(b.dist['alpha-bravo']! / a.dist['alpha-bravo']!).toBeGreaterThan(1.6);
  expect.soft(hubs.every((h) => h.inside), 'Auto fit keeps the hubs on the phone wall').toBe(true);
  expect(errors).toEqual([]);
  await ctx.close();
});

// =====================================================================================================================
// Card 405 (B090): no panel; the node labels.

for (const [theme, w, activity] of [['dark', 1280, 'open'], ['light', 1280, 'open'], ['dark', 375, 'open'], ['light', 375, 'open'], ['dark', 1280, 'collapsed'], ['light', 1280, 'collapsed']] as const) {
  test(`405 labels ${theme} ${w} activity ${activity}: no panel, "#id title" as the rule, >= 8 px, overlaps, hover / click / drag`, async ({ browser }) => {
    const r: Record<string, any> = {};
    const tag = `${theme}-${w}${activity === 'collapsed' ? '-noactivity' : ''}`;
    results.b090[`labels-${tag}`] = r;
    const { page, ctx, errors } = await open(browser, { w, theme, teams: 3, activity });
    let f = await settle(page, 3);
    const k = zoomOf(f);
    const box = await canvasBox(page);
    const labels = wfLabels(f);
    r.zoom = +k.toFixed(3); r.wall = Math.round(box.width);
    r.labels = labels.map((l) => l.text);
    r.mismatch = labels.filter((l) => l.text !== expectLabel(l.id, k, box.width)).map((l) => ({ got: l.text, want: expectLabel(l.id, k, box.width) }));
    r.screenPx = labels.map((l) => +(l.font * k).toFixed(2));
    r.maxLen = Math.max(...labels.map((l) => l.text.length));
    r.panelWords = f.ops.filter((o) => o.op === 'text' && /workflows/i.test(o.text!)).map((o) => o.text);
    // Overlap: label boxes (est. 0.55 em a character) that intersect.
    const boxes = labels.map((l) => { const wd = l.text.length * l.font * 0.55 * k; const s = toScreen(l.op, l.x, l.y); return { id: l.id, x0: s.x - wd / 2, x1: s.x + wd / 2, y0: s.y, y1: s.y + l.font * k * 1.2 }; });
    const over: string[] = [];
    let overArea = 0;
    for (let a = 0; a < boxes.length; a++) for (let b = a + 1; b < boxes.length; b++) {
      const p = boxes[a]!, q = boxes[b]!;
      const ox = Math.min(p.x1, q.x1) - Math.max(p.x0, q.x0), oy = Math.min(p.y1, q.y1) - Math.max(p.y0, q.y0);
      if (ox > 2 && oy > 2) {
        const bad = ox > 0.3 * Math.min(p.x1 - p.x0, q.x1 - q.x0) && oy > 0.5 * (p.y1 - p.y0);
        over.push(`#${p.id}/#${q.id} ${ox.toFixed(0)}x${oy.toFixed(0)}${bad ? ' BAD' : ''}`); overArea += ox * oy;
      }
    }
    r.overlaps = over;
    r.badOverlaps = over.filter((o) => o.endsWith('BAD')).length;
    r.labelAreaShare = +(boxes.reduce((s, b) => s + (b.x1 - b.x0) * (b.y1 - b.y0), 0) / (box.width * box.height)).toFixed(4);
    await page.screenshot({ path: path.join(S90, 'screens', `labels-${tag}.png`) });
    expect.soft(labels.length, '15 labels').toBe(15);
    expect.soft(r.mismatch, 'labels follow the rule').toEqual([]);
    expect.soft(Math.min(...r.screenPx), 'never under 8 px on screen').toBeGreaterThanOrEqual(7.99);
    expect.soft(r.maxLen).toBeLessThanOrEqual(30);
    expect.soft(r.panelWords, 'no WORKFLOWS panel on the wall').toEqual([]);
    expect.soft(r.badOverlaps, 'labels do not overlap badly (over 30% of the narrower one and half the height)').toBe(0);

    // Where the panel was (260 out from the hub, away from the middle): hover and click do nothing workflow-ish.
    const hubs = TEAMS.map((t) => hubAt(f, t)!);
    const cx = hubs.reduce((s, h) => s + h.x, 0) / 3, cy = hubs.reduce((s, h) => s + h.y, 0) / 3;
    r.panelSpot = [];
    for (const h of hubs) {
      const a = Math.atan2(h.y - cy, h.x - cx);
      const wp = { x: h.x + Math.cos(a) * 260, y: h.y + Math.sin(a) * 260 };
      const s = toScreen(h.op, wp.x, wp.y);
      if (s.x < 5 || s.y < 5 || s.x > box.width - 5 || s.y > box.height - 5) { r.panelSpot.push({ off: true }); continue; }
      await page.mouse.move(box.x + s.x, box.y + s.y);
      await page.waitForTimeout(250);
      const tip = (await page.locator('[data-test=monitor-tip]').count()) ? (await page.locator('[data-test=monitor-tip]').innerText()).replace(/\s+/g, ' ') : null;
      await page.mouse.down(); await page.mouse.up();
      await page.waitForTimeout(300);
      const opened = await page.locator('[data-test^=workflow-]').count();
      r.panelSpot.push({ x: Math.round(s.x), y: Math.round(s.y), tip, opened });
      if (opened) await page.keyboard.press('Escape');
    }
    expect(r.panelSpot.every((p: any) => p.off || (!p.opened && !(p.tip ?? '').match(/workflow/i))), 'the old panel spot opens nothing').toBe(true);
    await page.mouse.move(2, 2);

    // Hover a backlog node: the full title. Then a drag on it opens nothing; a click opens it.
    const target = wfsOf(1)[0]!.id; // bravo's backlog workflow
    const node = async () => { const g = await lastFrame(page); const l = wfLabels(g).find((x) => x.id === target)!; return toScreen(l.op, l.cx, l.cy); };
    let tip = '';
    for (let i = 0; i < 5 && !tip.includes(titleOf(target)); i++) {
      const s = await node();
      await page.mouse.move(box.x + s.x, box.y + s.y);
      await page.waitForTimeout(250);
      tip = (await page.locator('[data-test=monitor-tip]').count()) ? (await page.locator('[data-test=monitor-tip]').innerText()).replace(/\s+/g, ' ') : '';
    }
    r.hoverTip = tip;
    expect(tip, 'the hover shows the full title').toContain(`#${target} ${titleOf(target)}`);
    await page.screenshot({ path: path.join(S90, 'screens', `hover-${tag}.png`) });
    {
      const s = await node();
      await page.mouse.move(box.x + s.x, box.y + s.y);
      await page.mouse.down();
      for (let i = 1; i <= 8; i++) await page.mouse.move(box.x + s.x + i * 8, box.y + s.y + i * 4);
      await page.mouse.up();
      await page.waitForTimeout(500);
      r.dragOpened = await page.locator(`[data-test=workflow-${target}]`).count();
      expect(r.dragOpened, 'a drag opens nothing').toBe(0);
    }
    await page.waitForTimeout(800);
    let opened = 0;
    for (let i = 0; i < 5 && !opened; i++) {
      const s = await node();
      await page.mouse.move(box.x + s.x, box.y + s.y);
      await page.waitForTimeout(120);
      await page.mouse.down(); await page.mouse.up();
      await page.waitForTimeout(400);
      opened = await page.locator(`[data-test=workflow-${target}]`).count();
    }
    r.clickOpened = opened ? await page.locator(`[data-test=workflow-${target}]`).getAttribute('aria-current') : null;
    expect(r.clickOpened, 'a click opens the workflow').toBe('true');
    await page.screenshot({ path: path.join(S90, 'screens', `click-opens-${tag}.png`) });
    console.log(`B090 ${tag}:`, JSON.stringify(r));
    expect(errors).toEqual([]);
    await ctx.close();
  });
}

test('405 375 zoomed out: only "#id"; zoomed in: "#id title" again; labels stay >= 8 px', async ({ browser }) => {
  const { page, ctx, errors } = await open(browser, { w: 375, theme: 'dark', teams: 3 });
  let f = await settle(page, 3);
  const r: Record<string, any> = {};
  // Zoom out with the wheel (pins the camera).
  const box = await canvasBox(page);
  await page.mouse.move(box.x + box.width / 2, box.y + box.height / 2);
  for (let i = 0; i < 6; i++) { await page.mouse.wheel(0, 300); await page.waitForTimeout(80); }
  await page.waitForTimeout(600);
  f = await lastFrame(page);
  r.out = { zoom: +zoomOf(f).toFixed(3), labels: wfLabels(f).map((l) => l.text), px: wfLabels(f).map((l) => +(l.font * zoomOf(f)).toFixed(2)) };
  await page.screenshot({ path: path.join(S90, 'screens', 'phone-zoomed-out.png') });
  expect(r.out.zoom).toBeLessThan(0.6);
  expect(r.out.labels.every((t: string) => /^#\d+$/.test(t)), 'only "#id" zoomed out on a phone').toBe(true);
  expect(Math.min(...r.out.px)).toBeGreaterThanOrEqual(7.99);
  for (let i = 0; i < 14; i++) { await page.mouse.wheel(0, -300); await page.waitForTimeout(80); }
  await page.waitForTimeout(600);
  f = await lastFrame(page);
  r.in = { zoom: +zoomOf(f).toFixed(3), labels: wfLabels(f).map((l) => l.text) };
  await page.screenshot({ path: path.join(S90, 'screens', 'phone-zoomed-in.png') });
  expect(r.in.zoom).toBeGreaterThanOrEqual(0.6);
  if (r.in.labels.length) expect(r.in.labels.some((t: string) => t.includes(' ')), 'titles back zoomed in').toBe(true);
  results.b090.phoneZoom = r;
  console.log('B090 phone zoom:', JSON.stringify(r));
  expect(errors).toEqual([]);
  await ctx.close();
});

test('405 1280 wide zoomed out (Activity hidden, wall over 760): titles stay (only a phone wall drops to "#id")', async ({ browser }) => {
  const { page, ctx, errors } = await open(browser, { w: 1280, teams: 3, activity: 'collapsed' });
  await settle(page, 3);
  const box = await canvasBox(page);
  await page.mouse.move(box.x + box.width / 2, box.y + box.height / 2);
  for (let i = 0; i < 6; i++) { await page.mouse.wheel(0, 300); await page.waitForTimeout(80); }
  await page.waitForTimeout(600);
  const f = await lastFrame(page);
  const r = { wall: Math.round(box.width), zoom: +zoomOf(f).toFixed(3), labels: wfLabels(f).map((l) => l.text), px: wfLabels(f).map((l) => +(l.font * zoomOf(f)).toFixed(2)) };
  results.b090.wideZoomOut = r;
  console.log('B090 wide zoom out:', JSON.stringify(r));
  await page.screenshot({ path: path.join(S90, 'screens', 'wide-zoomed-out.png') });
  expect(r.zoom).toBeLessThan(0.6);
  expect(r.labels.filter((t) => t.includes(' ')).length, 'titles kept on a wide wall').toBe(r.labels.length);
  expect(Math.min(...r.px)).toBeGreaterThanOrEqual(7.99);
  expect(errors).toEqual([]);
  await ctx.close();
});

test('405 focus: the other teams\' labels dim; the focused team\'s stay full', async ({ browser }) => {
  const { page, ctx, errors } = await open(browser, { w: 1280, teams: 3 });
  let f = await settle(page, 3);
  const before = wfLabels(f).map((l) => l.alpha);
  let ok = false;
  for (let i = 0; i < 8 && !ok; i++) {
    f = await lastFrame(page);
    const h = hubAt(f, 'alpha')!;
    const box = await canvasBox(page);
    const s = toScreen(h.op, h.x, h.y);
    await page.mouse.move(box.x + s.x, box.y + s.y); await page.mouse.down(); await page.mouse.up();
    await page.waitForTimeout(150);
    ok = (await page.locator('[data-test=monitor-focus]').count()) > 0;
    if (!ok) await page.waitForTimeout(300);
  }
  expect(ok, 'alpha focused').toBe(true);
  await page.mouse.move(2, 2); // off the hub: a hover dims what is not its neighbour (0.28), which is not the focus
  await page.waitForTimeout(3000);
  f = await lastFrame(page);
  const mineL = wfLabels(f).filter((l) => wfsOf(0).some((w) => w.id === l.id));
  const mine = mineL.map((l) => l.alpha);
  const others = wfLabels(f).filter((l) => !wfsOf(0).some((w) => w.id === l.id)).map((l) => l.alpha);
  // The squares' alpha (the fill before each label) too, to see whether a dim label goes with a dim node.
  const sq = f.ops.filter((o) => o.op === 'fill' && o.style === 'rgba(7, 11, 20, 0.95)' && o.items?.[0]?.t === 'm').map((o) => +o.alpha.toFixed(3));
  const r = {
    before: [Math.min(...before), Math.max(...before)], focused: [Math.min(...mine), Math.max(...mine)], others: others.length ? [Math.min(...others), Math.max(...others)] : [], othersDrawn: others.length,
    mine: mineL.map((l) => ({ id: l.id, text: l.text, alpha: +l.alpha.toFixed(3), px: +(l.font * zoomOf(f)).toFixed(2) })), squareAlphas: sq, zoom: +zoomOf(f).toFixed(3),
  };
  results.b090.focus = r;
  console.log('B090 focus:', JSON.stringify(r));
  await page.screenshot({ path: path.join(S90, 'screens', 'focus-dims.png') });
  expect(Math.min(...mine)).toBeGreaterThan(0.9);
  if (others.length) expect(Math.max(...others), 'other teams dimmed').toBeLessThan(0.6);
  expect(errors).toEqual([]);
  await ctx.close();
});

test('405 reduced motion: the labels and nodes do not animate (no turning arc, still labels)', async ({ browser }) => {
  const { page, ctx, errors } = await open(browser, { w: 1280, teams: 3, reduced: true });
  await settle(page, 3);
  await page.waitForTimeout(1500);
  const fs1 = await recent(page, 1);
  await page.waitForTimeout(1500);
  const fs2 = await recent(page, 1);
  const a = wfLabels(fs1[0]!), b = wfLabels(fs2[0]!);
  const moved = a.map((l) => { const m = b.find((x) => x.id === l.id); return m ? Math.hypot(m.x - l.x, m.y - l.y) + Math.abs(m.alpha - l.alpha) + Math.abs(m.font - l.font) : 99; });
  // The working arc (r + 4 = 14, not a full turn) round a node.
  const arcs = (f: Frame) => f.ops.filter((o) => o.op === 'stroke' && o.items?.length === 1 && o.items[0].t === 'arc' && Math.abs(o.items[0].r - 14) < 0.01 && Math.abs(o.items[0].a1 - o.items[0].a0 - Math.PI * 0.6) < 0.01).length;
  const r = { maxMove: +Math.max(...moved).toFixed(3), turningArcs: arcs(fs2[0]!), sameText: a.every((l) => b.some((x) => x.id === l.id && x.text === l.text)) };
  results.b090.reduced = r;
  console.log('B090 reduced:', JSON.stringify(r));
  await page.screenshot({ path: path.join(S90, 'screens', 'reduced-motion.png') });
  expect(r.maxMove, 'labels still').toBeLessThan(0.5);
  expect(r.turningArcs, 'no turning arc').toBe(0);
  expect(r.sameText).toBe(true);
  expect(errors).toEqual([]);
  await ctx.close();
});

test('405 a workflow that completes flashes green and leaves, one closed fades; both show in Activity', async ({ browser }) => {
  const { page, ctx, feed, errors } = await open(browser, { w: 1280, teams: 2 });
  await settle(page, 2);
  const done = wfsOf(0)[1]!, closed = wfsOf(1)[2]!;
  const rowsBefore = await page.locator('[data-test=monitor-row]').count();
  await page.evaluate(() => { (window as any).__frames = []; });
  feed.wf(1, done.id, 'completed', [], done.title);
  feed.row('done', 'alpha', null, 'Manager', done.id, 'Delivered the refunds');
  feed.wf(2, closed.id, 'closed', [], closed.title);
  feed.row('done', 'bravo', null, 'Manager', closed.id, 'Closed: not needed');
  const end = Date.now() + 12_000;
  let greens = 0, greys = 0, gone = false;
  while (Date.now() < end) {
    for (const f of await recent(page, 20)) { greens = Math.max(greens, wfBoxCount(f, DONE)); greys = Math.max(greys, wfBoxCount(f, INK_DIM)); }
    const f = await lastFrame(page);
    gone = !wfLabels(f).some((l) => l.id === done.id || l.id === closed.id);
    if (gone && greens && greys) break;
    await page.waitForTimeout(150);
  }
  const all: Frame[] = await page.evaluate(() => (window as any).__frames);
  const flashFrames = all.filter((f) => f.ops.some((o) => o.op === 'stroke' && o.style === DONE && o.items?.some((i) => i.t === 'arc' && Math.abs(i.a1 - i.a0 - Math.PI * 2) < 0.01))).length;
  await page.screenshot({ path: path.join(S90, 'screens', 'completed-and-closed.png') });
  const rows = (await page.locator('[data-test=monitor-row]').allInnerTexts()).map((t) => t.replace(/\s+/g, ' '));
  const r = { greenBox: greens, greyBox: greys, flashFrames, labelsGone: gone, rowsBefore, rowsAfter: rows.length, doneRows: rows.filter((t) => /finished the work/.test(t)).slice(0, 4) };
  results.b090.ends = r;
  console.log('B090 ends:', JSON.stringify(r));
  expect(gone, 'both nodes leave').toBe(true);
  expect(greens, 'the completed node drawn green').toBeGreaterThan(0);
  expect(flashFrames, 'a green flash ring').toBeGreaterThan(0);
  expect(greys, 'the closed node fades grey').toBeGreaterThan(0);
  expect(r.doneRows.length, 'Activity shows both').toBeGreaterThanOrEqual(2);
  expect(errors).toEqual([]);
  await ctx.close();
});
