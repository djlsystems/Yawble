// @vitest-environment happy-dom
//
// AN EXISTING MEMBER'S SETTINGS. The name carries `memberName`, an Agent picker with nothing to
// offer is in its own error state, and a refusal stays in the dialog - a 409 on the name as well.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';

const { listCatalog, updateMember } = vi.hoisted(() => ({
  listCatalog: vi.fn(),
  updateMember: vi.fn(),
}));

vi.mock('../../api/client', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  listCatalog,
  updateMember,
}));

import MemberSettingsDialog from '../MemberSettingsDialog.vue';
import { useConsoleStore } from '../../stores/console';
import { asMemberId, asTeamId, type ContainerSnapshot, type Team } from '../../api/types';
import { bodyText, mountDialog, resetBody } from '../../test/mountQuasar';
import { blur, button, hasError, isDisabled, pressEnter, settle, type } from '../../test/formProbe';

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
});

afterEach(resetBody);

async function mountSettings(memberAgents: string[] | null = ['claude']) {
  setActivePinia(createPinia());
  useConsoleStore().teams = [{ id: asTeamId('alpha'), name: 'Alpha', memberAgents } as unknown as Team];

  return mountDialog(MemberSettingsDialog, { snapshot }, { pinia: false });
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

  /** What a member is told comes from the build by role, so there is nothing to choose here. */
  it('offers no Prompt picker and no added instructions, and sends no prompt', async () => {
    const wrapper = await mountSettings();

    const labels = wrapper.findAllComponents({ name: 'QSelect' }).map((select) => select.props('label'));
    expect(labels).toEqual(['Agent']);
    expect(document.body.querySelector('textarea')).toBeNull();
    expect(bodyText()).toContain('Additional instructions');

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
});
