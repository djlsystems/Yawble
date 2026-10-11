// @vitest-environment happy-dom
//
// AN IN-APP LINK TO THE INSTALL REVIEW, `#/solutions/install?folder=…`, FOLLOWED FROM THE CONSOLE.
// The feed row's "Review and install", the board notice's and the backlog item notice's are
// all the same plain hash link (`installHref`): the browser changes the hash and the router follows.
//
// THE CONSOLE MUST STAY UP WHILE THE REVIEW OPENS OVER IT. It used to be torn down and built again
// under the wizard, and a Console whose teardown faulted - any error already in its tree, which is
// how it was reproduced in Chromium on v2026.10.01.3 - aborted the switch: the address changed, the
// team view stayed, and no review and no error ever showed. The stand-in team view below faults on
// teardown for exactly that reason; the real one is covered by `index-page.mount.spec.ts`.
//
// Through the app's real routes and real layout, with a hash history as the app has.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

const teamView = vi.hoisted(() => ({ mounted: 0, unmounted: 0, faulty: true }));

vi.mock('@/pages/IndexPage.vue', async () => {
  const { defineComponent, h, onMounted, onBeforeUnmount } = await import('vue');
  const { installHref } = await import('../../lib/solutionNotice');
  const { Folder } = await import('../../test/solutionFixtures');
  return {
    default: defineComponent({
      name: 'IndexPage',
      setup() {
        onMounted(() => teamView.mounted++);
        onBeforeUnmount(() => {
          teamView.unmounted++;
          if (teamView.faulty) throw new Error('A card in the team view failed to tear down.');
        });
        return () =>
          h('div', { 'data-team-view': '' }, [
            h('a', { class: 'feed-install', href: installHref(Folder) }, 'Review and install'),
          ]);
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
import { bodyFind, bodyText, resetBody } from '../../test/mountQuasar';
import { settle } from '../../test/formProbe';
import { Folder, fakeHost, reply, sent, wizardRoutes, type Call, type Route } from '../../test/solutionFixtures';
// Loaded up front so the routes' lazy imports resolve at once: run alone, the first link otherwise
// lands before the review's page has been transformed, and the test fails on timing, not on the app.
import '../../layouts/MainLayout.vue';
import '../SolutionInstallPage.vue';
import '../SolutionsPage.vue';

let calls: Call[] = [];
let router: Router;

function serve(extra: Route[] = []) {
  calls = [];
  vi.stubGlobal('fetch', vi.fn(fakeHost([...extra, ...wizardRoutes()], calls)));
}

async function settled() {
  await settle();
  await settle();
  await flushPromises();
}

/** The Console at `start`, signed in, as a fresh load of that address shows it. */
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

/**
 * What a person's click on a hash link does: the browser moves the hash and fires `popstate`, which
 * is what the router listens to. happy-dom moves the hash on a click but does not always fire it.
 */
async function follow(link: HTMLAnchorElement) {
  let fired = false;
  const seen = () => (fired = true);
  window.addEventListener('popstate', seen, { once: true });
  link.click();
  if (!fired) {
    window.removeEventListener('popstate', seen);
    window.location.hash = new URL(link.href).hash;
    window.dispatchEvent(new PopStateEvent('popstate', { state: null }));
  }
  await new Promise((resolve) => setTimeout(resolve, 0));
  await settled();
}

function anchor(href: string): HTMLAnchorElement {
  const link = document.createElement('a');
  link.href = href;
  document.body.appendChild(link);
  return link;
}

beforeEach(() => {
  teamView.mounted = 0;
  teamView.unmounted = 0;
  teamView.faulty = true;
  serve();
});

afterEach(() => {
  teamView.faulty = false;
  vi.unstubAllGlobals();
  resetBody();
  window.location.hash = '';
});

describe('a Review and install link followed from the Console', () => {
  it("opens the review on the link's folder over the team view, with no reload", async () => {
    await load('#/console');
    expect(bodyFind('[data-team-view]')).not.toBeNull();
    expect(bodyFind('[data-solution-wizard]')).toBeNull();

    await follow(bodyFind('a.feed-install') as HTMLAnchorElement);

    expect(router.currentRoute.value.path).toBe('/solutions/install');
    expect(bodyFind('[data-solution-wizard]')).not.toBeNull();
    expect(bodyFind('[data-folder]')!.textContent).toBe(Folder);
    expect(sent(calls, 'POST', '/api/solutions/check').map((call) => call.body)).toEqual([{ folder: Folder, from: 'link' }]);
    // In place: the same team view, never torn down and built again.
    expect(teamView).toMatchObject({ mounted: 1, unmounted: 0 });
    expect(sent(calls, 'POST', '/api/solutions/install')).toHaveLength(0);
  });

  it("opens the second folder's review when followed while on a solutions route with another folder", async () => {
    const other = '/data/documents/mail-plugin-builder/mail-1.0.0';
    await load(installHref(other));
    expect(bodyFind('[data-folder]')!.textContent).toBe(other);

    await follow(anchor(installHref(Folder)));

    expect(router.currentRoute.value.query.folder).toBe(Folder);
    expect(bodyFind('[data-solution-wizard]')).not.toBeNull();
    expect(bodyFind('[data-folder]')!.textContent).toBe(Folder);
    expect(sent(calls, 'POST', '/api/solutions/check').map((call) => call.body)).toEqual([
      { folder: other, from: 'link' },
      { folder: Folder, from: 'link' },
    ]);
    expect(bodyFind('[data-step="team"]')).not.toBeNull();
  });

  it('opens it from the solutions launcher too, keeping the team view', async () => {
    await load('#/solutions');

    await follow(anchor(installHref(Folder)));

    expect(bodyFind('[data-folder]')!.textContent).toBe(Folder);
    expect(teamView).toMatchObject({ mounted: 1, unmounted: 0 });
  });

  it('says in words, on the review screen, that a folder cannot be read', async () => {
    const missing = 'That folder does not exist: /data/documents/gone-1.0.0.';
    serve([(call) => (call.url === '/api/solutions/check' ? reply(400, { error: missing }) : undefined)]);
    await load('#/console');

    await follow(anchor(installHref('/data/documents/gone-1.0.0')));

    expect(bodyFind('[data-solution-wizard]')).not.toBeNull();
    expect(bodyFind('[data-folder]')!.textContent).toBe('/data/documents/gone-1.0.0');
    expect(bodyFind('[data-folder-refusal]')!.textContent).toContain(missing);
    expect(bodyText()).not.toContain('Checking the package');
    // By folder: the previous case's wizard may still be finishing its own preview through this
    // case's fake Host, and that call is not this folder's.
    expect(sent(calls, 'POST', '/api/solutions/preview')
      .filter((call) => (call.body as { folder?: string } | undefined)?.folder === '/data/documents/gone-1.0.0')).toHaveLength(0);
  });
});
