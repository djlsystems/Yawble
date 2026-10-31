// @vitest-environment happy-dom
//
// A CONNECTION IS MADE FROM THE MCP SERVER FORM (CSEL 1): the web address's "Connection (API key,
// optional)" and each environment variable's "Connection" are ConnectionSelects. New connection...
// opens the wizard over the form - API key only for a web address; at API key's Other for a variable,
// so a person names its fields - and on save the new connection is chosen, the connections are read
// again, and the Field select lists its fields. Nothing typed in the form is lost; Cancel changes nothing.
//
// THE MOCKS ARE OF `api/mcpServers` AND `api/client`. No request leaves the test.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { VueWrapper } from '@vue/test-utils';

const { listMcpServers, addMcpServer, listConnections, listOpenConnectionFlows, listMailboxPresets, listCredentialPresets, addCredentialConnection } =
  vi.hoisted(() => ({
    listMcpServers: vi.fn(),
    addMcpServer: vi.fn(),
    listConnections: vi.fn(),
    listOpenConnectionFlows: vi.fn(),
    listMailboxPresets: vi.fn(),
    listCredentialPresets: vi.fn(),
    addCredentialConnection: vi.fn(),
  }));

vi.mock('../../api/mcpServers', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  listMcpServers,
  addMcpServer,
}));

vi.mock('../../api/client', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  listConnections,
  listOpenConnectionFlows,
  listMailboxPresets,
  listCredentialPresets,
  addCredentialConnection,
}));

vi.mock('../../lib/browserNavigation', () => ({
  goTo: vi.fn(),
  currentOrigin: () => 'https://instance.example.test',
}));

import McpServersDialog from '../McpServersDialog.vue';
import type { Connection, CredentialPreset } from '../../api/types';
import { bodyFind, mountDialog, resetBody } from '../../test/mountQuasar';
import { hostConnection } from '../../test/pluginFixtures';
import { button, field, settle, type } from '../../test/formProbe';

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

const apiKey = (id: string, name: string, fields: string[]): Connection =>
  hostConnection({
    id, name, provider: 'apikey', providerKind: 'apikey', kind: 'apikey', account: name, scopes: [],
    fields: fields.map((field) => ({ name: field, label: field, masked: field === 'key', value: null, set: true })),
  });

const existing = apiKey('conn-old', 'Old key', ['token']);
const login = hostConnection({ id: 'conn-login', name: 'Router', provider: 'login', providerKind: 'login', kind: 'login', account: 'admin', scopes: [] });
const gmail = hostConnection({ id: 'conn-gmail', name: 'Work mail', provider: 'google', kind: 'oauth', account: 'me@example.test' });
const linearKey = apiKey('conn-linear', 'Linear key', ['key']);
const mem0 = apiKey('conn-mem0', 'Mem0', ['key', 'autoSave', 'endpoint']);

let known: Connection[] = [];

beforeEach(() => {
  for (const mock of [listMcpServers, addMcpServer, listConnections, listOpenConnectionFlows, listMailboxPresets, listCredentialPresets, addCredentialConnection]) {
    mock.mockReset();
  }
  known = [existing, login, gmail];
  listMcpServers.mockResolvedValue([]);
  listConnections.mockImplementation(async () => known);
  listOpenConnectionFlows.mockResolvedValue([]);
  listMailboxPresets.mockResolvedValue([]);
  listCredentialPresets.mockResolvedValue(presets);
});

afterEach(resetBody);

type Select = { props: (name: string) => unknown; vm: { $emit: (event: string, value: unknown) => void } };

const selects = (wrapper: VueWrapper, label: string) =>
  (wrapper.findAllComponents({ name: 'QSelect' }) as unknown as Select[]).filter((s) => s.props('label') === label);
const select = (wrapper: VueWrapper, label: string) => {
  const found = selects(wrapper, label)[0];
  if (!found) throw new Error(`no QSelect labelled "${label}"`);
  return found;
};
const optionValues = (s: Select) => (s.props('options') as ({ value: string } | string)[]).map((o) => (typeof o === 'string' ? o : o.value));

const wizard = () => bodyFind('[data-guided-connect]');
const step = () => wizard()?.querySelector('[data-connect-step]')?.getAttribute('data-connect-step');

/** A field of the wizard's, by its label: the form underneath has a Name too. */
const wizardField = (label: string) =>
  [...wizard()!.querySelectorAll<HTMLElement>('.q-field')].find((f) => f.querySelector('.q-field__label')?.textContent?.trim() === label)!.querySelector('input')!;

async function click(element: Element | null) {
  if (!element) throw new Error('nothing to click');
  (element as HTMLElement).click();
  await settle();
}

/** The wizard's Name, and each named field's name and value, then Save. */
async function saveOther(name: string, rows: [string, string][]) {
  const nameInput = wizardField('Name');
  nameInput.value = name;
  nameInput.dispatchEvent(new Event('input', { bubbles: true }));
  for (let i = 0; i < rows.length; i++) await click([...wizard()!.querySelectorAll('button')].find((b) => b.textContent?.includes('Add a field')) ?? null);
  rows.forEach(([field, value], index) => {
    const inputs = wizard()!.querySelectorAll<HTMLInputElement>(`[data-credential-field="${index}"] input`);
    inputs[0]!.value = field;
    inputs[0]!.dispatchEvent(new Event('input', { bubbles: true }));
    inputs[1]!.value = value;
    inputs[1]!.dispatchEvent(new Event('input', { bubbles: true }));
  });
  await settle();
  await click(wizard()!.querySelector('[data-credential-save]'));
  await settle();
}

describe('MCP server form: a web address', () => {
  it("lists API keys only, ends with New connection..., and a key made there is chosen with its fields listed - the form kept", async () => {
    addCredentialConnection.mockImplementation(async () => {
      known = [...known, linearKey];
      return { connection: linearKey };
    });
    const wrapper = await mountDialog(McpServersDialog, {}, { attachTo: document.body });
    await click(button('Add MCP server'));
    await type('Name', 'linear');
    await type('Address', 'https://mcp.linear.test/mcp');

    const connection = select(wrapper, 'Connection (API key, optional)');
    expect(optionValues(connection)).toEqual(['conn-old', '__new_connection__']);

    connection.vm.$emit('update:modelValue', '__new_connection__');
    await settle();
    await settle();

    // API key is the only kind a web address sends: the wizard opens at its form.
    expect(step()).toBe('credential');
    expect(wizard()!.querySelector('[data-credential-kind]')!.getAttribute('data-credential-kind')).toBe('apikey');
    await click(wizard()!.querySelector('[data-credential-preset="stripe"]'));
    const nameInput = wizardField('Name');
    nameInput.value = 'Linear key';
    nameInput.dispatchEvent(new Event('input', { bubbles: true }));
    const secret = wizard()!.querySelector<HTMLInputElement>('[data-credential-field="key"] input')!;
    secret.value = 'lin_never-shown';
    secret.dispatchEvent(new Event('input', { bubbles: true }));
    await settle();
    await click(wizard()!.querySelector('[data-credential-save]'));
    await settle();

    expect(select(wrapper, 'Connection (API key, optional)').props('modelValue')).toBe('conn-linear');
    // Read again: opened once, and again after the save.
    expect(listConnections).toHaveBeenCalledTimes(2);
    expect(optionValues(select(wrapper, 'Field (default: its secret field)'))).toEqual(['key']);
    expect(field('Address').value).toBe('https://mcp.linear.test/mcp');
    expect(field('Name').value).toBe('linear');

    // Finish, then Save: the entry names the new connection.
    await click([...wizard()!.querySelectorAll('button')].find((b) => b.textContent?.trim() === 'Finish') ?? null);
    addMcpServer.mockResolvedValue({ server: { id: 'mcp-9', name: 'linear' }, check: null });
    await click(bodyFind('[data-mcp-save]'));
    expect(addMcpServer).toHaveBeenCalledWith(expect.objectContaining({ name: 'linear', url: 'https://mcp.linear.test/mcp', connection: 'conn-linear' }));

    wrapper.unmount();
  });

  it('Cancel in the wizard leaves the connection and the form as they were', async () => {
    const wrapper = await mountDialog(McpServersDialog, {}, { attachTo: document.body });
    await click(button('Add MCP server'));
    await type('Name', 'linear');
    select(wrapper, 'Connection (API key, optional)').vm.$emit('update:modelValue', 'conn-old');
    await settle();

    select(wrapper, 'Connection (API key, optional)').vm.$emit('update:modelValue', '__new_connection__');
    await settle();
    await click(wizard()!.querySelector('[aria-label="Close"]'));
    await settle();

    expect(select(wrapper, 'Connection (API key, optional)').props('modelValue')).toBe('conn-old');
    expect(optionValues(select(wrapper, 'Field (default: its secret field)'))).toEqual(['token']);
    expect(field('Name').value).toBe('linear');
    expect(addCredentialConnection).not.toHaveBeenCalled();

    wrapper.unmount();
  });
});

describe('MCP server form: an environment variable', () => {
  it("lists credential connections, opens New connection... at API key's Other, and lists the fields the person named", async () => {
    addCredentialConnection.mockImplementation(async () => {
      known = [...known, mem0];
      return { connection: mem0 };
    });
    const wrapper = await mountDialog(McpServersDialog, {}, { attachTo: document.body });
    await click(button('Add MCP server'));
    await type('Name', 'memory');
    (wrapper.findAllComponents({ name: 'QBtnToggle' })[0] as unknown as Select).vm.$emit('update:modelValue', 'runs');
    await settle();
    await type('Package', 'mem0-mcp');
    await click(button('Add variable'));

    const row = select(wrapper, 'Connection');
    expect(optionValues(row)).toEqual(['conn-old', 'conn-login', '__new_connection__']);

    row.vm.$emit('update:modelValue', '__new_connection__');
    await settle();
    await settle();

    expect(step()).toBe('credential');
    expect(wizard()!.querySelector('[data-credential-preset="other"]')!.getAttribute('aria-pressed')).toBe('true');

    await saveOther('Mem0', [['key', 'm0-never-shown'], ['autoSave', 'true'], ['endpoint', 'https://api.mem0.test']]);

    expect(addCredentialConnection).toHaveBeenCalledWith(expect.objectContaining({ kind: 'apikey', preset: 'other', name: 'Mem0' }));
    expect(select(wrapper, 'Connection').props('modelValue')).toBe('conn-mem0');
    expect(optionValues(select(wrapper, 'Field'))).toEqual(['key', 'autoSave', 'endpoint']);
    expect(field('Package').value).toBe('mem0-mcp');

    wrapper.unmount();
  });

  it("a variable's Back reaches the other credential kinds, and never a sign-in or a mailbox", async () => {
    const wrapper = await mountDialog(McpServersDialog, {}, { attachTo: document.body });
    await click(button('Add MCP server'));
    (wrapper.findAllComponents({ name: 'QBtnToggle' })[0] as unknown as Select).vm.$emit('update:modelValue', 'runs');
    await settle();
    await click(button('Add variable'));

    select(wrapper, 'Connection').vm.$emit('update:modelValue', '__new_connection__');
    await settle();
    await settle();
    await click([...wizard()!.querySelectorAll('button')].find((b) => b.textContent?.trim() === 'Back') ?? null);

    const kinds = [...wizard()!.querySelectorAll('[data-connection-kind]')].map((tile) => tile.getAttribute('data-connection-kind'));
    expect(kinds).toEqual(['website', 'apikey', 'login']);

    wrapper.unmount();
  });
});
