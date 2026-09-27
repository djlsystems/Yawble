// @vitest-environment happy-dom
//
// HIRING A PLUGIN FROM THE ADD MEMBER DIALOG. Installed plugins are offered beside the Agent
// presets; choosing one shows its manifest's config fields as inputs and its secrets BY NAME, and
// the hire goes through the same `addMember` call with the config and the secret's LOGICAL KEY.
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
import type { Agent, InstalledPlugin, PluginList, TeamId } from '../../api/types';
import { bodyText, mountDialog, resetBody } from '../../test/mountQuasar';
import { button, field, isDisabled, settle, type } from '../../test/formProbe';

const headless = (name: string): Agent => ({ name, mode: 'Headless' });

/** The shape `GET /api/plugins` answers, for a plugin with an enum, a number and two secrets. */
const sampleEcho: InstalledPlugin = {
  id: 'sample-echo',
  reference: 'plugin:sample-echo',
  name: 'Sample Echo',
  description: "Deterministic test plugin: transforms each instruction's text.",
  version: '0.1.0',
  config: {
    mode: { type: 'string', description: 'How to transform.', required: false, default: 'upper', enum: ['upper', 'reverse'] },
    repeat: { type: 'number', description: 'How many times.', required: false, default: null, enum: null },
  },
  secrets: {
    token: { description: 'A demo credential.', required: false },
    signing: { description: 'Signs the output.', required: true },
  },
};

const plugins: PluginList = { plugins: [sampleEcho], refused: [] };

const baseProps = {
  team: 'alpha' as TeamId,
  teamName: 'Alpha',
  memberAgents: ['claude'],
};

beforeEach(() => {
  listCatalog.mockReset();
  listCatalog.mockResolvedValue({ agents: [headless('claude')] });
  listPlugins.mockReset();
  listPlugins.mockResolvedValue(plugins);
  addMember.mockReset();
  addMember.mockResolvedValue({ name: 'Echo' });
});

afterEach(resetBody);

type Select = { props: (name: string) => unknown; vm: { $emit: (event: string, value: unknown) => void } };

function selectLabelled(wrapper: { findAllComponents: (s: { name: string }) => unknown[] }, label: string): Select {
  const found = (wrapper.findAllComponents({ name: 'QSelect' }) as Select[]).find((s) => s.props('label') === label);
  if (!found) throw new Error(`no QSelect labelled "${label}"`);
  return found;
}

async function choose(wrapper: Parameters<typeof selectLabelled>[0], label: string, value: string) {
  selectLabelled(wrapper, label).vm.$emit('update:modelValue', value);
  await settle();
}

describe('AddMemberDialog, hiring a plugin', () => {
  it('offers each installed plugin after the Agent presets, by its reference', async () => {
    const wrapper = await mountDialog(AddMemberDialog, baseProps);

    expect(selectLabelled(wrapper, 'Agent').props('options')).toEqual(['claude', 'plugin:sample-echo']);

    wrapper.unmount();
  });

  it('shows the manifest config fields and the secrets by name, with no value', async () => {
    const wrapper = await mountDialog(AddMemberDialog, baseProps);

    await choose(wrapper, 'Agent', 'plugin:sample-echo');

    expect(bodyText()).toContain("Deterministic test plugin: transforms each instruction's text.");
    expect(selectLabelled(wrapper, 'mode').props('options')).toEqual(['upper', 'reverse']);
    expect(selectLabelled(wrapper, 'mode').props('modelValue')).toBe('upper');
    expect(field('repeat').value).toBe('');

    // Secrets by NAME: each asks for the key, and starts empty - there is no value to show.
    expect(field('Secret token: key name').value).toBe('');
    expect(field('Secret signing: key name').value).toBe('');
    expect(bodyText()).toContain('never its value');

    wrapper.unmount();
  });

  it('holds Add member until a required secret has a key', async () => {
    const wrapper = await mountDialog(AddMemberDialog, baseProps);

    await type('Member name', 'Echo');
    await choose(wrapper, 'Agent', 'plugin:sample-echo');

    expect(isDisabled('Add member')).toBe(true);

    await type('Secret signing: key name', 'ECHO_SIGNING_KEY');

    expect(isDisabled('Add member')).toBe(false);

    wrapper.unmount();
  });

  it('hires the plugin through addMember with its config and the secret keys', async () => {
    const wrapper = await mountDialog(AddMemberDialog, baseProps);

    await type('Member name', 'Echo');
    await choose(wrapper, 'Agent', 'plugin:sample-echo');
    await choose(wrapper, 'mode', 'reverse');
    await type('repeat', '3');
    await type('Secret token: key name', ' SAMPLE_ECHO_TOKEN ');
    await type('Secret signing: key name', 'ECHO_SIGNING_KEY');

    button('Add member').click();
    await settle();

    expect(addMember).toHaveBeenCalledTimes(1);
    expect(addMember.mock.calls[0]).toEqual([
      'alpha',
      'Echo',
      'plugin:sample-echo',
      {
        config: { mode: 'reverse', repeat: 3 },
        secrets: { token: 'SAMPLE_ECHO_TOKEN', signing: 'ECHO_SIGNING_KEY' },
      },
    ]);

    wrapper.unmount();
  });

  it('leaves an empty optional field and an unnamed optional secret out of the hire', async () => {
    const wrapper = await mountDialog(AddMemberDialog, baseProps);

    await type('Member name', 'Echo');
    await choose(wrapper, 'Agent', 'plugin:sample-echo');
    await type('Secret signing: key name', 'ECHO_SIGNING_KEY');

    button('Add member').click();
    await settle();

    expect(addMember.mock.calls[0]?.[3]).toEqual({
      config: { mode: 'upper' },
      secrets: { signing: 'ECHO_SIGNING_KEY' },
    });

    wrapper.unmount();
  });

  it('still hires an Agent with no plugin settings', async () => {
    const wrapper = await mountDialog(AddMemberDialog, baseProps);

    await type('Member name', 'Scout');
    button('Add member').click();
    await settle();

    expect(addMember.mock.calls[0]).toEqual(['alpha', 'Scout', 'claude']);
    expect(document.body.querySelector('[data-plugin-hire]')).toBeNull();

    wrapper.unmount();
  });
});
