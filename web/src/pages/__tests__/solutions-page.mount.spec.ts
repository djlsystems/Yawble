// @vitest-environment happy-dom
//
// THE SOLUTIONS SCREENS AS ADDRESSES, through a real router with the app's route shape:
// `#/solutions` opens the launcher over the Console, a tile's Manage goes to `#/solutions/<team>` and
// opens that solution's panel, the panel's back arrow returns to the launcher, and closing leaves for
// the plain Console.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { mount } from '@vue/test-utils';
import { createPinia, setActivePinia } from 'pinia';
import { createMemoryHistory, createRouter, type Router } from 'vue-router';

import SolutionsPage from '../SolutionsPage.vue';
import { bodyFind, resetBody } from '../../test/mountQuasar';
import { settle } from '../../test/formProbe';
import { fakeHost, reply, type Call } from '../../test/solutionFixtures';
import { launcherRow, panelRead, panelRoutes } from '../../test/solutionPanelFixtures';

let calls: Call[] = [];

beforeEach(() => {
  calls = [];
  vi.stubGlobal(
    'fetch',
    vi.fn(
      fakeHost(
        [
          (call) => (call.url === '/api/solutions/installed' ? reply(200, [launcherRow()]) : undefined),
          (call) =>
            call.url === '/api/teams/news/solution/panel'
              ? reply(200, panelRead({ team: 'news', teamName: 'News desk', name: 'News Desk', members: [], settings: [] }))
              : undefined,
          ...panelRoutes(),
        ],
        calls,
      ),
    ),
  );
});

afterEach(() => {
  vi.unstubAllGlobals();
  resetBody();
});

async function app(start: string): Promise<Router> {
  setActivePinia(createPinia());
  const router = createRouter({
    history: createMemoryHistory(),
    routes: [
      { path: '/console', component: { template: '<div class="console-shell" />' } },
      {
        path: '/solutions',
        component: { template: '<div class="console-shell"><router-view /></div>' },
        children: [
          { path: '', component: SolutionsPage },
          { path: ':team', component: SolutionsPage },
        ],
      },
    ],
  });
  mount({ template: '<router-view />' }, { attachTo: document.body, global: { plugins: [router], stubs: { IndexPage: true } } });
  await router.push(start);
  await router.isReady();
  await settle();
  await settle();
  return router;
}

describe('the solutions addresses', () => {
  it('opens the launcher at #/solutions, and Manage goes to the panel', async () => {
    const router = await app('/solutions');

    expect(bodyFind('[data-solutions-launcher]')).not.toBeNull();
    bodyFind('[data-solution-tile="job-tracker"] [data-tile-manage]')!.click();
    await settle();
    await settle();

    expect(router.currentRoute.value.fullPath).toBe('/solutions/job-tracker');
    expect(bodyFind('[data-solution-panel]')).not.toBeNull();
  });

  it("opens a solution's panel at #/solutions/<team>, and back goes to the launcher", async () => {
    const router = await app('/solutions/job-tracker');

    expect(bodyFind('[data-panel-title]')?.textContent).toContain('Job Tracker');
    bodyFind('[data-panel-back]')!.click();
    await settle();
    await settle();

    expect(router.currentRoute.value.fullPath).toBe('/solutions');
  });

  it("changing the address from one panel to another opens the other solution's panel", async () => {
    const router = await app('/solutions/job-tracker');
    expect(bodyFind('[data-panel-title]')?.textContent).toContain('Job Tracker');

    // A pasted link, or "Manage solution" followed in the same tab.
    await router.push('/solutions/news');
    await settle();
    await settle();

    expect(router.currentRoute.value.fullPath).toBe('/solutions/news');
    expect(bodyFind('[data-solution-panel]')).not.toBeNull();
    expect(bodyFind('[data-panel-title]')?.textContent).toContain('News Desk');
    expect(calls.some((call) => call.url === '/api/teams/news/solution/panel')).toBe(true);
  });

  it('leaves for the Console when the launcher is closed', async () => {
    const router = await app('/solutions');

    (bodyFind('[data-solutions-launcher] [aria-label="Close"]') as HTMLElement).click();
    await settle();
    await settle();

    expect(router.currentRoute.value.fullPath).toBe('/console');
  });
});
