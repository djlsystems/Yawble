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
//   npx playwright test card403-team-spacing --workers=1      (E2E_SKIP_BUILD=1 reuses out/e2e/renderer)
// Screenshots and results go to $YAWBLE_TEAM_DOCS/b089-team-spacing and b090-workflow-nodes.
import { expect, test, type Browser, type Page } from './phone/fixtures';
import { existsSync, readFileSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { createFakeCore } from './phone/fake-core';
import { ORIGIN, buildRenderer, builtDir, ensureDir } from './phone/server';

test.use({ channel: 'chrome', headless: true, actionTimeout: 8_000 });
test.setTimeout(180_000);

const DOCS = process.env.YAWBLE_TEAM_DOCS || path.resolve(__dirname, '..', '..', 'test-results');
const S89 = path.join(DOCS, 'b089-team-spacing');
const S90 = path.join(DOCS, 'b090-workflow-nodes');
test.beforeAll(async () => {
  test.setTimeout(600_000);
  for (const d of [S89, S90]) ensureDir(path.join(d, 'screens'));
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

/**
 * The rule cards 405 / 409 state, written out here to check what is drawn: "#id title", "Backlog item " dropped, 30 chars
 * with "…"; only "#id" when the WINDOW is phone-wide (under 760, card 409: not the canvas) and zoomed out under 0.6.
 * A crowded label may also fall back to "#id" or be left out (card 409, placeWorkflowLabels): see labelCheck.
 */
function expectLabel(id: number, zoom: number, windowPx: number): string {
  if (windowPx < 760 && zoom < 0.6) return `#${id}`;
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
      // Inside the code map's 2 minute file life (main 6dcc8eb): 35-60 s ago, so the map stays for the whole test.
      const at = feed.iso(60_000 - f * 5_000);
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

/**
 * The workflow nodes' squares (scene drawWorkflowNode: roundRect(x - 10, y - 10, 20, 20, 4), first moveTo at x - 6, y - 10),
 * each with the label drawn right after it, if any (card 409: a label may be left out, so nodes are found by their squares).
 */
const WF_R = 10;
type Label = ReturnType<typeof wfLabels>[number];
type Sq = { x: number; y: number; alpha: number; label: Label | null; id: number | null; op: Op };
function squaresOf(f: Frame): Sq[] {
  const labels = wfLabels(f);
  const out: Sq[] = [];
  let cur: Sq | null = null;
  for (const o of f.ops) {
    if (o.op === 'fill' && o.style === 'rgba(7, 11, 20, 0.95)' && o.items?.[0]?.t === 'm') {
      cur = { x: o.items[0].x - 4 + WF_R, y: o.items[0].y + WF_R, alpha: o.alpha, label: null, id: null, op: o };
      out.push(cur);
      continue;
    }
    if (cur && !cur.label && o.op === 'text') { const l = labels.find((x) => x.op === o); if (l) { cur.label = l; cur.id = l.id; } }
  }
  return out;
}

const tipText = async (page: Page) => ((await page.locator('[data-test=monitor-tip]').count()) ? (await page.locator('[data-test=monitor-tip]').innerText()).replace(/\s+/g, ' ') : '');

/** Hovers every square on the wall and reads its tip: which workflow it is, and whether the tip gives "#id <whole title>". */
async function hoverAll(page: Page): Promise<{ ids: number[]; full: number[]; bySquare: (number | null)[]; misses: string[] }> {
  const box = await canvasBox(page);
  const f = await lastFrame(page);
  const bySquare: (number | null)[] = [];
  const misses: string[] = [];
  const full = new Set<number>();
  for (const s of squaresOf(f)) {
    const p = toScreen(s.op, s.x, s.y);
    if (p.x < 2 || p.y < 2 || p.x > box.width - 2 || p.y > box.height - 2) { bySquare.push(null); misses.push(`${s.id ? '#' + s.id : '?'} off the wall at ${Math.round(p.x)},${Math.round(p.y)}`); continue; }
    let t = '';
    for (let tries = 0; tries < 3 && !/^#\d+/.test(t); tries++) {
      const g = squaresOf(await lastFrame(page)).find((q) => (s.id !== null ? q.id === s.id : Math.hypot(q.x - s.x, q.y - s.y) < 6)) ?? s;
      const q = toScreen(g.op, g.x, g.y);
      await page.mouse.move(box.x + q.x, box.y + q.y);
      await page.waitForTimeout(200);
      t = await tipText(page);
    }
    const id = Number(/^#(\d+)/.exec(t)?.[1] ?? NaN);
    bySquare.push(Number.isFinite(id) ? id : null);
    if (!Number.isFinite(id)) misses.push(`${s.id ? '#' + s.id : '?'} at ${Math.round(p.x)},${Math.round(p.y)} tip "${t.slice(0, 60)}"`);
    if (Number.isFinite(id) && ALL_WFS.some((w) => w.id === id) && t.includes(`#${id} ${titleOf(id)}`)) full.add(id);
  }
  await page.mouse.move(2, 2);
  return { ids: bySquare.filter((x): x is number => x !== null), full: [...full], bySquare, misses };
}

/** The screen point of workflow id's square: by its label if drawn, else by hovering the unlabelled squares. */
async function squareOnScreen(page: Page, id: number): Promise<Pt | null> {
  const f = await lastFrame(page);
  const sqs = squaresOf(f);
  const s = sqs.find((q) => q.id === id);
  if (s) return toScreen(s.op, s.x, s.y);
  const box = await canvasBox(page);
  for (const q of sqs.filter((x) => !x.label)) {
    const p = toScreen(q.op, q.x, q.y);
    await page.mouse.move(box.x + p.x, box.y + p.y);
    await page.waitForTimeout(200);
    if ((await tipText(page)).includes(`#${id} `)) return p;
  }
  return null;
}

/** Text widths as the page's canvas measures them, for (font, text) pairs. */
async function widths(page: Page, pairs: [string, string][]): Promise<number[]> {
  return page.evaluate((ps) => {
    const c = document.createElement('canvas').getContext('2d')!;
    return ps.map(([font, text]) => { c.font = font; return c.measureText(text).width; });
  }, pairs);
}

type WBox = { x: number; y: number; w: number; h: number; what: string; team?: number };
const hit = (a: WBox, b: WBox, slack = 1) => a.x < b.x + b.w - slack && b.x < a.x + a.w - slack && a.y < b.y + b.h - slack && b.y < a.y + a.h - slack;

/**
 * Card 409's label rules on one frame, in world px with the page's own text widths:
 *   overlaps   - a label over another label, any workflow square, a member's disc (r + 5) or name, or a map folder / file dot
 *                (map things and folder names of the label's own team listed apart as ownMapOverlaps; since card 411 (F5)
 *                those are faults too and the tests assert both).
 *   roomMissed - a label left out (or cut to "#id") although one of the scene's own spots (under, above, to a side; full,
 *                then "#id") is clear of everything drawn in the final frame; checking against the final frame is lenient
 *                (later labels only take room after), so a hit here is a real fault.
 */
async function labelCheck(page: Page, f: Frame, n: number, windowPx: number) {
  const k = zoomOf(f);
  const names = TEAMS.slice(0, n);
  const hubs = names.map((t) => hubAt(f, t)!);
  const near = (p: Pt) => { let bi = 0, bd = Infinity; hubs.forEach((h, i) => { const d = Math.hypot(h.x - p.x, h.y - p.y); if (d < bd) { bd = d; bi = i; } }); return bi; };
  const teamOfWf = (id: number) => names.findIndex((_, ti) => wfsOf(ti).some((w) => w.id === id));
  const labels = wfLabels(f);
  const sqs = squaresOf(f);
  const members = f.ops.filter((o) => o.op === 'text' && !isScreen(o) && new Set(Object.values(MEMBERS).flat()).has(o.text!.replace(/ /g, '')));
  const fontOf = labels[0]?.op.font ?? `600 ${11}px sans-serif`;
  const lw = await widths(page, [...labels.map((l) => [l.op.font!, l.text] as [string, string]), ...members.map((o) => [o.font!, o.text!] as [string, string])]);
  const lab: WBox[] = labels.map((l, i) => ({ x: l.x - lw[i]! / 2, y: l.y, w: lw[i]!, h: l.font * 1.2, what: `label #${l.id}`, team: teamOfWf(l.id) }));
  const sqBox: WBox[] = sqs.map((s) => ({ x: s.x - WF_R, y: s.y - WF_R, w: WF_R * 2, h: WF_R * 2, what: `square ${s.id ? '#' + s.id : '?'}` }));
  const memBox: WBox[] = [];
  members.forEach((o, i) => {
    const raw = o.text!.replace(/ /g, ''), r = raw === 'Manager' ? 17 : 14, ring = r + 5;
    const cx = o.x!, cy = o.y! - r - 11;
    memBox.push({ x: cx - ring, y: cy - ring, w: ring * 2, h: ring * 2, what: `disc ${raw}` });
    memBox.push({ x: o.x! - lw[labels.length + i]! / 2, y: o.y!, w: lw[labels.length + i]!, h: 13, what: `name ${raw}` });
  });
  const mapBox: WBox[] = [];
  for (const o of f.ops) {
    if (o.op === 'fill' && o.style === 'rgba(12, 19, 34, 0.92)' && o.items?.[0]?.t === 'm') { const x = o.items[0].x - 2.5, y = o.items[0].y; mapBox.push({ x, y, w: 12, h: 10, what: 'folder', team: near({ x: x + 6, y: y + 5 }) }); }
    if (o.op === 'fill' && FILE_COLORS.includes(o.style) && o.items?.length === 1 && o.items[0].t === 'arc' && Math.abs(o.items[0].r - 3.6) < 0.05) { const { x, y } = o.items[0]; mapBox.push({ x: x - 3.6, y: y - 3.6, w: 7.2, h: 7.2, what: 'file', team: near({ x, y }) }); }
  }
  // The team hubs' name pills (scene drawTeamPill: 700 13px, text width + 18 wide, 22 high, its text centred 11.5 down).
  const pillOps = f.ops.filter((o) => o.op === 'text' && !isScreen(o) && names.includes(o.text!) && o.style === INK);
  const pw = await widths(page, pillOps.map((o) => [o.font!, o.text!] as [string, string]));
  const pillBox: WBox[] = pillOps.map((o, i) => ({ x: o.x! - (pw[i]! + 18) / 2, y: o.y! - 11.5, w: pw[i]! + 18, h: 22, what: `pill ${o.text}` }));
  // The map's folder names (scene drawMapNodes: 600 10px, centred, zoomed in past 0.55).
  const nameOps = f.ops.filter((o) => o.op === 'text' && !isScreen(o) && /^600 10px/.test(o.font ?? '') && !labels.some((l) => l.op === o));
  const nw = await widths(page, nameOps.map((o) => [o.font!, o.text!] as [string, string]));
  const folderNames: WBox[] = nameOps.map((o, i) => ({ x: o.x! - nw[i]! / 2, y: o.y! - 6, w: nw[i]!, h: 12, what: `folder name ${o.text}`, team: near({ x: o.x!, y: o.y! + 13 }) }));
  const overlaps: string[] = [], ownMap: string[] = [];
  lab.forEach((a, i) => {
    const mineSq = sqs.findIndex((s) => s.label === labels[i]);
    lab.forEach((b, j) => { if (j > i && hit(a, b)) overlaps.push(`${a.what} / ${b.what}`); });
    sqBox.forEach((b, j) => { if (j !== mineSq && hit(a, b)) overlaps.push(`${a.what} / ${b.what}`); });
    memBox.forEach((b) => { if (hit(a, b)) overlaps.push(`${a.what} / ${b.what}`); });
    pillBox.forEach((b) => { if (hit(a, b)) overlaps.push(`${a.what} / ${b.what}`); });
    [...mapBox, ...folderNames].forEach((b) => { if (hit(a, b)) (b.team === a.team ? ownMap : overlaps).push(`${a.what} / ${b.team === a.team ? 'own' : names[b.team!]} ${b.what}`); });
  });
  // Room for what was left out or cut short. The other teams' maps as the scene keeps clear of them (80 x 20 round each node).
  const otherMap = (team: number) => mapBox.filter((b) => b.team !== team).map((b) => ({ x: b.x + b.w / 2 - 40, y: b.y + b.h / 2 - 10, w: 80, h: 20, what: 'map zone' }));
  const font = labels[0]?.font ?? nodeFontGuess(k);
  const roomMissed: string[] = [];
  const short: number[] = [], dropped: number[] = [];
  const pending: { s: Sq; id: number; texts: string[] }[] = [];
  for (const s of sqs) {
    if (s.label && s.label.text !== `#${s.label.id}`) continue;
    if (s.label) { short.push(s.label.id); if (expectLabel(s.label.id, k, windowPx) !== `#${s.label.id}`) pending.push({ s, id: s.label.id, texts: [expectLabel(s.label.id, k, windowPx)] }); }
    else pending.push({ s, id: NaN, texts: [] });
  }
  for (const p of pending) {
    if (!Number.isFinite(p.id)) {
      // Which workflow it is: hover it.
      const box = await canvasBox(page);
      const q = toScreen(p.s.op, p.s.x, p.s.y);
      await page.mouse.move(box.x + q.x, box.y + q.y);
      await page.waitForTimeout(200);
      p.id = Number(/#(\d+)/.exec(await tipText(page))?.[1] ?? NaN);
      dropped.push(p.id);
      if (Number.isFinite(p.id)) p.texts = [...new Set([expectLabel(p.id, k, windowPx), `#${p.id}`])];
    }
  }
  if (pending.length) await page.mouse.move(2, 2);
  const tw = await widths(page, pending.flatMap((p) => p.texts.map((t) => [fontOf, t] as [string, string])));
  let ti = 0;
  for (const p of pending) {
    const team = teamOfWf(p.id);
    const mineSq = sqs.indexOf(p.s);
    // Card 411 (F5, the Manager's decision): a label keeps off its own team's map too (folders, file dots, folder names).
    const block = [...lab, ...sqBox.filter((_, j) => j !== mineSq), ...memBox, ...pillBox, ...otherMap(team), ...mapBox.filter((b) => b.team === team), ...folderNames];
    for (const text of p.texts) {
      const w = tw[ti++]!, h = font * 1.2, { x, y } = p.s, r = WF_R;
      const spots: [number, boolean][] = [[x - w / 2, false], [x - w / 2, true], [x - r, false], [x + r - w, false], [x - r, true], [x + r - w, true]];
      // A 2 px margin on top of the scene's 1 px slack, so a near miss of my estimates is not called a fault.
      const free = spots.find(([sx, above]) => { const b = { x: sx, y: above ? y - r - 3 - h : y + r + 3, w, h, what: '' }; return !block.some((o) => hit(b, o, -2)); });
      if (free) { roomMissed.push(`#${p.id} "${text}" fits ${free[1] ? 'above' : 'under'} at x ${Math.round(free[0] - x)}`); break; }
    }
  }
  return { drawn: labels.length, squares: sqs.length, short, dropped, overlaps, ownMapOverlaps: ownMap, roomMissed };
}
const nodeFontGuess = (k: number) => Math.max(9, 8 / k);

/** Each drawn workflow label's width as the page measures it (for measure's gap). */
async function labelWidthMap(page: Page, f: Frame): Promise<Map<string, number>> {
  const ls = wfLabels(f);
  const ws = await widths(page, ls.map((l) => [l.op.font!, l.text] as [string, string]));
  return new Map(ls.map((l, i) => [l.text, ws[i]!]));
}

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

/** Waits until every team's hub, its 5 workflow squares and its map are drawn and the hubs stop moving (under 1 px in 600 ms). */
async function settle(page: Page, n: number, timeout = 20_000): Promise<Frame> {
  const end = Date.now() + timeout;
  let prev: Pt[] | null = null;
  let f = await lastFrame(page);
  while (Date.now() < end) {
    f = await lastFrame(page);
    const hubs = TEAMS.slice(0, n).map((t) => hubAt(f, t));
    const ready = hubs.every(Boolean) && squaresOf(f).length >= n * 5 && mapThings(f).length >= n * (FOLDERS + FOLDERS * FILES);
    // Card 411: the hubs are held at their homes now, so they stand still from the start; the members and the workflow
    // squares settle after them (and the teams' homes are measured once those come to rest), so they are watched too.
    const pts: Pt[] = ready ? [...(hubs as Pt[]), ...membersOf(f).map((m) => ({ x: m.x, y: m.y })), ...squaresOf(f).map((s) => ({ x: s.x, y: s.y }))] : [];
    if (ready && prev && prev.length === pts.length && pts.every((h, i) => Math.hypot(h.x - prev![i]!.x, h.y - prev![i]!.y) < 1)) return f;
    prev = ready ? pts : null;
    await page.waitForTimeout(600);
  }
  return f;
}

/** The hub distances, the cluster gap and the empty middle (see the header). */
function measure(f: Frame, n: number, lw?: Map<string, number>) {
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
  // Squares by nearest hub (a node sits by its members, well inside its own cluster); labels by their workflow.
  for (const s of squaresOf(f)) {
    const i = s.id !== null ? names.findIndex((_, ti) => wfsOf(ti).some((w) => w.id === s.id)) : near(s);
    if (i >= 0) things[i]!.push({ x: s.x, y: s.y, r: WF_R });
  }
  for (const l of wfLabels(f)) {
    const i = names.findIndex((_, ti) => wfsOf(ti).some((w) => w.id === l.id));
    if (i < 0) continue;
    const w = lw?.get(l.text) ?? l.text.length * l.font * 0.55; // card 410: the page's own width when given
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
    const m = measure(f, n, await labelWidthMap(page, f));
    const screen = await hubsOnScreen(page, f, n);
    // Card 412 (F6, steady): measured again 4 s later; a late settleSpacing move shows here (hubs moved, gap changed).
    await page.waitForTimeout(4000);
    const f2 = await lastFrame(page);
    const m2 = measure(f2, n, await labelWidthMap(page, f2));
    const hubMove = Math.round(Math.max(...TEAMS.slice(0, n).map((t) => { const a = hubAt(f, t)!, b = hubAt(f2, t)!; return Math.hypot(a.x - b.x, a.y - b.y); })));
    results.b089[`${theme}-${w}-${n}`] = { ...m, hubsOnScreen: screen, later: { gap: m2.gap, zoom: m2.zoom, hubMove } };
    console.log(`B089 ${theme} ${w} ${n} teams:`, JSON.stringify({ ...m }));
    await page.screenshot({ path: path.join(S89, 'screens', `spacing-${theme}-${w}-${n}teams.png`) });
    expect(m.mapCount, 'each team gets exactly its own map (6 folders + 24 files) by nearest hub').toEqual(Array(n).fill(FOLDERS * (FILES + 1)));
    expect(m.gap, 'clusters do not overlap').toBeGreaterThan(0);
    expect(m.gap, 'clusters sit close').toBeLessThan(160);
    // Card 410: the middle is judged in world px (< 160), except 3 teams on a phone wall: there the wall sits near the zoom
    // floor (about 0.24) and the labels are "#id" only, so the measure sees the bare triangle between the clusters; it is
    // judged on screen there (< 60 screen px). See b089-team-spacing/REPORT.md (card 410) for why.
    const middleScreen = Math.round(m.middle * m.zoom);
    results.b089[`${theme}-${w}-${n}`].middleScreen = middleScreen;
    if (w < 760 && n === 3) expect(middleScreen, `no empty middle on screen (${m.middle} world px at zoom ${m.zoom})`).toBeLessThan(60);
    else expect(m.middle, 'no empty middle').toBeLessThan(160);
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
    // Card 409: a drawn label is the rule's text, or "#id" when crowded.
    r.mismatch = labels.filter((l) => l.text !== expectLabel(l.id, k, w) && l.text !== `#${l.id}`).map((l) => ({ got: l.text, want: expectLabel(l.id, k, w) }));
    r.withTitle = labels.filter((l) => l.text.includes(' ')).length;
    r.screenPx = labels.map((l) => +(l.font * k).toFixed(2));
    r.maxLen = Math.max(...labels.map((l) => l.text.length));
    r.panelWords = f.ops.filter((o) => o.op === 'text' && /workflows/i.test(o.text!)).map((o) => o.text);
    r.check = await labelCheck(page, f, 3, w);
    await page.screenshot({ path: path.join(S90, 'screens', `labels-${tag}.png`) });
    // Every workflow keeps at least its hover title.
    const hv = await hoverAll(page);
    r.hover = { squares: hv.bySquare.length, ids: hv.ids.length, fullTitles: hv.full.length, missing: ALL_WFS.map((x) => x.id).filter((id) => !hv.full.includes(id)), misses: hv.misses };
    expect.soft(r.check.squares, '15 workflow squares').toBe(15);
    expect.soft(r.hover.missing, 'every workflow gives its whole title on hover').toEqual([]);
    expect.soft(r.mismatch, 'labels follow the rule (or "#id" when crowded)').toEqual([]);
    if (w >= 760 && k >= 0.6) expect.soft(r.withTitle, 'a desktop wall at zoom >= 0.6 shows titles').toBeGreaterThan(0);
    expect.soft(Math.min(...r.screenPx), 'never under 8 px on screen').toBeGreaterThanOrEqual(7.99);
    expect.soft(r.maxLen).toBeLessThanOrEqual(30);
    expect.soft(r.panelWords, 'no WORKFLOWS panel on the wall').toEqual([]);
    expect.soft(r.check.overlaps, 'no label over a label, square, member disc or name, or another team\'s map').toEqual([]);
    expect.soft(r.check.roomMissed, 'no label left out or cut short when it had room').toEqual([]);
    // Card 412 (F5, the Manager's decision): a label over its own team's map (folder, file dot, folder name) is a fault too.
    expect.soft(r.check.ownMapOverlaps, 'no label over its own team\'s map').toEqual([]);

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
    const node = async () => (await squareOnScreen(page, target)) ?? { x: -100, y: -100 };
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
  r.outCheck = await labelCheck(page, f, 3, 375);
  expect.soft(r.outCheck.overlaps, 'phone zoomed out: no overlaps').toEqual([]);
  expect.soft(r.outCheck.roomMissed, 'phone zoomed out: nothing left out that had room').toEqual([]);
  expect.soft(r.outCheck.ownMapOverlaps, 'phone zoomed out: no label over its own map').toEqual([]);
  await page.mouse.move(box.x + box.width / 2, box.y + box.height / 2); // labelCheck's hovers moved the mouse off the wall
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
  // Card 410: step out one wheel notch at a time; the titles are judged at the first zoom under 0.6 (where the phone
  // rule would cut them), and the far end (about 0.15) only reports how many are left out, with the room check.
  const steps: any[] = [];
  for (let i = 0; i < 6; i++) {
    await page.mouse.wheel(0, 300);
    await page.waitForTimeout(700);
    const f = await lastFrame(page);
    const labels = wfLabels(f);
    const check = await labelCheck(page, f, 3, 1280);
    const zoom = zoomOf(f);
    steps.push({ zoom: +zoom.toFixed(3), squares: check.squares, drawn: labels.length, withTitle: labels.filter((l) => l.text.includes(' ')).length, wrongTitle: labels.filter((l) => l.text.includes(' ') && l.text !== expectLabel(l.id, zoom, 1280)).map((l) => l.text), idOnly: labels.filter((l) => !l.text.includes(' ')).map((l) => l.id), dropped: check.dropped, overlaps: check.overlaps, ownMap: check.ownMapOverlaps, roomMissed: check.roomMissed, px: Math.min(...labels.map((l) => +(l.font * zoomOf(f)).toFixed(2))) });
    await page.mouse.move(box.x + box.width / 2, box.y + box.height / 2);
    if (i === 5) await page.screenshot({ path: path.join(S90, 'screens', 'wide-zoomed-out.png') });
  }
  const fit = steps.find((s) => s.zoom < 0.6)!;
  const far = steps.at(-1)!;
  const r = { wall: Math.round(box.width), steps, judgedAt: fit.zoom, farZoom: far.zoom, farDropped: far.dropped.length };
  results.b090.wideZoomOut = r;
  console.log('B090 wide zoom out:', JSON.stringify(r));
  expect(fit, 'a zoom under 0.6 reached').toBeTruthy();
  // Titles kept: no label is cut to "#id" by the phone rule on a wide wall. A label that is "#id" or left out must have had
  // no room (roomMissed below); the rest carry their title.
  // Card 500: this was "at least 10 of the 15 keep a title", which failed about half the runs with the scene right each
  // time (no overlaps, nothing left out that had room). The workflow nodes are born at a random offset beside their member
  // (scene.ts rebuild), so the forces settle a different layout each run, and how many 30-character titles find room at
  // zoom ~0.37 went from 8 to 12. So every node is accounted for instead: it shows exactly its wide-wall title, or it is
  // "#id" or left out (and roomMissed proves it had no room). At least one title is drawn: the phone rule would cut all.
  expect(fit.squares, 'all 15 workflow nodes drawn').toBe(ALL_WFS.length);
  expect.soft(fit.withTitle + fit.idOnly.length + fit.dropped.length, `every node titled, "#id" or left out at zoom ${fit.zoom}`).toBe(fit.squares);
  expect.soft(fit.dropped.filter((id: number) => !Number.isFinite(id)), 'each left-out label\'s node named by its hover (so its room was checked)').toEqual([]);
  expect.soft(fit.wrongTitle, 'a drawn title is the wide-wall label, not a phone cut').toEqual([]);
  expect.soft(fit.withTitle, `labels keep a title at zoom ${fit.zoom} (the phone rule would leave none)`).toBeGreaterThan(0);
  for (const s of steps) {
    expect.soft(s.overlaps, `zoom ${s.zoom}: no overlaps`).toEqual([]);
    expect.soft(s.roomMissed, `zoom ${s.zoom}: nothing left out that had room`).toEqual([]);
    expect.soft(s.ownMap, `zoom ${s.zoom}: no label over its own map`).toEqual([]);
    expect.soft(s.px).toBeGreaterThanOrEqual(7.99);
  }
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
