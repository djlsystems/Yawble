// @vitest-environment happy-dom
//
// A PLUGIN KEY'S VALUE, SET FROM THE SCREEN. Under each secret's key name, Add member (a hire) and
// Member settings (after hire) offer the key's write-only value field: a password box with no
// button of its own. What is typed is written when the person presses the dialog's own button (Add
// member, Save), before the hire or the settings; Clear, for a key set here, is held the same way.
// The box never shows or reads back a value, and what is said of the key is the Host's answer: set
// by whom and when, or from the Host's environment.
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
import { hostField, hostList, hostPlugin, hostSecret, hostSettings } from '../../test/pluginFixtures';
import { button, field, isDisabled, settle, type } from '../../test/formProbe';
import { localTime } from '../../lib/localTime';

const Value = 'sk-live-0123456789abcdef';
const SetAt = '2026-10-11T09:30:00Z';
const SetAtWords = localTime(Date.parse(SetAt), { date: true });

const sampleEcho = hostPlugin({
  id: 'sample-echo',
  name: 'Sample Echo',
  secrets: { token: hostSecret({ description: 'A demo credential.' }) },
  // An advanced setting, so the form has an Advanced toggle for the secret to come before.
  config: { region: hostField({ type: 'string', default: 'eu', advanced: true, description: 'Where it runs.' }) },
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
  it('is write-only: a password box with no button of its own, written by Add member before the hire', async () => {
    const wrapper = await hireForm('ECHO_TOKEN');

    expect(said().textContent?.trim()).toBe(
      'Not set on this Host: the plugin runs without it until it is. Type its value above and press Add member.',
    );
    expect(valueBox('ECHO_TOKEN').getAttribute('type')).toBe('password');
    expect(valueBox('ECHO_TOKEN').getAttribute('autocomplete')).toBe('new-password');
    expect(valueBox('ECHO_TOKEN').value).toBe('');
    expect(() => button('Set')).toThrow();
    expect(() => button('Replace')).toThrow();
    expect(bodyFind('[data-secret-value-clear]')).toBeNull();

    await type('Value for ECHO_TOKEN', Value);
    expect(said().textContent?.trim()).toBe('Saved when you press Add member.');
    expect(setSecretKey).not.toHaveBeenCalled();

    await type('Member name', 'Echo');
    button('Add member').click();
    await settle();

    expect(setSecretKey).toHaveBeenCalledWith('ECHO_TOKEN', Value);
    expect(setSecretKey.mock.invocationCallOrder[0]!).toBeLessThan(addMember.mock.invocationCallOrder[0]!);
    // The hire binds the key by its name: the value is in nothing it sends.
    expect(addMember).toHaveBeenCalledTimes(1);
    expect(JSON.stringify(addMember.mock.calls[0])).not.toContain(Value);
    expect(JSON.stringify(addMember.mock.calls[0])).toContain('ECHO_TOKEN');
    expect(bodyText()).not.toContain(Value);

    wrapper.unmount();
  });

  it('says who set a stored key and when, and offers Clear where Reset sits on a setting', async () => {
    host.ECHO_TOKEN = stored('ECHO_TOKEN');
    const wrapper = await hireForm('ECHO_TOKEN');

    expect(said().getAttribute('data-secret-key-state')).toBe('set');
    expect(said().textContent?.trim()).toBe(`Set by dana@example.com on ${SetAtWords}.`);
    expect(valueBox('ECHO_TOKEN').value).toBe('');
    expect(valueBox('ECHO_TOKEN').getAttribute('placeholder')).toBe('Type a new value to replace it');
    expect(button('Clear')).toBeDefined();

    wrapper.unmount();
  });

  it('Clear is held until Add member, Keep takes it back, and the cleared key is cleared before the hire', async () => {
    host.ECHO_TOKEN = stored('ECHO_TOKEN');
    const wrapper = await hireForm('ECHO_TOKEN');

    button('Clear').click();
    await settle();
    expect(clearSecretKey).not.toHaveBeenCalled();
    expect(said().textContent?.trim()).toBe('Cleared when you press Add member.');
    expect(valueBox('ECHO_TOKEN').disabled).toBe(true);

    button('Keep').click();
    await settle();
    expect(said().textContent?.trim()).toBe(`Set by dana@example.com on ${SetAtWords}.`);

    button('Clear').click();
    await settle();
    await type('Member name', 'Echo');
    button('Add member').click();
    await settle();
    expect(clearSecretKey).toHaveBeenCalledWith('ECHO_TOKEN');
    expect(clearSecretKey.mock.invocationCallOrder[0]!).toBeLessThan(addMember.mock.invocationCallOrder[0]!);

    wrapper.unmount();
  });

  it("says a key comes from the Host's environment and offers no Clear", async () => {
    const wrapper = await hireForm('ENV_TOKEN');

    expect(said().textContent?.trim()).toBe("Set in this Host's environment.");
    expect(valueBox('ENV_TOKEN')).toBeDefined();
    expect(bodyFind('[data-secret-value-clear]')).toBeNull();

    wrapper.unmount();
  });

  it("shows the Host's refusal of a write in the dialog, hires nothing, and keeps the value out of it", async () => {
    setSecretKey.mockRejectedValueOnce(
      new ActionRefused('refused', { refusal: 'ECHO_TOKEN is kept for the platform.' }),
    );
    const wrapper = await hireForm('ECHO_TOKEN');

    await type('Value for ECHO_TOKEN', Value);
    await type('Member name', 'Echo');
    button('Add member').click();
    await settle();

    expect(bodyText()).toContain('ECHO_TOKEN was not saved: ECHO_TOKEN is kept for the platform.');
    expect(addMember).not.toHaveBeenCalled();
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

  it('puts the secrets before the advanced settings, never under them', async () => {
    const wrapper = await hireForm('ECHO_TOKEN');

    const order = [...document.body.querySelectorAll('[data-setting], [data-secret-setting], [data-advanced-toggle]')]
      .map((element) => element.getAttribute('data-secret-setting') ? 'secret' : element.hasAttribute('data-advanced-toggle') ? 'advanced' : 'setting');
    expect(order).toEqual(['secret', 'advanced']);

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

  it('asks of the bound key, and Save writes the typed value, never reading a value back', async () => {
    const wrapper = await mountSettings();

    expect(getSecretKey).toHaveBeenCalledWith('ECHO_TOKEN');
    expect(said().textContent?.trim()).toBe(
      'Not set on this Host: the plugin runs without it until it is. Type its value above and press Save.',
    );
    expect(valueBox('ECHO_TOKEN').value).toBe('');
    expect(() => button('Set')).toThrow();

    await type('Value for ECHO_TOKEN', Value);
    expect(setSecretKey).not.toHaveBeenCalled();
    button('Save').click();
    await settle();

    expect(setSecretKey).toHaveBeenCalledWith('ECHO_TOKEN', Value);
    expect(bodyText()).not.toContain(Value);
    // Setting a key is not a settings change: no settings were saved.
    expect(savePluginSettings).not.toHaveBeenCalled();

    wrapper.unmount();
  });

  it('a refused write keeps the dialog open with the sentence and saves nothing else', async () => {
    setSecretKey.mockRejectedValueOnce(new ActionRefused('refused', { refusal: 'ECHO_TOKEN is kept for the platform.' }));
    const wrapper = await mountSettings();

    await type('Value for ECHO_TOKEN', Value);
    button('Save').click();
    await settle();

    expect(bodyText()).toContain('ECHO_TOKEN was not saved: ECHO_TOKEN is kept for the platform.');
    expect(savePluginSettings).not.toHaveBeenCalled();
    expect(updateMember).not.toHaveBeenCalled();
    expect(bodyText()).not.toContain(Value);

    wrapper.unmount();
  });
});
