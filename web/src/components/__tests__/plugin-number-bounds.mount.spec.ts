// @vitest-environment happy-dom
//
// A PLUGIN NUMBER SETTING'S BOUNDS, SAID FIRST (B001W). A manifest `number` field may declare
// `min`, `max` and `integer`; the Host refuses a value outside them on every writer. Member settings
// and Add member say the bounds in the field's hint and hold Save / Add member on an out-of-range or
// non-whole value, with the sentence under the field - as they hold a missing required field. A value
// ALREADY STORED out of range shows as out of range, unchanged. The Host's own refusal sentence still
// shows when the server refuses. And a number box does not change on the mouse wheel, anywhere.
//
// THE MOCK IS OF `api/client`: validation is the Host's and is pinned server-side.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';

const { addMember, getPluginSettings, listCatalog, listPlugins, savePluginSettings, updateMember } = vi.hoisted(() => ({
  addMember: vi.fn(),
  getPluginSettings: vi.fn(),
  listCatalog: vi.fn(),
  listPlugins: vi.fn(),
  savePluginSettings: vi.fn(),
  updateMember: vi.fn(),
}));

vi.mock('../../api/client', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  addMember,
  getPluginSettings,
  listCatalog,
  listPlugins,
  savePluginSettings,
  updateMember,
}));

import AddMemberDialog from '../AddMemberDialog.vue';
import MemberSettingsDialog from '../MemberSettingsDialog.vue';
import { useConsoleStore } from '../../stores/console';
import { asMemberId, asTeamId, type ContainerSnapshot, type PluginMemberSettings, type Team, type TeamId } from '../../api/types';
import { bodyFind, bodyText, mountDialog, resetBody } from '../../test/mountQuasar';
import { hostField, hostList, hostPlugin, hostSettings } from '../../test/pluginFixtures';
import { button, field, hasError, isDisabled, settle, type } from '../../test/formProbe';
import { guardNumberWheel } from '../../lib/numberWheel';

const fetcher = hostPlugin({
  id: 'job-fetcher',
  version: '1.0.0',
  config: {
    salaryMax: hostField({ type: 'number', description: 'Highest salary kept.', min: 0 }),
    archiveAfterDays: hostField({ type: 'number', description: 'Days before archiving.', default: 30, min: 1, max: 365, integer: true }),
    note: hostField({ type: 'string', description: 'Free text.' }),
  },
  secrets: {},
});

const fetcherMember = {
  team: asTeamId('alpha'),
  id: asMemberId('Fetcher'),
  name: 'Fetcher',
  agent: 'plugin:job-fetcher',
  kind: 'plugin',
  state: 'Idle',
  queueDepth: 0,
  ceiling: 16,
  subscribes: [],
  currentCorrelation: null,
  sinceSeq: 0,
} as unknown as ContainerSnapshot;

/** JobTracker's stored settings: `salaryMax: -2`, saved before the manifest declared `min: 0`. */
const stored = (config: PluginMemberSettings['config']) =>
  hostSettings(fetcher, { team: 'alpha', member: 'Fetcher', config, secrets: {} });

beforeEach(() => {
  for (const mock of [addMember, getPluginSettings, listCatalog, listPlugins, savePluginSettings, updateMember]) mock.mockReset();

  listCatalog.mockResolvedValue({ agents: [{ name: 'claude', mode: 'Headless' }] });
  listPlugins.mockResolvedValue(hostList([fetcher]));
  getPluginSettings.mockResolvedValue(stored({ salaryMax: 90000 }));
  savePluginSettings.mockResolvedValue(undefined);
  updateMember.mockResolvedValue({ ...fetcherMember });
  addMember.mockResolvedValue({ name: 'Fetcher' });
});

afterEach(resetBody);

async function mountSettings() {
  setActivePinia(createPinia());
  const board = useConsoleStore();
  board.teams = [{ id: asTeamId('alpha'), name: 'Alpha', memberAgents: ['claude'], containers: [fetcherMember] } as unknown as Team];
  board.managerName = asMemberId('Manager');

  const wrapper = await mountDialog(MemberSettingsDialog, { snapshot: fetcherMember }, { pinia: false });
  await settle();
  return wrapper;
}

const setting = (name: string) => bodyFind(`[data-setting="${name}"]`)!;

describe('Member settings - a bounded number field', () => {
  it('says its bounds in the hint', async () => {
    const wrapper = await mountSettings();

    expect(setting('salaryMax').textContent).toContain('At least 0.');
    expect(setting('archiveAfterDays').textContent).toContain('Between 1 and 365, whole numbers only.');
    // A field without bounds has no bounds hint.
    expect(setting('note').textContent).not.toContain('At least');

    wrapper.unmount();
  });

  it('refuses an out-of-range value inline and holds Save, sending nothing', async () => {
    const wrapper = await mountSettings();

    await type('salaryMax', '-2');

    expect(hasError('salaryMax')).toBe(true);
    expect(setting('salaryMax').textContent).toContain('-2 is out of range for salaryMax: it must be at least 0.');
    expect(isDisabled('Save')).toBe(true);

    button('Save').click();
    await settle();
    expect(savePluginSettings).not.toHaveBeenCalled();

    await type('archiveAfterDays', '400');
    expect(setting('archiveAfterDays').textContent).toContain('400 is out of range for archiveAfterDays: it must be at most 365.');

    await type('archiveAfterDays', '2.5');
    expect(setting('archiveAfterDays').textContent).toContain('archiveAfterDays takes whole numbers only; 2.5 is not one.');

    // Back in range: the message goes and Save sends the numbers.
    await type('salaryMax', '120000');
    await type('archiveAfterDays', '14');
    expect(hasError('salaryMax')).toBe(false);
    expect(hasError('archiveAfterDays')).toBe(false);
    expect(isDisabled('Save')).toBe(false);

    button('Save').click();
    await settle();
    expect(savePluginSettings).toHaveBeenCalledTimes(1);
    expect(savePluginSettings.mock.calls[0]![2]).toEqual({ config: { salaryMax: 120000, archiveAfterDays: 14 }, secrets: {} });

    wrapper.unmount();
  });

  it('shows a value already stored out of range as out of range, unchanged, with no Host sentence to hand', async () => {
    getPluginSettings.mockResolvedValue(stored({ salaryMax: -2 }));
    const wrapper = await mountSettings();

    // As stored - never rewritten for the person - and said to be out of range, in the form's own words.
    expect(field('salaryMax').value).toBe('-2');
    expect(hasError('salaryMax')).toBe(true);
    expect(setting('salaryMax').textContent).toContain('-2 is out of range for salaryMax: it must be at least 0.');
    expect(savePluginSettings).not.toHaveBeenCalled();

    wrapper.unmount();
  });

  it("shows the Host's outOfRange sentence for a stored value, and still saves a change to another field", async () => {
    const hostSentence = '`salaryMax` must be at least 0; -2 is below it.';
    getPluginSettings.mockResolvedValue({ ...stored({ salaryMax: -2 }), outOfRange: { salaryMax: hostSentence } });
    savePluginSettings.mockResolvedValue({ ...stored({ salaryMax: -2, note: 'remote only' }), outOfRange: { salaryMax: hostSentence } });
    const wrapper = await mountSettings();

    // The Host's sentence, not the form's own; the value as stored.
    expect(field('salaryMax').value).toBe('-2');
    expect(hasError('salaryMax')).toBe(true);
    expect(setting('salaryMax').textContent).toContain(hostSentence);
    expect(setting('salaryMax').textContent).not.toContain('is out of range for salaryMax');

    // The stored value, sent back unchanged, is kept by the Host: it does not hold the rest of the form.
    expect(isDisabled('Save')).toBe(false);
    await type('note', 'remote only');
    expect(isDisabled('Save')).toBe(false);

    button('Save').click();
    await settle();
    expect(savePluginSettings).toHaveBeenCalledTimes(1);
    expect(savePluginSettings.mock.calls[0]![2]).toEqual({ config: { salaryMax: -2, note: 'remote only' }, secrets: {} });

    wrapper.unmount();
  });

  it('refuses inline once a stored out-of-range value is changed to another out-of-range value', async () => {
    const hostSentence = '`salaryMax` must be at least 0; -2 is below it.';
    getPluginSettings.mockResolvedValue({ ...stored({ salaryMax: -2 }), outOfRange: { salaryMax: hostSentence } });
    const wrapper = await mountSettings();

    await type('salaryMax', '-5');
    expect(hasError('salaryMax')).toBe(true);
    expect(setting('salaryMax').textContent).toContain('-5 is out of range for salaryMax: it must be at least 0.');
    expect(isDisabled('Save')).toBe(true);

    button('Save').click();
    await settle();
    expect(savePluginSettings).not.toHaveBeenCalled();

    // Put back as stored: shown as out of range again, and no longer holding Save.
    await type('salaryMax', '-2');
    expect(setting('salaryMax').textContent).toContain(hostSentence);
    expect(isDisabled('Save')).toBe(false);

    wrapper.unmount();
  });

  it("still shows the Host's refusal sentence when the server refuses", async () => {
    const sentence = "Config field 'salaryMax' must be at least 0; -1 is out of range.";
    savePluginSettings.mockRejectedValue(Object.assign(new Error(sentence), { status: 400 }));
    const wrapper = await mountSettings();

    await type('salaryMax', '5');
    button('Save').click();
    await settle();

    expect(savePluginSettings).toHaveBeenCalledTimes(1);
    expect(bodyText()).toContain(sentence);

    wrapper.unmount();
  });
});

describe('Add member - a bounded number field', () => {
  const props = { team: 'alpha' as TeamId, teamName: 'Alpha', memberAgents: ['claude'] };

  type Select = { props: (name: string) => unknown; vm: { $emit: (event: string, value: unknown) => void } };

  async function hireFetcher() {
    const wrapper = await mountDialog(AddMemberDialog, props);
    await type('Member name', 'Fetcher');
    const agent = (wrapper.findAllComponents({ name: 'QSelect' }) as unknown as Select[]).find((s) => s.props('label') === 'Agent')!;
    agent.vm.$emit('update:modelValue', 'plugin:job-fetcher');
    await settle();
    return wrapper;
  }

  it('says the bounds in the hint, refuses an out-of-range value inline and holds Add member', async () => {
    const wrapper = await hireFetcher();

    expect(setting('salaryMax').textContent).toContain('At least 0.');
    expect(setting('archiveAfterDays').textContent).toContain('Between 1 and 365, whole numbers only.');
    expect(isDisabled('Add member')).toBe(false);

    await type('archiveAfterDays', '0');
    expect(hasError('archiveAfterDays')).toBe(true);
    expect(setting('archiveAfterDays').textContent).toContain('0 is out of range for archiveAfterDays: it must be at least 1.');
    expect(isDisabled('Add member')).toBe(true);

    button('Add member').click();
    await settle();
    expect(addMember).not.toHaveBeenCalled();

    await type('archiveAfterDays', '7');
    expect(hasError('archiveAfterDays')).toBe(false);
    button('Add member').click();
    await settle();
    expect(addMember).toHaveBeenCalledTimes(1);
    expect(JSON.stringify(addMember.mock.calls[0])).toContain('"archiveAfterDays":7');

    wrapper.unmount();
  });

  it("still shows the Host's refusal sentence when the hire is refused", async () => {
    const sentence = "Config field 'salaryMax' must be at least 0; -1 is out of range.";
    addMember.mockRejectedValue(Object.assign(new Error(sentence), { status: 400 }));
    const wrapper = await hireFetcher();

    button('Add member').click();
    await settle();

    expect(bodyText()).toContain(sentence);

    wrapper.unmount();
  });
});

describe('A number box and the mouse wheel', () => {
  it('does not change a focused number box on the wheel, and leaves other boxes and unfocused ones alone', async () => {
    const unguard = guardNumberWheel(document);
    const wrapper = await mountSettings();

    const box = field('salaryMax') as HTMLInputElement;
    expect(box.type).toBe('number');
    box.focus();
    expect(document.activeElement).toBe(box);

    const wheel = new WheelEvent('wheel', { deltaY: 100, bubbles: true, cancelable: true });
    box.dispatchEvent(wheel);
    await settle();

    // The browser's step is the event's default action: prevented, the value is what was stored.
    expect(wheel.defaultPrevented).toBe(true);
    expect(box.value).toBe('90000');

    // A text box, and a number box without focus, scroll the page as usual.
    const text = field('note') as HTMLInputElement;
    text.focus();
    const overText = new WheelEvent('wheel', { deltaY: 100, bubbles: true, cancelable: true });
    text.dispatchEvent(overText);
    expect(overText.defaultPrevented).toBe(false);

    const overUnfocused = new WheelEvent('wheel', { deltaY: 100, bubbles: true, cancelable: true });
    box.dispatchEvent(overUnfocused);
    expect(overUnfocused.defaultPrevented).toBe(false);

    unguard();
    wrapper.unmount();
  });
});
