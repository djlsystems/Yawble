// @vitest-environment happy-dom
//
// THE HEADER IS ONE ROW: the logo at the start of the ribbon, the commands, and the right cluster -
// version, live chip, Needs you, the activity gauge, the account menu - at its end. The brand row
// is gone, and the version is under the logo's tooltip and in the account menu. How the ribbon
// fits its commands beside the cluster is `ribbon-command-bar.mount.spec.ts` ("the one header row").
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

const { concierge, listCatalog, getAgentAuth } = vi.hoisted(() => ({
  concierge: vi.fn(),
  listCatalog: vi.fn(),
  getAgentAuth: vi.fn(),
}));

vi.mock('../../api/client', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  concierge,
  listCatalog,
  getAgentAuth,
  getCapacity: vi.fn(() => new Promise(() => {})),
}));

vi.mock('../../lib/concierge-socket', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  connectConcierge: vi.fn(() => ({ send: vi.fn(), resize: vi.fn(), dispose: vi.fn() })),
}));

import { flushPromises, mount, type VueWrapper } from '@vue/test-utils';
import { createPinia, setActivePinia } from 'pinia';
import { createMemoryHistory, createRouter } from 'vue-router';
import MainLayout from '../MainLayout.vue';
import RibbonBar from '../../components/RibbonBar.vue';
import { useSessionStore } from '../../stores/session';
import { useConsoleStore } from '../../stores/console';
import { bundleBuild, releaseLabel } from '../../lib/buildInfo';
import { GiveWaySteps, keepThrough } from '../../lib/headerFit';
import { productMark, productTitle } from '../../presentation/product';
import { Screen } from 'quasar';
import { Ribbon } from '../../lib/ribbon';
import { resetBody } from '../../test/mountQuasar';

const stubs = {
  ProfileDialog: true,
  BoardDisplayDialog: true,
  CreateTeamDialog: true,
  TeamSettingsDialog: true,
  TeamGitDialog: true,
  DocumentsDialog: true,
  UsersDialog: true,
  AgentsDialog: true,
  ResetTeamDialog: true,
  TenantLogDialog: true,
  SkillsDialog: true,
  OutcomesDialog: true,
  ConciergePanel: true,
};

async function mountShell(): Promise<VueWrapper> {
  setActivePinia(createPinia());
  useSessionStore().$patch({ user: { id: 'u1', email: 'sam@example.com' } });

  const router = createRouter({
    history: createMemoryHistory(),
    routes: [{ path: '/', component: MainLayout, children: [{ path: '', component: { template: '<div />' } }] }],
  });
  await router.push('/');

  const wrapper = mount({ template: '<router-view />' }, { attachTo: document.body, global: { plugins: [router], stubs } });
  await flushPromises();
  await flushPromises();
  return wrapper;
}

beforeEach(() => {
  localStorage.clear();
  concierge.mockReset().mockResolvedValue({ agent: 'claude-interactive', prompt: null, promptName: null });
  listCatalog.mockReset().mockResolvedValue([]);
  getAgentAuth.mockReset().mockResolvedValue([]);
  vi.stubGlobal('fetch', vi.fn(() => new Promise(() => {})));
});

afterEach(() => {
  vi.unstubAllGlobals();
  resetBody();
});

describe('the one header row', () => {
  it('is the ribbon row alone: no brand row above it', async () => {
    const wrapper = await mountShell();
    const header = wrapper.find('.q-header');

    expect(header.findAll('.ribbon')).toHaveLength(1);
    expect(header.find('.q-toolbar').exists()).toBe(false);
    expect(header.findComponent(RibbonBar).exists()).toBe(true);

    wrapper.unmount();
  });

  it('carries the logo at its start and the live chip, Needs you, gauge and account menu at its end, in that order', async () => {
    const wrapper = await mountShell();
    const row = wrapper.find('.q-header .ribbon');

    expect(row.find('.ribbon-start [data-test="brand-link"]').exists()).toBe(true);

    const cluster = row.find('.ribbon-end');
    const order = [
      cluster.find('[data-test="live-chip"]'),
      cluster.find('.needs-you'),
      cluster.find('[data-test="capacity-gauge"]'),
      cluster.find('.user-menu'),
    ];
    for (const part of order) expect(part.exists()).toBe(true);

    const all = [...cluster.element.querySelectorAll('*')];
    const positions = order.map((part) => all.indexOf(part.element));
    expect(positions).toEqual([...positions].sort((a, b) => a - b));
    expect(order[3]!.attributes('aria-label')).toBe('Account: sam@example.com');

    // None of it is inside the commands toolbar, which is what the overflow measures.
    expect(row.find('[role="toolbar"] [data-test="live-chip"]').exists()).toBe(false);

    wrapper.unmount();
  });

  it('says live or offline on the chip, in words', async () => {
    const wrapper = await mountShell();
    const chip = () => wrapper.find('[data-test="live-chip"]');

    expect(chip().text()).toContain('offline');
    useConsoleStore().connected = true;
    await flushPromises();
    expect(chip().text()).toContain('live');

    wrapper.unmount();
  });

  it('puts the version under the logo\'s tooltip and in the account menu', async () => {
    const wrapper = await mountShell();
    const label = releaseLabel(bundleBuild.version);

    const tooltip = wrapper.find('[data-test="brand-link"]').findComponent({ name: 'QTooltip' });
    (tooltip.vm as unknown as { show: () => void }).show();
    await flushPromises();
    expect(document.body.querySelector('.q-tooltip')?.textContent).toContain(label);
    (tooltip.vm as unknown as { hide: () => void }).hide();
    await flushPromises();

    await wrapper.find('.user-menu').trigger('click');
    await flushPromises();
    expect(document.body.querySelector('[data-test="account-version"]')?.textContent).toContain(label);

    wrapper.unmount();
  });

  /**
   * SHORT OF ROOM, THE ROW GIVES WAY IN ITS ORDER: each fitting that keeps fewer than New Team
   * through Backlog takes one step - the version leaves the row, the live chip is a dot, the account
   * menu is its icon, Needs you is its count pill, the commands' padding halves, and last the logo
   * is the head mark - and a fitting that keeps enough takes none. The ribbon's labels stay.
   */
  it('gives way in order, one step per fitting that keeps too few, and none while enough show', async () => {
    const wrapper = await mountShell();
    const ribbon = wrapper.findComponent(RibbonBar);
    const cluster = () => wrapper.find('.q-header .ribbon-end');
    const keep = keepThrough(Ribbon);
    const short = async () => {
      ribbon.vm.$emit('fit', keep - 1);
      await flushPromises();
    };
    const enough = async () => {
      ribbon.vm.$emit('fit', keep);
      await flushPromises();
    };
    /** What has given way, in the order of `GiveWaySteps`. */
    const taken = () => [
      !cluster().find('.version-tag').exists(),
      cluster().find('[data-test="live-chip"]').classes().includes('live-chip-dot'),
      !cluster().find('[data-test="account-name"]').exists(),
      !cluster().find('[data-test="needs-you-word"]').exists(),
      ribbon.props('tight') === true,
      wrapper.find('[data-test="brand-mark"]').exists(),
    ];

    expect(taken()).toEqual([false, false, false, false, false, false]);
    await enough();
    expect(taken()).toEqual([false, false, false, false, false, false]);

    for (let step = 1; step <= GiveWaySteps.length; step++) {
      await short();
      expect(taken(), GiveWaySteps[step - 1]).toEqual(GiveWaySteps.map((_, index) => index < step));
      // A fitting that now keeps enough takes nothing further.
      await enough();
      expect(taken(), GiveWaySteps[step - 1]).toEqual(GiveWaySteps.map((_, index) => index < step));
    }

    // Nothing past the last step; the logo is the mark, named, and the ribbon's labels are whole.
    await short();
    expect(taken()).toEqual(GiveWaySteps.map(() => true));
    const mark = wrapper.get('[data-test="brand-mark"]');
    expect(mark.attributes('src')).toBe(productMark);
    expect(mark.attributes('alt')).toBe(productTitle);
    expect(wrapper.get('[data-test="brand-link"]').attributes('aria-label')).toContain(productTitle);
    expect(wrapper.find('.ribbon-entry').text()).toContain('New Team');

    wrapper.unmount();
  });

  /** What leaves the row stays reachable: the account name in the menu, the Needs you words in its
   *  aria-label and tooltip, the live word in the chip's, the product name in the mark's tooltip. */
  it('keeps what gave way reachable', async () => {
    const wrapper = await mountShell();
    const ribbon = wrapper.findComponent(RibbonBar);
    for (let step = 0; step < GiveWaySteps.length; step++) {
      ribbon.vm.$emit('fit', 0);
      await flushPromises();
    }

    const account = wrapper.get('.user-menu');
    expect(account.attributes('aria-label')).toBe('Account: sam@example.com');
    const needsYou = wrapper.get('[data-test="needs-you"]');
    expect(needsYou.attributes('aria-label')).toMatch(/^Needs you: /);
    expect(needsYou.findComponent({ name: 'QTooltip' }).exists()).toBe(true);
    expect(wrapper.get('[data-test="live-chip"]').attributes('aria-label')).toBe('offline');

    const tooltip = wrapper.find('[data-test="brand-link"]').findComponent({ name: 'QTooltip' });
    (tooltip.vm as unknown as { show: () => void }).show();
    await flushPromises();
    expect(document.body.querySelector('.q-tooltip')?.textContent).toContain(productTitle);
    (tooltip.vm as unknown as { hide: () => void }).hide();
    await flushPromises();

    await account.trigger('click');
    await flushPromises();
    expect(document.body.querySelector('[data-test="account-email"]')?.textContent).toContain('sam@example.com');

    wrapper.unmount();
  });

  /** A wider window starts again from no steps; the ribbon's next fitting takes back what it needs. */
  it('takes back every step when the window widens', async () => {
    const wrapper = await mountShell();
    const ribbon = wrapper.findComponent(RibbonBar);
    for (let step = 0; step < GiveWaySteps.length; step++) {
      ribbon.vm.$emit('fit', 0);
      await flushPromises();
    }
    expect(wrapper.find('[data-test="brand-mark"]').exists()).toBe(true);

    Screen.width = Screen.width + 200;
    await flushPromises();

    expect(wrapper.find('[data-test="brand-mark"]').exists()).toBe(false);
    expect(ribbon.props('tight')).toBe(false);
    expect(wrapper.find('.q-header .ribbon-end .version-tag').exists()).toBe(true);
    expect(wrapper.find('[data-test="account-name"]').exists()).toBe(true);

    wrapper.unmount();
  });
});
