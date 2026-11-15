// @vitest-environment happy-dom
import { flushPromises, mount } from '@vue/test-utils';
import { beforeEach, describe, expect, it } from 'vitest';
import TerminalApp from '../../src/renderer/src/TerminalApp.vue';
import type { TerminalBanner, YawbleApi } from '../../src/shared/api';

// A member's terminal: one slim bar over the screen; the effective-policy banner is folded behind the bar's Policy
// toggle, closed by default and remembered. A fake API only (no core, no PTY).
const banner = (over: Partial<TerminalBanner> = {}): TerminalBanner => ({
  title: 'payments / Ben · sandbox · linux · grok · net: model + bus + registry.npmjs.org',
  details: ['timeout 15 min', 'memory 4 GB'], loosened: ['network.allowHosts + registry.npmjs.org'],
  state: 'idle', runId: null, queuePosition: null, queueLength: 0, lastRunState: 'completed', lastRunReason: null, ...over,
} as unknown as TerminalBanner);

function fakeApi(b: TerminalBanner) {
  return {
    getInfo: async () => { throw new Error('no'); },
    getAvailableBackends: async () => [],
    call: async (method: string) => {
      if (method === 'terminal.banner') return b;
      if (method === 'terminal.attach' || method === 'terminal.session') return null;
      return null;
    },
    onEvent: () => () => {},
    openTerminal: async () => {},
    pickFolder: async () => null,
  } as unknown as YawbleApi;
}

const mountTerm = async (b = banner()) => {
  const w = mount(TerminalApp, { props: { memberId: 3, api: fakeApi(b), pollMs: 60_000, embedded: true }, global: { stubs: { TerminalScreen: true } } });
  await flushPromises();
  return w;
};

describe("a member's terminal header", () => {
  beforeEach(() => localStorage.clear());

  it('shows one bar (state, Policy, Stop) and folds the policy banner away by default', async () => {
    const w = await mountTerm();
    expect(w.get('[data-test=terminal-state]').text()).toContain('Idle');
    expect((w.get('[data-test=banner]').element as HTMLElement).style.display).toBe('none');
    const toggle = w.get('[data-test=terminal-details-toggle]');
    expect(toggle.text()).toBe('⚠ Policy ▾'); // loosened: the toggle says so
    expect(toggle.attributes('aria-expanded')).toBe('false');
    w.unmount();
  });

  it('the Policy toggle opens the banner, and the choice is remembered for the next terminal', async () => {
    const w = await mountTerm();
    await w.get('[data-test=terminal-details-toggle]').trigger('click');
    expect((w.get('[data-test=banner]').element as HTMLElement).style.display).toBe('');
    expect(w.get('[data-test=banner-title]').text()).toContain('payments / Ben');
    expect(w.get('[data-test=banner-loosened]').text()).toContain('registry.npmjs.org');
    w.unmount();
    const again = await mountTerm(banner({ loosened: [] }));
    expect((again.get('[data-test=banner]').element as HTMLElement).style.display).toBe('');
    expect(again.get('[data-test=terminal-details-toggle]').text()).toBe('Policy ▴');
    await again.get('[data-test=terminal-details-toggle]').trigger('click');
    expect(localStorage.getItem('yawble.terminal.details')).toBeNull();
    again.unmount();
  });
});
