// @vitest-environment happy-dom
//
// ADD CONNECTION STAYS OPEN WHILE A PERSON TYPES. Two things closed it under them, with what they
// had typed: a press on the backdrop (on a Mac, a palm or a tap on the trackpad mid-word), and a
// route change - Quasar closes every dialog when the route changes, and the Console changes its
// address under open dialogs. Once anything is typed only Close closes it; no route change closes it,
// typed or not.
//
// THE MOCK IS OF `api/client`. No request leaves the test; the router is an in-memory one.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createMemoryHistory, createRouter, type Router } from 'vue-router';

const { listConnections, listConnectionProviders, listOpenConnectionFlows, listMailboxPresets } = vi.hoisted(() => ({
  listConnections: vi.fn(),
  listConnectionProviders: vi.fn(),
  listOpenConnectionFlows: vi.fn(),
  listMailboxPresets: vi.fn(),
}));

vi.mock('../../api/client', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  listConnections,
  listConnectionProviders,
  listOpenConnectionFlows,
  listMailboxPresets,
}));

vi.mock('../../lib/browserNavigation', () => ({
  goTo: vi.fn(),
  currentOrigin: () => 'https://instance.example.test',
}));

import ConnectionsDialog from '../ConnectionsDialog.vue';
import type { MailboxPreset } from '../../api/types';
import { bodyFind, mountDialog, resetBody } from '../../test/mountQuasar';
import { hostConnection, hostProvider } from '../../test/pluginFixtures';
import { button, field, settle, type } from '../../test/formProbe';

const presets: MailboxPreset[] = [
  { id: 'gmail', name: 'Gmail', imap: { host: 'imap.gmail.com', port: 993, security: 'TLS' }, smtp: { host: 'smtp.gmail.com', port: 465, security: 'TLS' } },
  { id: 'other', name: 'Other', imap: null, smtp: null },
];

let router: Router;

beforeEach(async () => {
  for (const mock of [listConnections, listConnectionProviders, listOpenConnectionFlows, listMailboxPresets]) mock.mockReset();
  listConnections.mockResolvedValue([]);
  listConnectionProviders.mockResolvedValue([hostProvider({ id: 'google' }), hostProvider({ id: 'microsoft' })]);
  listOpenConnectionFlows.mockResolvedValue([]);
  listMailboxPresets.mockResolvedValue(presets);

  router = createRouter({ history: createMemoryHistory(), routes: [{ path: '/:any(.*)', component: { template: '<div />' } }] });
  await router.push('/console');
});

afterEach(resetBody);

/** Admin, Connections, then Add connection, Gmail, with an address and part of the app password typed. */
async function typing() {
  const wrapper = await mountDialog(ConnectionsDialog, {}, { global: { plugins: [router] } });
  await settle();
  button('Add connection').click();
  await settle();
  bodyFind('[data-mailbox-preset="gmail"]')!.click();
  await settle();
  await type('Email address', 'me@gmail.com');
  await type('App password', 'abcd efgh');
  return wrapper;
}

/** The topmost backdrop: Add connection's, over Connections. */
function pressBackdrop() {
  const backdrops = document.body.querySelectorAll('.q-dialog__backdrop');
  backdrops[backdrops.length - 1]!.dispatchEvent(new MouseEvent('mousedown', { button: 0, bubbles: true }));
}

describe('Add connection, while a person types', () => {
  it('stays open, with what was typed, when the backdrop is pressed', async () => {
    const wrapper = await typing();

    pressBackdrop();
    await settle();

    expect(bodyFind('[data-guided-connect]')).not.toBeNull();
    expect(field('Email address').value).toBe('me@gmail.com');
    expect(field('App password').value).toBe('abcd efgh');

    wrapper.unmount();
  });

  it('stays open, with what was typed, when the route changes under it', async () => {
    const wrapper = await typing();

    await router.replace({ path: '/console', query: { team: 'another' } });
    await settle();

    expect(bodyFind('[data-guided-connect]')).not.toBeNull();
    expect(bodyFind('[data-connections-dialog]')).not.toBeNull();
    expect(field('Email address').value).toBe('me@gmail.com');
    expect(field('App password').value).toBe('abcd efgh');

    wrapper.unmount();
  });

  it('stays open on a route change before anything is typed', async () => {
    const wrapper = await mountDialog(ConnectionsDialog, {}, { global: { plugins: [router] } });
    await settle();
    button('Add connection').click();
    await settle();
    bodyFind('[data-mailbox-preset="gmail"]')!.click();
    await settle();
    expect(bodyFind('[data-guided-connect]')).not.toBeNull();

    await router.replace({ path: '/console', query: { team: 'another' } });
    await settle();

    // Nothing typed, so not persistent: only its own no-route-dismiss keeps it, at the tile chosen.
    expect(bodyFind('[data-guided-connect]')).not.toBeNull();
    expect(field('Email address').value).toBe('');

    wrapper.unmount();
  });

  it('still closes on a backdrop press before anything is typed', async () => {
    const wrapper = await mountDialog(ConnectionsDialog, {}, { global: { plugins: [router] } });
    await settle();
    button('Add connection').click();
    await settle();

    pressBackdrop();
    await settle();

    expect(bodyFind('[data-guided-connect]')).toBeNull();

    wrapper.unmount();
  });
});

describe('Connections, back from the provider', () => {
  it('stays open with its message when the Console takes the answer off the address', async () => {
    listConnections.mockResolvedValue([hostConnection({ id: 'conn-work', provider: 'google', name: 'Work mail', account: 'person@example.com' })]);
    await router.push('/console?connection=reconnected&id=conn-work');
    const wrapper = await mountDialog(
      ConnectionsDialog,
      { notice: { outcome: 'reconnected', id: 'conn-work' } },
      { global: { plugins: [router] } },
    );
    await settle();

    // What the Console does once it has opened the dialog with the answer.
    await router.replace({ query: {} });
    await settle();

    expect(bodyFind('[data-connections-dialog]')).not.toBeNull();
    expect(bodyFind('[data-connections-notice]')!.textContent).toContain('Reconnected Work mail (person@example.com).');

    wrapper.unmount();
  });
});
