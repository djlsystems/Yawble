// @vitest-environment happy-dom
//
// CANCEL A SIGN-IN WITH A CODE. Closing a dialog or pressing Back only hides a code; the Host keeps the
// flow until it expires and Add connection offers it again ("the sign-in you started earlier"). Cancel,
// beside the code wherever CodeSignIn shows one, ends it at the Host (`DELETE /api/connections/flows/{id}`):
// the reads stop, the code goes, and the opener goes back a step. The resumed sentence says Cancel ends
// it. A flow that ended meanwhile (409) is read again and ends as it ended.
//
// THE MOCK IS OF `api/client`, in the shapes the Host serves. Every code here is a fake one.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { flushPromises, mount } from '@vue/test-utils';

const {
  listConnections,
  listConnectionProviders,
  getConnectionNeeds,
  startConnection,
  startDeviceConnection,
  getConnectionFlow,
  cancelConnectionFlow,
  listOpenConnectionFlows,
  listMailboxPresets,
  goTo,
} = vi.hoisted(() => ({
  listConnections: vi.fn(),
  listConnectionProviders: vi.fn(),
  getConnectionNeeds: vi.fn(),
  startConnection: vi.fn(),
  startDeviceConnection: vi.fn(),
  getConnectionFlow: vi.fn(),
  cancelConnectionFlow: vi.fn(),
  listOpenConnectionFlows: vi.fn(),
  listMailboxPresets: vi.fn(),
  goTo: vi.fn(),
}));

vi.mock('../../api/client', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  listConnections,
  listConnectionProviders,
  getConnectionNeeds,
  startConnection,
  startDeviceConnection,
  getConnectionFlow,
  cancelConnectionFlow,
  listOpenConnectionFlows,
  listMailboxPresets,
}));

vi.mock('../../lib/browserNavigation', () => ({
  goTo,
  currentOrigin: () => 'http://localhost:8080',
}));

import CodeSignIn from '../CodeSignIn.vue';
import ConnectionsDialog from '../ConnectionsDialog.vue';
import type { ConnectionNeeds } from '../../api/types';
import { bodyFind, mountDialog, resetBody } from '../../test/mountQuasar';
import { hostConnection, hostProvider } from '../../test/pluginFixtures';
import { button, settle } from '../../test/formProbe';
import { letGoSignIns } from '../../lib/letGoSignIns';

const mailSend = 'https://graph.microsoft.com/Mail.Send';
const needs: ConnectionNeeds = {
  provider: 'microsoft',
  needs: [{ plugin: 'mail-helper', slot: 'mail', description: null, scopes: [mailSend] }],
  scopes: [{ scope: mailSend, words: 'Send mail as you', plugins: ['mail-helper'] }],
  apis: [],
};
const microsoft = hostProvider({ id: 'microsoft', configured: true, clientSource: 'default' });
const code = { flowId: 'flow-1', userCode: 'FAKE-CODE', verificationUri: 'https://microsoft.example.test/devicelogin', expiresAt: '2099-10-04T10:15:00Z' };
const work = hostConnection({ id: 'conn-work', provider: 'microsoft', name: 'Work', account: 'worker@example.test' });

beforeEach(() => {
  letGoSignIns.clear();
  for (const mock of [listConnections, listConnectionProviders, getConnectionNeeds, startConnection, startDeviceConnection, getConnectionFlow, cancelConnectionFlow, listOpenConnectionFlows, listMailboxPresets, goTo]) {
    mock.mockReset();
  }
  sessionStorage.clear();
  listConnections.mockResolvedValue([]);
  listConnectionProviders.mockResolvedValue([hostProvider({ id: 'google' }), microsoft]);
  getConnectionNeeds.mockResolvedValue(needs);
  startDeviceConnection.mockResolvedValue(code);
  getConnectionFlow.mockResolvedValue({ state: 'waiting', sentence: 'Waiting for you to sign in.' });
  cancelConnectionFlow.mockResolvedValue(undefined);
  listOpenConnectionFlows.mockResolvedValue([]);
  listMailboxPresets.mockResolvedValue([]);
});

afterEach(() => {
  vi.useRealTimers();
  resetBody();
});

async function click(element: Element | null) {
  if (!element) throw new Error('nothing to click');
  (element as HTMLElement).click();
  await settle();
}

describe('CodeSignIn, wherever a code is shown', () => {
  it('shows Cancel beside the code, ends the flow at the Host, stops reading it, and says so', async () => {
    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout', 'setInterval', 'clearInterval'] });
    const wrapper = mount(CodeSignIn, { props: { code, providerName: 'Microsoft' }, attachTo: document.body });
    await flushPromises();

    const cancel = wrapper.find('[data-device-cancel]');
    expect(cancel.exists()).toBe(true);
    // Beside the code, in the same row.
    expect(cancel.element.parentElement?.querySelector('[data-device-code]')).not.toBeNull();

    await cancel.trigger('click');
    await flushPromises();

    expect(cancelConnectionFlow).toHaveBeenCalledWith('flow-1');
    expect(wrapper.emitted('cancelled')).toHaveLength(1);
    await vi.advanceTimersByTimeAsync(10_000);
    await flushPromises();
    expect(getConnectionFlow).not.toHaveBeenCalled();
    expect(wrapper.emitted('ended')).toBeUndefined();

    wrapper.unmount();
  });

  it('a flow that ended meanwhile is read again and ends as it ended', async () => {
    cancelConnectionFlow.mockRejectedValue(Object.assign(new Error('The sign-in already ended: Connected Work.'), { status: 409 }));
    getConnectionFlow.mockResolvedValue({ state: 'done', sentence: 'Connected Work.', connection: work });
    const wrapper = mount(CodeSignIn, { props: { code, providerName: 'Microsoft' }, attachTo: document.body });
    await flushPromises();

    await wrapper.find('[data-device-cancel]').trigger('click');
    await settle();

    expect(wrapper.emitted('cancelled')).toBeUndefined();
    expect(wrapper.emitted('ended')?.[0]?.[0]).toMatchObject({ state: 'done' });

    wrapper.unmount();
  });
});

describe('Add connection, on a sign-in picked back up', () => {
  it('says Cancel ends it; Cancel ends it at the Host and the step starts over without the code', async () => {
    listOpenConnectionFlows.mockResolvedValue([{ ...code, provider: 'microsoft', state: 'waiting' }]);
    const wrapper = await mountDialog(ConnectionsDialog, {});
    await settle();
    await click(button('Add connection'));
    await settle();

    const resumed = bodyFind('[data-device-resumed]');
    expect(resumed?.textContent).toContain('started earlier');
    expect(resumed?.textContent).toContain('Cancel ends it');
    expect(bodyFind('[data-device-code]')?.textContent).toContain('FAKE-CODE');

    await click(bodyFind('[data-device-cancel]'));

    expect(cancelConnectionFlow).toHaveBeenCalledWith('flow-1');
    expect(bodyFind('[data-device-code]')).toBeNull();
    expect(bodyFind('[data-device-resumed]')).toBeNull();
    // Back at the Sign in step's start: the directory field, ready to sign in again.
    expect(bodyFind('[data-connect-step]')?.getAttribute('data-connect-step')).toBe('signin');
    expect(bodyFind('[data-directory-field]')).not.toBeNull();

    wrapper.unmount();
  });
});

describe("A Connections tile's Reconnect, with a code", () => {
  it('shows Cancel beside the code; Cancel ends it at the Host and the dialog offers the code again', async () => {
    listConnections.mockResolvedValue([work]);
    const wrapper = await mountDialog(ConnectionsDialog, {});
    await settle();
    const reconnect = [...bodyFind('[data-connection="conn-work"]')!.querySelectorAll('button')].find(
      (candidate) => candidate.getAttribute('aria-label') === 'Reconnect Work',
    );
    await click(reconnect ?? null);
    await click(bodyFind('[data-reconnect-continue]'));

    expect(bodyFind('[data-reconnect-dialog] [data-device-code]')?.textContent).toContain('FAKE-CODE');
    await click(bodyFind('[data-reconnect-dialog] [data-device-cancel]'));

    expect(cancelConnectionFlow).toHaveBeenCalledWith('flow-1');
    expect(bodyFind('[data-reconnect-dialog] [data-device-code]')).toBeNull();
    expect(bodyFind('[data-reconnect-continue]')).not.toBeNull();

    wrapper.unmount();
  });
});
