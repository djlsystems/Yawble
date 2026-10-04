// @vitest-environment happy-dom
//
// BACK FROM A PROVIDER'S CONSENT PAGE, THE WAY THE BROWSER REALLY COMES BACK. The Host's callback
// redirects to `/console?connection=…`: a SEARCH, which the hash router never reads. This spec sets
// the address the redirect leaves, builds a real hash router over it, and mounts the real shell; it
// does not hand the dialog a notice as a prop. Without the rewrite the router resolves `/` and the
// Connections dialog never opens.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { flushPromises, mount } from '@vue/test-utils';
import { createPinia, setActivePinia } from 'pinia';
import { createRouter, createWebHashHistory } from 'vue-router';
const { goTo } = vi.hoisted(() => ({ goTo: vi.fn() }));
vi.mock('../../lib/browserNavigation', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  goTo,
}));

import MainLayout from '../MainLayout.vue';
import ConnectionsDialog from '../../components/ConnectionsDialog.vue';
import { landProviderReturn } from '../../lib/connections';
import { awaitProviderReturn } from '../../lib/providerReturn';
import { useConsoleStore } from '../../stores/console';
import { resetBody } from '../../test/mountQuasar';

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
  ConciergePanel: true,
  ConnectionsDialog: true,
};

/** What the Host's callback leaves in the address bar, then the app's boot: rewrite, router, shell. */
async function returnFromProvider(search: string, address = `/console${search}`) {
  window.history.replaceState(null, '', address);

  landProviderReturn();

  setActivePinia(createPinia());
  useConsoleStore().$patch({ teams: [], activeTeamId: '', overviewLanded: true });

  const router = createRouter({
    history: createWebHashHistory(),
    routes: [
      { path: '/', component: { template: '<div class="front-page" />' } },
      { path: '/console', component: MainLayout },
    ],
  });

  const wrapper = mount({ template: '<router-view />' }, { attachTo: document.body, global: { plugins: [router], stubs } });
  await router.isReady();
  await flushPromises();

  return { wrapper, router };
}

beforeEach(() => {
  goTo.mockReset();
  vi.stubGlobal('fetch', vi.fn(() => new Promise(() => {})));
});

afterEach(() => {
  sessionStorage.clear();
  vi.unstubAllGlobals();
  resetBody();
  window.history.replaceState(null, '', '/');
});

describe("the provider's redirect back to /console?connection=…", () => {
  it('opens the Connections dialog saying it connected, and takes the query off the address', async () => {
    const { wrapper, router } = await returnFromProvider('?connection=connected&id=conn-1');

    expect(wrapper.find('.front-page').exists(), 'landed on the front page').toBe(false);
    const dialog = wrapper.findComponent(ConnectionsDialog);
    expect(dialog.props('modelValue')).toBe(true);
    expect(dialog.props('notice')).toEqual({ outcome: 'connected', id: 'conn-1' });

    expect(window.location.search).toBe('');
    expect(router.currentRoute.value.path).toBe('/console');
    expect(router.currentRoute.value.query).toEqual({});

    wrapper.unmount();
  });

  it("opens the Connections dialog with the Host's reason when it was refused", async () => {
    const { wrapper } = await returnFromProvider('?connection=refused&reason=The+provider+sent+no+refresh+token.');

    const dialog = wrapper.findComponent(ConnectionsDialog);
    expect(dialog.props('modelValue')).toBe(true);
    expect(dialog.props('notice')).toEqual({ outcome: 'refused', reason: 'The provider sent no refresh token.' });
    expect(window.location.search).toBe('');

    wrapper.unmount();
  });

  it('tells the tab waiting on its own sign-in what came back, and closes itself once it is taken', async () => {
    // This tab was opened from a slot's Add connection, for the sign-in tagged 'tag-1'.
    sessionStorage.setItem('connections.tab', 'tag-1');
    const heard: unknown[] = [];
    const stop = awaitProviderReturn('tag-1', 'https://accounts.google.com/o/oauth2/v2/auth?state=r2', (outcome) => heard.push(outcome));
    const close = vi.spyOn(window, 'close').mockImplementation(() => {});

    const { wrapper } = await returnFromProvider('?connection=connected&id=conn-1');
    await new Promise((resolve) => setTimeout(resolve, 50));

    expect(heard).toEqual([{ outcome: 'connected', id: 'conn-1' }]);
    expect(close).toHaveBeenCalled();
    expect(sessionStorage.getItem('connections.tab')).toBeNull();

    stop();
    close.mockRestore();
    wrapper.unmount();
  });

  it('stays open with its notice when no tab takes its sign-in any more', async () => {
    sessionStorage.setItem('connections.tab', 'tag-gone');
    const close = vi.spyOn(window, 'close').mockImplementation(() => {});

    const { wrapper } = await returnFromProvider('?connection=connected&id=conn-1');
    await new Promise((resolve) => setTimeout(resolve, 1100));

    expect(wrapper.findComponent(ConnectionsDialog).props('modelValue')).toBe(true);
    expect(close).not.toHaveBeenCalled();

    close.mockRestore();
    wrapper.unmount();
  });

  it('shows a return nobody is waiting for where it lands, as before', async () => {
    sessionStorage.clear();
    const close = vi.spyOn(window, 'close').mockImplementation(() => {});

    const { wrapper } = await returnFromProvider('?connection=connected&id=conn-1');

    expect(wrapper.findComponent(ConnectionsDialog).props('modelValue')).toBe(true);
    expect(close).not.toHaveBeenCalled();

    close.mockRestore();
    wrapper.unmount();
  });

  it("goes on to the provider the waiting tab names, when opened from a slot's sign-in link", async () => {
    const stop = awaitProviderReturn('tag-2', 'https://accounts.google.com/o/oauth2/v2/auth?state=r2', () => {});

    const { wrapper } = await returnFromProvider('', '/#/console?signin=tag-2');
    await new Promise((resolve) => setTimeout(resolve, 50));

    expect(goTo).toHaveBeenCalledWith('https://accounts.google.com/o/oauth2/v2/auth?state=r2');
    // Kept for the way back, in this tab only.
    expect(sessionStorage.getItem('connections.tab')).toBe('tag-2');

    stop();
    wrapper.unmount();
  });
});
