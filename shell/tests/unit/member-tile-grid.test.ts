// @vitest-environment happy-dom
// B076 (card 365): a team page's members are equal tiles in a wrapping grid, like the Teams list; the width and height
// handles in the gaps resize every tile at once and the size is kept (memberTileSize.ts, this viewer's storage). The
// last tile is New member: its + opens the add form (Job and first name, CLI, Add) in the tile. On a phone: one
// full-width tile per row, as tall as its content, no handles, New member last.
// A fake api and a fake matchMedia only: no core, program, network or port.
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils';
import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import TeamView from '../../src/renderer/src/components/TeamView.vue';
import {
  MEMBER_TILE_DEFAULT, MEMBER_TILE_MAX_W, MEMBER_TILE_MIN_H, MEMBER_TILE_MIN_W, memberTileSize, reloadMemberTileSize, resetMemberTileSize, setMemberTileSize,
} from '../../src/renderer/src/memberTileSize';
import { tileSize } from '../../src/renderer/src/teamTileSize';
import { applyRemoteAccess } from '../../src/renderer/src/remoteAccess';
import { PHONE_BREAKPOINT, PHONE_QUERY } from '../../src/renderer/src/phone';
import type { AgentCard, TeamSummary, YawbleApi } from '../../src/shared/api';

let width = 1280;
beforeEach(() => {
  width = 1280;
  Object.defineProperty(window, 'matchMedia', {
    configurable: true, writable: true,
    value: (query: string) => ({
      get matches() { return query === PHONE_QUERY ? width < PHONE_BREAKPOINT : false; },
      media: query, addEventListener: () => {}, removeEventListener: () => {},
    }),
  });
  try { localStorage.clear(); } catch { /* none */ }
  resetMemberTileSize();
  applyRemoteAccess('full');
});
let wrappers: VueWrapper[] = [];
afterEach(() => { for (const w of wrappers) w.unmount(); wrappers = []; document.body.innerHTML = ''; applyRemoteAccess('full'); });

const team = { id: 1, name: 'payments', repoPath: null, members: 2, running: 0, queued: 0 } as TeamSummary;
const mk = (id: number, name: string): AgentCard => ({
  member: { id, teamId: 1, team: 'payments', name, role: 'member', cli: 'claude', instructions: '', model: '', credential: 'default' },
  state: 'idle', queuePosition: null, queueLength: 0, queuedInstructions: 0, statusLine: 'hello', runId: null,
  runStartedAt: null, lastRunState: null, lastRunReason: null, net: 'model + bus', wallTimeout: '2 h', loosened: [], policyProblems: [],
}) as AgentCard;

function fakeApi(opts: { refuse?: string; hold?: Promise<void> } = {}) {
  const cards = [mk(7, 'DeveloperMarlo'), mk(8, 'TesterWren'), mk(9, 'Manager')];
  const adds: unknown[] = [];
  const api = {
    getInfo: async () => ({ hostChecks: [] }), getAvailableBackends: async () => [],
    call: async (method: string, params: any) => {
      if (method === 'teams.agents') return [...cards];
      if (method === 'members.add') {
        adds.push(params);
        if (opts.hold) await opts.hold;
        if (opts.refuse) throw new Error(opts.refuse);
        cards.push(mk(10 + adds.length, String(params.name).replace(/\s+/g, '')));
        return { ok: true };
      }
      return [];
    },
    onEvent: () => () => {}, openTerminal: async () => {}, pickFolder: async () => null,
  } as unknown as YawbleApi;
  return { api, adds };
}
async function page(api = fakeApi().api) {
  const w = mount(TeamView, { props: { team, api, pollMs: 60_000 }, attachTo: document.body });
  wrappers.push(w);
  await flushPromises();
  return w;
}
const cells = (w: VueWrapper) => w.findAll('[data-test=member-grid] > .member-cell');
const down = (h: any, x = 0, y = 0) => h.trigger('pointerdown', { clientX: x, clientY: y, pointerId: 1, button: 0 });
const move = (x: number, y: number) => window.dispatchEvent(new PointerEvent('pointermove', { clientX: x, clientY: y, pointerId: 1 }));
const up = () => window.dispatchEvent(new PointerEvent('pointerup', { pointerId: 1 }));
const src = readFileSync(resolve(__dirname, '../../src/renderer/src/components/TeamView.vue'), 'utf8');
const style = src.slice(src.indexOf('<style'));

describe('team page: the member tiles grid (desktop)', () => {
  it('puts every member and then the New member tile in one grid with the shared size', async () => {
    const w = await page();
    const grid = w.get('[data-test=member-grid]');
    expect(grid.attributes('style')).toContain(`--tile-w: ${MEMBER_TILE_DEFAULT.w}px`);
    expect(grid.attributes('style')).toContain(`--tile-h: ${MEMBER_TILE_DEFAULT.h}px`);
    const c = cells(w);
    expect(c).toHaveLength(4);
    expect(c.slice(0, 3).map((x) => x.get('section.pane').attributes('data-test'))).toEqual(['agent-DeveloperMarlo', 'agent-TesterWren', 'agent-Manager']);
    expect(c[3]!.find('[data-test=member-card-new-tile]').exists()).toBe(true);
    // Every tile (the New member one too) has its width and height handle; no old gap handle, no corner grip.
    for (const x of c) {
      expect(x.findAll('[data-test=member-tile-handle-w]')).toHaveLength(1);
      expect(x.findAll('[data-test=member-tile-handle-h]')).toHaveLength(1);
    }
    expect(w.find('[data-test=member-handle-h]').exists()).toBe(false);
    expect(w.find('[data-test=member-tile-grip]').exists()).toBe(false);
    expect(w.get('[data-test=member-tile-handle-w]').attributes('aria-label')).toBe('Resize all member tiles: width');
    // The old add row is gone.
    expect(w.find('.add.row').exists()).toBe(false);
    expect(w.find('[data-test=add-agent-name]').exists()).toBe(false);
  });

  it('the grid wraps equal tiles: auto-fill columns of the shared width (never wider than the row), rows of the shared height', () => {
    expect(style).toMatch(/\.member-grid \{[^}]*display: grid;[^}]*grid-template-columns: repeat\(auto-fill, min\(var\(--tile-w\), 100%\)\);[^}]*grid-auto-rows: var\(--tile-h\);/);
    expect(style).toMatch(/\.member-grid \{[^}]*gap: 14px;/);
    // The pane fills its tile on the desktop, and its view takes the rest (not the old shared output height).
    expect(style).toMatch(/@media \(min-width: 760px\) \{\s*\.member-cell > :deep\(\.pane\) \{ height: 100%;/);
  });

  it('defaults to two members per row at a 1280 px window', () => {
    const content = 1280 - 268 - 2 * 24 - 15; // sidebar, the page's padding, a scrollbar
    const perRow = (w: number) => Math.floor((content + 14) / (w + 14));
    expect(perRow(MEMBER_TILE_DEFAULT.w)).toBe(2);
    expect(perRow(MEMBER_TILE_DEFAULT.w)).toBe(Math.floor((1280 - 56 - 48 - 15 + 14) / (MEMBER_TILE_DEFAULT.w + 14))); // the sidebar folded too
  });

  it('the width handle resizes every tile live, keeps it on release, and clamps; the Teams list size is untouched', async () => {
    const teams = { ...tileSize.value };
    const w = await page();
    const grid = w.get('[data-test=member-grid]');
    await down(cells(w)[1]!.get('[data-test=member-tile-handle-w]'), 100, 0);
    move(160, 0);
    await w.vm.$nextTick();
    expect(grid.attributes('style')).toContain(`--tile-w: ${MEMBER_TILE_DEFAULT.w + 60}px`);
    expect(localStorage.getItem('yawble.memberTiles')).toBeNull();
    up();
    expect(JSON.parse(localStorage.getItem('yawble.memberTiles')!)).toEqual({ w: MEMBER_TILE_DEFAULT.w + 60, h: MEMBER_TILE_DEFAULT.h });
    await down(cells(w)[3]!.get('[data-test=member-tile-handle-w]'), 0, 0);
    move(-5000, 0);
    up();
    await w.vm.$nextTick();
    expect(memberTileSize.value.w).toBe(MEMBER_TILE_MIN_W);
    await down(cells(w)[0]!.get('[data-test=member-tile-handle-w]'), 0, 0);
    move(5000, 0);
    up();
    expect(memberTileSize.value.w).toBe(MEMBER_TILE_MAX_W);
    expect(tileSize.value).toEqual(teams);
    expect(localStorage.getItem('yawble.teamTiles')).toBeNull();
  });

  it('the height handle resizes every tile, keeps it, and the size survives a fresh start of the page', async () => {
    const w = await page();
    await down(cells(w)[0]!.get('[data-test=member-tile-handle-h]'), 0, 300);
    move(0, 380);
    up();
    await w.vm.$nextTick();
    expect(w.get('[data-test=member-grid]').attributes('style')).toContain(`--tile-h: ${MEMBER_TILE_DEFAULT.h + 80}px`);
    w.unmount();
    wrappers = [];
    memberTileSize.value = { ...MEMBER_TILE_DEFAULT };
    reloadMemberTileSize();
    const again = await page();
    expect(again.get('[data-test=member-grid]').attributes('style')).toContain(`--tile-h: ${MEMBER_TILE_DEFAULT.h + 80}px`);
    // Keyboard: arrows on the handles, 16 px a step; up/down only on the height handle.
    const h = again.get('[data-test=member-tile-handle-h]');
    await h.trigger('keydown', { key: 'ArrowDown' });
    expect(memberTileSize.value.h).toBe(MEMBER_TILE_DEFAULT.h + 96);
    await h.trigger('keydown', { key: 'ArrowRight' });
    expect(memberTileSize.value.w).toBe(MEMBER_TILE_DEFAULT.w);
    await again.get('[data-test=member-tile-handle-w]').trigger('keydown', { key: 'ArrowLeft' });
    expect(memberTileSize.value.w).toBe(MEMBER_TILE_DEFAULT.w - 16);
    // Double-click goes back to the default and forgets the saved size.
    await h.trigger('dblclick');
    expect(memberTileSize.value).toEqual(MEMBER_TILE_DEFAULT);
    expect(localStorage.getItem('yawble.memberTiles')).toBeNull();
  });

  it('a stored size is clamped when read, and junk falls back to the default', () => {
    localStorage.setItem('yawble.memberTiles', JSON.stringify({ w: 10, h: 10 }));
    reloadMemberTileSize();
    expect(memberTileSize.value).toEqual({ w: MEMBER_TILE_MIN_W, h: MEMBER_TILE_MIN_H });
    localStorage.setItem('yawble.memberTiles', '{nope');
    reloadMemberTileSize();
    expect(memberTileSize.value).toEqual(MEMBER_TILE_DEFAULT);
    setMemberTileSize({ w: 333.4, h: 444.6 });
    expect(memberTileSize.value).toEqual({ w: 333, h: 445 });
  });
});

describe('team page: the New member tile', () => {
  it('its + opens the form in the tile; Add adds the member with the CLI chosen and closes the form', async () => {
    const { api, adds } = fakeApi();
    const w = await page(api);
    const tile = () => w.get('[data-test=member-card-new-tile]');
    expect(tile().get('[data-test=member-card-new]').text()).toContain('New member');
    await tile().get('[data-test=member-card-new]').trigger('click');
    await flushPromises();
    expect(tile().find('[data-test=member-card-new]').exists()).toBe(false);
    const name = tile().get('[data-test=add-agent-name]');
    expect(document.activeElement).toBe(name.element);
    expect(tile().text()).toContain('Job and first name');
    expect(tile().text()).toContain('CLI');
    expect(tile().find('[data-test=add-agent-cli]').exists()).toBe(true);
    // Today's validation: no name, no Add.
    expect(tile().get('[data-test=add-agent]').attributes('disabled')).toBeDefined();
    await name.setValue('   ');
    expect(tile().get('[data-test=add-agent]').attributes('disabled')).toBeDefined();
    await name.setValue(' Developer Priya ');
    const cli = tile().get('[data-test=add-agent-cli]');
    const other = [...(cli.element as HTMLSelectElement).options].map((o) => o.value).find((v) => v !== 'claude');
    if (other) await cli.setValue(other);
    await tile().get('[data-test=add-agent]').trigger('click');
    await flushPromises();
    expect(adds).toEqual([{ teamId: 1, name: 'Developer Priya', cli: other ?? 'claude' }]);
    expect(w.find('[data-test=agent-DeveloperPriya]').exists()).toBe(true);
    // The form closed; New member is again the last tile, after the new member.
    expect(tile().find('[data-test=add-agent-form]').exists()).toBe(false);
    const c = cells(w);
    expect(c).toHaveLength(5);
    expect(c[3]!.find('[data-test=agent-DeveloperPriya]').exists()).toBe(true);
    expect(c[4]!.find('[data-test=member-card-new-tile]').exists()).toBe(true);
    // Reopened, the form is empty.
    await tile().get('[data-test=member-card-new]').trigger('click');
    expect((tile().get('[data-test=add-agent-name]').element as HTMLInputElement).value).toBe('');
  });

  it('Enter adds; while adding the button is busy; a refusal keeps the form and what was typed and shows the error', async () => {
    let release!: () => void;
    const hold = new Promise<void>((r) => { release = r; });
    const { api, adds } = fakeApi({ refuse: 'A member named DeveloperPriya already exists.', hold });
    const w = await page(api);
    await w.get('[data-test=member-card-new]').trigger('click');
    await w.get('[data-test=add-agent-name]').setValue('Developer Priya');
    await w.get('[data-test=add-agent-form]').trigger('submit');
    await w.vm.$nextTick();
    expect(adds).toHaveLength(1);
    const add = w.get('[data-test=add-agent]');
    expect(add.attributes('aria-busy')).toBe('true');
    expect(add.attributes('disabled')).toBeDefined();
    expect(add.text()).toContain('Adding…');
    release();
    await flushPromises();
    expect(w.get('[data-test=team-error]').text()).toContain('already exists');
    expect((w.get('[data-test=add-agent-name]').element as HTMLInputElement).value).toBe('Developer Priya');
    expect(w.get('[data-test=add-agent]').attributes('disabled')).toBeUndefined();
  });

  it('Cancel and Escape close the form and drop what was typed', async () => {
    const w = await page();
    await w.get('[data-test=member-card-new]').trigger('click');
    await w.get('[data-test=add-agent-name]').setValue('Tester Kofi');
    await w.get('[data-test=add-agent-cancel]').trigger('click');
    expect(w.find('[data-test=add-agent-form]').exists()).toBe(false);
    await w.get('[data-test=member-card-new]').trigger('click');
    expect((w.get('[data-test=add-agent-name]').element as HTMLInputElement).value).toBe('');
    await w.get('[data-test=add-agent-form]').trigger('keydown', { key: 'Escape' });
    expect(w.find('[data-test=add-agent-form]').exists()).toBe(false);
  });

  it('a read-only (Visitor) remote viewer cannot open it: the existing rule for changes', async () => {
    applyRemoteAccess('read');
    const w = await page();
    expect(w.get('[data-test=member-card-new]').attributes('disabled')).toBeDefined();
  });
});

describe('team page: the member tiles on a phone', () => {
  it('one full-width tile per row, as tall as its content, no handles, New member last', async () => {
    width = 375;
    const w = await page();
    const grid = w.get('[data-test=member-grid]');
    expect(grid.attributes('style') ?? '').not.toContain('--tile-w');
    expect(w.find('.tile-grip').exists()).toBe(false);
    const c = cells(w);
    expect(c).toHaveLength(4);
    expect(c[3]!.find('[data-test=member-card-new-tile]').exists()).toBe(true);
    for (const x of c.slice(0, 3)) expect((x.get('[data-test=member-pane-body]').element as HTMLElement).style.height).toBe('');
    const phone = style.slice(style.indexOf('@media (max-width: 759.98px)'));
    expect(phone).toMatch(/\.member-grid \{ grid-template-columns: minmax\(0, 1fr\); grid-auto-rows: auto;/);
    // The form opens in the tile there too.
    await w.get('[data-test=member-card-new]').trigger('click');
    expect(w.find('[data-test=add-agent-name]').exists()).toBe(true);
  });
});
