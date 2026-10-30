// @vitest-environment happy-dom
//
// WHICH CONNECTIONS A TEAM MAY USE (CON 2): Team settings → Connections lists what is assigned to the
// team and who on it uses each, adds from a pick list, and removes only after naming the members it
// unbinds; New team → Connections chooses them for the create. A plugin member's slot lists only the
// connections assigned to its team that fit, preselects the only one, and ends with New connection...
// A required slot with nothing bound reads Needs you where the member's status shows.
//
// THE MOCK IS OF `api/client`; the Host's own rules (a person's act only, one transaction, the hire
// rule) are pinned server-side in ConnectionsTests.Teams.cs.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

const {
  listCatalog,
  listPlugins,
  addMember,
  listConnections,
  listConnectionProviders,
  listTeamConnections,
  assignTeamConnection,
  removeTeamConnection,
  getConnectionNeeds,
  listOpenConnectionFlows,
} = vi.hoisted(() => ({
  getConnectionNeeds: vi.fn(async () => ({ provider: 'google', scopes: [], plugins: [] })),
  listOpenConnectionFlows: vi.fn(async () => []),
  listCatalog: vi.fn(),
  listPlugins: vi.fn(),
  addMember: vi.fn(),
  listConnections: vi.fn(),
  listConnectionProviders: vi.fn(),
  listTeamConnections: vi.fn(),
  assignTeamConnection: vi.fn(),
  removeTeamConnection: vi.fn(),
}));

vi.mock('../../api/client', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  listCatalog,
  listPlugins,
  addMember,
  listConnections,
  listConnectionProviders,
  listTeamConnections,
  assignTeamConnection,
  removeTeamConnection,
  getConnectionNeeds,
  listOpenConnectionFlows,
  listMailboxPresets: vi.fn(async () => []),
  listCredentialPresets: vi.fn(async () => []),
}));

import AddMemberDialog from '../AddMemberDialog.vue';
import TeamConnectionsPanel from '../TeamConnectionsPanel.vue';
import type { Connection, ContainerSnapshot, InstalledPlugin, TeamId } from '../../api/types';
import { containerMark } from '../../lib/teamKpis';
import { memberStateOf, stateWords } from '../../lib/memberState';
import { bodyFind, bodyText, mountDialog, resetBody } from '../../test/mountQuasar';
import { hostConnection, hostList, hostPlugin, hostProvider, hostSlot } from '../../test/pluginFixtures';
import { button, settle, type } from '../../test/formProbe';

const mailer: InstalledPlugin = hostPlugin({
  id: 'mailer',
  name: 'Mailer',
  connections: {
    mail: hostSlot({
      description: 'The mailbox to read and send from.',
      providers: ['google'],
      scopes: { google: ['https://mail.google.com/'] },
      required: true,
      summary: 'needs a Google connection',
    }),
  },
});

const workMail = hostConnection({
  id: 'conn-work',
  name: 'Work mail',
  provider: 'google',
  scopes: ['openid', 'email', 'https://mail.google.com/'],
  usedBy: [{ team: 'alpha', member: 'inbox', label: 'Inbox', slot: 'mail' }],
});
const homeMail = hostConnection({
  id: 'conn-home',
  name: 'Home mail',
  provider: 'google',
  account: 'home@example.com',
  scopes: ['openid', 'email', 'https://mail.google.com/'],
});

/** Alpha was given Work mail; Beta was given nothing. */
const assignments: Record<string, Connection[]> = { alpha: [workMail], beta: [] };

beforeEach(() => {
  for (const mock of [
    listCatalog,
    listPlugins,
    addMember,
    listConnections,
    listConnectionProviders,
    listTeamConnections,
    assignTeamConnection,
    removeTeamConnection,
  ]) {
    mock.mockReset();
  }
  listCatalog.mockResolvedValue({ agents: [{ name: 'claude', mode: 'Headless' }] });
  listPlugins.mockResolvedValue(hostList([mailer]));
  addMember.mockResolvedValue({ name: 'Inbox' });
  listConnections.mockResolvedValue([workMail, homeMail]);
  listConnectionProviders.mockResolvedValue([hostProvider({ id: 'google', configured: true })]);
  listTeamConnections.mockImplementation(async (team: string) => ({
    team,
    connections: (assignments[team] ?? []).map((connection) => ({ assignedAt: '2026-10-30T10:00:00Z', connection })),
  }));
  assignTeamConnection.mockResolvedValue({});
  removeTeamConnection.mockResolvedValue(undefined);
});

afterEach(resetBody);

type Select = { props: (name: string) => unknown; vm: { $emit: (event: string, value: unknown) => void } };

function select(wrapper: { findAllComponents: (s: { name: string }) => unknown[] }, label: string): Select {
  const found = (wrapper.findAllComponents({ name: 'QSelect' }) as Select[]).find((s) => s.props('label') === label);
  if (!found) throw new Error(`no QSelect labelled "${label}"`);
  return found;
}

async function hire(team: string) {
  const wrapper = await mountDialog(AddMemberDialog, { team: team as TeamId, teamName: team, memberAgents: ['claude'] });
  await type('Member name', 'Inbox');
  select(wrapper, 'Agent').vm.$emit('update:modelValue', 'plugin:mailer');
  await settle();
  await settle();
  return wrapper;
}

const optionValues = (wrapper: Parameters<typeof select>[0]) =>
  (select(wrapper, 'Connection for mail').props('options') as { value: string }[]).map((option) => option.value);

describe("a plugin member's slot lists only its team's connections", () => {
  it("offers the team's one fitting connection, preselected, and the hire binds it", async () => {
    const wrapper = await hire('alpha');

    expect(listTeamConnections).toHaveBeenCalledWith('alpha');
    expect(optionValues(wrapper)).toEqual(['conn-work', '__new_connection__']);
    expect(select(wrapper, 'Connection for mail').props('modelValue')).toBe('conn-work');

    button('Add member').click();
    await settle();
    expect(addMember).toHaveBeenCalledWith('alpha', 'Inbox', 'plugin:mailer', {
      config: {},
      secrets: {},
      connections: { mail: 'conn-work' },
    });

    wrapper.unmount();
  });

  it("does not offer another team's connection: a team given none offers only New connection...", async () => {
    const wrapper = await hire('beta');

    expect(optionValues(wrapper)).toEqual(['__new_connection__']);
    expect(select(wrapper, 'Connection for mail').props('modelValue')).toBeNull();
    // Nothing to choose, so Connect is offered first, as it is for a slot with nothing suitable.
    expect(bodyFind('[data-connection-slot="mail"] [data-slot-connect]')).not.toBeNull();

    wrapper.unmount();
  });

  it('opens the connection wizard from New connection..., choosing nothing in the list', async () => {
    const wrapper = await hire('beta');

    select(wrapper, 'Connection for mail').vm.$emit('update:modelValue', '__new_connection__');
    await settle();

    expect(bodyFind('[data-guided-connect]')).not.toBeNull();
    expect(select(wrapper, 'Connection for mail').props('modelValue')).toBeNull();

    wrapper.unmount();
  });
});

describe('Team settings → Connections', () => {
  it('lists the connections assigned to the team and the members of the team using each', async () => {
    const wrapper = await mountDialog(TeamConnectionsPanel, { team: 'alpha' }, { attachTo: document.body });

    const row = bodyFind('[data-team-connection="Work mail"]');
    expect(row?.textContent).toContain('Work mail');
    expect(row?.querySelector('[data-team-connection-users]')?.textContent).toContain('Used by Inbox (mail)');
    expect(bodyFind('[data-team-connection="Home mail"]')).toBeNull();

    // The pick list offers what the team does not have yet.
    expect(optionValuesOf(wrapper, 'Add connection')).toEqual(['conn-home']);

    wrapper.unmount();
  });

  it('adds an existing connection from the pick list', async () => {
    const wrapper = await mountDialog(TeamConnectionsPanel, { team: 'alpha' }, { attachTo: document.body });

    select(wrapper, 'Add connection').vm.$emit('update:modelValue', 'conn-home');
    await settle();

    expect(assignTeamConnection).toHaveBeenCalledWith('alpha', 'conn-home');
    expect(listTeamConnections).toHaveBeenCalledTimes(2);

    wrapper.unmount();
  });

  it('asks before removing, naming the members it unbinds, then removes and unbinds them', async () => {
    const wrapper = await mountDialog(TeamConnectionsPanel, { team: 'alpha' }, { attachTo: document.body });

    (bodyFind('[data-team-connection="Work mail"] [data-team-connection-remove]') as HTMLButtonElement).click();
    await settle();

    expect(removeTeamConnection).not.toHaveBeenCalled();
    expect(bodyFind('[data-team-connection-confirm-users]')?.textContent).toContain('Inbox (mail)');
    expect(bodyText()).toContain('The connection itself stays, and other teams keep it.');

    button('Remove and unbind').click();
    await settle();

    expect(removeTeamConnection).toHaveBeenCalledWith('alpha', 'conn-work', true);

    wrapper.unmount();
  });

  it('a team with none says it starts with none', async () => {
    const wrapper = await mountDialog(TeamConnectionsPanel, { team: 'beta' }, { attachTo: document.body });

    expect(bodyFind('[data-team-connections-empty]')?.textContent).toContain('This team starts with none');

    wrapper.unmount();
  });
});

describe('New team → Connections', () => {
  it('chooses connections for the create without assigning anything at the Host', async () => {
    const wrapper = await mountDialog(TeamConnectionsPanel, { chosen: [] }, { attachTo: document.body });

    expect(bodyFind('[data-team-connections-empty]')?.textContent).toContain('The new team starts with none');
    select(wrapper, 'Add connection').vm.$emit('update:modelValue', 'conn-home');
    await settle();

    expect(wrapper.emitted('update:chosen')?.at(-1)).toEqual([['conn-home']]);
    expect(assignTeamConnection).not.toHaveBeenCalled();
    expect(listTeamConnections).not.toHaveBeenCalled();

    wrapper.unmount();
  });
});

function optionValuesOf(wrapper: Parameters<typeof select>[0], label: string): string[] {
  return (select(wrapper, label).props('options') as { value: string }[]).map((option) => option.value);
}

describe('a required slot with nothing bound reads Needs you', () => {
  const member = {
    id: 'inbox',
    name: 'Inbox',
    state: 'Idle',
    queueDepth: 0,
    held: false,
    needsPerson: 'Waits for a person to choose a connection for slot `mail`.',
    blocked: 'Connection Work mail was removed from this team.',
  } as unknown as ContainerSnapshot;

  it("where the member's status shows, ahead of the blocked line a removal also writes", () => {
    expect(containerMark(member)).toEqual({ kind: 'needs-decision', reason: member.needsPerson });
    expect(stateWords(memberStateOf(member))).toBe('Needs you');
  });

  it('a run that ended needs-person is a wait, never a failure', () => {
    const waited = {
      ...member,
      needsPerson: null,
      blocked: null,
      failed: 'Slot `mail` has no connection.',
      failureClass: 'needs-person',
    } as unknown as ContainerSnapshot;
    expect(containerMark(waited)).toEqual({ kind: 'needs-decision', reason: 'Slot `mail` has no connection.' });
    expect(stateWords(memberStateOf(waited))).toBe('Needs you');
  });

  it('and not once a binding clears it', () => {
    const bound = { ...member, needsPerson: null, blocked: null } as unknown as ContainerSnapshot;
    expect(containerMark(bound)).toBeNull();
    expect(stateWords(memberStateOf(bound))).toBe('Idle');
  });
});
