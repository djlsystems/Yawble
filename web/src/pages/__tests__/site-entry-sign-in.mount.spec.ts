// @vitest-environment happy-dom
//
// A signed-out visit to a site's entry (`/sites/<team>/<site>/?url=…`) is sent by the Host to the
// door with the entry in `next` - and so, since W27510, is a new tab the browser sent without the
// SameSite=Strict cookie. Signing in (or already being signed in) continues to that site with a FULL
// navigation to the capability the app asks for, never to the entry again and never to the board;
// a refusal is said on the door. An in-app `next` still goes through the router as before.
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
const Capability = '/sites/b0053-site-address/jobs/_c/CfDJ8tok/';
let opened: Response | null = null;

let signedIn = false;
let calls: Call[] = [];

const auth: Route[] = [
  (call) => (call.method === 'POST' && call.url === '/api/teams/b0053-site-address/sites/jobs/open'
    ? opened ?? reply(200, { url: Capability })
    : undefined),
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
  opened = null;
  calls = [];
  vi.stubGlobal('fetch', vi.fn(fakeHost(auth, calls)));
});

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
  resetBody();
});

describe('signing in with a site entry in next', () => {
  it('navigates to the site\'s capability, query and all, with a full navigation', async () => {
    const assign = vi.spyOn(page, 'assign').mockImplementation(() => {});
    const leave = vi.spyOn(page, 'replace').mockImplementation(() => {});
    const router = await door(Entry);
    expect(router.currentRoute.value.query.next).toBe(Entry);
    expect(leave).not.toHaveBeenCalled();

    await logIn();

    expect(leave).toHaveBeenCalledTimes(1);
    expect(leave).toHaveBeenCalledWith(`${Capability}?url=https%3A%2F%2Fexample.com%2Fjobs%2F1`);
    expect(assign).not.toHaveBeenCalled();
    expect(router.currentRoute.value.path).toBe('/');
  });

  it('already signed in (a tab sent without the cookie), opens the site, not the board, and draws no form', async () => {
    signedIn = true;
    const leave = vi.spyOn(page, 'replace').mockImplementation(() => {});
    const router = await door(Entry);

    expect(router.currentRoute.value.path).toBe('/');
    expect(document.body.querySelector('[data-door-opening-site]')).not.toBeNull();
    expect(document.body.textContent).not.toContain('Log in');
    expect(leave).toHaveBeenCalledTimes(1);
    expect(leave).toHaveBeenCalledWith(`${Capability}?url=https%3A%2F%2Fexample.com%2Fjobs%2F1`);
  });

  it('already signed in, says the Host\'s refusal on the door and goes to the Console only when asked', async () => {
    signedIn = true;
    opened = reply(404, { error: 'This site is not published.' });
    const leave = vi.spyOn(page, 'replace').mockImplementation(() => {});
    const router = await door(Entry);

    expect(leave).not.toHaveBeenCalled();
    expect(router.currentRoute.value.path).toBe('/');
    expect(document.body.querySelector('[data-door-site-refusal]')?.textContent).toBe('This site is not published.');

    (document.body.querySelector('[data-door-site-console]') as HTMLElement).click();
    await settle();
    expect(router.currentRoute.value.path).toBe('/console');
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
