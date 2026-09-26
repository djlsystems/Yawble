// @vitest-environment happy-dom
//
// THE BRAKE, ON THE SCREEN WHERE THE WORK IS.
//
// Pause and Resume are on the Teams rollup, in the team Settings dialog, AND on the active team's
// heading - because the first thing anybody does when they want to stop a running team is look at
// the running team. These cases pin both what it does and, in the theory below, what it
// deliberately does NOT gate on.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';
import { flushPromises, mount } from '@vue/test-utils';

const { pauseTeam, resumeTeam } = vi.hoisted(() => ({
  pauseTeam: vi.fn(),
  resumeTeam: vi.fn(),
}));

vi.mock('quasar', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  useQuasar: () => ({ notify: vi.fn() }),
}));

vi.mock('../../api/client', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  pauseTeam,
  resumeTeam,
}));

vi.mock('../../lib/hub', () => ({ connectHub: vi.fn().mockResolvedValue(null) }));

import IndexPage from '../IndexPage.vue';
import { useConsoleStore } from '../../stores/console';
import { useSessionStore } from '../../stores/session';
// Importing this module is what installs the Quasar plugin - it registers a `beforeAll` hook
// at MODULE level, so the import itself is the setup.
import { resetBody } from '../../test/mountQuasar';

/**
 * A team in whatever state the case is about.
 *
 * `containers` is what `teamStatus` reads to decide RUNNING/IDLE/BLOCKED, so the theory below
 * drives the real derivation rather than asserting against a status string nothing computes.
 */
function team(over: Record<string, unknown> = {}) {
  return {
    id: 'alpha',
    name: 'Alpha',
    paused: false,
    containers: [],
    memberAgents: [],
    repos: [],
    env: {},
    ...over,
  };
}

function container(state: string, over: Record<string, unknown> = {}) {
  return {
    id: 'Manager',
    team: 'alpha',
    state,
    label: 'Manager',
    progress: null,
    blocked: null,
    ...over,
  };
}

beforeEach(() => {
  // AN UNMOCKED `fetch` IN HAPPY-DOM REACHES A REAL SOCKET and rejects AFTER the assertions have
  // run - an unhandled rejection that fails the FILE while every case in it passes, which reads as a
  // defect in the page. The board makes several loads on mount that these cases do not care about.
  vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response('[]', {
    status: 200,
    headers: { 'content-type': 'application/json' },
  })));

  pauseTeam.mockReset();
  pauseTeam.mockResolvedValue(new Response(null, { status: 204 }));
  resumeTeam.mockReset();
  resumeTeam.mockResolvedValue(new Response(null, { status: 204 }));
});

afterEach(resetBody);

async function mountBoard(active: Record<string, unknown>) {
  setActivePinia(createPinia());

  const session = useSessionStore();
  session.user = { email: 'admin@example.com' } as never;
  session.checked = true;

  const board = useConsoleStore();
  board.teams = [active] as never;
  board.activeTeamId = active.id as never;
  board.refresh = vi.fn().mockResolvedValue(undefined) as never;
  board.refreshRollupIfShowing = vi.fn() as never;

  const wrapper = mount(IndexPage);
  await flushPromises();

  return { wrapper, board };
}

function pauseButton(wrapper: ReturnType<typeof mount>) {
  return wrapper
    .findAll('button')
    .find((b) => b.text().includes('Pause') || b.text().includes('Resume'));
}

describe('the active team heading', () => {
  it('offers Pause for a team that is running', async () => {
    const { wrapper } = await mountBoard(team({ containers: [container('Running')] }));

    const button = pauseButton(wrapper);

    expect(button).toBeDefined();
    expect(button!.text()).toContain('Pause');

    await button!.trigger('click');
    await flushPromises();

    expect(pauseTeam).toHaveBeenCalledWith('alpha');

    wrapper.unmount();
  });

  it('offers Resume for a team that is already paused', async () => {
    const { wrapper } = await mountBoard(
      team({ paused: true, containers: [container('Idle')] }),
    );

    const button = pauseButton(wrapper);

    expect(button!.text()).toContain('Resume');

    await button!.trigger('click');
    await flushPromises();

    expect(resumeTeam).toHaveBeenCalledWith('alpha');

    wrapper.unmount();
  });

  /**
   * BOTH REFRESHES, and the second is the one that is easy to forget. `refresh()` reloads the
   * overview this heading reads `paused` from; the rollup behind the Teams tab is a SEPARATE fetch
   * that would otherwise sit on a stale Status column - two screens disagreeing about whether the
   * team is paused.
   */
  it('refreshes both the overview and the rollup', async () => {
    const { wrapper, board } = await mountBoard(team({ containers: [container('Running')] }));

    await pauseButton(wrapper)!.trigger('click');
    await flushPromises();

    expect(board.refresh).toHaveBeenCalled();
    expect(board.refreshRollupIfShowing).toHaveBeenCalled();

    wrapper.unmount();
  });

  /**
   * THE CONTROL IS NOT GATED ON THE TEAM'S STATUS, AND THAT IS A DECISION.
   *
   * *"Does it make sense to have an enabled pause button for a team that is blocked or
   * completed?"* - a reasonable instinct, and the answer is that it does, because
   * PAUSE IS A STATEMENT ABOUT FUTURE WORK rather than about what is happening now. The pump stops
   * offering this team anything and its containers stop taking their next batch; RUNNING, IDLE,
   * BLOCKED and COMPLETED all have a future.
   *
   * IDLE IS THE MOST USEFUL CASE OF THE LOT, which is the part that makes the instinct backwards:
   * nothing has started, so the pause takes effect on the very next thing that would have woken the
   * team - a trigger, a schedule, a Manager being told. A brake you can only reach while moving is
   * one you cannot use to stop something starting.
   *
   * BLOCKED IS NOT A TEAM-WIDE STOP either. It is a mark on ONE container that gave up; other
   * members may still be working and new work still delivers. Disabling the brake there would take
   * it away at exactly the moment somebody is clearing up - which is the recoverable-direction rule
   * this codebase applies to every other guard.
   *
   * So `paused` is the only thing that decides, and this theory is what stops somebody adding a
   * status gate later and quietly removing the useful cases.
   */
  it.each([
    ['running', [container('Running')]],
    ['idle', [container('Idle')]],
    ['blocked', [container('Idle', { blocked: 'gave up' })]],
    ['empty, with no members at all', []],
  ])('is enabled for a team that is %s', async (_label, containers) => {
    const { wrapper } = await mountBoard(team({ containers }));

    const button = pauseButton(wrapper);

    expect(button).toBeDefined();
    expect(button!.attributes('disabled')).toBeUndefined();

    wrapper.unmount();
  });
});
