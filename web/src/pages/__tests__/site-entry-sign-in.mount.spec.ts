// @vitest-environment happy-dom
//
// A signed-out visit to a site's entry (`/sites/<team>/<site>/?url=…`) is sent by the Host to the
// door with the entry in `next`. Signing in continues there with a FULL navigation - the Host
// serves it, not the web app - and an in-app `next` still goes through the router as before.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { mount } from '@vue/test-utils';
import { createPinia, setActivePinia } from 'pinia';
import { createMemoryHistory, createRouter, type Router } from 'vue-router';

import LandingPage from '../LandingPage.vue';
import { page, sessionGuard } from '../../router/guard';
import { resetBody } from '../../test/mountQuasar';
import { button, settle, type } from '../../test/formProbe';
import { fakeHost, reply, type Call, type Route } from '../../test/solutionFixtures';

const Entry = '/sites/b0053-site-address/jobs/?url=https%3A%2F%2Fexample.com%2Fjobs%2F1';

let signedIn = false;
let calls: Call[] = [];

const auth: Route[] = [
  (call) => (call.url === '/api/auth/state' ? reply(200, { needsAdmin: false }) : undefined),
  (call) =>
    call.url === '/api/auth/me'
      ? signedIn
        ? reply(200, { id: 'u1', email: 'dana@example.com' })
        : reply(401, { error: 'Not signed in.' })
      : undefined,
  (call) => {
    if (call.url !== '/api/auth/login') return undefined;
    signedIn = true;
    return reply(200, { id: 'u1', email: 'dana@example.com' });
  },
];

async function door(next: string): Promise<Router> {
  setActivePinia(createPinia());

  const router = createRouter({
    history: createMemoryHistory(),
    routes: [
      { path: '/', component: LandingPage, meta: { publicRoute: true } },
      { path: '/console', component: { template: '<div class="console-shell" />' } },
      { path: '/:catchAll(.*)*', component: { template: '<div class="not-found" />' } },
    ],
  });
  router.beforeEach(sessionGuard);

  mount({ template: '<router-view />' }, {
    attachTo: document.body,
    global: { plugins: [router], stubs: { VersionTag: true } },
  });

  await router.push({ path: '/', query: { next } });
  await router.isReady();
  await settle();
  await settle();

  return router;
}

async function logIn() {
  await type('Email', 'dana@example.com');
  await type('Password', 'correct horse');
  button('Log in').click();
  await settle();
  await settle();
}

beforeEach(() => {
  signedIn = false;
  calls = [];
  vi.stubGlobal('fetch', vi.fn(fakeHost(auth, calls)));
});

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
  resetBody();
});

describe('signing in with a site entry in next', () => {
  it('navigates to the entry, query and all, with a full navigation', async () => {
    const assign = vi.spyOn(page, 'assign').mockImplementation(() => {});
    const router = await door(Entry);
    expect(router.currentRoute.value.query.next).toBe(Entry);
    expect(assign).not.toHaveBeenCalled();

    await logIn();

    expect(assign).toHaveBeenCalledTimes(1);
    expect(assign).toHaveBeenCalledWith(Entry);
  });

  it('keeps an in-app next in the router, as today', async () => {
    const assign = vi.spyOn(page, 'assign').mockImplementation(() => {});
    const router = await door('/console?view=kanban');

    await logIn();

    expect(assign).not.toHaveBeenCalled();
    expect(router.currentRoute.value.fullPath).toBe('/console?view=kanban');
  });

  it('refuses an entry on another origin and goes to the Console in the router', async () => {
    const assign = vi.spyOn(page, 'assign').mockImplementation(() => {});
    const router = await door(`//evil.example${Entry}`);

    await logIn();

    expect(assign).not.toHaveBeenCalled();
    expect(router.currentRoute.value.path).toBe('/console');
  });
});
