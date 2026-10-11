// @vitest-environment happy-dom
//
// THE AGENTS BADGE, ON THE DESKTOP STRIP AND IN THE PHONE DRAWER, IN BOTH ROLES. The shared list of
// installations is the fixture; the badge is the real one derived from it. A server that runs its
// runs itself says "not installed on this machine"; one whose agent CLIs are on workers names the
// worker, and with every CLI measured installed - or nothing measured at all - there is no badge.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';
import { flushPromises, mount } from '@vue/test-utils';
import { computed } from 'vue';

import type { AgentInstallation } from '../../api/types';

const fixture = vi.hoisted(() => ({ installations: [] as AgentInstallation[] }));

vi.mock('../../lib/useAgentInstallations', async () => {
  const { agentsBadge } = await vi.importActual<typeof import('../../lib/agentInstall')>('../../lib/agentInstall');
  return {
    useAgentInstallations: () => ({
      installations: computed(() => fixture.installations),
      badge: computed(() => agentsBadge(fixture.installations)),
    }),
    refreshAgentInstallations: vi.fn(),
  };
});

import RibbonBar from '../RibbonBar.vue';
import RibbonMobileMenu from '../RibbonMobileMenu.vue';
import '../../test/mountQuasar';

const at = '2026-10-02T12:00:00Z';

const entry = (agent: string, overrides: Partial<AgentInstallation>): AgentInstallation => ({
  agent,
  command: agent,
  state: null,
  resolvedPath: null,
  referenced: true,
  message: '',
  ...overrides,
});

beforeEach(() => {
  setActivePinia(createPinia());
  // Wide enough that every command sits on the strip.
  vi.spyOn(HTMLElement.prototype, 'clientWidth', 'get').mockImplementation(function (this: HTMLElement) {
    return this.classList.contains('ribbon') ? 100_000 : 0;
  });
});

afterEach(() => {
  vi.restoreAllMocks();
  document.body.innerHTML = '';
});

/** Every badge label on the strip and in the drawer, as a person or a screen reader is told it. */
async function labels(installations: AgentInstallation[]): Promise<{ strip: string[]; drawer: string[] }> {
  fixture.installations = installations;

  const strip = mount(RibbonBar, { attachTo: document.body });
  await flushPromises();
  const stripLabels = [...strip.element.querySelectorAll('.q-badge[aria-label]')].map((b) => b.getAttribute('aria-label') ?? '');
  strip.unmount();

  const drawer = mount(RibbonMobileMenu, { attachTo: document.body });
  await flushPromises();
  const drawerLabels = [...drawer.element.querySelectorAll('.q-badge[aria-label]')].map((b) => b.getAttribute('aria-label') ?? '');
  const caption = drawer.find('.ribbon-menu-warning');
  if (caption.exists()) expect(drawerLabels.some((label) => caption.text().trim().endsWith(label))).toBe(true);
  drawer.unmount();

  return { strip: stripLabels, drawer: drawerLabels };
}

describe('the Agents badge', () => {
  it("in all, the badge says '… not installed on this machine …' — desktop and drawer", async () => {
    const said = await labels([
      entry('copilot', { state: 'AgentNotInstalled', message: "copilot was not found on this machine's PATH." }),
    ]);

    const expected = 'copilot is not installed on this machine, and a team uses it.';
    expect(said).toEqual({ strip: [expected], drawer: [expected] });
  });

  it("in control, the badge says 'grok-headless is not installed on worker-1, and a team uses it.' — desktop and drawer", async () => {
    const said = await labels([
      entry('claude-headless', { message: 'claude is installed on worker-1.', measuredOn: [{ worker: 'worker-1', installed: true, at }] }),
      entry('grok-headless', {
        state: 'AgentNotInstalled',
        message: 'grok is not installed on worker-1.',
        measuredOn: [{ worker: 'worker-1', installed: false, at }],
      }),
    ]);

    const expected = 'grok-headless is not installed on worker-1, and a team uses it.';
    expect(said).toEqual({ strip: [expected], drawer: [expected] });
  });

  it('in control, when workers disagree, the badge names the worker it is missing on — desktop and drawer', async () => {
    const said = await labels([
      entry('grok-headless', {
        state: 'AgentNotInstalled',
        message: 'grok is installed on worker-1 but not on worker-2: the workers disagree.',
        measuredOn: [{ worker: 'worker-1', installed: true, at }, { worker: 'worker-2', installed: false, at }],
      }),
    ]);

    const expected = 'grok-headless is not installed on worker-2, and a team uses it.';
    expect(said).toEqual({ strip: [expected], drawer: [expected] });
  });

  it('in control, with every CLI measured installed on a worker, the Agents item carries no badge — desktop and drawer', async () => {
    const said = await labels([
      entry('claude-headless', { message: 'claude is installed on worker-1.', measuredOn: [{ worker: 'worker-1', installed: true, at }] }),
      entry('grok-headless', { message: 'grok is installed on worker-1.', measuredOn: [{ worker: 'worker-1', installed: true, at }] }),
    ]);

    expect(said).toEqual({ strip: [], drawer: [] });
  });

  it('in control, not-measured presets light no badge — desktop and drawer', async () => {
    const said = await labels([
      entry('claude-headless', { message: 'claude has not been measured: no worker is connected.', measuredOn: [] }),
      entry('grok-headless', { message: 'grok has not been measured: no worker is connected.', measuredOn: [] }),
    ]);

    expect(said).toEqual({ strip: [], drawer: [] });
  });
});

// WHILE THE PLATFORM'S UPDATE HOLDS A CLI A TEAM USES, THE BADGE SAYS SO IN WORDS - on the desktop
// strip and in the drawer - and never that it is not installed; once the update ends and the CLI is
// measured installed, there is no badge.
describe('the Agents badge while an update holds a CLI', () => {
  async function texts(installations: AgentInstallation[]): Promise<{ strip: string; drawer: string }> {
    fixture.installations = installations;
    const strip = mount(RibbonBar, { attachTo: document.body });
    await flushPromises();
    const stripText = [...strip.element.querySelectorAll('.q-badge')].map((b) => `${b.textContent} ${b.getAttribute('aria-label')}`).join(' ');
    strip.unmount();
    const drawer = mount(RibbonMobileMenu, { attachTo: document.body });
    await flushPromises();
    const drawerText = drawer.text() + ' ' + [...drawer.element.querySelectorAll('.q-badge')].map((b) => b.getAttribute('aria-label')).join(' ');
    drawer.unmount();
    return { strip: stripText, drawer: drawerText };
  }

  it("in control, while claude updates on worker-1 the Agents item says so in words and never 'not installed' — desktop and drawer", async () => {
    const said = await labels([
      entry('claude-headless', {
        state: 'AgentUpdating',
        message: 'Updating claude on worker-1; it is measured again when the update ends.',
        measuredOn: [{ worker: 'worker-1', installed: false, at }],
        updating: { phase: 'updating', worker: 'worker-1', since: at },
      }),
    ]);

    const expected = 'claude-headless is being updated on worker-1; its runs wait for it.';
    expect(said).toEqual({ strip: [expected], drawer: [expected] });

    const { strip, drawer } = await texts(fixture.installations);
    for (const text of [strip, drawer]) {
      expect(text).toMatch(/being updated/);
      expect(text).not.toMatch(/not installed/i);
      expect(text).not.toMatch(/install it/i);
    }
    expect(strip).toContain('↻');
  });

  it('while the update waits, the badge says its runs wait and names no worker — desktop and drawer', async () => {
    const said = await labels([
      entry('claude-headless', {
        state: 'AgentUpdating',
        message: 'Updating claude: waiting for 1 run of it to finish, then it runs on one worker; it is measured again when the update ends.',
        measuredOn: [{ worker: 'worker-1', installed: true, at }],
        updating: { phase: 'waiting', worker: null, since: at },
      }),
    ]);

    const expected = 'claude-headless is being updated once its runs finish; new runs wait for it.';
    expect(said).toEqual({ strip: [expected], drawer: [expected] });
    expect(said.strip[0]).not.toMatch(/not installed/i);
  });

  it('once the update ends and the CLI is installed the badge is gone — desktop and drawer', async () => {
    const said = await labels([
      entry('claude-headless', { message: 'claude is installed on worker-1.', measuredOn: [{ worker: 'worker-1', installed: true, at }] }),
    ]);

    expect(said).toEqual({ strip: [], drawer: [] });
  });
});
