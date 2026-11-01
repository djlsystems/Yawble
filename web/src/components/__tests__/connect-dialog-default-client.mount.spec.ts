// @vitest-environment happy-dom
//
// ADD CONNECTION, MICROSOFT, WITH THE INSTANCE'S OWN APP: while the Host answers `clientSource:
// 'default'`, Microsoft's app step says that app is in use - no app to register - and setting up an
// app of one's own is on that same step, never under an Advanced…. When the account's organisation
// lets only an admin approve the app, Microsoft's refusal reads as one sentence saying an admin of
// that organisation approves it, never as its error code alone.
//
// THE MOCK IS OF `api/client`, in the shapes the Host serves. Every code and ID here is a fake one.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { flushPromises } from '@vue/test-utils';

const {
  listConnections,
  listConnectionProviders,
  getConnectionNeeds,
  startConnection,
  startDeviceConnection,
  getConnectionFlow,
  listOpenConnectionFlows,
  listMailboxPresets,
  renameConnection,
  saveConnectionProvider,
  goTo,
} = vi.hoisted(() => ({
  listConnections: vi.fn(),
  listConnectionProviders: vi.fn(),
  getConnectionNeeds: vi.fn(),
  startConnection: vi.fn(),
  startDeviceConnection: vi.fn(),
  getConnectionFlow: vi.fn(),
  listOpenConnectionFlows: vi.fn(),
  listMailboxPresets: vi.fn(),
  renameConnection: vi.fn(),
  saveConnectionProvider: vi.fn(),
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
  listOpenConnectionFlows,
  listMailboxPresets,
  renameConnection,
  saveConnectionProvider,
}));

vi.mock('../../lib/browserNavigation', () => ({
  goTo,
  currentOrigin: () => 'http://localhost:8080',
}));

import ConnectionsDialog from '../ConnectionsDialog.vue';
import type { ConnectionGuide, ConnectionNeeds } from '../../api/types';
import { AdminApprovalSentence } from '../../lib/connections';
import { bodyFind, mountDialog, resetBody } from '../../test/mountQuasar';
import { hostProvider } from '../../test/pluginFixtures';
import { button, settle } from '../../test/formProbe';

const mailRead = 'https://graph.microsoft.com/Mail.Read';

const needs: ConnectionNeeds = {
  provider: 'microsoft',
  needs: [{ plugin: 'mail-helper', slot: 'mail', description: null, scopes: [mailRead] }],
  scopes: [{ scope: mailRead, words: 'Read your mail', plugins: ['mail-helper'] }],
  apis: [],
};

const guide: ConnectionGuide = {
  steps: [
    { id: 'register', title: 'Register an app', text: 'Register a new application.', link: null, copy: [] },
    { id: 'credentials', title: 'Paste the Application (client) ID', text: 'Paste it here.', link: null, copy: [] },
  ],
};

const google = hostProvider({ id: 'google' });
const microsoftDefault = hostProvider({ id: 'microsoft', clientSource: 'default', configured: true, tenant: 'common', guide });

beforeEach(() => {
  for (const mock of [
    listConnections,
    listConnectionProviders,
    getConnectionNeeds,
    startConnection,
    startDeviceConnection,
    getConnectionFlow,
    listOpenConnectionFlows,
    listMailboxPresets,
    renameConnection,
    saveConnectionProvider,
    goTo,
  ]) {
    mock.mockReset();
  }
  sessionStorage.clear();
  listConnections.mockResolvedValue([]);
  listConnectionProviders.mockResolvedValue([google, microsoftDefault]);
  getConnectionNeeds.mockResolvedValue(needs);
  startDeviceConnection.mockResolvedValue({
    flowId: 'flow-1',
    userCode: 'FAKE-CODE',
    verificationUri: 'https://microsoft.example.test/devicelogin',
    expiresAt: '2026-10-04T10:15:00Z',
  });
  getConnectionFlow.mockResolvedValue({ state: 'waiting', sentence: 'Waiting for you to sign in.' });
  listOpenConnectionFlows.mockResolvedValue([]);
  listMailboxPresets.mockResolvedValue([]);
});

afterEach(() => {
  vi.useRealTimers();
  resetBody();
});

async function settled() {
  if (!vi.isFakeTimers()) return settle();
  await flushPromises();
  await vi.advanceTimersByTimeAsync(0);
  await flushPromises();
}

async function click(element: Element | null) {
  if (!element) throw new Error('nothing to click');
  (element as HTMLElement).click();
  await settled();
}

const step = () => bodyFind('[data-connect-step]')?.getAttribute('data-connect-step') ?? null;
const stepList = () => bodyFind('.connect-steps')?.textContent ?? '';

/** To Microsoft's app step. */
async function toApp() {
  await mountDialog(ConnectionsDialog, {});
  await settle();
  await click(button('Add connection'));
  await click(bodyFind('[data-connection-kind="oauth"]'));
  await click(bodyFind('[data-connect-service="microsoft"]'));
  await click(button('Next'));
}

/** Through the app step, on the instance's app, to Sign in. */
async function chooseMicrosoft() {
  await toApp();
  await click(button('Next'));
}

describe("Microsoft with the instance's own app", () => {
  it("says on its own app step that the instance's app is in use, with an app of one's own set up there", async () => {
    await toApp();

    expect(step()).toBe('setup');
    expect(stepList()).toContain('App');
    expect(bodyFind('[data-app-in-use]')!.getAttribute('data-app-in-use')).toBe('default');
    expect(bodyFind('[data-app-in-use]')!.textContent).toContain('nothing to register');
    // No saved app, so nothing to undo; the guide waits until asked for.
    expect(bodyFind('[data-clear-client-button]')).toBeNull();
    expect(bodyFind('[data-guide-step="credentials"]')).toBeNull();

    await click(bodyFind('[data-own-app]'));
    expect(step()).toBe('setup');
    expect(bodyFind('[data-setup-own]')).not.toBeNull();
    expect(bodyFind('[data-guide-audience]')).not.toBeNull();
    expect(bodyFind('[data-guide-step="credentials"]')).not.toBeNull();

    await click(button('Next'));
    expect(step()).toBe('signin');
    await click(button('Sign in with Microsoft'));
    expect(startDeviceConnection).toHaveBeenCalledWith({ provider: 'microsoft', scopes: [mailRead], flow: 'device' });
    expect(startConnection).not.toHaveBeenCalled();
    expect(bodyFind('[data-device-code]')!.textContent).toContain('FAKE-CODE');
  });

  it('puts no Advanced… on the service step or the sign-in step, and Back from Sign in is the app step', async () => {
    await mountDialog(ConnectionsDialog, {});
    await settle();
    await click(button('Add connection'));
    await click(bodyFind('[data-connection-kind="oauth"]'));
    await click(bodyFind('[data-connect-service="microsoft"]'));
    expect(bodyFind('[data-service-own-app]')).toBeNull();
    expect(bodyFind('[data-connect-own-app-service]')).toBeNull();
    // Gmail's own way in is not offered under the Microsoft tile.
    expect(bodyFind('[data-connect-advanced]')).toBeNull();
    expect(bodyFind('[data-guided-connect]')!.textContent).not.toContain('Advanced');

    await click(button('Next'));
    await click(button('Next'));
    expect(step()).toBe('signin');
    expect(bodyFind('[data-connect-own-app]')).toBeNull();
    expect(bodyFind('[data-signin-advanced]')).toBeNull();
    expect(bodyFind('[data-guided-connect]')!.textContent).not.toContain('Advanced');

    await click(button('Back'));
    expect(step()).toBe('setup');
    await click(button('Back'));
    expect(step()).toBe('service');
  });

  it('shows the guide at once, and holds Next, when there is no app at all', async () => {
    listConnectionProviders.mockResolvedValue([google, hostProvider({ id: 'microsoft', guide })]);
    await toApp();

    expect(step()).toBe('setup');
    expect(bodyFind('[data-app-in-use]')!.getAttribute('data-app-in-use')).toBe('none');
    expect(bodyFind('[data-guide-step="credentials"]')).not.toBeNull();
    expect(bodyFind('[data-own-app]')).toBeNull();
    expect(button('Next').hasAttribute('disabled')).toBe(true);
  });
});

describe('a refusal needing an admin of the organisation', () => {
  for (const sentence of [
    'Microsoft refused the sign-in: invalid_grant: AADSTS90094: The grant requires admin permission. Nothing was stored; try again.',
    'Microsoft refused the sign-in: AADSTS65001. Nothing was stored; try again.',
    AdminApprovalSentence,
  ]) {
    it(`reads as one sentence saying an admin approves the app: ${sentence.slice(0, 50)}`, async () => {
      await chooseMicrosoft();
      vi.useFakeTimers({ now: new Date('2026-10-04T10:00:00Z'), toFake: ['Date', 'setTimeout', 'clearTimeout', 'setInterval', 'clearInterval'] });
      await click(button('Sign in with Microsoft'));

      getConnectionFlow.mockResolvedValue({ state: 'refused', sentence });
      await vi.advanceTimersByTimeAsync(10_000);
      await settled();

      const shown = bodyFind('[data-device-sentence]')!.textContent!.trim();
      expect(shown).toBe(AdminApprovalSentence);
      expect(shown).toContain('An admin of that organisation approves the app');
      expect(shown).not.toMatch(/AADSTS|invalid_grant/);
      expect(shown.split(/[.!?](\s|$)/).filter((part) => part.trim() !== '').length).toBe(1);
    });
  }

  it('words a start Microsoft refuses for the same reason alike', async () => {
    startDeviceConnection.mockRejectedValue(new Error('Microsoft did not start the sign-in: invalid_client: AADSTS90099: The application has not been authorized in the tenant.'));
    await chooseMicrosoft();
    await click(button('Sign in with Microsoft'));

    expect(bodyFind('[data-guided-problem]')!.textContent!.trim()).toBe(AdminApprovalSentence);
  });

  it('leaves any other refusal as the Host said it', async () => {
    await chooseMicrosoft();
    vi.useFakeTimers({ now: new Date('2026-10-04T10:00:00Z'), toFake: ['Date', 'setTimeout', 'clearTimeout', 'setInterval', 'clearInterval'] });
    await click(button('Sign in with Microsoft'));

    getConnectionFlow.mockResolvedValue({ state: 'refused', sentence: 'The sign-in was declined at Microsoft. Nothing was stored; try again.' });
    await vi.advanceTimersByTimeAsync(10_000);
    await settled();

    expect(bodyFind('[data-device-sentence]')!.textContent).toContain('The sign-in was declined at Microsoft.');
  });
});
