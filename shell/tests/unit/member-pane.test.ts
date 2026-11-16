// @vitest-environment happy-dom
import { flushPromises, mount } from '@vue/test-utils';
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { defineComponent, h } from 'vue';
import MemberPane from '../../src/renderer/src/components/MemberPane.vue';
import { setOutputHeight } from '../../src/renderer/src/agentOutputHeight';
import { getMemberView, resetMemberViews, setMemberView } from '../../src/renderer/src/memberPaneView';
import type { AgentCard, TerminalBanner, YawbleApi } from '../../src/shared/api';

// One member's pane on the team page (B061): one frame with a title bar, one toolbar (the actions and the
// Activity | Terminal toggle) and a body (what was said on the left, the Activity or the terminal on the right).
// Fakes only: the core is a scripted fake, the terminal screen a stand-in (no xterm, no PTY).
const member = { id: 7, teamId: 1, team: 'payments', name: 'DeveloperMarlo', role: 'member', cli: 'claude', instructions: '', model: '', credential: 'default' } as const;
const card = (over: Partial<AgentCard> = {}, m: Partial<AgentCard['member']> = {}): AgentCard => ({
  member: { ...member, ...m }, state: 'idle', queuePosition: null, queueLength: 0, queuedInstructions: 0, statusLine: null, runId: null,
  runStartedAt: null, lastRunState: null, lastRunReason: null, net: 'model + bus', wallTimeout: '2 h', loosened: [], policyProblems: [], ...over,
});
const report = 'handed back: Card 42 is done.\n\n**Delivered**\n1. **Settings > Health tab** (new `HealthPanel.vue`)\n2. Backups section';

const banner = (memberId: number): TerminalBanner => ({
  memberId, title: `m${memberId}`, state: 'idle', runId: null, queuePosition: null, queueLength: 0, lastRunState: null, lastRunReason: null,
  details: [], loosened: [], interactive: false,
} as unknown as TerminalBanner);
/** The core: counts every call, per method and member. */
function fakeApi() {
  const calls: { method: string; params: any }[] = [];
  const api = {
    getInfo: async () => { throw new Error('no'); }, getAvailableBackends: async () => [],
    call: async (method: string, params?: any) => {
      calls.push({ method, params });
      if (method === 'terminal.banner') return banner(params.memberId);
      return {};
    },
    onEvent: () => () => {},
  } as unknown as YawbleApi;
  const count = (method: string, memberId?: number) => calls.filter((c) => c.method === method && (memberId === undefined || c.params?.memberId === memberId)).length;
  return { api, calls, count };
}
/** The terminal screen: the channel it shows, a fitted size, and how many are mounted. */
const screens = new Set<string>();
const FakeScreen = defineComponent({
  props: { channel: { type: String, required: true } },
  setup(props, { expose }) {
    expose({ size: () => ({ cols: 100, rows: 30 }) });
    screens.add(props.channel);
    return () => h('div', { class: 'fake-screen', 'data-channel': props.channel });
  },
  unmounted() { screens.delete((this as any).channel); },
});
const stubs = { global: { stubs: { TerminalScreen: FakeScreen } } };
const mounted: { unmount(): void }[] = [];
const pane = (props: Record<string, unknown>) => { const w = mount(MemberPane, { props: props as any, ...stubs }); mounted.push(w); return w; };

beforeEach(() => { localStorage.clear(); resetMemberViews(); setOutputHeight(120); screens.clear(); });
afterEach(() => { vi.useRealTimers(); while (mounted.length) { try { mounted.pop()!.unmount(); } catch { /* already */ } } });

describe("a member's pane", () => {
  it('is one frame: the title bar, one toolbar, then the two areas side by side', () => {
    const w = pane({ card: card({ state: 'running', statusLine: 'Split work' }) });
    const root = w.get('[data-test=agent-DeveloperMarlo]');
    expect(root.element.tagName).toBe('SECTION');
    expect([...root.element.children].map((c) => c.getAttribute('data-test'))).toEqual(['agent-title', 'member-toolbar', 'member-pane-body']);
    const title = w.get('[data-test=agent-title]');
    expect(title.find('[data-test=agent-name]').exists()).toBe(true);
    expect(title.get('[data-test=agent-face-state]').text()).toBe('Running');
    const bar = w.get('[data-test=member-toolbar]');
    for (const b of ['open-interactive-cli', 'open-settings', 'open-logs', 'stop', 'remove-agent', 'member-view-activity', 'member-view-terminal'])
      expect(bar.find(`[data-test=${b}]`).exists(), b).toBe(true);
    const body = w.get('[data-test=member-pane-body]');
    expect([...body.element.children].map((c) => c.getAttribute('data-test'))).toEqual(['member-said', 'member-side']);
    expect(body.get('[data-test=member-said]').find('[data-test=agent-output]').exists()).toBe(true);
    expect(body.get('[data-test=member-side]').find('[data-test=member-activity]').exists()).toBe(true);
  });

  it('a loosened agent shows only a short marker, which opens its settings at the Policy tab; no guardrail button', async () => {
    const w = pane({ card: card({ loosened: ['network.allowHosts + registry.npmjs.org', 'compute.wallTimeoutSec + 4 h'] }) });
    const chip = w.get('[data-test=loosened-chip]');
    expect(chip.text()).toBe('⚠ loosened');
    expect(chip.attributes('title')).toContain('registry.npmjs.org');
    expect(w.find('[data-test=open-guardrail]').exists()).toBe(false);
    await chip.trigger('click');
    expect(w.emitted('policy')).toHaveLength(1);
    expect(pane({ card: card() }).find('[data-test=loosened-chip]').exists()).toBe(false);
  });

  it('the loosened marker is the same size as the role and CLI chips (no font of its own)', () => {
    const css = readFileSync(path.resolve(__dirname, '../../src/renderer/src/styles.css'), 'utf8');
    const rule = /^\.chip\.loosened \{([^}]*)\}/m.exec(css)![1];
    expect(rule).not.toMatch(/\bfont(-size)?\s*:/);
    expect(/^\.chip \{([^}]*)\}/m.exec(css)![1]).toContain('font-size: 11px');
  });

  it('the toolbar asks for Settings, Logs, Stop and Remove; Stop only while running, Remove never while running', async () => {
    const idle = pane({ card: card() });
    await idle.get('[data-test=open-settings]').trigger('click');
    await idle.get('[data-test=open-logs]').trigger('click');
    await idle.get('[data-test=remove-agent]').trigger('click');
    expect(Object.keys(idle.emitted())).toEqual(expect.arrayContaining(['settings', 'logs', 'remove']));
    expect(idle.get('[data-test=stop]').attributes('disabled')).toBeDefined();
    const running = pane({ card: card({ state: 'running' }) });
    expect(running.get('[data-test=stop]').attributes('disabled')).toBeUndefined();
    expect(running.get('[data-test=remove-agent]').attributes('disabled')).toBeDefined();
    await running.get('[data-test=stop]').trigger('click');
    expect(running.emitted('stop')).toHaveLength(1);
    // The Manager cannot be removed.
    expect(pane({ card: card({}, { role: 'manager', name: 'Manager' }) }).find('[data-test=remove-agent]').exists()).toBe(false);
  });

  it("shows what the agent said as formatted text in its own box, not squeezed into the state line", () => {
    const w = pane({ card: card({ lastRunState: 'completed', statusLine: report }) });
    const out = w.get('[data-test=agent-output]');
    expect(out.find('strong').text()).toBe('Delivered');
    expect(out.findAll('ol li').map((li) => li.text())).toEqual(['Settings > Health tab (new HealthPanel.vue)', 'Backups section']);
    expect(out.find('code').text()).toBe('HealthPanel.vue');
    expect(w.get('[data-test=agent-state]').text()).not.toContain('Delivered');
    expect(w.get('[data-test=agent-state]').text()).toContain('last run completed');
  });

  it('never renders HTML an agent wrote', () => {
    const w = pane({ card: card({ state: 'running', statusLine: '<img src=x onerror=alert(1)> hi' }) });
    expect(w.get('[data-test=agent-output]').find('img').exists()).toBe(false);
  });

  it('the body is the shared height (what was said scrolls inside), and keeps the box when there is nothing said', () => {
    const w = pane({ card: card({ state: 'running', statusLine: report }) });
    expect((w.get('[data-test=member-pane-body]').element as HTMLElement).style.height).toBe('120px');
    const out = w.get('[data-test=agent-output]');
    expect(out.classes()).toContain('scrolls');
    expect(out.find('[data-test=agent-output-empty]').exists()).toBe(false);
    const quiet = pane({ card: card() });
    const box = quiet.get('[data-test=agent-output]');
    expect(box.get('[data-test=agent-output-empty]').text()).toBe('Nothing said yet.');
    expect(box.get('[data-test=agent-output-empty]').classes()).toContain('muted');
    const ranBefore = pane({ card: card({ lastRunState: 'completed', statusLine: '  ' }) });
    expect(ranBefore.get('[data-test=agent-output-empty]').text()).toBe('No report since the app started.');
  });

  it('keeps the shared height for next time', () => {
    setOutputHeight(240);
    const again = pane({ card: card({ state: 'running', statusLine: report }) });
    expect((again.get('[data-test=member-pane-body]').element as HTMLElement).style.height).toBe('240px');
    expect(localStorage.getItem('yawble.agentOutput')).toBe('240');
  });

  it('its one resize bar sits between the two areas and sets their width (the height is the gaps on the team board)', () => {
    const w = pane({ card: card() });
    const grips = w.findAll('[role=slider]');
    expect(grips.map((g) => g.attributes('data-test'))).toEqual(['member-activity-handle-w']);
    expect(grips[0]!.element.parentElement!.getAttribute('data-test')).toBe('member-side');
  });
});

describe("a member's pane: Activity | Terminal", () => {
  it('starts on Activity; the toggle shows the terminal in the right area and back, remembered per member', async () => {
    const fake = fakeApi();
    const w = pane({ card: card(), api: fake.api, pollMs: 60_000 });
    expect(w.get('[data-test=member-view-activity]').attributes('aria-selected')).toBe('true');
    expect(w.find('[data-test=member-terminal]').exists()).toBe(false);
    await w.get('[data-test=member-view-terminal]').trigger('click');
    await flushPromises();
    expect(w.get('[data-test=member-view-terminal]').attributes('aria-selected')).toBe('true');
    expect(w.find('[data-test=member-activity]').exists()).toBe(false);
    const side = w.get('[data-test=member-side]');
    expect(side.find('[data-test=member-terminal] .fake-screen').attributes('data-channel')).toBe('member:7');
    // The terminal's own bar: what it shows and Policy; Open and Stop are the pane's toolbar.
    expect(side.find('[data-test=terminal-state]').exists()).toBe(true);
    expect(side.find('[data-test=terminal-details-toggle]').exists()).toBe(true);
    expect(side.find('[data-test=open-interactive]').exists()).toBe(false);
    expect(side.find('[data-test=terminal-stop]').exists()).toBe(false);
    expect(getMemberView('payments', 'DeveloperMarlo')).toBe('terminal');
    w.unmount();
    // Shown again (the page re-opened): still the terminal.
    const again = pane({ card: card(), api: fake.api, pollMs: 60_000 });
    expect(again.find('[data-test=member-terminal]').exists()).toBe(true);
    await again.get('[data-test=member-view-activity]').trigger('click');
    expect(again.find('[data-test=member-terminal]').exists()).toBe(false);
    expect(getMemberView('payments', 'DeveloperMarlo')).toBe('activity');
  });

  it('follows the shared view: an entry point setting Terminal shows it', async () => {
    const w = pane({ card: card(), api: fakeApi().api, pollMs: 60_000 });
    setMemberView('payments', 'DeveloperMarlo', 'terminal');
    await flushPromises();
    expect(w.find('[data-test=member-terminal]').exists()).toBe(true);
  });

  it('two members can show their terminals at once, each its own channel', async () => {
    const fake = fakeApi();
    setMemberView('payments', 'DeveloperMarlo', 'terminal');
    setMemberView('payments', 'TesterWren', 'terminal');
    // Both on one page, as the team page shows them.
    const Page = defineComponent(() => () => h('div', [
      h(MemberPane, { card: card(), api: fake.api, pollMs: 60_000 }),
      h(MemberPane, { card: card({}, { id: 8, name: 'TesterWren' }), api: fake.api, pollMs: 60_000 }),
    ]));
    const w = mount(Page, stubs);
    mounted.push(w);
    await flushPromises();
    expect(w.findAll('[data-test=member-terminal] .fake-screen').map((s) => s.attributes('data-channel'))).toEqual(['member:7', 'member:8']);
    expect([...screens].sort()).toEqual(['member:7', 'member:8']);
    expect(fake.count('terminal.banner', 7)).toBe(1);
    expect(fake.count('terminal.banner', 8)).toBe(1);
  });

  it('only a shown terminal polls: switching to Activity stops it', async () => {
    vi.useFakeTimers();
    const fake = fakeApi();
    const w = pane({ card: card(), api: fake.api, pollMs: 1000 });
    await vi.advanceTimersByTimeAsync(5000);
    expect(fake.count('terminal.banner')).toBe(0);
    await w.get('[data-test=member-view-terminal]').trigger('click');
    await vi.advanceTimersByTimeAsync(3000);
    const shown = fake.count('terminal.banner', 7);
    expect(shown).toBeGreaterThanOrEqual(3);
    expect(screens.has('member:7')).toBe(true);
    await w.get('[data-test=member-view-activity]').trigger('click');
    await vi.advanceTimersByTimeAsync(5000);
    expect(fake.count('terminal.banner', 7)).toBe(shown);
    expect(screens.size).toBe(0);
  });

  it('Open interactive CLI shows the terminal and starts the CLI at its fitted size; held while the member runs', async () => {
    const fake = fakeApi();
    const w = pane({ card: card(), api: fake.api, pollMs: 60_000 });
    await w.get('[data-test=open-interactive-cli]').trigger('click');
    await flushPromises();
    expect(w.find('[data-test=member-terminal]').exists()).toBe(true);
    expect(fake.calls.find((c) => c.method === 'terminal.openInteractive')?.params).toEqual({ memberId: 7, cols: 100, rows: 30 });
    expect(pane({ card: card({ state: 'running' }), api: fake.api }).get('[data-test=open-interactive-cli]').attributes('disabled')).toBeDefined();
  });
});

describe("a member's pane: what waits, and no policy summary", () => {
  it('says how many tells wait behind a running member, with why in its tooltip', () => {
    const w = pane({ card: card({ state: 'running', queuedInstructions: 2 }) });
    const more = w.get('[data-test=agent-title]').get('[data-test=agent-more-waiting]');
    expect(more.text()).toBe('· 2 more waiting');
    expect(more.attributes('title')).toBe('2 instructions waiting; they run after this one (oldest workflow first)');
    const one = pane({ card: card({ state: 'running', queuedInstructions: 1 }) });
    expect(one.get('[data-test=agent-more-waiting]').attributes('title')).toBe('1 instruction waiting; they run after this one (oldest workflow first)');
    expect(pane({ card: card({ state: 'running', queuedInstructions: 0 }) }).find('[data-test=agent-more-waiting]').exists()).toBe(false);
    // A queued member already says its instructions in the state line.
    expect(pane({ card: card({ state: 'queued', queuePosition: 1, queueLength: 1, queuedInstructions: 2 }) })
      .find('[data-test=agent-more-waiting]').exists()).toBe(false);
  });

  it('shows no network / timeout summary, but keeps policy problems', () => {
    const w = pane({ card: card({ net: 'model + bus + 3 hosts', wallTimeout: '2 h', lastRunState: 'completed' }) });
    const state = w.get('[data-test=agent-state]').text();
    expect(state).not.toContain('net:');
    expect(state).not.toContain('model + bus');
    expect(state).not.toContain('timeout');
    expect(state).toBe('last run completed');
    const bad = pane({ card: card({ policyProblems: ['global compute.idleTimeoutSec must be 60..86400'] }) });
    expect(bad.get('[data-test=agent-policy-problems]').text()).toBe('policy: global compute.idleTimeoutSec must be 60..86400');
  });
});
