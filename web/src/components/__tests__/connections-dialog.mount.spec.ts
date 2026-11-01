// @vitest-environment happy-dom
//
// ADMIN > CONNECTIONS. Each connection with its provider, account, scopes, when connected and last
// refreshed, its status (ok / needs reconnect with the provider's reason) and the members using it;
// one Add connection and no second door - no Advanced, no Providers tab, no Connect an account. Each
// OAuth service's app is its own step of Add connection: a custom service is named there, with the
// exact redirect URI for this address shown beforehand, and signed in through. Reconnect, Rename, and
// Disconnect with the Host's refusal naming the members. The client secret is write-only: the wizard
// says "set" and never holds a value.
//
// THE MOCK IS OF `api/client`, in the shapes connections-api.md gives, and of the one navigation
// the web makes (`lib/browserNavigation`), so where the browser would go is asserted, not followed.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

const {
  listConnections,
  listConnectionProviders,
  startConnection,
  renameConnection,
  disconnectConnection,
  saveConnectionProvider,
  deleteConnectionProvider,
  getConnectionNeeds,
  listOpenConnectionFlows,
  listMailboxPresets,
  listCredentialPresets,
  goTo,
} = vi.hoisted(() => ({
  listConnections: vi.fn(),
  listConnectionProviders: vi.fn(),
  startConnection: vi.fn(),
  renameConnection: vi.fn(),
  disconnectConnection: vi.fn(),
  saveConnectionProvider: vi.fn(),
  deleteConnectionProvider: vi.fn(),
  getConnectionNeeds: vi.fn(),
  listOpenConnectionFlows: vi.fn(),
  listMailboxPresets: vi.fn(),
  listCredentialPresets: vi.fn(),
  goTo: vi.fn(),
}));

vi.mock('../../api/client', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  listConnections,
  listConnectionProviders,
  startConnection,
  renameConnection,
  disconnectConnection,
  saveConnectionProvider,
  deleteConnectionProvider,
  getConnectionNeeds,
  listOpenConnectionFlows,
  listMailboxPresets,
  listCredentialPresets,
}));

// The address the page is on: a test sets another to see what a LAN address is told.
const page = vi.hoisted(() => ({ origin: 'https://instance.example.test' }));

vi.mock('../../lib/browserNavigation', () => ({
  goTo,
  currentOrigin: () => page.origin,
}));

import ConnectionsDialog from '../ConnectionsDialog.vue';
import { ActionRefused } from '../../api/client';
import { bodyFind, bodyText, mountDialog, resetBody } from '../../test/mountQuasar';
import { hostConnection, hostProvider } from '../../test/pluginFixtures';
import { button, field, isDisabled, settle, type } from '../../test/formProbe';

const workMail = hostConnection({
  id: 'conn-work',
  name: 'Work mail',
  provider: 'google',
  account: 'person@example.com',
  scopes: ['openid', 'email', 'https://www.googleapis.com/auth/userinfo.email', 'https://mail.google.com/'],
  permissions: ['Confirm who you are', 'See your email address', 'Read, change, send and permanently delete all your Gmail'],
  connectedAt: '2026-09-28T10:00:00Z',
  refreshedAt: '2026-09-28T11:00:00Z',
  usedBy: [{ team: 'mail-team', member: 'Inbox', label: 'Inbox', slot: 'mail' }],
});

const outlook = hostConnection({
  id: 'conn-outlook',
  name: 'Outlook',
  provider: 'microsoft',
  account: 'someone@contoso.test',
  scopes: ['offline_access'],
  status: 'needs-reconnect',
  statusReason: 'invalid_grant: Token has been expired or revoked.',
});

const google = hostProvider({
  id: 'google',
  clientId: '123.apps.googleusercontent.com',
  clientSecretSet: true,
  configured: true,
  help: 'Google: a "Desktop app" client accepts http://127.0.0.1:<port>; a "Web application" client needs the URI registered.',
});
const microsoft = hostProvider({ id: 'microsoft' });

const authorizationUrl = 'https://accounts.google.com/o/oauth2/v2/auth?client_id=123&state=abc';

beforeEach(() => {
  for (const mock of [
    listConnections,
    listConnectionProviders,
    startConnection,
    renameConnection,
    disconnectConnection,
    saveConnectionProvider,
    deleteConnectionProvider,
    getConnectionNeeds,
    listOpenConnectionFlows,
    listMailboxPresets,
    listCredentialPresets,
    goTo,
  ]) {
    mock.mockReset();
  }
  listConnections.mockResolvedValue([workMail, outlook]);
  listConnectionProviders.mockResolvedValue([google, microsoft]);
  getConnectionNeeds.mockResolvedValue({ provider: 'google', needs: [], scopes: [], apis: [] });
  listOpenConnectionFlows.mockResolvedValue([]);
  listMailboxPresets.mockResolvedValue([]);
  listCredentialPresets.mockResolvedValue([]);
  sessionStorage.clear();
  startConnection.mockResolvedValue({
    authorizationUrl,
    state: 'abc',
    redirectUri: 'https://instance.example.test/api/connections/callback',
    expiresAt: '2026-09-28T10:10:00Z',
  });
});

afterEach(resetBody);

async function mountConnections(props: Record<string, unknown> = {}) {
  const wrapper = await mountDialog(ConnectionsDialog, props);
  await settle();
  return wrapper;
}

async function click(element: Element | null) {
  if (!element) throw new Error('nothing to click');
  (element as HTMLElement).click();
  await settle();
}

const step = () => bodyFind('[data-connect-step]')?.getAttribute('data-connect-step') ?? null;

/** Add connection -> Sign-in -> the service's tile -> Next: its app step. */
async function toAppStep(tile: string) {
  await click(button('Add connection'));
  await click(bodyFind('[data-connection-kind="oauth"]'));
  await click(bodyFind(`[data-provider-tile="${tile}"]`));
  await click(button('Next'));
}

const row = (id: string) => bodyFind(`[data-connection="${id}"]`)!;

type Select = { props: (name: string) => unknown; vm: { $emit: (event: string, value: unknown) => void } };

function select(wrapper: { findAllComponents: (s: { name: string }) => unknown[] }, label: string): Select {
  const found = (wrapper.findAllComponents({ name: 'QSelect' }) as Select[]).find((s) => s.props('label') === label);
  if (!found) throw new Error(`no QSelect labelled "${label}"`);
  return found;
}

/** A button inside one element, by its label: its words, or a tile icon's aria-label naming it. */
function buttonIn(container: Element, label: string): HTMLButtonElement {
  const found = [...container.querySelectorAll('button')].find((candidate) => {
    const aria = candidate.getAttribute('aria-label') ?? '';
    return (candidate.querySelector('.block') ?? candidate).textContent?.trim() === label
      || aria === label
      || aria.startsWith(`${label} `);
  });
  if (!found) throw new Error(`no button labelled "${label}" in the element`);
  return found as HTMLButtonElement;
}

/** Opens a tab: the connections, or the providers. */
async function showTab(name: 'connections' | 'providers') {
  (bodyFind(`[data-connections-tab="${name}"]`) as HTMLElement).click();
  await settle();
}

describe('ConnectionsDialog, the list', () => {
  it('shows each connection: provider, account, scopes, connected, refreshed, status and who uses it', async () => {
    const wrapper = await mountConnections();

    const work = row('conn-work');
    expect(work.querySelector('[data-connection-name]')!.textContent).toBe('Work mail');
    expect(work.querySelector('[data-connection-provider]')!.textContent).toBe('Google');
    expect(work.querySelector('[data-connection-account]')!.textContent).toBe('person@example.com');
    // In words, once each: `email` and `userinfo.email` are one line.
    const permissions = [...work.querySelectorAll('[data-connection-permission]')].map((line) => line.textContent?.trim());
    expect(permissions).toEqual(['Confirm who you are', 'See your email address', 'Read, change, send and permanently delete all your Gmail']);
    expect(work.querySelector('[data-connection-scopes]')!.textContent).not.toContain('https://');
    expect(work.querySelector('[data-connection-connected]')!.textContent).toBe(new Date('2026-09-28T10:00:00Z').toLocaleString());
    expect(work.querySelector('[data-connection-refreshed]')!.textContent).toBe(new Date('2026-09-28T11:00:00Z').toLocaleString());
    expect(work.querySelector('[data-connection-status]')!.textContent).toBe('ok');
    expect(work.querySelector('[data-connection-used-by]')!.textContent).toContain('mail-team / Inbox (mail)');

    // Needs reconnect, in words, with the provider's reason; never refreshed reads "never".
    const stale = row('conn-outlook');
    expect(stale.querySelector('[data-connection-status]')!.textContent).toBe('needs reconnect');
    expect(stale.querySelector('[data-connection-reason]')!.textContent).toContain('invalid_grant: Token has been expired or revoked.');
    expect(stale.querySelector('[data-connection-refreshed]')!.textContent).toBe('never');
    expect(stale.querySelector('[data-connection-used-by]')!.textContent).toContain('No member');

    wrapper.unmount();
  });

  it('has one Add connection and no second door: no Advanced, no Providers tab, no Connect an account', async () => {
    const wrapper = await mountConnections();

    expect(button('Add connection')).not.toBeNull();
    expect(bodyFind('[data-connections-advanced]')).toBeNull();
    expect(bodyFind('[data-connections-tab]')).toBeNull();
    expect(bodyFind('[data-connections-panel="providers"]')).toBeNull();
    expect(bodyFind('[data-provider]')).toBeNull();
    expect(bodyText()).not.toContain('Connect an account');
    expect(bodyText()).not.toContain('Providers');
    expect(bodyFind('[data-connection="conn-work"]')).not.toBeNull();

    wrapper.unmount();
  });

  it("shows no account line for an Other API key whose unmasked fields are settings; Namecheap's API user still shows", async () => {
    listConnections.mockResolvedValue([
      hostConnection({
        id: 'conn-saver', name: 'Acme saver', provider: 'apikey', providerKind: 'apikey', kind: 'apikey', preset: 'other', account: '',
        fields: [{ name: 'autoSave', label: 'autoSave', masked: false, value: 'true', set: true }, { name: 'token', label: 'token', masked: true, value: null, set: true }],
      }),
      hostConnection({
        id: 'conn-nc', name: 'Namecheap', provider: 'apikey', providerKind: 'apikey', kind: 'apikey', preset: 'namecheap', account: 'nc-user',
        fields: [{ name: 'apiUser', label: 'API user', masked: false, value: 'nc-user', set: true }],
      }),
    ]);
    const wrapper = await mountConnections();

    expect(row('conn-saver').querySelector('[data-connection-account]')).toBeNull();
    // The setting is still shown as what it is, a field.
    expect(row('conn-saver').querySelector('[data-connection-field="autoSave"]')!.textContent!.trim()).toBe('true');
    expect(row('conn-nc').querySelector('[data-connection-account]')!.textContent).toBe('nc-user');

    wrapper.unmount();
  });

  it('says what the provider round trip came back with', async () => {
    const wrapper = await mountConnections({
      notice: { outcome: 'refused', reason: 'The sign-in was cancelled at the provider.' },
    });

    expect(bodyFind('[data-connections-notice]')!.textContent).toContain(
      'The account was not connected: The sign-in was cancelled at the provider.',
    );

    wrapper.unmount();
  });
});

describe('ConnectionsDialog, an OAuth service through Add connection', () => {
  it('offers every kind there is on the one flow', async () => {
    const wrapper = await mountConnections();
    await click(button('Add connection'));

    expect([...document.body.querySelectorAll('[data-kind-tiles] [data-connection-kind]')].map((tile) => tile.getAttribute('data-connection-kind')))
      .toEqual(['oauth', 'imap', 'website', 'apikey', 'login']);
    expect(bodyFind('[data-connection-kind="oauth"]')!.textContent).toContain('Microsoft, Google or another OAuth service');

    await click(bodyFind('[data-connection-kind="oauth"]'));
    expect([...document.body.querySelectorAll('[data-provider-tile]')].map((tile) => tile.getAttribute('data-provider-tile')))
      .toEqual(['google', 'outlook', 'custom-new']);

    wrapper.unmount();
  });

  it('names a custom service on its own step, with the exact redirect URI, then signs in through it', async () => {
    const wrapper = await mountConnections();
    await toAppStep('custom-new');

    expect(step()).toBe('custom');
    expect(bodyFind('.connect-steps')!.textContent).toContain('Name the service');
    expect(bodyFind('[data-connect-step="custom"] [data-redirect-uri]')!.textContent).toBe('https://instance.example.test/api/connections/callback');
    expect(bodyFind('[data-connect-step="custom"] [data-redirect-warning]')).toBeNull();
    expect(isDisabled('Save and continue')).toBe(true);

    await type('Service name', 'Acme ID');
    await type('Authorization URL', 'https://id.acme.example.test/authorize');
    await type('Token URL', 'https://id.acme.example.test/token');
    await type('Userinfo URL (optional)', 'https://id.acme.example.test/userinfo');
    await type('Default scopes', 'openid, profile');
    await type('Client ID', 'acme-client');
    await type('Client secret', 'acme-secret-value');
    const saved = hostProvider({
      id: 'custom-acme-id',
      name: 'Acme ID',
      clientId: 'acme-client',
      clientSecretSet: true,
      configured: true,
      authorizeUrl: 'https://id.acme.example.test/authorize',
      tokenUrl: 'https://id.acme.example.test/token',
      userinfoUrl: 'https://id.acme.example.test/userinfo',
      defaultScopes: ['openid', 'profile'],
    });
    saveConnectionProvider.mockResolvedValue(saved);
    await click(button('Save and continue'));

    expect(saveConnectionProvider).toHaveBeenCalledWith('custom-acme-id', {
      clientId: 'acme-client',
      clientSecret: 'acme-secret-value',
      name: 'Acme ID',
      authorizeUrl: 'https://id.acme.example.test/authorize',
      tokenUrl: 'https://id.acme.example.test/token',
      userinfoUrl: 'https://id.acme.example.test/userinfo',
      defaultScopes: ['openid', 'profile'],
    });
    expect(step()).toBe('signin');

    // A scope no plugin asks for is added on the sign-in step.
    await type('More scopes (optional)', 'acme.read acme.write');
    await click(button('Sign in with Acme ID'));
    expect(startConnection).toHaveBeenCalledWith({ provider: 'custom-acme-id', scopes: ['acme.read', 'acme.write'] });
    expect(goTo).toHaveBeenCalledWith(authorizationUrl);

    wrapper.unmount();
  });

  // A service named just now has had no needs read yet: saving it reads them, so a plugin's scopes
  // for it are asked for on its very first sign-in, as they are once it has its own tile.
  it("asks a newly named custom service for the plugins' scopes on its first sign-in", async () => {
    const wrapper = await mountConnections();
    await toAppStep('custom-new');
    await type('Service name', 'Acme');
    await type('Authorization URL', 'https://id.acme.example.test/authorize');
    await type('Token URL', 'https://id.acme.example.test/token');
    await type('Client ID', 'acme-client');
    saveConnectionProvider.mockResolvedValue(hostProvider({
      id: 'custom-acme',
      name: 'Acme',
      clientId: 'acme-client',
      configured: true,
      authorizeUrl: 'https://id.acme.example.test/authorize',
      tokenUrl: 'https://id.acme.example.test/token',
    }));
    getConnectionNeeds.mockResolvedValue({
      provider: 'custom-acme',
      needs: [{ plugin: 'acme-plugin', slot: 'acme', description: null, scopes: ['acme.read'] }],
      scopes: [{ scope: 'acme.read', words: null, plugins: ['acme-plugin'] }],
      apis: [],
    });
    await click(button('Save and continue'));

    expect(getConnectionNeeds).toHaveBeenCalledWith('custom-acme');
    expect(step()).toBe('signin');
    await click(button('Sign in with Acme'));
    expect(startConnection).toHaveBeenCalledWith({ provider: 'custom-acme', scopes: ['acme.read'] });

    wrapper.unmount();
  });

  // A provider checks the redirect URI's spelling when it is registered: a LAN address over http is
  // refused, so the step says so, and how to get one that is accepted, before anyone copies it.
  it('warns on the custom step that a LAN address will be refused, naming localhost on that port', async () => {
    page.origin = 'http://172.31.242.154:8080';
    try {
      const wrapper = await mountConnections();
      await toAppStep('custom-new');

      expect(bodyFind('[data-connect-step="custom"] [data-redirect-uri]')!.textContent).toBe('http://172.31.242.154:8080/api/connections/callback');
      expect(bodyFind('[data-connect-step="custom"] [data-redirect-warning]')!.textContent).toContain('IP address such as 172.31.242.154');
      expect(bodyFind('[data-connect-step="custom"] [data-redirect-warning]')!.textContent).toContain('http://localhost:8080');

      wrapper.unmount();
    } finally {
      page.origin = 'https://instance.example.test';
    }
  });

  it('opens a saved custom service at its step, filled but for the secret, and removes it there', async () => {
    const acme = hostProvider({
      id: 'custom-acme',
      name: 'Acme',
      clientId: 'acme-client',
      clientSecretSet: true,
      configured: true,
      authorizeUrl: 'https://id.acme.example.test/authorize',
      tokenUrl: 'https://id.acme.example.test/token',
      defaultScopes: ['openid'],
    });
    listConnectionProviders.mockResolvedValue([google, microsoft, acme]);
    const wrapper = await mountConnections();
    await toAppStep('custom-acme');

    expect(step()).toBe('custom');
    expect(field('Authorization URL').value).toBe('https://id.acme.example.test/authorize');
    expect(field('Client ID').value).toBe('acme-client');
    expect(field('Client secret').value).toBe('');

    // Saved as it is, an empty secret keeps the stored one: no `clientSecret` key at all.
    saveConnectionProvider.mockResolvedValue(acme);
    await click(button('Save and continue'));
    expect(saveConnectionProvider).toHaveBeenCalledWith('custom-acme', {
      clientId: 'acme-client',
      name: 'Acme',
      authorizeUrl: 'https://id.acme.example.test/authorize',
      tokenUrl: 'https://id.acme.example.test/token',
      defaultScopes: ['openid'],
    });

    await click(button('Back'));
    expect(step()).toBe('custom');
    deleteConnectionProvider.mockResolvedValue(undefined);
    await click(button('Remove this service'));
    expect(deleteConnectionProvider).toHaveBeenCalledWith('custom-acme');
    expect(step()).toBe('service');

    wrapper.unmount();
  });

  it("shows the app a saved Google client is, never fills its secret, and an empty one keeps what is stored", async () => {
    listConnectionProviders.mockResolvedValue([
      { ...google, clientSource: 'saved', guide: { steps: [{ id: 'credentials', title: 'Client ID and secret', text: 'Paste them.', link: null, copy: [] }] } },
      microsoft,
    ]);
    const wrapper = await mountConnections();
    await toAppStep('google');

    expect(step()).toBe('setup');
    expect(bodyFind('[data-app-in-use="saved"]')!.textContent).toContain('123.apps.googleusercontent.com');
    expect(bodyFind('[data-clear-client-button]')!.textContent).toContain('Remove this app');

    await click(bodyFind('[data-own-app]'));
    expect(field('Client ID').value).toBe('');
    expect(field('Client secret').value).toBe('');
    await type('Client ID', '456.apps.googleusercontent.com');
    saveConnectionProvider.mockResolvedValue({ ...google, clientId: '456.apps.googleusercontent.com' });
    await click(button('Save client'));

    // No `clientSecret` key at all: the Host keeps the stored one.
    expect(saveConnectionProvider).toHaveBeenCalledWith('google', { clientId: '456.apps.googleusercontent.com' });
    expect(step()).toBe('signin');

    wrapper.unmount();
  });
});

describe('ConnectionsDialog, on a connection', () => {
  it('Reconnect first says what it will ask for, then asks the Host for that connection and sends the browser to the provider', async () => {
    const wrapper = await mountConnections();

    buttonIn(row('conn-work'), 'Reconnect').click();
    await settle();

    // Nothing has left yet: the dialog says what it will ask for, in words.
    expect(startConnection).not.toHaveBeenCalled();
    expect(getConnectionNeeds).toHaveBeenCalledWith('google');
    expect(bodyFind('[data-reconnect-asks]')!.textContent).toContain('person@example.com');
    const kept = [...bodyFind('[data-reconnect-kept]')!.querySelectorAll('li')].map((line) => line.textContent?.trim());
    expect(kept).toEqual(['Confirm who you are', 'See your email address', 'Read, change, send and permanently delete all your Gmail']);
    expect(bodyFind('[data-reconnect-adds]')).toBeNull();

    (bodyFind('[data-reconnect-continue]') as HTMLElement).click();
    await settle();

    expect(startConnection).toHaveBeenCalledWith({ reconnectId: 'conn-work', scopes: [] });
    expect(goTo).toHaveBeenCalledWith(authorizationUrl);

    wrapper.unmount();
  });

  it("Reconnect says it adds the scopes an installed plugin's slot needs, and asks for them", async () => {
    const calendar = 'https://www.googleapis.com/auth/calendar.events';
    getConnectionNeeds.mockResolvedValue({
      provider: 'google',
      needs: [{ plugin: 'diary', slot: 'calendar', description: null, scopes: ['https://mail.google.com/', calendar] }],
      scopes: [
        { scope: 'https://mail.google.com/', words: 'Read, change, send and permanently delete all your Gmail', plugins: ['diary'] },
        { scope: calendar, words: 'See and change events on your Google calendars', plugins: ['diary'] },
      ],
      apis: [],
    });
    const wrapper = await mountConnections();

    buttonIn(row('conn-work'), 'Reconnect').click();
    await settle();

    expect(bodyFind('[data-reconnect-adds-intro]')!.textContent).toContain('diary needs');
    const adds = [...bodyFind('[data-reconnect-adds]')!.querySelectorAll('li')].map((line) => line.textContent?.trim());
    expect(adds).toEqual(['See and change events on your Google calendars']);

    (bodyFind('[data-reconnect-continue]') as HTMLElement).click();
    await settle();

    expect(startConnection).toHaveBeenCalledWith({ reconnectId: 'conn-work', scopes: [calendar] });

    wrapper.unmount();
  });

  it('back from a Reconnect, says it worked, naming the connection', async () => {
    const wrapper = await mountConnections({ notice: { outcome: 'reconnected', id: 'conn-work' } });

    expect(bodyFind('[data-connections-notice]')!.textContent).toContain('Reconnected Work mail (person@example.com).');

    wrapper.unmount();
  });

  it('back from a Reconnect that failed, says which connection was not reconnected and why', async () => {
    const first = await mountConnections();
    buttonIn(row('conn-work'), 'Reconnect').click();
    await settle();
    (bodyFind('[data-reconnect-continue]') as HTMLElement).click();
    await settle();
    first.unmount();
    resetBody();

    // The provider's answer brings the Console back with the Connections dialog open on it.
    const wrapper = await mountConnections({ notice: { outcome: 'refused', reason: 'The sign-in was cancelled at the provider.' } });

    expect(bodyFind('[data-connections-notice]')!.textContent).toContain(
      'Work mail was not reconnected: The sign-in was cancelled at the provider.',
    );

    wrapper.unmount();
  });

  it('renames it', async () => {
    const wrapper = await mountConnections();

    buttonIn(row('conn-work'), 'Rename').click();
    await settle();
    await type('Name', 'Personal mail');
    renameConnection.mockResolvedValue({ ...workMail, name: 'Personal mail' });
    buttonIn(bodyFind('[data-rename-dialog]')!, 'Rename').click();
    await settle();

    expect(renameConnection).toHaveBeenCalledWith('conn-work', 'Personal mail');
    expect(listConnections).toHaveBeenCalledTimes(2);

    wrapper.unmount();
  });

  it('asks first, naming each member that uses it and saying their runs stop, then deletes it and unbinds them', async () => {
    const wrapper = await mountConnections();
    disconnectConnection.mockResolvedValue(undefined);

    buttonIn(row('conn-work'), 'Delete').click();
    await settle();

    const users = bodyFind('[data-disconnect-users]')!.textContent;
    expect(users).toContain('mail-team / Inbox (mail)');
    expect(users).toContain('Their runs stop until another connection is chosen for them.');
    expect(disconnectConnection).not.toHaveBeenCalled();

    buttonIn(bodyFind('[data-disconnect-dialog]')!, 'Delete and unbind').click();
    await settle();

    expect(disconnectConnection).toHaveBeenCalledWith('conn-work', true);
    expect(bodyFind('[data-disconnect-dialog]')).toBeNull();

    wrapper.unmount();
  });

  it("lists a member the Host names when one was bound since the list was read, and confirming then unbinds it", async () => {
    const wrapper = await mountConnections();

    disconnectConnection.mockRejectedValueOnce(
      Object.assign(
        new ActionRefused(
          "Connection 'Outlook' is used by mail-team/Inbox (slot mail). Deleting it stops their runs until another is chosen; confirm to delete it and unbind them.",
          {
            error: "Connection 'Outlook' is used by mail-team/Inbox (slot mail). Deleting it stops their runs until another is chosen; confirm to delete it and unbind them.",
            usedBy: workMail.usedBy,
          },
        ),
        { status: 409 },
      ),
    );

    buttonIn(row('conn-outlook'), 'Delete').click();
    await settle();
    expect(bodyFind('[data-disconnect-users]')).toBeNull();
    buttonIn(bodyFind('[data-disconnect-dialog]')!, 'Delete').click();
    await settle();

    expect(disconnectConnection).toHaveBeenCalledWith('conn-outlook', false);
    expect(bodyFind('[data-disconnect-refusal]')!.textContent).toContain('is used by mail-team/Inbox (slot mail)');
    expect(bodyFind('[data-disconnect-users]')!.textContent).toContain('mail-team / Inbox (mail)');

    disconnectConnection.mockResolvedValueOnce(undefined);
    buttonIn(bodyFind('[data-disconnect-dialog]')!, 'Delete and unbind').click();
    await settle();
    expect(disconnectConnection).toHaveBeenLastCalledWith('conn-outlook', true);

    wrapper.unmount();
  });

  it('deletes one no member uses', async () => {
    const wrapper = await mountConnections();

    disconnectConnection.mockResolvedValue(undefined);
    buttonIn(row('conn-outlook'), 'Delete').click();
    await settle();
    buttonIn(bodyFind('[data-disconnect-dialog]')!, 'Delete').click();
    await settle();

    expect(disconnectConnection).toHaveBeenCalledWith('conn-outlook', false);
    expect(bodyFind('[data-disconnect-refusal]')).toBeNull();

    wrapper.unmount();
  });
});
