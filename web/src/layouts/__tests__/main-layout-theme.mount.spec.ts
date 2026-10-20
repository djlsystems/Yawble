// @vitest-environment happy-dom
//
// THE THEME, THROUGH THE REAL SHELL. App.vue is mounted with MainLayout as its route, because App
// is where the appearance is applied: a spec that mounted the layout alone would be testing a
// dialog with nothing listening to it. This clicks what a person clicks - the account menu's Theme
// line, then a theme in Themes - and reads what changed: the class on <body> every dark token in
// `app.scss` hangs off, what Save sent, and what a reload reads back from the server. The Concierge
// terminal is the exception: it keeps xterm's own colours, which are what the agent's CLI draws for.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

const { concierge, listCatalog, getAppearance, saveAppearance } = vi.hoisted(() => ({
  concierge: vi.fn(),
  listCatalog: vi.fn(),
  getAppearance: vi.fn(),
  saveAppearance: vi.fn(),
}));

vi.mock('../../api/client', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  concierge,
  listCatalog,
  getAppearance,
  saveAppearance,
  // The app bar's activity monitor reads its route on mount; nothing here is about its figures.
  getCapacity: vi.fn(() => new Promise(() => {})),
}));

const { connectConcierge } = vi.hoisted(() => ({
  connectConcierge: vi.fn(() => ({ send: vi.fn(), resize: vi.fn(), dispose: vi.fn() })),
}));

vi.mock('../../lib/concierge-socket', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  connectConcierge,
}));

// The real xterm Terminal, recording what the panel built it with and keeping each instance, so the
// test can read the theme the open terminal actually holds.
const { terminals } = vi.hoisted(() => ({
  terminals: [] as { options: { theme?: unknown }; built: { theme?: unknown } }[],
}));

vi.mock('@xterm/xterm', async (importOriginal) => {
  const real = await importOriginal<typeof import('@xterm/xterm')>();

  class RecordingTerminal extends real.Terminal {
    readonly built: { theme?: unknown };

    constructor(options?: ConstructorParameters<typeof real.Terminal>[0]) {
      super(options);
      this.built = { ...(options ?? {}) };
      terminals.push(this as unknown as (typeof terminals)[number]);
    }
  }

  return { ...real, Terminal: RecordingTerminal };
});

import { flushPromises, mount } from '@vue/test-utils';
import { createPinia, setActivePinia } from 'pinia';
import { Dark } from 'quasar';
import { createMemoryHistory, createRouter } from 'vue-router';
import App from '../../App.vue';
import { productLogoDark, productLogoLight } from '../../presentation/product';
import { bundleBuild, releaseLabel } from '../../lib/buildInfo';
import MainLayout from '../MainLayout.vue';
import { useConsoleStore } from '../../stores/console';
import { useSessionStore } from '../../stores/session';
import type { Team, TeamId } from '../../api/types';
import { resetBody } from '../../test/mountQuasar';

function giveTheDomALayout(): void {
  for (const property of ['clientWidth', 'offsetWidth'] as const) {
    Object.defineProperty(window.HTMLElement.prototype, property, { value: 800, configurable: true });
  }

  for (const property of ['clientHeight', 'offsetHeight'] as const) {
    Object.defineProperty(window.HTMLElement.prototype, property, { value: 600, configurable: true });
  }
}

const team: Team = {
  id: 'alpha' as TeamId,
  name: 'Alpha',
  concierge: 'claude-interactive',
  memberAgents: null,
  additionalInstructions: null,
  root: null,
  containers: [],
};

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
};

async function mountShell() {
  setActivePinia(createPinia());

  useSessionStore().$patch({ user: { id: 'u1', email: 'sam@example.com' } });
  useConsoleStore().$patch({ teams: [team], activeTeamId: team.id, overviewLanded: true });

  const router = createRouter({
    history: createMemoryHistory(),
    routes: [
      {
        path: '/',
        component: MainLayout,
        children: [{ path: '', component: { template: '<div />' } }],
      },
    ],
  });

  await router.push('/');

  const wrapper = mount(App, {
    attachTo: document.body,
    global: { plugins: [router], stubs },
  });

  await flushPromises();

  return wrapper;
}

async function openAccountMenu(wrapper: Awaited<ReturnType<typeof mountShell>>) {
  await wrapper.find('.user-menu').trigger('click');
  await flushPromises();
}

/** The Themes dialog's choice for a theme, opening the dialog from the account menu if it is not. */
async function choose(wrapper: Awaited<ReturnType<typeof mountShell>>, id: string) {
  if (!document.body.querySelector('[data-test="themes-save"]')) {
    await openAccountMenu(wrapper);
    const line = document.body.querySelector<HTMLElement>('[data-test="theme-open"]');
    expect(line, 'no Theme line in the account menu').not.toBeNull();
    line!.click();
    await flushPromises();
  }

  const choice = document.body.querySelector<HTMLElement>(`[data-test="theme-choice-${id}"]`);
  expect(choice, `no "${id}" in Themes`).not.toBeNull();
  choice!.click();
  await flushPromises();
}

async function save() {
  document.body.querySelector<HTMLElement>('[data-test="themes-save"]')!.click();
  await flushPromises();
}

beforeEach(() => {
  localStorage.clear();
  Dark.set(false);
  terminals.length = 0;
  concierge.mockReset();
  concierge.mockResolvedValue({ agent: 'claude-interactive', prompt: null, promptName: null });
  listCatalog.mockReset();
  listCatalog.mockResolvedValue([]);
  getAppearance.mockReset();
  getAppearance.mockResolvedValue({ appearance: null, instanceDefault: 'light' });
  saveAppearance.mockReset();
  saveAppearance.mockResolvedValue(new Response(null, { status: 204 }));
  giveTheDomALayout();
});

afterEach(() => {
  resetBody();
  document.body.style.overflow = '';
});

describe('the theme, from the account menu', () => {
  it('turns the console dark and back', async () => {
    const wrapper = await mountShell();

    await choose(wrapper, 'dark');

    expect(document.body.classList.contains('body--dark')).toBe(true);

    await choose(wrapper, 'light');

    expect(document.body.classList.contains('body--dark')).toBe(false);
    expect(document.body.classList.contains('body--light')).toBe(true);

    wrapper.unmount();
  });

  /** The top bar carries the full logo for the theme: dark lettering in light, white in dark. */
  it('shows the logo for the theme in the top bar', async () => {
    const wrapper = await mountShell();
    const logo = () => document.body.querySelector('.brand-link img')?.getAttribute('src');

    expect(logo()).toBe(productLogoLight);
    expect(document.body.querySelector('.brand-wordmark'), 'the name is in the logo, not typed beside it').toBeNull();

    await choose(wrapper, 'dark');
    expect(logo()).toBe(productLogoDark);

    await choose(wrapper, 'light');
    expect(logo()).toBe(productLogoLight);

    wrapper.unmount();
  });

  /** The version sits right of the logo, in the same title row, in both themes. */
  it('shows this build at the start of the right cluster, on the one header row', async () => {
    const wrapper = await mountShell();
    const cluster = document.body.querySelector('.q-header .ribbon-end')!;
    const version = cluster.querySelector('.version-tag');

    expect(version?.textContent?.trim()).toBe(releaseLabel(bundleBuild.version));
    expect(cluster.firstElementChild).toBe(version);

    await choose(wrapper, 'dark');
    expect(document.body.querySelector('.q-header .ribbon-end .version-tag')?.textContent?.trim()).toBe(releaseLabel(bundleBuild.version));
    await choose(wrapper, 'light');

    wrapper.unmount();
  });

  /** "Survives reload": Save sends the choice to the server, and a reload paints what it answers. */
  it('saves the choice for the person and a reload paints it', async () => {
    const wrapper = await mountShell();

    await choose(wrapper, 'dark');
    await save();

    expect(saveAppearance).toHaveBeenCalledWith({ selected: 'dark' });

    wrapper.unmount();

    // The reload: the page starts light, the server answers the saved record, and App applies it.
    Dark.set(false);
    localStorage.clear();
    getAppearance.mockResolvedValue({ appearance: { selected: 'dark' }, instanceDefault: 'light' });

    const reloaded = await mountShell();

    expect(document.body.classList.contains('body--dark')).toBe(true);

    reloaded.unmount();
  });

  /** Cancel puts back what was saved: the change was a preview. */
  it('puts back the saved theme on Cancel', async () => {
    const wrapper = await mountShell();

    await choose(wrapper, 'dark');
    expect(document.body.classList.contains('body--dark')).toBe(true);

    document.body.querySelector<HTMLElement>('[data-test="themes-cancel"]')!.click();
    await flushPromises();

    expect(document.body.classList.contains('body--dark')).toBe(false);
    expect(saveAppearance).not.toHaveBeenCalled();

    wrapper.unmount();
  });

  /**
   * THE CONCIERGE TERMINAL KEEPS THE AGENT'S OWN COLOURS. The agent's CLI draws for a terminal's
   * default palette, so the panel hands xterm no theme and does not repaint it on a toggle.
   */
  it('leaves the Concierge terminal in the agent\'s own colours, through a toggle', async () => {
    const wrapper = await mountShell();

    await wrapper.find('.concierge-fab').trigger('click');
    await flushPromises();
    await flushPromises();

    expect(document.querySelector('.xterm'), 'the terminal never attached').not.toBeNull();
    expect(terminals, 'the panel built no terminal').toHaveLength(1);
    expect(terminals[0]!.built.theme, 'the panel handed xterm a theme').toBeUndefined();

    const before = JSON.stringify(terminals[0]!.options.theme ?? {});

    await choose(wrapper, 'dark');

    expect(JSON.stringify(terminals[0]!.options.theme ?? {})).toBe(before);

    wrapper.unmount();
  });
});
