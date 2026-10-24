// @vitest-environment happy-dom
//
// HIRING A PLUGIN FROM THE ADD MEMBER DIALOG. Installed plugins are offered beside the Agent
// presets; choosing one shows its manifest's config fields as inputs and one box per secret for the
// new member's own VALUE, and the hire goes through the same `addMember` call with the config and
// the values as `secretValues` - no key name.
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
import { bodyText, helpTexts, mountDialog, resetBody } from '../../test/mountQuasar';
import { hostField, hostList, hostPlugin, hostSecret } from '../../test/pluginFixtures';
import { button, field, isDisabled, settle, type } from '../../test/formProbe';

const headless = (name: string): Agent => ({ name, mode: 'Headless' });

/** The shape `GET /api/plugins` answers, for a plugin with an enum, a number and two secrets. */
const sampleEcho: InstalledPlugin = hostPlugin({
  id: 'sample-echo',
  name: 'Sample Echo',
  description: "Deterministic test plugin: transforms each instruction's text.",
  version: '0.1.0',
  config: {
    mode: hostField({ type: 'string', description: 'How to transform.', default: 'upper', enum: ['upper', 'reverse'] }),
    repeat: hostField({ type: 'number', description: 'How many times.' }),
  },
  secrets: {
    token: hostSecret({ description: 'A demo credential.' }),
    signing: hostSecret({ description: 'Signs the output.', required: true }),
  },
});

const plugins: PluginList = hostList([sampleEcho]);

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

async function choose(wrapper: Parameters<typeof selectLabelled>[0], label: string, value: string | string[]) {
  selectLabelled(wrapper, label).vm.$emit('update:modelValue', value);
  await settle();
}

describe('AddMemberDialog, hiring a plugin', () => {
  it('offers each installed plugin after the Agent presets, by its reference', async () => {
    const wrapper = await mountDialog(AddMemberDialog, baseProps);

    expect(selectLabelled(wrapper, 'Agent').props('options')).toEqual(['claude', 'plugin:sample-echo']);

    wrapper.unmount();
  });

  it('shows the manifest config fields and one empty value box per secret, with no key name and no info icon', async () => {
    const wrapper = await mountDialog(AddMemberDialog, baseProps);

    await choose(wrapper, 'Agent', 'plugin:sample-echo');

    expect(bodyText()).toContain("Deterministic test plugin: transforms each instruction's text.");
    expect(selectLabelled(wrapper, 'Mode').props('options')).toEqual(['upper', 'reverse']);
    expect(selectLabelled(wrapper, 'Mode').props('modelValue')).toBe('upper');
    expect(field('Repeat').value).toBe('');

    // One box per secret for its VALUE, starting empty: no key name is asked for.
    expect(field('Token').value).toBe('');
    expect(field('Token').getAttribute('type')).toBe('password');
    expect(field('Signing').value).toBe('');
    expect(bodyText()).not.toContain('key name');
    expect(helpTexts()).not.toContain('How to transform.');

    wrapper.unmount();
  });

  it('holds Add member until a required secret has a value', async () => {
    const wrapper = await mountDialog(AddMemberDialog, baseProps);

    await type('Member name', 'Echo');
    await choose(wrapper, 'Agent', 'plugin:sample-echo');

    expect(isDisabled('Add member')).toBe(true);

    await type('Signing', 'sign-value-1');

    expect(isDisabled('Add member')).toBe(false);

    wrapper.unmount();
  });

  it('hires the plugin through addMember with its config and the secret values typed', async () => {
    const wrapper = await mountDialog(AddMemberDialog, baseProps);

    await type('Member name', 'Echo');
    await choose(wrapper, 'Agent', 'plugin:sample-echo');
    await choose(wrapper, 'Mode', 'reverse');
    await type('Repeat', '3');
    await type('Token', 'token-value-1');
    await type('Signing', 'sign-value-1');

    button('Add member').click();
    await settle();

    expect(addMember).toHaveBeenCalledTimes(1);
    expect(addMember.mock.calls[0]).toEqual([
      'alpha',
      'Echo',
      'plugin:sample-echo',
      {
        config: { mode: 'reverse', repeat: 3 },
        secrets: {},
        secretValues: { token: 'token-value-1', signing: 'sign-value-1' },
      },
    ]);

    wrapper.unmount();
  });

  it('leaves a field at its default, an empty optional field and an empty optional secret out of the hire', async () => {
    const wrapper = await mountDialog(AddMemberDialog, baseProps);

    await type('Member name', 'Echo');
    await choose(wrapper, 'Agent', 'plugin:sample-echo');
    await type('Signing', 'sign-value-1');

    button('Add member').click();
    await settle();

    expect(addMember.mock.calls[0]?.[3]).toEqual({
      config: {},
      secrets: {},
      secretValues: { signing: 'sign-value-1' },
    });

    wrapper.unmount();
  });

  it('hires with a list setting edited as chips, through the same editor Member settings shows', async () => {
    listPlugins.mockResolvedValue(hostList([{
      ...sampleEcho,
      config: { ...sampleEcho.config, allow: hostField({ type: 'list', enum: ['ops', 'dev'], setBy: 'person' }) },
    }]));
    const wrapper = await mountDialog(AddMemberDialog, baseProps);

    await type('Member name', 'Echo');
    await choose(wrapper, 'Agent', 'plugin:sample-echo');
    await type('Signing', 'sign-value-1');

    // No "Set by a person only" line: what a person must see now stays, nothing else.
    expect(document.body.querySelector('[data-setting="allow"] [data-person-only]')).toBeNull();

    // An enum list is chosen from its values, as chips.
    await choose(wrapper, 'Allow', ['dev']);
    expect(document.body.querySelector('[data-setting="allow"] [data-chip="dev"]')).not.toBeNull();
    expect(addMember).not.toHaveBeenCalled();

    button('Add member').click();
    await settle();

    expect(addMember.mock.calls[0]?.[3]).toEqual({
      config: { allow: ['dev'] },
      secrets: {},
      secretValues: { signing: 'sign-value-1' },
    });

    wrapper.unmount();
  });

  it('still hires an Agent with no plugin settings', async () => {
    const wrapper = await mountDialog(AddMemberDialog, baseProps);

    await type('Member name', 'Scout');
    button('Add member').click();
    await settle();

    expect(addMember.mock.calls[0]).toEqual(['alpha', 'Scout', 'claude', undefined, undefined]);
    expect(document.body.querySelector('[data-plugin-hire]')).toBeNull();

    wrapper.unmount();
  });
});
