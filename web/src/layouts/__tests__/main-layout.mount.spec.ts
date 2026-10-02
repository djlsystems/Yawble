// @vitest-environment happy-dom
//
// THE ACCEPTANCE TEST FOR THE WHOLE DOM STACK: a mounted dialog test that fails on the
// mount-at-open defect. A DOM stack that cannot catch that defect has not been verified.
//
// THE DEFECT, which typecheck and the unit suite cannot see: ConciergePanel attaches its terminal
// from `watch(open)` with no `immediate` - on purpose, because "a console is a child process, and
// one should not be spawned for a team nobody has looked at". If MainLayout rendered the panel
// under a `v-if` flag set in the SAME TICK as `conciergeOpen`, the component's first render would
// already have `modelValue === true`. There would be no false->true edge for the watcher to see,
// `attach()` would never run, and the panel would paint its chrome over a dead terminal.
//
// WHY THE ASSERTION IS THE TERMINAL AND NOT THE PANEL: the chrome renders under the defect. A test
// that asserts "the panel is visible" PASSES while the product is broken.
//
// WHY IT ASSERTS ON THE FIRST OPEN: closing and reopening produces a real edge and attaches
// normally, so a test that opens twice is green against the defect.
//
// WHY THE REAL MainLayout IS MOUNTED, rather than a small parent reproducing its v-if: the defect
// would live in MainLayout's own template, and a hand-built parent would only ever test the fixture.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

// `vi.mock` IS HOISTED ABOVE EVERY CONST IN THIS FILE, so a plain `const fn = vi.fn()` above it is
// still in its temporal dead zone when the factory runs - "Cannot access 'connectConcierge' before
// initialization", reported as a MOCKING error rather than an ordering one. `vi.hoisted` is the
// declaration that rises with it.
const { concierge, listCatalog } = vi.hoisted(() => ({
  concierge: vi.fn(),
  listCatalog: vi.fn(),
}));

// THE API CALLS ATTACH MAKES MUST SETTLE, and that is not a tidiness point. `attach()` does
// `await readConcierge()` between building the terminal and opening it, wrapped in a try/catch that
// swallows failure. Left unmocked in this environment the promise never settles inside the test, so
// `terminal.open` has not run when the assertion reads the DOM - and the spec fails with the exact
// message it would print for the real defect. It then throws AFTER `unmount`, as an unhandled
// rejection, because `host` is null by then. A false red that names the true defect is the worst
// possible failure mode for an acceptance test.
vi.mock('../../api/client', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  concierge,
  listCatalog,
}));

const { connectConcierge } = vi.hoisted(() => ({
  connectConcierge: vi.fn(() => ({
    send: vi.fn(),
    resize: vi.fn(),
    dispose: vi.fn(),
  })),
}));

vi.mock('../../lib/concierge-socket', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  connectConcierge,
}));

import { flushPromises, mount } from '@vue/test-utils';
import { createPinia, setActivePinia } from 'pinia';
import { createMemoryHistory, createRouter } from 'vue-router';
import MainLayout from '../MainLayout.vue';
import { useConsoleStore } from '../../stores/console';
import { useKanbanStore } from '../../stores/kanban';
import { useTerminalDisplayStore } from '../../stores/terminalDisplay';
import ConciergePanel from '../../components/ConciergePanel.vue';
import RibbonBar from '../../components/RibbonBar.vue';
import SitesDialog from '../../components/SitesDialog.vue';
import { Ribbon, SitesAction, TeamSitesAction, needsActiveWorkTeam } from '../../lib/ribbon';
import type { Team, TeamId } from '../../api/types';
import { resetBody } from '../../test/mountQuasar';

/**
 * happy-dom HAS NO LAYOUT ENGINE, so every element reports `clientWidth === 0` - and `attach()`
 * returns early on exactly that, parking a ResizeObserver instead of building a terminal. Without
 * this the assertion below would be red whether or not the defect is present, which is the one
 * thing an acceptance test may not be. The dimensions are the ENVIRONMENT's missing half, not a
 * stub of the subject: nothing about the component under test is replaced.
 */
function giveTheDomALayout(): void {
  for (const property of ['clientWidth', 'offsetWidth'] as const) {
    Object.defineProperty(window.HTMLElement.prototype, property, { value: 800, configurable: true });
  }

  for (const property of ['clientHeight', 'offsetHeight'] as const) {
    Object.defineProperty(window.HTMLElement.prototype, property, { value: 600, configurable: true });
  }
}

const team: Team = {
  id: 'alpha' as TeamId,
  name: 'Alpha',
  concierge: 'claude-interactive',
  memberAgents: null,
  additionalInstructions: null,
  root: null,
  containers: [],
};

function router() {
  return createRouter({
    history: createMemoryHistory(),
    routes: [{ path: '/:pathMatch(.*)*', component: { template: '<div />' } }],
  });
}

/** Everything that is not the Concierge panel, so this spec is about one thing. */
const stubs = {
  ProfileDialog: true,
  BoardDisplayDialog: true,
  CreateTeamDialog: true,
  TeamSettingsDialog: true,
  TeamGitDialog: true,
  DocumentsDialog: true,
  UsersDialog: true,
  AgentsDialog: true,
  ResetTeamDialog: true,
  TenantLogDialog: true,
  ApiKeysDialog: true,
  SkillsDialog: true,
  SitesDialog: true,
};

const beta: Team = { ...team, id: 'beta' as TeamId, name: 'Beta' };

async function mountShell(teams: Team[], activeTeamId: TeamId | '') {
  setActivePinia(createPinia());

  const board = useConsoleStore();
  board.$patch({ teams, activeTeamId, overviewLanded: true });

  // ATTACHED TO THE DOCUMENT, which vue-test-utils does NOT do by default. xterm refuses to open
  // onto an orphan - "Terminal requires a parent element" - so a detached mount fails here for a
  // reason that has nothing to do with the defect under test.
  const wrapper = mount(MainLayout, {
    attachTo: document.body,
    global: { plugins: [router()], stubs },
  });

  await flushPromises();

  return wrapper;
}

async function mountShellWithActiveTeam() {
  const wrapper = await mountShell([team], team.id);

  // If this ever stops holding, every assertion below is vacuous: openConcierge() returns early
  // without an active work team and nothing opens at all.
  expect(useConsoleStore().activeWorkTeam, 'the fixture did not produce an active work team').not.toBeNull();

  return wrapper;
}

async function clickTheConciergeFab(wrapper: Awaited<ReturnType<typeof mountShell>>) {
  const fab = wrapper.find('.concierge-fab');

  expect(fab.exists(), 'no .concierge-fab in the mounted shell').toBe(true);

  await fab.trigger('click');
  await flushPromises();
  await flushPromises();
}

beforeEach(() => {
  connectConcierge.mockClear();
  concierge.mockReset();
  concierge.mockResolvedValue({ agent: 'claude-interactive', prompt: null, promptName: null });
  listCatalog.mockReset();
  listCatalog.mockResolvedValue([]);
  giveTheDomALayout();

  // Anything the shell reads that this file has not mocked stays PENDING rather than reaching for
  // a server on localhost, which is what it did before and what filled the run with ECONNREFUSED.
  vi.stubGlobal('fetch', vi.fn(() => new Promise(() => {})));
});

afterEach(() => {
  vi.unstubAllGlobals();
  resetBody();
  document.body.style.overflow = '';
});

describe('the Concierge panel, mounted in the real shell', () => {
  it('attaches a terminal on the FIRST open', async () => {
    const wrapper = await mountShellWithActiveTeam();

    await clickTheConciergeFab(wrapper);

    // attach() builds `new Terminal(...)` and calls .open(host), which is what puts an .xterm
    // element in the DOM. Under the defect this element is absent and everything else is present.
    expect(
      document.querySelector('.xterm'),
      'no .xterm after the first open: attach() never ran, which is the mount-at-open defect',
    ).not.toBeNull();

    wrapper.unmount();
  });

  it('opens exactly one socket on the FIRST open', async () => {
    const wrapper = await mountShellWithActiveTeam();

    await clickTheConciergeFab(wrapper);

    expect(connectConcierge).toHaveBeenCalledTimes(1);

    wrapper.unmount();
  });
});

describe('the Concierge bubble is a toggle', () => {
  const dock = () => document.body.querySelector('.concierge-fab-dock') as HTMLElement | null;

  it('opens the panel on the first press and minimizes it on the second', async () => {
    const wrapper = await mountShellWithActiveTeam();
    const panel = wrapper.findComponent(ConciergePanel);

    await clickTheConciergeFab(wrapper);
    expect(panel.props('modelValue')).toBe(true);
    expect(wrapper.find('.concierge-fab').attributes('aria-label')).toBe('Minimize the Concierge');

    await clickTheConciergeFab(wrapper);
    expect(panel.props('modelValue')).toBe(false);
    expect(wrapper.find('.concierge-fab').attributes('aria-label')).toBe('Open the Concierge');

    wrapper.unmount();
  });

  it('stays visible beside a windowed panel, and is hidden while the panel is maximized', async () => {
    const wrapper = await mountShellWithActiveTeam();

    await clickTheConciergeFab(wrapper);
    expect(dock()?.style.display).not.toBe('none');

    useTerminalDisplayStore().maximised = true;
    await flushPromises();
    expect(dock()?.style.display).toBe('none');

    // Minimized from the panel's own control: the bubble comes back to open it again.
    await wrapper.findComponent(ConciergePanel).vm.$emit('update:modelValue', false);
    await flushPromises();
    expect(dock()?.style.display).not.toBe('none');

    wrapper.unmount();
  });
});

describe('the Concierge entry point, with and without a team', () => {
  /** The Concierge serves the instance, so the bubble exists before there is a team to open. */
  it.each([
    ['no teams at all', [] as Team[], '' as const],
    ['no active team', [team], '' as const],
  ])('is offered with %s, and opens the per-person socket', async (_, teams, active) => {
    const wrapper = await mountShell(teams, active);

    await clickTheConciergeFab(wrapper);

    expect(connectConcierge).toHaveBeenCalledTimes(1);
    // The socket is the person's and names no team: its first argument is the geometry.
    expect((connectConcierge.mock.calls[0] as unknown[])[0]).toEqual(
      expect.objectContaining({ cols: expect.any(Number), rows: expect.any(Number) }),
    );

    wrapper.unmount();
  });

  /**
   * THE SESSION IS PINNED TO ITS LAUNCH TEAM while the board follows the tab: switching tabs with
   * the panel open changes the ACTIVE team the panel is told about, never the team it launched on.
   */
  it('keeps the launch team while passing the new active team', async () => {
    const wrapper = await mountShell([team, beta], team.id);

    await clickTheConciergeFab(wrapper);
    useConsoleStore().$patch({ activeTeamId: beta.id });
    await flushPromises();

    const panel = wrapper.findComponent(ConciergePanel);
    expect(panel.props('team')).toBe('alpha');
    expect(panel.props('teamName')).toBe('Alpha');
    expect(panel.props('activeTeam')).toBe('beta');
    expect(panel.props('activeTeamName')).toBe('Beta');

    wrapper.unmount();
  });
});

describe('MainLayout ribbon actions', () => {
  async function ribbonAction(action: string) {
    const wrapper = await mountShell([team], team.id);
    const board = useConsoleStore();
    const kanban = useKanbanStore();
    const pullRepoStatus = vi.spyOn(board, 'pullRepoStatus').mockResolvedValue();
    const openForTeam = vi.spyOn(kanban, 'openForTeam').mockImplementation(() => {});

    wrapper.findComponent(RibbonBar).vm.$emit('action', action);
    await flushPromises();

    return { wrapper, pullRepoStatus, openForTeam };
  }

  // A dialog opened from the ribbon painted behind an open panel (7000 over a dialog's 6000) and,
  // being modal, took the keyboard from the terminal. Choosing a command minimizes the panel.
  it('minimizes an open Concierge when a command is chosen, and still opens what was chosen', async () => {
    const wrapper = await mountShellWithActiveTeam();
    const pullRepoStatus = vi.spyOn(useConsoleStore(), 'pullRepoStatus').mockResolvedValue();
    const panel = wrapper.findComponent(ConciergePanel);

    await clickTheConciergeFab(wrapper);
    expect(panel.props('modelValue')).toBe(true);

    wrapper.findComponent(RibbonBar).vm.$emit('action', 'team-git');
    await flushPromises();

    expect(panel.props('modelValue')).toBe(false);
    expect(pullRepoStatus).toHaveBeenCalled();
    expect(wrapper.find('.concierge-fab').attributes('aria-label')).toBe('Open the Concierge');

    wrapper.unmount();
  });

  it('re-reads repo status when the git dialog opens', async () => {
    const { wrapper, pullRepoStatus } = await ribbonAction('team-git');

    expect(pullRepoStatus).toHaveBeenCalled();

    wrapper.unmount();
  });

  /**
   * `team-kanban` opens no dialog: it selects the Kanban tab pre-filtered to the team whose ribbon
   * was used.
   */
  it('opens the board filtered to the active team on team-kanban', async () => {
    const { wrapper, openForTeam } = await ribbonAction('team-kanban');

    expect(openForTeam).toHaveBeenCalledWith('alpha');

    wrapper.unmount();
  });

  /** Active Team › Sites is Admin › Sites narrowed to the team whose ribbon was used. */
  it('opens Sites filtered to the active team on team-sites, and unfiltered from Admin', async () => {
    const { wrapper } = await ribbonAction(TeamSitesAction);

    const sites = wrapper.findComponent(SitesDialog);
    expect(sites.props('modelValue')).toBe(true);
    expect(sites.props('team')).toBe('alpha');

    await sites.vm.$emit('update:modelValue', false);
    wrapper.findComponent(RibbonBar).vm.$emit('action', SitesAction);
    await flushPromises();

    expect(sites.props('modelValue')).toBe(true);
    expect(sites.props('team')).toBeNull();

    wrapper.unmount();
  });

  /** `team-` is the prefix that means "needs an active team", and the button inherits it. */
  it('keeps team-kanban a ribbon action that needs an active team', () => {
    const actions = Ribbon.tabs.flatMap((tab) => tab.items.map((item) => item.action));

    expect(actions).toContain('team-kanban');
    expect(needsActiveWorkTeam('team-kanban', undefined)).toBe(true);
  });
});
