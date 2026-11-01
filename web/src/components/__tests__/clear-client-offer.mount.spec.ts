// @vitest-environment happy-dom
//
// UNDO A SAVED MICROSOFT OR GOOGLE APP, and MICROSOFT NOT KNOWING THE SAVED APP'S CLIENT ID.
//
// Once Set up client is saved it wins over the instance's own app for good, unless a person clears it.
// The service's own app step in Add connection offers "Use <product>'s app" for Microsoft on an
// instance with an app of its own and "Remove this app" for Google, naming the connections that
// signed in through the saved app and need Reconnect; the Host's answer then names them again. A
// refusal (the row could not be written) is said and the step still shows the saved app.
//
// A code start or a code that Microsoft ends with the Host's unknown-client sentence (AADSTS700016 or
// unauthorized_client, a directory ID pasted into Client ID) shows that one sentence, never the code
// alone, and offers the same "Use <product>'s app" under it.
//
// THE MOCK IS OF `api/client`, in the shapes the Host serves. Every ID here is a fake one.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { flushPromises, mount } from '@vue/test-utils';

const {
  listConnections,
  listConnectionProviders,
  getConnectionNeeds,
  startConnection,
  startDeviceConnection,
  getConnectionFlow,
  clearConnectionProviderClient,
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
  clearConnectionProviderClient: vi.fn(),
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
  clearConnectionProviderClient,
  listOpenConnectionFlows,
  listMailboxPresets,
}));

vi.mock('../../lib/browserNavigation', () => ({
  goTo,
  currentOrigin: () => 'http://localhost:8080',
}));

import CodeSignIn from '../CodeSignIn.vue';
import ConnectionsDialog from '../ConnectionsDialog.vue';
import type { ConnectionNeeds, ConnectionProvider } from '../../api/types';
import { UnknownClientSentence } from '../../lib/connections';
import { productTitle } from '../../presentation/product';
import { bodyFind, mountDialog, resetBody } from '../../test/mountQuasar';
import { hostConnection, hostProvider } from '../../test/pluginFixtures';
import { button, isDisabled, settle } from '../../test/formProbe';
import { letGoSignIns } from '../../lib/letGoSignIns';

const mailSend = 'https://graph.microsoft.com/Mail.Send';
const needs: ConnectionNeeds = {
  provider: 'microsoft',
  needs: [{ plugin: 'mail-helper', slot: 'mail', description: null, scopes: [mailSend] }],
  scopes: [{ scope: mailSend, words: 'Send mail as you', plugins: ['mail-helper'] }],
  apis: [],
};
const savedClient = '8f3c2a71-0b6e-4d2a-9c11-5e7f4a2b9d03';
const savedMicrosoft = hostProvider({ id: 'microsoft', configured: true, clientSource: 'saved', clientId: savedClient, instanceClient: true });
const defaultMicrosoft = hostProvider({ id: 'microsoft', configured: true, clientSource: 'default', instanceClient: true });
const savedGoogle = hostProvider({ id: 'google', configured: true, clientSource: 'saved', clientId: 'client-1.apps.googleusercontent.com', clientSecretSet: true });
const work = hostConnection({ id: 'conn-work', provider: 'microsoft', name: 'Work', account: 'worker@example.test' });
const mail = hostConnection({ id: 'conn-mail', provider: 'google', name: 'Gmail', account: 'person@example.com' });
const code = { flowId: 'flow-1', userCode: 'FAKE-CODE', verificationUri: 'https://microsoft.example.test/devicelogin', expiresAt: '2099-10-04T10:15:00Z' };
const useInstance = `Use ${productTitle}'s app`;

beforeEach(() => {
  letGoSignIns.clear();
  for (const mock of [listConnections, listConnectionProviders, getConnectionNeeds, startConnection, startDeviceConnection, getConnectionFlow, clearConnectionProviderClient, listOpenConnectionFlows, listMailboxPresets, goTo]) {
    mock.mockReset();
  }
  sessionStorage.clear();
  listConnections.mockResolvedValue([work, mail]);
  listConnectionProviders.mockResolvedValue([savedGoogle, savedMicrosoft]);
  getConnectionNeeds.mockResolvedValue(needs);
  startDeviceConnection.mockResolvedValue(code);
  getConnectionFlow.mockResolvedValue({ state: 'waiting', sentence: 'Waiting for you to sign in.' });
  listOpenConnectionFlows.mockResolvedValue([]);
  listMailboxPresets.mockResolvedValue([]);
});

afterEach(resetBody);

async function click(element: Element | null) {
  if (!element) throw new Error('nothing to click');
  (element as HTMLElement).click();
  await settle();
}

/** Add connection -> Sign-in -> the service -> Next: its own app step. */
async function appStep(service: 'google' | 'microsoft') {
  const wrapper = await mountDialog(ConnectionsDialog, {});
  await settle();
  await click(button('Add connection'));
  await click(bodyFind('[data-connection-kind="oauth"]'));
  await click(bodyFind(`[data-connect-service="${service}"]`));
  await click(button('Next'));
  expect(bodyFind('[data-connect-step]')?.getAttribute('data-connect-step')).toBe('setup');
  return wrapper;
}

const app = () => bodyFind('[data-connect-step="setup"]')!;

function cleared(provider: ConnectionProvider, reconnect: { id: string; name: string; account: string }[]) {
  return { provider, reconnect };
}

describe('The app step of a service with a saved app', () => {
  it("offers Use the instance's app for Microsoft, naming the connections that will need Reconnect, and clears it", async () => {
    clearConnectionProviderClient.mockResolvedValue(cleared(defaultMicrosoft, [{ id: 'conn-work', name: 'Work', account: 'worker@example.test' }]));
    const wrapper = await appStep('microsoft');

    const offer = app().querySelector('[data-clear-client]')!;
    expect(offer.querySelector('[data-clear-client-button]')?.textContent).toContain(useInstance);
    const words = offer.querySelector('[data-clear-client-words]')!.textContent!;
    expect(words).toContain(`signs in with ${productTitle}'s app again`);
    expect(words).toContain('needs Reconnect afterwards: Work');
    expect(words).not.toContain('Gmail');

    listConnectionProviders.mockResolvedValue([savedGoogle, defaultMicrosoft]);
    const reads = listConnectionProviders.mock.calls.length;
    await click(offer.querySelector('[data-clear-client-button]'));

    expect(clearConnectionProviderClient).toHaveBeenCalledWith('microsoft');
    const done = app().querySelector('[data-clear-client-done]')!.textContent!;
    expect(done).toContain(`Microsoft signs in with ${productTitle}'s app again.`);
    expect(done).toContain('This connection signed in through the saved app and needs Reconnect: Work.');
    // The list is read again, and the step now says the instance's app is in use.
    expect(listConnectionProviders.mock.calls.length).toBeGreaterThan(reads);
    expect(app().querySelector('[data-app-in-use]')?.getAttribute('data-app-in-use')).toBe('default');
    expect(app().querySelector('[data-app-in-use]')?.textContent).toContain(`${productTitle}'s app`);
    expect(app().querySelector('[data-clear-client-button]')).toBeNull();
    // Still the one way on: Next to Sign in.
    expect(isDisabled('Next')).toBe(false);

    wrapper.unmount();
  });

  it('offers Remove this app for Google, which then reads not set up', async () => {
    clearConnectionProviderClient.mockResolvedValue(
      cleared(hostProvider({ id: 'google', configured: false, clientSource: null }), [{ id: 'conn-mail', name: 'Gmail', account: 'person@example.com' }]),
    );
    const wrapper = await appStep('google');

    const offer = app().querySelector('[data-clear-client]')!;
    expect(offer.querySelector('[data-clear-client-button]')?.textContent).toContain('Remove this app');
    expect(offer.querySelector('[data-clear-client-words]')?.textContent).toContain('needs Reconnect afterwards: Gmail');

    await click(offer.querySelector('[data-clear-client-button]'));

    expect(clearConnectionProviderClient).toHaveBeenCalledWith('google');
    expect(app().querySelector('[data-clear-client-done]')?.textContent).toContain("Google's app was removed");
    // Not set up now: the guide is shown, and there is no Sign in until an app is saved.
    expect(app().querySelector('[data-app-in-use]')?.getAttribute('data-app-in-use')).toBe('none');
    expect(isDisabled('Next')).toBe(true);

    wrapper.unmount();
  });

  it('says the refusal and changes nothing when the row cannot be written', async () => {
    clearConnectionProviderClient.mockRejectedValue(new Error('The client was not cleared: its record could not be written (forced failure).'));
    const wrapper = await appStep('microsoft');
    const reads = listConnectionProviders.mock.calls.length;

    await click(app().querySelector('[data-clear-client-button]'));

    expect(app().querySelector('[data-clear-client-problem]')?.textContent).toContain('The client was not cleared');
    expect(app().querySelector('[data-clear-client-button]')).not.toBeNull();
    expect(app().querySelector('[data-app-in-use="saved"]')?.textContent).toContain(savedClient);
    expect(listConnectionProviders.mock.calls.length).toBe(reads);

    wrapper.unmount();
  });

  it('offers nothing for a built-in using the instance\'s app or with none saved', async () => {
    listConnectionProviders.mockResolvedValue([hostProvider({ id: 'google' }), defaultMicrosoft]);
    let wrapper = await appStep('microsoft');
    expect(app().querySelector('[data-app-in-use]')?.getAttribute('data-app-in-use')).toBe('default');
    expect(app().querySelector('[data-clear-client-button]')).toBeNull();
    wrapper.unmount();
    resetBody();

    wrapper = await appStep('google');
    expect(app().querySelector('[data-app-in-use]')?.getAttribute('data-app-in-use')).toBe('none');
    expect(app().querySelector('[data-clear-client-button]')).toBeNull();
    wrapper.unmount();
  });
});

describe('Microsoft not knowing the saved app', () => {
  it('a code it ends says the one sentence, no code, and offers the instance\'s app', async () => {
    getConnectionFlow.mockResolvedValue({ state: 'refused', sentence: UnknownClientSentence });
    clearConnectionProviderClient.mockResolvedValue(cleared(defaultMicrosoft, []));
    const wrapper = mount(CodeSignIn, { props: { code, providerName: 'Microsoft', resumed: true }, attachTo: document.body });
    await settle();
    await settle();

    const sentence = wrapper.find('[data-device-sentence]').text();
    expect(sentence).toBe(UnknownClientSentence);
    expect(sentence).not.toMatch(/AADSTS|unauthorized_client/);
    const offer = wrapper.find('[data-clear-client-button]');
    expect(offer.text()).toContain(useInstance);

    await offer.trigger('click');
    await flushPromises();
    expect(clearConnectionProviderClient).toHaveBeenCalledWith('microsoft');
    expect(wrapper.find('[data-clear-client-done]').text()).toContain(`signs in with ${productTitle}'s app again`);

    wrapper.unmount();
  });

  it('a refused code start in Add connection says the sentence and offers the instance\'s app beside it', async () => {
    startDeviceConnection.mockRejectedValue(new Error(UnknownClientSentence));
    const wrapper = await mountDialog(ConnectionsDialog, {});
    await settle();
    await click(button('Add connection'));
    await click(bodyFind('[data-connection-kind="oauth"]'));
    await click(bodyFind('[data-connect-service="microsoft"]'));
    await click(button('Next'));
    await click(button('Next'));
    expect(bodyFind('[data-connect-step]')?.getAttribute('data-connect-step')).toBe('signin');

    await click(button('Sign in with Microsoft'));

    const problem = bodyFind('[data-guided-problem]')!;
    expect(problem.textContent).toContain(UnknownClientSentence);
    expect(problem.querySelector('[data-clear-client-button]')?.textContent).toContain(useInstance);

    // Used from there: said in the dialog, the providers read again, the sentence gone.
    clearConnectionProviderClient.mockResolvedValue(cleared(defaultMicrosoft, []));
    listConnectionProviders.mockResolvedValue([savedGoogle, defaultMicrosoft]);
    await click(problem.querySelector('[data-clear-client-button]'));
    expect(clearConnectionProviderClient).toHaveBeenCalledWith('microsoft');
    expect(bodyFind('[data-client-cleared]')?.textContent).toContain(`Microsoft signs in with ${productTitle}'s app again.`);
    expect(bodyFind('[data-guided-problem]')).toBeNull();

    wrapper.unmount();
  });
});
