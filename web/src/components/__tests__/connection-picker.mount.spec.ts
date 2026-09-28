// @vitest-environment happy-dom
//
// THE BINDING PICKER: one per connection slot a plugin's manifest declares, at hire (Add member)
// and after it (Member settings). It lists only connections of a provider the slot allows, stores
// the connection's ID and never a token, and says before the save what the Host would refuse: a
// required slot left unbound, and a connection without a scope the slot needs - offering Reconnect.
//
// THE MOCK IS OF `api/client`, in the shapes connections-api.md gives; the Host's own checks are
// pinned server-side.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';

const {
  listCatalog,
  listPlugins,
  addMember,
  listConnections,
  listConnectionProviders,
  startConnection,
  getMember,
  getPluginSettings,
  savePluginSettings,
  updateMember,
  goTo,
} = vi.hoisted(() => ({
  listCatalog: vi.fn(),
  listPlugins: vi.fn(),
  addMember: vi.fn(),
  listConnections: vi.fn(),
  listConnectionProviders: vi.fn(),
  startConnection: vi.fn(),
  getMember: vi.fn(),
  getPluginSettings: vi.fn(),
  savePluginSettings: vi.fn(),
  updateMember: vi.fn(),
  goTo: vi.fn(),
}));

vi.mock('../../api/client', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  listCatalog,
  listPlugins,
  addMember,
  listConnections,
  listConnectionProviders,
  startConnection,
  getMember,
  getPluginSettings,
  savePluginSettings,
  updateMember,
}));

vi.mock('../../lib/browserNavigation', () => ({
  goTo,
  currentOrigin: () => 'https://instance.example.test',
}));

import AddMemberDialog from '../AddMemberDialog.vue';
import MemberSettingsDialog from '../MemberSettingsDialog.vue';
import { useConsoleStore } from '../../stores/console';
import { asMemberId, asTeamId, type ContainerSnapshot, type InstalledPlugin, type Team, type TeamId } from '../../api/types';
import { bodyFind, mountDialog, resetBody } from '../../test/mountQuasar';
import { hostConnection, hostList, hostPlugin, hostProvider, hostSettings, hostSlot } from '../../test/pluginFixtures';
import { button, isDisabled, settle, type } from '../../test/formProbe';

const mailer: InstalledPlugin = hostPlugin({
  id: 'mailer',
  name: 'Mailer',
  connections: {
    mail: hostSlot({
      description: 'The mailbox to read and send from.',
      providers: ['google', 'microsoft'],
      scopes: { google: ['https://mail.google.com/'], microsoft: ['https://outlook.office.com/SMTP.Send'] },
      required: true,
      summary: 'needs a Google or Microsoft connection',
    }),
  },
});

const workMail = hostConnection({
  id: 'conn-work',
  name: 'Work mail',
  provider: 'google',
  scopes: ['openid', 'email', 'https://mail.google.com/'],
});
const readOnly = hostConnection({
  id: 'conn-read',
  name: 'Read only',
  provider: 'google',
  account: 'reader@example.com',
  scopes: ['openid', 'email'],
});
const acme = hostConnection({ id: 'conn-acme', name: 'Acme', provider: 'custom-acme', account: 'acme-user' });

beforeEach(() => {
  for (const mock of [
    listCatalog,
    listPlugins,
    addMember,
    listConnections,
    listConnectionProviders,
    startConnection,
    getMember,
    getPluginSettings,
    savePluginSettings,
    updateMember,
    goTo,
  ]) {
    mock.mockReset();
  }
  listCatalog.mockResolvedValue({ agents: [{ name: 'claude', mode: 'Headless' }] });
  listPlugins.mockResolvedValue(hostList([mailer]));
  addMember.mockResolvedValue({ name: 'Inbox' });
  listConnections.mockResolvedValue([workMail, readOnly, acme]);
  listConnectionProviders.mockResolvedValue([
    hostProvider({ id: 'google', configured: true }),
    hostProvider({ id: 'microsoft' }),
    hostProvider({ id: 'custom-acme', name: 'Acme', configured: true }),
  ]);
  startConnection.mockResolvedValue({
    authorizationUrl: 'https://accounts.google.com/o/oauth2/v2/auth?state=r1',
    state: 'r1',
    redirectUri: 'https://instance.example.test/api/connections/callback',
    expiresAt: '2026-09-28T10:10:00Z',
  });
  savePluginSettings.mockResolvedValue(undefined);
});

afterEach(resetBody);

type Select = { props: (name: string) => unknown; vm: { $emit: (event: string, value: unknown) => void } };

function select(wrapper: { findAllComponents: (s: { name: string }) => unknown[] }, label: string): Select {
  const found = (wrapper.findAllComponents({ name: 'QSelect' }) as Select[]).find((s) => s.props('label') === label);
  if (!found) throw new Error(`no QSelect labelled "${label}"`);
  return found;
}

async function choose(wrapper: Parameters<typeof select>[0], label: string, value: string | null) {
  select(wrapper, label).vm.$emit('update:modelValue', value);
  await settle();
}

const refusal = () => bodyFind('[data-connection-slot="mail"] [data-binding-refusal]')?.textContent ?? '';

async function mountHire() {
  const wrapper = await mountDialog(AddMemberDialog, { team: 'alpha' as TeamId, teamName: 'Alpha', memberAgents: ['claude'] });
  await type('Member name', 'Inbox');
  await choose(wrapper, 'Agent', 'plugin:mailer');
  await settle();
  return wrapper;
}

describe('the binding picker, at hire', () => {
  it('lists only connections of a provider the slot allows, by name, account and provider', async () => {
    const wrapper = await mountHire();

    const options = select(wrapper, 'Connection for mail').props('options') as { value: string; label: string }[];
    expect(options.map((option) => option.value)).toEqual(['conn-read', 'conn-work']);
    expect(options.find((option) => option.value === 'conn-work')!.label).toBe('Work mail - person@example.com (Google)');
    expect(select(wrapper, 'Connection for mail').props('hint')).toContain('needs a Google or Microsoft connection');

    wrapper.unmount();
  });

  it("says what an unbound required slot's runs will be blocked with, and still hires", async () => {
    const wrapper = await mountHire();

    expect(refusal()).toBe(
      "This member has no connection bound for the plugin's required slot `mail`. A person binds one in the member's settings.",
    );
    // The Host hires a member with the slot unbound and blocks its runs instead.
    expect(isDisabled('Add member')).toBe(false);
    button('Add member').click();
    await settle();

    expect(addMember).toHaveBeenCalledWith('alpha', 'Inbox', 'plugin:mailer', { config: {}, secrets: {}, connections: {} });

    wrapper.unmount();
  });

  it('refuses a connection without the scope the slot needs, and Reconnect asks for it', async () => {
    const wrapper = await mountHire();

    await choose(wrapper, 'Connection for mail', 'conn-read');

    expect(refusal()).toContain(
      "Connection 'Read only' (reader@example.com) was not granted the scope `https://mail.google.com/` that slot `mail` needs. " +
        'Reconnect it from Admin → Connections with that scope, then bind it again.',
    );

    const reconnect = [...bodyFind('[data-connection-slot="mail"]')!.querySelectorAll('button')].find(
      (candidate) => candidate.textContent?.includes('Reconnect'),
    )!;
    reconnect.click();
    await settle();

    expect(startConnection).toHaveBeenCalledWith({ reconnectId: 'conn-read', scopes: ['https://mail.google.com/'] });
    expect(goTo).toHaveBeenCalledWith('https://accounts.google.com/o/oauth2/v2/auth?state=r1');

    wrapper.unmount();
  });

  it("hires with the connection's id bound to the slot, never a token", async () => {
    const wrapper = await mountHire();

    await choose(wrapper, 'Connection for mail', 'conn-work');
    expect(refusal()).toBe('');
    expect(isDisabled('Add member')).toBe(false);

    button('Add member').click();
    await settle();

    expect(addMember).toHaveBeenCalledWith('alpha', 'Inbox', 'plugin:mailer', {
      config: {},
      secrets: {},
      connections: { mail: 'conn-work' },
    });

    wrapper.unmount();
  });

  it('makes no connections call for a plugin without slots', async () => {
    listPlugins.mockResolvedValue(hostList([hostPlugin({ id: 'plain', connections: {} })]));
    const wrapper = await mountDialog(AddMemberDialog, { team: 'alpha' as TeamId, teamName: 'Alpha', memberAgents: ['claude'] });
    await choose(wrapper, 'Agent', 'plugin:plain');

    expect(bodyFind('[data-connection-slot]')).toBeNull();
    expect(listConnections).not.toHaveBeenCalled();

    wrapper.unmount();
  });
});

describe('the binding picker, in Member settings', () => {
  const inbox = {
    team: asTeamId('alpha'),
    id: asMemberId('Inbox'),
    name: 'Inbox',
    agent: 'plugin:mailer',
    kind: 'plugin',
    state: 'Idle',
    queueDepth: 0,
    ceiling: 16,
    subscribes: [],
    currentCorrelation: null,
    sinceSeq: 0,
  } as unknown as ContainerSnapshot;

  async function mountSettings(bound: Record<string, string>) {
    getPluginSettings.mockResolvedValue(
      hostSettings(mailer, {
        team: 'alpha',
        member: 'Inbox',
        connections: bound,
        connectionFields: mailer.connections ?? {},
      }),
    );
    updateMember.mockResolvedValue({ ...inbox });

    setActivePinia(createPinia());
    const board = useConsoleStore();
    board.teams = [{ id: asTeamId('alpha'), name: 'Alpha', memberAgents: ['claude'], containers: [inbox] } as unknown as Team];
    board.managerName = asMemberId('Manager');

    const wrapper = await mountDialog(MemberSettingsDialog, { snapshot: inbox }, { pinia: false });
    await settle();
    return wrapper;
  }

  it('shows the stored binding, and unbinding the required slot says what the runs would be blocked with', async () => {
    const wrapper = await mountSettings({ mail: 'conn-work' });

    expect(select(wrapper, 'Connection for mail').props('modelValue')).toBe('conn-work');
    expect(refusal()).toBe('');

    await choose(wrapper, 'Connection for mail', null);
    expect(refusal()).toContain('required slot `mail`');

    wrapper.unmount();
  });

  it('says so when the bound connection no longer exists', async () => {
    const wrapper = await mountSettings({ mail: 'conn-gone' });

    expect(refusal()).toBe(
      'The connection bound for slot `mail` no longer exists. A person binds another in the member\'s settings.',
    );

    wrapper.unmount();
  });

  it('saves the new binding through the settings route', async () => {
    const wrapper = await mountSettings({});

    await choose(wrapper, 'Connection for mail', 'conn-work');
    button('Save').click();
    await settle();

    expect(savePluginSettings).toHaveBeenCalledWith('alpha', 'Inbox', {
      config: {},
      secrets: {},
      connections: { mail: 'conn-work' },
    });

    wrapper.unmount();
  });
});
