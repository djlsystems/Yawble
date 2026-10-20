// @vitest-environment happy-dom
//
// THE COMMAND PALETTE IN THE REAL SHELL: the account menu says its shortcut once and opens it, the
// first-run guide's last step says the shortcut, a ribbon-command row runs the ribbon's own handler
// and an outcome row opens the Outcomes dialog at that outcome. Ctrl+K from the board opens it, and
// opening it minimizes the Concierge. The palette's own rows are `command-palette.mount.spec.ts`.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

const { concierge, listCatalog, getAgentAuth, backlogItems } = vi.hoisted(() => ({
  concierge: vi.fn(),
  listCatalog: vi.fn(),
  getAgentAuth: vi.fn(),
  backlogItems: vi.fn(),
}));

vi.mock('../../api/client', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  concierge,
  listCatalog,
  getAgentAuth,
  backlogItems,
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
import CommandPalette from '../../components/CommandPalette.vue';
import CreateTeamDialog from '../../components/CreateTeamDialog.vue';
import OutcomesDialog from '../../components/OutcomesDialog.vue';
import ConciergePanel from '../../components/ConciergePanel.vue';
import { useSessionStore } from '../../stores/session';
import { PaletteShortcut } from '../../lib/palette';
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
    routes: [{ path: '/', component: MainLayout, children: [{ path: '', component: { template: '<button data-test="board-control">card</button>' } }] }],
  });
  await router.push('/');

  const wrapper = mount({ template: '<router-view />' }, { attachTo: document.body, global: { plugins: [router], stubs } });
  await flushPromises();
  await flushPromises();
  return wrapper;
}

const palette = (wrapper: VueWrapper) => wrapper.findComponent(CommandPalette);

beforeEach(() => {
  localStorage.clear();
  concierge.mockReset().mockResolvedValue({ agent: 'claude-interactive', prompt: null, promptName: null });
  listCatalog.mockReset().mockResolvedValue([]);
  getAgentAuth.mockReset().mockResolvedValue([]);
  backlogItems.mockReset().mockResolvedValue([]);
  vi.stubGlobal('fetch', vi.fn(() => new Promise(() => {})));
});

afterEach(() => {
  vi.unstubAllGlobals();
  resetBody();
});

describe('the palette in the shell', () => {
  it('the account menu says the shortcut once and opens the palette', async () => {
    const wrapper = await mountShell();
    expect(palette(wrapper).props('open')).toBe(false);

    await wrapper.find('.user-menu').trigger('click');
    await flushPromises();

    const item = document.body.querySelector<HTMLElement>('[data-test="account-command-palette"]');
    expect(item, 'no Find anything item in the account menu').not.toBeNull();
    expect(item!.textContent).toContain('Find anything');
    const shortcuts = document.body.querySelectorAll('[data-test="account-palette-shortcut"]');
    expect(shortcuts).toHaveLength(1);
    expect(shortcuts[0]!.textContent?.trim()).toBe(PaletteShortcut);
    expect(PaletteShortcut).toBe('Ctrl+K');

    item!.click();
    await flushPromises();
    expect(palette(wrapper).props('open')).toBe(true);
    wrapper.unmount();
  });

  it('the first-run guide says the shortcut in its last step', async () => {
    const wrapper = await mountShell();
    const steps = document.body.querySelectorAll('[data-test="first-run-guide"] li');
    expect(steps.length).toBeGreaterThan(0);
    const last = steps[steps.length - 1]!;
    expect(last.querySelector('[data-test="first-run-guide-shortcut"]')?.textContent?.trim()).toBe(PaletteShortcut);
    expect(document.body.querySelectorAll('[data-test="first-run-guide-shortcut"]')).toHaveLength(1);
    wrapper.unmount();
  });

  it('Ctrl+K from the board opens it and minimizes the Concierge', async () => {
    const wrapper = await mountShell();
    const panel = wrapper.findComponent(ConciergePanel);
    await panel.vm.$emit('update:modelValue', true);
    await flushPromises();
    expect(panel.props('modelValue')).toBe(true);

    const control = document.body.querySelector<HTMLElement>('[data-test="board-control"]')!;
    control.dispatchEvent(new KeyboardEvent('keydown', { key: 'k', ctrlKey: true, bubbles: true, cancelable: true }));
    await flushPromises();

    expect(palette(wrapper).props('open')).toBe(true);
    expect(panel.props('modelValue')).toBe(false);
    wrapper.unmount();
  });

  it('a ribbon-command row runs the ribbon handler', async () => {
    const wrapper = await mountShell();
    palette(wrapper).vm.$emit('action', 'admin-new-team');
    await flushPromises();
    expect(wrapper.findComponent(CreateTeamDialog).props('modelValue')).toBe(true);
    wrapper.unmount();
  });

  it('an outcome row opens the Outcomes dialog at that outcome, and Admin > Outcomes at the list', async () => {
    const wrapper = await mountShell();
    palette(wrapper).vm.$emit('outcome', 'o-1');
    await flushPromises();
    const outcomes = wrapper.findComponent(OutcomesDialog);
    expect(outcomes.props('modelValue')).toBe(true);
    expect(outcomes.props('outcome')).toBe('o-1');

    await outcomes.vm.$emit('update:modelValue', false);
    palette(wrapper).vm.$emit('action', 'admin-outcomes');
    await flushPromises();
    expect(outcomes.props('modelValue')).toBe(true);
    expect(outcomes.props('outcome')).toBeNull();
    wrapper.unmount();
  });
});
