// @vitest-environment happy-dom
//
// THE INSTALL DEEP LINK, `#/solutions/install?folder=<absolute path>`, through a real router with the
// app's own routes' shape and its real sign-in guard. It opens the wizard filled in with the folder
// and checks it with `from: "link"`; it NEVER installs by itself; a folder the Host refuses for a
// link (outside the instance's documents and team folders) shows the Host's sentence and nothing
// else happens; and a signed-out visitor is sent to sign in and then lands in the wizard.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { flushPromises, mount } from '@vue/test-utils';
import { createPinia, setActivePinia } from 'pinia';
import { createMemoryHistory, createRouter, type Router } from 'vue-router';

import LandingPage from '../LandingPage.vue';
import SolutionInstallPage from '../SolutionInstallPage.vue';
import { returnPath, sessionGuard } from '../../router/guard';
import { installHref } from '../../lib/solutionNotice';
import { bodyFind, bodyText, resetBody } from '../../test/mountQuasar';
import { button, settle, type } from '../../test/formProbe';
import { Folder, fakeHost, reply, sent, wizardRoutes, type Call, type Route } from '../../test/solutionFixtures';

const Link = `/solutions/install?folder=${encodeURIComponent(Folder)}`;
const Outside = 'That folder is outside this instance\'s documents and team folders, so a link cannot install from it.';

let calls: Call[] = [];
let signedIn = false;

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

function serve(extra: Route[] = []) {
  calls = [];
  vi.stubGlobal('fetch', vi.fn(fakeHost([...auth, ...extra, ...wizardRoutes()], calls)));
}

/** The app's route shape: the public door, and the deep link inside the (stubbed) Console shell. */
async function app(start: string): Promise<Router> {
  setActivePinia(createPinia());

  const router = createRouter({
    history: createMemoryHistory(),
    routes: [
      { path: '/', component: LandingPage, meta: { publicRoute: true } },
      { path: '/console', component: { template: '<div class="console-shell" />' } },
      {
        path: '/solutions/install',
        component: { template: '<div class="console-shell"><router-view /></div>' },
        children: [{ path: '', component: SolutionInstallPage }],
      },
    ],
  });
  router.beforeEach(sessionGuard);

  mount({ template: '<router-view />' }, {
    attachTo: document.body,
    global: { plugins: [router], stubs: { IndexPage: true, VersionTag: true } },
  });

  await router.push(start);
  await router.isReady();
  await settle();
  await settle();

  return router;
}

beforeEach(() => {
  signedIn = true;
  serve();
});

afterEach(() => {
  vi.unstubAllGlobals();
  resetBody();
});

describe('the install deep link', () => {
  it('opens the wizard filled in with the folder, checked with from: "link"', async () => {
    await app(Link);

    expect(bodyFind('[data-solution-wizard]')).not.toBeNull();
    expect(bodyFind('[data-folder]')!.textContent).toBe(Folder);
    expect(sent(calls, 'POST', '/api/solutions/check')[0]!.body).toEqual({ folder: Folder, from: 'link' });
    expect(bodyFind('[data-step="team"]')).not.toBeNull();
  });

  it('never installs by itself: only the Install button, after the review, sends', async () => {
    await app(Link);
    await new Promise((resolve) => setTimeout(resolve, 450));
    await settle();

    expect(sent(calls, 'POST', '/api/solutions/install')).toHaveLength(0);
    expect(sent(calls, 'POST', '/api/solutions/update')).toHaveLength(0);
    expect(bodyText()).not.toContain('Installed');
  });

  it("shows the Host's refusal for a folder outside the documents and team folders, and does nothing else", async () => {
    serve([(call) => (call.url === '/api/solutions/check' ? reply(400, { error: Outside }) : undefined)]);

    await app(`/solutions/install?folder=${encodeURIComponent('/etc/elsewhere')}`);

    expect(bodyFind('[data-folder-refusal]')!.textContent).toContain(Outside);
    expect(sent(calls, 'POST', '/api/solutions/check')[0]!.body).toEqual({ folder: '/etc/elsewhere', from: 'link' });
    expect(sent(calls, 'POST', '/api/solutions/preview')).toHaveLength(0);
    expect(sent(calls, 'GET', '/api/solutions/installed')).toHaveLength(0);
    expect(sent(calls, 'POST', '/api/solutions/install')).toHaveLength(0);
    expect(() => button('Next')).toThrow();
  });

  it('survives sign-in: a signed-out visit goes to the door, and signing in lands in the wizard', async () => {
    signedIn = false;
    const router = await app(Link);

    expect(router.currentRoute.value.path).toBe('/');
    expect(router.currentRoute.value.query.next).toBe(Link);
    expect(bodyFind('[data-solution-wizard]')).toBeNull();
    expect(sent(calls, 'POST', '/api/solutions/check')).toHaveLength(0);

    await type('Email', 'dana@example.com');
    await type('Password', 'correct horse');
    button('Log in').click();
    await settle();
    await settle();

    expect(router.currentRoute.value.path).toBe('/solutions/install');
    expect(router.currentRoute.value.query.folder).toBe(Folder);
    expect(bodyFind('[data-solution-wizard]')).not.toBeNull();
    expect(sent(calls, 'POST', '/api/solutions/check')[0]!.body).toEqual({ folder: Folder, from: 'link' });
    expect(sent(calls, 'POST', '/api/solutions/install')).toHaveLength(0);
  });

  it("accepts the board notice's own link, escaping and all, through sign-in", async () => {
    // What the board notice builds (solutionNotice.ts), for a folder with every character its
    // escaping touches: spaces, &, #, ?, +, %, = and a non-ASCII letter.
    const awkward = '/data/documents/team one/Job Tracker & co #2?v=1+2 100% é-1.0.0';
    const href = installHref(awkward);
    expect(href.startsWith('#/solutions/install?folder=')).toBe(true);

    signedIn = false;
    const router = await app(href.slice(1));
    expect(router.currentRoute.value.path).toBe('/');

    await type('Email', 'dana@example.com');
    await type('Password', 'correct horse');
    button('Log in').click();
    await settle();
    await settle();

    expect(router.currentRoute.value.path).toBe('/solutions/install');
    expect(router.currentRoute.value.query.folder).toBe(awkward);
    expect(bodyFind('[data-folder]')!.textContent).toBe(awkward);
    expect(sent(calls, 'POST', '/api/solutions/check')[0]!.body).toEqual({ folder: awkward, from: 'link' });
    expect(sent(calls, 'POST', '/api/solutions/install')).toHaveLength(0);
  });

  it('a second link in the same tab, while the wizard is open, reopens it on the new folder', async () => {
    const router = await app(`/solutions/install?folder=${encodeURIComponent('/etc')}`);
    expect(bodyFind('[data-folder]')!.textContent).toBe('/etc');

    await router.push(Link);
    await settle();
    await settle();
    await flushPromises();

    expect(router.currentRoute.value.path).toBe('/solutions/install');
    expect(router.currentRoute.value.query.folder).toBe(Folder);
    expect(bodyFind('[data-solution-wizard]')).not.toBeNull();
    expect(bodyFind('[data-folder]')!.textContent).toBe(Folder);
    expect(sent(calls, 'POST', '/api/solutions/check').map((call) => call.body)).toEqual([
      { folder: '/etc', from: 'link' },
      { folder: Folder, from: 'link' },
    ]);
    expect(bodyFind('[data-step="team"]')).not.toBeNull();
    expect(sent(calls, 'POST', '/api/solutions/install')).toHaveLength(0);
  });

  it('closing the wizard leaves the link for the Console', async () => {
    const router = await app(Link);

    button('Cancel').click();
    await settle();
    await flushPromises();

    expect(router.currentRoute.value.path).toBe('/console');
  });
});

describe('where signing in continues to', () => {
  it('is the remembered in-app route, and never another origin or the door', () => {
    expect(returnPath({ next: Link })).toBe(Link);
    expect(returnPath({})).toBe('/console');
    expect(returnPath({ next: '//evil.example/x' })).toBe('/console');
    expect(returnPath({ next: 'https://evil.example/' })).toBe('/console');
    expect(returnPath({ next: '/login' })).toBe('/console');
    expect(returnPath({ next: '/' })).toBe('/console');
  });
});
