// @vitest-environment happy-dom
//
// AN AGENT MEMBER'S OWN INSTRUCTIONS, given when a person adds it: an "Instructions (optional)" box
// whose words ride the hire as `systemPrompt`. A plugin has no prompt, so choosing one takes the box
// away and the hire carries none.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

const { listCatalog, listPlugins, addMember } = vi.hoisted(() => ({
  listCatalog: vi.fn(),
  listPlugins: vi.fn(),
  addMember: vi.fn(),
}));

vi.mock('../../api/client', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  listCatalog,
  listPlugins,
  addMember,
}));

import AddMemberDialog from '../AddMemberDialog.vue';
import type { InstalledPlugin, TeamId } from '../../api/types';
import { mountDialog, resetBody } from '../../test/mountQuasar';
import { hostList, hostPlugin } from '../../test/pluginFixtures';
import { button, field, settle, type } from '../../test/formProbe';

const sampleEcho: InstalledPlugin = hostPlugin({
  id: 'sample-echo',
  name: 'Sample Echo',
  description: 'Deterministic test plugin.',
  version: '0.1.0',
});

const baseProps = {
  team: 'alpha' as TeamId,
  teamName: 'Alpha',
  memberAgents: ['claude'],
};

const Hint =
  'Who this member is and how it works: added to its prompt after the built-in Member prompt, before the team instructions.';

beforeEach(() => {
  listCatalog.mockReset();
  listCatalog.mockResolvedValue({ agents: [{ name: 'claude', mode: 'Headless' }] });
  listPlugins.mockReset();
  listPlugins.mockResolvedValue(hostList([sampleEcho]));
  addMember.mockReset();
  addMember.mockResolvedValue({ name: 'Scout' });
});

afterEach(resetBody);

type Select = { props: (name: string) => unknown; vm: { $emit: (event: string, value: unknown) => void } };

async function chooseAgent(wrapper: { findAllComponents: (s: { name: string }) => unknown[] }, value: string) {
  const found = (wrapper.findAllComponents({ name: 'QSelect' }) as Select[]).find((s) => s.props('label') === 'Agent');
  if (!found) throw new Error('no Agent picker');
  found.vm.$emit('update:modelValue', value);
  await settle();
}

function hasField(label: string): boolean {
  try {
    field(label);
    return true;
  } catch {
    return false;
  }
}

describe('AddMemberDialog, a member\'s own instructions', () => {
  it('offers Instructions (optional) for an Agent, with its hint, as a multi-line box', async () => {
    const wrapper = await mountDialog(AddMemberDialog, baseProps);

    expect(field('Instructions (optional)').tagName).toBe('TEXTAREA');
    expect(document.body.textContent).toContain(Hint);

    wrapper.unmount();
  });

  it('sends what was typed as systemPrompt on an Agent\'s hire', async () => {
    const wrapper = await mountDialog(AddMemberDialog, baseProps);

    await type('Member name', 'Scout');
    await type('Instructions (optional)', '  You review pull requests.\nNever merge.  ');
    button('Add member').click();
    await settle();

    expect(addMember).toHaveBeenCalledTimes(1);
    expect(addMember.mock.calls[0]).toEqual([
      'alpha', 'Scout', 'claude', undefined, 'You review pull requests.\nNever merge.',
    ]);

    wrapper.unmount();
  });

  it('is absent for a plugin, and the plugin\'s hire carries no instructions', async () => {
    const wrapper = await mountDialog(AddMemberDialog, baseProps);

    await type('Member name', 'Echo');
    await type('Instructions (optional)', 'Typed before the plugin was chosen.');
    await chooseAgent(wrapper, 'plugin:sample-echo');

    expect(hasField('Instructions (optional)')).toBe(false);
    expect(document.body.textContent).not.toContain(Hint);

    button('Add member').click();
    await settle();

    expect(addMember).toHaveBeenCalledTimes(1);
    const call = addMember.mock.calls[0]!;
    expect(call.slice(0, 3)).toEqual(['alpha', 'Echo', 'plugin:sample-echo']);
    expect(call[4]).toBeUndefined();

    wrapper.unmount();
  });
});
