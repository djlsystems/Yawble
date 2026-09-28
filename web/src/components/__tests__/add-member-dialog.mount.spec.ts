// @vitest-environment happy-dom
//
// A PERMIT GRANTS THE VERB, NOT THE FIELDS, and `teams.member_agents` is what closes that gap: a
// manager may HIRE, but it may not choose what the hire RUNS. `allowedAgentOptions` IS the Agent
// picker's options and `allowlistIncludes` IS the Add button's disabled state, so "the dropdown
// offers only what the team allows" is a claim worth making against a rendered dialog rather than
// against source text.
//
// NULL AND EMPTY ARE DIFFERENT STATES - "nobody has chosen" versus "explicitly empty" - and both
// refuse a hire. Both are covered below, separately, because collapsing them is the defect this
// allowlist exists to prevent.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

// `vi.mock` is hoisted above every const in this file, so the mock functions have to rise with it.
const { listCatalog, addMember } = vi.hoisted(() => ({ listCatalog: vi.fn(), addMember: vi.fn() }));

// No plugin is installed here: the Agent picker's options are the presets alone.
const { listPlugins } = vi.hoisted(() => ({ listPlugins: vi.fn(() => Promise.resolve({ plugins: [], refused: [], versions: [] })) }));

// listCatalog runs from watch(open) AND again from useAgentInstallations, so one mock covers both.
vi.mock('../../api/client', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  listCatalog,
  addMember,
  listPlugins,
}));

import AddMemberDialog from '../AddMemberDialog.vue';
import { allowedAgentOptions, normalizeAllowlist } from '../../lib/memberAllowlist';
import type { Agent, TeamId } from '../../api/types';
import { bodyText, mountDialog, resetBody } from '../../test/mountQuasar';
import { blur, button, hasError, isDisabled, pressEnter, settle, type } from '../../test/formProbe';

const headless = (name: string): Agent => ({ name, mode: 'Headless' });

/** Deliberately WIDER than the allowlist below - see the assertion that depends on it. */
const catalog = {
  agents: [headless('claude'), headless('codex'), headless('copilot')],
};

const allowlist = ['claude'];

const baseProps = {
  team: 'alpha' as TeamId,
  teamName: 'Alpha',
  memberAgents: allowlist,
};

beforeEach(() => {
  listCatalog.mockReset();
  listCatalog.mockResolvedValue(catalog);
  addMember.mockReset();
  addMember.mockResolvedValue({ name: 'Scout' });
});

afterEach(resetBody);

function agentSelectOptions(wrapper: { findAllComponents: (s: { name: string }) => unknown[] }) {
  const selects = wrapper.findAllComponents({ name: 'QSelect' }) as {
    props: (name: string) => unknown;
  }[];

  const agentSelect = selects.find((select) => {
    const options = select.props('options');
    return Array.isArray(options);
  });

  if (!agentSelect) throw new Error('no QSelect with options in the rendered dialog');

  return agentSelect.props('options') as string[];
}

describe('AddMemberDialog, mounted', () => {
  it('offers exactly the agents the allowlist permits, not the whole catalog', async () => {
    const expected = allowedAgentOptions(catalog.agents.map((a) => a.name), allowlist);

    // Without a catalog entry OUTSIDE the allowlist, "shows the allowed agents" and "shows
    // everything" are the same assertion and this case proves nothing.
    expect(expected.length).toBeGreaterThan(0);
    expect(expected.length).toBeLessThan(catalog.agents.length);

    const wrapper = await mountDialog(AddMemberDialog, baseProps);

    expect(agentSelectOptions(wrapper)).toEqual(expected);

    wrapper.unmount();
  });

  it('says so, visibly, when the team has chosen an EMPTY allowlist', async () => {
    expect(normalizeAllowlist([])).toHaveLength(0);

    const wrapper = await mountDialog(AddMemberDialog, { ...baseProps, memberAgents: [] });

    expect(document.body.querySelector('.text-negative')).not.toBeNull();
    expect(bodyText()).toContain('empty allowlist');

    wrapper.unmount();
  });

  it('says so, visibly, when the team has chosen NOTHING at all', async () => {
    expect(normalizeAllowlist(null)).toHaveLength(0);

    const wrapper = await mountDialog(AddMemberDialog, { ...baseProps, memberAgents: null });

    expect(document.body.querySelector('.text-negative')).not.toBeNull();

    wrapper.unmount();
  });

  it('offers nothing when the allowlist names an Agent the catalog does not have', async () => {
    // A hiring Agent removed from the catalog costs nothing visible and refuses the team's next
    // hire - which is why PUT /api/agents checks this reference. The dialog's half is that the
    // picker comes back empty rather than falling back to the whole catalog.
    const wrapper = await mountDialog(AddMemberDialog, { ...baseProps, memberAgents: ['gone'] });

    expect(agentSelectOptions(wrapper)).toEqual([]);

    wrapper.unmount();
  });
});

describe('AddMemberDialog, validated', () => {
  it('disables Add member while the name is empty', async () => {
    const wrapper = await mountDialog(AddMemberDialog, baseProps);

    expect(isDisabled('Add member')).toBe(true);

    wrapper.unmount();
  });

  it('enables Add member with a name and the allowed Agent', async () => {
    const wrapper = await mountDialog(AddMemberDialog, baseProps);

    await type('Member name', 'Scout');

    expect(isDisabled('Add member')).toBe(false);

    wrapper.unmount();
  });

  it('refuses a whitespace-only name under the field', async () => {
    const wrapper = await mountDialog(AddMemberDialog, baseProps);

    await type('Member name', '   ');
    await blur('Member name');

    expect(hasError('Member name')).toBe(true);
    expect(isDisabled('Add member')).toBe(true);

    wrapper.unmount();
  });

  it('submits on Enter in the name', async () => {
    const wrapper = await mountDialog(AddMemberDialog, baseProps);

    await type('Member name', ' Scout ');
    await pressEnter('Member name');

    expect(addMember).toHaveBeenCalledTimes(1);
    expect(addMember.mock.calls[0]).toEqual(['alpha', 'Scout', 'claude', undefined, undefined]);

    wrapper.unmount();
  });

  it('puts the empty allowlist on the Agent picker as its error state', async () => {
    const wrapper = await mountDialog(AddMemberDialog, { ...baseProps, memberAgents: [] });

    expect(hasError('Agent')).toBe(true);
    expect(isDisabled('Add member')).toBe(true);

    wrapper.unmount();
  });

  it('shows a 409 in the dialog and on the name, and stays open', async () => {
    addMember.mockRejectedValue(
      Object.assign(new Error("A member named 'Scout' already exists."), { status: 409 }),
    );
    const wrapper = await mountDialog(AddMemberDialog, baseProps);

    await type('Member name', 'Scout');
    button('Add member').click();
    await settle();

    expect(hasError('Member name')).toBe(true);
    expect(document.body.querySelector('.q-banner')?.textContent).toContain('already exists');
    expect(wrapper.emitted('update:modelValue')).toBeUndefined();

    wrapper.unmount();
  });
});
