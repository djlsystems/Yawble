// @vitest-environment happy-dom
//
// TEST CONNECTION: Add connection's saved step, the Connections tile and its Edit screen each offer
// Test connection for a connection the Host says has a test (`testable`), and none for one that has
// not - a website, a preset with nothing to try, a username and password with no URL. The answer is
// one sentence: ok, or Needs you with the sentence, which the tile then shows as the connection's
// health. Nothing typed is sent; a masked value is never shown.
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
  testConnection,
} = vi.hoisted(() => ({
  listConnections: vi.fn(),
  listConnectionProviders: vi.fn(),
  listOpenConnectionFlows: vi.fn(),
  listMailboxPresets: vi.fn(),
  listCredentialPresets: vi.fn(),
  addCredentialConnection: vi.fn(),
  testConnection: vi.fn(),
}));

vi.mock('../../api/client', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  listConnections,
  listConnectionProviders,
  listOpenConnectionFlows,
  listMailboxPresets,
  listCredentialPresets,
  addCredentialConnection,
  testConnection,
}));

vi.mock('../../lib/browserNavigation', () => ({
  goTo: vi.fn(),
  currentOrigin: () => 'https://instance.example.test',
}));

import ConnectionsDialog from '../ConnectionsDialog.vue';
import type { Connection, CredentialPreset } from '../../api/types';
import { bodyFind, mountDialog, resetBody } from '../../test/mountQuasar';
import { hostConnection, hostProvider } from '../../test/pluginFixtures';
import { button, settle, type } from '../../test/formProbe';

// One API key preset: the form opens on it, with no tiles to choose from.
const presets: CredentialPreset[] = [
  { kind: 'apikey', id: 'stripe', name: 'Stripe', namedFields: false, test: true, fields: [{ name: 'key', label: 'Secret key', masked: true, required: true }] },
  { kind: 'website', id: null, name: 'Website', namedFields: false, test: false, fields: [] },
];

const stripeKey = 'sk_live_never-shown-0001';

const stripe: Connection = hostConnection({
  id: 'conn-stripe', name: 'Stripe live', provider: 'apikey', providerKind: 'apikey', kind: 'apikey', account: 'Stripe', scopes: [],
  preset: 'stripe', testable: true, fields: [{ name: 'key', label: 'Secret key', masked: true, value: null, set: true }],
});

const refused: Connection = { ...stripe, status: 'needs-reconnect', statusReason: 'Stripe refused the secret key (HTTP 401).' };

const shop: Connection = hostConnection({
  id: 'conn-shop', name: 'Shop', provider: 'website', providerKind: 'website', kind: 'website', account: '', scopes: [],
  preset: null, testable: false, fields: [],
});

const router: Connection = hostConnection({
  id: 'conn-router', name: 'Router', provider: 'login', providerKind: 'login', kind: 'login', account: 'admin', scopes: [], preset: null,
  testable: false,
  fields: [
    { name: 'username', label: 'Username', masked: false, value: 'admin', set: true },
    { name: 'password', label: 'Password', masked: true, value: null, set: true },
    { name: 'url', label: 'URL', masked: false, value: null, set: false },
  ],
});

beforeEach(() => {
  for (const mock of [listConnections, listConnectionProviders, listOpenConnectionFlows, listMailboxPresets, listCredentialPresets,
    addCredentialConnection, testConnection]) {
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

describe('Add connection, once saved', () => {
  it('offers Test connection for a Stripe key, and says ok or Needs you in one sentence', async () => {
    addCredentialConnection.mockResolvedValue({ connection: stripe });
    const wrapper = await mountDialog(ConnectionsDialog);
    await settle();
    await click(button('Add connection'));
    await click(bodyFind('[data-connection-kind="apikey"]'));
    expect(bodyFind('[data-connection-test]')).toBeNull();

    await type('Name', 'Stripe live');
    await type('Secret key', stripeKey);
    await click(button('Save'));

    testConnection.mockResolvedValueOnce({ health: 'ok', sentence: 'Connected: Stripe accepted the secret key.', connection: stripe });
    await click(bodyFind('[data-connection-test-run]'));
    expect(testConnection).toHaveBeenCalledWith('conn-stripe');
    expect(bodyFind('[data-connection-test-result="ok"]')!.textContent?.trim()).toBe('Connected: Stripe accepted the secret key.');

    testConnection.mockResolvedValueOnce({ health: 'needs-reconnect', sentence: 'Stripe refused the secret key (HTTP 401).', connection: refused });
    await click(bodyFind('[data-connection-test-run]'));
    expect(bodyFind('[data-connection-test-result="needs-you"]')!.textContent?.trim()).toBe('Needs you: Stripe refused the secret key (HTTP 401).');
    expect(bodyFind('[data-connection-test-result="ok"]')).toBeNull();
    expect(document.body.innerHTML).not.toContain(stripeKey);

    wrapper.unmount();
  });

  it('offers none for a website, which has no test yet', async () => {
    addCredentialConnection.mockResolvedValue({ connection: shop });
    const wrapper = await mountDialog(ConnectionsDialog);
    await settle();
    await click(button('Add connection'));
    await click(bodyFind('[data-connection-kind="website"]'));
    await type('Name', 'Shop');
    await click(button('Save'));

    expect(bodyFind('[data-credential-saved]')).not.toBeNull();
    expect(bodyFind('[data-connection-test]')).toBeNull();
    expect(testConnection).not.toHaveBeenCalled();

    wrapper.unmount();
  });

  it('says why a test did not run, and records nothing', async () => {
    addCredentialConnection.mockResolvedValue({ connection: stripe });
    testConnection.mockRejectedValue(new Error('The test did not run: No worker is connected. Nothing was recorded; try again.'));
    const wrapper = await mountDialog(ConnectionsDialog);
    await settle();
    await click(button('Add connection'));
    await click(bodyFind('[data-connection-kind="apikey"]'));
    await type('Name', 'Stripe live');
    await type('Secret key', stripeKey);
    await click(button('Save'));
    await click(bodyFind('[data-connection-test-run]'));

    expect(bodyFind('[data-connection-test-problem]')!.textContent).toContain('No worker is connected');
    expect(bodyFind('[data-connection-test-result]')).toBeNull();

    wrapper.unmount();
  });
});

describe('Admin > Connections', () => {
  it('has Test connection on a tile with a test only, and a refusal becomes the tile\'s health', async () => {
    listConnections.mockResolvedValue([stripe, shop, router]);
    const wrapper = await mountDialog(ConnectionsDialog);
    await settle();

    expect(bodyFind('[data-connection="conn-stripe"] [data-connection-test-row]')).not.toBeNull();
    expect(bodyFind('[data-connection="conn-shop"] [data-connection-test-row]')).toBeNull();
    expect(bodyFind('[data-connection="conn-router"] [data-connection-test-row]')).toBeNull();

    testConnection.mockResolvedValueOnce({ health: 'needs-reconnect', sentence: refused.statusReason, connection: refused });
    listConnections.mockResolvedValue([refused, shop, router]);
    await click(bodyFind('button[aria-label="Test connection Stripe live"]'));

    expect(testConnection).toHaveBeenCalledWith('conn-stripe');
    expect(bodyFind('[data-connection="conn-stripe"] [data-connection-reason]')!.textContent?.replace(/\s+/g, ' ').trim())
      .toBe('Stripe refused the secret key (HTTP 401). Edit it, then test it again, to clear this.');

    // RIGHT AGAIN: ok is said on the tile, and the health is ok.
    testConnection.mockResolvedValueOnce({ health: 'ok', sentence: 'Connected: Stripe accepted the secret key.', connection: stripe });
    listConnections.mockResolvedValue([stripe, shop, router]);
    await click(bodyFind('button[aria-label="Test connection Stripe live"]'));
    expect(bodyFind('[data-connection="conn-stripe"] [data-connection-reason]')).toBeNull();
    expect(bodyFind('[data-connection="conn-stripe"] [data-row-notice]')!.textContent?.trim()).toBe('Connected: Stripe accepted the secret key.');

    wrapper.unmount();
  });

  it('has Test connection on the Edit screen of a connection with a test, and none on one without', async () => {
    listConnections.mockResolvedValue([stripe, router]);
    const wrapper = await mountDialog(ConnectionsDialog);
    await settle();

    await click(bodyFind('[data-connection="conn-stripe"] button[aria-label="Edit Stripe live"]'));
    expect(bodyFind('[data-edit-test] [data-connection-test-run]')).not.toBeNull();
    testConnection.mockResolvedValueOnce({ health: 'ok', sentence: 'Connected: Stripe accepted the secret key.', connection: stripe });
    await click(bodyFind('[data-edit-test] [data-connection-test-run]'));
    expect(bodyFind('[data-edit-test] [data-connection-test-result="ok"]')).not.toBeNull();
    await click(button('Cancel'));

    await click(bodyFind('[data-connection="conn-router"] button[aria-label="Edit Router"]'));
    expect(bodyFind('[data-edit-dialog]')).not.toBeNull();
    expect(bodyFind('[data-edit-test]')).toBeNull();

    wrapper.unmount();
  });
});
