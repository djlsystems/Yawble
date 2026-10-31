// @vitest-environment happy-dom
//
// THE SLOT PICKER BUILDS ON CONNECTIONSELECT (CSEL 1): its list is ConnectionSelect's - the slot's
// providers, the team's, then New connection... - and New connection... offers only the kinds the slot
// takes. What the wizard makes is bound as before: through the member's settings route where the member
// exists (`bind`, the select choosing nothing itself), or chosen for the save at a hire.
//
// THE MOCK IS OF `api/client`. No request leaves the test.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { flushPromises, mount } from '@vue/test-utils';

const { listOpenConnectionFlows, listMailboxPresets, listCredentialPresets, addCredentialConnection } = vi.hoisted(() => ({
  listOpenConnectionFlows: vi.fn(),
  listMailboxPresets: vi.fn(),
  listCredentialPresets: vi.fn(),
  addCredentialConnection: vi.fn(),
}));

vi.mock('../../api/client', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  listOpenConnectionFlows,
  listMailboxPresets,
  listCredentialPresets,
  addCredentialConnection,
}));

vi.mock('../../lib/browserNavigation', () => ({
  goTo: vi.fn(),
  currentOrigin: () => 'https://instance.example.test',
}));

import { resetBody, bodyFind } from '../../test/mountQuasar';
import ConnectionPicker from '../ConnectionPicker.vue';
import ConnectionSelect from '../ConnectionSelect.vue';
import type { Connection, CredentialPreset } from '../../api/types';
import { hostConnection, hostSlot } from '../../test/pluginFixtures';
import { settle } from '../../test/formProbe';

const presets: CredentialPreset[] = [
  { kind: 'apikey', id: 'stripe', name: 'Stripe', namedFields: false, fields: [{ name: 'key', label: 'Secret key', masked: true, required: true }] },
];

const apiKey = (id: string, name: string): Connection =>
  hostConnection({ id, name, provider: 'apikey', providerKind: 'apikey', kind: 'apikey', account: name, scopes: [] });

const live = apiKey('conn-live', 'Stripe live');
const test = apiKey('conn-test', 'Stripe test');
const gmail = hostConnection({ id: 'conn-gmail', name: 'Work mail', provider: 'google', kind: 'oauth', account: 'me@example.test' });
const made = apiKey('conn-made', 'Stripe new');

const billing = hostSlot({ providers: ['apikey'], required: true, description: 'The Stripe account to bill through.' });

beforeEach(() => {
  for (const mock of [listOpenConnectionFlows, listMailboxPresets, listCredentialPresets, addCredentialConnection]) mock.mockReset();
  listOpenConnectionFlows.mockResolvedValue([]);
  listMailboxPresets.mockResolvedValue([]);
  listCredentialPresets.mockResolvedValue(presets);
  addCredentialConnection.mockResolvedValue({ connection: made });
});

afterEach(resetBody);

function picker(props: Record<string, unknown>) {
  return mount(ConnectionPicker, {
    attachTo: document.body,
    props: { modelValue: '', slotName: 'billing', spec: billing, connections: [live, test, gmail], providers: [], plugin: 'shop', ...props },
  });
}

const wizard = () => bodyFind('[data-guided-connect]');

async function makeOne(wrapper: ReturnType<typeof picker>) {
  wrapper.findComponent(ConnectionSelect).findComponent({ name: 'QSelect' }).vm.$emit('update:modelValue', '__new_connection__');
  await flushPromises();
  await settle();
  await settle();
  expect(wizard()!.querySelector('[data-connect-step]')!.getAttribute('data-connect-step')).toBe('credential');
  const name = [...wizard()!.querySelectorAll<HTMLElement>('.q-field')].find((f) => f.querySelector('.q-field__label')?.textContent?.trim() === 'Name')!.querySelector('input')!;
  name.value = 'Stripe new';
  name.dispatchEvent(new Event('input', { bubbles: true }));
  const key = wizard()!.querySelector<HTMLInputElement>('[data-credential-field="key"] input')!;
  key.value = 'sk_never-shown';
  key.dispatchEvent(new Event('input', { bubbles: true }));
  await settle();
  (wizard()!.querySelector('[data-credential-save]') as HTMLElement).click();
  await settle();
  await flushPromises();
}

describe('ConnectionPicker on ConnectionSelect', () => {
  it("lists the slot's connections through ConnectionSelect, the team's only, then New connection...", async () => {
    const wrapper = picker({ assigned: ['conn-live', 'conn-gmail'] });
    await flushPromises();

    const select = wrapper.findComponent(ConnectionSelect);
    expect(select.exists()).toBe(true);
    const options = select.findComponent({ name: 'QSelect' }).props('options') as { value: string; label: string }[];
    expect(options.map((o) => o.value)).toEqual(['conn-live', '__new_connection__']);
    expect(options[0]!.label).toBe('Stripe live - Stripe live (API key)');
    // The one assigned connection that fits is preselected, as before.
    expect(wrapper.emitted('update:modelValue')?.[0]).toEqual(['conn-live']);

    wrapper.unmount();
  });

  it('New connection... offers only the kinds the slot takes, and binds what it makes through `bind`', async () => {
    const bind = vi.fn(async () => undefined);
    const wrapper = picker({ bind, noPreselect: true });
    await flushPromises();

    await makeOne(wrapper);

    expect(addCredentialConnection).toHaveBeenCalledWith(expect.objectContaining({ kind: 'apikey', preset: 'stripe', name: 'Stripe new' }));
    expect(bind).toHaveBeenCalledWith('conn-made');
    expect(wrapper.emitted('connected')?.[0]?.[0]).toMatchObject({ id: 'conn-made' });
    // The binding is the Host's: the select chose nothing itself.
    expect(wrapper.emitted('update:modelValue')).toBeUndefined();

    wrapper.unmount();
  });

  it('at a hire (no `bind`), what it makes is chosen for the save', async () => {
    const wrapper = picker({ noPreselect: true });
    await flushPromises();

    await makeOne(wrapper);

    expect(wrapper.emitted('update:modelValue')?.at(-1)).toEqual(['conn-made']);

    wrapper.unmount();
  });
});
