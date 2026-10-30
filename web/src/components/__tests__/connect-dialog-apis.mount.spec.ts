// @vitest-environment happy-dom
//
// ADMIN > CONNECTIONS > ADD CONNECTION, FOR A MICROSOFT CONNECTION SPANNING SEVERAL APIS: a Microsoft
// token is for one API, so the Host answers the needs grouped by API (`microsoftApis`). The service
// step lists each API's permissions in words under its own name - Azure management, Azure SQL, Azure
// Storage, Key Vault, Log Analytics, Microsoft Graph - each line still untickable, and the one sign-in
// asks for every ticked scope of every API. Needs naming one API, and Google, read as before.
//
// THE MOCK IS OF `api/client`, in the shapes the Host serves.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

const {
  listConnections,
  listConnectionProviders,
  getConnectionNeeds,
  startDeviceConnection,
  getConnectionFlow,
  listOpenConnectionFlows,
  listMailboxPresets,
  goTo,
  copyText,
} = vi.hoisted(() => ({
  listConnections: vi.fn(),
  listConnectionProviders: vi.fn(),
  getConnectionNeeds: vi.fn(),
  startDeviceConnection: vi.fn(),
  getConnectionFlow: vi.fn(),
  listOpenConnectionFlows: vi.fn(),
  listMailboxPresets: vi.fn(),
  goTo: vi.fn(),
  copyText: vi.fn(),
}));

vi.mock('../../api/client', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  listConnections,
  listConnectionProviders,
  getConnectionNeeds,
  startDeviceConnection,
  getConnectionFlow,
  listOpenConnectionFlows,
  listMailboxPresets,
}));

vi.mock('../../lib/browserNavigation', () => ({
  goTo,
  currentOrigin: () => 'http://localhost:8080',
}));

vi.mock('../../lib/clipboard', () => ({ copyText }));

import ConnectionsDialog from '../ConnectionsDialog.vue';
import type { ConnectionNeeds } from '../../api/types';
import { bodyFind, bodyText, mountDialog, resetBody } from '../../test/mountQuasar';
import { hostProvider } from '../../test/pluginFixtures';
import { button, settle } from '../../test/formProbe';

const graph = 'https://graph.microsoft.com/';
const management = 'https://management.azure.com/';
const sql = 'https://database.windows.net/';
const storage = 'https://storage.azure.com/';
const vault = 'https://vault.azure.net/';
const logs = 'https://api.loganalytics.io/';

const mailRead = `${graph}Mail.Read`;
const manage = `${management}user_impersonation`;
const sqlUse = `${sql}user_impersonation`;
const storageUse = `${storage}user_impersonation`;
const vaultUse = `${vault}user_impersonation`;
const logsRead = `${logs}Data.Read`;

/** A line as the Host serves it, wanted by the one plugin. */
const line = (scope: string, words: string | null) => ({ scope, words, plugins: ['azure-report'] });

const lines: Record<string, { scope: string; words: string | null; plugins: string[] }> = {
  [management]: line(manage, 'Manage your Azure resources as you'),
  [sql]: line(sqlUse, 'Use your Azure SQL databases as you'),
  [storage]: line(storageUse, 'Read and change your Azure Storage data as you'),
  [vault]: line(vaultUse, 'Use your Key Vault keys, secrets and certificates as you'),
  [logs]: line(logsRead, 'Read your Log Analytics data'),
  [graph]: line(mailRead, 'Read your mail'),
};

const names: Record<string, string> = {
  [management]: 'Azure management',
  [sql]: 'Azure SQL',
  [storage]: 'Azure Storage',
  [vault]: 'Key Vault',
  [logs]: 'Log Analytics',
  [graph]: 'Microsoft Graph',
};

const order = [management, sql, storage, vault, logs, graph];

/** `GET /api/connections/needs?provider=microsoft` for a slot spanning six APIs. */
const severalApis: ConnectionNeeds = {
  provider: 'microsoft',
  needs: [{ plugin: 'azure-report', slot: 'azure', description: null, scopes: order.map((api) => lines[api]!.scope) }],
  scopes: order.map((api) => lines[api]!),
  apis: [],
  microsoftApis: order.map((api) => ({ api, name: names[api]!, scopes: [lines[api]!] })),
};

/** The same read for a slot naming Graph only. */
const oneApi: ConnectionNeeds = {
  provider: 'microsoft',
  needs: [{ plugin: 'mail-helper', slot: 'mail', description: null, scopes: [mailRead] }],
  scopes: [lines[graph]!],
  apis: [],
  microsoftApis: [{ api: graph, name: 'Microsoft Graph', scopes: [lines[graph]!] }],
};

const gmail = 'https://www.googleapis.com/auth/gmail.modify';
const googleNeeds: ConnectionNeeds = {
  provider: 'google',
  needs: [{ plugin: 'mail-helper', slot: 'mail', description: null, scopes: [gmail] }],
  scopes: [{ scope: gmail, words: 'Read, change and send your Gmail', plugins: ['mail-helper'] }],
  apis: [],
};

const google = hostProvider({ id: 'google', clientId: '123.apps.googleusercontent.com', clientSecretSet: true, configured: true });
const microsoft = hostProvider({ id: 'microsoft', clientId: '00000000-0000-0000-0000-00000000c1d0', configured: true });

beforeEach(() => {
  for (const mock of [
    listConnections,
    listConnectionProviders,
    getConnectionNeeds,
    startDeviceConnection,
    getConnectionFlow,
    listOpenConnectionFlows,
    listMailboxPresets,
    goTo,
    copyText,
  ]) {
    mock.mockReset();
  }
  sessionStorage.clear();
  listConnections.mockResolvedValue([]);
  listConnectionProviders.mockResolvedValue([google, microsoft]);
  getConnectionNeeds.mockImplementation(async (provider: string) => (provider === 'google' ? googleNeeds : severalApis));
  startDeviceConnection.mockResolvedValue({
    flowId: 'flow-1',
    userCode: 'FAKE-CODE',
    verificationUri: 'https://microsoft.example.test/devicelogin',
    expiresAt: '2099-01-01T00:15:00Z',
  });
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

async function choose(service: 'microsoft' | 'google') {
  await click(button('Add connection'));
  await click(bodyFind('[data-connection-kind="oauth"]'));
  await click(bodyFind(`[data-connect-service="${service}"]`));
}

const group = (api: string) => bodyFind(`[data-connect-api="${api}"]`);
const groupsShown = () => [...document.body.querySelectorAll('[data-connect-api]')].map((element) => element.getAttribute('data-connect-api'));

describe('A Microsoft connection spanning several APIs', () => {
  it("lists each API's permissions in words, grouped under the API's name, in the Host's order", async () => {
    const wrapper = await mountConnections();
    await choose('microsoft');

    expect(getConnectionNeeds).toHaveBeenCalledWith('microsoft');
    expect(groupsShown()).toEqual(order);

    for (const api of order) {
      const shown = group(api)!;
      expect(shown.querySelector('[data-connect-api-name]')!.textContent).toContain(names[api]);
      expect(shown.querySelector('[data-connect-api-name]')!.textContent).toContain(api);

      // The API's own line, in words, naming the plugin that wants it, ticked - and no other API's.
      const own = shown.querySelectorAll('[data-connect-scope]');
      expect([...own].map((element) => element.getAttribute('data-connect-scope'))).toEqual([lines[api]!.scope]);
      expect(own[0]!.querySelector('[data-scope-words]')!.textContent).toContain(lines[api]!.words);
      expect(own[0]!.querySelector('[data-scope-plugins]')!.textContent).toContain('azure-report');
      expect(own[0]!.querySelector('[role="checkbox"]')!.getAttribute('aria-checked')).toBe('true');
    }

    wrapper.unmount();
  });

  it('signs in once, asking for every ticked scope of every API', async () => {
    const wrapper = await mountConnections();
    await choose('microsoft');

    await click(group(sql)!.querySelector('[role="checkbox"]'));
    await click(button('Next'));
    await click(button('Sign in with Microsoft'));

    expect(startDeviceConnection).toHaveBeenCalledTimes(1);
    expect(startDeviceConnection).toHaveBeenCalledWith({
      provider: 'microsoft',
      scopes: [manage, storageUse, vaultUse, logsRead, mailRead],
      flow: 'device',
    });

    wrapper.unmount();
  });

  it('reads as one list, with no API headings, when the needs name one API', async () => {
    getConnectionNeeds.mockResolvedValue(oneApi);
    const wrapper = await mountConnections();
    await choose('microsoft');

    expect(bodyFind('[data-connect-api-name]')).toBeNull();
    expect(bodyFind(`[data-connect-scope="${mailRead}"]`)!.textContent).toContain('Read your mail');

    wrapper.unmount();
  });

  it('leaves Google as it was: one list, no API headings', async () => {
    const wrapper = await mountConnections();
    await choose('google');

    expect(getConnectionNeeds).toHaveBeenCalledWith('google');
    expect(bodyFind('[data-connect-api-name]')).toBeNull();
    expect(bodyFind(`[data-connect-scope="${gmail}"]`)!.textContent).toContain('Read, change and send your Gmail');

    wrapper.unmount();
  });
});

describe('Two codes for a sign-in spanning several APIs', () => {
  afterEach(() => vi.useRealTimers());

  async function readAgain() {
    await vi.advanceTimersByTimeAsync(3_000);
    await vi.advanceTimersByTimeAsync(0);
  }

  it('says before the first code that there are two, numbers each, and says when the first is done', async () => {
    const wrapper = await mountConnections();
    await choose('microsoft');
    await click(button('Next'));

    const told = bodyFind('[data-two-codes]')!.textContent!;
    expect(told).toContain('two short codes');
    expect(told).toContain('come back to this');

    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout', 'setInterval', 'clearInterval'] });
    button('Sign in with Microsoft')!.click();
    await vi.advanceTimersByTimeAsync(0);
    await vi.advanceTimersByTimeAsync(0);
    expect(bodyFind('[data-code-number]')!.textContent).toBe('Code 1 of 2');
    expect(bodyFind('[data-first-code-done]')).toBeNull();
    expect(bodyText()).toContain('Waiting for the first code');

    getConnectionFlow.mockResolvedValue({
      state: 'waiting',
      sentence: 'Microsoft needs a second code to finish the sign-in.',
      userCode: 'FAKE-CODE-TWO',
      verificationUri: 'https://microsoft.example.test/devicelogin',
      expiresAt: '2099-01-01T00:20:00Z',
    });
    await readAgain();

    expect(bodyFind('[data-first-code-done]')!.textContent).toContain('approved the permissions');
    expect(bodyFind('[data-code-number]')!.textContent).toContain('Code 2 of 2');
    expect(bodyFind('[data-device-code]')!.textContent).toContain('FAKE-CODE-TWO');
    expect(bodyText()).toContain('Waiting for the second code');

    wrapper.unmount();
  });

  it('says nothing of two codes when what is ticked names one API', async () => {
    getConnectionNeeds.mockResolvedValue(oneApi);
    const wrapper = await mountConnections();
    await choose('microsoft');
    await click(button('Next'));

    expect(bodyFind('[data-two-codes]')).toBeNull();
    await click(button('Sign in with Microsoft'));
    expect(bodyFind('[data-code-number]')).toBeNull();

    wrapper.unmount();
  });

  it('says nothing of two codes once every API but one is unticked', async () => {
    const wrapper = await mountConnections();
    await choose('microsoft');
    for (const api of [sql, storage, vault, logs, graph]) await click(group(api)!.querySelector('[role="checkbox"]'));
    await click(button('Next'));

    expect(bodyFind('[data-two-codes]')).toBeNull();

    wrapper.unmount();
  });
});
