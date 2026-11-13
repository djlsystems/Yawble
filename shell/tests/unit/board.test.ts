// @vitest-environment happy-dom
import { flushPromises, mount } from '@vue/test-utils';
import { describe, expect, it } from 'vitest';
import BoardView from '../../src/renderer/src/board/BoardView.vue';
import type { BoardCard, YawbleApi } from '../../src/shared/api';

const card = (id: number, lane: BoardCard['lane'], status: BoardCard['status'], member: string | null, title: string, team = 'payments'): BoardCard => ({
  id, team, teamId: 1, member, item: id === 3 ? 'B007' : null, title, body: `${title} in full`, status, lane, placed: false,
  awaitingManager: id === 2, workflow: 10 + id, createdAt: '2026-11-08T10:00:00Z', updatedAt: `2026-11-08T10:0${id}:00Z`, trailCount: id,
});

function fakeApi() {
  const calls: { method: string; params: any }[] = [];
  const cards = [
    card(1, 'in-progress', 'running', 'DeveloperPriya', 'Write the README'),
    card(2, 'blocked', 'needs-decision', 'TesterWren', 'Pick a licence'),
    card(3, 'todo', 'queued', null, 'Add LICENSE'),
    card(4, 'done', 'handback', 'DeveloperKofi', 'Greet in French', 'i18n'),
  ];
  const api = {
    call: async (method: string, params?: any) => {
      calls.push({ method, params });
      if (method === 'board.list') return { lanes: [{ id: 'todo', label: 'To Do' }, { id: 'in-progress', label: 'In Progress' }, { id: 'blocked', label: 'Needs You' }, { id: 'done', label: 'Done' }], cards, teams: [{ id: 1, name: 'payments' }, { id: 2, name: 'i18n' }] };
      if (method === 'board.card') return { ...cards.find((c) => c.id === params.id)!, trail: [{ id: 1, at: '2026-11-08T10:00:00Z', source: 'Manager', kind: 'told', text: 'Write it' }] };
      if (method === 'board.move') return { ...cards.find((c) => c.id === params.id)!, lane: params.lane, awaitingManager: true, trail: [] };
      throw new Error(method);
    },
    onEvent: () => () => {},
  } as unknown as YawbleApi;
  return { api, calls };
}

describe('the Board', () => {
  it('puts each card in its lane with its member, and filters by team, member and search', async () => {
    const { api } = fakeApi();
    const w = mount(BoardView, { props: { api, pollMs: 60_000 } });
    await flushPromises();
    expect(w.get('[data-test=lane-in-progress]').text()).toContain('Write the README');
    expect(w.get('[data-test=lane-blocked]').text()).toContain('Pick a licence');
    expect(w.get('[data-test=lane-blocked]').text()).toContain('Needs your decision');
    expect(w.get('[data-test=lane-blocked]').text()).toContain('awaiting Manager');
    expect(w.get('[data-test=lane-todo]').text()).toContain('Unassigned');
    expect(w.get('[data-test=lane-todo]').text()).toContain('B007');
    expect(w.get('[data-test=board-card-1]').text()).toContain('Developer Priya');

    await w.get('[data-test=board-team]').setValue('i18n');
    expect(w.findAll('[data-card]')).toHaveLength(1);
    await w.get('[data-test=board-clear]').trigger('click');
    await w.get('[data-test=board-search]').setValue('licen');
    expect(w.findAll('[data-card]').map((c) => c.text())).toHaveLength(2);
  });

  it('opens a card and moves it, which the Manager is told', async () => {
    const { api, calls } = fakeApi();
    const w = mount(BoardView, { props: { api, pollMs: 60_000 } });
    await flushPromises();
    await w.get('[data-test=board-card-1]').trigger('click');
    await flushPromises();
    expect(w.get('[data-test=card-panel]').text()).toContain('Write the README in full');
    expect(w.get('[data-test=card-panel]').text()).toContain('Write it');
    await w.get('[data-test=card-lane]').setValue('done');
    await w.get('[data-test=card-note]').setValue('it shipped');
    await w.get('[data-test=card-move]').trigger('click');
    await flushPromises();
    expect(calls.find((c) => c.method === 'board.move')!.params).toEqual({ id: 1, lane: 'done', note: 'it shipped' });
    expect(w.get('[data-test=card-panel]').text()).toContain('awaiting Manager');
  });
});

describe('the Board: Needs You', () => {
  it('lists a connection that needs reconnecting, with its reason and where to fix it, and opens Connections', async () => {
    const { api, calls } = fakeApi();
    const call = api.call.bind(api);
    let attention = [{ id: 7, name: 'Pets key', reason: 'the service answered 401 Unauthorized', where: 'Settings → Integrations → Connections' }];
    let listener: ((e: { event: string; data: unknown }) => void) | null = null;
    (api as any).onEvent = (l: typeof listener) => { listener = l; return () => { listener = null; }; };
    (api as any).call = async (method: string, params?: any) => {
      if (method === 'integrations.connectionAttention') { calls.push({ method, params }); return attention; }
      return call(method, params);
    };
    const w = mount(BoardView, { props: { api, pollMs: 60_000 } });
    await flushPromises();
    const item = w.get('[data-test=needs-reconnect-7]');
    expect(item.text()).toContain('The connection Pets key needs reconnecting');
    expect(item.text()).toContain('the service answered 401 Unauthorized');
    expect(item.text()).toContain('Settings → Integrations → Connections');
    expect(w.get('[data-test=lane-blocked] .count').text()).toBe('2'); // the question card and the connection
    await item.trigger('click');
    expect(w.emitted('connections')).toBeTruthy();

    attention = [];
    listener!({ event: 'connections.changed', data: null });
    await flushPromises();
    expect(w.find('[data-test=needs-reconnect-7]').exists()).toBe(false);
  });

  it('a remote view never asks for connections', async () => {
    const { api, calls } = fakeApi();
    (api as any).remote = true;
    mount(BoardView, { props: { api, pollMs: 60_000 } });
    await flushPromises();
    expect(calls.some((c) => c.method.startsWith('integrations.connection'))).toBe(false);
  });
});

describe('the Board: Clear done', () => {
  /**
   * A fake core with Done cards in two teams. board.list with includeCleared returns every card with its clearedAt;
   * clearDone hides the ids given that are in Done; restore brings one back; both push board.changed, as the core does.
   */
  function clearingApi() {
    const calls: { method: string; params: any }[] = [];
    const cards = [
      card(1, 'in-progress', 'running', 'DeveloperPriya', 'Write the README'),
      card(4, 'done', 'handback', 'DeveloperKofi', 'Greet in French', 'i18n'),
      card(5, 'done', 'done', 'DeveloperPriya', 'Ship the README'),
      card(6, 'done', 'done', 'TesterWren', 'Test the README'),
    ];
    const hidden = new Set<number>();
    const listeners = new Set<(e: { event: string; data: unknown }) => void>();
    const changed = () => { for (const l of [...listeners]) l({ event: 'board.changed', data: null }); };
    const lanes = [{ id: 'todo', label: 'To Do' }, { id: 'in-progress', label: 'In Progress' }, { id: 'blocked', label: 'Needs You' }, { id: 'done', label: 'Done' }];
    const api = {
      call: async (method: string, params?: any) => {
        calls.push({ method, params });
        if (method === 'board.list') {
          const all = cards.map((c) => ({ ...c, clearedAt: hidden.has(c.id) ? '2026-11-12T09:00:00Z' : null }));
          return {
            lanes, teams: [{ id: 1, name: 'payments' }, { id: 2, name: 'i18n' }],
            cards: params?.includeCleared ? all : all.filter((c) => !c.clearedAt), clearedCount: hidden.size,
          };
        }
        if (method === 'board.clearDone') {
          const n = cards.filter((c) => params.ids.includes(c.id) && c.lane === 'done' && !hidden.has(c.id));
          n.forEach((c) => hidden.add(c.id));
          if (n.length) changed();
          return { cleared: n.length };
        }
        if (method === 'board.restore') {
          const restored = hidden.delete(params.id);
          if (restored) changed();
          return { restored };
        }
        throw new Error(method);
      },
      onEvent: (l: (e: { event: string; data: unknown }) => void) => { listeners.add(l); return () => listeners.delete(l); },
    } as unknown as YawbleApi;
    return { api, calls, hidden, changed };
  }
  const lists = (calls: { method: string }[]) => calls.filter((c) => c.method === 'board.list').length;

  it('shows Clear only when Done has cards, and clears exactly the Done cards shown', async () => {
    const { api, calls } = clearingApi();
    const w = mount(BoardView, { props: { api, pollMs: 60_000 } });
    await flushPromises();
    expect(calls[0]).toEqual({ method: 'board.list', params: { includeCleared: true } }); // one call serves board and cleared
    const clear = w.get('[data-test=board-clear-done]');
    expect(clear.text()).toBe('Clear');
    expect(clear.attributes('title')).toBe('Hide the 3 cards shown in Done from the board; nothing is deleted');
    expect(w.find('[data-test=board-show-cleared]').exists()).toBe(false); // nothing cleared yet

    // payments, and only Priya's: Wren's Done card is filtered out of sight and is not cleared.
    await w.get('[data-test=board-team]').setValue('payments');
    await w.get('[data-test=board-member]').setValue('DeveloperPriya');
    expect(w.get('[data-test=board-clear-done]').attributes('title')).toBe('Hide the 1 card shown in Done from the board; nothing is deleted');
    const before = lists(calls);
    await w.get('[data-test=board-clear-done]').trigger('click');
    await flushPromises();
    expect(calls.find((c) => c.method === 'board.clearDone')!.params).toEqual({ ids: [5] });
    expect(lists(calls)).toBe(before + 1); // reloaded once, by board.changed
    expect(w.get('[data-test=lane-done]').text()).not.toContain('Ship the README');
    expect(w.find('[data-test=board-clear-done]').exists()).toBe(false); // nothing shown in Done now
    expect(w.get('[data-test=board-show-cleared]').text()).toBe('Show cleared (1)');

    // Everyone in payments: Wren's card is still there, and Clear sends only it.
    await w.get('[data-test=board-member]').setValue('');
    expect(w.get('[data-test=lane-done]').text()).toContain('Test the README');
    await w.get('[data-test=board-clear-done]').trigger('click');
    await flushPromises();
    expect(calls.filter((c) => c.method === 'board.clearDone')[1].params).toEqual({ ids: [6] });
    expect(w.get('[data-test=board-show-cleared]').text()).toBe('Show cleared (2)');
    await w.get('[data-test=board-team]').setValue('');
    expect(w.get('[data-test=lane-done]').text()).toContain('Greet in French'); // i18n was never touched
  });

  it('without events (an older bridge) an action reloads by itself', async () => {
    const { api, calls } = clearingApi();
    (api as any).onEvent = undefined;
    const w = mount(BoardView, { props: { api, pollMs: 60_000 } });
    await flushPromises();
    const before = lists(calls);
    await w.get('[data-test=board-clear-done]').trigger('click');
    await flushPromises();
    expect(lists(calls)).toBe(before + 1);
    expect(w.find('[data-test=board-clear-done]').exists()).toBe(false);
  });

  it('Show cleared lists them dimmed with Restore, which calls board.restore', async () => {
    const { api, calls, hidden } = clearingApi();
    hidden.add(5);
    const w = mount(BoardView, { props: { api, pollMs: 60_000 } });
    await flushPromises();
    expect(w.find('[data-test=board-cleared]').exists()).toBe(false);
    const before = lists(calls);
    await w.get('[data-test=board-show-cleared]').trigger('click');
    await flushPromises();
    expect(lists(calls)).toBe(before); // the cleared cards came with the board
    expect(w.get('[data-test=cleared-card-5]').text()).toContain('Ship the README');
    expect(w.get('[data-test=board-show-cleared]').text()).toBe('Hide cleared (1)');

    await w.get('[data-test=board-restore-5]').trigger('click');
    await flushPromises();
    expect(calls.find((c) => c.method === 'board.restore')!.params).toEqual({ id: 5 });
    expect(lists(calls)).toBe(before + 1);
    expect(w.get('[data-test=lane-done]').text()).toContain('Ship the README');
    expect(w.find('[data-test=cleared-card-5]').exists()).toBe(false);
    expect(w.find('[data-test=board-show-cleared]').exists()).toBe(false); // nothing cleared any more
  });

  it('the cleared count follows the team, member and search filters, so the list it opens is never empty', async () => {
    const { api, hidden } = clearingApi();
    hidden.add(4); hidden.add(5); hidden.add(6);
    const w = mount(BoardView, { props: { api, pollMs: 60_000 } });
    await flushPromises();
    expect(w.get('[data-test=board-show-cleared]').text()).toBe('Show cleared (3)');
    await w.get('[data-test=board-team]').setValue('payments');
    expect(w.get('[data-test=board-show-cleared]').text()).toBe('Show cleared (2)');
    await w.get('[data-test=board-member]').setValue('DeveloperPriya');
    expect(w.get('[data-test=board-show-cleared]').text()).toBe('Show cleared (1)');
    await w.get('[data-test=board-show-cleared]').trigger('click');
    expect(w.findAll('[data-test^=cleared-card-]').map((c) => c.attributes('data-test'))).toEqual(['cleared-card-5']);
    await w.get('[data-test=board-search]').setValue('nothing like it');
    expect(w.find('[data-test=board-show-cleared]').exists()).toBe(false);
    await w.get('[data-test=board-search]').setValue('');
    await w.get('[data-test=board-show-cleared]').trigger('click');
    expect(w.find('[data-test=board-cleared]').exists()).toBe(false);
  });

  it('refreshes on board.changed (a card un-cleared by a move, or a clear made elsewhere), one load at a time', async () => {
    const { api, calls, hidden, changed } = clearingApi();
    hidden.add(5);
    const w = mount(BoardView, { props: { api, pollMs: 60_000 } });
    await flushPromises();
    expect(w.get('[data-test=lane-done]').text()).not.toContain('Ship the README');
    const before = lists(calls);
    hidden.delete(5); // moved out of Done and back, by an agent
    changed(); changed(); changed(); // a burst while the first reload runs
    await flushPromises();
    expect(lists(calls)).toBe(before + 2); // the first, then one more for the rest
    expect(w.get('[data-test=lane-done]').text()).toContain('Ship the README');
  });

  it('uses theme colours only', async () => {
    const fs = await import('node:fs');
    const path = await import('node:path');
    const src = fs.readFileSync(path.resolve(__dirname, '../../src/renderer/src/board/BoardView.vue'), 'utf8');
    const rules = src.split('\n').filter((l) => /^\.(clear(ed|-done)|show-cleared)/.test(l));
    expect(rules.length).toBeGreaterThan(0);
    for (const r of rules) expect(r).not.toMatch(/#[0-9a-f]{3,8}\b|rgb\(/i);
  });
});
