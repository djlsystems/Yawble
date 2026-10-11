// @vitest-environment happy-dom
//
// THE CONSOLE ADDRESSES WITH THE OVERLAY VIEW, through the app's real routes and real layout:
// a fresh load of each address opens its screen over the team view, and closing a screen goes back
// to the same team view, never torn down and built again. The stand-in team view faults on teardown,
// as in `solution-link-in-place.mount.spec.ts`, so any rebuild of the Console fails the test.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

const teamView = vi.hoisted(() => ({ mounted: 0, unmounted: 0, faulty: true }));

vi.mock('@/pages/IndexPage.vue', async () => {
  const { defineComponent, h, onMounted, onBeforeUnmount } = await import('vue');
  return {
    default: defineComponent({
      name: 'IndexPage',
      setup() {
        onMounted(() => teamView.mounted++);
        onBeforeUnmount(() => {
          teamView.unmounted++;
          if (teamView.faulty) throw new Error('A card in the team view failed to tear down.');
        });
        return () => h('div', { 'data-team-view': '' });
      },
    }),
  };
});

vi.mock('../../lib/hub', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  connectHub: vi.fn(() => Promise.resolve(null)),
}));

import { flushPromises, mount } from '@vue/test-utils';
import { createPinia, setActivePinia } from 'pinia';
import { createRouter, createWebHashHistory, type Router } from 'vue-router';
import routes from '../../router/routes';
import { sessionGuard } from '../../router/guard';
import { installHref } from '../../lib/solutionNotice';
import { useSessionStore } from '../../stores/session';
import { bodyFind, resetBody } from '../../test/mountQuasar';
import { button, settle } from '../../test/formProbe';
import { Folder, fakeHost, reply, sent, wizardRoutes, type Call } from '../../test/solutionFixtures';
import { launcherRow, panelRoutes } from '../../test/solutionPanelFixtures';
// Loaded up front so the routes' lazy imports resolve at once, whatever ran before this file.
import '../../layouts/MainLayout.vue';
import '../SolutionInstallPage.vue';
import '../SolutionsPage.vue';

let calls: Call[] = [];
let router: Router;

async function settled() {
  await settle();
  await settle();
  await flushPromises();
}

async function load(start: string) {
  setActivePinia(createPinia());
  const session = useSessionStore();
  session.user = { id: 'u1', email: 'dana@example.com' } as never;
  session.checked = true;

  window.location.hash = start;
  router = createRouter({ history: createWebHashHistory(), routes });
  router.beforeEach(sessionGuard);
  mount({ template: '<router-view />' }, {
    attachTo: document.body,
    global: { plugins: [router], stubs: { VersionTag: true } },
  });
  await router.isReady();
  await settled();
}

async function arrivesAt(path: string) {
  await vi.waitFor(() => expect(router.currentRoute.value.fullPath).toBe(path));
  await settled();
}

beforeEach(() => {
  teamView.mounted = 0;
  teamView.unmounted = 0;
  teamView.faulty = true;
  calls = [];
  vi.stubGlobal(
    'fetch',
    vi.fn(
      fakeHost(
        [
          (call) => (call.url === '/api/solutions/installed' ? reply(200, [launcherRow()]) : undefined),
          ...panelRoutes(),
          ...wizardRoutes(),
        ],
        calls,
      ),
    ),
  );
});

afterEach(() => {
  teamView.faulty = false;
  vi.unstubAllGlobals();
  resetBody();
  window.location.hash = '';
});

describe('the Console addresses, each loaded fresh', () => {
  it('#/console is the team view with no screen over it', async () => {
    await load('#/console');

    expect(bodyFind('[data-team-view]')).not.toBeNull();
    expect(bodyFind('[data-solution-wizard]')).toBeNull();
    expect(bodyFind('[data-solutions-launcher]')).toBeNull();
  });

  it('#/board still lands on the team view', async () => {
    await load('#/board');

    expect(router.currentRoute.value.fullPath).toBe('/console');
    expect(bodyFind('[data-team-view]')).not.toBeNull();
  });

  it('#/solutions/install?folder= opens the review over the team view; closing it keeps the team view', async () => {
    await load(installHref(Folder));

    expect(bodyFind('[data-team-view]')).not.toBeNull();
    expect(bodyFind('[data-solution-wizard]')).not.toBeNull();
    expect(bodyFind('[data-folder]')!.textContent).toBe(Folder);
    expect(sent(calls, 'POST', '/api/solutions/check').map((call) => call.body)).toEqual([{ folder: Folder, from: 'link' }]);

    button('Cancel').click();
    await arrivesAt('/console');

    expect(bodyFind('[data-solution-wizard]')).toBeNull();
    expect(bodyFind('[data-team-view]')).not.toBeNull();
    expect(teamView).toMatchObject({ mounted: 1, unmounted: 0 });
    expect(sent(calls, 'POST', '/api/solutions/install')).toHaveLength(0);
  });

  it('#/solutions opens the launcher over the team view; closing it keeps the team view', async () => {
    await load('#/solutions');

    expect(bodyFind('[data-team-view]')).not.toBeNull();
    expect(bodyFind('[data-solutions-launcher]')).not.toBeNull();

    (bodyFind('[data-solutions-launcher] [aria-label="Close"]') as HTMLElement).click();
    await arrivesAt('/console');

    expect(bodyFind('[data-solutions-launcher]')).toBeNull();
    expect(bodyFind('[data-team-view]')).not.toBeNull();
    expect(teamView).toMatchObject({ mounted: 1, unmounted: 0 });
  });

  it("#/solutions/<team> opens that solution's panel; back goes to the launcher over the same team view", async () => {
    await load('#/solutions/job-tracker');

    expect(bodyFind('[data-team-view]')).not.toBeNull();
    expect(bodyFind('[data-panel-title]')?.textContent).toContain('Job Tracker');

    bodyFind('[data-panel-back]')!.click();
    await arrivesAt('/solutions');

    expect(bodyFind('[data-solutions-launcher]')).not.toBeNull();
    expect(teamView).toMatchObject({ mounted: 1, unmounted: 0 });
  });
});
