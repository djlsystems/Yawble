// @vitest-environment happy-dom
// The phone layout (below 760 px wide) of the shell, the drawer, sheets, the Concierge, Needs You and Settings;
// at 760 px and wider nothing changes. The width comes from a fake matchMedia and the on-screen keyboard from a fake
// visualViewport; the API is a scripted fake (no core, no network).
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils';
import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { defineComponent, h, nextTick } from 'vue';
import App from '../../src/renderer/src/App.vue';
import Modal from '../../src/renderer/src/components/Modal.vue';
import Composer from '../../src/renderer/src/components/Composer.vue';
import ConciergeView from '../../src/renderer/src/components/ConciergeView.vue';
import NetworkAskCard from '../../src/renderer/src/components/NetworkAskCard.vue';
import ToolAskCard from '../../src/renderer/src/components/ToolAskCard.vue';
import TaskAskCard from '../../src/renderer/src/components/TaskAskCard.vue';
import CompanionAsks from '../../src/renderer/src/components/CompanionAsks.vue';
import SettingsView from '../../src/renderer/src/components/SettingsView.vue';
import TeamsList from '../../src/renderer/src/components/TeamsList.vue';
import Sidebar from '../../src/renderer/src/components/Sidebar.vue';
import { applyRemoteAccess } from '../../src/renderer/src/remoteAccess';
import { PHONE_BREAKPOINT, PHONE_QUERY, installTapTitles, keyboardInset, tapTitleTarget, usePhone } from '../../src/renderer/src/phone';
import type { CoreEvent, NetworkAsk, ToolAsk, YawbleApi } from '../../src/shared/api';

// ---- the fakes: a window width matchMedia answers for, and a visual viewport the keyboard shrinks
type Listener = (e: { matches: boolean }) => void;
const screen = { width: 1280, listeners: new Map<string, Set<Listener>>() };
const isPhoneWidth = () => screen.width < PHONE_BREAKPOINT;
function fakeMatchMedia(query: string) {
  const answers = () => (query === PHONE_QUERY ? isPhoneWidth() : false);
  const set = screen.listeners.get(query) ?? new Set<Listener>();
  screen.listeners.set(query, set);
  return {
    get matches() { return answers(); },
    media: query,
    addEventListener: (_: string, l: Listener) => { set.add(l); },
    removeEventListener: (_: string, l: Listener) => { set.delete(l); },
  };
}
/** Sets the window's width; the phone query's listeners hear when the answer changes. */
function setWidth(width: number) {
  const before = isPhoneWidth();
  screen.width = width;
  if (before !== isPhoneWidth()) for (const l of screen.listeners.get(PHONE_QUERY) ?? []) l({ matches: isPhoneWidth() });
}

class FakeVisualViewport extends EventTarget {
  height = 800;
  offsetTop = 0;
  /** The keyboard comes up (covering `px` of the bottom) or goes away (0). */
  keyboard(px: number) { this.height = 800 - px; this.dispatchEvent(new Event('resize')); }
}
let vv: FakeVisualViewport;

beforeEach(() => {
  screen.listeners.clear();
  setWidth(1280);
  Object.defineProperty(window, 'matchMedia', { configurable: true, writable: true, value: fakeMatchMedia });
  vv = new FakeVisualViewport();
  Object.defineProperty(window, 'visualViewport', { configurable: true, writable: true, value: vv });
  Object.defineProperty(window, 'innerHeight', { configurable: true, writable: true, value: 800 });
  try { localStorage.clear(); sessionStorage.clear(); } catch { /* none */ }
  applyRemoteAccess('full');
});

let wrappers: VueWrapper[] = [];
afterEach(() => { for (const w of wrappers) w.unmount(); wrappers = []; document.body.innerHTML = ''; });
function keep<T extends VueWrapper>(w: T): T { wrappers.push(w); return w; }

function fakeApi(answers: Record<string, (params: any) => unknown> = {}, extra: Partial<YawbleApi> = {}) {
  const calls: { method: string; params: any }[] = [];
  const listeners = new Set<(e: CoreEvent) => void>();
  const api: YawbleApi = {
    getInfo: async () => { throw new Error('no'); },
    getAvailableBackends: async () => [],
    call: async (method: string, params?: Record<string, unknown>) => {
      calls.push({ method, params });
      const answer = answers[method];
      if (!answer) throw new Error(`unexpected ${method}`);
      return answer(params) as any;
    },
    onEvent: (l) => { listeners.add(l); return () => listeners.delete(l); },
    openTerminal: async () => {},
    pickFolder: async () => null,
    ...extra,
  };
  return { api, calls };
}

const core = {
  'app.overview': () => ({ slots: { running: 0, limit: 4, queued: 0 }, teams: [{ id: 1, name: 'payments', repoPath: null, members: 2, running: 0, queued: 0 }] }),
  'link.status': () => ({ state: 'off', viewers: [] }),
  'agents.detect': () => [{ cliId: 'claude', found: true, signIn: { status: 'signed-in', detail: null } }],
  'concierge.list': () => [{ id: 'c1', title: 'Payments today', cli: 'claude', teamId: null, team: null, createdAt: '', updatedAt: '', running: false, unread: 0, watching: 0 }],
  'network.asks': () => [],
  'tools.asks': () => [],
};

async function mountApp(extra: Partial<YawbleApi> = {}) {
  const f = fakeApi(core, extra);
  const w = keep(mount(App, { props: { api: f.api, pollMs: 60_000 }, attachTo: document.body }));
  await flushPromises();
  await flushPromises();
  return { w, calls: f.calls };
}
const drawerState = (w: VueWrapper) => w.get('[data-test=sidebar]').attributes('data-drawer');

describe('usePhone and the keyboard inset (phone.ts)', () => {
  it('is a phone below 760 px only, and follows the width as it changes', async () => {
    expect(PHONE_QUERY).toBe('(max-width: 759.98px)');
    const Probe = defineComponent({ setup() { const phone = usePhone(); return () => h('i', phone.value ? 'phone' : 'desktop'); } });
    setWidth(759);
    const w = keep(mount(Probe));
    expect(w.text()).toBe('phone');
    setWidth(760);
    await nextTick();
    expect(w.text()).toBe('desktop'); // 760 px is the desktop layout
    setWidth(390);
    await nextTick();
    expect(w.text()).toBe('phone');
  });

  it('measures what the keyboard covers from the visual viewport, never below 0', () => {
    expect(keyboardInset({ innerHeight: 800, visualViewport: { height: 800, offsetTop: 0 } })).toBe(0);
    expect(keyboardInset({ innerHeight: 800, visualViewport: { height: 460, offsetTop: 0 } })).toBe(340);
    expect(keyboardInset({ innerHeight: 800, visualViewport: { height: 460, offsetTop: 40 } })).toBe(300); // the page scrolled up by 40
    expect(keyboardInset({ innerHeight: 800, visualViewport: { height: 900, offsetTop: 0 } })).toBe(0);
    expect(keyboardInset({ innerHeight: 800, visualViewport: null })).toBe(0);
  });
});

describe('the shell on a phone: the drawer', () => {
  it('shows a slim top bar with a menu button and the page title; the sidebar is a closed, inert drawer, never the rail', async () => {
    setWidth(390);
    sessionStorage.clear();
    localStorage.setItem('yawble.place', JSON.stringify({ collapsed: true })); // folded on the desktop: no rail on a phone
    const { w } = await mountApp();
    expect(w.get('.app').classes()).toContain('phone');
    expect(w.get('.app').classes()).not.toContain('collapsed');
    expect(w.find('[data-test=sidebar-rail]').exists()).toBe(false);
    expect(drawerState(w)).toBe('closed');
    expect(w.get('[data-test=sidebar]').attributes('inert')).toBeDefined();
    expect(w.get('[data-test=sidebar]').attributes('aria-hidden')).toBe('true');
    expect(w.get('[data-test=phone-title]').text()).toBe('New chat');
    const toggle = w.get('[data-test=drawer-toggle]');
    expect(toggle.attributes('aria-expanded')).toBe('false');
    expect(toggle.attributes('aria-controls')).toBe('app-drawer');
    expect(w.get('#app-drawer').attributes('data-test')).toBe('sidebar');
    // The desktop window's menu bar stays (only a remote view moves it into the drawer).
    expect(w.find('[data-test=menubar]').exists()).toBe(true);
  });

  it('opens from the menu button and closes on the scrim, on Escape, on its own close button and on going anywhere', async () => {
    setWidth(390);
    const { w } = await mountApp();
    const open = async () => { await w.get('[data-test=drawer-toggle]').trigger('click'); expect(drawerState(w)).toBe('open'); };

    await open();
    expect(w.get('[data-test=sidebar]').attributes('inert')).toBeUndefined();
    expect(w.get('[data-test=drawer-toggle]').attributes('aria-expanded')).toBe('true');
    // The Concierge's chat list is in the drawer.
    expect(w.findAll('[data-test=chat-item]')).toHaveLength(1);
    await w.get('[data-test=drawer-scrim]').trigger('click');
    expect(drawerState(w)).toBe('closed');
    expect(w.find('[data-test=drawer-scrim]').exists()).toBe(false);

    await open();
    window.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' }));
    await nextTick();
    expect(drawerState(w)).toBe('closed');

    await open();
    await w.get('[data-test=sidebar-collapse]').trigger('click'); // its close button, not the desktop's fold to a rail
    expect(drawerState(w)).toBe('closed');
    expect(w.get('.app').classes()).not.toContain('collapsed');

    await open();
    await w.get('[data-test=nav-teams]').trigger('click');
    await flushPromises();
    expect(drawerState(w)).toBe('closed');
    expect(w.get('[data-test=phone-title]').text()).toBe('Teams');

    await open();
    await w.get('[data-test=chat-item] .open').trigger('click');
    await flushPromises();
    expect(drawerState(w)).toBe('closed');
  });

  it('closes the drawer when the window grows to the desktop width, and the desktop layout comes back unchanged', async () => {
    setWidth(390);
    const { w } = await mountApp();
    await w.get('[data-test=drawer-toggle]').trigger('click');
    setWidth(1024);
    await nextTick();
    expect(w.get('.app').classes()).not.toContain('phone');
    expect(w.find('[data-test=drawer-toggle]').exists()).toBe(false);
    expect(w.get('[data-test=sidebar]').attributes('data-drawer')).toBeUndefined();
    expect(w.get('[data-test=sidebar]').classes()).not.toContain('drawer');
  });

  it('in a remote view: no menu bar; the theme, the terminal and Help are at the drawer\'s bottom; the Visitor chip stays', async () => {
    setWidth(390);
    const { w } = await mountApp({ remote: true });
    applyRemoteAccess('read');
    await nextTick();
    expect(w.find('[data-test=menubar]').exists()).toBe(false);
    await w.get('[data-test=drawer-toggle]').trigger('click');
    // Folded at first (F2): one row, Theme, terminal and help; a tap opens it.
    const more = w.get('[data-test=drawer-more]');
    expect(more.attributes('aria-expanded')).toBe('false');
    expect((w.get('[data-test=drawer-menu-items]').element as HTMLElement).style.display).toBe('none');
    await more.trigger('click');
    expect(more.attributes('aria-expanded')).toBe('true');
    expect((w.get('[data-test=drawer-menu-items]').element as HTMLElement).style.display).toBe('');
    const items = w.findAll('[data-test=drawer-menu-items] button').map((b) => b.attributes('data-test'));
    // No Terminal for a Visitor (the core refuses every terminal call to a read-only viewer).
    expect(items).toEqual(['drawer-theme:system', 'drawer-theme:light', 'drawer-theme:dark', 'drawer-checkUpdates', 'drawer-about', 'drawer-go:settings-agents']);
    expect(w.get('[data-test="drawer-theme:system"]').attributes('aria-checked')).toBeDefined();
    // The read-only chip is on the page, outside the drawer, so it shows whether the drawer is open or not.
    const chip = document.querySelector('[data-test=remote-access-chip]');
    expect(chip?.textContent).toBe('Read-only (Visitor)');
    expect(w.get('[data-test=sidebar]').element.contains(chip)).toBe(false);
    // A Help item: the drawer closes and the place opens.
    await w.get('[data-test=drawer-about]').trigger('click');
    await flushPromises();
    expect(drawerState(w)).toBe('closed');
    expect(w.find('[data-test=settings]').exists()).toBe(true);
    expect(w.get('[data-test=settings]').attributes('data-phone-screen')).toBe('section');
    expect(w.get('[data-test=settings-section-title]').text()).toBe('System');
  });

  it('on the desktop (760 px and wider): the menu bar, the sidebar in the grid, no menu button, no drawer menu', async () => {
    setWidth(760);
    const { w } = await mountApp({ remote: true });
    expect(w.get('.app').classes()).not.toContain('phone');
    expect(w.find('[data-test=menubar]').exists()).toBe(true);
    expect(w.find('[data-test=drawer-toggle]').exists()).toBe(false);
    expect(w.find('[data-test=phone-title]').exists()).toBe(false);
    expect(w.find('[data-test=drawer-menu]').exists()).toBe(false);
    expect(w.get('[data-test=sidebar]').attributes('inert')).toBeUndefined();
    await w.get('[data-test=sidebar-collapse]').trigger('click');
    expect(w.get('.app').classes()).toContain('collapsed'); // the desktop's fold to a rail, as before
    expect(w.find('[data-test=sidebar-rail]').exists()).toBe(true);
  });
});

describe('the drawer (Sidebar) on a phone', () => {
  it('shows a chat\'s Rename and Delete without hover, as tap-sized rows', () => {
    setWidth(390);
    const props = { view: 'concierge' as const, conversations: [core['concierge.list']()[0] as any], teams: [], conversationId: null, teamId: null, slots: null, remote: false };
    const w = keep(mount(Sidebar, { props: { ...props, drawer: true, open: true } }));
    expect(w.get('[data-test=sidebar]').classes()).toEqual(expect.arrayContaining(['drawer', 'open']));
    expect(w.find('[data-test=chat-rename]').exists()).toBe(true);
    const css = readFileSync(resolve(__dirname, '../../src/renderer/src/components/Sidebar.vue'), 'utf8');
    expect(css).toMatch(/\.drawer \.chat \.actions \{ display: flex; \}/);
    expect(css).toMatch(/\.drawer \.item, \.drawer \.chat \.open \{ min-height: 44px; \}/);
  });
});

describe('dialogs on a phone: a full-screen sheet', () => {
  const host = (title: string) => defineComponent({ setup: () => () => h(Modal, { title, wide: true }, () => h('p', 'body')) });

  it('is a sheet below 760 px', () => {
    setWidth(390);
    const w = keep(mount(host('New team'), { attachTo: document.body }));
    expect(w.get('.modal').classes()).toContain('sheet');
    expect(w.get('.backdrop').classes()).toContain('sheet');
    expect(w.get('.modal').attributes('data-sheet')).toBe('true');
    expect(w.get('[data-test=modal-close]').attributes('aria-label')).toBe('Close');
  });

  it('is the centred dialog at 760 px and wider, as before', () => {
    setWidth(760);
    const w = keep(mount(host('New team'), { attachTo: document.body }));
    expect(w.get('.modal').classes()).not.toContain('sheet');
    expect(w.get('.backdrop').classes()).not.toContain('sheet');
    expect(w.get('.modal').classes()).toContain('wide');
  });
});

describe('the Concierge on a phone', () => {
  const props = { teams: [], agents: [{ id: 'claude', label: 'Claude Code', ready: true }], cli: 'claude', teamId: null, running: false };

  it('the composer is full width; Enter is a new line and Send sends; no autofocus (no keyboard on arrival)', async () => {
    setWidth(390);
    const w = keep(mount(Composer, { props: { ...props, autofocus: true }, attachTo: document.body }));
    expect(w.get('[data-test=composer]').classes()).toContain('phone');
    expect(document.activeElement).not.toBe(w.get('[data-test=composer-text]').element);
    await w.get('[data-test=composer-text]').setValue('Hello');
    await w.get('[data-test=composer-text]').trigger('keydown', { key: 'Enter' });
    expect(w.emitted('send')).toBeUndefined();
    await w.get('[data-test=composer-send]').trigger('click');
    expect(w.emitted('send')?.[0]?.[0]).toBe('Hello');
  });

  it('on the desktop Enter still sends and the box takes the focus', async () => {
    setWidth(1280);
    const w = keep(mount(Composer, { props: { ...props, autofocus: true }, attachTo: document.body }));
    expect(w.get('[data-test=composer]').classes()).not.toContain('phone');
    expect(document.activeElement).toBe(w.get('[data-test=composer-text]').element);
    await w.get('[data-test=composer-text]').setValue('Hello');
    await w.get('[data-test=composer-text]').trigger('keydown', { key: 'Enter' });
    expect(w.emitted('send')?.[0]?.[0]).toBe('Hello');
  });

  it('keeps the composer above the on-screen keyboard: the page\'s bottom is padded by what the keyboard covers', async () => {
    setWidth(390);
    const f = fakeApi();
    const w = keep(mount(ConciergeView, { props: { conversationId: null, teams: [], agents: props.agents, defaultCli: 'claude', focusTeamId: null, api: f.api } }));
    await flushPromises();
    const page = () => w.get('[data-test=concierge]');
    expect(page().classes()).toContain('phone');
    expect(page().attributes('style')).toContain('--keyboard: 0px');
    vv.keyboard(320);
    await nextTick();
    expect(page().attributes('style')).toContain('--keyboard: 320px');
    expect(page().classes()).toContain('keyboard');
    vv.keyboard(0);
    await nextTick();
    expect(page().attributes('style')).toContain('--keyboard: 0px');
    expect(page().classes()).not.toContain('keyboard');
    const css = readFileSync(resolve(__dirname, '../../src/renderer/src/components/ConciergeView.vue'), 'utf8');
    expect(css).toContain('.concierge.phone { padding-bottom: var(--keyboard, 0px); }');
  });

  it('on the desktop the page has no keyboard padding and no phone class', async () => {
    setWidth(1280);
    const f = fakeApi();
    const w = keep(mount(ConciergeView, { props: { conversationId: null, teams: [], agents: props.agents, defaultCli: 'claude', focusTeamId: null, api: f.api } }));
    await flushPromises();
    vv.keyboard(320);
    await nextTick();
    expect(w.get('[data-test=concierge]').classes()).not.toContain('phone');
    expect(w.get('[data-test=concierge]').attributes('style')).toBeUndefined();
  });
});

describe('Needs You on a phone: full-width cards, buttons stacked', () => {
  const networkAsk: NetworkAsk = {
    id: 7, at: '2026-11-13T21:00:00Z', teamId: 1, team: 'Alpha', member: 'Alpha/DeveloperInes', memberName: 'DeveloperInes',
    host: 'api.nuget.org', reason: null, method: 'CONNECT', pathSample: null, count: 3, lastSeen: '2026-11-13T21:05:00Z',
    hint: null, workflow: 12, state: 'pending', scope: null, decidedBy: null, decidedAt: null, plainHttp: false, choices: ['agent', 'team', 'everywhere', 'deny'],
  } as NetworkAsk;
  const toolAsk: ToolAsk = {
    id: 4, at: '2026-11-13T21:00:00Z', teamId: 1, team: 'Alpha', member: 'Alpha/DeveloperInes', memberName: 'DeveloperInes',
    tool: 'dotnet-sdk-10', displayName: '.NET SDK 10', publisher: 'Microsoft', wingetId: 'Microsoft.DotNet.SDK.10', scope: 'machine',
    reason: 'build', count: 2, lastSeen: '2026-11-13T21:05:00Z', workflow: 12, state: 'pending', attempts: 0, error: null,
    decidedBy: null, decidedVia: null, decidedAt: null, canInstall: true, command: 'winget install', choices: ['install', 'notNow'],
  } as ToolAsk;
  const taskAsk = { member: 'Alpha/DeveloperInes', taskName: 'deploy', commit: 'abcdef1234', reason: 'ship it', detail: null } as any;
  const later = new Date(Date.now() + 5 * 60_000).toISOString();

  for (const width of [390, 1280]) {
    const phone = width < 760;
    it(`${phone ? 'a phone' : 'the desktop'} (${width} px): ${phone ? 'stacked' : 'unchanged'}`, async () => {
      setWidth(width);
      const f = fakeApi({ 'companion.list': () => ({ clients: [], pairings: [{ id: 'p1', name: 'claude-code', level: 'act', code: 'K7Q2ZD', expires: later }], actions: [] }) });
      const net = keep(mount(NetworkAskCard, { props: { ask: networkAsk, api: f.api } }));
      const tool = keep(mount(ToolAskCard, { props: { ask: toolAsk, api: f.api } }));
      const task = keep(mount(TaskAskCard, { props: { ask: taskAsk } }));
      const comp = keep(mount(CompanionAsks, { props: { api: f.api, pollMs: 60_000 } }));
      await flushPromises();
      const has = (w: VueWrapper, sel: string) => w.get(sel).classes().includes('phone');
      expect(has(net, '[data-test=network-ask]')).toBe(phone);
      expect(has(tool, '[data-test=tool-ask]')).toBe(phone);
      expect(has(task, '[data-test=task-ask]')).toBe(phone);
      expect(has(comp, '[data-test=companion-asks]')).toBe(phone);
      expect(net.findAll('.buttons button').length).toBeGreaterThan(1);
      expect(comp.find('[data-test=companion-approve]').exists()).toBe(true);
    });
  }

  it('the phone rules stack the buttons and take the full width', () => {
    for (const file of ['NetworkAskCard', 'ToolAskCard', 'TaskAskCard', 'CompanionAsks']) {
      const css = readFileSync(resolve(__dirname, `../../src/renderer/src/components/${file}.vue`), 'utf8');
      expect(css, file).toMatch(/\.phone \.buttons \{ flex-direction: column; align-items: stretch;/);
      expect(css, file).toMatch(/\.phone[^{]*\{[^}]*width: 100%/);
    }
  });
});

describe('Settings on a phone: one section per screen', () => {
  const answers = { 'agents.detect': () => [], 'agents.list': () => [] };

  it('opens at the list of sections; a tap opens one section under a Back to the list', async () => {
    setWidth(390);
    const f = fakeApi(answers);
    const w = keep(mount(SettingsView, { props: { api: f.api, tab: 'general', teams: [], landing: true } }));
    await flushPromises();
    expect(w.get('[data-test=settings]').attributes('data-phone-screen')).toBe('list');
    expect(w.find('[data-test=settings-pane]').exists()).toBe(false);
    expect(w.get('.tabs').classes()).toContain('list');
    expect(w.findAll('.tabs.list button').length).toBeGreaterThan(5);

    await w.get('[data-test=open-agents]').trigger('click');
    expect(w.emitted('tab')?.[0]).toEqual(['agents']);
    await w.setProps({ tab: 'agents' });
    await flushPromises();
    expect(w.get('[data-test=settings]').attributes('data-phone-screen')).toBe('section');
    expect(w.find('.tabs').exists()).toBe(false);
    expect(w.find('[data-test=settings-pane]').exists()).toBe(true);
    expect(w.get('[data-test=settings-section-title]').text()).toBe('Agents');

    await w.get('[data-test=settings-back]').trigger('click');
    expect(w.get('[data-test=settings]').attributes('data-phone-screen')).toBe('list');
    expect(w.find('[data-test=settings-pane]').exists()).toBe(false);

    // The section already chosen: a tap on it opens it again (no tab change to wait for).
    await w.get('[data-test=open-agents]').trigger('click');
    expect(w.get('[data-test=settings]').attributes('data-phone-screen')).toBe('section');
  });

  it('a link to one section opens it at once', async () => {
    setWidth(390);
    const f = fakeApi(answers);
    const w = keep(mount(SettingsView, { props: { api: f.api, tab: 'agents', teams: [], landing: false } }));
    await flushPromises();
    expect(w.get('[data-test=settings]').attributes('data-phone-screen')).toBe('section');
    expect(w.get('[data-test=settings-section-title]').text()).toBe('Agents');
  });

  it('on the desktop: the tabs and the pane together, as before', async () => {
    setWidth(1280);
    const f = fakeApi(answers);
    const w = keep(mount(SettingsView, { props: { api: f.api, tab: 'general', teams: [], landing: true } }));
    await flushPromises();
    expect(w.get('[data-test=settings]').attributes('data-phone-screen')).toBeUndefined();
    expect(w.get('.tabs').classes()).not.toContain('list');
    expect(w.find('[data-test=settings-pane]').exists()).toBe(true);
    expect(w.find('[data-test=settings-back]').exists()).toBe(false);
    expect(w.get('[data-test=open-general]').classes()).toContain('on');
  });

  it('App: Settings from the gear in the top bar is the list; File > Settings too; a fresh list each time it is opened', async () => {
    setWidth(390);
    const { w } = await mountApp();
    await w.get('[data-testid=settings-gear]').trigger('click');
    await flushPromises();
    expect(w.get('[data-test=settings]').attributes('data-phone-screen')).toBe('list');
    await w.get('[data-test=open-system]').trigger('click');
    await flushPromises();
    expect(w.get('[data-test=settings]').attributes('data-phone-screen')).toBe('section');
    await w.get('[data-testid=settings-gear]').trigger('click');
    await flushPromises();
    expect(w.get('[data-test=settings]').attributes('data-phone-screen')).toBe('list');
  });
});

describe('the Teams list on a phone', () => {
  const team = { id: 1, name: 'payments', repoPath: null, members: 2, running: 0, queued: 0, paused: false } as any;
  it('one column and no size grips below 760 px; the grips stay on the desktop', async () => {
    const f = fakeApi({ 'workflows.list': () => [] , 'teams.workflows': () => [] });
    setWidth(390);
    const phone = keep(mount(TeamsList, { props: { teams: [team], archived: [], api: f.api, pollMs: 60_000, refresh: () => {} } }));
    await flushPromises();
    expect(phone.get('.teams-list').classes()).toContain('phone');
    expect(phone.findAll('.tile-grip')).toHaveLength(0);
    setWidth(1280);
    const desk = keep(mount(TeamsList, { props: { teams: [team], archived: [], api: f.api, pollMs: 60_000, refresh: () => {} } }));
    await flushPromises();
    expect(desk.get('.teams-list').classes()).not.toContain('phone');
    expect(desk.findAll('.tile-grip').length).toBeGreaterThan(0);
  });
});

describe('tooltips by tap (no hover on a phone)', () => {
  it('a tap on something with a title that is not a control shows the title; the next tap hides it', () => {
    let phone = true;
    document.body.innerHTML = '<span id="dot" title="2 agents running">•</span><button id="btn" title="Delete">x</button><p id="plain">text</p>';
    const off = installTapTitles(() => phone);
    const tip = () => document.querySelector('[data-test=tap-tip]');
    document.getElementById('dot')!.click();
    expect(tip()?.textContent).toBe('2 agents running');
    expect(tip()?.getAttribute('role')).toBe('tooltip');
    document.getElementById('plain')!.click();
    expect(tip()).toBeNull();
    document.getElementById('dot')!.click();
    document.getElementById('dot')!.click(); // a second tap on it: hidden
    expect(tip()).toBeNull();
    document.getElementById('btn')!.click(); // a control keeps its tap for what it does
    expect(tip()).toBeNull();
    phone = false;
    document.getElementById('dot')!.click();
    expect(tip()).toBeNull(); // the desktop has hover
    off();
    expect(tapTitleTarget(document.getElementById('btn'))).toBeNull();
    expect(tapTitleTarget(document.getElementById('dot'))?.id).toBe('dot');
  });
});

// The faults F1 to F4 the phone e2e run found. happy-dom has no layout, so the sizes are the CSS rules.
describe('phone e2e faults F1 to F4', () => {
  const src = (file: string) => readFileSync(resolve(__dirname, `../../src/renderer/src/${file}`), 'utf8');
  const asks = {
    ...core,
    'network.asks': () => [{ id: 7, at: '2026-11-13T21:00:00Z', teamId: 1, team: 'payments', member: 'payments/DeveloperInes', memberName: 'DeveloperInes',
      host: 'api.nuget.org', reason: null, method: 'CONNECT', pathSample: null, count: 1, lastSeen: '2026-11-13T21:00:00Z', hint: null, workflow: 1,
      state: 'pending', scope: null, decidedBy: null, decidedAt: null, plainHttp: false, choices: ['agent', 'deny'] }],
    'tools.asks': () => [{ id: 4, at: '2026-11-13T21:00:00Z', teamId: 1, team: 'payments', member: 'payments/DeveloperInes', memberName: 'DeveloperInes',
      tool: 'dotnet-sdk-10', displayName: '.NET SDK 10', publisher: 'Microsoft', wingetId: 'Microsoft.DotNet.SDK.10', scope: 'machine', reason: 'build',
      count: 1, lastSeen: '2026-11-13T21:00:00Z', workflow: 1, state: 'pending', attempts: 0, error: null, decidedBy: null, decidedVia: null, decidedAt: null,
      canInstall: false, command: null, choices: ['notNow'] }],
  };
  async function mountWithAsks(width: number) {
    setWidth(width);
    const f = fakeApi(asks, { remote: true });
    const w = keep(mount(App, { props: { api: f.api, pollMs: 60_000 }, attachTo: document.body }));
    await flushPromises();
    await flushPromises();
    return w;
  }

  it('F1: on a phone every banner is in one box above the view; the view and the composer stay outside it', async () => {
    const w = await mountWithAsks(390);
    const box = w.get('[data-test=banners]');
    expect(box.find('[data-test=network-asks]').exists()).toBe(true);
    expect(box.find('[data-test=tool-asks]').exists()).toBe(true);
    expect(box.find('[data-test=concierge]').exists()).toBe(false);
    expect(w.find('.content [data-test=composer]').exists()).toBe(true);
    // The box is a sibling of .content in .main: the banners never sit inside the view's share.
    expect(box.element.parentElement?.classList.contains('main')).toBe(true);
    expect(box.element.nextElementSibling?.classList.contains('content')).toBe(true);
  });

  it('F1: the box holds them all in at most 40% of a phone\'s height and scrolls; no banner has its own half-screen cap', () => {
    const app = src('App.vue');
    // 50dvh after its 50vh fallback: half of what the browser really shows.
    expect(app).toContain('.banners { display: block; flex: none; max-height: 50vh; max-height: 50dvh; overflow-y: auto;');
    expect(app).toContain('.phone .banners { max-height: 40dvh; }');
    expect(app).toMatch(/\.content \{ flex: 1; min-height: 0;/); // the view takes the rest
    for (const file of ['components/NetworkAsks.vue', 'components/ToolAsks.vue']) {
      expect(src(file), file).not.toMatch(/max-height: 50dvh/);
      expect(src(file), file).toContain('.banner.phone { padding: 8px 8px; }');
    }
  });

  it('at 760 px and wider the same box holds the banners in at most half the window and scrolls', async () => {
    expect(src('App.vue')).not.toContain('.banners { display: contents; }');
    const w = await mountWithAsks(760);
    expect(w.get('.app').classes()).not.toContain('phone');
    expect(w.get('[data-test=banners] [data-test=network-asks]').exists()).toBe(true);
    expect(w.get('[data-test=banners] [data-test=tool-asks]').exists()).toBe(true);
  });

  const sidebarProps = {
    view: 'concierge' as const, conversations: [core['concierge.list']()[0] as any], teams: [{ id: 1, name: 'payments', running: 0, queued: 0 } as any],
    conversationId: null, teamId: null, slots: { running: 0, limit: 4, queued: 0 }, remote: true,
    menu: [{ id: 'terminal', label: 'Terminal' }, { id: 'theme:dark', label: 'Dark', radio: true, checked: true }, { id: 'about', label: 'About' }] as any,
  };

  it('F2: the drawer\'s menu is one folded row; a tap shows its items and a second tap folds it again', async () => {
    setWidth(390);
    const w = keep(mount(Sidebar, { props: { ...sidebarProps, drawer: true, open: true } }));
    const items = () => w.get('[data-test=drawer-menu-items]').element as HTMLElement;
    expect(w.get('[data-test=drawer-more]').text()).toContain('Theme, terminal and help');
    expect(items().style.display).toBe('none');
    await w.get('[data-test=drawer-more]').trigger('click');
    expect(items().style.display).toBe('');
    expect(w.findAll('[data-test=drawer-menu-items] button').map((b) => b.attributes('data-test'))).toEqual(['drawer-terminal', 'drawer-theme:dark', 'drawer-about']);
    await w.get('[data-test=drawer-about]').trigger('click');
    expect(w.emitted('command')?.[0]).toEqual(['about']);
    await w.get('[data-test=drawer-more]').trigger('click');
    expect(items().style.display).toBe('none');
    // The run-slots line is in the drawer, after the lists; Settings is not (it is the gear in the top bar).
    expect(w.find('[data-test=open-app-settings]').exists()).toBe(false);
    expect(w.get('[data-test=slots]').text()).toContain('Run slots');
  });

  it('the drawer scrolls as one list: the Teams and Chats lists are not a box of their own, and the footer follows them', () => {
    const css = src('components/Sidebar.vue');
    expect(css).toContain('.side.drawer { overflow-y: auto;');
    expect(css).toContain('.drawer .brand, .drawer .item.new, .drawer .places, .drawer .foot, .drawer .lists { flex: none; }');
    expect(css).toContain('.drawer .lists { overflow: visible; }');
    // The desktop sidebar keeps its rules: the lists flex and scroll. Only a window shorter than its fixed rows
    // (a phone in landscape at a desktop width) scrolls the sidebar itself, the lists then kept at least 96 px.
    expect(css).toContain('.lists { flex: 1; overflow: auto; min-height: min(96px, 100%); }');
    expect(css).toMatch(/\.side \{ [^}]*min-height: 0; overflow-y: auto; padding: 8px 8px 6px; \}/);
  });

  it('F3 and F4: on a phone Attach is a 44 x 44 square and the text box is at least 44 px tall; the desktop rules are unchanged', () => {
    const css = src('components/Composer.vue');
    expect(css).toContain('.phone .pick.attach { min-width: 44px; width: 44px; height: 44px; padding: 0; flex: none;');
    expect(css).toMatch(/\.phone textarea \{[^}]*min-height: 44px; box-sizing: border-box;/);
    // The desktop's: unchanged.
    expect(css).toContain('textarea { width: 100%; border: 0; outline: 0; resize: none; background: transparent; font-size: 15px; line-height: 1.5; padding: 4px 0; max-height: 240px;');
    expect(css).toContain('.attach { border: 0; background: transparent; cursor: pointer; }');
  });

  it('F3: the Attach button carries both classes, in a phone composer and for a Visitor', async () => {
    setWidth(390);
    applyRemoteAccess('read');
    const w = keep(mount(Composer, { props: { teams: [], agents: [], cli: 'claude', teamId: null, running: false } }));
    expect(w.get('[data-test=composer]').classes()).toContain('phone');
    expect(w.get('[data-test=composer-attach]').classes()).toEqual(expect.arrayContaining(['pick', 'attach']));
  });

  it('F7: the Visitor chip\'s 150 px room is kept in the top bar only, never in the Composer\'s bar', async () => {
    const css = readFileSync(resolve(__dirname, '../../src/renderer/src/styles.css'), 'utf8');
    const rule = /(:root\[data-remote-access="read"\][^{]*)\{ padding-right: calc\(150px/.exec(css);
    expect(rule).not.toBeNull();
    const selector = rule![1].trim();
    expect(css).not.toContain('.app.phone .bar {'); // the bare .bar matched every bar under the shell
    setWidth(390);
    const { w } = await mountApp({ remote: true });
    applyRemoteAccess('read');
    await nextTick();
    const top = w.get('.main > header.bar').element;
    const bar = w.get('[data-test=composer] .bar').element;
    expect(top.matches(selector)).toBe(true);
    expect(bar.matches(selector)).toBe(false);
  });

  it('F8: a sheet\'s header keeps the Visitor chip\'s 150 px room too; no bar in its body, nothing on the desktop', async () => {
    const css = readFileSync(resolve(__dirname, '../../src/renderer/src/styles.css'), 'utf8');
    const at = css.indexOf('@media (max-width: 759.98px) {');
    const m = /(:root\[data-remote-access="read"\][^{]*\.modal\.sheet[^{]*)\{ padding-right: calc\(150px/.exec(css);
    expect(m).not.toBeNull();
    expect(m!.index).toBeGreaterThan(at); // inside the phone block only
    const selector = m![1].trim();
    const host = defineComponent({ setup: () => () => h(Modal, { title: 'Logs', full: true }, () => h('header', { class: 'row bar' }, 'inner')) });
    applyRemoteAccess('read');
    setWidth(390);
    const w = keep(mount(host, { attachTo: document.body }));
    expect(w.get('.modal > header.row').element.matches(selector)).toBe(true);
    expect(w.get('.body > header').element.matches(selector)).toBe(false);
    w.unmount();
    setWidth(760);
    const d = keep(mount(host, { attachTo: document.body }));
    expect(d.get('.modal > header.row').element.matches(selector)).toBe(false); // no sheet: the dialog as before
    applyRemoteAccess('full');
    setWidth(390);
    const f = keep(mount(host, { attachTo: document.body }));
    expect(f.get('.modal > header.row').element.matches(selector)).toBe(false);
  });
});

describe('the page and the stylesheet', () => {
  it('remote.html lets a phone show the page at its own width, edge to edge', () => {
    const html = readFileSync(resolve(__dirname, '../../src/renderer/remote.html'), 'utf8');
    expect(html).toContain('<meta name="viewport" content="width=device-width, initial-scale=1, viewport-fit=cover">');
  });

  it('styles.css: below 760 px every tap target is at least 44 px, the Visitor chip moves to the top bar; nothing at 760 px and wider', () => {
    const css = readFileSync(resolve(__dirname, '../../src/renderer/src/styles.css'), 'utf8');
    const at = css.indexOf('@media (max-width: 759.98px) {');
    expect(at).toBeGreaterThan(0);
    const block = css.slice(at);
    expect(block).toMatch(/button, select, input:not\(\[type=checkbox\], \[type=radio\], \[type=hidden\]\) \{ min-height: 44px; \}/);
    expect(block).toMatch(/button \{ min-width: 44px; \}/);
    expect(block).toMatch(/\.remote-access-chip \{[^}]*top:/);
    expect(css).not.toMatch(/@media \(min-width/); // nothing keyed on the desktop width: it keeps its rules as they were
    expect(css).toContain('.tap-tip {');
  });
});
