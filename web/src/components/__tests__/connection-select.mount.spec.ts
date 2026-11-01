// @vitest-environment happy-dom
//
// A CONNECTION IS MADE WHEREVER ONE IS ASKED FOR (CSEL 1): ConnectionSelect lists the existing
// connections a place accepts - its kinds, its providers, a slot's providers and the team's - and its
// last entry is always New connection..., which opens the wizard over the dialog the select sits in,
// offering only the kinds the place accepts. On save the new connection is listed and chosen at once
// and `created` says which; Cancel leaves the select and the form as they were. A Google sign-in opens
// in another tab and waits, so nothing typed underneath is lost; a Microsoft one shows its code here.
//
// THE MOCK IS OF `api/client`. No request leaves the test.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { defineComponent, h, ref, type PropType } from 'vue';
import { QCard, QDialog, QInput } from 'quasar';

const {
  listConnections,
  listOpenConnectionFlows,
  listMailboxPresets,
  listCredentialPresets,
  addCredentialConnection,
  getConnectionNeeds,
  startConnection,
  startDeviceConnection,
  getConnectionFlow,
  listConnectionProviders,
  goTo,
} = vi.hoisted(() => ({
  listConnections: vi.fn(),
  listOpenConnectionFlows: vi.fn(),
  listMailboxPresets: vi.fn(),
  listCredentialPresets: vi.fn(),
  addCredentialConnection: vi.fn(),
  getConnectionNeeds: vi.fn(),
  startConnection: vi.fn(),
  startDeviceConnection: vi.fn(),
  getConnectionFlow: vi.fn(),
  listConnectionProviders: vi.fn(),
  goTo: vi.fn(),
}));

vi.mock('../../api/client', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  listConnections,
  listOpenConnectionFlows,
  listMailboxPresets,
  listCredentialPresets,
  addCredentialConnection,
  getConnectionNeeds,
  startConnection,
  startDeviceConnection,
  getConnectionFlow,
  listConnectionProviders,
}));

vi.mock('../../lib/browserNavigation', () => ({
  goTo,
  currentOrigin: () => 'https://instance.example.test',
}));

import ConnectionSelect from '../ConnectionSelect.vue';
import type { Connection, ConnectionKind, ConnectionProvider, ConnectionSlot, CredentialPreset } from '../../api/types';
import { bodyFind, mountDialog, resetBody } from '../../test/mountQuasar';
import { hostConnection, hostProvider, hostSlot } from '../../test/pluginFixtures';
import { button, field, settle, type } from '../../test/formProbe';
import { announceProviderReturn, leaveForProvider } from '../../lib/providerReturn';

const presets: CredentialPreset[] = [
  { kind: 'apikey', id: 'stripe', name: 'Stripe', namedFields: false, fields: [{ name: 'key', label: 'Secret key', masked: true, required: true }] },
  { kind: 'apikey', id: 'other', name: 'Other', namedFields: true, fields: [] },
  {
    kind: 'login', id: null, name: 'Username and password', namedFields: false,
    fields: [
      { name: 'username', label: 'Username', masked: false, required: true },
      { name: 'password', label: 'Password', masked: true, required: true },
    ],
  },
];

const credential = (id: string, name: string, kind: 'apikey' | 'login' | 'website'): Connection =>
  hostConnection({ id, name, provider: kind, providerKind: kind, kind, account: name, scopes: [] });

const stripe = credential('conn-stripe', 'Stripe live', 'apikey');
const router = credential('conn-router', 'Router', 'login');
const gmail = hostConnection({ id: 'conn-gmail', name: 'Work mail', provider: 'google', kind: 'oauth', account: 'me@example.test' });
const outlook = hostConnection({ id: 'conn-outlook', name: 'Office', provider: 'microsoft', kind: 'oauth', account: 'me@example.test' });
const made = { ...credential('conn-made', 'Acme', 'apikey'), fields: [{ name: 'key', label: 'key', masked: true, value: null, set: true }] };

const catalog: ConnectionProvider[] = [
  hostProvider({ id: 'google', configured: true }),
  hostProvider({ id: 'microsoft', clientId: '00000000-0000-0000-0000-00000000c1d0', configured: true }),
];

/**
 * A dialog with something typed in it and a ConnectionSelect: the place asking. It shows what is
 * chosen and what `created` said, so a spec reads them from the page.
 */
const Place = defineComponent({
  props: {
    modelValue: { type: Boolean, default: false },
    connections: { type: Array as PropType<Connection[]>, default: () => [] },
    kinds: { type: Array as PropType<ConnectionKind[]>, default: undefined },
    providers: { type: Array as PropType<string[]>, default: undefined },
    forSlot: { type: Object as PropType<ConnectionSlot>, default: undefined },
    assigned: { type: Array as PropType<string[]>, default: undefined },
    start: { type: Object as PropType<{ kind: ConnectionKind; preset?: string }>, default: undefined },
    initial: { type: String, default: null },
  },
  setup(props) {
    const chosen = ref<string | null>(props.initial);
    const notes = ref('');
    const created = ref<string[]>([]);
    return () =>
      h(QDialog, { modelValue: props.modelValue }, () =>
        h(QCard, { 'data-place': '' }, () => [
          h(QInput, { label: 'Notes', modelValue: notes.value, 'onUpdate:modelValue': (value: string | number | null) => (notes.value = String(value ?? '')) }),
          h(ConnectionSelect, {
            modelValue: chosen.value,
            'onUpdate:modelValue': (value: string | null) => (chosen.value = value),
            onCreated: (connection: Connection) => (created.value = [...created.value, connection.id]),
            connections: props.connections,
            catalog,
            kinds: props.kinds,
            providers: props.providers,
            forSlot: props.forSlot,
            assigned: props.assigned,
            start: props.start,
            label: 'Connection',
            clearable: true,
          }),
          h('div', { 'data-chosen': '' }, chosen.value ?? ''),
          h('div', { 'data-created': '' }, created.value.join(',')),
        ]),
      );
  },
});

type Select = { props: (name: string) => unknown; vm: { $emit: (event: string, value: unknown) => void } };

function select(wrapper: { findAllComponents: (s: { name: string }) => unknown[] }): Select {
  const found = (wrapper.findAllComponents({ name: 'QSelect' }) as Select[]).find((s) => s.props('label') === 'Connection');
  if (!found) throw new Error('no QSelect labelled "Connection"');
  return found;
}

const labels = (wrapper: Parameters<typeof select>[0]) =>
  (select(wrapper).props('options') as { label: string }[]).map((option) => option.label);
const values = (wrapper: Parameters<typeof select>[0]) =>
  (select(wrapper).props('options') as { value: string }[]).map((option) => option.value);

const chosenText = () => bodyFind('[data-chosen]')!.textContent;
const wizard = () => bodyFind('[data-guided-connect]');
const step = () => wizard()?.querySelector('[data-connect-step]')?.getAttribute('data-connect-step');
const kindTiles = () => [...(wizard()?.querySelectorAll('[data-connection-kind]') ?? [])].map((tile) => tile.getAttribute('data-connection-kind'));

async function click(element: Element | null) {
  if (!element) throw new Error('nothing to click');
  (element as HTMLElement).click();
  await settle();
}

async function chooseNew(wrapper: Parameters<typeof select>[0]) {
  select(wrapper).vm.$emit('update:modelValue', '__new_connection__');
  await settle();
  await settle();
}

beforeEach(() => {
  for (const mock of [listConnections, listOpenConnectionFlows, listMailboxPresets, listCredentialPresets, addCredentialConnection, getConnectionNeeds, startConnection, startDeviceConnection, getConnectionFlow, listConnectionProviders, goTo]) {
    mock.mockReset();
  }
  listConnections.mockResolvedValue([stripe, router, gmail, outlook]);
  // The app step reads the guide again for what is ticked: none here, so the provider's own stands.
  listConnectionProviders.mockResolvedValue([]);
  listOpenConnectionFlows.mockResolvedValue([]);
  listMailboxPresets.mockResolvedValue([]);
  listCredentialPresets.mockResolvedValue(presets);
  addCredentialConnection.mockResolvedValue({ connection: made });
  getConnectionNeeds.mockImplementation(async (provider: string) => ({ provider, needs: [], scopes: [], apis: [] }));
  startConnection.mockResolvedValue({
    authorizationUrl: 'https://accounts.google.example.test/o/oauth2/auth?state=s1',
    state: 's1',
    redirectUri: 'http://localhost:8080/api/connections/callback',
    expiresAt: '2026-10-31T10:10:00Z',
  });
  startDeviceConnection.mockResolvedValue({
    flowId: 'flow-1',
    userCode: 'FAKE-CODE',
    verificationUri: 'https://microsoft.example.test/devicelogin',
    expiresAt: '2099-10-31T10:15:00Z',
  });
  getConnectionFlow.mockResolvedValue({ state: 'waiting', sentence: null, connection: null });
});

afterEach(() => {
  sessionStorage.clear();
  resetBody();
});

const all = [stripe, router, gmail, outlook];

describe('ConnectionSelect: what is listed', () => {
  it('lists only the kinds the place accepts, by name, and ends with New connection...', async () => {
    const wrapper = await mountDialog(Place, { connections: all, kinds: ['apikey', 'login'] });

    expect(values(wrapper)).toEqual(['conn-router', 'conn-stripe', '__new_connection__']);
    expect(labels(wrapper).at(-1)).toBe('New connection...');
    expect(labels(wrapper)[0]).toBe('Router - Router (Username and password)');

    wrapper.unmount();
  });

  it('lists only the providers the place accepts', async () => {
    const wrapper = await mountDialog(Place, { connections: all, providers: ['microsoft'] });

    expect(values(wrapper)).toEqual(['conn-outlook', '__new_connection__']);

    wrapper.unmount();
  });

  it("for a slot, lists what ConnectionPicker filters on: the slot's providers, of the team's", async () => {
    const slot = hostSlot({ providers: ['google', 'apikey'], required: true });
    const wrapper = await mountDialog(Place, { connections: all, forSlot: slot, assigned: ['conn-gmail'] });

    expect(values(wrapper)).toEqual(['conn-gmail', '__new_connection__']);

    wrapper.unmount();
  });

  it('with nothing that fits, New connection... is still there, alone', async () => {
    const wrapper = await mountDialog(Place, { connections: [], kinds: ['website'] });

    expect(values(wrapper)).toEqual(['__new_connection__']);

    wrapper.unmount();
  });

  it('keeps the chosen connection listed even when it is not one the place accepts', async () => {
    const wrapper = await mountDialog(Place, { connections: all, kinds: ['apikey'], initial: 'conn-router' });

    expect(values(wrapper)).toEqual(['conn-router', 'conn-stripe', '__new_connection__']);
    expect(select(wrapper).props('modelValue')).toBe('conn-router');

    wrapper.unmount();
  });
});

describe('ConnectionSelect: New connection...', () => {
  it('opens the wizard over the dialog with only the kinds the place accepts, choosing nothing', async () => {
    const wrapper = await mountDialog(Place, { connections: all, kinds: ['apikey', 'login'], initial: 'conn-stripe' });

    await chooseNew(wrapper);

    expect(wizard()).not.toBeNull();
    expect(step()).toBe('kind');
    expect(kindTiles()).toEqual(['apikey', 'login']);
    expect(select(wrapper).props('modelValue')).toBe('conn-stripe');
    expect(bodyFind('[data-place]')).not.toBeNull();

    wrapper.unmount();
  });

  it('a place taking one kind opens at that kind form', async () => {
    const wrapper = await mountDialog(Place, { connections: all, kinds: ['apikey'] });

    await chooseNew(wrapper);

    expect(step()).toBe('credential');
    expect(wizard()!.querySelector('[data-credential-kind]')!.getAttribute('data-credential-kind')).toBe('apikey');

    wrapper.unmount();
  });

  it('opens where `start` says: API key, at Other', async () => {
    const wrapper = await mountDialog(Place, { connections: all, kinds: ['apikey', 'login', 'website'], start: { kind: 'apikey', preset: 'other' } });

    await chooseNew(wrapper);

    expect(step()).toBe('credential');
    expect(wizard()!.querySelector('[data-credential-preset="other"]')!.getAttribute('aria-pressed')).toBe('true');
    // Back reaches the other kinds the place takes.
    await click(button('Back'));
    expect(kindTiles()).toEqual(['website', 'apikey', 'login']);

    wrapper.unmount();
  });

  it('on save, lists and chooses the new connection at once, says created, and leaves the form underneath as it was', async () => {
    const wrapper = await mountDialog(Place, { connections: all, kinds: ['apikey'], initial: 'conn-stripe' });
    await type('Notes', 'half a sentence');

    await chooseNew(wrapper);
    await click(wizard()!.querySelector('[data-credential-preset="other"]'));
    await type('Name', 'Acme');
    await click(button('Add a field'));
    const rowInputs = wizard()!.querySelectorAll<HTMLInputElement>('[data-credential-field="0"] input');
    rowInputs[0]!.value = 'key';
    rowInputs[0]!.dispatchEvent(new Event('input', { bubbles: true }));
    rowInputs[1]!.value = 'secret-never-shown';
    rowInputs[1]!.dispatchEvent(new Event('input', { bubbles: true }));
    await settle();
    await click(wizard()!.querySelector('[data-credential-save]'));
    await settle();

    expect(addCredentialConnection).toHaveBeenCalledTimes(1);
    expect(chosenText()).toBe('conn-made');
    expect(bodyFind('[data-created]')!.textContent).toBe('conn-made');
    // The opener's list has not been read again yet; the select lists it all the same.
    expect(values(wrapper)).toContain('conn-made');
    expect(field('Notes').value).toBe('half a sentence');

    wrapper.unmount();
  });

  it('Cancel in the wizard leaves the select and the form as they were', async () => {
    const wrapper = await mountDialog(Place, { connections: all, kinds: ['apikey', 'login'], initial: 'conn-stripe' });
    await type('Notes', 'still here');

    await chooseNew(wrapper);
    await click(button('Cancel'));
    await settle();

    expect(chosenText()).toBe('conn-stripe');
    expect(select(wrapper).props('modelValue')).toBe('conn-stripe');
    expect(bodyFind('[data-created]')!.textContent).toBe('');
    expect(field('Notes').value).toBe('still here');
    expect(addCredentialConnection).not.toHaveBeenCalled();

    wrapper.unmount();
  });

  it('a Google sign-in opens in another tab and waits; what it makes is chosen, and the page never left', async () => {
    const google = hostConnection({ id: 'conn-google-new', name: 'New mail', provider: 'google', kind: 'oauth', account: 'new@example.test' });
    const wrapper = await mountDialog(Place, { connections: all, kinds: ['oauth'], providers: ['google'] });
    await type('Notes', 'typed before');

    await chooseNew(wrapper);
    expect(step()).toBe('service');
    await click(button('Next'));
    expect(step()).toBe('setup');
    await click(button('Next'));
    expect(step()).toBe('signin');
    await click(button('Sign in with Google'));

    const link = bodyFind('[data-guided-connect] a[data-signin-link]');
    expect(link).not.toBeNull();
    expect(goTo).not.toHaveBeenCalled();

    listConnections.mockResolvedValue([...all, google]);
    const href = link!.getAttribute('href')!;
    const tag = new URLSearchParams(href.slice(href.indexOf('?'))).get('signin')!;
    expect(await leaveForProvider(tag)).toBe('https://accounts.google.example.test/o/oauth2/auth?state=s1');
    expect(await announceProviderReturn({ outcome: 'connected', id: 'conn-google-new' })).toBe(true);
    await new Promise((resolve) => setTimeout(resolve, 20));
    await settle();

    expect(chosenText()).toBe('conn-google-new');
    expect(field('Notes').value).toBe('typed before');
    expect(goTo).not.toHaveBeenCalled();

    wrapper.unmount();
  });

  it('a Microsoft sign-in shows its code in place, never leaving the page', async () => {
    const wrapper = await mountDialog(Place, { connections: all, kinds: ['oauth'], providers: ['microsoft'] });

    await chooseNew(wrapper);
    await click(button('Next'));
    await click(button('Next'));
    await click(button('Sign in with Microsoft'));
    await settle();

    expect(startDeviceConnection).toHaveBeenCalledTimes(1);
    expect(bodyFind('[data-guided-connect] [data-device-code]')!.textContent).toBe('FAKE-CODE');
    expect(startConnection).not.toHaveBeenCalled();
    expect(goTo).not.toHaveBeenCalled();

    wrapper.unmount();
  });
});
