// @vitest-environment happy-dom
import { flushPromises, mount } from '@vue/test-utils';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import TeamsList from '../../src/renderer/src/components/TeamsList.vue';
import { reloadTileSize, resetTileSize, setTileSize, tileSize } from '../../src/renderer/src/teamTileSize';
import type { YawbleApi, TeamSummary } from '../../src/shared/api';

const mk = (id: number, name: string, over: Partial<TeamSummary> = {}) =>
  ({ id, name, repoPath: null, members: 2, running: 0, queued: 0, paused: false, archived: false, ...over }) as TeamSummary;

function fakeApi(calls: string[] = []): YawbleApi {
  return {
    call: async (method: string) => { calls.push(method); return (method === 'bus.workflows' ? [] : { ok: true }) as any; },
    onEvent: () => () => {},
  } as unknown as YawbleApi;
}
function mountList(teams: TeamSummary[], archived: TeamSummary[] = [], api = fakeApi()) {
  return mount(TeamsList, { props: { teams, archived, api, pollMs: 60_000, refresh: async () => {} }, attachTo: document.body });
}
const tiles = (w: ReturnType<typeof mountList>) => [...w.findAll('[data-test=team-card]'), w.get('[data-test=team-card-new-tile]')];
const move = (x: number, y: number) => window.dispatchEvent(new PointerEvent('pointermove', { clientX: x, clientY: y, pointerId: 1 }));
const up = () => window.dispatchEvent(new PointerEvent('pointerup', { pointerId: 1 }));

describe('Teams list tiles: one shared size', () => {
  beforeEach(() => { localStorage.clear(); resetTileSize(); document.body.innerHTML = ''; });

  it('gives every team tile and the New team tile the shared size', async () => {
    const w = mountList([mk(1, 'a'), mk(2, 'b')]);
    await flushPromises();
    const grid = w.get('[data-test=team-grid]');
    expect(grid.attributes('style')).toContain('--tile-w: 320px');
    expect(grid.attributes('style')).toContain('--tile-h: 260px');
    expect(tiles(w)).toHaveLength(3); // two teams and the New team tile, all in the one grid
    for (const t of tiles(w)) expect(grid.element.contains(t.element)).toBe(true);
    expect(w.get('[data-test=team-card-new-tile]').find('[data-test=tile-grip]').exists()).toBe(true);
    w.unmount();
  });

  it('dragging one grip resizes all tiles live and keeps the size on release', async () => {
    const w = mountList([mk(1, 'a'), mk(2, 'b')], [mk(3, 'old', { archived: true })]);
    await w.get('[data-test=show-archived]').trigger('click');
    const grip = w.findAll('[data-test=team-card]')[1].get('[data-test=tile-grip]');
    expect(grip.attributes('aria-label')).toBe('Resize all team tiles');
    await grip.trigger('pointerdown', { clientX: 100, clientY: 100, pointerId: 1, button: 0 });
    move(160, 140);
    await flushPromises();
    expect(tileSize.value).toEqual({ w: 380, h: 300 });
    expect(localStorage.getItem('yawble.teamTiles')).toBeNull(); // only previewed
    expect(w.get('[data-test=team-grid]').attributes('style')).toContain('--tile-w: 380px');
    expect(w.get('[data-test=archived-grid]').attributes('style')).toContain('--tile-h: 300px'); // archived tiles follow too
    up();
    expect(JSON.parse(localStorage.getItem('yawble.teamTiles')!)).toEqual({ w: 380, h: 300 });
    w.unmount();
  });

  it('clamps to the limits, resets on double-click and survives a remount', async () => {
    const w = mountList([mk(1, 'a')]);
    const grip = w.get('[data-test=tile-grip]');
    await grip.trigger('pointerdown', { clientX: 0, clientY: 0, pointerId: 1, button: 0 });
    move(5000, 5000); up();
    expect(tileSize.value).toEqual({ w: 640, h: 640 });
    await grip.trigger('pointerdown', { clientX: 0, clientY: 0, pointerId: 1, button: 0 });
    move(-5000, -5000); up();
    expect(tileSize.value).toEqual({ w: 280, h: 200 });
    w.unmount();

    setTileSize({ w: 400, h: 350 });
    tileSize.value = { w: 1, h: 1 }; // forget the in-memory value: a fresh start reads the saved one
    reloadTileSize();
    const w2 = mountList([mk(1, 'a')]);
    expect(w2.get('[data-test=team-grid]').attributes('style')).toContain('--tile-w: 400px');
    await w2.get('[data-test=tile-grip]').trigger('dblclick');
    expect(tileSize.value).toEqual({ w: 320, h: 260 });
    expect(localStorage.getItem('yawble.teamTiles')).toBeNull();
    w2.unmount();
  });

  it('copes with a storage that throws', () => {
    const get = vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => { throw new Error('blocked'); });
    const set = vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => { throw new Error('blocked'); });
    reloadTileSize();
    expect(tileSize.value).toEqual({ w: 320, h: 260 });
    expect(() => setTileSize({ w: 300, h: 300 })).not.toThrow();
    expect(tileSize.value).toEqual({ w: 300, h: 300 });
    get.mockRestore(); set.mockRestore();
  });

  it('a cancelled drag goes back to the size it had and never sticks', async () => {
    const w = mountList([mk(1, 'a')]);
    const grip = w.get('[data-test=tile-grip]');
    await grip.trigger('pointerdown', { clientX: 0, clientY: 0, pointerId: 1, button: 0 });
    move(50, 50);
    expect(tileSize.value).toEqual({ w: 370, h: 310 });
    window.dispatchEvent(new PointerEvent('pointercancel', { pointerId: 1 }));
    expect(tileSize.value).toEqual({ w: 320, h: 260 });
    expect(localStorage.getItem('yawble.teamTiles')).toBeNull();
    move(90, 90); // the listeners are gone
    expect(tileSize.value).toEqual({ w: 320, h: 260 });
    expect(document.body.classList.contains('resizing-tiles')).toBe(false);
    w.unmount();
  });

  it('resizes from the keyboard: 16 px steps, 64 px with Shift', async () => {
    const w = mountList([mk(1, 'a')]);
    const grip = w.get('[data-test=tile-grip]');
    expect(grip.attributes('tabindex')).toBe('0');
    await grip.trigger('keydown', { key: 'ArrowRight' });
    expect(tileSize.value).toEqual({ w: 336, h: 260 });
    await grip.trigger('keydown', { key: 'ArrowDown', shiftKey: true });
    expect(tileSize.value).toEqual({ w: 336, h: 324 });
    await grip.trigger('keydown', { key: 'ArrowLeft' });
    await grip.trigger('keydown', { key: 'ArrowUp', shiftKey: true });
    expect(tileSize.value).toEqual({ w: 320, h: 260 });
    w.unmount();
  });
});

describe('Teams list tiles: the gaps are resize handles', () => {
  beforeEach(() => { localStorage.clear(); resetTileSize(); document.body.innerHTML = ''; });
  const down = (h: any, x = 0, y = 0) => h.trigger('pointerdown', { clientX: x, clientY: y, pointerId: 1, button: 0 });

  it('every tile, the New team tile and archived tiles have a width and a height handle with labels', async () => {
    const w = mountList([mk(1, 'a'), mk(2, 'b')], [mk(3, 'old', { archived: true })]);
    await w.get('[data-test=show-archived]').trigger('click');
    expect(w.findAll('[data-test=tile-handle-w]')).toHaveLength(4);
    expect(w.findAll('[data-test=tile-handle-h]')).toHaveLength(4);
    expect(w.get('[data-test=tile-handle-w]').attributes('aria-label')).toBe('Resize all team tiles: width');
    expect(w.get('[data-test=tile-handle-h]').attributes('aria-label')).toBe('Resize all team tiles: height');
    // outside the tile, so they never cover its content or clicks
    for (const t of tiles(w)) expect(t.find('[data-test=tile-handle-w]').exists()).toBe(false);
    w.unmount();
  });

  it('a width handle drag changes only the width, for all tiles, and keeps it on release', async () => {
    const w = mountList([mk(1, 'a'), mk(2, 'b')]);
    await down(w.findAll('[data-test=tile-handle-w]')[1], 100, 100);
    move(150, 400);
    await flushPromises();
    expect(tileSize.value).toEqual({ w: 370, h: 260 });
    expect(w.get('[data-test=team-grid]').attributes('style')).toContain('--tile-w: 370px');
    up();
    expect(JSON.parse(localStorage.getItem('yawble.teamTiles')!)).toEqual({ w: 370, h: 260 });
    w.unmount();
  });

  it('a height handle drag changes only the height', async () => {
    const w = mountList([mk(1, 'a')]);
    await down(w.get('[data-test=tile-handle-h]'), 100, 100);
    move(400, 150);
    expect(tileSize.value).toEqual({ w: 320, h: 310 });
    up();
    expect(JSON.parse(localStorage.getItem('yawble.teamTiles')!)).toEqual({ w: 320, h: 310 });
    w.unmount();
  });

  it('the handles clamp', async () => {
    const w = mountList([mk(1, 'a')]);
    await down(w.get('[data-test=tile-handle-w]'));
    move(9999, 0); up();
    expect(tileSize.value.w).toBe(640);
    await down(w.get('[data-test=tile-handle-h]'));
    move(0, -9999); up();
    expect(tileSize.value).toEqual({ w: 640, h: 200 });
    w.unmount();
  });

  it('keyboard: the width handle takes left/right only, the height handle up/down only; Shift is bigger', async () => {
    const w = mountList([mk(1, 'a')]);
    const wh = w.get('[data-test=tile-handle-w]');
    const hh = w.get('[data-test=tile-handle-h]');
    await wh.trigger('keydown', { key: 'ArrowRight' });
    expect(tileSize.value).toEqual({ w: 336, h: 260 });
    await wh.trigger('keydown', { key: 'ArrowDown' });
    expect(tileSize.value).toEqual({ w: 336, h: 260 });
    await wh.trigger('keydown', { key: 'ArrowLeft', shiftKey: true });
    expect(tileSize.value).toEqual({ w: 280, h: 260 });
    await hh.trigger('keydown', { key: 'ArrowDown', shiftKey: true });
    expect(tileSize.value).toEqual({ w: 280, h: 324 });
    await hh.trigger('keydown', { key: 'ArrowRight' });
    await hh.trigger('keydown', { key: 'ArrowUp' });
    expect(tileSize.value).toEqual({ w: 280, h: 308 });
    expect(hh.attributes('aria-orientation')).toBe('vertical');
    w.unmount();
  });

  it('double-click on a handle resets both axes', async () => {
    setTileSize({ w: 500, h: 400 });
    const w = mountList([mk(1, 'a')]);
    await w.get('[data-test=tile-handle-h]').trigger('dblclick');
    expect(tileSize.value).toEqual({ w: 320, h: 260 });
    expect(localStorage.getItem('yawble.teamTiles')).toBeNull();
    w.unmount();
  });

  it('pointercancel ends a handle drag and restores the size', async () => {
    const w = mountList([mk(1, 'a')]);
    await down(w.get('[data-test=tile-handle-w]'));
    move(60, 0);
    expect(tileSize.value.w).toBe(380);
    expect(document.body.classList.contains('resizing-tiles-w')).toBe(true);
    window.dispatchEvent(new PointerEvent('pointercancel', { pointerId: 1 }));
    expect(tileSize.value).toEqual({ w: 320, h: 260 });
    expect(localStorage.getItem('yawble.teamTiles')).toBeNull();
    move(90, 0);
    expect(tileSize.value.w).toBe(320);
    expect(document.body.className).not.toContain('resizing-tiles');
    w.unmount();
  });

  it('clicking a tile still opens it, and clicking a handle does not', async () => {
    const w = mountList([mk(1, 'a')]);
    await w.get('[data-test=tile-handle-w]').trigger('click');
    expect(w.emitted('open')).toBeUndefined();
    await w.get('[data-test=team-open-a]').trigger('click');
    expect(w.emitted('open')![0]).toEqual([1]);
    w.unmount();
  });
});

describe('Teams list tiles: the button row', () => {
  beforeEach(() => { localStorage.clear(); resetTileSize(); document.body.innerHTML = ''; });

  it('sits between the header and Recent workflows, one icon and one label per button', async () => {
    const w = mountList([mk(1, 'a')]);
    await flushPromises();
    const card = w.get('[data-test=team-card]');
    const kids = [...card.element.children].map((c) => c.getAttribute('data-test') ?? c.className);
    expect(kids.indexOf('team-open-a')).toBeLessThan(kids.indexOf('card-actions'));
    expect(kids.indexOf('card-actions')).toBeLessThan(kids.indexOf('team-workflows'));
    for (const id of ['card-settings', 'card-pause', 'card-archive', 'card-delete']) {
      const b = card.get(`[data-test=${id}]`);
      expect(b.find('svg').exists()).toBe(true);
      expect(b.find('.lbl').text()).not.toBe('');
      expect(b.attributes('aria-label')).toBe(b.find('.lbl').text());
    }
    expect(card.get('[data-test=card-delete]').classes()).toContain('danger-text');
    w.unmount();
  });

  it('shows Resume instead of Pause for a paused team', async () => {
    const w = mountList([mk(1, 'a'), mk(2, 'b', { paused: true })]);
    const [a, b] = w.findAll('[data-test=team-card]');
    expect(a.find('[data-test=card-pause]').exists()).toBe(true);
    expect(a.find('[data-test=card-resume]').exists()).toBe(false);
    expect(b.find('[data-test=card-resume]').text()).toBe('Resume');
    expect(b.find('[data-test=card-pause]').exists()).toBe(false);
    w.unmount();
  });

  it('keeps only the icons when a button row would overflow (measured, not guessed), with the label as tooltip', async () => {
    // happy-dom has no layout: pretend the labelled row needs 300 px and the row's width is the tile's width - 24.
    const sw = vi.spyOn(HTMLElement.prototype, 'scrollWidth', 'get').mockImplementation(function (this: HTMLElement) { return this.classList.contains('card-actions') ? 300 : 0; });
    const cw = vi.spyOn(HTMLElement.prototype, 'clientWidth', 'get').mockImplementation(function (this: HTMLElement) {
      return this.classList.contains('card-actions') ? tileSize.value.w - 24 : 0;
    });
    setTileSize({ w: 340, h: 260 }); // row 316: the labels fit
    const w = mountList([mk(1, 'a')]);
    await flushPromises();
    expect(w.get('[data-test=team-grid]').classes()).not.toContain('icons-only');
    setTileSize({ w: 300, h: 260 }); // row 276: they would overflow
    await flushPromises();
    expect(w.get('[data-test=team-grid]').classes()).toContain('icons-only');
    const b = w.get('[data-test=card-archive]');
    expect(b.attributes('title')).toBe('Archive');
    expect(b.attributes('aria-label')).toBe('Archive');
    setTileSize({ w: 340, h: 260 });
    await flushPromises();
    expect(w.get('[data-test=team-grid]').classes()).not.toContain('icons-only'); // and back
    w.unmount();
    sw.mockRestore(); cw.mockRestore();
  });

  it('follows the container width (ResizeObserver): tiles never exceed it and a drag starts from the rendered size', async () => {
    let notify: () => void = () => {};
    vi.stubGlobal('ResizeObserver', class { constructor(cb: () => void) { notify = cb; } observe() {} disconnect() {} unobserve() {} });
    let width = 900;
    const cw = vi.spyOn(HTMLElement.prototype, 'clientWidth', 'get').mockImplementation(function (this: HTMLElement) { return this.classList.contains('teams-list') ? width : 0; });
    setTileSize({ w: 500, h: 260 });
    const w = mountList([mk(1, 'a')]);
    await flushPromises();
    expect(w.get('[data-test=team-grid]').attributes('style')).toContain('--tile-w: 500px');
    width = 300; notify(); await flushPromises();
    expect(w.get('[data-test=team-grid]').attributes('style')).toContain('--tile-w: 300px');
    expect(w.get('[data-test=tile-grip]').attributes('aria-valuenow')).toBe('300');
    await w.get('[data-test=tile-grip]').trigger('pointerdown', { clientX: 0, clientY: 0, pointerId: 1, button: 0 });
    move(-20, 0);
    expect(tileSize.value.w).toBe(280); // from 300, not from 500: no dead zone
    up();
    await w.get('[data-test=tile-grip]').trigger('keydown', { key: 'ArrowRight' });
    expect(tileSize.value.w).toBe(296);
    w.unmount();
    cw.mockRestore();
    vi.unstubAllGlobals();
  });

  it('keeps the state chip visible and ellipsizes a very long team name', async () => {
    const name = 'a-very-long-team-name-'.repeat(8);
    const w = mountList([mk(1, name)]);
    const nameEl = w.get('.card-name');
    expect(nameEl.text()).toBe(name);
    expect(nameEl.attributes('title')).toBe(name);
    expect(w.find('[data-test=team-state]').exists()).toBe(true);
    expect(w.find('[data-test=card-actions]').exists()).toBe(true);
    w.unmount();
  });

  it('a busy button says what it is doing in its aria-label', async () => {
    let release: () => void = () => {};
    const api = { call: (m: string) => (m === 'teams.pause' ? new Promise((r) => { release = () => r({ ok: true }); }) : Promise.resolve([])), onEvent: () => () => {} } as unknown as YawbleApi;
    const w = mountList([mk(1, 'a')], [], api);
    await w.get('[data-test=card-pause]').trigger('click');
    expect(w.get('[data-test=card-pause]').attributes('aria-label')).toBe('Pausing…');
    release(); await flushPromises();
    w.unmount();
  });

  it('a button click acts without opening the team', async () => {
    const calls: string[] = [];
    const w = mountList([mk(1, 'a')], [], fakeApi(calls));
    await w.get('[data-test=card-pause]').trigger('click');
    await flushPromises();
    expect(calls).toContain('teams.pause');
    await w.get('[data-test=card-settings]').trigger('click');
    await w.get('[data-test=card-delete]').trigger('click');
    expect(w.emitted('open')).toBeUndefined();
    expect(w.emitted('settings')).toHaveLength(1);
    expect(w.get('[data-test=card-delete-yes]').exists()).toBe(true);
    await w.get('[data-test=team-open-a]').trigger('click');
    expect(w.emitted('open')![0]).toEqual([1]);
    w.unmount();
  });

  it('archived tiles keep Unarchive and Delete, styled the same way', async () => {
    const w = mountList([], [mk(3, 'old', { archived: true })]);
    await w.get('[data-test=show-archived]').trigger('click');
    const t = w.get('[data-test=archived-team-card]');
    expect(t.get('[data-test=card-unarchive]').classes()).toContain('tile-btn');
    expect(t.get('[data-test=card-unarchive]').find('svg').exists()).toBe(true);
    expect(t.get('[data-test=card-delete]').classes()).toContain('danger-text');
    w.unmount();
  });
});
