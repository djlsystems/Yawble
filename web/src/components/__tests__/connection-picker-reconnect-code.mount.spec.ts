// @vitest-environment happy-dom
//
// RECONNECT FROM A SLOT'S PICKER, FOR MICROSOFT: WITH A CODE, IN PLACE. A Microsoft connection signs
// in only with a code, so the picker's Reconnect - beside a chosen connection that lacks a scope, and
// the one a refused binding offers - asks the Host for a code (`flow: "device"`, the connection's ID
// and the scopes the slot still needs) and shows it where the person is, through the same code
// sign-in Add connection shows: the code, the link, the countdown. The page is never left. Once the
// code is approved the connection is read again and bound; a refused or expired code says so in the
// Host's sentence and binds nothing. Every other provider keeps the browser sign-in.
//
// THE MOCK IS OF `api/client`, in the shapes the Host serves. Every code and address is a fake one.
// Timers are faked from the code on, so the flow's reads are driven by the spec.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils';

const {
  listCatalog,
  listPlugins,
  addMember,
  listConnections,
  listConnectionProviders,
  startConnection,
  startDeviceConnection,
  getConnectionFlow,
  goTo,
} = vi.hoisted(() => ({
  listCatalog: vi.fn(),
  listPlugins: vi.fn(),
  addMember: vi.fn(),
  listConnections: vi.fn(),
  listConnectionProviders: vi.fn(),
  startConnection: vi.fn(),
  startDeviceConnection: vi.fn(),
  getConnectionFlow: vi.fn(),
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
  startDeviceConnection,
  getConnectionFlow,
}));

vi.mock('../../lib/browserNavigation', () => ({
  goTo,
  currentOrigin: () => 'https://instance.example.test',
}));

import AddMemberDialog from '../AddMemberDialog.vue';
import ConnectionPicker from '../ConnectionPicker.vue';
import SlotConnect from '../SlotConnect.vue';
import type { Connection, InstalledPlugin, TeamId } from '../../api/types';
import { bodyFind, mountDialog, resetBody } from '../../test/mountQuasar';
import { hostConnection, hostList, hostPlugin, hostProvider, hostSlot } from '../../test/pluginFixtures';
import { button, settle, type } from '../../test/formProbe';

const smtp = 'https://outlook.office.com/SMTP.Send';
const gmail = 'https://mail.google.com/';
const fakeCode = 'FAKE-CODE-ONE';
const verificationUri = 'https://login.example.test/device';

const mailSlot = hostSlot({
  description: 'The mailbox to read and send from.',
  providers: ['google', 'microsoft'],
  scopes: { google: [gmail], microsoft: [smtp] },
  required: true,
  summary: 'needs a Google or Microsoft connection',
});

const mailer: InstalledPlugin = hostPlugin({ id: 'mailer', name: 'Mailer', connections: { mail: mailSlot } });

const outlookShort = hostConnection({
  id: 'conn-ms',
  name: 'Office mail',
  provider: 'microsoft',
  account: 'person@example.test',
  scopes: ['openid', 'email'],
});
const outlookFull: Connection = { ...outlookShort, scopes: ['openid', 'email', smtp] };
const googleShort = hostConnection({ id: 'conn-read', name: 'Read only', provider: 'google', account: 'reader@example.com' });

const providers = [hostProvider({ id: 'google', configured: true }), hostProvider({ id: 'microsoft', configured: true, clientSource: 'default' })];

const now = new Date('2026-10-04T10:00:00Z');

beforeEach(() => {
  for (const mock of [listCatalog, listPlugins, addMember, listConnections, listConnectionProviders, startConnection, startDeviceConnection, getConnectionFlow, goTo]) {
    mock.mockReset();
  }
  listCatalog.mockResolvedValue({ agents: [{ name: 'claude', mode: 'Headless' }] });
  listPlugins.mockResolvedValue(hostList([mailer]));
  addMember.mockResolvedValue({ name: 'Inbox' });
  listConnections.mockResolvedValue([outlookShort, googleShort]);
  listConnectionProviders.mockResolvedValue(providers);
  startConnection.mockResolvedValue({
    authorizationUrl: 'https://accounts.google.com/o/oauth2/v2/auth?state=r1',
    state: 'r1',
    redirectUri: 'https://instance.example.test/api/connections/callback',
    expiresAt: '2026-10-04T10:10:00Z',
  });
  startDeviceConnection.mockResolvedValue({ flowId: 'flow-1', userCode: fakeCode, verificationUri, expiresAt: '2026-10-04T10:15:00Z' });
  getConnectionFlow.mockResolvedValue({ state: 'waiting', sentence: 'Waiting for you to sign in.' });
});

afterEach(() => {
  vi.useRealTimers();
  resetBody();
});

/** From the code on the clock is the spec's: pinned at `now`, moved on only by `advance`. */
function pinClock() {
  vi.useFakeTimers({ now, toFake: ['Date', 'setTimeout', 'clearTimeout', 'setInterval', 'clearInterval'] });
}

async function settled() {
  if (!vi.isFakeTimers()) return settle();
  await flushPromises();
  await vi.advanceTimersByTimeAsync(0);
  await flushPromises();
}

async function advance(ms: number) {
  await vi.advanceTimersByTimeAsync(ms);
  await settled();
}

type Select = { props: (name: string) => unknown; vm: { $emit: (event: string, value: unknown) => void } };

function select(wrapper: VueWrapper, label: string): Select {
  const found = (wrapper.findAllComponents({ name: 'QSelect' }) as unknown as Select[]).find((s) => s.props('label') === label);
  if (!found) throw new Error(`no QSelect labelled "${label}"`);
  return found;
}

async function choose(wrapper: VueWrapper, label: string, value: string | null) {
  select(wrapper, label).vm.$emit('update:modelValue', value);
  await settled();
}

const slot = () => bodyFind('[data-connection-slot="mail"]')!;

function reconnectButton(): HTMLElement {
  const found = [...slot().querySelectorAll('button')].find((candidate) => candidate.textContent?.trim() === 'Reconnect');
  if (!found) throw new Error('no Reconnect in the slot');
  return found as HTMLElement;
}

async function mountHire() {
  const wrapper = await mountDialog(AddMemberDialog, { team: 'alpha' as TeamId, teamName: 'Alpha', memberAgents: ['claude'] });
  await type('Member name', 'Inbox');
  await choose(wrapper, 'Agent', 'plugin:mailer');
  await settle();
  return wrapper;
}

/** Add member, the Microsoft connection chosen, Reconnect pressed: the code is up. */
async function reconnectInAddMember() {
  const wrapper = await mountHire();
  await choose(wrapper, 'Connection for mail', 'conn-ms');
  expect(slot().querySelector('[data-binding-refusal]')?.textContent).toContain(smtp);
  pinClock();
  reconnectButton().click();
  await settled();
  return wrapper;
}

describe("Reconnect on a Microsoft connection, in Add member's picker", () => {
  it('starts a sign-in with a code for that connection and the missing scopes, shows the code and the link, and never leaves', async () => {
    const wrapper = await reconnectInAddMember();

    expect(startDeviceConnection).toHaveBeenCalledWith({ reconnectId: 'conn-ms', scopes: [smtp], flow: 'device' });
    expect(startConnection).not.toHaveBeenCalled();
    expect(goTo).not.toHaveBeenCalled();

    expect(slot().querySelector('[data-device-code]')?.textContent).toContain(fakeCode);
    const link = slot().querySelector('a[data-device-link]')!;
    expect(link.getAttribute('href')).toBe(verificationUri);
    expect(link.getAttribute('target')).toBe('_blank');
    expect(slot().querySelector('[data-device-countdown]')?.textContent).toContain('15:00');
    // Plain words: what the person reads names no protocol.
    const words = slot().querySelector('[data-reconnect-code]')!.textContent!.replace(verificationUri, '');
    expect(words).not.toMatch(/device|oauth|tenant/i);

    await advance(10_000);
    expect(getConnectionFlow).toHaveBeenCalledWith('flow-1');
    expect(goTo).not.toHaveBeenCalled();

    wrapper.unmount();
  });

  it('shows the second code in place when Microsoft asks for one', async () => {
    const wrapper = await reconnectInAddMember();

    getConnectionFlow.mockResolvedValue({
      state: 'waiting',
      sentence: 'Microsoft needs a second code to finish the sign-in.',
      userCode: 'FAKE-CODE-TWO',
      verificationUri,
      expiresAt: '2026-10-04T10:20:03Z',
    });
    await advance(3_000);

    expect(slot().querySelector('[data-device-code]')?.textContent).toContain('FAKE-CODE-TWO');
    expect(slot().querySelector('[data-first-code-done]')).not.toBeNull();
    expect(slot().querySelector('[data-code-number]')?.textContent).toContain('Code 2 of 2');

    wrapper.unmount();
  });

  it('once approved, reads the connection again and binds it: the refusal goes and Add member hires with it', async () => {
    const wrapper = await reconnectInAddMember();
    const reads = listConnections.mock.calls.length;

    listConnections.mockResolvedValue([outlookFull, googleShort]);
    getConnectionFlow.mockResolvedValue({ state: 'done', sentence: 'Reconnected person@example.test.', connection: outlookFull });
    await advance(3_000);

    expect(listConnections.mock.calls.length).toBeGreaterThan(reads);
    expect(select(wrapper, 'Connection for mail').props('modelValue')).toBe('conn-ms');
    expect(slot().querySelector('[data-binding-refusal]')).toBeNull();
    expect(slot().querySelector('[data-device-code]')).toBeNull();
    expect(slot().querySelector('[data-reconnect-done]')?.textContent).toContain('Reconnected Office mail (person@example.test).');

    vi.useRealTimers();
    button('Add member').click();
    await settle();
    expect(addMember).toHaveBeenCalledWith('alpha', 'Inbox', 'plugin:mailer', { config: {}, secrets: {}, connections: { mail: 'conn-ms' } });

    wrapper.unmount();
  });

  it.each([
    ['refused', 'Microsoft refused the sign-in: the person declined.'],
    ['expired', 'The code expired before it was entered. Press Reconnect for a new one.'],
  ] as const)('a %s code shows its sentence, binds nothing, and Reconnect can be pressed again', async (state, sentence) => {
    const wrapper = await reconnectInAddMember();
    const reads = listConnections.mock.calls.length;

    getConnectionFlow.mockResolvedValue({ state, sentence });
    await advance(3_000);

    expect(slot().querySelector('[data-device-sentence]')?.textContent).toContain(sentence);
    expect(slot().querySelector('[data-device-code]')).toBeNull();
    expect(slot().querySelector('[data-reconnect-done]')).toBeNull();
    expect(listConnections.mock.calls.length).toBe(reads);
    expect(slot().querySelector('[data-binding-refusal]')?.textContent).toContain(smtp);
    expect(goTo).not.toHaveBeenCalled();

    reconnectButton().click();
    await settled();
    expect(startDeviceConnection).toHaveBeenCalledTimes(2);
    expect(slot().querySelector('[data-device-code]')?.textContent).toContain(fakeCode);

    wrapper.unmount();
  });
});

describe('Reconnect on a Google connection, in the same picker', () => {
  it('still goes to the browser sign-in', async () => {
    const wrapper = await mountHire();
    await choose(wrapper, 'Connection for mail', 'conn-read');

    reconnectButton().click();
    await settled();

    expect(startConnection).toHaveBeenCalledWith({ reconnectId: 'conn-read', scopes: [gmail] });
    expect(goTo).toHaveBeenCalledWith('https://accounts.google.com/o/oauth2/v2/auth?state=r1');
    expect(startDeviceConnection).not.toHaveBeenCalled();
    expect(slot().querySelector('[data-device-code]')).toBeNull();

    wrapper.unmount();
  });
});

describe('Reconnect a refused binding offers, for a Microsoft connection', () => {
  it('signs in with a code for the refused scopes, then binds the connection again', async () => {
    const refused = Object.assign(new Error("Connection 'Office mail' was not granted the scope that slot `mail` needs."), {
      body: { reconnect: { connectionId: 'conn-ms', scopes: [smtp] } },
    });
    const bind = vi.fn().mockRejectedValueOnce(refused).mockResolvedValue(undefined);
    const wrapper = mount(ConnectionPicker, {
      props: { slotName: 'mail', spec: mailSlot, connections: [outlookShort, googleShort], providers, plugin: 'mailer', bind, modelValue: '' },
      attachTo: document.body,
    });
    await settle();

    // Connect from the slot made the connection; the Host refused to bind it.
    wrapper.findComponent(SlotConnect).vm.$emit('connected', outlookShort);
    await settle();
    expect(slot().querySelector('[data-bind-refused]')?.textContent).toContain('was not granted');

    pinClock();
    reconnectButton().click();
    await settled();

    expect(startDeviceConnection).toHaveBeenCalledWith({ reconnectId: 'conn-ms', scopes: [smtp], flow: 'device' });
    expect(startConnection).not.toHaveBeenCalled();
    expect(goTo).not.toHaveBeenCalled();
    expect(slot().querySelector('[data-device-code]')?.textContent).toContain(fakeCode);

    listConnections.mockResolvedValue([outlookFull, googleShort]);
    getConnectionFlow.mockResolvedValue({ state: 'done', sentence: 'Reconnected.', connection: outlookFull });
    await advance(3_000);

    expect(bind).toHaveBeenCalledTimes(2);
    expect(bind).toHaveBeenLastCalledWith('conn-ms');
    expect(slot().querySelector('[data-bind-refused]')).toBeNull();
    expect(wrapper.emitted('connected')?.at(-1)).toEqual([outlookFull]);

    wrapper.unmount();
  });
});
