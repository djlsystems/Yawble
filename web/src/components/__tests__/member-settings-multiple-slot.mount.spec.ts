// @vitest-environment happy-dom
//
// A SLOT THAT TAKES SEVERAL CONNECTIONS, in Member settings (workflow 26256): `appSecrets` holds a
// mailbox and an API key (Other). The dialog lists both, each with its own Remove, and adds another with
// a picker below; it is never one dropdown that keeps only the connection just picked. Save submits every
// connection still listed, and names a removed one as removed, so the Host keeps the rest. A binding the
// active version cannot use is said here in the Host's words.
//
// THE MOCK IS OF `api/client`; the Host's merge is pinned by PluginUpgradeAttentionTests.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';

const { listCatalog, listPlugins, listConnections, listConnectionProviders, getMember, getPluginSettings, savePluginSettings, updateMember, listTeamConnections } =
  vi.hoisted(() => ({
    listCatalog: vi.fn(),
    listPlugins: vi.fn(),
    listConnections: vi.fn(),
    listConnectionProviders: vi.fn(),
    getMember: vi.fn(),
    getPluginSettings: vi.fn(),
    savePluginSettings: vi.fn(),
    updateMember: vi.fn(),
    listTeamConnections: vi.fn(() => Promise.reject(new Error('not read'))),
  }));

vi.mock('../../api/client', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  listCatalog,
  listPlugins,
  listConnections,
  listConnectionProviders,
  getMember,
  getPluginSettings,
  savePluginSettings,
  updateMember,
  listTeamConnections,
}));

vi.mock('../../lib/browserNavigation', () => ({
  goTo: vi.fn(),
  currentOrigin: () => 'https://instance.example.test',
}));

import MemberSettingsDialog from '../MemberSettingsDialog.vue';
import { useConsoleStore } from '../../stores/console';
import { asMemberId, asTeamId, type ContainerSnapshot, type InstalledPlugin, type Team, type UnusableConnection } from '../../api/types';
import { bodyFind, mountDialog, resetBody } from '../../test/mountQuasar';
import { hostConnection, hostField, hostList, hostPlugin, hostProvider, hostSettings, hostSlot } from '../../test/pluginFixtures';
import { button, settle, type } from '../../test/formProbe';

const cloud: InstalledPlugin = hostPlugin({
  id: 'cloud',
  name: 'Cloud',
  config: { region: hostField({ type: 'string', description: 'Where apps run.' }) },
  connections: {
    appSecrets: hostSlot({
      // As azure-plugin words it: the binding sentence points at a page that cannot bind.
      description: 'Connections a deployed app may use. <connection> is the name the connection has in Team settings -> Connections. '
        + 'Bind as many as needed in Team settings -> Connections; an Update password on a connection reaches the app at its next create.',
      providers: ['imap', 'login', 'apikey', 'website'],
      multiple: true,
      summary: 'needs a mailbox, login, API key or website connection',
    }),
  },
});

const mailbox = hostConnection({ id: 'conn-mail', name: 'noreply mailbox', provider: 'imap', providerKind: 'imap', kind: 'imap', account: 'noreply@example.test' });
const signIn = hostConnection({ id: 'conn-key', name: 'cloud sign-in', provider: 'apikey', providerKind: 'apikey', kind: 'apikey', account: 'cloud sign-in' });
const smtp = hostConnection({ id: 'conn-smtp', name: 'smtp login', provider: 'login', providerKind: 'login', kind: 'login', account: 'smtp' });

const member = {
  team: asTeamId('alpha'),
  id: asMemberId('AzureInfrastructure'),
  name: 'AzureInfrastructure',
  agent: 'plugin:cloud',
  kind: 'plugin',
  state: 'Idle',
  queueDepth: 0,
  ceiling: 16,
  subscribes: [],
  currentCorrelation: null,
  sinceSeq: 0,
} as unknown as ContainerSnapshot;

beforeEach(() => {
  for (const mock of [listCatalog, listPlugins, listConnections, listConnectionProviders, getMember, getPluginSettings, savePluginSettings, updateMember]) {
    mock.mockReset();
  }
  listCatalog.mockResolvedValue({ agents: [{ name: 'claude', mode: 'Headless' }] });
  listPlugins.mockResolvedValue(hostList([cloud]));
  listConnections.mockResolvedValue([mailbox, signIn, smtp]);
  listConnectionProviders.mockResolvedValue([hostProvider({ id: 'google', configured: true })]);
  savePluginSettings.mockResolvedValue(undefined);
  updateMember.mockResolvedValue({ ...member });
});

afterEach(resetBody);

async function mountSettings(bound: Record<string, string>, unusable: UnusableConnection[] = []) {
  getPluginSettings.mockResolvedValue(
    hostSettings(cloud, {
      team: 'alpha',
      member: 'AzureInfrastructure',
      connections: bound,
      connectionFields: cloud.connections ?? {},
      unusableConnections: unusable,
    }),
  );

  setActivePinia(createPinia());
  const board = useConsoleStore();
  board.teams = [{ id: asTeamId('alpha'), name: 'Alpha', memberAgents: ['claude'], containers: [member] } as unknown as Team];
  board.managerName = asMemberId('Manager');

  const wrapper = await mountDialog(MemberSettingsDialog, { snapshot: member }, { pinia: false });
  await settle();
  return wrapper;
}

type Select = { props: (name: string) => unknown; vm: { $emit: (event: string, value: unknown) => void } };

function selects(wrapper: { findAllComponents: (s: { name: string }) => unknown[] }): Select[] {
  return wrapper.findAllComponents({ name: 'QSelect' }) as Select[];
}

const both = { appSecrets: 'conn-mail', 'appSecrets#2': 'conn-key' };
const listed = () => [...document.body.querySelectorAll('[data-connection-slot="appSecrets"] [data-bound-connection]')]
  .map((item) => item.getAttribute('data-connection-id'));

describe('a multiple connection slot in Member settings', () => {
  it('lists every connection already bound, with an Add picker and no single dropdown for the slot', async () => {
    const wrapper = await mountSettings(both);

    expect(listed()).toEqual(['conn-mail', 'conn-key']);
    const text = bodyFind('[data-connection-slot="appSecrets"]')?.textContent ?? '';
    expect(text).toContain('noreply mailbox');
    expect(text).toContain('cloud sign-in');
    const labels = selects(wrapper).map((s) => s.props('label'));
    expect(labels).not.toContain('Connection for appSecrets');
    expect(labels).toContain('Add a connection to appSecrets');

    // The help describes this control, and never sends the person to a page that cannot bind.
    expect(text).toContain('Every connection listed here is bound to appSecrets');
    expect(text).not.toMatch(/Bind[^.;]*Team settings/);
    expect(text).toContain('Connections a deployed app may use.');
    expect(text).toContain('An Update password on a connection reaches the app at its next create.');

    wrapper.unmount();
  });

  it('saving with both still listed submits both, and removes none', async () => {
    const wrapper = await mountSettings(both);

    await type('Region', 'westeurope');
    button('Save').click();
    await settle();

    expect(savePluginSettings).toHaveBeenCalledWith('alpha', 'AzureInfrastructure', {
      config: { region: 'westeurope' },
      connections: both,
    });

    wrapper.unmount();
  });

  it('adding one keeps the others: the new connection joins the list', async () => {
    const wrapper = await mountSettings(both);

    selects(wrapper).find((s) => s.props('label') === 'Add a connection to appSecrets')!.vm.$emit('update:modelValue', 'conn-smtp');
    await settle();
    expect(listed()).toEqual(['conn-mail', 'conn-key', 'conn-smtp']);

    button('Save').click();
    await settle();
    expect(savePluginSettings).toHaveBeenCalledWith('alpha', 'AzureInfrastructure', {
      config: {},
      connections: { ...both, 'appSecrets#3': 'conn-smtp' },
    });

    wrapper.unmount();
  });

  it('an explicit Remove of one submits only the one that remains, naming the removed one', async () => {
    const wrapper = await mountSettings(both);

    const remove = document.body.querySelector<HTMLButtonElement>('[data-connection-id="conn-mail"] [data-remove-connection]')!;
    expect(remove.getAttribute('aria-label')).toContain('Remove noreply mailbox');
    remove.click();
    await settle();
    expect(listed()).toEqual(['conn-key']);

    button('Save').click();
    await settle();
    expect(savePluginSettings).toHaveBeenCalledWith('alpha', 'AzureInfrastructure', {
      config: {},
      connections: { appSecrets: 'conn-key' },
      removedConnections: { appSecrets: ['conn-mail'] },
    });

    wrapper.unmount();
  });

  it('says a binding the active version cannot use, in the Host words', async () => {
    const reason = "Connection 'cloud sign-in' is bound to `appSecrets#2` of this member, and slot `appSecrets` of cloud 2.0.0 no longer takes an API key connection: the binding is kept and not used.";
    const wrapper = await mountSettings(both, [{ key: 'appSecrets#2', slot: 'appSecrets', connection: 'conn-key', name: 'cloud sign-in', reason }]);

    expect(bodyFind('[data-unusable-connection][data-binding-key="appSecrets#2"]')?.textContent).toContain(reason);

    wrapper.unmount();
  });
});
