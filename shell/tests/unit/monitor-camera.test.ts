// @vitest-environment happy-dom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { MonitorModel, teamKey, type TeamSnapshot } from '../../src/renderer/src/monitor/model';
import { MonitorScene, PanelArrivedZoom, PanelRestAlpha, panelsMayStep, panelStep } from '../../src/renderer/src/monitor/scene';
import type { StepRow } from '../../src/renderer/src/monitor/steps';
import type { WorkflowSummary } from '../../src/shared/api';

/**
 * Card 385 (B080 F1): with two teams' code maps the camera never settled (zoom 0.30-0.51, the hub moving 50-100 px in
 * 100 ms), so a hub click missed. The workflow panels grow as the wall zooms out and are bodies in the forces: each
 * size step moved the graph, the fit followed, the zoom changed, the panels stepped again; and the forces never cooled.
 */

describe('the workflow panels\' size steps (panelStep, panelsMayStep)', () => {
  it('steps only by 8% or more, and after a step only the same way until the inputs change', () => {
    expect(panelStep(1, 1.05, 0)).toEqual({ k: 1, dir: 0 });
    expect(panelStep(1, 1.6, 0)).toEqual({ k: 1.6, dir: 1 });
    expect(panelStep(1.6, 2.2, 1)).toEqual({ k: 2.2, dir: 1 });
    expect(panelStep(2.2, 1.5, 1)).toEqual({ k: 2.2, dir: 1 }); // back the other way: held
    expect(panelStep(2.2, 1.5, 0)).toEqual({ k: 1.5, dir: -1 }); // an input changed: free again
    expect(panelStep(1.5, 2.4, -1)).toEqual({ k: 1.5, dir: -1 });
  });

  it('while the camera fits on its own, waits for the forces to rest and the camera to arrive', () => {
    expect(panelsMayStep(0.3, 0.5, 0.5)).toBe(false);
    expect(panelsMayStep(PanelRestAlpha, 0.5, 0.5)).toBe(true);
    expect(panelsMayStep(0, 0.4, 0.5)).toBe(false);
    expect(panelsMayStep(0, 0.5 * (1 + PanelArrivedZoom * 0.9), 0.5)).toBe(true);
  });
});

// ---------- the scene, frame by frame on a stub canvas ----------

function stubContext(): CanvasRenderingContext2D {
  const target: Record<string | symbol, unknown> = {};
  return new Proxy(target, {
    get(t, p) {
      if (p in t) return t[p];
      if (p === 'measureText') return (s: string) => ({ width: s.length * 6 });
      return () => ({ addColorStop() {} });
    },
    set(t, p, v) { t[p] = v; return true; },
  }) as unknown as CanvasRenderingContext2D;
}

// The test runs the frames itself: the scene's own requestAnimationFrame loop must not run too.
beforeEach(() => { vi.stubGlobal('requestAnimationFrame', () => 0); vi.stubGlobal('cancelAnimationFrame', () => {}); });
const scenes: MonitorScene[] = [];
afterEach(() => { for (const s of scenes.splice(0)) s.destroy(); document.body.innerHTML = ''; vi.unstubAllGlobals(); });

type Inner = {
  frame: (t: number) => void; cam: { x: number; y: number; k: number }; panelK: number;
  sim: { alpha: () => number }; nodes: Map<string, { x?: number; y?: number }>; mapNodes: unknown[];
};

const TEAM2 = 'checkout-web-with-a-rather-long-team-name';
const member = (name: string, role: 'manager' | 'member' = 'member') => ({ id: 1, name, role, state: 'running' as const, queued: 0, statusLine: null, cli: 'claude' });
const wf = (id: number, busy: string[] = []): WorkflowSummary =>
  ({ id, title: `Workflow ${id}`, owner: null, state: 'open', undeclared: false, busy, ending: null, startedAt: new Date(0).toISOString(), lastAt: new Date(id * 1000).toISOString(), messages: 2 });
const edit = (id: number, team: string, m: string, target: string): StepRow => ({ id, at: new Date().toISOString(), team, member: m, run: 1, kind: 'edit', tool: 'Edit', target });

/** The card 375 test 5 wall: payments (three members, three workflows) and a second team, each with a small code map. */
function wall(w: number, h: number) {
  const canvas = document.createElement('canvas');
  const ctx = stubContext();
  Object.defineProperty(canvas, 'getContext', { value: () => ctx });
  Object.defineProperty(canvas, 'clientWidth', { value: w });
  Object.defineProperty(canvas, 'clientHeight', { value: h });
  document.body.appendChild(canvas);
  const scene = new MonitorScene(canvas);
  scenes.push(scene);
  const teams: TeamSnapshot[] = [
    { id: 1, name: 'payments', members: [member('Manager', 'manager'), member('DeveloperAda'), member('TesterBen')] },
    { id: 2, name: TEAM2, members: [member('Manager', 'manager'), member('DeveloperCleo')] },
  ];
  const m = new MonitorModel();
  m.reconcile(teams, 0);
  m.setWorkflows('payments', [wf(10, ['DeveloperAda is running']), wf(11), wf(12, ['TesterBen is queued'])], 0);
  for (let i = 0; i < 6; i++) {
    m.applyStep(edit(i + 1, 'payments', 'DeveloperAda', `src/refund/f${i}.ts`), 0);
    m.applyStep(edit(i + 20, TEAM2, 'DeveloperCleo', `web/cart/c${i}.ts`), 0);
  }
  scene.model = m;
  scene.setTeams(teams);
  const S = scene as unknown as Inner;
  let t = 0, i = 0;
  /** Runs `ms` of frames (60 a second); the view's poll gives the same teams again every 1.2 s, as live. */
  const run = (ms: number, each?: () => void) => {
    for (const end = t + ms; t < end; i++) {
      t += 1000 / 60;
      if (i % 72 === 0) scene.setTeams(teams.map((x) => ({ ...x, members: x.members.map((y) => ({ ...y })) })));
      S.frame(t);
      each?.();
    }
  };
  /** The payments hub on the wall, in CSS px. */
  const hub = () => {
    const n = S.nodes.get(teamKey('payments'))!;
    return { x: (n.x! - S.cam.x) * S.cam.k + w / 2, y: (n.y! - S.cam.y) * S.cam.k + h / 2 };
  };
  return { scene, S, run, hub };
}

/** Over `ms` more frames: how far the zoom (relative), the camera's centre and the payments hub (screen px) move at most. */
function drift(o: ReturnType<typeof wall>, ms: number) {
  const k0 = o.S.cam.k, x0 = o.S.cam.x, y0 = o.S.cam.y, h0 = o.hub();
  let dk = 0, dc = 0, dh = 0;
  o.run(ms, () => {
    dk = Math.max(dk, Math.abs(o.S.cam.k - k0) / k0);
    dc = Math.max(dc, Math.hypot(o.S.cam.x - x0, o.S.cam.y - y0) * o.S.cam.k);
    const h = o.hub();
    dh = Math.max(dh, Math.hypot(h.x - h0.x, h.y - h0.y));
  });
  return { dk, dc, dh };
}

describe('the camera settles with two teams\' code maps (card 385, F1)', () => {
  for (const [w, h] of [[660, 654], [1280, 800]] as const) {
    it(`at ${w} x ${h}: once nothing new comes, the zoom, the camera and the hub stop moving`, () => {
      const o = wall(w, h);
      o.run(12_000);
      expect(o.S.mapNodes.length, 'both maps are on the wall').toBeGreaterThan(10);
      // Before the fix the zoom swung 0.30-0.51 and the hub moved 50-100 px per 100 ms, for ever.
      const d = drift(o, 4_000);
      expect(d.dk, 'zoom').toBeLessThan(0.002);
      expect(d.dc, 'camera centre, px').toBeLessThan(1);
      expect(d.dh, 'payments hub, px').toBeLessThan(1);
      expect(o.S.sim.alpha(), 'the forces at rest, polls do not wake them').toBeLessThanOrEqual(0.001);
      const at = o.hub();
      expect(at.x > 0 && at.x < w && at.y > 0 && at.y < h, 'the hub is on the wall').toBe(true);
    });
  }

  it('focusing a team zooms in on it and settles again; leaving settles too', () => {
    const o = wall(660, 654);
    o.run(12_000);
    const k0 = o.S.cam.k;
    o.scene.setFocus(1);
    o.run(10_000);
    expect(o.S.cam.k, 'zoomed in').toBeGreaterThan(k0 * 1.15);
    let d = drift(o, 3_000);
    expect(d.dk).toBeLessThan(0.002);
    expect(d.dh).toBeLessThan(1);
    o.scene.setFocus(null);
    o.run(10_000);
    d = drift(o, 3_000);
    expect(d.dk).toBeLessThan(0.002);
    expect(d.dh).toBeLessThan(1);
  });

  it('zoomed by hand the panels follow the zoom at once (no wait for the forces)', () => {
    const o = wall(1280, 800);
    o.run(8_000);
    const before = o.S.panelK;
    const canvas = document.querySelector('canvas')!;
    for (let i = 0; i < 12; i++) canvas.dispatchEvent(new WheelEvent('wheel', { deltaY: 120, clientX: 640, clientY: 400, bubbles: true, cancelable: true }));
    o.run(50);
    expect(o.S.cam.k).toBeLessThan(0.3);
    expect(o.S.panelK).toBeGreaterThan(before * 1.5);
  });
});
