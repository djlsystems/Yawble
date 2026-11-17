// @vitest-environment happy-dom
// B037: the phone layout (a window below 760 px wide) of Teams, the Kanban board, Backlog, Monitor, Logs, Documents
// and the Code browser. matchMedia is a fake whose width the tests set: below 760 px the screens change what CSS
// cannot (one board lane at a time with tabs and swipe, the list or the file, entries as cards, menus); at 760 px
// and wider each screen is as it was. The scoped styles carry the same width as @media (max-width: 759.98px).
import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils';
import { afterEach, describe, expect, it, vi } from 'vitest';
import MarkdownIt from 'markdown-it';
import BoardView from '../../src/renderer/src/board/BoardView.vue';
import BacklogView from '../../src/renderer/src/board/BacklogView.vue';
import TeamView from '../../src/renderer/src/components/TeamView.vue';
import TeamsList from '../../src/renderer/src/components/TeamsList.vue';
import LogsDialog from '../../src/renderer/src/components/LogsDialog.vue';
import DocumentsBrowser from '../../src/renderer/src/components/DocumentsBrowser.vue';
import MonitorView from '../../src/renderer/src/monitor/MonitorView.vue';
import CodeBrowser from '../../../code-browser/src/CodeBrowser.vue';
import { NarrowQuery, SwipeMin, useNarrow, useSwipe } from '../../src/renderer/src/narrowScreen';
import type { AgentCard, BacklogItem, BoardCard, LogSourceInfo, YawbleApi } from '../../src/shared/api';

// ---------------------------------------------------------------- the matchMedia fake

type Listener = () => void;
/** A window `width` px wide: matchMedia answers max-width queries from it, and setWidth fires their change listeners. */
function screen(width: number) {
  const lists: { query: string; listeners: Set<Listener>; mql: { matches: boolean } }[] = [];
  const answers = (query: string) => {
    const max = /\(max-width:\s*([\d.]+)px\)/.exec(query);
    return max ? width <= Number(max[1]) : false;
  };
  const matchMedia = vi.fn((query: string) => {
    const listeners = new Set<Listener>();
    const mql = {
      get matches() { return answers(query); }, media: query,
      addEventListener: (_: string, l: Listener) => listeners.add(l),
      removeEventListener: (_: string, l: Listener) => listeners.delete(l),
    };
    lists.push({ query, listeners, mql });
    return mql;
  });
  vi.stubGlobal('matchMedia', matchMedia);
  return {
    matchMedia,
    setWidth(w: number) { width = w; for (const l of lists) for (const f of [...l.listeners]) f(); },
    listening: () => lists.filter((l) => l.query === NarrowQuery).reduce((n, l) => n + l.listeners.size, 0),
  };
}
const Phone = 390;
const Desktop = 1280;

const wrappers: VueWrapper[] = [];
const keep = <T extends VueWrapper>(w: T) => { wrappers.push(w); return w; };
afterEach(() => { while (wrappers.length) wrappers.pop()!.unmount(); vi.unstubAllGlobals(); document.body.innerHTML = ''; });
const settle = async (n = 5) => { for (let i = 0; i < n; i++) await flushPromises(); };

/** A fake core: each method's answer from `answers`; anything else fails (the views keep going without it). */
function fakeApi(answers: Record<string, (params: any) => unknown>) {
  const calls: { method: string; params: any }[] = [];
  const api = {
    getInfo: async () => { throw new Error('no'); },
    getAvailableBackends: async () => [],
    call: async (method: string, params?: any) => {
      calls.push({ method, params });
      const a = answers[method];
      if (!a) throw new Error(`unexpected ${method}`);
      return a(params);
    },
    onEvent: () => () => {},
    openTerminal: async () => {},
    pickFolder: async () => null,
  } as unknown as YawbleApi;
  return { api, calls, called: (m: string) => calls.filter((c) => c.method === m) };
}

/** A component's scoped style rules inside its phone @media block, and the rules outside it (the desktop ones). */
const PhoneBlock = /@media \(max-width: 759\.98px\) \{([\s\S]*?)\n\}/;
const phoneRules = (src: string) => PhoneBlock.exec(src)?.[1] ?? '';
const desktopRules = (src: string) => (/<style scoped>([\s\S]*?)<\/style>/.exec(src)?.[1] ?? '').replace(PhoneBlock, '');

const pointer = (type: string, x: number, y: number, id = 1) => new PointerEvent(type, { clientX: x, clientY: y, pointerId: id, isPrimary: true, bubbles: true });

// ---------------------------------------------------------------- the helper itself

describe('useNarrow and useSwipe', () => {
  it('follows the window across 760 px, and is the desktop layout without matchMedia', async () => {
    const s = screen(Phone);
    const w = keep(mount({ setup: () => ({ narrow: useNarrow() }), template: '<i>{{ narrow }}</i>' }));
    expect(s.matchMedia).toHaveBeenCalledWith('(max-width: 759.98px)');
    expect(w.text()).toBe('true');
    s.setWidth(760);
    await flushPromises();
    expect(w.text()).toBe('false');
    s.setWidth(759);
    await flushPromises();
    expect(w.text()).toBe('true');
    expect(s.listening()).toBe(1);
    w.unmount(); wrappers.pop();
    expect(s.listening()).toBe(0); // the listener goes with the component
    vi.stubGlobal('matchMedia', undefined);
    const none = keep(mount({ setup: () => ({ narrow: useNarrow() }), template: '<i>{{ narrow }}</i>' }));
    expect(none.text()).toBe('false');
  });

  it('a swipe is a mostly horizontal move of at least SwipeMin px; a vertical one, a short one or a cancelled one is not', () => {
    const seen: number[] = [];
    const s = useSwipe((d) => seen.push(d));
    const swipe = (dx: number, dy: number) => { s.down(pointer('pointerdown', 200, 300)); s.up(pointer('pointerup', 200 + dx, 300 + dy)); };
    swipe(-SwipeMin, 0);
    swipe(80, 10);
    swipe(-30, 0); // too short
    swipe(-80, 70); // more down than across: a scroll
    s.down(pointer('pointerdown', 200, 300)); s.cancel(); s.up(pointer('pointerup', 50, 300));
    s.down(pointer('pointerdown', 200, 300, 1)); s.up(pointer('pointerup', 50, 300, 2)); // another finger
    expect(seen).toEqual([-1, 1]);
  });
});

// ---------------------------------------------------------------- the Kanban board

const lanes = [{ id: 'todo', label: 'To Do' }, { id: 'in-progress', label: 'In Progress' }, { id: 'blocked', label: 'Needs You' }, { id: 'done', label: 'Done' }];
const bcard = (id: number, lane: BoardCard['lane'], title: string): BoardCard => ({
  id, team: 'payments', teamId: 1, member: 'DeveloperPriya', item: null, title, body: title, status: 'running', lane, placed: false,
  awaitingManager: false, workflow: 10, createdAt: '2026-11-08T10:00:00Z', updatedAt: `2026-11-08T10:0${id}:00Z`, trailCount: 0,
});
function boardApi() {
  const cards = [bcard(1, 'todo', 'One'), bcard(2, 'todo', 'Two'), bcard(3, 'in-progress', 'Three'), bcard(4, 'done', 'Four')];
  return fakeApi({
    'board.list': () => ({ lanes, cards, teams: [{ id: 1, name: 'payments' }] }),
    'board.card': (p) => ({ ...cards.find((c) => c.id === p.id)!, trail: [] }),
    'integrations.connectionAttention': () => [{ id: 7, name: 'Pets key', reason: '401', where: 'Settings' }],
    'network.asks': () => [], 'tools.asks': () => [],
  });
}
const visibleLanes = (w: VueWrapper) => lanes.map((l) => l.id).filter((id) => (w.get(`[data-test=lane-${id}]`).element as HTMLElement).style.display !== 'none');

describe('the Kanban board on a phone', () => {
  it('shows one lane at a time under lane tabs with their counts; a tab picks the lane', async () => {
    screen(Phone);
    const { api } = boardApi();
    const w = keep(mount(BoardView, { props: { api, pollMs: 60_000 } }));
    await settle();
    const tabs = w.findAll('[data-test=lane-tabs] [role=tab]');
    expect(tabs.map((t) => t.get('.lname').text())).toEqual(['To Do', 'In Progress', 'Needs You', 'Done']);
    // Needs You counts the connection that needs reconnecting too.
    expect(tabs.map((t) => t.get('[data-test=lane-tab-count]').text())).toEqual(['2', '1', '1', '1']);
    expect(visibleLanes(w)).toEqual(['todo']);
    expect(w.get('[data-test=lane-tab-todo]').attributes('aria-selected')).toBe('true');
    await w.get('[data-test=lane-tab-done]').trigger('click');
    expect(visibleLanes(w)).toEqual(['done']);
    expect(w.get('[data-test=lane-tab-done]').attributes('aria-selected')).toBe('true');
    expect(w.get('[data-test=lane-tab-todo]').attributes('aria-selected')).toBe('false');
    // The hidden lanes stay mounted: Needs You still lists its asks while another lane is shown.
    expect(w.find('[data-test=needs-reconnect-7]').exists()).toBe(true);
  });

  it('swipes left to the next lane and right to the previous one, and stops at the ends', async () => {
    screen(Phone);
    const { api } = boardApi();
    const w = keep(mount(BoardView, { props: { api, pollMs: 60_000 } }));
    await settle();
    const el = w.get('[data-test=board-lanes]').element;
    const swipe = async (dx: number, dy = 0) => { el.dispatchEvent(pointer('pointerdown', 200, 300)); el.dispatchEvent(pointer('pointerup', 200 + dx, 300 + dy)); await flushPromises(); };
    await swipe(80); // right on the first lane: stays
    expect(visibleLanes(w)).toEqual(['todo']);
    await swipe(-80);
    expect(visibleLanes(w)).toEqual(['in-progress']);
    await swipe(-80);
    await swipe(-80);
    expect(visibleLanes(w)).toEqual(['done']);
    await swipe(-80); // left on the last lane: stays
    expect(visibleLanes(w)).toEqual(['done']);
    await swipe(80);
    expect(visibleLanes(w)).toEqual(['blocked']);
    await swipe(-20, 200); // a scroll down the lane, not a swipe
    expect(visibleLanes(w)).toEqual(['blocked']);
  });

  it('an opened card covers the board (the board-main is hidden behind it) and closing it comes back', async () => {
    screen(Phone);
    const { api } = boardApi();
    const w = keep(mount(BoardView, { props: { api, pollMs: 60_000 } }));
    await settle();
    await w.get('[data-test=board-card-1]').trigger('click');
    await settle();
    expect(w.get('[data-test=board]').classes()).toEqual(expect.arrayContaining(['phone', 'card-open']));
    expect(w.find('[data-test=card-panel]').exists()).toBe(true);
    await w.get('[data-test=card-close]').trigger('click');
    expect(w.get('[data-test=board]').classes()).not.toContain('card-open');
  });

  it('follows the window: grown past 760 px every lane shows and the tabs go; narrowed again, one lane', async () => {
    const s = screen(Phone);
    const { api } = boardApi();
    const w = keep(mount(BoardView, { props: { api, pollMs: 60_000 } }));
    await settle();
    s.setWidth(800);
    await flushPromises();
    expect(w.find('[data-test=lane-tabs]').exists()).toBe(false);
    expect(visibleLanes(w)).toEqual(['todo', 'in-progress', 'blocked', 'done']);
    s.setWidth(Phone);
    await flushPromises();
    expect(visibleLanes(w)).toEqual(['todo']);
  });

  it('at desktop width: four lanes side by side, no tabs, and a swipe changes nothing', async () => {
    screen(Desktop);
    const { api } = boardApi();
    const w = keep(mount(BoardView, { props: { api, pollMs: 60_000 } }));
    await settle();
    expect(w.find('[data-test=lane-tabs]').exists()).toBe(false);
    expect(w.get('[data-test=board]').classes()).not.toContain('phone');
    const el = w.get('[data-test=board-lanes]').element;
    el.dispatchEvent(pointer('pointerdown', 400, 300)); el.dispatchEvent(pointer('pointerup', 100, 300));
    await flushPromises();
    expect(visibleLanes(w)).toEqual(['todo', 'in-progress', 'blocked', 'done']);
    expect(w.get('[data-test=lane-todo] .count').text()).toBe('2');
  });

  it("makes the team link in an opened card's subtitle a 44 px tap target at phone width only (F5)", () => {
    const src = readFileSync(resolve(__dirname, '../../src/renderer/src/board/CardPanel.vue'), 'utf8');
    const rules = phoneRules(src);
    expect(rules).toMatch(/\.sub a \{[^}]*min-height: 44px; min-width: 44px; box-sizing: border-box/);
    expect(desktopRules(src)).not.toMatch(/\.sub a \{/);
  });
});

// ---------------------------------------------------------------- the Backlog

const item = (id: number, title: string, over: Partial<BacklogItem> = {}): BacklogItem => ({
  id, label: `B00${id}`, title, body: `# ${title}`, state: 'pending', createdAt: '2026-11-08T10:00:00Z', updatedAt: '2026-11-08T10:00:00Z',
  archivedAt: null, dispatchedAt: null, dispatchedTeam: null, dispatchCount: 0, inFlight: false, ...over,
} as BacklogItem);
function backlogApi() {
  let items = [item(1, 'First'), item(2, 'Second'), item(3, 'Third')];
  return fakeApi({
    'backlog.list': () => items,
    'backlog.item': (p) => ({ ...items.find((i) => i.id === p.id)!, dispatches: [] }),
    'backlog.move': (p) => {
      const at = items.findIndex((i) => i.id === p.id);
      const to = p.direction === 'up' ? at - 1 : at + 1;
      const copy = [...items];
      [copy[at], copy[to]] = [copy[to]!, copy[at]!];
      items = copy;
      return items;
    },
  });
}

describe('the Backlog on a phone', () => {
  it('rows carry a ⋯ menu with the priority moves instead of the ↑ ↓ buttons, and sorting is a select', async () => {
    screen(Phone);
    const { api, called } = backlogApi();
    const w = keep(mount(BacklogView, { props: { api, teams: [], pollMs: 60_000 } }));
    await settle();
    const rows = w.findAll('[data-test=backlog-row]');
    expect(rows).toHaveLength(3);
    expect(rows[0]!.attributes('draggable')).toBe('false');
    expect(rows[0]!.find('[title="Higher priority"]').exists()).toBe(false);
    expect(w.find('[data-test=backlog-row-menu-list]').exists()).toBe(false);
    await rows[1]!.get('[data-test=backlog-row-menu]').trigger('click');
    expect(rows[1]!.get('[data-test=backlog-row-menu]').attributes('aria-expanded')).toBe('true');
    expect(w.findAll('[data-test=backlog-row-menu-list]')).toHaveLength(1);
    await w.get('[data-test=backlog-row-up]').trigger('click');
    await settle();
    expect(called('backlog.move')[0]!.params).toEqual({ id: 2, direction: 'up' });
    expect(w.find('[data-test=backlog-row-menu-list]').exists()).toBe(false); // the menu closes after the move
    expect(w.findAll('[data-test=backlog-row]').map((r) => r.get('.ttl').text())).toEqual(['Second', 'First', 'Third']);
    // The opening of a row is not triggered by its menu.
    expect(w.find('[data-test=backlog-item]').exists()).toBe(false);

    // The sort is in the header's fold (B054), closed at first.
    expect(w.find('[data-test=backlog-sort-key]').exists()).toBe(false);
    await w.get('[data-test=backlog-tools-toggle]').trigger('click');
    await w.get('[data-test=backlog-sort-key]').setValue('title');
    expect(w.findAll('[data-test=backlog-row]').map((r) => r.get('.ttl').text())).toEqual(['First', 'Second', 'Third']);
    await w.get('[data-test=backlog-sort-dir]').trigger('click');
    expect(w.findAll('[data-test=backlog-row]').map((r) => r.get('.ttl').text())).toEqual(['Third', 'Second', 'First']);
    // A sort other than priority: no menu (as on the desktop, where ↑ ↓ show only by priority).
    expect(w.find('[data-test=backlog-row-menu]').exists()).toBe(false);
  });

  it('an opened item takes the page, with Back to the list', async () => {
    screen(Phone);
    const { api } = backlogApi();
    const w = keep(mount(BacklogView, { props: { api, teams: [], pollMs: 60_000 } }));
    await settle();
    await w.findAll('[data-test=backlog-row]')[0]!.trigger('click');
    await settle();
    expect(w.find('[data-test=backlog-item]').exists()).toBe(true);
    expect((w.get('[data-test=backlog-listwrap]').element as HTMLElement).style.display).toBe('none');
    await w.get('[data-test=backlog-item-back]').trigger('click');
    expect(w.find('[data-test=backlog-item]').exists()).toBe(false);
    expect((w.get('[data-test=backlog-listwrap]').element as HTMLElement).style.display).toBe('');
  });

  it('at desktop width: the table with ↑ ↓ per row, draggable rows, no menu or sort select; the item beside the list', async () => {
    screen(Desktop);
    const { api } = backlogApi();
    const w = keep(mount(BacklogView, { props: { api, teams: [], pollMs: 60_000 } }));
    await settle();
    expect(w.find('[data-test=backlog-row-menu]').exists()).toBe(false);
    expect(w.find('[data-test=backlog-sort]').exists()).toBe(false);
    const row = w.findAll('[data-test=backlog-row]')[1]!;
    expect(row.attributes('draggable')).toBe('true');
    expect(row.find('[title="Higher priority"]').exists()).toBe(true);
    expect(w.findAll('thead th').length).toBe(7);
    await row.trigger('click');
    await settle();
    expect(w.find('[data-test=backlog-item-back]').exists()).toBe(false);
    expect((w.get('[data-test=backlog-listwrap]').element as HTMLElement).style.display).toBe('');
  });
});

// ---------------------------------------------------------------- Teams

const member = (id: number, name: string) => ({ id, teamId: 1, team: 'payments', name, role: id === 1 ? 'manager' : 'member', cli: 'claude', instructions: '', model: '', credential: 'default' });
const agent = (id: number, name: string): AgentCard => ({
  member: member(id, name), state: 'idle', queuePosition: null, queueLength: 0, queuedInstructions: 0, statusLine: null, runId: null,
  runStartedAt: null, lastRunState: null, lastRunReason: null,
} as unknown as AgentCard);
const team = { id: 1, name: 'payments', repoPath: null, members: 2, running: 0, queued: 0 };
function teamApi() {
  return fakeApi({
    'teams.agents': () => [agent(1, 'Manager'), agent(2, 'DeveloperAda')], 'bus.decisions': () => [], 'integrations.asks': () => [],
    'monitor.messages': () => ({ end: 0, messages: [] }), 'monitor.steps': () => ({ end: 0, pending: false, steps: [] }),
    'agentProfiles.list': () => [], 'bus.workflows': () => [],
  });
}

describe('Teams on a phone', () => {
  it("a team's members are stacked, each one's pane showing one view (its Report first) at its own height, with no resize handles", async () => {
    screen(Phone);
    const { api } = teamApi();
    const w = keep(mount(TeamView, { props: { team, api, pollMs: 100_000 }, global: { stubs: { TeamSyncBanner: true } } }));
    await settle();
    // B076: the member tiles, one per row, then the New member tile last.
    const cells = w.findAll('.member-cell:not(.new-cell)');
    expect(cells).toHaveLength(2);
    expect(w.findAll('.member-cell').at(-1)!.find('[data-test=member-card-new-tile]').exists()).toBe(true);
    for (const c of cells) {
      const row = c.get('[data-test=member-pane-body]');
      // One view: the chosen tab's (the Report by default); the tabs above it.
      expect([...row.element.children].map((k) => k.getAttribute('data-test'))).toEqual(['agent-output']);
      expect(c.findAll('[data-test=member-tabs] button').map((b) => b.attributes('data-test')))
        .toEqual(['member-view-report', 'member-view-activity', 'member-view-terminal']);
      expect((row.element as HTMLElement).style.height).toBe('');
      // The Activity tab shows the Activity in the same place.
      await c.get('[data-test=member-view-activity]').trigger('click');
      expect(c.get('[data-test=member-pane-body]').find('[data-test=member-activity]').exists()).toBe(true);
      expect(c.get('[data-test=member-pane-body]').find('[data-test=agent-output]').exists()).toBe(false);
    }
    expect(w.find('.tile-grip').exists()).toBe(false);
    expect(w.get('section.team').classes()).toContain('phone');
  });

  it('at desktop width: one view per member, the member tiles with their width and height handles (B076)', async () => {
    screen(Desktop);
    const { api } = teamApi();
    const w = keep(mount(TeamView, { props: { team, api, pollMs: 100_000 }, global: { stubs: { TeamSyncBanner: true } } }));
    await settle();
    const body = w.findAll('[data-test=member-pane-body]')[0]!.element as HTMLElement;
    expect(body.style.height).toMatch(/px$/);
    expect(w.find('[data-test=member-side]').exists()).toBe(false);
    // Two members and the New member tile, each with a width and a height handle in its gaps.
    expect(w.findAll('[data-test=member-tile-handle-w]')).toHaveLength(3);
    expect(w.findAll('[data-test=member-tile-handle-h]')).toHaveLength(3);
    expect(w.get('section.team').classes()).not.toContain('phone');
  });

  it('the Teams page and the member pieces stack into one column at phone width (scoped @media rules)', () => {
    const src = (p: string) => readFileSync(resolve(__dirname, '../../src/renderer/src', p), 'utf8');
    const phoneRules = (p: string) => /@media \(max-width: 759\.98px\) \{([\s\S]*?)\n\}/.exec(src(p))?.[1] ?? '';
    expect(phoneRules('components/TeamsList.vue')).toMatch(/\.team-grid \{ grid-template-columns: minmax\(0, 1fr\)/);
    // The member's pane is one column everywhere (one view under its tabs); on a phone the view is at its own height.
    expect(src('components/MemberPane.vue')).toMatch(/\n\.body \{ display: flex; flex-direction: column/);
    expect(phoneRules('components/MemberPane.vue')).toMatch(/\.body \{ height: auto; \}/);
    expect(src('components/TileGrip.vue')).toMatch(/@media \(max-width: 759\.98px\) \{ \.tile-grip \{ display: none; \} \}/);
    expect(phoneRules('components/MemberPane.vue')).toMatch(/min-width: 44px/);
    for (const f of ['GitBanner', 'HostChecksBanner', 'TeamSyncBanner', 'UpdateBanner', 'TeamWorkflows', 'AgentTile', 'MemberActivity'])
      expect(src(`components/${f}.vue`), f).toContain('@media (max-width: 759.98px)');
  });

  it('the Teams list still renders its tiles at phone width (its grips are hidden by CSS)', async () => {
    screen(Phone);
    const { api } = teamApi();
    const w = keep(mount(TeamsList, { props: { teams: [{ ...team, paused: false } as any], archived: [], api, refresh: () => {} } }));
    await settle();
    expect(w.findAll('[data-test=team-card]')).toHaveLength(1);
  });
});

// ---------------------------------------------------------------- the Monitor

function monitorApi() {
  return fakeApi({
    'app.overview': () => ({ slots: { running: 0, limit: 4, queued: 0 }, teams: [{ id: 1, name: 'payments', repoPath: null, members: 1, running: 0, queued: 0 }] }),
    'teams.agents': () => [agent(1, 'Manager')],
    'monitor.messages': () => ({ end: 0, messages: [] }),
    'monitor.steps': () => ({ end: 0, pending: false, steps: [] }),
    'bus.workflows': () => [],
  });
}

describe('the Monitor on a phone', () => {
  it('starts with the activity collapsed (whatever the desktop left), and the legend toggle sits in the camera row', async () => {
    screen(Phone);
    localStorage.setItem('yawble.monitor.activity', 'open');
    const { api } = monitorApi();
    const w = keep(mount(MonitorView, { props: { api, pollMs: 60_000 } }));
    await settle();
    expect(w.get('[data-test=monitor]').classes()).toContain('phone');
    expect(w.get('[data-test=monitor-activity]').classes()).toContain('collapsed');
    expect(w.findAll('[data-test=monitor-legend-toggle]')).toHaveLength(1);
    expect(w.get('.controls').find('[data-test=monitor-legend-toggle]').exists()).toBe(true);
    expect(w.get('.controls').find('[data-test=monitor-camera]').exists()).toBe(true);
    // Opened on the phone: shown, but the desktop's choice is not changed.
    await w.get('[data-test=monitor-activity-toggle]').trigger('click');
    expect(w.get('[data-test=monitor-activity]').classes()).not.toContain('collapsed');
    await w.get('[data-test=monitor-activity-toggle]').trigger('click');
    expect(localStorage.getItem('yawble.monitor.activity')).toBe('open');
    localStorage.removeItem('yawble.monitor.activity');
  });

  it('at desktop width: the activity as the person left it (open by default), the legend toggle on the wall', async () => {
    screen(Desktop);
    const { api } = monitorApi();
    const w = keep(mount(MonitorView, { props: { api, pollMs: 60_000 } }));
    await settle();
    expect(w.get('[data-test=monitor]').classes()).not.toContain('phone');
    expect(w.get('[data-test=monitor-activity]').classes()).not.toContain('collapsed');
    expect(w.get('.controls').find('[data-test=monitor-legend-toggle]').exists()).toBe(false);
    expect(w.findAll('[data-test=monitor-legend-toggle]')).toHaveLength(1);
    // The forces cog stays beside Pinned, as main has it.
    const order = w.get('.controls').element.children;
    expect((order[0] as HTMLElement).dataset.test).toBe('monitor-camera');
    expect((order[1] as HTMLElement).dataset.test).toBe('monitor-layout-toggle');
    expect(order).toHaveLength(2);
  });

  it('puts the forces cog in the compact row with the camera and the legend, and opens the forces panel from it', async () => {
    screen(Phone);
    const { api } = monitorApi();
    const w = keep(mount(MonitorView, { props: { api, pollMs: 60_000 } }));
    await settle();
    const row = [...w.get('.controls').element.children].map((e) => (e as HTMLElement).dataset.test);
    expect(row).toEqual(['monitor-camera', 'monitor-layout-toggle', 'monitor-legend-toggle']);
    expect(w.findAll('[data-test=monitor-layout-toggle]')).toHaveLength(1);
    expect(w.find('[data-test=monitor-layout]').exists()).toBe(false);
    await w.get('[data-test=monitor-layout-toggle]').trigger('click');
    expect(w.get('[data-test=monitor-layout-toggle]').attributes('aria-expanded')).toBe('true');
    expect(w.find('[data-test=monitor-layout]').exists()).toBe(true);
    await w.get('[data-test=monitor-layout-close]').trigger('click');
    expect(w.find('[data-test=monitor-layout]').exists()).toBe(false);
  });

  it('stays dark at phone width: its phone rules use no theme variables', () => {
    const src = readFileSync(resolve(__dirname, '../../src/renderer/src/monitor/MonitorView.vue'), 'utf8');
    const rules = /@media \(max-width: 759\.98px\) \{([\s\S]*?)\n\}/.exec(src)?.[1] ?? '';
    expect(rules).toContain('.controls');
    expect(rules).not.toContain('var(--');
  });

  it('fits the forces panel to the phone screen, with 44 px tap targets', () => {
    const src = readFileSync(resolve(__dirname, '../../src/renderer/src/monitor/MonitorView.vue'), 'utf8');
    const rules = /@media \(max-width: 759\.98px\) \{([\s\S]*?)\n\}/.exec(src)?.[1] ?? '';
    expect(rules).toMatch(/\.controls \.cog \{[^}]*min-width: 44px/);
    expect(rules).toMatch(/\.forces \{[^}]*left: 8px; right: 8px; width: auto;[^}]*overflow-y: auto/);
    expect(rules).toMatch(/\.forces \.ctl \{[^}]*min-height: 44px; min-width: 44px/);
    expect(rules).toMatch(/\.force input \{[^}]*min-height: 44px/);
  });
});

// ---------------------------------------------------------------- Logs

const col = (key: string, label: string, kind: 'time' | 'text' | 'number' = 'text') => ({ key, label, kind, filter: false });
const logSources: LogSourceInfo[] = [{
  id: 'activity', title: 'All activity', group: 'Activity', about: 'Everything.', columns: [col('at', 'Time', 'time'), col('member', 'Member'), col('event', 'Event'), col('details', 'Details')],
  prunable: false, loss: null, keptBecause: 'read from the others', defaultDays: null, retentionDays: null, team: true, member: true,
} as unknown as LogSourceInfo];
function logsApi() {
  const rows = [
    { _id: '1', at: '2026-11-13T12:00:00Z', member: 'web/DeveloperAda', event: 'run 1', details: 'a long line of details' },
    { _id: '2', at: '2026-11-13T11:00:00Z', member: 'web/Manager', event: 'run 2', details: null },
  ];
  return fakeApi({
    'logs.sources': () => structuredClone(logSources), 'logs.who': () => ({ teams: [], members: [] }), 'logs.values': () => [],
    'logs.query': () => ({ rows, total: 2, asOf: '2026-11-13T12:00:00Z', next: null }),
  });
}

describe('Logs on a phone', () => {
  it('each entry is a card of its labelled values (empty ones left out); a tap opens it in full; sort is a select', async () => {
    screen(Phone);
    const { api, called } = logsApi();
    const w = keep(mount(LogsDialog, { props: { api }, attachTo: document.body }));
    await settle();
    expect(w.find('table.grid').exists()).toBe(false);
    const cards = w.findAll('[data-test=logs-cards] [data-test=logs-row]');
    expect(cards).toHaveLength(2);
    expect(cards[0]!.findAll('dt').map((d) => d.text())).toEqual(['Time', 'Member', 'Event', 'Details']);
    expect(cards[1]!.findAll('dt').map((d) => d.text())).toEqual(['Time', 'Member', 'Event']);
    expect(cards[0]!.text()).toContain('run 1');
    await cards[1]!.trigger('click');
    expect(cards[1]!.classes()).toContain('open');
    expect(cards[1]!.attributes('aria-expanded')).toBe('true');
    expect(cards[1]!.findAll('dt').map((d) => d.text())).toEqual(['Time', 'Member', 'Event', 'Details']); // in full: every column
    expect(cards[1]!.findAll('dd')[0]!.text()).toBe('2026-11-13T11:00:00Z');

    await w.get('[data-test=logs-sort-key]').setValue('event');
    await settle();
    const last = called('logs.query').at(-1)!.params;
    expect([last.sort, last.descending]).toEqual(['event', false]);
    await w.get('[data-test=logs-sort-dir]').trigger('click');
    await settle();
    expect(called('logs.query').at(-1)!.params.descending).toBe(true);
  });

  it('the filters fold behind a Filters button (closed at first, counting what is set), so the entries get the screen', async () => {
    screen(Phone);
    const { api } = logsApi();
    const w = keep(mount(LogsDialog, { props: { api, team: 'web' }, attachTo: document.body }));
    await settle();
    const filters = w.get('[data-test=logs-filters]').element as HTMLElement;
    expect(filters.style.display).toBe('none');
    expect(w.find('[data-test=logs-about]').exists()).toBe(false);
    const toggle = w.get('[data-test=logs-filters-toggle]');
    expect(toggle.text()).toBe('Filters (1) ▾'); // the team it was opened for
    await toggle.trigger('click');
    expect(filters.style.display).toBe('');
    expect(toggle.attributes('aria-expanded')).toBe('true');
  });

  it('at desktop width the filters are always shown, with no Filters button', async () => {
    screen(Desktop);
    const { api } = logsApi();
    const w = keep(mount(LogsDialog, { props: { api }, attachTo: document.body }));
    await settle();
    expect((w.get('[data-test=logs-filters]').element as HTMLElement).style.display).toBe('');
    expect(w.find('[data-test=logs-filters-toggle]').exists()).toBe(false);
  });

  it('at desktop width: the table with its sortable column headers, no cards', async () => {
    screen(Desktop);
    const { api } = logsApi();
    const w = keep(mount(LogsDialog, { props: { api }, attachTo: document.body }));
    await settle();
    expect(w.find('[data-test=logs-cards]').exists()).toBe(false);
    expect(w.find('[data-test=logs-sort]').exists()).toBe(false);
    expect(w.findAll('table.grid thead th').map((t) => t.text())).toEqual(['Time▼', 'Member', 'Event', 'Details']);
    expect(w.findAll('tr[data-test=logs-row]')).toHaveLength(2);
  });
});

// ---------------------------------------------------------------- Documents

function docsApi() {
  return fakeApi({
    'docs.list': (p) => ({ path: p.path ?? '', entries: [{ name: 'report.md', path: 'report.md', folder: false, size: 20 }, { name: 'specs', path: 'specs', folder: true, size: null }] }),
    'docs.read': (p) => ({ path: p.path, name: 'report.md', kind: 'markdown', size: 20, text: '# Report\n\nDone.' }),
  });
}
const shown = (w: VueWrapper, sel: string) => (w.get(sel).element as HTMLElement).style.display !== 'none';

describe('Documents on a phone', () => {
  it('the list first; a file opened takes the area, with Back to the list', async () => {
    screen(Phone);
    const { api } = docsApi();
    const w = keep(mount(DocumentsBrowser, { props: { api, team: team as any, fill: true } }));
    await settle();
    expect(shown(w, '[data-test=docs-list]')).toBe(true);
    expect(shown(w, '[data-test=docs-viewer]')).toBe(false);
    await w.get('[data-test="doc-report.md"]').trigger('click');
    await settle();
    expect(shown(w, '[data-test=docs-list]')).toBe(false);
    expect(shown(w, '[data-test=docs-viewer]')).toBe(true);
    expect(w.get('[data-test=docs-viewer]').text()).toContain('Report');
    expect(w.get('[data-test=docs-back]').text()).toBe('← Documents');
    await w.get('[data-test=docs-back]').trigger('click');
    expect(shown(w, '[data-test=docs-list]')).toBe(true);
    expect(shown(w, '[data-test=docs-viewer]')).toBe(false);
  });

  it('at desktop width: the list and the viewer side by side, no Back', async () => {
    screen(Desktop);
    const { api } = docsApi();
    const w = keep(mount(DocumentsBrowser, { props: { api, team: team as any } }));
    await settle();
    expect(shown(w, '[data-test=docs-list]')).toBe(true);
    expect(shown(w, '[data-test=docs-viewer]')).toBe(true);
    await w.get('[data-test="doc-report.md"]').trigger('click');
    await settle();
    expect(shown(w, '[data-test=docs-list]')).toBe(true);
    expect(w.find('[data-test=docs-back]').exists()).toBe(false);
  });

  it('every breadcrumb (the Documents link up and each folder) is at least 44x44 at phone width only (F6)', async () => {
    screen(Phone);
    const { api } = docsApi();
    const w = keep(mount(DocumentsBrowser, { props: { api, team: team as any, fill: true, above: 'Documents' } }));
    await settle();
    // The crumbs the rules reach: the link up and the team's own folder, all <a> inside nav.crumbs.
    const links = w.findAll('nav.crumbs > a');
    expect(links.map((a) => a.text())).toEqual(['Documents', team.name]);
    expect(links[0]!.attributes('data-test')).toBe('docs-up');
    const src = readFileSync(resolve(__dirname, '../../src/renderer/src/components/DocumentsBrowser.vue'), 'utf8');
    expect(phoneRules(src)).toMatch(/\.crumbs a \{[^}]*min-height: 44px; min-width: 44px; box-sizing: border-box/);
    expect(desktopRules(src)).toMatch(/\.crumbs a \{ color: var\(--accent\); text-decoration: none; \}/);
    expect(desktopRules(src)).not.toMatch(/\.crumbs a \{[^}]*min-/);
  });
});

// ---------------------------------------------------------------- the Code browser

const md = new MarkdownIt({ html: false });
function codeApi() {
  const listeners = new Set<(e: any) => void>();
  const calls: string[] = [];
  const api = {
    async call(method: string, params: any) {
      calls.push(method);
      if (method === 'code.open') return { session: 's1', root: 'C:/x', latest: { path: 'src/app.ts', line: 3 }, clonePresent: true };
      if (method === 'code.list') return { path: params.path, entries: [
        { name: 'app.ts', path: 'src/app.ts', kind: 'file', size: 30, mtime: new Date().toISOString(), marker: 'live', hidden: false },
        { name: 'util.ts', path: 'src/util.ts', kind: 'file', size: 30, mtime: new Date().toISOString(), marker: 'live', hidden: false },
      ] };
      if (method === 'code.read') return { path: params.path, type: 'text', size: 30, content: 'const a = 1;\nconst b = 2;\nconst c = 3;\n', lines: [], removed: [], linesUnknown: false };
      if (method === 'code.close') return {};
      throw new Error(method);
    },
    onEvent(l: (e: any) => void) { listeners.add(l); return () => listeners.delete(l); },
  };
  return { api, calls };
}

describe('the Code browser on a phone', () => {
  it('the list first (the latest file ready behind it); a file opened is full-screen with ← Files back to the list', async () => {
    screen(Phone);
    const { api } = codeApi();
    const w = keep(mount(CodeBrowser, { props: { api, team: 'payments', member: 'DeveloperAda', renderMarkdown: (t: string) => md.render(t), tickMs: 0 }, attachTo: document.body }));
    await settle();
    expect(shown(w, '[data-test=code-list-pane]')).toBe(true);
    expect(shown(w, '[data-test=code-file-pane]')).toBe(false);
    // The file it opened on is one tap away.
    expect(w.get('[data-test=code-show-file]').text()).toBe('app.ts →');
    await w.get('[data-test="code-entry-src/util.ts"] button').trigger('click');
    await settle();
    expect(shown(w, '[data-test=code-list-pane]')).toBe(false);
    expect(shown(w, '[data-test=code-file-pane]')).toBe(true);
    expect(w.get('[data-test=code-crumb-file]').text()).toBe('util.ts');
    await w.get('[data-test=code-back-to-list]').trigger('click');
    expect(shown(w, '[data-test=code-list-pane]')).toBe(true);
    expect(shown(w, '[data-test=code-file-pane]')).toBe(false);
    await w.get('[data-test=code-show-file]').trigger('click');
    expect(shown(w, '[data-test=code-file-pane]')).toBe(true);
  });

  it('at desktop width: the list and the file side by side, no back controls', async () => {
    screen(Desktop);
    const { api } = codeApi();
    const w = keep(mount(CodeBrowser, { props: { api, team: 'payments', member: 'DeveloperAda', renderMarkdown: (t: string) => md.render(t), tickMs: 0 }, attachTo: document.body }));
    await settle();
    expect(shown(w, '[data-test=code-list-pane]')).toBe(true);
    expect(shown(w, '[data-test=code-file-pane]')).toBe(true);
    expect(w.find('[data-test=code-back-to-list]').exists()).toBe(false);
    expect(w.find('[data-test=code-show-file]').exists()).toBe(false);
  });
});

// ---------------------------------------------------------------- every screen's phone styles

describe('the phone width in the scoped styles', () => {
  it('each screen of B037 has a @media (max-width: 759.98px) block, and none uses another phone width', () => {
    const root = resolve(__dirname, '../..');
    const files = [
      'src/renderer/src/board/BoardView.vue', 'src/renderer/src/board/BoardCardItem.vue', 'src/renderer/src/board/CardPanel.vue', 'src/renderer/src/board/BacklogView.vue',
      'src/renderer/src/monitor/MonitorView.vue', 'src/renderer/src/monitor/Timeline.vue', 'src/renderer/src/components/LogsDialog.vue',
      'src/renderer/src/components/DocumentsView.vue', 'src/renderer/src/components/DocumentsBrowser.vue', 'src/renderer/src/components/TeamView.vue',
      'src/renderer/src/components/TeamsList.vue', '../code-browser/src/CodeBrowser.vue', '../code-browser/src/FileList.vue',
    ];
    for (const f of files) {
      const text = readFileSync(resolve(root, f), 'utf8');
      expect(text, f).toContain('@media (max-width: 759.98px)');
      expect(text, f).not.toMatch(/max-width: 7[56]\dpx\)/);
    }
  });
});
