// @vitest-environment happy-dom
//
// A SLOT'S RECONNECT NAMES THE DIRECTORY. A binding refused for missing scopes offers Reconnect in the
// slot's picker (and in the solution wizard, SlotReconnect). For a Microsoft connection it shows the
// same optional Directory field as Add connection - DirectoryField, with UsernameIncorrectHint under
// it - and sends what is typed as `tenant`; left empty, no `tenant` is sent and the Host keeps the
// connection's own (a connection made with none signs in through `common`, where a personal Microsoft
// account cannot get Azure tokens). A Google connection shows no such field. Cancel beside the code
// ends the sign-in at the Host.
//
// THE MOCK IS OF `api/client`, in the shapes the Host serves. Every code and directory is a fake one.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { mount, type VueWrapper } from '@vue/test-utils';

const { listConnections, startConnection, startDeviceConnection, getConnectionFlow, cancelConnectionFlow, goTo } = vi.hoisted(() => ({
  listConnections: vi.fn(),
  startConnection: vi.fn(),
  startDeviceConnection: vi.fn(),
  getConnectionFlow: vi.fn(),
  cancelConnectionFlow: vi.fn(),
  goTo: vi.fn(),
}));

vi.mock('../../api/client', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  listConnections,
  startConnection,
  startDeviceConnection,
  getConnectionFlow,
  cancelConnectionFlow,
}));

vi.mock('../../lib/browserNavigation', () => ({
  goTo,
  currentOrigin: () => 'https://instance.example.test',
}));

import ConnectionPicker from '../ConnectionPicker.vue';
import SlotReconnect from '../SlotReconnect.vue';
import { resetBody } from '../../test/mountQuasar';
import { DirectoryLabel, UsernameIncorrectHint } from '../../lib/connections';
import { hostConnection, hostProvider, hostSlot } from '../../test/pluginFixtures';
import { field, fieldWrapper, settle, type } from '../../test/formProbe';

const smtp = 'https://outlook.office.com/SMTP.Send';
const gmail = 'https://mail.google.com/';
const directoryGuid = 'aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee';
const label = `${DirectoryLabel} (optional)`;
const refusal =
  'Directory (tenant) ID or domain is a directory ID such as xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx or a domain such as example.onmicrosoft.com.';
const code = { flowId: 'flow-1', userCode: 'FAKE-CODE', verificationUri: 'https://login.example.test/device', expiresAt: '2099-10-04T10:15:00Z' };

const mailSlot = hostSlot({
  description: 'The mailbox.',
  providers: ['google', 'microsoft'],
  scopes: { google: [gmail], microsoft: [smtp] },
  required: true,
});
// Made with no directory: today it would sign in again through `common`.
const outlookShort = hostConnection({ id: 'conn-ms', name: 'Office mail', provider: 'microsoft', account: 'person@example.test', scopes: ['openid', 'email'], tenant: null });
const googleShort = hostConnection({ id: 'conn-read', name: 'Read only', provider: 'google', account: 'reader@example.com' });
const providers = [hostProvider({ id: 'google', configured: true }), hostProvider({ id: 'microsoft', configured: true, clientSource: 'default' })];

beforeEach(() => {
  for (const mock of [listConnections, startConnection, startDeviceConnection, getConnectionFlow, cancelConnectionFlow, goTo]) mock.mockReset();
  listConnections.mockResolvedValue([outlookShort, googleShort]);
  startConnection.mockResolvedValue({ authorizationUrl: 'https://accounts.google.com/o/oauth2/v2/auth?state=r1', state: 'r1', redirectUri: 'r', expiresAt: '2099-10-04T10:10:00Z' });
  startDeviceConnection.mockResolvedValue(code);
  getConnectionFlow.mockResolvedValue({ state: 'waiting', sentence: 'Waiting for you to sign in.' });
  cancelConnectionFlow.mockResolvedValue(undefined);
});

afterEach(resetBody);

const mounted: VueWrapper[] = [];
afterEach(() => {
  while (mounted.length > 0) mounted.pop()!.unmount();
});

async function picker(chosen: string) {
  const wrapper = mount(ConnectionPicker, {
    props: { slotName: 'mail', spec: mailSlot, connections: [outlookShort, googleShort], providers, modelValue: chosen },
    attachTo: document.body,
  });
  mounted.push(wrapper);
  await settle();
  return wrapper;
}

function reconnectIn(root: ParentNode): HTMLElement {
  const found = [...root.querySelectorAll('button')].find((candidate) => candidate.textContent?.trim() === 'Reconnect');
  if (!found) throw new Error('no Reconnect');
  return found as HTMLElement;
}

async function click(element: Element | null) {
  if (!element) throw new Error('nothing to click');
  (element as HTMLElement).click();
  await settle();
}

describe("The slot picker's Reconnect on a Microsoft connection", () => {
  it('shows the optional Directory field with the username hint under it', async () => {
    const wrapper = await picker('conn-ms');

    expect(wrapper.find('[data-binding-refusal]').text()).toContain(smtp);
    expect(wrapper.find('[data-reconnect-directory] [data-directory-field]').exists()).toBe(true);
    expect(field(label).value).toBe('');
    expect(wrapper.find('[data-reconnect-directory-hint]').text()).toBe(UsernameIncorrectHint);
  });

  it('sends the directory typed as tenant', async () => {
    const wrapper = await picker('conn-ms');
    await type(label, `  ${directoryGuid} `);
    await click(reconnectIn(wrapper.element));

    expect(startDeviceConnection).toHaveBeenCalledWith({ reconnectId: 'conn-ms', scopes: [smtp], flow: 'device', tenant: directoryGuid });
    expect(wrapper.find('[data-device-code]').text()).toContain('FAKE-CODE');
    // The field goes while the code is shown.
    expect(wrapper.find('[data-reconnect-directory]').exists()).toBe(false);
  });

  it('sends no tenant when the field is left empty', async () => {
    const wrapper = await picker('conn-ms');
    await click(reconnectIn(wrapper.element));

    expect(startDeviceConnection).toHaveBeenCalledWith({ reconnectId: 'conn-ms', scopes: [smtp], flow: 'device' });
    expect(startDeviceConnection.mock.calls[0]![0]).not.toHaveProperty('tenant');
  });

  it("shows the Host's refusal of the directory at the field and no code", async () => {
    startDeviceConnection.mockRejectedValue(new Error(refusal));
    const wrapper = await picker('conn-ms');
    await type(label, 'not a directory');
    await click(reconnectIn(wrapper.element));

    expect(fieldWrapper(label).textContent).toContain(refusal);
    expect(wrapper.find('[data-reconnect-problem]').exists()).toBe(false);
    expect(wrapper.find('[data-device-code]').exists()).toBe(false);
  });

  it('Cancel beside the code ends it at the Host and brings the field back', async () => {
    const wrapper = await picker('conn-ms');
    await click(reconnectIn(wrapper.element));
    await click(wrapper.find('[data-device-cancel]').element);

    expect(cancelConnectionFlow).toHaveBeenCalledWith('flow-1');
    expect(wrapper.find('[data-device-code]').exists()).toBe(false);
    expect(wrapper.find('[data-reconnect-directory]').exists()).toBe(true);
  });

  it('shows no directory field for a Google connection', async () => {
    const wrapper = await picker('conn-read');

    expect(wrapper.find('[data-binding-refusal]').exists()).toBe(true);
    expect(wrapper.find('[data-reconnect-directory]').exists()).toBe(false);
  });
});

describe("The solution wizard's Reconnect (SlotReconnect) on a Microsoft connection", () => {
  async function slotReconnect() {
    const wrapper = mount(SlotReconnect, {
      props: { connectionId: 'conn-ms', scopes: [smtp], providerName: 'Microsoft', withCode: true },
      attachTo: document.body,
    });
    mounted.push(wrapper);
    await settle();
    return wrapper;
  }

  it('shows the Directory field with the hint, and sends the directory typed as tenant', async () => {
    const wrapper = await slotReconnect();
    expect(wrapper.find('[data-reconnect-directory-hint]').text()).toBe(UsernameIncorrectHint);

    await type(label, 'example.onmicrosoft.com');
    await click(reconnectIn(wrapper.element));

    expect(startDeviceConnection).toHaveBeenCalledWith({ reconnectId: 'conn-ms', scopes: [smtp], flow: 'device', tenant: 'example.onmicrosoft.com' });
  });

  it('sends no tenant when left empty, and Cancel beside the code ends it at the Host', async () => {
    const wrapper = await slotReconnect();
    await click(reconnectIn(wrapper.element));

    expect(startDeviceConnection).toHaveBeenCalledWith({ reconnectId: 'conn-ms', scopes: [smtp], flow: 'device' });
    await click(wrapper.find('[data-device-cancel]').element);
    expect(cancelConnectionFlow).toHaveBeenCalledWith('flow-1');
    expect(wrapper.find('[data-reconnect-code]').exists()).toBe(false);
  });

  it('shows no directory field for a reconnect in another tab (Google)', async () => {
    const wrapper = mount(SlotReconnect, {
      props: { connectionId: 'conn-read', scopes: [gmail], providerName: 'Google' },
      attachTo: document.body,
    });
    mounted.push(wrapper);
    await settle();

    expect(wrapper.find('[data-reconnect-directory]').exists()).toBe(false);
  });
});
