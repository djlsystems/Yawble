// @vitest-environment happy-dom
//
// CONNECT, RIGHT WHERE A SLOT NEEDS ONE. A plugin member's slot with no suitable connection bound
// offers Connect <Provider> first: it opens Add connection started from that slot - its providers,
// its scopes (the needs read for that plugin and slot), the service step skipped when the slot takes
// one provider. When the sign-in completes, the new connection is bound to the slot through the
// member's own settings route, the one a person's binding uses; a binding the Host refuses is shown
// with its sentence and Reconnect. Choosing an existing connection is still offered beside it.
//
// THE MOCK IS OF `api/client`, in the shapes the Host serves. No provider is called: the sign-in is
// Microsoft's sign-in with a code, faked at the flow read. Every code here is an obviously fake one.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';
import { flushPromises } from '@vue/test-utils';

const {
  listCatalog,
  listPlugins,
  addMember,
  listConnections,
  listConnectionProviders,
  getConnectionNeeds,
  startConnection,
  startDeviceConnection,
  getConnectionFlow,
  listOpenConnectionFlows,
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
  getConnectionNeeds: vi.fn(),
  startConnection: vi.fn(),
  startDeviceConnection: vi.fn(),
  getConnectionFlow: vi.fn(),
  listOpenConnectionFlows: vi.fn(),
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
  getConnectionNeeds,
  startConnection,
  startDeviceConnection,
  getConnectionFlow,
  listOpenConnectionFlows,
  getMember,
  getPluginSettings,
  savePluginSettings,
  updateMember,
}));

vi.mock('../../lib/browserNavigation', () => ({
  goTo,
  currentOrigin: () => 'http://localhost:8080',
}));

import AddMemberDialog from '../AddMemberDialog.vue';
import MemberSettingsDialog from '../MemberSettingsDialog.vue';
import { ActionRefused } from '../../api/client';
import { useConsoleStore } from '../../stores/console';
import {
  asMemberId,
  asTeamId,
  type ConnectionSlot,
  type ContainerSnapshot,
  type InstalledPlugin,
  type PluginMemberSettings,
  type Team,
  type TeamId,
} from '../../api/types';
import { bodyFind, mountDialog, resetBody } from '../../test/mountQuasar';
import { hostConnection, hostField, hostList, hostPlugin, hostProvider, hostSecret, hostSettings, hostSlot } from '../../test/pluginFixtures';
import { button, settle, type } from '../../test/formProbe';
import { announceProviderReturn, leaveForProvider } from '../../lib/providerReturn';
import type { CallbackOutcome } from '../../lib/connections';

const mailSend = 'https://graph.microsoft.com/Mail.Send';
const gmail = 'https://mail.google.com/';

const microsoftOnly = hostSlot({
  description: 'The mailbox to send from.',
  providers: ['microsoft'],
  scopes: { microsoft: [mailSend] },
  required: true,
});
const either = hostSlot({
  description: 'The mailbox to send from.',
  providers: ['google', 'microsoft'],
  scopes: { google: [gmail], microsoft: [mailSend] },
  required: true,
});

const mailer = (slot: ConnectionSlot): InstalledPlugin => hostPlugin({ id: 'mailer', name: 'Mailer', connections: { mail: slot } });

const fresh = hostConnection({
  id: 'conn-new',
  provider: 'microsoft',
  name: 'person@example.test',
  account: 'person@example.test',
  scopes: ['openid', 'email', mailSend],
});
const workMail = hostConnection({ id: 'conn-work', provider: 'microsoft', name: 'Work mail', scopes: ['openid', mailSend] });

const now = new Date('2026-10-04T10:00:00Z');

beforeEach(() => {
  for (const mock of [
    listCatalog,
    listPlugins,
    addMember,
    listConnections,
    listConnectionProviders,
    getConnectionNeeds,
    startConnection,
    startDeviceConnection,
    getConnectionFlow,
    listOpenConnectionFlows,
    getMember,
    getPluginSettings,
    savePluginSettings,
    updateMember,
    goTo,
  ]) {
    mock.mockReset();
  }
  listCatalog.mockResolvedValue({ agents: [{ name: 'claude', mode: 'Headless' }] });
  listConnections.mockResolvedValue([]);
  listConnectionProviders.mockResolvedValue([
    hostProvider({ id: 'google', configured: true }),
    hostProvider({ id: 'microsoft', clientId: '00000000-0000-0000-0000-00000000c1d0', configured: true }),
  ]);
  getConnectionNeeds.mockImplementation(async (provider: string, slot?: { plugin: string; slot: string }) => ({
    provider,
    needs: slot ? [{ plugin: slot.plugin, slot: slot.slot, description: null, scopes: [provider === 'google' ? gmail : mailSend] }] : [],
    scopes: [{ scope: provider === 'google' ? gmail : mailSend, words: 'Send mail as you', plugins: ['Mailer'] }],
    apis: [],
  }));
  listOpenConnectionFlows.mockResolvedValue([]);
  startDeviceConnection.mockResolvedValue({
    flowId: 'flow-1',
    userCode: 'FAKE-CODE',
    verificationUri: 'https://microsoft.example.test/devicelogin',
    expiresAt: '2026-10-04T10:15:00Z',
  });
  getConnectionFlow.mockResolvedValue({ state: 'done', sentence: 'Signed in.', connection: fresh });
  startConnection.mockResolvedValue({
    authorizationUrl: 'https://login.microsoftonline.com/common/oauth2/v2.0/authorize?state=r1',
    state: 'r1',
    redirectUri: 'http://localhost:8080/api/connections/callback',
    expiresAt: '2026-10-04T10:10:00Z',
  });
  savePluginSettings.mockResolvedValue(undefined);
});

afterEach(() => {
  vi.useRealTimers();
  resetBody();
});

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

/** Member settings for a plugin member whose `mail` slot is `slot`, bound as given. */
async function mountMember(
  slot: ConnectionSlot,
  bound: Record<string, string> = {},
  stored: { plugin?: InstalledPlugin; config?: PluginMemberSettings['config']; secrets?: Record<string, string> } = {},
) {
  const plugin = stored.plugin ?? mailer(slot);
  getPluginSettings.mockResolvedValue(
    hostSettings(plugin, {
      team: 'alpha',
      member: 'Inbox',
      config: stored.config ?? {},
      secrets: stored.secrets ?? {},
      connections: bound,
      connectionFields: { mail: slot },
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

/** `settle` on either clock: it waits on a real timer, which a faked one never fires. */
async function settled() {
  if (!vi.isFakeTimers()) return settle();
  await flushPromises();
  await vi.advanceTimersByTimeAsync(0);
  await flushPromises();
}

async function click(element: Element | null | undefined) {
  if (!element) throw new Error('nothing to click');
  (element as HTMLElement).click();
  await settled();
}

const slotButton = (label: string) =>
  [...(bodyFind('[data-connection-slot="mail"]')?.querySelectorAll('button') ?? [])].find((candidate) =>
    candidate.textContent?.includes(label),
  );
const step = () => bodyFind('[data-guided-connect] [data-connect-step]')?.getAttribute('data-connect-step') ?? null;

/** Connect Microsoft, Sign in with Microsoft, and the flow reads done at its first read. */
async function signInFromSlot() {
  await click(slotButton('Connect Microsoft'));
  expect(step()).toBe('signin');
  vi.useFakeTimers({ now, toFake: ['Date', 'setTimeout', 'clearTimeout', 'setInterval', 'clearInterval'] });
  await click(button('Sign in with Microsoft'));
  await vi.advanceTimersByTimeAsync(3000);
  await settled();
}

describe("a plugin member's slot with nothing suitable bound", () => {
  it('offers Connect <Provider> first, opened from that slot with the service step skipped', async () => {
    const wrapper = await mountMember(microsoftOnly);

    const first = bodyFind('[data-connection-slot="mail"] [data-slot-connect]');
    expect(first?.textContent).toContain('Connect Microsoft');

    await click(slotButton('Connect Microsoft'));

    expect(bodyFind('[data-guided-connect]')).not.toBeNull();
    expect(getConnectionNeeds).toHaveBeenCalledWith('microsoft', { plugin: 'mailer', slot: 'mail' });
    // One provider: no choice to make, so straight on to signing in.
    expect(step()).toBe('signin');

    wrapper.unmount();
  });

  it('binds the new connection to the slot through the settings route when the sign-in completes', async () => {
    const wrapper = await mountMember(microsoftOnly);
    listConnections.mockResolvedValue([fresh]);

    await signInFromSlot();

    expect(step()).toBe('result');
    expect(savePluginSettings).toHaveBeenCalledTimes(1);
    expect(savePluginSettings).toHaveBeenCalledWith('alpha', 'Inbox', { config: {}, secrets: {}, connections: { mail: 'conn-new' } });
    // Bound: nothing suitable is missing any more, so Connect is no longer offered.
    expect(bodyFind('[data-connection-slot="mail"] [data-slot-connect]')).toBeNull();

    wrapper.unmount();
  });

  it("keeps the member's stored settings and secret names when it binds the slot", async () => {
    // The route replaces config and secrets with what it is sent: binding sends back what is stored.
    const plugin = hostPlugin({
      id: 'mailer',
      name: 'Mailer',
      connections: { mail: microsoftOnly },
      config: { folder: hostField({ type: 'string', default: 'Inbox' }), labels: hostField({ type: 'list', default: [] }) },
      secrets: { signing: hostSecret() },
    });
    const wrapper = await mountMember(microsoftOnly, {}, {
      plugin,
      config: { folder: 'Jobs', labels: ['urgent'] },
      secrets: { signing: 'MAILER_SIGNING_KEY' },
    });
    listConnections.mockResolvedValue([fresh]);

    await signInFromSlot();

    expect(savePluginSettings).toHaveBeenCalledWith('alpha', 'Inbox', {
      config: { folder: 'Jobs', labels: ['urgent'] },
      secrets: { signing: 'MAILER_SIGNING_KEY' },
      connections: { mail: 'conn-new' },
    });

    wrapper.unmount();
  });

  it('asks which provider when the slot takes two, and reads that slot for the one chosen', async () => {
    const wrapper = await mountMember(either);

    expect(slotButton('Connect Google or Microsoft')).toBeDefined();
    await click(slotButton('Connect Google or Microsoft'));

    expect(step()).toBe('service');
    const services = [...document.body.querySelectorAll('[data-guided-connect] [data-connect-service]')].map((element) =>
      element.getAttribute('data-connect-service'),
    );
    expect(services).toEqual(['google', 'microsoft']);
    expect(getConnectionNeeds).not.toHaveBeenCalled();

    await click(bodyFind('[data-guided-connect] [data-connect-service="google"]'));
    expect(getConnectionNeeds).toHaveBeenCalledWith('google', { plugin: 'mailer', slot: 'mail' });

    wrapper.unmount();
  });

  it('shows the sentence of a binding the Host refuses, and offers Reconnect', async () => {
    const sentence =
      "Connection 'person@example.test' was not granted the scope `https://graph.microsoft.com/Mail.Send` that slot `mail` needs. " +
      'Reconnect it from Admin → Connections with that scope, then bind it again.';
    savePluginSettings.mockRejectedValue(
      Object.assign(new ActionRefused(sentence, { error: sentence, reconnect: { connectionId: 'conn-new', scopes: [mailSend] } }), {
        status: 400,
      }),
    );
    const wrapper = await mountMember(microsoftOnly);
    listConnections.mockResolvedValue([fresh]);

    await signInFromSlot();

    const refused = bodyFind('[data-connection-slot="mail"] [data-bind-refused]');
    expect(refused?.textContent).toContain(sentence);
    vi.useRealTimers();

    await click([...refused!.querySelectorAll('button')].find((candidate) => candidate.textContent?.includes('Reconnect')));
    expect(startConnection).toHaveBeenCalledWith({ reconnectId: 'conn-new', scopes: [mailSend] });
    expect(goTo).toHaveBeenCalledWith('https://login.microsoftonline.com/common/oauth2/v2.0/authorize?state=r1');

    wrapper.unmount();
  });
});

describe("a Google slot's sign-in, in another tab", () => {
  const googleOnly = hostSlot({ providers: ['google'], scopes: { google: [gmail] }, required: true });
  const made = hostConnection({ id: 'conn-g', provider: 'google', name: 'Dana', scopes: ['openid', 'email', gmail] });

  const googleFlow = {
    authorizationUrl: 'https://accounts.google.com/o/oauth2/v2/auth?state=r2',
    state: 'r2',
    redirectUri: 'http://localhost:8080/api/connections/callback',
    expiresAt: '2026-10-04T10:10:00Z',
  };

  /** Connect Google and Sign in with Google: the link to the other tab, or null. */
  async function startGoogle() {
    startConnection.mockResolvedValue(googleFlow);
    await click(slotButton('Connect Google'));
    expect(step()).toBe('signin');
    await click(button('Sign in with Google'));
    return bodyFind('[data-guided-connect] a[data-signin-link]');
  }

  /** The other tab: opened from the link, sent on to the provider, and back with `outcome`. */
  async function otherTab(href: string, outcome: CallbackOutcome) {
    const tag = new URLSearchParams(href.slice(href.indexOf('?'))).get('signin')!;
    const sent = await leaveForProvider(tag);
    expect(await announceProviderReturn(outcome)).toBe(true);
    await new Promise((resolve) => setTimeout(resolve, 20));
    await settle();
    return sent;
  }

  afterEach(() => sessionStorage.clear());

  it('opens the provider in a new tab, leaves this page as it is, and binds the connection the other tab came back with', async () => {
    const wrapper = await mountMember(googleOnly);

    const link = await startGoogle();

    expect(startConnection).toHaveBeenCalledWith({ provider: 'google', scopes: [gmail] });
    expect(goTo).not.toHaveBeenCalled();
    expect(link?.getAttribute('target')).toBe('_blank');
    expect(link?.getAttribute('rel')).toContain('noopener');

    // The other tab is sent on to the provider, and says what came back.
    listConnections.mockResolvedValue([made]);
    const sent = await otherTab(link!.getAttribute('href')!, { outcome: 'connected', id: 'conn-g' });

    expect(sent).toBe(googleFlow.authorizationUrl);
    expect(step()).toBe('result');
    expect(savePluginSettings).toHaveBeenCalledWith('alpha', 'Inbox', { config: {}, secrets: {}, connections: { mail: 'conn-g' } });

    wrapper.unmount();
  });

  it('binds only the connection its own sign-in came back with, not one another sign-in announces', async () => {
    const wrapper = await mountMember(googleOnly);
    const link = await startGoogle();
    listConnections.mockResolvedValue([made, workMail]);

    // Another tab's sign-in - another slot's, or Admin → Connections' - comes back while this one waits.
    sessionStorage.setItem('connections.tab', 'another-sign-in');
    void announceProviderReturn({ outcome: 'connected', id: 'conn-work' }, 20);
    const stray = new BroadcastChannel('connections.return');
    stray.postMessage({ outcome: 'connected', id: 'conn-work' });
    stray.close();
    await new Promise((resolve) => setTimeout(resolve, 20));
    await settle();

    expect(savePluginSettings).not.toHaveBeenCalled();
    expect(step()).toBe('signin');

    // Its own comes back: that one is bound.
    await otherTab(link!.getAttribute('href')!, { outcome: 'connected', id: 'conn-g' });
    expect(savePluginSettings).toHaveBeenCalledTimes(1);
    expect(savePluginSettings).toHaveBeenCalledWith('alpha', 'Inbox', { config: {}, secrets: {}, connections: { mail: 'conn-g' } });

    wrapper.unmount();
  });
});

describe("a slot's Add connection and a sign-in with a code already waiting", () => {
  const googleOnly = hostSlot({ providers: ['google'], scopes: { google: [gmail] }, required: true });
  const waitingMicrosoft = {
    flowId: 'flow-waiting',
    provider: 'microsoft',
    userCode: 'FAKE-WAIT',
    verificationUri: 'https://microsoft.example.test/devicelogin',
    expiresAt: '2099-10-04T10:15:00Z',
    state: 'waiting' as const,
  };

  it("starts the slot's own sign-in when the waiting one is for a provider the slot does not take", async () => {
    listOpenConnectionFlows.mockResolvedValue([waitingMicrosoft]);
    const wrapper = await mountMember(googleOnly);

    await click(slotButton('Connect Google'));

    const dialog = bodyFind('[data-guided-connect]')!;
    expect(dialog.textContent).not.toContain('FAKE-WAIT');
    expect(getConnectionFlow).not.toHaveBeenCalled();
    expect(getConnectionNeeds).toHaveBeenCalledWith('google', { plugin: 'mailer', slot: 'mail' });
    expect(step()).toBe('signin');
    expect(button('Sign in with Google')).toBeDefined();

    wrapper.unmount();
  });

  it('picks the waiting one back up when the slot takes its provider', async () => {
    listOpenConnectionFlows.mockResolvedValue([waitingMicrosoft]);
    getConnectionFlow.mockResolvedValue({ state: 'waiting', sentence: 'Waiting for you to sign in.', connection: null });
    const wrapper = await mountMember(microsoftOnly);

    await click(slotButton('Connect Microsoft'));

    expect(bodyFind('[data-guided-connect]')?.textContent).toContain('FAKE-WAIT');
    expect(getConnectionNeeds).not.toHaveBeenCalled();

    wrapper.unmount();
  });
});

describe('a slot with a connection to choose', () => {
  it('still offers Choose an existing connection beside Connect when none is bound', async () => {
    listConnections.mockResolvedValue([workMail]);
    const wrapper = await mountMember(microsoftOnly);

    const slot = bodyFind('[data-connection-slot="mail"]')!;
    expect(slotButton('Connect Microsoft')).toBeDefined();
    expect(slot.textContent).toContain('Or choose an existing connection');
    expect(slot.querySelector('[data-no-connections]')).toBeNull();

    wrapper.unmount();
  });

  it('offers no Connect while a suitable connection is bound', async () => {
    listConnections.mockResolvedValue([workMail]);
    const wrapper = await mountMember(microsoftOnly, { mail: 'conn-work' });

    expect(bodyFind('[data-connection-slot="mail"] [data-slot-connect]')).toBeNull();
    expect(bodyFind('[data-connection-slot="mail"] [data-binding-refusal]')).toBeNull();

    wrapper.unmount();
  });
});

describe('a slot that takes only a custom provider', () => {
  it('offers no Connect, and still says where such a connection is made', async () => {
    // Add connection signs in to Google and Microsoft only: a custom provider's account is connected
    // in Admin → Connections, and with nothing to choose the slot must still say so.
    const custom = hostSlot({ providers: ['custom-crm'], scopes: { 'custom-crm': ['read'] }, required: true });
    const wrapper = await mountMember(custom);

    const slot = bodyFind('[data-connection-slot="mail"]')!;
    expect(slot.querySelector('[data-slot-connect]')).toBeNull();
    expect(slot.querySelector('[data-no-connections]')?.textContent).toContain('Admin → Connections');

    wrapper.unmount();
  });
});

describe('a slot at hire, before the member exists', () => {
  it('selects the new connection in the picker, and the hire binds it', async () => {
    listPlugins.mockResolvedValue(hostList([mailer(microsoftOnly)]));
    addMember.mockResolvedValue({ name: 'Inbox' });
    const wrapper = await mountDialog(AddMemberDialog, { team: 'alpha' as TeamId, teamName: 'Alpha', memberAgents: ['claude'] });
    await type('Member name', 'Inbox');
    const agent = wrapper.findAllComponents({ name: 'QSelect' }).find((select) => select.props('label') === 'Agent')!;
    agent.vm.$emit('update:modelValue', 'plugin:mailer');
    await settle();
    listConnections.mockResolvedValue([fresh]);

    await signInFromSlot();
    vi.useRealTimers();

    expect(savePluginSettings).not.toHaveBeenCalled();
    await click(button('Finish'));
    await click(button('Add member'));

    expect(addMember).toHaveBeenCalledWith('alpha', 'Inbox', 'plugin:mailer', { config: {}, secrets: {}, connections: { mail: 'conn-new' } });

    wrapper.unmount();
  });
});
