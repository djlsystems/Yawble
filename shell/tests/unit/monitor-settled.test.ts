// @vitest-environment happy-dom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { MonitorModel, teamKey, type TeamSnapshot } from '../../src/renderer/src/monitor/model';
import { cameraAtTarget, layoutAtRest, MonitorScene, RestAlpha } from '../../src/renderer/src/monitor/scene';
import type { StepRow } from '../../src/renderer/src/monitor/steps';
import type { DeliverableSummary } from '../../src/shared/api';

/**
 * The wall says when it is at rest: the canvas's data-settled is "true" once the forces cool, the hubs are home and the
 * map nodes are in place, and "false" again when something wakes it (a drag, new nodes, new homes); data-camera-idle is
 * "true" once the camera has eased to where it aims. Frames are run by hand on a stub canvas (as monitor-camera.test.ts).
 */

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

beforeEach(() => {
  vi.stubGlobal('requestAnimationFrame', () => 0); vi.stubGlobal('cancelAnimationFrame', () => {});
  let seed = 11;
  vi.spyOn(Math, 'random').mockImplementation(() => (seed = (seed * 1103515245 + 12345) % 2147483648) / 2147483648);
});
const scenes: MonitorScene[] = [];
afterEach(() => { for (const s of scenes.splice(0)) s.destroy(); document.body.innerHTML = ''; vi.unstubAllGlobals(); vi.restoreAllMocks(); });

type Inner = { frame: (t: number) => void; sim: { alpha: () => number } };

const member = (name: string, role: 'manager' | 'member' = 'member') => ({ id: 1, name, role, state: 'running' as const, queued: 0, statusLine: null, cli: 'claude' });
const wf = (id: number, busy: string[] = []): DeliverableSummary =>
  ({ id, title: `Deliverable ${id}`, owner: null, state: 'open', undeclared: false, busy, ending: null, startedAt: new Date(0).toISOString(), lastAt: new Date(id * 1000).toISOString(), messages: 2 });
const edit = (id: number, team: string, m: string, target: string): StepRow => ({ id, at: new Date().toISOString(), team, member: m, run: 1, kind: 'edit', tool: 'Edit', target });

function wall(w = 1280, h = 800, teamCount = 2) {
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
    { id: 2, name: 'checkout', members: [member('Manager', 'manager'), member('DeveloperCleo')] },
  ].slice(0, teamCount);
  const m = new MonitorModel();
  m.reconcile(teams, 0);
  m.setDeliverables('payments', [wf(10, ['DeveloperAda is running']), wf(11)], 0);
  for (let i = 0; i < 4; i++) m.applyStep(edit(i + 1, 'payments', 'DeveloperAda', `src/refund/f${i}.ts`), 0);
  scene.model = m;
  scene.setTeams(teams);
  const S = scene as unknown as Inner;
  let t = 0;
  const run = (ms: number) => { for (const end = t + ms; t < end;) { t += 1000 / 60; S.frame(t); } };
  /** Runs frames until data-settled and data-camera-idle are both "true" (at most `ms`); the time it took, or null. */
  const untilStill = (ms: number) => {
    const t0 = t;
    for (const end = t + ms; t < end;) {
      t += 1000 / 60; S.frame(t);
      if (canvas.dataset.settled === 'true' && canvas.dataset.cameraIdle === 'true') return t - t0;
    }
    return null;
  };
  return { scene, S, canvas, run, untilStill, teams, model: m };
}

describe('layoutAtRest and cameraAtTarget', () => {
  const rest = { alpha: RestAlpha, dragging: false, shifting: false, hubsHome: true, mapAtRest: true, spacingStill: true };
  it('at rest only when every part is', () => {
    expect(layoutAtRest(rest)).toBe(true);
    expect(layoutAtRest({ ...rest, alpha: RestAlpha * 2 })).toBe(false);
    expect(layoutAtRest({ ...rest, dragging: true })).toBe(false);
    expect(layoutAtRest({ ...rest, shifting: true })).toBe(false);
    expect(layoutAtRest({ ...rest, hubsHome: false })).toBe(false);
    expect(layoutAtRest({ ...rest, mapAtRest: false })).toBe(false);
    expect(layoutAtRest({ ...rest, spacingStill: false })).toBe(false);
  });
  it('the camera is idle within 0.1% of the zoom and half a screen pixel of the centre', () => {
    expect(cameraAtTarget({ x: 0, y: 0, k: 1 }, { x: 0, y: 0, k: 1 })).toBe(true);
    expect(cameraAtTarget({ x: 0.4, y: 0, k: 1 }, { x: 0, y: 0, k: 1 })).toBe(true);
    expect(cameraAtTarget({ x: 0.6, y: 0, k: 1 }, { x: 0, y: 0, k: 1 })).toBe(false);
    expect(cameraAtTarget({ x: 0.4, y: 0, k: 2 }, { x: 0, y: 0, k: 2 }), 'half a pixel on screen, at the zoom').toBe(false);
    expect(cameraAtTarget({ x: 0, y: 0, k: 1.0005 }, { x: 0, y: 0, k: 1 })).toBe(true);
    expect(cameraAtTarget({ x: 0, y: 0, k: 1.01 }, { x: 0, y: 0, k: 1 })).toBe(false);
  });
});

describe('the canvas says when the wall is at rest', () => {
  for (const teamCount of [1, 2]) {
    it(`${teamCount} team(s): "false" while it lays out, "true" once the forces cool and the camera arrives`, () => {
      const o = wall(1280, 800, teamCount);
      o.run(50);
      expect(o.canvas.dataset.settled, 'a new wall is moving').toBe('false');
      expect(o.canvas.dataset.cameraIdle).toBe('false');
      const took = o.untilStill(30_000);
      expect(took, 'it comes to rest').not.toBeNull();
      expect(o.S.sim.alpha()).toBeLessThanOrEqual(RestAlpha);
      expect(o.scene.settled).toBe(true);
      expect(o.scene.cameraIdle).toBe(true);
      // And stays at rest while nothing changes (the view's poll gives the same teams again).
      for (let i = 0; i < 5; i++) { o.scene.setTeams(o.teams.map((x) => ({ ...x, members: x.members.map((y) => ({ ...y })) }))); o.run(1200); }
      expect(o.canvas.dataset.settled, 'the same teams again do not wake it').toBe('true');
      expect(o.canvas.dataset.cameraIdle).toBe('true');
    });
  }

  it('a new member wakes it ("false"), and it settles again', () => {
    const o = wall();
    expect(o.untilStill(30_000)).not.toBeNull();
    const more = o.teams.map((t) => (t.id === 1 ? { ...t, members: [...t.members, member('ReviewerZoe')] } : t));
    o.scene.setTeams(more);
    o.run(20);
    expect(o.canvas.dataset.settled, 'new nodes wake the forces').toBe('false');
    expect(o.untilStill(30_000), 'and they cool again').not.toBeNull();
  });

  it('a new Team spacing wakes it; a drag holds it awake until let go', () => {
    const o = wall();
    expect(o.untilStill(30_000)).not.toBeNull();
    o.scene.setLayout({ ...(o.scene as unknown as { layout: Record<string, number> }).layout, spacing: 2 } as never);
    o.run(20);
    expect(o.canvas.dataset.settled, 'new homes').toBe('false');
    expect(o.canvas.dataset.cameraIdle, 'the fit follows the teams out').toBe('false');
    expect(o.untilStill(30_000)).not.toBeNull();

    // A drag on a member: pointer down on its node, a move, and the wall is awake while it is held.
    const S = o.scene as unknown as { nodes: Map<string, { x?: number; y?: number; kind: string }>; cam: { x: number; y: number; k: number } };
    const ada = [...S.nodes.entries()].find(([k]) => k !== teamKey('payments') && k.includes('developerada'))?.[1];
    expect(ada).toBeDefined();
    const sx = (ada!.x! - S.cam.x) * S.cam.k + 1280 / 2, sy = (ada!.y! - S.cam.y) * S.cam.k + 800 / 2;
    const ev = (type: string, x: number, y: number) => {
      const e = new MouseEvent(type, { clientX: x, clientY: y, bubbles: true }) as MouseEvent & { pointerId: number; pointerType: string };
      Object.defineProperty(e, 'pointerId', { value: 1 }); Object.defineProperty(e, 'pointerType', { value: 'mouse' });
      return e;
    };
    o.canvas.setPointerCapture = () => {};
    o.canvas.dispatchEvent(ev('pointerdown', sx, sy));
    o.canvas.dispatchEvent(ev('pointermove', sx + 40, sy + 20));
    o.run(5_000);
    expect(o.canvas.dataset.settled, 'held by the pointer').toBe('false');
    o.canvas.dispatchEvent(ev('pointerup', sx + 40, sy + 20));
    expect(o.untilStill(30_000), 'let go, it settles').not.toBeNull();
  });

  it('a focus moves the camera ("camera idle" false) and it arrives again', () => {
    const o = wall();
    expect(o.untilStill(30_000)).not.toBeNull();
    o.scene.setFocus(1);
    o.run(20);
    expect(o.canvas.dataset.cameraIdle, 'easing to the focused team').toBe('false');
    expect(o.untilStill(30_000)).not.toBeNull();
    expect(o.canvas.dataset.cameraIdle).toBe('true');
  });
});
