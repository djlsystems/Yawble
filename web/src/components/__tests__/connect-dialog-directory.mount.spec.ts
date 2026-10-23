// @vitest-environment happy-dom
//
// A MICROSOFT CONNECTION'S OWN DIRECTORY, in Admin > Connections. Add connection's Microsoft Sign in
// step has an optional "Directory (tenant) ID or domain" field, its explanation behind FieldHelp,
// sent as `tenant` when typed and left out when not; the Host's refusal is shown at the field.
// Reconnect shows the connection's directory to change or clear, and sends nothing when it is left
// as it was. Each connection with a directory names it on its tile; one without names none. Connect
// an account (Advanced) offers the same field for Microsoft's browser sign-in.
//
// THE MOCK IS OF `api/client`, in the shapes the Host serves. Every code and directory here is a fake one.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

const {
  listConnections,
  listConnectionProviders,
  getConnectionNeeds,
  startConnection,
  startDeviceConnection,
  getConnectionFlow,
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
  listOpenConnectionFlows,
  listMailboxPresets,
}));

vi.mock('../../lib/browserNavigation', () => ({
  goTo,
  currentOrigin: () => 'http://localhost:8080',
}));

import ConnectionsDialog from '../ConnectionsDialog.vue';
import type { ConnectionNeeds } from '../../api/types';
import { bodyFind, mountDialog, resetBody } from '../../test/mountQuasar';
import { hostConnection, hostProvider } from '../../test/pluginFixtures';
import { button, field, fieldWrapper, hasError, settle, type } from '../../test/formProbe';
import { letGoSignIns } from '../../lib/letGoSignIns';

const mailSend = 'https://graph.microsoft.com/Mail.Send';
const label = 'Directory (tenant) ID or domain (optional)';
const directoryGuid = 'aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee';
const refusal =
  'Directory (tenant) ID or domain is a directory ID such as xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx or a domain such as example.onmicrosoft.com.';

const needs: ConnectionNeeds = {
  provider: 'microsoft',
  needs: [{ plugin: 'mail-helper', slot: 'mail', description: null, scopes: [mailSend] }],
  scopes: [{ scope: mailSend, words: 'Send mail as you', plugins: ['mail-helper'] }],
  apis: [],
};

const microsoft = hostProvider({ id: 'microsoft', configured: true, clientSource: 'default' });
const code = { flowId: 'flow-1', userCode: 'FAKE-CODE', verificationUri: 'https://microsoft.example.test/devicelogin', expiresAt: '2026-10-04T10:15:00Z' };

const inDirectory = hostConnection({ id: 'conn-dir', provider: 'microsoft', name: 'Subscription', account: 'person@example.test', tenant: directoryGuid });
const noDirectory = hostConnection({ id: 'conn-plain', provider: 'microsoft', name: 'Work', account: 'worker@example.test', tenant: null });

beforeEach(() => {
  letGoSignIns.clear();
  for (const mock of [listConnections, listConnectionProviders, getConnectionNeeds, startConnection, startDeviceConnection, getConnectionFlow, listOpenConnectionFlows, listMailboxPresets, goTo]) {
    mock.mockReset();
  }
  sessionStorage.clear();
  listConnections.mockResolvedValue([]);
  listConnectionProviders.mockResolvedValue([hostProvider({ id: 'google' }), microsoft]);
  getConnectionNeeds.mockResolvedValue(needs);
  startDeviceConnection.mockResolvedValue(code);
  startConnection.mockResolvedValue({ authorizationUrl: 'https://login.example.test/authorize', state: 's', redirectUri: 'r', expiresAt: '2026-10-04T10:15:00Z' });
  getConnectionFlow.mockResolvedValue({ state: 'waiting', sentence: 'Waiting for you to sign in.' });
  listOpenConnectionFlows.mockResolvedValue([]);
  listMailboxPresets.mockResolvedValue([]);
});

afterEach(resetBody);

async function mountConnections() {
  const wrapper = await mountDialog(ConnectionsDialog, {});
  await settle();
  return wrapper;
}

async function click(element: Element | null) {
  if (!element) throw new Error('nothing to click');
  (element as HTMLElement).click();
  await settle();
}

/** Add connection, then Microsoft, then Next: with the instance's app, straight to Sign in. */
async function microsoftSignIn() {
  await click(button('Add connection'));
  await click(bodyFind('[data-connect-service="microsoft"]'));
  await click(button('Next'));
  expect(bodyFind('[data-connect-step]')?.getAttribute('data-connect-step')).toBe('signin');
}

const helpOf = (wrapper: HTMLElement) => wrapper.querySelector('[data-help]')?.getAttribute('aria-label') ?? '';

describe("Add connection's Microsoft Sign in step", () => {
  it('has an optional directory field whose help says when a person needs it and where the ID is', async () => {
    const wrapper = await mountConnections();
    await microsoftSignIn();

    const help = helpOf(fieldWrapper(label));
    expect(help).toContain('personal Microsoft account that owns an Azure subscription');
    expect(help).toContain('Microsoft Entra ID');
    expect(help).toContain('work or school account leaves it empty');

    wrapper.unmount();
  });

  it('sends the directory typed as tenant on the sign-in with a code', async () => {
    const wrapper = await mountConnections();
    await microsoftSignIn();
    await type(label, `  ${directoryGuid} `);
    await click(button('Sign in with Microsoft'));

    expect(startDeviceConnection).toHaveBeenCalledWith({ provider: 'microsoft', scopes: [mailSend], flow: 'device', tenant: directoryGuid });
    expect(bodyFind('[data-device-code]')?.textContent).toContain('FAKE-CODE');

    wrapper.unmount();
  });

  it('sends no tenant when the field is left empty', async () => {
    const wrapper = await mountConnections();
    await microsoftSignIn();
    await click(button('Sign in with Microsoft'));

    expect(startDeviceConnection).toHaveBeenCalledWith({ provider: 'microsoft', scopes: [mailSend], flow: 'device' });

    wrapper.unmount();
  });

  it("shows the Host's refusal of the directory at the field, and no code", async () => {
    startDeviceConnection.mockRejectedValue(new Error(refusal));
    const wrapper = await mountConnections();
    await microsoftSignIn();
    await type(label, 'not a directory');
    await click(button('Sign in with Microsoft'));

    expect(hasError(label)).toBe(true);
    expect(fieldWrapper(label).textContent).toContain(refusal);
    expect(bodyFind('[data-guided-problem]')).toBeNull();
    expect(bodyFind('[data-device-code]')).toBeNull();

    wrapper.unmount();
  });
});

describe('Admin > Connections, a Microsoft connection with its own directory', () => {
  it('names the directory on its tile, and nothing for one without', async () => {
    listConnections.mockResolvedValue([inDirectory, noDirectory]);
    const wrapper = await mountConnections();

    expect(bodyFind('[data-connection="conn-dir"] [data-connection-directory]')?.textContent).toContain(directoryGuid);
    expect(bodyFind('[data-connection="conn-plain"] [data-connection-directory]')).toBeNull();

    wrapper.unmount();
  });
});

describe('Reconnect of a Microsoft connection with its own directory', () => {
  async function reconnect(connection = inDirectory) {
    listConnections.mockResolvedValue([connection]);
    const wrapper = await mountConnections();
    const button = [...bodyFind(`[data-connection="${connection.id}"]`)!.querySelectorAll('button')].find(
      (candidate) => candidate.getAttribute('aria-label') === `Reconnect ${connection.name}`,
    );
    await click(button ?? null);
    return wrapper;
  }

  it('shows the directory, and keeps it by sending none when it is left as it was', async () => {
    const wrapper = await reconnect();

    expect(field(label).value).toBe(directoryGuid);
    await click(bodyFind('[data-reconnect-continue]'));
    expect(startDeviceConnection).toHaveBeenCalledWith({ reconnectId: 'conn-dir', scopes: [mailSend], flow: 'device' });

    wrapper.unmount();
  });

  it('sends the directory changed to', async () => {
    const wrapper = await reconnect();
    await type(label, 'example.onmicrosoft.com');
    await click(bodyFind('[data-reconnect-continue]'));

    expect(startDeviceConnection).toHaveBeenCalledWith({ reconnectId: 'conn-dir', scopes: [mailSend], flow: 'device', tenant: 'example.onmicrosoft.com' });

    wrapper.unmount();
  });

  it('clears the directory with an empty tenant when the field is emptied', async () => {
    const wrapper = await reconnect();
    await type(label, '');
    await click(bodyFind('[data-reconnect-continue]'));

    expect(startDeviceConnection).toHaveBeenCalledWith({ reconnectId: 'conn-dir', scopes: [mailSend], flow: 'device', tenant: '' });

    wrapper.unmount();
  });

  it("shows the Host's refusal at the field and no code", async () => {
    startDeviceConnection.mockRejectedValue(new Error(refusal));
    const wrapper = await reconnect();
    await type(label, 'not a directory');
    await click(bodyFind('[data-reconnect-continue]'));

    expect(fieldWrapper(label).textContent).toContain(refusal);
    expect(bodyFind('[data-reconnect-problem]')).toBeNull();
    expect(bodyFind('[data-reconnect-dialog] [data-device-code]')).toBeNull();

    wrapper.unmount();
  });
});

describe("Connect an account (Advanced), Microsoft's browser sign-in", () => {
  it('sends the directory typed as tenant', async () => {
    const wrapper = await mountConnections();
    await click(bodyFind('[data-connections-advanced]'));
    await click(button('Connect an account…'));
    await type(label, directoryGuid);
    await click(button('Connect'));

    expect(startConnection).toHaveBeenCalledWith({ provider: 'microsoft', scopes: [], name: null, tenant: directoryGuid });
    expect(goTo).toHaveBeenCalledWith('https://login.example.test/authorize');

    wrapper.unmount();
  });
});
