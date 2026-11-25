// @vitest-environment happy-dom
import { flushPromises, mount } from '@vue/test-utils';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { defineComponent, h } from 'vue';
import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import MemberPane from '../../src/renderer/src/components/MemberPane.vue';
import { setOutputHeight } from '../../src/renderer/src/agentOutputHeight';
import { getMemberView, resetMemberViews, setMemberView } from '../../src/renderer/src/memberPaneView';
import type { AgentCard, TerminalBanner, YawbleApi } from '../../src/shared/api';

// One member's pane on the team page: one frame with a title bar (its ☰ menu holds every action), a row of tabs
// (Report, Activity, Terminal) and one view, the chosen tab's.
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

/** Opens the pane's ☰ menu (every action is in it). */
async function menu(w: ReturnType<typeof pane>) {
  if (!w.find('[data-test=member-more-menu]').exists()) await w.get('[data-test=member-more]').trigger('click');
  return w.get('[data-test=member-more-menu]');
}

describe("a member's pane", () => {
  it('is one frame: the title bar (with its ☰), the tabs, then one view', async () => {
    const w = pane({ card: card({ state: 'running', statusLine: 'Split work' }) });
    const root = w.get('[data-test=agent-DeveloperMarlo]');
    expect(root.element.tagName).toBe('SECTION');
    // The running outline is decoration (aria-hidden), drawn over the frame's edge, not part of it.
    const parts = [...root.element.children].filter((c) => c.getAttribute('aria-hidden') !== 'true');
    expect(parts.map((c) => c.getAttribute('data-test'))).toEqual(['agent-title', 'member-tabs', 'member-pane-body']);
    expect(root.find('[data-test=running-outline]').exists()).toBe(true);
    const title = w.get('[data-test=agent-title]');
    expect(title.find('[data-test=agent-name]').exists()).toBe(true);
    expect(title.get('[data-test=agent-face-state]').text()).toBe('Running');
    expect(title.get('[data-test=agent-cli]').text()).toBe('claude');
    // The ☰ is always there, at the right; its menu holds every action.
    expect(title.element.lastElementChild!.querySelector('[data-test=member-more]')).not.toBeNull();
    expect(w.find('[data-test=member-toolbar]').exists()).toBe(false);
    const m = await menu(w);
    for (const b of ['open-interactive-cli', 'open-settings', 'open-logs', 'stop', 'remove-agent']) expect(m.find(`[data-test=${b}]`).exists(), b).toBe(true);
    expect(w.get('[data-test=member-tabs]').findAll('[role=tab]').map((t) => t.text())).toEqual(['Report', 'Activity', 'Terminal']);
    // One view: the Report, what the member said, and nothing beside it.
    const body = w.get('[data-test=member-pane-body]');
    expect([...body.element.children].map((c) => c.getAttribute('data-test'))).toEqual(['agent-output']);
    expect(body.find('[data-test=member-activity]').exists()).toBe(false);
  });

  it('shows no loosened marker (what is loosened is in the agent settings)', () => {
    const w = pane({ card: card({ loosened: ['network.allowHosts + registry.npmjs.org'] }) });
    expect(w.find('[data-test=loosened-chip]').exists()).toBe(false);
    expect(w.text()).not.toContain('loosened');
    expect(w.find('[data-test=open-guardrail]').exists()).toBe(false);
  });

  it('the menu asks for Settings, Logs, Stop and Remove and closes; Stop only while running, Remove never while running', async () => {
    const idle = pane({ card: card() });
    for (const b of ['open-settings', 'open-logs', 'remove-agent']) {
      await (await menu(idle)).get(`[data-test=${b}]`).trigger('click');
      expect(idle.find('[data-test=member-more-menu]').exists(), b).toBe(false);
    }
    expect(Object.keys(idle.emitted())).toEqual(expect.arrayContaining(['settings', 'logs', 'remove']));
    expect((await menu(idle)).get('[data-test=stop]').attributes('disabled')).toBeDefined();
    const running = pane({ card: card({ state: 'running' }) });
    const m = await menu(running);
    expect(m.get('[data-test=stop]').attributes('disabled')).toBeUndefined();
    expect(m.get('[data-test=remove-agent]').attributes('disabled')).toBeDefined();
    await m.get('[data-test=stop]').trigger('click');
    expect(running.emitted('stop')).toHaveLength(1);
    // Held while the core answers.
    expect((await menu(pane({ card: card({ state: 'running' }), pending: 'stop' }))).get('[data-test=stop]').text()).toContain('Stopping…');
    // The Manager cannot be removed.
    expect((await menu(pane({ card: card({}, { role: 'manager', name: 'Manager' }) }))).find('[data-test=remove-agent]').exists()).toBe(false);
  });

  it('the menu closes on Esc and on a click outside it', async () => {
    const w = mount(MemberPane, { props: { card: card() } as any, attachTo: document.body, ...stubs });
    mounted.push(w);
    await w.get('[data-test=member-more]').trigger('click');
    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' }));
    await flushPromises();
    expect(w.find('[data-test=member-more-menu]').exists()).toBe(false);
    await w.get('[data-test=member-more]').trigger('click');
    expect(w.find('[data-test=member-more-menu]').exists()).toBe(true);
    document.body.dispatchEvent(new Event('pointerdown', { bubbles: true }));
    await flushPromises();
    expect(w.find('[data-test=member-more-menu]').exists()).toBe(false);
  });

  it("the Report shows what the agent said as formatted text; the run's details are beside the tabs", () => {
    const w = pane({ card: card({ lastRunState: 'completed', statusLine: report }) });
    const out = w.get('[data-test=agent-output]');
    expect(out.find('strong').text()).toBe('Delivered');
    expect(out.findAll('ol li').map((li) => li.text())).toEqual(['Settings > Health tab (new HealthPanel.vue)', 'Backups section']);
    expect(out.find('code').text()).toBe('HealthPanel.vue');
    const state = w.get('[data-test=member-tabs] [data-test=agent-state]');
    expect(state.text()).not.toContain('Delivered');
    expect(state.text()).toContain('last run completed');
  });

  it('never renders HTML an agent wrote', () => {
    const w = pane({ card: card({ state: 'running', statusLine: '<img src=x onerror=alert(1)> hi' }) });
    expect(w.get('[data-test=agent-output]').find('img').exists()).toBe(false);
  });

  it('the view is the shared height (the Report scrolls inside), and keeps the box when there is nothing said', () => {
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

  it('has no resize bar of its own (the height is the gaps on the team board)', () => {
    expect(pane({ card: card() }).findAll('[role=slider]')).toHaveLength(0);
  });
});

describe("a member's pane: Report | Activity | Terminal", () => {
  it('starts on the Report; each tab shows only its view, remembered per member', async () => {
    const fake = fakeApi();
    const w = pane({ card: card(), api: fake.api, pollMs: 60_000 });
    expect(w.get('[data-test=member-view-report]').attributes('aria-selected')).toBe('true');
    await w.get('[data-test=member-view-activity]').trigger('click');
    expect(w.find('[data-test=member-activity]').exists()).toBe(true);
    expect(w.find('[data-test=agent-output]').exists()).toBe(false);
    expect(getMemberView('payments', 'DeveloperMarlo')).toBe('activity');
    await w.get('[data-test=member-view-terminal]').trigger('click');
    await flushPromises();
    expect(w.get('[data-test=member-view-terminal]').attributes('aria-selected')).toBe('true');
    expect(w.find('[data-test=member-activity]').exists()).toBe(false);
    expect(w.find('[data-test=agent-output]').exists()).toBe(false);
    const body = w.get('[data-test=member-pane-body]');
    expect(body.find('[data-test=member-terminal] .fake-screen').attributes('data-channel')).toBe('member:7');
    // Just the screen, Formatted: no bar, no Policy, no Formatted / Raw choice, no Open interactive CLI, no Stop of its own.
    expect(body.find('.term-window > .bar').exists()).toBe(false);
    expect(body.find('[data-test=terminal-state]').exists()).toBe(false);
    expect(body.find('[data-test=terminal-details-toggle]').exists()).toBe(false);
    expect(body.find('[data-test=banner]').exists()).toBe(false);
    expect(body.find('[data-test=terminal-modes]').exists()).toBe(false);
    expect(body.find('[data-test=open-interactive]').exists()).toBe(false);
    expect(body.find('[data-test=terminal-stop]').exists()).toBe(false);
    expect(getMemberView('payments', 'DeveloperMarlo')).toBe('terminal');
    w.unmount();
    // Shown again (the page re-opened): still the terminal.
    const again = pane({ card: card(), api: fake.api, pollMs: 60_000 });
    expect(again.find('[data-test=member-terminal]').exists()).toBe(true);
    await again.get('[data-test=member-view-report]').trigger('click');
    expect(again.find('[data-test=member-terminal]').exists()).toBe(false);
    expect(getMemberView('payments', 'DeveloperMarlo')).toBe('report');
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

  it('only a shown terminal polls: switching to another tab stops it', async () => {
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

  it('the terminal here takes no input: its screen is told noInput', async () => {
    setMemberView('payments', 'DeveloperMarlo', 'terminal');
    const seen: unknown[] = [];
    const Probe = defineComponent({
      props: { channel: String, noInput: Boolean },
      setup(props, { expose }) { expose({ size: () => null }); seen.push(props.noInput); return () => h('div'); },
    });
    const w = mount(MemberPane, { props: { card: card(), api: fakeApi().api, pollMs: 60_000 } as any, global: { stubs: { TerminalView: Probe } } });
    mounted.push(w);
    await flushPromises();
    expect(seen).toEqual([true]);
  });

  it("Open interactive CLI opens the agent's terminal window and starts nothing here; held while the member runs", async () => {
    const fake = fakeApi();
    const opened: number[] = [];
    (fake.api as any).openTerminal = async (id: number) => { opened.push(id); };
    const w = pane({ card: card(), api: fake.api, pollMs: 60_000 });
    await (await menu(w)).get('[data-test=open-interactive-cli]').trigger('click');
    await flushPromises();
    expect(opened).toEqual([7]);
    expect(fake.calls.some((c) => c.method === 'terminal.openInteractive')).toBe(false);
    expect(w.find('[data-test=member-terminal]').exists()).toBe(false);
    expect((await menu(pane({ card: card({ state: 'running' }), api: fake.api }))).get('[data-test=open-interactive-cli]').attributes('disabled')).toBeDefined();
  });

  it('the remote view has no Open interactive CLI (no terminal window there)', async () => {
    const fake = fakeApi();
    (fake.api as any).remote = true;
    const m = await menu(pane({ card: card(), api: fake.api, pollMs: 60_000 }));
    expect(m.find('[data-test=open-interactive-cli]').exists()).toBe(false);
    expect(m.find('[data-test=open-settings]').exists()).toBe(true);
  });
});

// The panes are tiles in a grid, two a row at 1280 px and narrower when resized; the grid sets the
// tile's height. happy-dom lays nothing out, so the pane's own desktop rules (outside the phone block) are matched
// against the mounted elements: the last rule that matches an element and sets a property wins, as in the page.
const src = readFileSync(resolve(__dirname, '../../src/renderer/src/components/MemberPane.vue'), 'utf8').replace(/\r\n/g, '\n');
const desktopRules = (() => {
  const css = (/<style scoped>([\s\S]*?)<\/style>/.exec(src)?.[1] ?? '').replace(/@media \(max-width: 759\.98px\) \{[\s\S]*?\n\}/, '')
    .replace(/@media[^{]*\{[^{}]*\{[^{}]*\}\s*\}/g, '').replace(/@keyframes[^{]*\{(?:[^{}]*\{[^{}]*\})*\s*\}/g, '').replace(/\/\*[\s\S]*?\*\//g, '');
  return [...css.matchAll(/([^{}]+)\{([^{}]*)\}/g)].map((m) => ({
    sels: m[1]!.split(',').map((s) => s.trim().replace(/:deep\(([^)]*)\)/g, '$1')).filter(Boolean),
    decls: Object.fromEntries(m[2]!.split(';').map((d) => [d.slice(0, d.indexOf(':')).trim(), d.slice(d.indexOf(':') + 1).trim()]).filter(([k]) => k)),
  }));
})();
const css = (el: Element, prop: string) => {
  let v: string | undefined;
  for (const r of desktopRules) if (r.decls[prop] !== undefined && r.sels.some((s) => { try { return el.matches(s); } catch { return false; } })) v = r.decls[prop];
  return v;
};

describe("a member's pane as a narrow tile (320 x 300)", () => {
  const tile = (over: Partial<AgentCard> = {}) => {
    const cell = document.createElement('div');
    cell.style.cssText = 'width: 320px; height: 300px; display: grid;';
    document.body.appendChild(cell);
    const w = mount(MemberPane, { props: { card: card({ state: 'running', queuedInstructions: 3, statusLine: report, lastRunState: 'completed', ...over }, { role: 'a-rather-long-role-name' }) } as any, attachTo: cell, ...stubs });
    mounted.push(w);
    return w;
  };

  it('fills its cell as a column: the title and tabs at their height, the view taking the rest', () => {
    const p = tile().get('[data-test=agent-DeveloperMarlo]').element;
    expect(css(p, 'display')).toBe('flex');
    expect(css(p, 'flex-direction')).toBe('column');
    expect(css(p, 'height')).toBe('100%');
    expect(css(p, 'min-width')).toBe('0');
    expect(css(p, 'min-height')).toBe('0');
    for (const row of ['agent-title', 'member-tabs']) expect(css(p.querySelector(`[data-test=${row}]`)!, 'flex'), row).toBe('none');
  });

  it("the title bar's chips shrink and ellipsize, on one line, and the ☰ never shrinks", () => {
    const title = tile().get('[data-test=agent-title]').element;
    expect(css(title, 'white-space')).toBe('nowrap');
    expect(css(title, 'min-width')).toBe('0');
    const items = ['[data-test=agent-name]', '.chip', '[data-test=agent-cli]', '[data-test=agent-face-state]', '[data-test=agent-more-waiting]'];
    for (const sel of items) {
      const el = title.querySelector(sel)!;
      expect(el, sel).not.toBeNull();
      expect(css(el, 'text-overflow'), sel).toBe('ellipsis');
      expect(css(el, 'overflow'), sel).toBe('hidden');
      expect(css(el, 'white-space'), sel).toBe('nowrap');
      expect(css(el, 'min-width'), sel).toBe('0');
      expect(css(el, 'flex'), sel).toBe('0 1 auto');
    }
    expect(css(title.querySelector('.menu-wrap')!, 'flex')).toBe('none');
    expect(css(title.querySelector('.avatar')!, 'flex')).toBe('none');
    // The ☰ menu drops out of the title bar: nothing there clips it.
    expect(css(title, 'overflow')).toBeUndefined();
  });

  it('the tab row stays one line: the tabs keep their size, the details ellipsize', () => {
    const tabs = tile().get('[data-test=member-tabs]').element;
    expect(css(tabs, 'flex-wrap')).toBe('nowrap');
    expect(css(tabs, 'white-space')).toBe('nowrap');
    expect(css(tabs.querySelector('[data-test=member-view-toggle]')!, 'flex')).toBe('none');
    const state = tabs.querySelector('[data-test=agent-state]')!;
    expect(css(state, 'min-width')).toBe('0');
    expect(css(state, 'text-overflow')).toBe('ellipsis');
  });

  it('the view scrolls inside the tile instead of growing the pane', async () => {
    const w = tile();
    const body = w.get('[data-test=member-pane-body]').element;
    expect(css(body, 'flex')).toBe('1 1 auto');
    expect(css(body, 'min-height')).toBe('0');
    expect(css(body, 'overflow')).toBe('auto');
    expect(css(w.get('[data-test=agent-output]').element, 'overflow-y')).toBe('auto');
    await w.get('[data-test=member-view-activity]').trigger('click');
    expect(css(w.get('.member-activity').element, 'min-height')).toBe('0');
  });

  it('on a phone it is not a tile: a block as tall as its rows, the view unscrolled (the page scrolls)', () => {
    const phone = /@media \(max-width: 759\.98px\) \{([\s\S]*?)\n\}/.exec(src)?.[1] ?? '';
    expect(phone).toMatch(/\n {2}\.pane \{ [^}]*display: block; height: auto; \}/);
    expect(phone).toMatch(/\n {2}\.pane > \.body \{ overflow: visible; \}/);
    expect(phone).toMatch(/\n {2}\.tabs \{ flex-wrap: wrap;/);
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
