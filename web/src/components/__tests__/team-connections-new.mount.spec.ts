// @vitest-environment happy-dom
//
// NEW CONNECTION... IS ADD CONNECTION'S LAST ENTRY (CSEL 1): Team settings → Connections and New team's
// Connections tab choose from a ConnectionSelect, whose last entry opens the connection wizard - every
// kind, as Admin → Connections offers. The separate New connection... button is gone. What is made
// there is assigned to the team as before (Team settings), or chosen for the create (New team).
//
// THE MOCK IS OF `api/client`. No request leaves the test.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { VueWrapper } from '@vue/test-utils';

const {
  listConnections,
  listConnectionProviders,
  listTeamConnections,
  assignTeamConnection,
  listOpenConnectionFlows,
  listMailboxPresets,
  listCredentialPresets,
  addCredentialConnection,
} = vi.hoisted(() => ({
  listConnections: vi.fn(),
  listConnectionProviders: vi.fn(),
  listTeamConnections: vi.fn(),
  assignTeamConnection: vi.fn(),
  listOpenConnectionFlows: vi.fn(),
  listMailboxPresets: vi.fn(),
  listCredentialPresets: vi.fn(),
  addCredentialConnection: vi.fn(),
}));

vi.mock('../../api/client', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  listConnections,
  listConnectionProviders,
  listTeamConnections,
  assignTeamConnection,
  listOpenConnectionFlows,
  listMailboxPresets,
  listCredentialPresets,
  addCredentialConnection,
}));

vi.mock('../../lib/browserNavigation', () => ({
  goTo: vi.fn(),
  currentOrigin: () => 'https://instance.example.test',
}));

import TeamConnectionsPanel from '../TeamConnectionsPanel.vue';
import type { Connection, CredentialPreset } from '../../api/types';
import { bodyFind, mountDialog, resetBody } from '../../test/mountQuasar';
import { hostConnection, hostProvider } from '../../test/pluginFixtures';
import { settle } from '../../test/formProbe';

const presets: CredentialPreset[] = [
  {
    kind: 'login', id: null, name: 'Username and password', namedFields: false,
    fields: [
      { name: 'username', label: 'Username', masked: false, required: true },
      { name: 'password', label: 'Password', masked: true, required: true },
    ],
  },
];

const workMail = hostConnection({ id: 'conn-work', name: 'Work mail', provider: 'google', scopes: [] });
const homeMail = hostConnection({ id: 'conn-home', name: 'Home mail', provider: 'google', scopes: [] });
const router: Connection = hostConnection({ id: 'conn-router', name: 'Router', provider: 'login', providerKind: 'login', kind: 'login', account: 'admin', scopes: [] });

let known: Connection[] = [];
let assigned: Connection[] = [];

beforeEach(() => {
  for (const mock of [listConnections, listConnectionProviders, listTeamConnections, assignTeamConnection, listOpenConnectionFlows, listMailboxPresets, listCredentialPresets, addCredentialConnection]) {
    mock.mockReset();
  }
  known = [workMail, homeMail];
  assigned = [workMail];
  listConnections.mockImplementation(async () => known);
  listConnectionProviders.mockResolvedValue([hostProvider({ id: 'google', configured: true })]);
  listTeamConnections.mockImplementation(async (team: string) => ({
    team,
    connections: assigned.map((connection) => ({ assignedAt: '2026-10-31T10:00:00Z', connection })),
  }));
  assignTeamConnection.mockImplementation(async (_team: string, id: string) => {
    assigned = [...assigned, known.find((connection) => connection.id === id)!];
    return {};
  });
  listOpenConnectionFlows.mockResolvedValue([]);
  listMailboxPresets.mockResolvedValue([]);
  listCredentialPresets.mockResolvedValue(presets);
  addCredentialConnection.mockImplementation(async () => {
    known = [...known, router];
    return { connection: router };
  });
});

afterEach(resetBody);

type Select = { props: (name: string) => unknown; vm: { $emit: (event: string, value: unknown) => void } };

function addSelect(wrapper: VueWrapper): Select {
  const found = (wrapper.findAllComponents({ name: 'QSelect' }) as unknown as Select[]).find((s) => s.props('label') === 'Add connection');
  if (!found) throw new Error('no QSelect labelled "Add connection"');
  return found;
}

const values = (wrapper: VueWrapper) => (addSelect(wrapper).props('options') as { value: string }[]).map((o) => o.value);
const wizard = () => bodyFind('[data-guided-connect]');
const kindTiles = () => [...(wizard()?.querySelectorAll('[data-connection-kind]') ?? [])].map((tile) => tile.getAttribute('data-connection-kind'));

async function click(element: Element | null) {
  if (!element) throw new Error('nothing to click');
  (element as HTMLElement).click();
  await settle();
}

function typeIn(label: string, value: string) {
  const input = [...wizard()!.querySelectorAll<HTMLElement>('.q-field')]
    .find((f) => f.querySelector('.q-field__label')?.textContent?.trim() === label)!
    .querySelector('input')!;
  input.value = value;
  input.dispatchEvent(new Event('input', { bubbles: true }));
}

/** New connection..., every kind offered, then a username and password saved. */
async function makeRouter(wrapper: VueWrapper) {
  addSelect(wrapper).vm.$emit('update:modelValue', '__new_connection__');
  await settle();
  await settle();
  expect(kindTiles()).toEqual(['oauth', 'imap', 'website', 'apikey', 'login']);
  await click(wizard()!.querySelector('[data-connection-kind="login"]'));
  typeIn('Name', 'Router');
  typeIn('Username', 'admin');
  typeIn('Password', 'never-shown');
  await settle();
  await click(wizard()!.querySelector('[data-credential-save]'));
  await settle();
  await settle();
}

describe('Team settings → Connections: New connection...', () => {
  it('is Add connection’s last entry, and the separate button is gone', async () => {
    const wrapper = await mountDialog(TeamConnectionsPanel, { team: 'alpha' }, { attachTo: document.body });

    expect(values(wrapper)).toEqual(['conn-home', '__new_connection__']);
    expect(bodyFind('[data-team-connection-new]')).toBeNull();
    expect([...document.body.querySelectorAll('button')].some((b) => b.textContent?.includes('New connection...'))).toBe(false);

    wrapper.unmount();
  });

  it('assigns what the wizard makes to the team, and lists it', async () => {
    const wrapper = await mountDialog(TeamConnectionsPanel, { team: 'alpha' }, { attachTo: document.body });

    await makeRouter(wrapper);

    expect(addCredentialConnection).toHaveBeenCalledTimes(1);
    expect(assignTeamConnection).toHaveBeenCalledTimes(1);
    expect(assignTeamConnection).toHaveBeenCalledWith('alpha', 'conn-router');
    expect(bodyFind('[data-team-connection="Router"]')).not.toBeNull();
    // On the team now: no longer offered to add, and the select is ready for the next.
    expect(values(wrapper)).toEqual(['conn-home', '__new_connection__']);
    expect(addSelect(wrapper).props('modelValue')).toBeNull();

    wrapper.unmount();
  });

  it('Cancel in the wizard assigns nothing', async () => {
    const wrapper = await mountDialog(TeamConnectionsPanel, { team: 'alpha' }, { attachTo: document.body });

    addSelect(wrapper).vm.$emit('update:modelValue', '__new_connection__');
    await settle();
    await click(wizard()!.querySelector('[aria-label="Close"]'));

    expect(assignTeamConnection).not.toHaveBeenCalled();
    expect(addSelect(wrapper).props('modelValue')).toBeNull();

    wrapper.unmount();
  });
});

describe("New team → Connections: New connection...", () => {
  it('chooses what the wizard makes for the create, assigning nothing at the Host', async () => {
    const wrapper = await mountDialog(TeamConnectionsPanel, {}, { attachTo: document.body });

    expect(values(wrapper)).toEqual(['conn-home', 'conn-work', '__new_connection__']);
    await makeRouter(wrapper);

    expect(assignTeamConnection).not.toHaveBeenCalled();
    expect(wrapper.emitted('update:chosen')?.at(-1)).toEqual([['conn-router']]);

    wrapper.unmount();
  });
});
