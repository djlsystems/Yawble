// @vitest-environment happy-dom
//
// ADMIN > CONNECTIONS, ONE STORE OF TYPED CREDENTIALS: Add connection chooses a kind first, then only
// that kind's fields. An API key chooses its service (Stripe, Namecheap, Twilio, Other) and shows its
// fields, masked or not as the preset says; Other names its own. A username and password, and a website
// (its name alone for now). Each masked value is write-only: sent once, never filled, never shown. The
// Connections screen names each connection's kind, shows a masked field only as set, and Edit changes
// what is behind the name - an empty masked field keeps its value.
//
// THE MOCK IS OF `api/client`. No request leaves the test.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

const {
  listConnections,
  listConnectionProviders,
  listOpenConnectionFlows,
  listMailboxPresets,
  listCredentialPresets,
  addCredentialConnection,
  editCredentialConnection,
  editMailbox,
} = vi.hoisted(() => ({
  listConnections: vi.fn(),
  listConnectionProviders: vi.fn(),
  listOpenConnectionFlows: vi.fn(),
  listMailboxPresets: vi.fn(),
  listCredentialPresets: vi.fn(),
  addCredentialConnection: vi.fn(),
  editCredentialConnection: vi.fn(),
  editMailbox: vi.fn(),
}));

vi.mock('../../api/client', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  listConnections,
  listConnectionProviders,
  listOpenConnectionFlows,
  listMailboxPresets,
  listCredentialPresets,
  addCredentialConnection,
  editCredentialConnection,
  editMailbox,
}));

vi.mock('../../lib/browserNavigation', () => ({
  goTo: vi.fn(),
  currentOrigin: () => 'https://instance.example.test',
}));

import ConnectionsDialog from '../ConnectionsDialog.vue';
import type { Connection, CredentialPreset } from '../../api/types';
import { bodyFind, mountDialog, resetBody } from '../../test/mountQuasar';
import { hostConnection, hostProvider } from '../../test/pluginFixtures';
import { button, field, isDisabled, settle, type } from '../../test/formProbe';

const presets: CredentialPreset[] = [
  { kind: 'apikey', id: 'stripe', name: 'Stripe', namedFields: false, fields: [{ name: 'key', label: 'Secret key', masked: true, required: true }] },
  {
    kind: 'apikey', id: 'namecheap', name: 'Namecheap', namedFields: false,
    fields: [{ name: 'apiUser', label: 'API user', masked: false, required: true }, { name: 'apiKey', label: 'API key', masked: true, required: true }],
  },
  {
    kind: 'apikey', id: 'twilio', name: 'Twilio', namedFields: false,
    fields: [
      { name: 'accountSid', label: 'Account SID', masked: false, required: true },
      { name: 'apiKeySid', label: 'API key SID', masked: false, required: true },
      { name: 'apiKeySecret', label: 'API key secret', masked: true, required: true },
    ],
  },
  { kind: 'apikey', id: 'other', name: 'Other', namedFields: true, fields: [] },
  {
    kind: 'login', id: null, name: 'Username and password', namedFields: false,
    fields: [
      { name: 'username', label: 'Username', masked: false, required: true },
      { name: 'password', label: 'Password', masked: true, required: true },
      { name: 'url', label: 'URL', masked: false, required: false },
    ],
  },
  { kind: 'website', id: null, name: 'Website', namedFields: false, fields: [] },
];

const stripeKey = 'sk_live_never-shown-0001';

const stripe: Connection = hostConnection({
  id: 'conn-stripe',
  name: 'Stripe live',
  provider: 'apikey',
  providerKind: 'apikey',
  kind: 'apikey',
  account: 'Stripe',
  scopes: [],
  preset: 'stripe',
  fields: [{ name: 'key', label: 'Secret key', masked: true, value: null, set: true }],
  usedBy: [{ team: 'shop', member: 'Billing', label: 'Billing', slot: 'stripe' }],
});

const router: Connection = hostConnection({
  id: 'conn-router',
  name: 'Router',
  provider: 'login',
  providerKind: 'login',
  kind: 'login',
  account: 'admin',
  scopes: [],
  preset: null,
  fields: [
    { name: 'username', label: 'Username', masked: false, value: 'admin', set: true },
    { name: 'password', label: 'Password', masked: true, value: null, set: true },
    { name: 'url', label: 'URL', masked: false, value: null, set: false },
  ],
});

beforeEach(() => {
  for (const mock of [listConnections, listConnectionProviders, listOpenConnectionFlows, listMailboxPresets, listCredentialPresets,
    addCredentialConnection, editCredentialConnection, editMailbox]) {
    mock.mockReset();
  }
  listConnections.mockResolvedValue([]);
  listConnectionProviders.mockResolvedValue([hostProvider({ id: 'google' }), hostProvider({ id: 'microsoft' })]);
  listOpenConnectionFlows.mockResolvedValue([]);
  listMailboxPresets.mockResolvedValue([]);
  listCredentialPresets.mockResolvedValue(presets);
});

afterEach(resetBody);

async function click(element: Element | null) {
  if (!element) throw new Error('nothing to click');
  (element as HTMLElement).click();
  await settle();
}

async function openKind(kind: string) {
  const wrapper = await mountDialog(ConnectionsDialog);
  await settle();
  await click(button('Add connection'));
  await click(bodyFind(`[data-connection-kind="${kind}"]`));
  return wrapper;
}

describe('Add connection, the kinds', () => {
  it('offers sign-in, mailbox, website, API key and username and password, each saying its way in', async () => {
    const wrapper = await mountDialog(ConnectionsDialog);
    await settle();
    await click(button('Add connection'));

    const tiles = [...document.body.querySelectorAll('[data-connection-kind] [data-tile-name]')].map((tile) => tile.textContent?.trim());
    expect(tiles).toEqual(['Sign-in', 'Mailbox', 'Website', 'API key', 'Username and password']);

    wrapper.unmount();
  });

  it('adds a Stripe key: the service first, then only its masked key, sent once and never shown back', async () => {
    addCredentialConnection.mockResolvedValue({ connection: stripe });
    const wrapper = await openKind('apikey');

    expect(bodyFind('[data-connect-step]')!.getAttribute('data-connect-step')).toBe('credential');
    expect([...document.body.querySelectorAll('[data-credential-preset]')].map((tile) => tile.textContent?.trim()))
      .toEqual(['Stripe', 'Namecheap', 'Twilio', 'Other']);
    await click(bodyFind('[data-credential-preset="stripe"]'));

    expect(field('Secret key').getAttribute('type')).toBe('password');
    expect(isDisabled('Save')).toBe(true);
    await type('Name', 'Stripe live');
    await type('Secret key', stripeKey);
    await click(button('Save'));

    expect(addCredentialConnection).toHaveBeenCalledWith({
      kind: 'apikey', preset: 'stripe', name: 'Stripe live', fields: [{ name: 'key', value: stripeKey }],
    });
    expect(bodyFind('[data-credential-saved]')).not.toBeNull();
    expect(document.body.innerHTML).not.toContain(stripeKey);

    wrapper.unmount();
  });

  it("shows Twilio's account SID and key SID in the clear and its secret masked, as the preset says", async () => {
    const wrapper = await openKind('apikey');
    await click(bodyFind('[data-credential-preset="twilio"]'));

    expect(field('Account SID').getAttribute('type')).toBe('text');
    expect(field('API key SID').getAttribute('type')).toBe('text');
    expect(field('API key secret').getAttribute('type')).toBe('password');

    wrapper.unmount();
  });

  it("adds Other's named fields, each masked unless the switch is turned off", async () => {
    addCredentialConnection.mockResolvedValue({ connection: stripe });
    const wrapper = await openKind('apikey');
    await click(bodyFind('[data-credential-preset="other"]'));
    await type('Name', 'Acme');
    await click(button('Add a field'));
    await click(button('Add a field'));

    const rows = [...document.body.querySelectorAll('[data-credential-field]')].map((row) => [...row.querySelectorAll('input')]);
    for (const [input, value] of [[rows[0]![0]!, 'region'], [rows[0]![1]!, 'eu-west'], [rows[1]![0]!, 'token'], [rows[1]![1]!, 'acme-token-0001']] as const) {
      input.value = value;
      input.dispatchEvent(new Event('input', { bubbles: true }));
    }
    await settle();
    await click(document.body.querySelector('[data-credential-field="0"] .q-toggle'));
    await click(button('Save'));

    expect(addCredentialConnection).toHaveBeenCalledWith({
      kind: 'apikey', preset: 'other', name: 'Acme',
      fields: [{ name: 'region', value: 'eu-west', masked: false }, { name: 'token', value: 'acme-token-0001', masked: true }],
    });

    wrapper.unmount();
  });

  it('adds a username and password with no URL, and a website with its name alone', async () => {
    addCredentialConnection.mockResolvedValue({ connection: router });
    const wrapper = await openKind('login');
    expect(field('URL (optional)')).not.toBeNull();
    await type('Name', 'Router');
    await type('Username', 'admin');
    await type('Password', 'router-password-01');
    await click(button('Save'));
    expect(addCredentialConnection).toHaveBeenLastCalledWith({
      kind: 'login', preset: null, name: 'Router',
      fields: [{ name: 'username', value: 'admin' }, { name: 'password', value: 'router-password-01' }, { name: 'url', value: '' }],
    });
    wrapper.unmount();
    resetBody();

    const again = await openKind('website');
    expect(bodyFind('[data-website-note]')).not.toBeNull();
    await type('Name', 'Supplier portal');
    await click(button('Save'));
    expect(addCredentialConnection).toHaveBeenLastCalledWith({ kind: 'website', preset: null, name: 'Supplier portal', fields: [] });
    again.unmount();
  });

  it("says the Host's refusal of a name another connection has, and saves nothing", async () => {
    addCredentialConnection.mockRejectedValue(new Error("Another connection is already called 'Router'. Every connection has its own name: choose another."));
    const wrapper = await openKind('login');
    await type('Name', 'Router');
    await type('Username', 'admin');
    await type('Password', 'router-password-01');
    await click(button('Save'));

    expect(bodyFind('[data-credential-problem]')!.textContent).toContain("Another connection is already called 'Router'");
    expect(bodyFind('[data-credential-saved]')).toBeNull();

    wrapper.unmount();
  });
});

describe('Admin > Connections, a credential connection', () => {
  it('names its kind, shows a masked field only as set, and lists who uses it, read only', async () => {
    listConnections.mockResolvedValue([stripe, router]);
    const wrapper = await mountDialog(ConnectionsDialog);
    await settle();

    const tile = bodyFind('[data-connection="conn-stripe"]')!;
    expect(tile.querySelector('[data-connection-kind]')!.textContent).toContain('API key');
    expect(tile.querySelector('[data-connection-field="key"]')!.textContent?.trim()).toBe('set');
    expect(tile.querySelector('[data-connection-used-by]')!.textContent).toContain('shop / Billing (stripe)');
    expect(tile.querySelector('button[aria-label^="Reconnect"]')).toBeNull();
    expect(bodyFind('[data-connection="conn-router"] [data-connection-field="username"]')!.textContent?.trim()).toBe('admin');

    wrapper.unmount();
  });

  it('edits what is behind the name: an empty masked field keeps its value, a typed one replaces it', async () => {
    listConnections.mockResolvedValue([router]);
    editCredentialConnection.mockResolvedValue({ connection: router });
    const wrapper = await mountDialog(ConnectionsDialog);
    await settle();

    await click(bodyFind('[data-connection="conn-router"] button[aria-label="Edit Router"]'));
    expect(field('Username').value).toBe('admin');
    expect(field('Password').value).toBe('');
    await type('Username', 'root');
    await click(bodyFind('[data-edit-save]'));
    expect(editCredentialConnection).toHaveBeenLastCalledWith('conn-router', [
      { name: 'username', value: 'root' }, { name: 'password', value: null }, { name: 'url', value: '' },
    ]);

    await click(bodyFind('[data-connection="conn-router"] button[aria-label="Edit Router"]'));
    await type('Password', 'router-password-02');
    await click(bodyFind('[data-edit-save]'));
    expect(editCredentialConnection).toHaveBeenLastCalledWith('conn-router', [
      { name: 'username', value: 'admin' }, { name: 'password', value: 'router-password-02' }, { name: 'url', value: '' },
    ]);
    expect(document.body.innerHTML).not.toContain('router-password-02');

    wrapper.unmount();
  });

  it("edits a mailbox's servers and keeps its password when none is typed", async () => {
    const mail = hostConnection({
      id: 'conn-mail', name: 'Work mail', provider: 'imap', providerKind: 'imap', kind: 'imap', account: 'me@example.com',
      username: 'me@example.com', preset: 'other', passwordSet: true, scopes: [],
      imap: { host: 'imap.example.com', port: 993, security: 'TLS' }, smtp: { host: 'smtp.example.com', port: 465, security: 'TLS' },
    });
    listConnections.mockResolvedValue([mail]);
    listMailboxPresets.mockResolvedValue([{ id: 'other', name: 'Other', imap: null, smtp: null }]);
    editMailbox.mockResolvedValue({ sentence: 'Connected: 3 messages in Inbox', connection: mail });
    const wrapper = await mountDialog(ConnectionsDialog);
    await settle();

    await click(bodyFind('[data-connection="conn-mail"] button[aria-label="Edit Work mail"]'));
    expect(field('IMAP server').value).toBe('imap.example.com');
    expect(field('App password').value).toBe('');
    await type('IMAP server', 'imap2.example.com');
    await click(bodyFind('[data-edit-save]'));

    expect(editMailbox).toHaveBeenCalledWith('conn-mail', {
      account: 'me@example.com', username: 'me@example.com',
      imap: { host: 'imap2.example.com', port: 993, security: 'TLS' }, smtp: { host: 'smtp.example.com', port: 465, security: 'TLS' },
    });

    wrapper.unmount();
  });
});
