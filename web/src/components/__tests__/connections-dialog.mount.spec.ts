// @vitest-environment happy-dom
//
// ADMIN > CONNECTIONS. Each connection with its provider, account, scopes, when connected and last
// refreshed, its status (ok / needs reconnect with the provider's reason) and the members using it;
// Connect an account (a provider - or its client set up first - and scopes), which sends the browser
// to the provider with the exact redirect URI for this address shown beforehand; Reconnect, Rename,
// and Disconnect with the Host's refusal naming the members. The client secret is write-only: the
// dialog says "set" and never holds a value.
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
  goTo,
} = vi.hoisted(() => ({
  listConnections: vi.fn(),
  listConnectionProviders: vi.fn(),
  startConnection: vi.fn(),
  renameConnection: vi.fn(),
  disconnectConnection: vi.fn(),
  saveConnectionProvider: vi.fn(),
  deleteConnectionProvider: vi.fn(),
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
  scopes: ['openid', 'email', 'https://mail.google.com/'],
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
    goTo,
  ]) {
    mock.mockReset();
  }
  listConnections.mockResolvedValue([workMail, outlook]);
  listConnectionProviders.mockResolvedValue([google, microsoft]);
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
    expect(work.querySelector('[data-connection-scopes]')!.textContent).toContain('https://mail.google.com/');
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

  it('shows the connections and the providers on two tabs, each as tiles', async () => {
    const wrapper = await mountConnections();

    expect(bodyFind('[data-connections-tab="connections"]')!.textContent).toContain('Connections (2)');
    expect(bodyFind('[data-connections-tab="providers"]')!.textContent).toContain('Providers (2)');
    expect(bodyFind('[data-connection="conn-work"]')).not.toBeNull();
    expect(bodyFind('[data-provider="google"]')).toBeNull();

    await showTab('providers');
    expect(bodyFind('[data-provider="google"]')).not.toBeNull();
    expect(bodyFind('[data-provider="google"] [data-provider-connections]')!.textContent).toBe('1 account');
    expect(bodyFind('[data-connection="conn-work"]')).toBeNull();

    wrapper.unmount();
  });

  it('shows a provider client secret only as "set", never a value', async () => {
    const wrapper = await mountConnections();
    await showTab('providers');

    expect(bodyFind('[data-provider="google"] [data-client-secret]')!.textContent).toBe('set');
    expect(bodyFind('[data-provider="google"] [data-client-id]')!.textContent).toBe('123.apps.googleusercontent.com');
    expect(bodyFind('[data-provider="microsoft"] [data-client-secret]')!.textContent).toBe('not set');
    expect(bodyFind('[data-provider="microsoft"] [data-client-state]')!.textContent).toBe('Client not set up');

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

describe('ConnectionsDialog, connecting an account', () => {
  it('shows the exact redirect URI for this address and the provider help, then sends the browser to the provider', async () => {
    const wrapper = await mountConnections();

    button('Connect an account…').click();
    await settle();

    expect(bodyFind('[data-redirect-uri]')!.textContent).toBe('https://instance.example.test/api/connections/callback');
    expect(bodyFind('[data-provider-help]')!.textContent).toContain('"Desktop app" client');
    expect(bodyFind('[data-redirect-warning]')).toBeNull();

    await type('Scopes', 'https://mail.google.com/, https://www.googleapis.com/auth/drive.readonly');
    await type('Name', 'Work mail');
    button('Connect').click();
    await settle();

    expect(startConnection).toHaveBeenCalledWith({
      provider: 'google',
      scopes: ['https://mail.google.com/', 'https://www.googleapis.com/auth/drive.readonly'],
      name: 'Work mail',
    });
    expect(goTo).toHaveBeenCalledWith(authorizationUrl);

    wrapper.unmount();
  });

  // A provider checks the redirect URI's spelling when it is registered: a LAN address over http is
  // refused, so the dialog says so, and how to get one that is accepted, before anyone copies it.
  it('warns that a LAN address will be refused and names localhost on that port, in Connect and in Set up client', async () => {
    page.origin = 'http://172.31.242.154:8080';
    try {
      const wrapper = await mountConnections();

      button('Connect an account…').click();
      await settle();
      expect(bodyFind('[data-connect-dialog] [data-redirect-uri]')!.textContent).toBe('http://172.31.242.154:8080/api/connections/callback');
      expect(bodyFind('[data-connect-dialog] [data-redirect-warning]')!.textContent).toContain('IP address such as 172.31.242.154');
      expect(bodyFind('[data-connect-dialog] [data-redirect-warning]')!.textContent).toContain('http://localhost:8080');
      button('Cancel').click();
      await settle();

      await showTab('providers');
      buttonIn(bodyFind('[data-provider="google"]')!, 'Set up client…').click();
      await settle();
      expect(bodyFind('[data-client-dialog] [data-redirect-warning]')!.textContent).toContain('http://localhost:8080');

      wrapper.unmount();
    } finally {
      page.origin = 'https://instance.example.test';
    }
  });

  it('holds Connect for a provider whose client is not set up, and sets it up first', async () => {
    const wrapper = await mountConnections();

    button('Connect an account…').click();
    await settle();
    select(wrapper, 'Provider').vm.$emit('update:modelValue', 'microsoft');
    await settle();

    expect(bodyFind('[data-needs-client]')!.textContent).toContain('Its client is not set up yet.');
    expect(isDisabled('Connect')).toBe(true);

    buttonIn(bodyFind('[data-needs-client]')!, 'Set up client…').click();
    await settle();

    expect(bodyText()).toContain('Microsoft client');
    await type('Client ID', 'app-id-1');
    await type('Client secret', 'the-secret-value');
    await type('Tenant', 'contoso.onmicrosoft.com');
    saveConnectionProvider.mockResolvedValue({ ...microsoft, clientId: 'app-id-1', clientSecretSet: true, configured: true });
    button('Save client').click();
    await settle();

    expect(saveConnectionProvider).toHaveBeenCalledWith('microsoft', {
      clientId: 'app-id-1',
      clientSecret: 'the-secret-value',
      tenant: 'contoso.onmicrosoft.com',
    });

    wrapper.unmount();
  });

  it('never fills the client secret, and an empty one keeps what is stored', async () => {
    const wrapper = await mountConnections();
    await showTab('providers');

    buttonIn(bodyFind('[data-provider="google"]')!, 'Set up client…').click();
    await settle();

    expect(field('Client secret').value).toBe('');
    expect(bodyFind('[data-secret-set]')!.textContent).toBe('set');

    saveConnectionProvider.mockResolvedValue(google);
    button('Save client').click();
    await settle();

    // No `clientSecret` key at all: the Host keeps the stored one.
    expect(saveConnectionProvider).toHaveBeenCalledWith('google', { clientId: '123.apps.googleusercontent.com' });

    wrapper.unmount();
  });
});

describe('ConnectionsDialog, on a connection', () => {
  it('Reconnect asks the Host for that connection and sends the browser to the provider', async () => {
    const wrapper = await mountConnections();

    buttonIn(row('conn-outlook'), 'Reconnect').click();
    await settle();

    expect(startConnection).toHaveBeenCalledWith({ reconnectId: 'conn-outlook', scopes: [] });
    expect(goTo).toHaveBeenCalledWith(authorizationUrl);

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

  it("shows the Host's refusal to disconnect, naming the members that use it", async () => {
    const wrapper = await mountConnections();

    disconnectConnection.mockRejectedValue(
      Object.assign(
        new ActionRefused(
          "Connection 'Work mail' is used by mail-team/Inbox (slot mail). Unbind it from those members first.",
          {
            error: "Connection 'Work mail' is used by mail-team/Inbox (slot mail). Unbind it from those members first.",
            usedBy: workMail.usedBy,
          },
        ),
        { status: 409 },
      ),
    );

    buttonIn(row('conn-work'), 'Disconnect').click();
    await settle();
    buttonIn(bodyFind('[data-disconnect-dialog]')!, 'Disconnect').click();
    await settle();

    const refusal = bodyFind('[data-disconnect-refusal]')!.textContent;
    expect(refusal).toContain("Connection 'Work mail' is used by mail-team/Inbox (slot mail).");
    expect(refusal).toContain('mail-team / Inbox (mail)');
    expect(disconnectConnection).toHaveBeenCalledWith('conn-work');

    wrapper.unmount();
  });

  it('disconnects one no member uses', async () => {
    const wrapper = await mountConnections();

    disconnectConnection.mockResolvedValue(undefined);
    buttonIn(row('conn-outlook'), 'Disconnect').click();
    await settle();
    buttonIn(bodyFind('[data-disconnect-dialog]')!, 'Disconnect').click();
    await settle();

    expect(disconnectConnection).toHaveBeenCalledWith('conn-outlook');
    expect(bodyFind('[data-disconnect-refusal]')).toBeNull();

    wrapper.unmount();
  });
});
