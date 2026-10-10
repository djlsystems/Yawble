// @vitest-environment happy-dom
//
// A SLOT'S CONNECT STAYS OPEN WHILE A PERSON TYPES, wherever the slot is. Add connection opened from
// a slot lives inside the member's settings or Add member; Quasar closes a dialog when the route
// changes, and closing the parent unmounts Add connection with whatever was typed in it. The parents
// stay open on a route change, as Connections and Add connection do.
//
// THE MOCK IS OF `api/client`, in the shapes the Host serves. No request leaves the test; the router
// is an in-memory one.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';
import { createMemoryHistory, createRouter, type Router } from 'vue-router';

const {
  listCatalog,
  listPlugins,
  listConnections,
  listConnectionProviders,
  listOpenConnectionFlows,
  listMailboxPresets,
  getConnectionNeeds,
  getPluginSettings,
  updateMember,
} = vi.hoisted(() => ({
  listCatalog: vi.fn(),
  listPlugins: vi.fn(),
  listConnections: vi.fn(),
  listConnectionProviders: vi.fn(),
  listOpenConnectionFlows: vi.fn(),
  listMailboxPresets: vi.fn(),
  getConnectionNeeds: vi.fn(),
  getPluginSettings: vi.fn(),
  updateMember: vi.fn(),
}));

vi.mock('../../api/client', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  listCatalog,
  listPlugins,
  listConnections,
  listConnectionProviders,
  listOpenConnectionFlows,
  listMailboxPresets,
  getConnectionNeeds,
  getPluginSettings,
  updateMember,
}));

vi.mock('../../lib/browserNavigation', () => ({
  goTo: vi.fn(),
  currentOrigin: () => 'https://instance.example.test',
}));

import AddMemberDialog from '../AddMemberDialog.vue';
import MemberSettingsDialog from '../MemberSettingsDialog.vue';
import { useConsoleStore } from '../../stores/console';
import { asMemberId, asTeamId, type ContainerSnapshot, type MailboxPreset, type Team, type TeamId } from '../../api/types';
import { bodyFind, mountDialog, resetBody } from '../../test/mountQuasar';
import { hostList, hostPlugin, hostProvider, hostSettings, hostSlot } from '../../test/pluginFixtures';
import { button, field, settle, type } from '../../test/formProbe';

const presets: MailboxPreset[] = [
  { id: 'gmail', name: 'Gmail', imap: { host: 'imap.gmail.com', port: 993, security: 'TLS' }, smtp: { host: 'smtp.gmail.com', port: 465, security: 'TLS' } },
  { id: 'other', name: 'Other', imap: null, smtp: null },
];

const mailbox = hostSlot({ description: 'The mailbox to read.', providers: ['imap'], scopes: {}, required: true });
const reader = hostPlugin({ id: 'reader', name: 'Reader', connections: { mail: mailbox } });

const inbox = {
  team: asTeamId('alpha'),
  id: asMemberId('Inbox'),
  name: 'Inbox',
  agent: 'plugin:reader',
  kind: 'plugin',
  state: 'Idle',
  queueDepth: 0,
  ceiling: 16,
  subscribes: [],
  currentCorrelation: null,
  sinceSeq: 0,
} as unknown as ContainerSnapshot;

let router: Router;

beforeEach(async () => {
  for (const mock of [
    listCatalog,
    listPlugins,
    listConnections,
    listConnectionProviders,
    listOpenConnectionFlows,
    listMailboxPresets,
    getConnectionNeeds,
    getPluginSettings,
    updateMember,
  ]) {
    mock.mockReset();
  }
  listCatalog.mockResolvedValue({ agents: [{ name: 'claude', mode: 'Headless' }] });
  listPlugins.mockResolvedValue(hostList([reader]));
  listConnections.mockResolvedValue([]);
  listConnectionProviders.mockResolvedValue([hostProvider({ id: 'google' }), hostProvider({ id: 'microsoft' })]);
  listOpenConnectionFlows.mockResolvedValue([]);
  listMailboxPresets.mockResolvedValue(presets);
  getConnectionNeeds.mockResolvedValue({ provider: 'imap', needs: [], scopes: [], apis: [] });
  getPluginSettings.mockResolvedValue(
    hostSettings(reader, { team: 'alpha', member: 'Inbox', config: {}, secrets: {}, connections: {}, connectionFields: { mail: mailbox } }),
  );
  updateMember.mockResolvedValue({ ...inbox });

  router = createRouter({ history: createMemoryHistory(), routes: [{ path: '/:any(.*)', component: { template: '<div />' } }] });
  await router.push('/console');
});

afterEach(resetBody);

async function mountSettings() {
  setActivePinia(createPinia());
  const board = useConsoleStore();
  board.teams = [{ id: asTeamId('alpha'), name: 'Alpha', memberAgents: ['claude'], containers: [inbox] } as unknown as Team];
  board.managerName = asMemberId('Manager');

  const wrapper = await mountDialog(MemberSettingsDialog, { snapshot: inbox }, { pinia: false, global: { plugins: [router] } });
  await settle();
  return wrapper;
}

async function mountAddMember() {
  const wrapper = await mountDialog(
    AddMemberDialog,
    { team: 'alpha' as TeamId, teamName: 'Alpha', memberAgents: ['claude'] },
    { global: { plugins: [router] } },
  );
  await type('Member name', 'Inbox');
  const agent = wrapper.findAllComponents({ name: 'QSelect' }).find((select) => select.props('label') === 'Agent')!;
  agent.vm.$emit('update:modelValue', 'plugin:reader');
  await settle();
  return wrapper;
}

/** The slot's Connect a mailbox, Gmail, and part of an app password typed. */
async function typeInSlotConnect() {
  button('Connect a mailbox').click();
  await settle();
  bodyFind('[data-mailbox-preset="gmail"]')!.click();
  await settle();
  await type('Email address', 'me@example.com');
  await type('App password', 'abcd efgh');
}

async function routeChanges() {
  await router.replace({ path: '/console', query: { team: 'another' } });
  await settle();
}

function expectStillTyping() {
  expect(bodyFind('[data-guided-connect]')).not.toBeNull();
  expect(field('Email address').value).toBe('me@example.com');
  expect(field('App password').value).toBe('abcd efgh');
}

describe("a slot's Connect, when the route changes under it", () => {
  it("stays open with what was typed in a member's settings", async () => {
    const wrapper = await mountSettings();
    await typeInSlotConnect();
    expectStillTyping();

    await routeChanges();

    expectStillTyping();
    expect(bodyFind('[data-connection-slot="mail"]')).not.toBeNull();

    wrapper.unmount();
  });

  it('stays open with what was typed in Add member', async () => {
    const wrapper = await mountAddMember();
    await typeInSlotConnect();
    expectStillTyping();

    await routeChanges();

    expectStillTyping();
    expect(field('Member name').value).toBe('Inbox');

    wrapper.unmount();
  });
});
