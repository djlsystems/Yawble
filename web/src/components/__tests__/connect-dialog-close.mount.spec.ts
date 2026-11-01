// @vitest-environment happy-dom
//
// THE CONNECTION WIZARD'S CLOSE (X) ANSWERS A CLICK ON THE WHOLE BUTTON. Quasar's q-gutter-y-* pulls
// its container up by the gutter with a negative margin, and the kind and credential steps' sections
// carried it straight under the header: they lay over the X, so a click at its centre hit the section
// and closed nothing. No section of the wizard pulls itself up any more, on any step; and once
// something is typed in the website form - where a click on the backdrop no longer closes it - the X
// is still the way out, and the only one besides Cancel's own steps.
//
// happy-dom lays nothing out, so what is asserted here is the cause (no negative-margin gutter on a
// step's section) and the click; the browser check at 1440 and 390 measures the X itself.
//
// THE MOCK IS OF `api/client`. No request leaves the test.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

const { listConnections, listConnectionProviders, listOpenConnectionFlows, listMailboxPresets, listCredentialPresets, getConnectionNeeds } = vi.hoisted(() => ({
  listConnections: vi.fn(),
  listConnectionProviders: vi.fn(),
  listOpenConnectionFlows: vi.fn(),
  listMailboxPresets: vi.fn(),
  listCredentialPresets: vi.fn(),
  getConnectionNeeds: vi.fn(),
}));

vi.mock('../../api/client', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  listConnections,
  listConnectionProviders,
  listOpenConnectionFlows,
  listMailboxPresets,
  listCredentialPresets,
  getConnectionNeeds,
}));

vi.mock('../../lib/browserNavigation', () => ({
  goTo: vi.fn(),
  currentOrigin: () => 'https://instance.example.test',
}));

import ConnectionsDialog from '../ConnectionsDialog.vue';
import type { CredentialPreset } from '../../api/types';
import { bodyFind, mountDialog, resetBody } from '../../test/mountQuasar';
import { hostProvider } from '../../test/pluginFixtures';
import { button, settle, type } from '../../test/formProbe';

const presets: CredentialPreset[] = [
  {
    kind: 'website', id: null, name: 'Website', namedFields: false, test: true,
    fields: [
      { name: 'siteUrl', label: 'Site URL', masked: false, required: true, type: 'url' },
      { name: 'method', label: 'Method', masked: false, required: true, type: 'enum', options: ['password', 'token', 'interactive'] },
    ],
  },
  { kind: 'login', id: null, name: 'Username and password', namedFields: false, test: false, fields: [{ name: 'username', label: 'Username', masked: false, required: true }] },
];

beforeEach(() => {
  for (const mock of [listConnections, listConnectionProviders, listOpenConnectionFlows, listMailboxPresets, listCredentialPresets, getConnectionNeeds]) mock.mockReset();
  listConnections.mockResolvedValue([]);
  listConnectionProviders.mockResolvedValue([
    hostProvider({ id: 'google', configured: true, clientSource: 'saved', clientId: '1.apps.googleusercontent.com' }),
    hostProvider({ id: 'microsoft', configured: true, clientSource: 'default' }),
  ]);
  listOpenConnectionFlows.mockResolvedValue([]);
  listMailboxPresets.mockResolvedValue([]);
  listCredentialPresets.mockResolvedValue(presets);
  getConnectionNeeds.mockResolvedValue({ provider: 'google', needs: [], scopes: [], apis: [] });
});

afterEach(resetBody);

async function click(element: Element | null) {
  if (!element) throw new Error('nothing to click');
  (element as HTMLElement).click();
  await settle();
}

const wizard = () => bodyFind('[data-guided-connect]');
const step = () => bodyFind('[data-connect-step]')?.getAttribute('data-connect-step') ?? null;
const close = () => wizard()!.querySelector<HTMLElement>('button[aria-label="Close"]');

/** Every section of the wizard that pulls itself up over what is above it: none, on every step. */
function pulledUp() {
  return [...wizard()!.querySelectorAll('.q-card__section')]
    .filter((section) => [...section.classList].some((name) => /^q-gutter-(y-|)(xs|sm|md|lg|xl)$/.test(name)))
    .map((section) => section.getAttribute('data-connect-step') ?? section.className);
}

async function openWizard() {
  const wrapper = await mountDialog(ConnectionsDialog, {});
  await settle();
  await click(button('Add connection'));
  return wrapper;
}

describe("Add connection's Close (X)", () => {
  it('has no section pulled up over the header on the kind step, and its click closes the wizard', async () => {
    const wrapper = await openWizard();
    expect(step()).toBe('kind');
    expect(pulledUp()).toEqual([]);

    await click(close());
    expect(wizard()).toBeNull();

    wrapper.unmount();
  });

  it('has no section pulled up on the credential step, and closes the web-address wizard once something is typed', async () => {
    const wrapper = await openWizard();
    await click(bodyFind('[data-connection-kind="website"]'));
    expect(step()).toBe('credential');
    expect(pulledUp()).toEqual([]);

    await type('Name', 'Supplier portal');
    await type('Site URL', 'supplier.example.test');
    // Typed: a stray click on the backdrop no longer closes it, and no other way out was added.
    const backdrops = document.body.querySelectorAll('.q-dialog__backdrop');
    backdrops[backdrops.length - 1]!.dispatchEvent(new MouseEvent('mousedown', { button: 0, bubbles: true }));
    await settle();
    expect(wizard()).not.toBeNull();
    expect([...wizard()!.querySelectorAll('.q-card__actions button')].map((candidate) => candidate.textContent?.trim())).toEqual(['Back', 'Save']);

    await click(close());
    expect(wizard()).toBeNull();

    wrapper.unmount();
  });

  it('has no section pulled up on the service, app and sign-in steps either', async () => {
    const wrapper = await openWizard();
    await click(bodyFind('[data-connection-kind="oauth"]'));
    await click(bodyFind('[data-connect-service="google"]'));
    expect(step()).toBe('service');
    expect(pulledUp()).toEqual([]);

    await click(button('Next'));
    expect(step()).toBe('setup');
    expect(pulledUp()).toEqual([]);

    await click(button('Next'));
    expect(step()).toBe('signin');
    expect(pulledUp()).toEqual([]);

    await click(close());
    expect(wizard()).toBeNull();

    wrapper.unmount();
  });
});
