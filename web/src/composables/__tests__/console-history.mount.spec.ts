// @vitest-environment happy-dom
//
// THE BROWSER'S BACK AND FORWARD MOVE BETWEEN THE CONSOLE'S VIEWS. The Teams table, a team's board
// and the Kanban were switched in the store alone: the address never changed, so Back from a team's
// board left the Console instead of returning to the Teams table it was opened from.
//
// Through a real router and the real stores, with the Host's answers faked at `fetch`.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { defineComponent, h } from 'vue';
import { flushPromises, mount } from '@vue/test-utils';
import { createPinia, setActivePinia } from 'pinia';
import { createMemoryHistory, createRouter, type Router } from 'vue-router';
import { useConsoleHistory } from '../useConsoleHistory';
import { useConsoleStore } from '../../stores/console';
import { useKanbanStore } from '../../stores/kanban';
import { useHighlightStore } from '../../stores/highlight';
import type { TeamId } from '../../api/types';

const teams = [
  { id: 'alpha', name: 'alpha', containers: [] },
  { id: 'beta', name: 'beta', containers: [] },
];

function json(body: unknown) {
  return Promise.resolve(new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } }));
}

beforeEach(() => {
  localStorage.clear();
  vi.stubGlobal(
    'fetch',
    vi.fn((input: RequestInfo | URL) => {
      const url = String(input);
      if (url.includes('/api/overview')) return json({ teams, managerName: 'Manager' });
      return json([]);
    }),
  );
});

afterEach(() => {
  vi.unstubAllGlobals();
});

const Harness = defineComponent({
  setup() {
    useConsoleHistory();
    return () => h('div');
  },
});

/** The Console opened at `start`, with the team list landed as a load lands it. */
async function open(start: string): Promise<Router> {
  setActivePinia(createPinia());
  const router = createRouter({
    history: createMemoryHistory(),
    routes: [{ path: '/console', component: { render: () => h('div') } }],
  });
  await router.push(start);
  await router.isReady();
  mount(Harness, { global: { plugins: [router] } });
  await settle();
  const board = useConsoleStore();
  await board.refresh();
  await settle();
  return router;
}

async function settle() {
  for (let i = 0; i < 4; i++) await flushPromises();
}

/** A person clicking a team row: the same two calls the Teams table makes. */
async function openTeam(id: string) {
  const board = useConsoleStore();
  board.showBoard();
  board.setActiveTeam(id as TeamId);
  await settle();
}

async function back(router: Router) {
  router.back();
  await settle();
}

describe('the browser back button in the Console', () => {
  it('goes from a team opened on the Teams table back to the Teams table, and forward to the team again', async () => {
    const router = await open('/console');
    const board = useConsoleStore();
    board.showTeamsView();
    await settle();
    expect(router.currentRoute.value.query).toEqual({ view: 'teams' });

    await openTeam('alpha');
    expect(router.currentRoute.value.query).toEqual({ team: 'alpha' });

    await back(router);
    expect(board.view).toBe('teams');
    expect(board.activeTeamId).toBe('');
    expect(router.currentRoute.value.query).toEqual({ view: 'teams' });

    router.forward();
    await settle();
    expect(board.view).toBe('board');
    expect(board.activeTeamId).toBe('alpha');
  });

  it('goes from the Kanban back to the team it was opened from', async () => {
    const router = await open('/console');
    await openTeam('beta');
    useKanbanStore().showKanban('beta');
    await settle();
    expect(router.currentRoute.value.query).toEqual({ view: 'kanban' });

    await back(router);
    const board = useConsoleStore();
    expect(board.view).toBe('board');
    expect(board.activeTeamId).toBe('beta');
  });

  it('goes from the Monitor back to the team it was opened from, and an address opens the Monitor', async () => {
    const router = await open('/console');
    await openTeam('beta');
    const board = useConsoleStore();
    board.showMonitor();
    await settle();
    expect(router.currentRoute.value.query).toEqual({ view: 'monitor' });

    await back(router);
    expect(board.view).toBe('board');
    expect(board.activeTeamId).toBe('beta');

    router.forward();
    await settle();
    expect(board.view).toBe('monitor');
    expect(board.activeTeamId).toBe('beta');
  });

  it('opens the place an address names, and the load adds no step to go back through', async () => {
    const router = await open('/console?team=beta');
    const board = useConsoleStore();
    expect(board.view).toBe('board');
    expect(board.activeTeamId).toBe('beta');
    expect(router.currentRoute.value.query).toEqual({ team: 'beta' });

    // The first step a person makes is the only one Back undoes.
    board.showTeamsView();
    await settle();
    await back(router);
    expect(board.activeTeamId).toBe('beta');
  });

  it('lands on the Teams table, with the address corrected, when Back reaches a team that is gone', async () => {
    const router = await open('/console?team=gone');
    const board = useConsoleStore();
    expect(board.view).toBe('teams');
    expect(router.currentRoute.value.query).toEqual({ view: 'teams' });
  });

  it('leaves every other key in the address alone', async () => {
    const router = await open('/console?connection=done');
    await openTeam('alpha');
    expect(router.currentRoute.value.query).toEqual({ connection: 'done', team: 'alpha' });
  });
});

// THE HIGHLIGHT DEEP LINK: `member=` beside `team=` and `card=` on the Kanban are read into the
// highlight store, stay in the address through a reload until the view lands them, and are dropped
// (replacing) once it has.
describe('the highlight keys in the address', () => {
  it('reads member beside team, keeps it through a reload, and drops it once consumed', async () => {
    let router = await open('/console?team=beta&member=Mailer');
    expect(useHighlightStore().member).toEqual({ team: 'beta', member: 'Mailer' });
    expect(router.currentRoute.value.query).toEqual({ team: 'beta', member: 'Mailer' });

    // A reload before the board landed it: a new page at the same address asks again.
    router = await open('/console?team=beta&member=Mailer');
    const highlight = useHighlightStore();
    expect(highlight.member).toEqual({ team: 'beta', member: 'Mailer' });
    expect(useConsoleStore().activeTeamId).toBe('beta');

    highlight.land('member:beta/Mailer', null);
    await settle();
    expect(router.currentRoute.value.query).toEqual({ team: 'beta' });
    expect(highlight.member).toBeNull();
    expect(router.currentRoute.value.query.member).toBeUndefined();
  });

  it('reads card on the Kanban, keeps it through a reload, and drops it once consumed', async () => {
    let router = await open('/console?view=kanban&card=c1');
    expect(useHighlightStore().card).toBe('c1');
    expect(router.currentRoute.value.query).toEqual({ view: 'kanban', card: 'c1' });

    router = await open('/console?view=kanban&card=c1');
    const highlight = useHighlightStore();
    expect(highlight.card).toBe('c1');
    expect(useConsoleStore().view).toBe('kanban');

    highlight.miss('Card c1 is not on the board.');
    await settle();
    expect(router.currentRoute.value.query).toEqual({ view: 'kanban' });
    expect(highlight.card).toBe('');
  });

  it('asks again when a link names a member while the board is already showing its team', async () => {
    const router = await open('/console?team=alpha');
    expect(useHighlightStore().member).toBeNull();

    await router.push('/console?team=alpha&member=Manager');
    await settle();
    expect(useHighlightStore().member).toEqual({ team: 'alpha', member: 'Manager' });
  });

  it('drops an unconsumed highlight when the person moves to another place', async () => {
    const router = await open('/console?team=beta&member=Mailer');
    await openTeam('alpha');
    expect(router.currentRoute.value.query).toEqual({ team: 'alpha' });
    expect(useHighlightStore().member).toBeNull();
  });
});
