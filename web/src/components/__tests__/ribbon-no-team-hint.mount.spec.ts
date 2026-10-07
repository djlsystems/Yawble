// @vitest-environment happy-dom
//
// A GREYED RIBBON COMMAND SAYS WHY. With no team chosen every `team-` command is disabled, and a
// row of dead buttons with no reason reads as a broken console. Each one says what to do instead:
// on hover on the strip, and as a caption on a phone, where nothing hovers.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils';

vi.mock('../../lib/useAgentInstallations', () => ({
  useAgentInstallations: () => ({ badge: { value: null } }),
  refreshAgentInstallations: vi.fn(),
}));

import RibbonBar from '../RibbonBar.vue';
import RibbonMobileMenu from '../RibbonMobileMenu.vue';
import { Ribbon, needsActiveWorkTeam } from '../../lib/ribbon';
import { useConsoleStore } from '../../stores/console';
import { asTeamId } from '../../api/types';
import '../../test/mountQuasar';

const Hint = 'Pick or create a team first';

/** Every command the ribbon greys out while no team is chosen - the Active Team block. */
const TeamCommands = Ribbon.tabs
  .flatMap((tab) => tab.items)
  .filter((item) => item.kind === 'button' && needsActiveWorkTeam(item.action, undefined));

beforeEach(() => {
  setActivePinia(createPinia());
});

afterEach(() => {
  document.body.innerHTML = '';
});

async function render(component: typeof RibbonBar | typeof RibbonMobileMenu): Promise<VueWrapper> {
  const wrapper = mount(component, { attachTo: document.body });
  await flushPromises();

  return wrapper as VueWrapper;
}

/** The hover text of the strip command at `index` in the bar, or null when it has no tooltip. */
async function hoverText(wrapper: VueWrapper, index: number): Promise<string | null> {
  const wrap = wrapper.findAll('.ribbon-item-wrap')[index]!;
  const tooltip = wrap.findComponent({ name: 'QTooltip' });
  if (!tooltip.exists()) return null;

  (tooltip.vm as unknown as { show: () => void }).show();
  await flushPromises();
  const text = document.body.querySelector('.q-tooltip')?.textContent?.trim() ?? '';
  (tooltip.vm as unknown as { hide: () => void }).hide();
  await flushPromises();

  return text;
}

/** Index among the strip's ordinary buttons, found by label so reordering the strip is harmless. */
function wrapIndex(wrapper: VueWrapper, label: string, nth = 0): number {
  const all = wrapper.findAll('.ribbon-item-wrap');
  const matches = all.map((w, i) => [w, i] as const).filter(([w]) => w.find('.q-btn').text().endsWith(label));

  return matches[nth]![1];
}

describe('a greyed ribbon command with no team chosen', () => {
  it('there are seven of them, so the hint is not tested on an empty list', () => {
    expect(TeamCommands).toHaveLength(7);
  });

  it('says to pick or create a team, on hover, on every one of them', async () => {
    const wrapper = await render(RibbonBar);

    const disabled = wrapper
      .findAll('.ribbon-item-wrap')
      .map((wrap, index) => ({ wrap, index }))
      .filter(({ wrap }) => wrap.find('.q-btn').attributes('disabled') !== undefined);
    expect(disabled).toHaveLength(TeamCommands.length);

    for (const { index } of disabled) {
      expect(await hoverText(wrapper, index)).toBe(Hint);
    }
  });

  it('drops the hint once a team is chosen', async () => {
    const board = useConsoleStore();
    board.teams = [{ id: 'alpha', name: 'Alpha' }] as never;
    board.activeTeamId = asTeamId('alpha');
    const wrapper = await render(RibbonBar);

    // Members has no description of its own, so with a team it has no tooltip at all.
    expect(await hoverText(wrapper, wrapIndex(wrapper, 'Members'))).toBeNull();
  });

  it('leaves a command that needs no team saying only what it does', async () => {
    const wrapper = await render(RibbonBar);

    // The instance-wide Settings is last on the strip and is never disabled.
    const settings = wrapper.findAll('.ribbon-item-wrap').length - 1;
    expect(await hoverText(wrapper, settings)).toBe(Ribbon.tabs.at(-1)!.items.at(-1)!.tooltip);
  });

  it('puts the same words under each greyed row in the phone menu', async () => {
    const wrapper = await render(RibbonMobileMenu);

    for (const item of TeamCommands) {
      const row = wrapper
        .findAllComponents({ name: 'QItem' })
        .find((candidate) => candidate.props('disable') && candidate.text().includes(item.label!));
      expect(row, `no greyed row for ${item.label}`).toBeDefined();
      expect(row!.text()).toContain(Hint);
    }
  });
});
