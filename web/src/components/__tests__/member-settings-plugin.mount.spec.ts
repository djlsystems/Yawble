// @vitest-environment happy-dom
//
// A PLUGIN MEMBER'S SETTINGS, AFTER HIRE. Member settings shows a Settings section: the editor Add
// member shows, generated from the manifest - text for a string, a number box, a toggle for a bool,
// a dropdown for an enum, chips for a list - each with its description, its default, whether it is
// required and "Set by a person only"; Reset to default per field; secrets as key names; a read-only
// "as JSON" view; and a save that sends only the fields that differ from their default.
//
// THE MOCK IS OF `api/client`: validation is the Host's and is pinned server-side.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';

const { getMember, getPluginSettings, listCatalog, savePluginSettings, updateMember } = vi.hoisted(() => ({
  getMember: vi.fn(),
  getPluginSettings: vi.fn(),
  listCatalog: vi.fn(),
  savePluginSettings: vi.fn(),
  updateMember: vi.fn(),
}));

vi.mock('../../api/client', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  getMember,
  getPluginSettings,
  listCatalog,
  savePluginSettings,
  updateMember,
}));

import MemberSettingsDialog from '../MemberSettingsDialog.vue';
import { useConsoleStore } from '../../stores/console';
import {
  asMemberId,
  asTeamId,
  type ContainerSnapshot,
  type PluginConfigField,
  type PluginMemberSettings,
  type Team,
} from '../../api/types';
import { bodyFind, mountDialog, resetBody } from '../../test/mountQuasar';
import { hostField, hostPlugin, hostSecret, hostSettings } from '../../test/pluginFixtures';
import { blur, button, field, fieldWrapper, hasError, isDisabled, settle, type } from '../../test/formProbe';
import { addButtonIn, addChips, cancelAddDialog, chipsIn, removeChip, typeInAddDialog } from '../../test/chipList';

const fields: Record<string, PluginConfigField> = {
  greeting: hostField({ type: 'string', description: 'What it says first.', default: 'hello' }),
  mode: hostField({ type: 'string', description: 'How to transform.', default: 'upper', enum: ['upper', 'reverse'], setBy: 'person' }),
  repeat: hostField({ type: 'number', description: 'How many times.', default: 1 }),
  loud: hostField({ type: 'bool', description: 'Shout.', default: false }),
  labels: hostField({ type: 'list', description: 'Free labels.' }),
  recipients: hostField({ type: 'list', description: 'Who may be sent to.', required: true, enum: ['ops', 'dev'] }),
};

const sampleEcho = hostPlugin({
  id: 'sample-echo',
  version: '0.2.0',
  config: fields,
  secrets: { token: hostSecret({ description: 'A demo credential.', required: true }) },
});

const stored: PluginMemberSettings = hostSettings(sampleEcho, {
  team: 'alpha',
  member: 'Echo',
  config: { repeat: 3, recipients: ['ops'] },
  secrets: { token: 'ECHO_TOKEN' },
});

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

beforeEach(() => {
  for (const mock of [getMember, getPluginSettings, listCatalog, savePluginSettings, updateMember]) mock.mockReset();

  listCatalog.mockResolvedValue({ agents: [{ name: 'claude', mode: 'Headless' }] });
  getPluginSettings.mockResolvedValue(structuredClone(stored));
  savePluginSettings.mockResolvedValue(undefined);
  updateMember.mockResolvedValue({ ...echo });
});

afterEach(resetBody);

async function mountSettings() {
  setActivePinia(createPinia());
  const board = useConsoleStore();
  // The allowlist names no plugin: editing a plugin member's settings is not a repoint.
  board.teams = [{ id: asTeamId('alpha'), name: 'Alpha', memberAgents: ['claude'], containers: [echo] } as unknown as Team];
  board.managerName = asMemberId('Manager');

  const wrapper = await mountDialog(MemberSettingsDialog, { snapshot: echo }, { pinia: false });
  await settle();

  return wrapper;
}

const setting = (name: string) => bodyFind(`[data-setting="${name}"]`)!;

type Select = { props: (name: string) => unknown; vm: { $emit: (event: string, value: unknown) => void } };

function select(wrapper: { findAllComponents: (s: { name: string }) => unknown[] }, label: string): Select {
  const found = (wrapper.findAllComponents({ name: 'QSelect' }) as Select[]).find((s) => s.props('label') === label);
  if (!found) throw new Error(`no QSelect labelled "${label}"`);
  return found;
}

const chips = (name: string) => chipsIn(setting(name));

function json() {
  return JSON.parse(bodyFind('[data-settings-json]')!.textContent ?? '');
}

describe('MemberSettingsDialog, a plugin member', () => {
  it('renders each type from the manifest, filled with what is stored', async () => {
    const wrapper = await mountSettings();

    expect(getPluginSettings).toHaveBeenCalledWith('alpha', 'Echo');
    expect(bodyFind('[data-member-plugin-settings]')?.textContent).toContain('Settings');

    // string: a text box at its default; number: a number box holding the stored value.
    expect(field('greeting').getAttribute('type')).toBe('text');
    expect(field('greeting').value).toBe('hello');
    expect(field('repeat').getAttribute('type')).toBe('number');
    expect(field('repeat').value).toBe('3');

    // enum: a dropdown of its values; bool: a toggle.
    expect(select(wrapper, 'mode').props('options')).toEqual(['upper', 'reverse']);
    expect(select(wrapper, 'mode').props('modelValue')).toBe('upper');
    expect(setting('loud').querySelector('.q-toggle')).not.toBeNull();

    // list: chips - chosen from the enum where there is one, else added through the Add dialog.
    expect(chips('recipients')).toEqual(['ops']);
    expect(chips('labels')).toEqual([]);
    expect(addButtonIn(setting('labels'))).not.toBeNull();
    expect(addButtonIn(setting('recipients'))).toBeNull();
    expect(select(wrapper, 'recipients').props('options')).toEqual([{ label: 'dev', value: 'dev' }]);

    // Secrets as KEY NAMES, filled with the bound key.
    expect(field('Secret token: key name').value).toBe('ECHO_TOKEN');

    // No free-form JSON: nothing in the section is a text area.
    expect(bodyFind('[data-member-plugin-settings] textarea')).toBeNull();

    wrapper.unmount();
  });

  it('shows each field with its description, default, required and person-only marks', async () => {
    const wrapper = await mountSettings();

    expect(setting('greeting').querySelector('[data-description]')?.textContent).toBe('What it says first.');
    expect(setting('greeting').querySelector('[data-default]')?.textContent).toBe('Default: hello');
    expect(setting('mode').querySelector('[data-default]')?.textContent).toBe('Default: upper');
    expect(setting('repeat').querySelector('[data-default]')?.textContent).toBe('Default: 1');
    expect(setting('loud').querySelector('[data-default]')?.textContent).toBe('Default: off');
    expect(setting('labels').querySelector('[data-default]')?.textContent).toBe('Default: none');

    expect(setting('recipients').querySelector('[data-required]')?.textContent).toBe('Required');
    expect(setting('greeting').querySelector('[data-required]')).toBeNull();

    expect(setting('mode').querySelector('[data-person-only]')?.textContent).toBe('Set by a person only');
    expect(setting('greeting').querySelector('[data-person-only]')).toBeNull();

    wrapper.unmount();
  });

  it('adds a free-text list value through its dialog without saving, removes one with ×, and chooses an enum list from its values', async () => {
    const wrapper = await mountSettings();

    await addChips(setting('labels'), 'nightly');

    expect(chips('labels')).toEqual(['nightly']);
    // Adding a chip is not a save.
    expect(savePluginSettings).not.toHaveBeenCalled();
    expect(updateMember).not.toHaveBeenCalled();

    // An enum list has no free text: its values are chosen from the dropdown.
    select(wrapper, 'recipients').vm.$emit('update:modelValue', ['ops', 'dev']);
    await settle();
    expect(chips('recipients')).toEqual(['ops', 'dev']);

    await removeChip(setting('recipients'), 'ops');
    expect(chips('recipients')).toEqual(['dev']);

    wrapper.unmount();
  });

  it('resets a field to its default', async () => {
    const wrapper = await mountSettings();

    const reset = () => setting('repeat').querySelector('[aria-label="Reset repeat to default"]') as HTMLButtonElement;
    const greetingReset = setting('greeting').querySelector('[aria-label="Reset greeting to default"]') as HTMLButtonElement;

    // A field at its default has nothing to reset.
    expect(greetingReset.hasAttribute('disabled')).toBe(true);
    expect(reset().hasAttribute('disabled')).toBe(false);

    reset().click();
    await settle();

    expect(field('repeat').value).toBe('1');
    expect(reset().hasAttribute('disabled')).toBe(true);

    wrapper.unmount();
  });

  it('shows exactly what will be stored as JSON, read-only', async () => {
    const wrapper = await mountSettings();

    expect(bodyFind('[data-settings-json]')).toBeNull();
    button('As JSON').click();
    await settle();

    expect(bodyFind('[data-settings-json]')?.tagName).toBe('PRE');
    expect(json()).toEqual({ config: { repeat: 3, recipients: ['ops'] }, secrets: { token: 'ECHO_TOKEN' } });

    await type('greeting', 'hi');
    expect(json()).toEqual({ config: { greeting: 'hi', repeat: 3, recipients: ['ops'] }, secrets: { token: 'ECHO_TOKEN' } });

    wrapper.unmount();
  });

  it('saves only the fields that differ from their default', async () => {
    const wrapper = await mountSettings();

    await type('greeting', 'hi');
    // Back to the default: left out of what is saved.
    (setting('repeat').querySelector('[aria-label="Reset repeat to default"]') as HTMLButtonElement).click();
    await settle();
    select(wrapper, 'mode').vm.$emit('update:modelValue', 'reverse');
    await settle();
    (setting('loud').querySelector('.q-toggle') as HTMLElement).click();
    await settle();

    button('Save').click();
    await settle();

    expect(savePluginSettings).toHaveBeenCalledTimes(1);
    expect(savePluginSettings.mock.calls[0]).toEqual([
      'alpha',
      'Echo',
      { config: { greeting: 'hi', mode: 'reverse', loud: true, recipients: ['ops'] }, secrets: { token: 'ECHO_TOKEN' } },
    ]);

    wrapper.unmount();
  });

  // A typed value is added or cancelled in its dialog - never left in a box that Save ignores. What
  // was added is sent by Save; what was cancelled is not.
  it('sends what was added through the dialog on Save, and nothing that was cancelled', async () => {
    const wrapper = await mountSettings();

    await addChips(setting('labels'), 'Systems Analyst\nBoston, MA');
    await typeInAddDialog(setting('labels'), 'Remote');
    await cancelAddDialog();

    expect(chips('labels')).toEqual(['Systems Analyst', 'Boston, MA']);

    button('Save').click();
    await settle();

    expect(savePluginSettings).toHaveBeenCalledTimes(1);
    expect(savePluginSettings.mock.calls[0]![2]).toMatchObject({ config: { labels: ['Systems Analyst', 'Boston, MA'] } });

    wrapper.unmount();
  });

  it('does not write the settings when nothing in them moved', async () => {
    const wrapper = await mountSettings();

    button('Save').click();
    await settle();

    expect(savePluginSettings).not.toHaveBeenCalled();
    expect(updateMember).not.toHaveBeenCalled();

    wrapper.unmount();
  });

  it('holds Save while a required secret is empty', async () => {
    const wrapper = await mountSettings();

    await type('Secret token: key name', '');
    expect(isDisabled('Save')).toBe(true);

    await type('Secret token: key name', 'OTHER_KEY');
    expect(isDisabled('Save')).toBe(false);

    wrapper.unmount();
  });

  it('saves a required list left empty, as the Host does: a list defaults to []', async () => {
    const wrapper = await mountSettings();

    (setting('recipients').querySelector('[aria-label="Remove ops"]') as HTMLElement).click();
    await settle();
    expect(isDisabled('Save')).toBe(false);

    button('Save').click();
    await settle();

    expect(savePluginSettings).toHaveBeenCalledTimes(1);
    expect(savePluginSettings.mock.calls[0]![2]).toEqual({ config: { repeat: 3 }, secrets: { token: 'ECHO_TOKEN' } });

    wrapper.unmount();
  });

  it("keeps the Host's refusal in the dialog, naming the field", async () => {
    const wrapper = await mountSettings();

    savePluginSettings.mockRejectedValue(Object.assign(new Error("Config field 'greeting' is too long."), { status: 400 }));
    await type('greeting', 'hi');
    button('Save').click();
    await settle();

    expect(bodyFind('.q-banner')?.textContent).toContain("Config field 'greeting' is too long.");
    expect(updateMember).not.toHaveBeenCalled();

    wrapper.unmount();
  });

  it("shows the Host's sentence on a 409 for a plugin no longer installed, and does not flag the name", async () => {
    const wrapper = await mountSettings();

    const sentence = "Plugin 'sample-echo' is not installed: its active version 0.2.0 has no folder.";
    savePluginSettings.mockRejectedValue(Object.assign(new Error(sentence), { status: 409 }));
    await type('greeting', 'hi');
    button('Save').click();
    await settle();

    expect(bodyFind('.q-banner')?.textContent).toContain(sentence);
    expect(hasError('Member name')).toBe(false);
    expect(fieldWrapper('Member name').textContent).not.toContain(sentence);
    expect(updateMember).not.toHaveBeenCalled();

    wrapper.unmount();
  });
});
