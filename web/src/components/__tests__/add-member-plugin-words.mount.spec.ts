// @vitest-environment happy-dom
//
// WHAT THE ADD MEMBER DIALOG SAYS WHILE HIRING A PLUGIN, line by line: where the picker's choices
// come from, the Agent install line (not for a plugin), whether a secret's key is set on the Host
// and how to set it, and a connection slot's words - one sentence, and no red line while its picker
// and Connect are right there in the form.
//
// THE MOCK IS OF `api/client`: no route is reached.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

const { listCatalog, listPlugins, addMember, getSecretKey, listConnections, listConnectionProviders } = vi.hoisted(() => ({
  listCatalog: vi.fn(),
  listPlugins: vi.fn(),
  addMember: vi.fn(),
  getSecretKey: vi.fn(),
  listConnections: vi.fn(),
  listConnectionProviders: vi.fn(),
}));

vi.mock('../../api/client', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  listCatalog,
  listPlugins,
  addMember,
  getSecretKey,
  listConnections,
  listConnectionProviders,
}));

import AddMemberDialog from '../AddMemberDialog.vue';
import type { Agent, InstalledPlugin, SecretKeyState, TeamId } from '../../api/types';
import { bodyFind, bodyText, mountDialog, resetBody } from '../../test/mountQuasar';
import { hostList, hostPlugin, hostProvider, hostSecret, hostSlot } from '../../test/pluginFixtures';
import { button, settle, type } from '../../test/formProbe';
import { productCli } from '../../presentation/product';

const headless = (name: string): Agent => ({ name, mode: 'Headless' });

const sampleEcho: InstalledPlugin = hostPlugin({
  id: 'sample-echo-go',
  name: 'Sample Echo (Go)',
  secrets: { token: hostSecret({ description: 'A demo credential.' }) },
});

const sampleWhoami: InstalledPlugin = hostPlugin({
  id: 'sample-whoami-go',
  name: 'Sample Whoami (Go)',
  connections: {
    account: hostSlot({ description: 'The Google account to report on.', providers: ['google'], required: true, summary: 'needs a Google connection' }),
  },
});

const props = { team: 'alpha' as TeamId, teamName: 'Alpha', memberAgents: ['claude'] };

/** The Host's answer for a key: ECHO_TOKEN is set, every other key is not. */
const keyState = (key: string): SecretKeyState => ({
  key,
  set: key === 'ECHO_TOKEN',
  refusal: null,
  setWith: `the operator CLI's \`secret set ${key}\` (it prompts for the value), then its \`up\` to restart the Host`,
});

beforeEach(() => {
  for (const mock of [listCatalog, listPlugins, addMember, getSecretKey, listConnections, listConnectionProviders]) mock.mockReset();
  listCatalog.mockResolvedValue({ agents: [headless('claude')] });
  listPlugins.mockResolvedValue(hostList([sampleEcho, sampleWhoami]));
  getSecretKey.mockImplementation(async (key: string) => keyState(key));
  listConnections.mockResolvedValue([]);
  listConnectionProviders.mockResolvedValue([hostProvider({ id: 'google', configured: true })]);
});

afterEach(resetBody);

type Select = { props: (name: string) => unknown; vm: { $emit: (event: string, value: unknown) => void } };
type Wrapper = { findAllComponents: (s: { name: string }) => unknown[] };

function selectLabelled(wrapper: Wrapper, label: string): Select {
  const found = (wrapper.findAllComponents({ name: 'QSelect' }) as Select[]).find((s) => s.props('label') === label);
  if (!found) throw new Error(`no QSelect labelled "${label}"`);
  return found;
}

async function choose(wrapper: Wrapper, value: string) {
  selectLabelled(wrapper, 'Agent').vm.$emit('update:modelValue', value);
  await settle();
}

/** Past the form's pause after typing, and the answer it asked for. */
async function afterTyping() {
  await new Promise((resolve) => setTimeout(resolve, 300));
  await settle();
}

describe('Add member, hiring a plugin: what each line says', () => {
  it("the Agent hint says Agents come from the team's allowlist and plugins from those installed", async () => {
    const wrapper = await mountDialog(AddMemberDialog, props);

    expect(selectLabelled(wrapper, 'Agent').props('options')).toEqual(['claude', 'plugin:sample-echo-go', 'plugin:sample-whoami-go']);
    const hint = String(selectLabelled(wrapper, 'Agent').props('hint'));
    expect(hint).toContain("Agents come from this team's allowlist");
    expect(hint).toContain('plugins from those installed on this Host');

    wrapper.unmount();
  });

  it('shows no bare "Not checked" under the Agent: none for a plugin, and an Agent says what was not checked', async () => {
    const wrapper = await mountDialog(AddMemberDialog, props);

    expect(bodyFind('[data-agent-status-text]')?.textContent?.trim()).toBe(
      "Not checked: this Host has not said whether this Agent's command is installed.",
    );

    await choose(wrapper, 'plugin:sample-echo-go');

    expect(bodyFind('[data-agent-status]')).toBeNull();
    expect(bodyText()).not.toContain('Not checked');

    wrapper.unmount();
  });

  it("says whether a secret's key is set on the Host, and how to set it when it is not", async () => {
    const wrapper = await mountDialog(AddMemberDialog, props);
    await choose(wrapper, 'plugin:sample-echo-go');

    await type('Secret token: key name', 'ECHO_TOKEN');
    await afterTyping();

    expect(getSecretKey).toHaveBeenCalledWith('ECHO_TOKEN');
    expect(bodyFind('[data-secret-field="token"]')?.getAttribute('data-secret-key-state')).toBe('set');
    expect(bodyFind('[data-secret-field="token"]')?.textContent?.trim()).toBe('Set on this Host.');

    await type('Secret token: key name', 'ECHO_OTHER');
    await afterTyping();

    const unset = bodyFind('[data-secret-field="token"]');
    expect(unset?.getAttribute('data-secret-key-state')).toBe('unset');
    expect(unset?.textContent).toContain('Not set on this Host');
    expect(unset?.textContent).toContain(`${productCli} secret set ECHO_OTHER`);
    expect(unset?.textContent).toContain(`${productCli} up`);
    // Asked by name only: the form sends the key and nothing else.
    expect(getSecretKey.mock.calls.map((call) => call.length)).toEqual([1, 1]);

    wrapper.unmount();
  });

  it('words a connection slot as one sentence, with no red "binds one" line while Connect is in the form', async () => {
    const wrapper = await mountDialog(AddMemberDialog, props);
    await choose(wrapper, 'plugin:sample-whoami-go');
    await settle();

    expect(selectLabelled(wrapper, 'Connection for account').props('hint')).toBe(
      'The Google account to report on (needs a Google connection).',
    );
    expect(button('Connect Google')).toBeDefined();
    expect(bodyFind('[data-connection-slot="account"] [data-binding-refusal]')).toBeNull();
    expect(bodyText()).not.toContain("binds one in the member's settings");
    expect(bodyFind('[data-connection-slot="account"] [data-slot-unbound]')?.textContent?.trim()).toBe(
      "Required: the member's runs are blocked until a connection is chosen here.",
    );

    wrapper.unmount();
  });
});
