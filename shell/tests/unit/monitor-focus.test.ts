// @vitest-environment happy-dom
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { YawbleApi } from '../../src/shared/api';
import type { Effect } from '../../src/renderer/src/monitor/model';

// Card 374 (B080): the Monitor view's focus on a team, density, You-node click and sound. The wall is a fake scene with
// DeveloperTomas's setFocus / setDensity / hitYouNode (card 373); a scene picks on its own pointerdown, as the real one.
type Fake = {
  onPick?: (k: string | null) => void; onOpenWorkflow?: (team: string, id?: number) => void;
  focus: (number | null)[]; density: string[]; you: { x: number; y: number; r: number } | null; nextPick: string | null;
};
const scenes: Fake[] = [];
let withExtras = true;
vi.mock('../../src/renderer/src/monitor/scene', async (orig) => {
  const real = await orig<typeof import('../../src/renderer/src/monitor/scene')>();
  class FakeScene {
    model: unknown; onHover?: unknown; onPick?: (k: string | null) => void; onOpenWorkflow?: unknown; effects: unknown[] = [];
    focus: (number | null)[] = []; density: string[] = []; you: { x: number; y: number; r: number } | null = null; nextPick: string | null = null;
    constructor(cv: HTMLCanvasElement) {
      scenes.push(this as unknown as Fake);
      cv.addEventListener('pointerdown', () => { if (this.nextPick) { const k = this.nextPick; this.nextPick = null; this.onPick?.(k); } });
      if (withExtras) {
        Object.assign(this, {
          setFocus: (id: number | null) => { this.focus.push(id); },
          setDensity: (d: string) => { this.density.push(d); },
          hitYouNode: (x: number, y: number) => !!this.you && Math.hypot(x - this.you.x, y - this.you.y) <= this.you.r,
        });
      }
    }
    setPinned() {} setLayout() {} setTeams() {} setPicked() {} destroy() {} fit() {} clearFiles() {}
  }
  return { ...real, MonitorScene: FakeScene };
});

const { default: MonitorView } = await import('../../src/renderer/src/monitor/MonitorView.vue');
const snd = await import('../../src/renderer/src/monitor/sound');
const lay = await import('../../src/renderer/src/monitor/layout');

let teams = [
  { id: 1, name: 'payments', repoPath: null, members: 1, running: 0, queued: 0 },
  { id: 2, name: 'alpha', repoPath: null, members: 1, running: 0, queued: 0 },
];
function api(): YawbleApi {
  return {
    call: async (method: string) => {
      switch (method) {
        case 'app.overview': return { slots: { running: 0, limit: 8, queued: 0 }, teams } as any;
        case 'monitor.messages': return { end: 0, messages: [] } as any;
        case 'monitor.steps': return { end: 0, pending: false, steps: [] } as any;
        default: return [] as any;
      }
    },
    onEvent: () => () => {},
  } as unknown as YawbleApi;
}
const settle = async (n = 6) => { for (let i = 0; i < n; i++) await flushPromises(); };
const mounted: VueWrapper[] = [];
async function view() {
  const w = mount(MonitorView, { props: { api: api(), pollMs: 60_000 }, attachTo: document.body });
  mounted.push(w);
  await settle();
  return { w, scene: scenes.at(-1)!, cv: w.get('[data-test=monitor-canvas]').element as HTMLCanvasElement };
}
function pointer(cv: HTMLCanvasElement, type: string, x: number, y: number, id = 1) {
  cv.dispatchEvent(new PointerEvent(type, { bubbles: true, clientX: x, clientY: y, pointerId: id }));
}
/** A click (down and up at the same place) or a drag (up `move` px away). */
function click(cv: HTMLCanvasElement, x: number, y: number, move = 0) {
  pointer(cv, 'pointerdown', x, y);
  pointer(cv, 'pointerup', x + move, y);
}

beforeEach(() => {
  localStorage.clear(); document.body.innerHTML = ''; scenes.length = 0; withExtras = true;
  teams = [
    { id: 1, name: 'payments', repoPath: null, members: 1, running: 0, queued: 0 },
    { id: 2, name: 'alpha', repoPath: null, members: 1, running: 0, queued: 0 },
  ];
  HTMLCanvasElement.prototype.getContext = (() => ({})) as any;
});
afterEach(() => { for (const w of mounted.splice(0)) w.unmount(); vi.unstubAllGlobals(); });

describe('focus on a team', () => {
  it('a click on a team hub focuses it (scene.setFocus with its id); the chip names it', async () => {
    const { w, scene, cv } = await view();
    expect(w.find('[data-test=monitor-focus]').exists()).toBe(false);
    scene.nextPick = 't:alpha';
    click(cv, 100, 100);
    await settle(2);
    expect(scene.focus).toEqual([2]);
    expect(w.get('[data-test=monitor-focus]').text()).toContain('alpha');
    // A member's pick keeps the focus; another team's hub moves it.
    scene.nextPick = 'm:alpha/ines';
    click(cv, 120, 100);
    scene.nextPick = 't:payments';
    click(cv, 140, 100);
    await settle(2);
    expect(scene.focus).toEqual([2, 1]);
  });

  it('Esc leaves the focus; Esc in a text field does not', async () => {
    const { w, scene } = await view();
    scene.onPick!('t:alpha');
    await settle(2);
    const input = document.createElement('input');
    document.body.appendChild(input);
    input.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
    await settle(2);
    expect(scene.focus).toEqual([2]);
    window.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' }));
    await settle(2);
    expect(scene.focus).toEqual([2, null]);
    expect(w.find('[data-test=monitor-focus]').exists()).toBe(false);
  });

  it('a click on the empty wall leaves; a drag (pan) or a click that picks does not', async () => {
    const { w, scene, cv } = await view();
    scene.onPick!('t:alpha');
    click(cv, 50, 50, 30); // a pan
    await settle(2);
    expect(scene.focus).toEqual([2]);
    scene.nextPick = 'm:alpha/ines';
    click(cv, 50, 50);
    await settle(2);
    expect(scene.focus).toEqual([2]);
    click(cv, 300, 200);
    await settle(2);
    expect(scene.focus).toEqual([2, null]);
    expect(w.find('[data-test=monitor-focus]').exists()).toBe(false);
  });

  it("the chip's x leaves; a focused team that is gone leaves too", async () => {
    const { w, scene } = await view();
    scene.onPick!('t:alpha');
    await settle(2);
    await w.get('[data-test=monitor-focus-leave]').trigger('click');
    expect(scene.focus).toEqual([2, null]);
  });

  it('a focused team that is gone from the next snapshot leaves the focus', async () => {
    const w = mount(MonitorView, { props: { api: api(), pollMs: 30 }, attachTo: document.body });
    mounted.push(w);
    await settle();
    const scene = scenes.at(-1)!;
    scene.onPick!('t:payments');
    await settle(2);
    expect(scene.focus).toEqual([1]);
    teams = teams.filter((t) => t.id !== 1);
    await new Promise((r) => setTimeout(r, 120));
    await settle();
    expect(scene.focus).toEqual([1, null]);
    expect(w.find('[data-test=monitor-focus]').exists()).toBe(false);
  });

  it('a scene without setFocus: the view still shows the chip, no error', async () => {
    withExtras = false;
    const { w, scene, cv } = await view();
    scene.onPick!('t:alpha');
    await settle(2);
    expect(w.get('[data-test=monitor-focus]').text()).toContain('alpha');
    click(cv, 10, 10);
    await settle(2);
    expect(w.find('[data-test=monitor-focus]').exists()).toBe(false);
  });
});

describe('the You node', () => {
  it('a click on it opens Needs you (and leaves any focus as it is)', async () => {
    const { w, scene, cv } = await view();
    scene.you = { x: 200, y: 150, r: 20 };
    scene.onPick!('t:alpha');
    await settle(2);
    click(cv, 205, 150);
    await settle(2);
    expect(w.emitted('openNeedsYou')).toHaveLength(1);
    expect(scene.focus).toEqual([2]);
    click(cv, 205, 150, 20); // a drag over it opens nothing
    expect(w.emitted('openNeedsYou')).toHaveLength(1);
  });

  it('a pick of the You node itself is not a team focus', async () => {
    const { w, scene } = await view();
    scene.onPick!('you');
    await settle(2);
    expect(scene.focus).toEqual([]);
    expect(w.find('[data-test=monitor-focus]').exists()).toBe(false);
  });
});

describe('density', () => {
  it('normal by default; a choice reaches the scene and is kept for the next Monitor', async () => {
    const first = await view();
    expect(first.w.get('[data-test=monitor-density-normal]').attributes('aria-pressed')).toBe('true');
    expect(first.scene.density).toEqual([]);
    await first.w.get('[data-test=monitor-density-calm]').trigger('click');
    expect(first.scene.density).toEqual(['calm']);
    expect(localStorage.getItem(lay.DensityStorageKey)).toBe('calm');
    first.w.unmount(); mounted.length = 0;
    const second = await view();
    expect(second.w.get('[data-test=monitor-density-calm]').attributes('aria-pressed')).toBe('true');
    expect(second.scene.density).toEqual(['calm']);
    await second.w.get('[data-test=monitor-density-normal]').trigger('click');
    expect(localStorage.getItem(lay.DensityStorageKey)).toBeNull();
  });

  it('reads only a known level; storage that refuses keeps the default', () => {
    expect(lay.readDensity({ getItem: () => 'wild' })).toBe('normal');
    expect(lay.readDensity({ getItem: () => 'lively' })).toBe('lively');
    expect(lay.readDensity({ getItem: () => { throw new Error('no'); } })).toBe('normal');
    expect(() => lay.writeDensity('calm', { setItem: () => { throw new Error('no'); }, removeItem: () => {} })).not.toThrow();
  });
});

describe('sound', () => {
  const burst = (color: any, big = false): Effect => ({ kind: 'burst', node: 'n', color, big, at: 0 });

  it('is off by default; the toggle turns it on and is kept', async () => {
    const { w } = await view();
    const b = w.get('[data-test=monitor-sound]');
    expect(b.attributes('aria-pressed')).toBe('false');
    expect(snd.readSound()).toBe(false);
    await b.trigger('click');
    expect(w.get('[data-test=monitor-sound]').attributes('aria-pressed')).toBe('true');
    expect(localStorage.getItem(snd.SoundStorageKey)).toBe('on');
    await w.get('[data-test=monitor-sound]').trigger('click');
    expect(localStorage.getItem(snd.SoundStorageKey)).toBeNull();
  });

  it('plays nothing while off, one tone per kind, with gaps', () => {
    const played: string[] = [];
    const s = new snd.MonitorSound((k) => played.push(k));
    const fx: Effect[] = [burst('decision'), burst('fail', true), { kind: 'step', node: 'n', step: 'read', caption: null, at: 0 }, burst('decision')];
    expect(s.hear(fx, 0)).toEqual([]);
    s.on = true;
    expect(s.hear(fx, 0)).toEqual(['needsYou', 'failed']);
    expect(s.hear([burst('decision')], 500)).toEqual([]); // inside the gap
    expect(s.hear([burst('decision')], 500 + snd.SoundGapMs)).toEqual(['needsYou']);
    expect(played).toEqual(['needsYou', 'failed', 'needsYou']);
  });

  it('knows which events sound', () => {
    expect(snd.soundOf({ kind: 'merge', team: 't', big: true, at: 0 })).toBe('landed');
    expect(snd.soundOf({ kind: 'merge', team: 't', big: false, at: 0 })).toBe('merge');
    expect(snd.soundOf({ kind: 'push', team: 't', at: 0 })).toBe('push');
    expect(snd.soundOf({ kind: 'comet', from: 'a', to: 'b', color: 'handback', label: null, at: 0 })).toBe('handback');
    expect(snd.soundOf({ kind: 'comet', from: 'a', to: 'b', color: 'tell', label: null, at: 0 })).toBeNull();
    expect(snd.soundOf(burst('done', false))).toBeNull();
    expect(snd.soundOf(burst('done', true))).toBe('done');
    expect(snd.soundOf({ kind: 'float', node: 'n', color: 'progress', text: 'x', at: 0 })).toBeNull();
  });

  it('without Web Audio the real player is silent, no error', () => {
    vi.stubGlobal('AudioContext', undefined);
    const s = new snd.MonitorSound();
    s.on = true;
    expect(() => { s.unlock(); s.hear([burst('decision')], 0); s.destroy(); }).not.toThrow();
  });
});

describe('the remote view at 375 px', () => {
  it('the camera row keeps to camera, cog and legend; density and sound sit in the cog panel', async () => {
    vi.stubGlobal('matchMedia', vi.fn((q: string) => ({ matches: /max-width/.test(q) && 375 <= 759.98, addEventListener() {}, removeEventListener() {} })));
    const { w } = await view();
    expect(w.get('[data-test=monitor]').classes()).toContain('phone');
    const row = w.get('.controls');
    expect(row.find('[data-test=monitor-density]').exists()).toBe(false);
    expect(row.find('[data-test=monitor-sound]').exists()).toBe(false);
    await w.get('[data-test=monitor-layout-toggle]').trigger('click');
    const panel = w.get('[data-test=monitor-layout]');
    expect(panel.find('[data-test=monitor-density]').exists()).toBe(true);
    await panel.get('[data-test=monitor-density-lively]').trigger('click');
    expect(scenes.at(-1)!.density).toEqual(['lively']);
    expect(panel.get('[data-test=monitor-sound]').text()).toBe('Sound off');
  });
});
