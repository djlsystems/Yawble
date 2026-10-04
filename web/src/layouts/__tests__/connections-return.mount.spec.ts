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
import MainLayout from '../MainLayout.vue';
import ConnectionsDialog from '../../components/ConnectionsDialog.vue';
import { landProviderReturn } from '../../lib/connections';
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
async function returnFromProvider(search: string) {
  window.history.replaceState(null, '', `/console${search}`);

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
  vi.stubGlobal('fetch', vi.fn(() => new Promise(() => {})));
});

afterEach(() => {
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

  it('tells a tab waiting on a sign-in started from a slot what came back, and closes itself', async () => {
    // A slot's Add connection, in the tab that opened the provider, is waiting for this return.
    localStorage.setItem('connections.waiting', '1');
    const heard: unknown[] = [];
    const listener = new BroadcastChannel('connections.return');
    listener.onmessage = (event) => heard.push(event.data);
    const close = vi.spyOn(window, 'close').mockImplementation(() => {});

    const { wrapper } = await returnFromProvider('?connection=connected&id=conn-1');
    await new Promise((resolve) => setTimeout(resolve, 20));

    expect(heard).toEqual([{ outcome: 'connected', id: 'conn-1' }]);
    expect(close).toHaveBeenCalled();
    expect(localStorage.getItem('connections.waiting')).toBeNull();

    listener.close();
    close.mockRestore();
    wrapper.unmount();
  });

  it('shows a return nobody is waiting for where it lands, as before', async () => {
    localStorage.removeItem('connections.waiting');
    const close = vi.spyOn(window, 'close').mockImplementation(() => {});

    const { wrapper } = await returnFromProvider('?connection=connected&id=conn-1');

    expect(wrapper.findComponent(ConnectionsDialog).props('modelValue')).toBe(true);
    expect(close).not.toHaveBeenCalled();

    close.mockRestore();
    wrapper.unmount();
  });
});
