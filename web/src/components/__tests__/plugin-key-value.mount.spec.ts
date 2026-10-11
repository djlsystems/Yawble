// @vitest-environment happy-dom
//
// A PLUGIN KEY'S VALUE, SET FROM THE SCREEN. Under each secret's key name, Add member (a hire) and
// Member settings (after hire) offer the key's write-only value field: a password box, Set while the
// key is not set, Replace once it is, and Clear for a key set here. The box never shows or reads back
// a value - it starts empty and is emptied when a write is sent - and what is said of the key is the
// Host's answer: set by whom and when, or from the Host's environment. After a write that answer is
// what the form says, with no further ask.
//
// THE MOCK IS OF `api/client`: no route is reached.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';

const {
  listCatalog,
  listPlugins,
  addMember,
  getSecretKey,
  setSecretKey,
  clearSecretKey,
  getMember,
  getPluginSettings,
  savePluginSettings,
  updateMember,
} = vi.hoisted(() => ({
  listCatalog: vi.fn(),
  listPlugins: vi.fn(),
  addMember: vi.fn(),
  getSecretKey: vi.fn(),
  setSecretKey: vi.fn(),
  clearSecretKey: vi.fn(),
  getMember: vi.fn(),
  getPluginSettings: vi.fn(),
  savePluginSettings: vi.fn(),
  updateMember: vi.fn(),
}));

vi.mock('../../api/client', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  listCatalog,
  listPlugins,
  addMember,
  getSecretKey,
  setSecretKey,
  clearSecretKey,
  getMember,
  getPluginSettings,
  savePluginSettings,
  updateMember,
}));

import AddMemberDialog from '../AddMemberDialog.vue';
import MemberSettingsDialog from '../MemberSettingsDialog.vue';
import { ActionRefused } from '../../api/client';
import { useConsoleStore } from '../../stores/console';
import { asMemberId, asTeamId, type ContainerSnapshot, type SecretKeyState, type Team, type TeamId } from '../../api/types';
import { bodyFind, bodyText, mountDialog, resetBody } from '../../test/mountQuasar';
import { hostList, hostPlugin, hostSecret, hostSettings } from '../../test/pluginFixtures';
import { button, field, isDisabled, settle, type } from '../../test/formProbe';
import { localTime } from '../../lib/localTime';

const Value = 'sk-live-0123456789abcdef';
const SetAt = '2026-10-11T09:30:00Z';
const SetAtWords = localTime(Date.parse(SetAt), { date: true });

const sampleEcho = hostPlugin({
  id: 'sample-echo',
  name: 'Sample Echo',
  secrets: { token: hostSecret({ description: 'A demo credential.' }) },
});

const state = (key: string, over: Partial<SecretKeyState> = {}): SecretKeyState => ({
  key,
  set: false,
  refusal: null,
  setWith: `the field on the screen, or the operator CLI's \`secret set ${key}\``,
  setBy: null,
  setAt: null,
  source: null,
  ...over,
});

const stored = (key: string) => state(key, { set: true, setBy: 'dana@example.com', setAt: SetAt, source: 'stored' });
const environment = (key: string) => state(key, { set: true, source: 'environment' });

/** What the Host holds, by key: each write below changes it, as the routes would. */
let host: Record<string, SecretKeyState> = {};

beforeEach(() => {
  for (const mock of [listCatalog, listPlugins, addMember, getSecretKey, setSecretKey, clearSecretKey, getMember, getPluginSettings, savePluginSettings, updateMember]) {
    mock.mockReset();
  }
  host = { ECHO_TOKEN: state('ECHO_TOKEN'), ENV_TOKEN: environment('ENV_TOKEN') };

  listCatalog.mockResolvedValue({ agents: [{ name: 'claude', mode: 'Headless' }] });
  listPlugins.mockResolvedValue(hostList([sampleEcho]));
  addMember.mockResolvedValue({ name: 'Echo' });
  getSecretKey.mockImplementation(async (key: string) => host[key] ?? state(key));
  setSecretKey.mockImplementation(async (key: string) => (host[key] = stored(key)));
  clearSecretKey.mockImplementation(async (key: string) => (host[key] = key === 'BOTH_TOKEN' ? environment(key) : state(key)));
});

afterEach(resetBody);

type Select = { props: (name: string) => unknown; vm: { $emit: (event: string, value: unknown) => void } };
type Wrapper = { findAllComponents: (s: { name: string }) => unknown[]; unmount: () => void };

async function hireForm(key: string): Promise<Wrapper> {
  const wrapper = (await mountDialog(AddMemberDialog, { team: 'alpha' as TeamId, teamName: 'Alpha', memberAgents: ['claude'] })) as unknown as Wrapper;
  const agent = (wrapper.findAllComponents({ name: 'QSelect' }) as Select[]).find((s) => s.props('label') === 'Agent')!;
  agent.vm.$emit('update:modelValue', 'plugin:sample-echo');
  await settle();
  await type('Secret token: key name', key);
  await afterTyping();
  return wrapper;
}

/** Past the form's pause after typing, and the answer it asked for. */
async function afterTyping() {
  await new Promise((resolve) => setTimeout(resolve, 300));
  await settle();
}

const said = () => bodyFind('[data-secret-field="token"]')!;
const valueBox = (key: string) => field(`Value for ${key}`) as HTMLInputElement;

describe('A plugin key set from Add member', () => {
  it('is write-only: a password box that starts empty, is emptied by Set, and never shows the value', async () => {
    const wrapper = await hireForm('ECHO_TOKEN');

    expect(said().textContent).toContain('Not set on this Host');
    expect(valueBox('ECHO_TOKEN').getAttribute('type')).toBe('password');
    expect(valueBox('ECHO_TOKEN').getAttribute('autocomplete')).toBe('new-password');
    expect(valueBox('ECHO_TOKEN').value).toBe('');
    expect(isDisabled('Set')).toBe(true);
    expect(bodyFind('[data-secret-value-clear]')).toBeNull();

    await type('Value for ECHO_TOKEN', Value);
    expect(isDisabled('Set')).toBe(false);
    button('Set').click();
    await settle();

    expect(setSecretKey).toHaveBeenCalledWith('ECHO_TOKEN', Value);
    expect(valueBox('ECHO_TOKEN').value).toBe('');
    expect(bodyText()).not.toContain(Value);

    // The hire binds the key by its name: the value is in nothing it sends.
    await type('Member name', 'Echo');
    button('Add member').click();
    await settle();
    expect(addMember).toHaveBeenCalledTimes(1);
    expect(JSON.stringify(addMember.mock.calls[0])).not.toContain(Value);
    expect(JSON.stringify(addMember.mock.calls[0])).toContain('ECHO_TOKEN');

    wrapper.unmount();
  });

  it('after Set, says who set the key and when, and offers Replace and Clear', async () => {
    const wrapper = await hireForm('ECHO_TOKEN');

    await type('Value for ECHO_TOKEN', Value);
    button('Set').click();
    await settle();

    expect(said().getAttribute('data-secret-key-state')).toBe('set');
    expect(said().textContent?.trim()).toBe(`Set by dana@example.com on ${SetAtWords}.`);
    // The Host's answer to the write is the state: nothing asked again.
    expect(getSecretKey).toHaveBeenCalledTimes(1);
    expect(button('Replace')).toBeDefined();
    expect(button('Clear')).toBeDefined();
    expect(() => button('Set')).toThrow();

    wrapper.unmount();
  });

  it('Replace sends the new value and Clear clears the stored key, each refreshing what is said', async () => {
    host.ECHO_TOKEN = stored('ECHO_TOKEN');
    const wrapper = await hireForm('ECHO_TOKEN');

    expect(said().textContent?.trim()).toBe(`Set by dana@example.com on ${SetAtWords}.`);
    expect(valueBox('ECHO_TOKEN').value).toBe('');

    await type('Value for ECHO_TOKEN', 'a-new-value-9876');
    button('Replace').click();
    await settle();
    expect(setSecretKey).toHaveBeenCalledWith('ECHO_TOKEN', 'a-new-value-9876');
    expect(valueBox('ECHO_TOKEN').value).toBe('');

    button('Clear').click();
    await settle();
    expect(clearSecretKey).toHaveBeenCalledWith('ECHO_TOKEN');
    expect(said().getAttribute('data-secret-key-state')).toBe('unset');
    expect(said().textContent).toContain('Not set on this Host');
    expect(button('Set')).toBeDefined();
    expect(bodyFind('[data-secret-value-clear]')).toBeNull();

    wrapper.unmount();
  });

  it("says a key comes from the Host's environment, offers Replace and no Clear, and a cleared key falls back to it", async () => {
    const wrapper = await hireForm('ENV_TOKEN');

    expect(said().textContent?.trim()).toBe("Set in this Host's environment.");
    expect(button('Replace')).toBeDefined();
    expect(bodyFind('[data-secret-value-clear]')).toBeNull();
    wrapper.unmount();
    resetBody();

    host.BOTH_TOKEN = stored('BOTH_TOKEN');
    const both = await hireForm('BOTH_TOKEN');
    button('Clear').click();
    await settle();
    expect(said().textContent?.trim()).toBe("Set in this Host's environment.");
    expect(bodyFind('[data-secret-value-clear]')).toBeNull();
    both.unmount();
  });

  it("shows the Host's refusal of a write and keeps the value out of it", async () => {
    setSecretKey.mockRejectedValueOnce(
      new ActionRefused('refused', { refusal: 'ECHO_TOKEN is kept for the platform.' }),
    );
    const wrapper = await hireForm('ECHO_TOKEN');

    await type('Value for ECHO_TOKEN', Value);
    button('Set').click();
    await settle();

    expect(bodyFind('[data-secret-value-problem]')?.textContent).toBe('ECHO_TOKEN was not set: ECHO_TOKEN is kept for the platform.');
    expect(said().getAttribute('data-secret-key-state')).toBe('unset');
    expect(bodyText()).not.toContain(Value);

    wrapper.unmount();
  });

  it('offers no value field for a key name the Host refuses', async () => {
    host.HARNESS_TOKEN = state('HARNESS_TOKEN', { refusal: 'HARNESS_TOKEN is kept for the platform.' });
    const wrapper = await hireForm('HARNESS_TOKEN');

    expect(said().textContent?.trim()).toBe('HARNESS_TOKEN is kept for the platform.');
    expect(bodyFind('[data-secret-value]')).toBeNull();

    wrapper.unmount();
  });
});

describe("A plugin key set from a plugin member's settings", () => {
  const echo = {
    team: asTeamId('alpha'),
    id: asMemberId('Echo'),
    name: 'Echo',
    agent: 'plugin:sample-echo',
    kind: 'plugin',
    state: 'Idle',
    queueDepth: 0,
    ceiling: 16,
    subscribes: [],
    currentCorrelation: null,
    sinceSeq: 0,
  } as unknown as ContainerSnapshot;

  async function mountSettings() {
    getPluginSettings.mockResolvedValue(hostSettings(sampleEcho, { team: 'alpha', member: 'Echo', secrets: { token: 'ECHO_TOKEN' } }));
    setActivePinia(createPinia());
    const board = useConsoleStore();
    board.teams = [{ id: asTeamId('alpha'), name: 'Alpha', memberAgents: ['claude'], containers: [echo] } as unknown as Team];
    board.managerName = asMemberId('Manager');
    const wrapper = await mountDialog(MemberSettingsDialog, { snapshot: echo }, { pinia: false });
    await settle();
    await afterTyping();
    return wrapper;
  }

  it('asks of the bound key, and Set, Replace and Clear each refresh it, never reading a value back', async () => {
    const wrapper = await mountSettings();

    expect(getSecretKey).toHaveBeenCalledWith('ECHO_TOKEN');
    expect(said().textContent).toContain('Not set on this Host');
    expect(valueBox('ECHO_TOKEN').value).toBe('');

    await type('Value for ECHO_TOKEN', Value);
    button('Set').click();
    await settle();
    expect(setSecretKey).toHaveBeenCalledWith('ECHO_TOKEN', Value);
    expect(said().textContent?.trim()).toBe(`Set by dana@example.com on ${SetAtWords}.`);
    expect(valueBox('ECHO_TOKEN').value).toBe('');

    await type('Value for ECHO_TOKEN', 'a-new-value-9876');
    button('Replace').click();
    await settle();
    expect(setSecretKey).toHaveBeenLastCalledWith('ECHO_TOKEN', 'a-new-value-9876');

    button('Clear').click();
    await settle();
    expect(clearSecretKey).toHaveBeenCalledWith('ECHO_TOKEN');
    expect(said().getAttribute('data-secret-key-state')).toBe('unset');

    expect(bodyText()).not.toContain(Value);
    expect(bodyText()).not.toContain('a-new-value-9876');
    // Setting a key is not a settings change: nothing was saved.
    expect(savePluginSettings).not.toHaveBeenCalled();

    wrapper.unmount();
  });
});
