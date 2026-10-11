// @vitest-environment happy-dom
//
// ADMIN > CONNECTIONS > ADD CONNECTION, FOR MICROSOFT: guided like Google, but signing in with a
// code. The set-up step is Microsoft's own guide from the Host - register the app and choose who can
// sign in, allow public client flows, the Graph permissions to copy, then the Application (client)
// ID, checked as a GUID, and no secret. The sign-in step shows the code the Host was given (large and
// copyable), the link to enter it at (in a new tab) and a countdown to its expiry, and reads the flow
// until it is done, when it moves on by itself; refused and expired each say so and offer Try again.
//
// THE MOCK IS OF `api/client`, in the shapes the Host serves, and of the clipboard. Every code here is
// an obviously fake one. Timers are faked only from the sign-in step on, with the clock pinned, so
// the countdown and the reads are driven by the spec; the countdown is run under more than one time
// zone (TZ=UTC and the machine's own) and reads the same in each.
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
  copyText,
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
  copyText: vi.fn(),
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
  currentOrigin: () => 'http://192.168.1.20:8080',
}));

vi.mock('../../lib/clipboard', () => ({ copyText }));

import ConnectionsDialog from '../ConnectionsDialog.vue';
import type { ConnectionGuide, ConnectionNeeds } from '../../api/types';
import { bodyFind, mountDialog, resetBody } from '../../test/mountQuasar';
import { hostConnection, hostProvider } from '../../test/pluginFixtures';
import { button, field, isDisabled, settle, type } from '../../test/formProbe';

const mailSend = 'https://graph.microsoft.com/Mail.Send';

const needs: ConnectionNeeds = {
  provider: 'microsoft',
  needs: [{ plugin: 'mail-helper', slot: 'mail', description: null, scopes: [mailSend] }],
  scopes: [{ scope: mailSend, words: 'Send mail as you', plugins: ['mail-helper'] }],
  apis: [],
};

const microsoftGuide: ConnectionGuide = {
  steps: [
    {
      id: 'register',
      title: 'Register an app',
      text: 'In Microsoft Entra ID, register a new application and choose who can sign in.',
      link: 'https://entra.microsoft.com/#view/Microsoft_AAD_RegisteredApps/CreateApplicationBlade',
      copy: [],
    },
    {
      id: 'public-client',
      title: 'Allow public client flows',
      text: 'Under Authentication, set Allow public client flows to Yes.',
      link: null,
      copy: [],
    },
    {
      id: 'data-access',
      title: 'API permissions',
      text: 'Add these Microsoft Graph delegated permissions. A work tenant may need an admin to grant consent.',
      link: null,
      copy: [
        { label: 'Send mail as you', value: 'Mail.Send' },
        { label: 'Stay signed in', value: 'offline_access' },
      ],
    },
    {
      id: 'credentials',
      title: 'Paste the Application (client) ID',
      text: 'Copy the Application (client) ID from the app overview and paste it here.',
      link: null,
      copy: [],
    },
  ],
};

const google = hostProvider({ id: 'google' });
const microsoftBare = hostProvider({ id: 'microsoft', guide: microsoftGuide });
const microsoftReady = hostProvider({ id: 'microsoft', clientId: '00000000-0000-0000-0000-00000000c1d0', configured: true, guide: microsoftGuide });

const clientGuid = '11111111-2222-3333-4444-555555555555';
const tenantGuid = 'aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee';

// A fixed instant, and a code that lasts fifteen minutes from it.
const now = new Date('2026-10-04T10:00:00Z');
const fakeCode = 'FAKE-CODE';
const verificationUri = 'https://microsoft.example.test/devicelogin';
const started = (userCode = fakeCode) => ({
  flowId: 'flow-1',
  userCode,
  verificationUri,
  expiresAt: '2026-10-04T10:15:00Z',
});

const connected = hostConnection({ id: 'conn-ms', provider: 'microsoft', name: 'person@example.test', account: 'person@example.test' });

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
    copyText,
  ]) {
    mock.mockReset();
  }
  sessionStorage.clear();
  listConnections.mockResolvedValue([]);
  listConnectionProviders.mockResolvedValue([google, microsoftReady]);
  getConnectionNeeds.mockResolvedValue(needs);
  copyText.mockResolvedValue(undefined);
  startDeviceConnection.mockResolvedValue(started());
  getConnectionFlow.mockResolvedValue({ state: 'waiting', sentence: 'Waiting for you to sign in.' });
  listOpenConnectionFlows.mockResolvedValue([]);
  listMailboxPresets.mockResolvedValue([]);
});

afterEach(() => {
  vi.useRealTimers();
  resetBody();
});

async function mountConnections() {
  const wrapper = await mountDialog(ConnectionsDialog, {});
  await settle();
  return wrapper;
}

/** `settle`, which waits on a real timer, or its like on the spec's clock once that is pinned. */
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

/** `type`, on either clock. */
async function typeIn(label: string, value: string) {
  const element = field(label);
  element.value = value;
  element.dispatchEvent(new Event('input', { bubbles: true }));
  await settled();
}

const step = () => bodyFind('[data-connect-step]')?.getAttribute('data-connect-step') ?? null;
const audience = (value: string) => bodyFind(`[data-audience="${value}"]`);

/** Add connection, then Microsoft, then Next: the set-up step when its client is not set up. */
async function chooseMicrosoft() {
  await click(button('Add connection'));
  await click(bodyFind('[data-connect-service="microsoft"]'));
  await click(button('Next'));
}

/** From here the clock is the spec's: pinned at `now`, moved on only by `advance`. */
function pinClock() {
  vi.useFakeTimers({ now, toFake: ['Date', 'setTimeout', 'clearTimeout', 'setInterval', 'clearInterval'] });
}

async function advance(ms: number) {
  await vi.advanceTimersByTimeAsync(ms);
  await settled();
}

/** Signed-in step, code asked for: what a person sees once they press Sign in with Microsoft. */
async function startSignIn() {
  await chooseMicrosoft();
  expect(step()).toBe('signin');
  pinClock();
  await click(button('Sign in with Microsoft'));
}

describe('Microsoft in Add connection', () => {
  it('is guided like Google, not handed to Advanced', async () => {
    const wrapper = await mountConnections();
    await click(button('Add connection'));
    await click(bodyFind('[data-connect-service="microsoft"]'));

    expect(bodyFind('[data-guided-connect]')).not.toBeNull();
    expect(bodyFind('[data-connect-dialog]')).toBeNull();
    expect(getConnectionNeeds).toHaveBeenCalledWith('microsoft');
    expect(bodyFind(`[data-connect-scope="${mailSend}"]`)!.textContent).toContain('Send mail as you');

    wrapper.unmount();
  });
});

describe("Microsoft's set-up step", () => {
  it("shows Microsoft's guide in order, who can sign in on the first step, the permissions to copy, and no secret", async () => {
    listConnectionProviders.mockResolvedValue([google, microsoftBare]);
    const wrapper = await mountConnections();
    await chooseMicrosoft();

    expect(step()).toBe('setup');
    const ids = [...document.body.querySelectorAll('[data-guide-step]')].map((element) => element.getAttribute('data-guide-step'));
    expect(ids).toEqual(['register', 'public-client', 'data-access', 'credentials']);

    const register = bodyFind('[data-guide-step="register"]')!;
    expect(register.querySelector('a[data-guide-link]')!.getAttribute('target')).toBe('_blank');
    for (const value of ['common', 'organizations', 'tenant']) {
      expect(register.querySelector(`[data-audience="${value}"]`)).not.toBeNull();
    }
    expect(register.textContent).toContain('Personal and any work account');
    expect(register.textContent).toContain('Work accounts only');
    expect(register.textContent).toContain('Only my organisation');
    // Personal and any work account is the default.
    expect(audience('common')!.getAttribute('aria-checked')).toBe('true');

    expect(bodyFind('[data-guide-step="public-client"]')!.textContent).toContain('Allow public client flows');

    const permissions = bodyFind('[data-guide-step="data-access"]')!;
    expect(permissions.textContent).toContain('admin to grant consent');
    await click(permissions.querySelector('button[aria-label="Copy Stay signed in"]'));
    expect(copyText).toHaveBeenCalledWith('offline_access');

    expect(field('Application (client) ID')).toBeDefined();
    expect(() => field('Client secret')).toThrow();
    // No tenant id is asked for until only my organisation is chosen.
    expect(() => field('Directory (tenant) ID')).toThrow();

    wrapper.unmount();
  });

  it('checks the client ID as a GUID, then saves it with who can sign in and no secret', async () => {
    listConnectionProviders.mockResolvedValue([google, microsoftBare]);
    const wrapper = await mountConnections();
    await chooseMicrosoft();

    await type('Application (client) ID', 'my-app-name');
    expect(bodyFind('[data-client-id-shape]')!.textContent).toContain('GUID');
    expect(isDisabled('Save client')).toBe(true);

    await type('Application (client) ID', clientGuid);
    expect(bodyFind('[data-client-id-shape]')).toBeNull();
    saveConnectionProvider.mockResolvedValue({ ...microsoftReady, clientId: clientGuid });
    await click(button('Save client'));

    expect(saveConnectionProvider).toHaveBeenCalledWith('microsoft', { clientId: clientGuid, audience: 'common' });
    expect(step()).toBe('signin');

    wrapper.unmount();
  });

  it('saves work accounts only as organizations', async () => {
    listConnectionProviders.mockResolvedValue([google, microsoftBare]);
    const wrapper = await mountConnections();
    await chooseMicrosoft();

    await click(audience('organizations'));
    await type('Application (client) ID', clientGuid);
    saveConnectionProvider.mockResolvedValue({ ...microsoftReady, clientId: clientGuid });
    await click(button('Save client'));

    expect(saveConnectionProvider).toHaveBeenCalledWith('microsoft', { clientId: clientGuid, audience: 'organizations' });

    wrapper.unmount();
  });

  it('asks only my organisation for its tenant ID, checked as a GUID, and saves it', async () => {
    listConnectionProviders.mockResolvedValue([google, microsoftBare]);
    const wrapper = await mountConnections();
    await chooseMicrosoft();

    await click(audience('tenant'));
    await type('Application (client) ID', clientGuid);
    // Without a tenant ID there is nothing to save.
    expect(isDisabled('Save client')).toBe(true);

    await type('Directory (tenant) ID', 'contoso.example.test');
    expect(bodyFind('[data-tenant-id-shape]')!.textContent).toContain('GUID');
    expect(isDisabled('Save client')).toBe(true);

    await type('Directory (tenant) ID', tenantGuid);
    expect(bodyFind('[data-tenant-id-shape]')).toBeNull();
    saveConnectionProvider.mockResolvedValue({ ...microsoftReady, clientId: clientGuid });
    await click(button('Save client'));

    expect(saveConnectionProvider).toHaveBeenCalledWith('microsoft', { clientId: clientGuid, audience: 'tenant', tenantId: tenantGuid });

    wrapper.unmount();
  });
});

describe("Microsoft's sign-in step", () => {
  it('shows the code large and copyable, the link in a new tab, and counts down to expiry', async () => {
    const wrapper = await mountConnections();
    await startSignIn();

    expect(startDeviceConnection).toHaveBeenCalledWith({ provider: 'microsoft', scopes: [mailSend], flow: 'device' });
    expect(startConnection).not.toHaveBeenCalled();
    expect(goTo).not.toHaveBeenCalled();

    const code = bodyFind('[data-device-code]')!;
    expect(code.textContent).toContain(fakeCode);
    expect(code.classList.contains('connect-device-code')).toBe(true);
    await click(bodyFind('button[aria-label="Copy the code"]'));
    expect(copyText).toHaveBeenCalledWith(fakeCode);

    const link = bodyFind('a[data-device-link]')!;
    expect(link.getAttribute('href')).toBe(verificationUri);
    expect(link.getAttribute('target')).toBe('_blank');
    expect(link.getAttribute('rel')).toContain('noopener');

    expect(bodyFind('[data-device-countdown]')!.textContent).toContain('15:00');
    await advance(61_000);
    expect(bodyFind('[data-device-countdown]')!.textContent).toContain('13:59');

    wrapper.unmount();
  });

  it('reads the flow while it is waiting, and stays on the code', async () => {
    const wrapper = await mountConnections();
    await startSignIn();

    expect(bodyFind('[data-device-state]')!.getAttribute('data-device-state')).toBe('waiting');
    await advance(10_000);

    expect(getConnectionFlow).toHaveBeenCalledWith('flow-1');
    expect(getConnectionFlow.mock.calls.length).toBeGreaterThanOrEqual(2);
    expect(step()).toBe('signin');
    expect(bodyFind('[data-device-code]')!.textContent).toContain(fakeCode);

    wrapper.unmount();
  });

  it('moves on by itself when the flow reads done, to the account and an optional name', async () => {
    const wrapper = await mountConnections();
    await startSignIn();

    getConnectionFlow.mockResolvedValue({ state: 'done', sentence: 'Connected person@example.test.', connection: connected });
    await advance(10_000);

    expect(step()).toBe('result');
    expect(bodyFind('[data-guided-connect]')!.textContent).toContain('person@example.test');

    // No more reads once it is done.
    const reads = getConnectionFlow.mock.calls.length;
    await advance(30_000);
    expect(getConnectionFlow.mock.calls.length).toBe(reads);

    renameConnection.mockResolvedValue({});
    await typeIn('Name (optional)', 'Work mail');
    await click(button('Finish'));
    expect(renameConnection).toHaveBeenCalledWith('conn-ms', 'Work mail');

    wrapper.unmount();
  });

  for (const ending of [
    { state: 'refused', sentence: 'You did not allow the app to sign in. Nothing was connected.' },
    { state: 'expired', sentence: 'The code expired before you entered it. Nothing was connected.' },
  ] as const) {
    it(`says what happened when the flow reads ${ending.state}, and Try again asks for a new code`, async () => {
      const wrapper = await mountConnections();
      await startSignIn();

      getConnectionFlow.mockResolvedValue(ending);
      await advance(10_000);

      expect(step()).toBe('signin');
      expect(bodyFind('[data-device-state]')!.getAttribute('data-device-state')).toBe(ending.state);
      expect(bodyFind('[data-device-sentence]')!.textContent).toContain(ending.sentence);
      expect(bodyFind('[data-device-code]')).toBeNull();

      // No more reads of a flow that has ended.
      const reads = getConnectionFlow.mock.calls.length;
      await advance(30_000);
      expect(getConnectionFlow.mock.calls.length).toBe(reads);

      startDeviceConnection.mockResolvedValue(started('NEWC-ODE0'));
      getConnectionFlow.mockResolvedValue({ state: 'waiting', sentence: 'Waiting for you to sign in.' });
      await click(button('Try again'));

      expect(startDeviceConnection).toHaveBeenCalledTimes(2);
      expect(bodyFind('[data-device-code]')!.textContent).toContain('NEWC-ODE0');
      expect(bodyFind('[data-device-state]')!.getAttribute('data-device-state')).toBe('waiting');

      wrapper.unmount();
    });
  }

  it('stops reading the flow when the dialog is closed', async () => {
    const wrapper = await mountConnections();
    await startSignIn();

    await click(bodyFind('[data-guided-connect] button[aria-label="Close"]'));
    const reads = getConnectionFlow.mock.calls.length;
    await advance(30_000);

    expect(getConnectionFlow.mock.calls.length).toBe(reads);

    wrapper.unmount();
  });

  it('picks a waiting sign-in back up when Add connection is closed and opened again: its code, link, countdown and state', async () => {
    const wrapper = await mountConnections();
    await startSignIn();
    await advance(61_000);
    await click(bodyFind('[data-guided-connect] button[aria-label="Close"]'));

    // The Host still holds the flow, and lists it as the caller's own waiting one.
    listOpenConnectionFlows.mockResolvedValue([
      { flowId: 'flow-1', provider: 'microsoft', userCode: fakeCode, verificationUri, expiresAt: '2026-10-04T10:15:00Z', state: 'waiting' },
    ]);
    await advance(60_000);
    await click(button('Add connection'));

    expect(listOpenConnectionFlows).toHaveBeenCalled();
    expect(step()).toBe('signin');
    expect(bodyFind('[data-device-state]')!.getAttribute('data-device-state')).toBe('waiting');
    expect(bodyFind('[data-device-code]')!.textContent).toContain(fakeCode);
    expect(bodyFind('a[data-device-link]')!.getAttribute('href')).toBe(verificationUri);
    expect(bodyFind('[data-device-countdown]')!.textContent).toContain('12:59');
    // No second code was asked for: it is the same sign-in.
    expect(startDeviceConnection).toHaveBeenCalledTimes(1);

    // And it is read again until it is done.
    const reads = getConnectionFlow.mock.calls.length;
    getConnectionFlow.mockResolvedValue({ state: 'done', sentence: 'Connected person@example.test.', connection: connected });
    await advance(3_000);
    expect(getConnectionFlow.mock.calls.length).toBeGreaterThan(reads);
    expect(step()).toBe('result');

    wrapper.unmount();
  });
});

describe('Reconnect of a Microsoft connection', () => {
  // Made with a code: its app is a public client with no redirect URI, so the browser sign-in
  // would stop at Microsoft. Reconnect signs in with a code too, in the Reconnect dialog.
  const outlook = hostConnection({
    id: 'conn-outlook',
    provider: 'microsoft',
    name: 'Outlook',
    account: 'someone@contoso.test',
    scopes: ['offline_access'],
    status: 'needs-reconnect',
    statusReason: 'invalid_grant: Token has been expired or revoked.',
  });

  const reconnectButton = () =>
    [...bodyFind('[data-connection="conn-outlook"]')!.querySelectorAll('button')].find(
      (candidate) => candidate.getAttribute('aria-label') === 'Reconnect Outlook',
    ) ?? null;

  async function startReconnect() {
    listConnections.mockResolvedValue([outlook]);
    const wrapper = await mountConnections();
    await click(reconnectButton());
    pinClock();
    return wrapper;
  }

  it('says it shows a code, not that the browser comes back here', async () => {
    const wrapper = await startReconnect();

    const asks = bodyFind('[data-reconnect-asks]')!.textContent!;
    expect(asks).toContain('code');
    expect(asks).toContain('someone@contoso.test');
    expect(bodyFind('[data-reconnect-dialog]')!.textContent).not.toContain('You come back here');

    wrapper.unmount();
  });

  it('signs in with a code for that connection, shows the code, and once done says it worked', async () => {
    const wrapper = await startReconnect();
    await click(bodyFind('[data-reconnect-continue]'));

    expect(startDeviceConnection).toHaveBeenCalledWith({ reconnectId: 'conn-outlook', scopes: [mailSend], flow: 'device' });
    expect(startConnection).not.toHaveBeenCalled();
    expect(goTo).not.toHaveBeenCalled();
    expect(bodyFind('[data-reconnect-dialog] [data-device-code]')!.textContent).toBe(fakeCode);
    expect(bodyFind('[data-reconnect-dialog] a[data-device-link]')!.getAttribute('href')).toBe(verificationUri);

    const reconnected = { ...outlook, status: 'ok' as const, statusReason: null };
    listConnections.mockResolvedValue([reconnected]);
    getConnectionFlow.mockResolvedValue({ state: 'done', sentence: 'Reconnected Outlook.', connection: reconnected });
    await advance(3000);

    expect(getConnectionFlow).toHaveBeenCalledWith('flow-1');
    expect(bodyFind('[data-reconnect-dialog]')).toBeNull();
    expect(bodyFind('[data-connections-dialog]')).not.toBeNull();
    expect(bodyFind('[data-connections-notice]')!.textContent).toContain('Reconnected Outlook (someone@contoso.test).');

    wrapper.unmount();
  });

  for (const ending of [
    { state: 'refused' as const, sentence: 'Microsoft refused the sign-in: access_denied.' },
    { state: 'expired' as const, sentence: 'The code expired before anyone signed in.' },
  ]) {
    it(`says which connection was not reconnected and why when the code is ${ending.state}`, async () => {
      const wrapper = await startReconnect();
      await click(bodyFind('[data-reconnect-continue]'));

      getConnectionFlow.mockResolvedValue(ending);
      await advance(3000);

      expect(bodyFind('[data-reconnect-dialog]')).toBeNull();
      const notice = bodyFind('[data-connections-notice]')!;
      expect(notice.textContent).toContain(`Outlook was not reconnected: ${ending.sentence}`);
      expect(notice.className).toContain('text-negative');

      wrapper.unmount();
    });
  }
});
