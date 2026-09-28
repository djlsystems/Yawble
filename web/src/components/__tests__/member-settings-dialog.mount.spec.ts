// @vitest-environment happy-dom
//
// AN EXISTING MEMBER'S SETTINGS. The name carries `memberName`, an Agent picker with nothing to
// offer is in its own error state, and a refusal stays in the dialog - a 409 on the name as well.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';

const { getMember, listCatalog, updateMember } = vi.hoisted(() => ({
  getMember: vi.fn(),
  listCatalog: vi.fn(),
  updateMember: vi.fn(),
}));

vi.mock('../../api/client', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  getMember,
  listCatalog,
  updateMember,
}));

import MemberSettingsDialog from '../MemberSettingsDialog.vue';
import { useConsoleStore } from '../../stores/console';
import { asMemberId, asTeamId, type ContainerSnapshot, type MemberDetail, type Team } from '../../api/types';
import { bodyText, mountDialog, resetBody } from '../../test/mountQuasar';
import { blur, button, field, fieldWrapper, hasError, isDisabled, pressEnter, settle, type } from '../../test/formProbe';

const snapshot: ContainerSnapshot = {
  team: asTeamId('alpha'),
  id: asMemberId('scout'),
  name: 'Scout',
  agent: 'claude',
  state: 'Idle',
  queueDepth: 0,
  ceiling: 16,
  subscribes: [],
  currentCorrelation: null,
  sinceSeq: 0,
} as ContainerSnapshot;

beforeEach(() => {
  listCatalog.mockReset();
  listCatalog.mockResolvedValue({
    agents: [{ name: 'claude', mode: 'Headless' }],
  });
  updateMember.mockReset();
  updateMember.mockResolvedValue({ ...snapshot });
  getMember.mockReset();
  getMember.mockResolvedValue(detail());
});

/** What `GET /api/teams/{team}/containers/{name}` answers: none written, by default. */
function detail(overrides: Partial<MemberDetail> = {}): MemberDetail {
  return {
    team: asTeamId('alpha'),
    id: 'scout',
    name: 'Scout',
    systemPrompt: null,
    systemPromptSetBy: null,
    systemPromptSetByKind: null,
    systemPromptSetAt: null,
    ...overrides,
  };
}

afterEach(resetBody);

async function mountSettings(
  memberAgents: string[] | null = ['claude'],
  member: ContainerSnapshot = snapshot,
) {
  setActivePinia(createPinia());
  const board = useConsoleStore();
  board.teams = [{ id: asTeamId('alpha'), name: 'Alpha', memberAgents } as unknown as Team];
  board.managerName = asMemberId('Manager');

  const wrapper = mountDialog(MemberSettingsDialog, { snapshot: member }, { pinia: false });
  await settle();

  return wrapper;
}

describe('MemberSettingsDialog, mounted', () => {
  it('disables Save when the name is cleared', async () => {
    const wrapper = await mountSettings();

    await type('Member name', '');

    expect(isDisabled('Save')).toBe(true);

    wrapper.unmount();
  });

  it('enables Save with a legal name and an allowed Agent', async () => {
    const wrapper = await mountSettings();

    await type('Member name', 'Scout Two');

    expect(isDisabled('Save')).toBe(false);

    wrapper.unmount();
  });

  it('refuses a name over the length limit under the field', async () => {
    const wrapper = await mountSettings();

    await type('Member name', 'x'.repeat(61));
    await blur('Member name');

    expect(hasError('Member name')).toBe(true);
    expect(isDisabled('Save')).toBe(true);

    wrapper.unmount();
  });

  it('puts an empty allowlist on the Agent picker as its error state', async () => {
    const wrapper = await mountSettings([]);

    expect(hasError('Agent')).toBe(true);
    expect(isDisabled('Save')).toBe(true);

    wrapper.unmount();
  });

  it('offers no Prompt picker, and a rename alone sends no instructions', async () => {
    const wrapper = await mountSettings();

    const labels = wrapper.findAllComponents({ name: 'QSelect' }).map((select) => select.props('label'));
    expect(labels).toEqual(['Agent']);

    await type('Member name', 'Scout Two');
    button('Save').click();
    await settle();

    expect(Object.keys(updateMember.mock.calls[0]![2])).toEqual(['name']);

    wrapper.unmount();
  });

  it('saves on Enter in the name', async () => {
    const wrapper = await mountSettings();

    await type('Member name', 'Scout Two');
    await pressEnter('Member name');

    expect(updateMember).toHaveBeenCalledTimes(1);
    expect(updateMember.mock.calls[0]![2]).toMatchObject({ name: 'Scout Two' });

    wrapper.unmount();
  });

  it('shows a 409 in the dialog and on the name', async () => {
    updateMember.mockRejectedValue(
      Object.assign(new Error("A member named 'Lead' already exists."), { status: 409 }),
    );
    const wrapper = await mountSettings();

    await type('Member name', 'Lead');
    button('Save').click();
    await settle();

    expect(hasError('Member name')).toBe(true);
    expect(document.body.textContent).toContain('already exists');

    wrapper.unmount();
  });

  it('does not flag the name on a 409 that is not about the name', async () => {
    const sentence = 'The member is busy; try again when its run ends.';
    updateMember.mockRejectedValue(Object.assign(new Error(sentence), { status: 409 }));
    getMember.mockResolvedValue(detail({ systemPrompt: 'Old.' }));
    const wrapper = await mountSettings();

    await type('Instructions (optional)', 'New.');
    button('Save').click();
    await settle();

    expect(document.body.textContent).toContain(sentence);
    expect(hasError('Member name')).toBe(false);

    wrapper.unmount();
  });
});

const MemberHint =
  'Who this member is and how it works: added to its prompt after the built-in Member prompt, before the team instructions.';
const ManagerHint =
  "The Manager's own instructions: procedures only it runs, such as what to do when a trigger wakes it.";

function hint(label: string): string {
  return fieldWrapper(label).querySelector('.q-field__messages')?.textContent?.trim() ?? '';
}

function writtenBy(): string | null {
  return document.body.querySelector('[data-written-by]')?.textContent?.trim() ?? null;
}

/**
 * A MEMBER'S OWN INSTRUCTIONS: one field, read from the stored row, saved with PATCH, on every agent
 * member's card - the Manager's too - and absent for a plugin, which has no prompt.
 */
describe('MemberSettingsDialog, the member\'s own instructions', () => {
  it('is prefilled from the stored member, with the member hint and when it takes effect', async () => {
    getMember.mockResolvedValue(detail({ systemPrompt: 'You review pull requests.' }));
    const wrapper = await mountSettings();

    expect(getMember).toHaveBeenCalledWith('alpha', 'scout');
    expect(field('Instructions (optional)').tagName).toBe('TEXTAREA');
    expect(field('Instructions (optional)').value).toBe('You review pull requests.');
    expect(hint('Instructions (optional)')).toBe(MemberHint);
    expect(bodyText()).toContain('Takes effect on its next run.');

    wrapper.unmount();
  });

  it('saves a change through PATCH as systemPrompt', async () => {
    getMember.mockResolvedValue(detail({ systemPrompt: 'You review pull requests.' }));
    const wrapper = await mountSettings();

    await type('Instructions (optional)', 'You review pull requests.\nNever merge. ');
    button('Save').click();
    await settle();

    expect(updateMember).toHaveBeenCalledTimes(1);
    expect(updateMember.mock.calls[0]!.slice(0, 2)).toEqual(['alpha', 'scout']);
    expect(updateMember.mock.calls[0]![2]).toEqual({
      name: 'Scout',
      systemPrompt: 'You review pull requests.\nNever merge.',
    });

    wrapper.unmount();
  });

  it('clears them by sending blank through PATCH', async () => {
    getMember.mockResolvedValue(detail({ systemPrompt: 'You review pull requests.' }));
    const wrapper = await mountSettings();

    await type('Instructions (optional)', '');
    button('Save').click();
    await settle();

    expect(updateMember.mock.calls[0]![2]).toEqual({ name: 'Scout', systemPrompt: '' });

    wrapper.unmount();
  });

  it('is on the Manager\'s card, with the Manager\'s own hint', async () => {
    getMember.mockResolvedValue(detail({ id: 'Manager', name: 'Manager', systemPrompt: 'On a trigger, read the inbox.' }));
    const manager = { ...snapshot, id: asMemberId('Manager'), name: 'Manager' } as ContainerSnapshot;
    const wrapper = await mountSettings(['claude'], manager);

    expect(getMember).toHaveBeenCalledWith('alpha', 'Manager');
    expect(field('Instructions (optional)').value).toBe('On a trigger, read the inbox.');
    expect(hint('Instructions (optional)')).toBe(ManagerHint);

    await type('Instructions (optional)', 'On a trigger, read the inbox first.');
    button('Save').click();
    await settle();

    expect(updateMember.mock.calls[0]![2]).toMatchObject({ systemPrompt: 'On a trigger, read the inbox first.' });

    wrapper.unmount();
  });

  it('is absent for a plugin member, which has no prompt', async () => {
    const plugin = { ...snapshot, agent: 'plugin:sample-echo', kind: 'plugin' } as ContainerSnapshot;
    const wrapper = await mountSettings(['claude'], plugin);

    expect(document.body.querySelector('textarea')).toBeNull();
    expect(document.body.querySelector('[data-member-instructions]')).toBeNull();
    expect(bodyText()).not.toContain('Instructions (optional)');
    expect(bodyText()).not.toContain('Takes effect on its next run.');
    expect(getMember).not.toHaveBeenCalled();

    wrapper.unmount();
  });

  it('says the Manager wrote them when it hired the member', async () => {
    getMember.mockResolvedValue(detail({
      systemPrompt: 'You review pull requests.',
      systemPromptSetBy: 'Manager',
      systemPromptSetByKind: 'manager',
      systemPromptSetAt: '2026-09-20T08:15:00Z',
    }));
    const wrapper = await mountSettings();

    expect(writtenBy()).toBe('Written by the Manager when hiring');

    wrapper.unmount();
  });

  it('names the person who last set them, and the day', async () => {
    getMember.mockResolvedValue(detail({
      systemPrompt: 'You review pull requests.',
      systemPromptSetBy: 'admin@example.com',
      systemPromptSetByKind: 'person',
      systemPromptSetAt: '2026-09-27T23:30:00Z',
    }));
    const wrapper = await mountSettings();

    expect(writtenBy()).toBe('Set by admin@example.com, 2026-09-27');

    wrapper.unmount();
  });

  it('says nothing about who wrote them when nobody is recorded', async () => {
    getMember.mockResolvedValue(detail({ systemPrompt: 'Written before this was recorded.' }));
    const wrapper = await mountSettings();

    expect(field('Instructions (optional)').value).toBe('Written before this was recorded.');
    expect(writtenBy()).toBeNull();
    expect(bodyText()).not.toContain('Written by');
    expect(bodyText()).not.toContain('Set by');

    wrapper.unmount();
  });

  /** A Manager that hired with no instructions wrote nothing: the empty field makes no claim. */
  it('says nothing about the Manager when it hired the member with no instructions', async () => {
    getMember.mockResolvedValue(detail({
      systemPrompt: null,
      systemPromptSetBy: 'Manager',
      systemPromptSetByKind: 'manager',
      systemPromptSetAt: '2026-09-20T08:15:00Z',
    }));
    const wrapper = await mountSettings();

    expect(field('Instructions (optional)').value).toBe('');
    expect(writtenBy()).toBeNull();
    expect(bodyText()).not.toContain('Written by the Manager');

    wrapper.unmount();
  });
});
