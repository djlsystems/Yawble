// @vitest-environment happy-dom
import { enableAutoUnmount, flushPromises, mount } from '@vue/test-utils';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { nextTick, reactive } from 'vue';
import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import type { ConversationSummary, TeamSummary } from '../../src/shared/api';
import type { EmbedNav } from '../../src/shared/embedContract';
import Sidebar from '../../src/renderer/src/components/Sidebar.vue';
import {
  applyEmbedTheme, buildNav, dispatchNavigate, EMBED_PLACE_IDS, embedThemeFromQuery, HELP_SETTINGS_TAB, isEmbed, parseNavigate, parseTheme,
  placeAllowed, runSlotsLine, startEmbedBridge, type EmbedHandlers, type EmbedNavState,
} from '../../src/renderer/src/embed';
import { REMOTE_THEME_KEY } from '../../src/renderer/src/theme';
import { confirmState } from '../../src/renderer/src/confirm';

// B115 Part B (card 518): the remote view embedded in the cloud page. The sidebar as data (yawble.nav), the cloud's
// navigate and theme messages, the origin and source checks, and that the viewer's own remote theme is never used.
// Fakes only: a fake window and parent, fake handlers, the real Sidebar.vue for the "same as the click" checks.

enableAutoUnmount(afterEach);

const at = '2026-11-20T09:00:00Z';
const team = (id: number, name: string, extra: Partial<TeamSummary> = {}): TeamSummary =>
  ({ id, name, repoPath: null, members: 3, running: 0, queued: 0, ...extra });
const chat = (id: string, title: string, extra: Partial<ConversationSummary> = {}): ConversationSummary =>
  ({ id, title, cli: 'claude', teamId: null, team: null, createdAt: at, updatedAt: at, running: false, unread: 0, watching: 0, ...extra });

const base = (): EmbedNavState => ({
  view: 'concierge',
  conversations: [chat('c1', 'Plan the release'), chat('c2', 'Ask about logs')],
  teams: [team(1, 'payments', { running: 2 }), team(2, 'docs', { queued: 1 }), team(3, 'infra', { paused: true }), team(4, 'web')],
  conversationId: 'c1',
  teamId: null,
  slots: { running: 2, limit: 4, queued: 1 },
  readOnly: false,
  removing: null,
  openWorkflows: 3,
});

function fakeHandlers() {
  const calls: unknown[][] = [];
  const rec = (name: string) => (...a: unknown[]) => { calls.push([name, ...a]); };
  const h: EmbedHandlers = {
    newChat: rec('newChat'), go: rec('go'), logs: rec('logs'), openTeam: rec('openTeam'), openConversation: rec('openConversation'),
    newTeam: rec('newTeam'), rename: rec('rename'), remove: rec('remove'), terminal: rec('terminal'), help: rec('help'),
  };
  return { h, calls };
}

describe('isEmbed and the theme in the query', () => {
  it('embed=1 only', () => {
    expect(isEmbed('?embed=1&theme=dark')).toBe(true);
    expect(isEmbed('?theme=dark&embed=1')).toBe(true);
    expect(isEmbed('')).toBe(false);
    expect(isEmbed('?embed=0')).toBe(false);
    expect(isEmbed('?embed=true')).toBe(false);
    expect(isEmbed('?embedded=1')).toBe(false);
  });
  it('theme=light|dark, else null', () => {
    expect(embedThemeFromQuery('?embed=1&theme=light')).toBe('light');
    expect(embedThemeFromQuery('?embed=1&theme=dark')).toBe('dark');
    expect(embedThemeFromQuery('?embed=1')).toBeNull();
    expect(embedThemeFromQuery('?embed=1&theme=system')).toBeNull();
    expect(embedThemeFromQuery('?embed=1&theme=Dark')).toBeNull();
  });
});

describe('buildNav: the sidebar as data', () => {
  it('the places: ids, labels, icon keys and order', () => {
    const nav = buildNav(base());
    expect(nav.places.map((p) => [p.id, p.label, p.icon])).toEqual([
      ['newChat', 'New chat', 'newChat'], ['concierge', 'Concierge', 'concierge'], ['teams', 'Teams', 'teams'],
      ['backlog', 'Backlog', 'backlog'], ['workflows', 'Workflows', 'workflows'], ['board', 'Kanban board', 'kanban'],
      ['documents', 'Documents', 'documents'], ['monitor', 'Monitor', 'monitor'], ['logs', 'Logs', 'logs'],
      ['terminal', 'Terminal', 'terminal'], ['help', 'Help', 'help'],
    ]);
    expect(EMBED_PLACE_IDS).toEqual(nav.places.map((p) => p.id));
    expect(nav.places.every((p) => p.disabled === false)).toBe(true);
  });
  it('the same order and labels as Sidebar.vue draws (New chat, then the places nav), then Terminal and Help', () => {
    const w = mount(Sidebar, { props: { ...base(), remote: true } as any });
    const drawn = [w.get('[data-test=new-chat]').text(), ...w.findAll('nav.places button').map((b) => b.text().replace(/\d+$/, ''))];
    expect([...drawn, 'Terminal', 'Help']).toEqual(buildNav(base()).places.map((p) => p.label));
    const ids = w.findAll('nav.places button').map((b) => b.attributes('data-test')!.replace(/^nav-/, ''));
    expect(['newChat', ...ids, 'terminal', 'help']).toEqual(EMBED_PLACE_IDS);
  });
  it('B117: Terminal and Help are the phone drawer\'s Terminal and About Yawble (App.vue DRAWER_COMMANDS)', () => {
    const app = readFileSync(resolve(__dirname, '../../src/renderer/src/App.vue'), 'utf8');
    const drawer = /const DRAWER_COMMANDS = \[([^\]]*)\]/.exec(app)![1];
    expect(drawer).toContain("'terminal'");
    expect(drawer).toContain("'about'");
    // App.vue's handlers run the drawer's own commands.
    expect(app).toContain("terminal: () => command('terminal')");
    expect(app).toContain("help: () => command('about')");
    expect(app).toMatch(/case 'about': menu\('system'\)/);
    expect(HELP_SETTINGS_TAB).toBe('system');
  });
  it('B117: Terminal is active while the terminal pane is shown, Help while Settings shows System', () => {
    const active = (s: Partial<EmbedNavState>) => buildNav({ ...base(), ...s }).places.filter((p) => p.active).map((p) => p.id);
    expect(active({ terminalOpen: true })).toEqual(['concierge', 'terminal']);
    expect(active({ terminalOpen: false })).toEqual(['concierge']);
    expect(active({ view: 'settings', settingsTab: 'system' })).toEqual(['help']);
    expect(active({ view: 'settings', settingsTab: 'agents' })).toEqual([]);
    expect(active({ view: 'monitor', settingsTab: 'system' })).toEqual(['monitor']);
  });
  it('with the menu bar drawn (wide), Terminal and Help are left out: the menu has them; on a phone they stay', () => {
    const ids = (s: Partial<EmbedNavState>) => buildNav({ ...base(), ...s }).places.map((p) => p.id);
    expect(ids({ menuBar: true })).not.toContain('terminal');
    expect(ids({ menuBar: true })).not.toContain('help');
    expect(ids({ menuBar: true })).toEqual(EMBED_PLACE_IDS.filter((id) => id !== 'terminal' && id !== 'help'));
    expect(ids({ menuBar: false }).slice(-2)).toEqual(['terminal', 'help']);
    // A read-only viewer on a phone still gets Help and never Terminal.
    expect(ids({ menuBar: false, readOnly: true }).slice(-2)).toEqual(['logs', 'help']);
    // App.vue passes the menu bar's own condition.
    const app = readFileSync(resolve(__dirname, '../../src/renderer/src/App.vue'), 'utf8');
    expect(app).toContain('menuBar: !menuInDrawer.value');
    expect(app).toContain('<MenuBar v-if="!menuInDrawer"');
  });
  it('B117: placeAllowed leaves only Terminal out, and only for a read-only viewer', () => {
    for (const id of EMBED_PLACE_IDS) {
      expect(placeAllowed(id, false), id).toBe(true);
      expect(placeAllowed(id, true), id).toBe(id !== 'terminal');
    }
  });
  it('badges: Teams counts the teams, Workflows the open ones; none when 0 or not known', () => {
    const nav = buildNav(base());
    const badge = (n: EmbedNav, id: string) => n.places.find((p) => p.id === id)!.badge;
    expect(badge(nav, 'teams')).toBe(4);
    expect(badge(nav, 'workflows')).toBe(3);
    expect(nav.places.filter((p) => p.id !== 'teams' && p.id !== 'workflows').every((p) => p.badge === null)).toBe(true);
    const none = buildNav({ ...base(), teams: [], openWorkflows: 0 });
    expect(badge(none, 'teams')).toBeNull();
    expect(badge(none, 'workflows')).toBeNull();
    expect(badge(buildNav({ ...base(), openWorkflows: null }), 'workflows')).toBeNull();
    expect(badge(buildNav({ ...base(), openWorkflows: undefined }), 'workflows')).toBeNull();
  });
  it('the active place is the view; New chat, Logs and Settings never light a place', () => {
    for (const v of ['concierge', 'teams', 'backlog', 'workflows', 'board', 'documents', 'monitor']) {
      expect(buildNav({ ...base(), view: v }).places.filter((p) => p.active).map((p) => p.id)).toEqual([v]);
    }
    expect(buildNav({ ...base(), view: 'settings' }).places.some((p) => p.active)).toBe(false);
  });
  it('teams: dots by teamState, running count as badge, the open team active', () => {
    const nav = buildNav({ ...base(), view: 'teams', teamId: 2 });
    expect(nav.teams).toEqual([
      { id: 1, name: 'payments', dot: 'running', badge: 2, active: false },
      { id: 2, name: 'docs', dot: 'queued', badge: null, active: true },
      { id: 3, name: 'infra', dot: 'paused', badge: null, active: false },
      { id: 4, name: 'web', dot: 'idle', badge: null, active: false },
    ]);
    // Another screen: no team is active even with a teamId kept.
    expect(buildNav({ ...base(), view: 'board', teamId: 2 }).teams.some((t) => t.active)).toBe(false);
  });
  it('the same dots as Sidebar.vue draws', () => {
    const w = mount(Sidebar, { props: { ...base(), remote: true } as any });
    expect(w.findAll('[data-test=team-item] .dot').map((d) => d.attributes('data-state'))).toEqual(buildNav(base()).teams.map((t) => t.dot));
  });
  it('chats: the open one active, Rename and Delete (danger), deleting while removed', () => {
    const nav = buildNav({ ...base(), removing: 'c2' });
    expect(nav.chats).toEqual([
      { id: 'c1', title: 'Plan the release', active: true, busy: null, actions: [{ id: 'rename', label: 'Rename', danger: false }, { id: 'delete', label: 'Delete', danger: true }] },
      { id: 'c2', title: 'Ask about logs', active: false, busy: 'deleting', actions: [{ id: 'rename', label: 'Rename', danger: false }, { id: 'delete', label: 'Delete', danger: true }] },
    ]);
    // A chat is active only on the Concierge screen.
    expect(buildNav({ ...base(), view: 'teams' }).chats.some((c) => c.active)).toBe(false);
  });
  it('run slots: the line under the sidebar, null before the first answer', () => {
    expect(buildNav(base()).runSlots).toBe('Run slots: 2 / 4 in use · 1 queued');
    expect(buildNav({ ...base(), slots: null }).runSlots).toBeNull();
    const w = mount(Sidebar, { props: { ...base(), remote: true } as any });
    expect(w.get('[data-test=slots]').text()).toBe(runSlotsLine(base().slots));
  });
  it('read-only: no New team, no chat actions and no Terminal; the other places, Help included, stay', () => {
    const nav = buildNav({ ...base(), readOnly: true });
    expect(nav.readOnly).toBe(true);
    expect(nav.canNewTeam).toBe(false);
    expect(nav.chats.every((c) => c.actions.length === 0)).toBe(true);
    expect(nav.places.map((p) => p.id)).toEqual(EMBED_PLACE_IDS.filter((id) => id !== 'terminal'));
    expect(buildNav(base()).canNewTeam).toBe(true);
    expect(buildNav(base()).readOnly).toBe(false);
  });
  it('is pure: same state, same nav; the state is not changed; the result is plain data', () => {
    const s = base();
    const before = JSON.stringify(s);
    expect(buildNav(s)).toEqual(buildNav(s));
    expect(JSON.stringify(s)).toBe(before);
    const nav = buildNav(reactive(base()) as EmbedNavState);
    expect(() => structuredClone(nav)).not.toThrow();
  });
});

describe('parsing the cloud\'s messages', () => {
  it('navigate: every kind, and malformed ones refused', () => {
    expect(parseNavigate({ type: 'yawble.navigate', to: { kind: 'place', id: 'board' } })).toEqual({ kind: 'place', id: 'board' });
    expect(parseNavigate({ type: 'yawble.navigate', to: { kind: 'team', id: 2 } })).toEqual({ kind: 'team', id: 2 });
    // The cloud keeps every nav id as text: a decimal string is the same team.
    expect(parseNavigate({ type: 'yawble.navigate', to: { kind: 'team', id: '2' } })).toEqual({ kind: 'team', id: 2 });
    expect(parseNavigate({ type: 'yawble.navigate', to: { kind: 'chat', id: 'c1' } })).toEqual({ kind: 'chat', id: 'c1' });
    expect(parseNavigate({ type: 'yawble.navigate', to: { kind: 'newTeam' } })).toEqual({ kind: 'newTeam' });
    expect(parseNavigate({ type: 'yawble.navigate', to: { kind: 'chatAction', id: 'c1', action: 'delete' } })).toEqual({ kind: 'chatAction', id: 'c1', action: 'delete' });
    for (const bad of [
      null, 'yawble.navigate', { type: 'yawble.navigate' }, { type: 'yawble.nav', to: { kind: 'newTeam' } },
      { type: 'yawble.navigate', to: { kind: 'place', id: 'settings' } }, { type: 'yawble.navigate', to: { kind: 'place', id: 'kanban' } },
      { type: 'yawble.navigate', to: { kind: 'team', id: '2x' } }, { type: 'yawble.navigate', to: { kind: 'team', id: 1.5 } },
      { type: 'yawble.navigate', to: { kind: 'chat', id: '' } }, { type: 'yawble.navigate', to: { kind: 'chatAction', id: 'c1', action: 'archive' } },
      { type: 'yawble.navigate', to: { kind: 'open', id: 'x' } },
    ]) expect(parseNavigate(bad)).toBeNull();
  });
  it('theme: light or dark only', () => {
    expect(parseTheme({ type: 'yawble.theme', theme: 'dark' })).toBe('dark');
    expect(parseTheme({ type: 'yawble.theme', theme: 'light' })).toBe('light');
    expect(parseTheme({ type: 'yawble.theme', theme: 'system' })).toBeNull();
    expect(parseTheme({ type: 'yawble.navigate', theme: 'dark' })).toBeNull();
    expect(parseTheme(undefined)).toBeNull();
  });
});

describe('navigate does what the same sidebar click does', () => {
  // The sidebar's emits mapped onto the handlers, as App.vue wires them.
  function sidebarCalls(s: EmbedNavState, click: (w: ReturnType<typeof mount>) => Promise<void>) {
    return async () => {
      const w = mount(Sidebar, { props: { ...s, remote: true } as any });
      await click(w);
      const calls: unknown[][] = [];
      for (const [event, args] of Object.entries(w.emitted() as Record<string, unknown[][]>)) {
        const name = { newChat: 'newChat', go: 'go', logs: 'logs', openTeam: 'openTeam', openConversation: 'openConversation', newTeam: 'newTeam', rename: 'rename', remove: 'remove' }[event];
        if (name) for (const a of args) calls.push([name, ...a]);
      }
      return calls;
    };
  }
  const s = base();
  const cases: [string, unknown, (w: ReturnType<typeof mount>) => Promise<void>][] = [
    ['newChat', { kind: 'place', id: 'newChat' }, async (w) => { await w.get('[data-test=new-chat]').trigger('click'); }],
    ...(['concierge', 'teams', 'backlog', 'workflows', 'board', 'documents', 'monitor'] as const).map((v) =>
      [v, { kind: 'place', id: v }, async (w: ReturnType<typeof mount>) => { await w.get(`[data-test=nav-${v}]`).trigger('click'); }] as [string, unknown, (w: ReturnType<typeof mount>) => Promise<void>]),
    ['logs', { kind: 'place', id: 'logs' }, async (w) => { await w.get('[data-test=nav-logs]').trigger('click'); }],
    ['team', { kind: 'team', id: 2 }, async (w) => { await w.findAll('[data-test=team-item]')[1].trigger('click'); }],
    ['chat', { kind: 'chat', id: 'c2' }, async (w) => { await w.findAll('[data-test=chat-item] button.open')[1].trigger('click'); }],
    ['newTeam', { kind: 'newTeam' }, async (w) => { await w.get('[data-test=new-team]').trigger('click'); }],
  ];
  for (const [name, to, click] of cases) {
    it(name, async () => {
      const { h, calls } = fakeHandlers();
      await dispatchNavigate(parseNavigate({ type: 'yawble.navigate', to })!, s, h);
      expect(calls.length).toBe(1);
      expect(calls).toEqual(await sidebarCalls(s, click)());
    });
  }
  it('chat Delete: asks "Delete this chat?" first (Delete, red), as the sidebar does; Keep does nothing', async () => {
    const sidebar = await sidebarCalls(s, async (w) => {
      await w.findAll('[data-test=chat-delete]')[0].trigger('click');
      expect(w.get('[data-test=chat-item]').text()).toContain('Delete this chat?');
      await w.get('[data-test=chat-delete-yes]').trigger('click');
    })();
    const { h, calls } = fakeHandlers();
    const asked: unknown[] = [];
    await dispatchNavigate({ kind: 'chatAction', id: 'c1', action: 'delete' }, s, h, { confirm: async (o) => { asked.push(o); return true; } });
    expect(asked).toEqual([{ message: 'Delete this chat?', okLabel: 'Delete', danger: true }]);
    expect(calls).toEqual(sidebar);
    const kept = fakeHandlers();
    await dispatchNavigate({ kind: 'chatAction', id: 'c1', action: 'delete' }, s, kept.h, { confirm: async () => false });
    expect(kept.calls).toEqual([]);
  });
  it('chat Delete uses the window\'s own confirm dialog (drawn inside the frame) by default', async () => {
    const { h, calls } = fakeHandlers();
    const p = dispatchNavigate({ kind: 'chatAction', id: 'c2', action: 'delete' }, s, h)!;
    expect(confirmState.value?.message).toBe('Delete this chat?');
    expect(confirmState.value?.okLabel).toBe('Delete');
    expect(confirmState.value?.danger).toBe(true);
    confirmState.value!.resolve(true);
    await p;
    expect(calls).toEqual([['remove', 'c2']]);
  });
  it('chat Rename: asks for the title (given the current one), then renames with it trimmed, as the sidebar does', async () => {
    const sidebar = await sidebarCalls(s, async (w) => {
      await w.findAll('[data-test=chat-rename]')[0].trigger('click');
      const box = w.get('[data-test=chat-rename-box]');
      await box.setValue('  Release plan ');
      await box.trigger('keydown', { key: 'Enter' });
    })();
    const { h, calls } = fakeHandlers();
    const asked: string[] = [];
    await dispatchNavigate({ kind: 'chatAction', id: 'c1', action: 'rename' }, s, h, { askTitle: async (t) => { asked.push(t); return '  Release plan '; } });
    expect(asked).toEqual(['Plan the release']);
    expect(calls).toEqual(sidebar);
    expect(calls).toEqual([['rename', 'c1', 'Release plan']]);
    for (const answer of [null, '', '   ']) {
      const f = fakeHandlers();
      await dispatchNavigate({ kind: 'chatAction', id: 'c1', action: 'rename' }, s, f.h, { askTitle: async () => answer });
      expect(f.calls).toEqual([]);
    }
  });
  it('ignored like a missing or disabled button: unknown team or chat, read-only New team and chat actions, Delete while deleting', async () => {
    const { h, calls } = fakeHandlers();
    const confirm = vi.fn(async () => true);
    const askTitle = vi.fn(async () => 'x');
    const ro = { ...s, readOnly: true };
    await dispatchNavigate({ kind: 'team', id: 99 }, s, h);
    await dispatchNavigate({ kind: 'chat', id: 'nope' }, s, h);
    await dispatchNavigate({ kind: 'chatAction', id: 'nope', action: 'delete' }, s, h, { confirm, askTitle });
    await dispatchNavigate({ kind: 'newTeam' }, ro, h);
    await dispatchNavigate({ kind: 'chatAction', id: 'c1', action: 'delete' }, ro, h, { confirm, askTitle });
    await dispatchNavigate({ kind: 'chatAction', id: 'c1', action: 'rename' }, ro, h, { confirm, askTitle });
    await dispatchNavigate({ kind: 'chatAction', id: 'c1', action: 'delete' }, { ...s, removing: 'c2' }, h, { confirm, askTitle });
    expect(calls).toEqual([]);
    expect(confirm).not.toHaveBeenCalled();
    expect(askTitle).not.toHaveBeenCalled();
    // The sidebar agrees: read-only disables New team and the chat buttons (v-change).
    const { applyRemoteAccess } = await import('../../src/renderer/src/remoteAccess');
    applyRemoteAccess('read');
    try {
      const w = mount(Sidebar, { props: { ...ro, remote: true } as any });
      expect((w.get('[data-test=new-team]').element as HTMLButtonElement).disabled).toBe(true);
      expect((w.get('[data-test=chat-delete]').element as HTMLButtonElement).disabled).toBe(true);
      expect((w.get('[data-test=chat-rename]').element as HTMLButtonElement).disabled).toBe(true);
    } finally { applyRemoteAccess('full'); }
    // Places still work for a reader, as in the sidebar.
    await dispatchNavigate({ kind: 'place', id: 'board' }, ro, h);
    expect(calls).toEqual([['go', 'board']]);
  });
  it('B117: Terminal and Help parse, and run the drawer\'s Terminal and About Yawble commands', async () => {
    expect(parseNavigate({ type: 'yawble.navigate', to: { kind: 'place', id: 'terminal' } })).toEqual({ kind: 'place', id: 'terminal' });
    expect(parseNavigate({ type: 'yawble.navigate', to: { kind: 'place', id: 'help' } })).toEqual({ kind: 'place', id: 'help' });
    expect(parseNavigate({ type: 'yawble.navigate', to: { kind: 'place', id: 'about' } })).toBeNull();
    const { h, calls } = fakeHandlers();
    await dispatchNavigate({ kind: 'place', id: 'terminal' }, s, h);
    await dispatchNavigate({ kind: 'place', id: 'help' }, s, h);
    expect(calls).toEqual([['terminal'], ['help']]);
    // The drawer's buttons for the same items emit those commands (Sidebar.vue's menu at a phone's drawer bottom).
    const w = mount(Sidebar, { props: { ...s, remote: true, drawer: true, open: true,
      menu: [{ id: 'terminal', label: 'Terminal' }, { id: 'about', label: 'About Yawble' }] } as any });
    await w.get('[data-test=drawer-terminal]').trigger('click');
    await w.get('[data-test=drawer-about]').trigger('click');
    expect(w.emitted('command')).toEqual([['terminal'], ['about']]);
  });
  it('B117: a read-only viewer\'s Terminal is ignored (not offered, as in the drawer); Help still opens', async () => {
    const { h, calls } = fakeHandlers();
    const ro = { ...s, readOnly: true };
    await dispatchNavigate({ kind: 'place', id: 'terminal' }, ro, h);
    expect(calls).toEqual([]);
    await dispatchNavigate({ kind: 'place', id: 'help' }, ro, h);
    expect(calls).toEqual([['help']]);
    expect(buildNav(ro).places.some((p) => p.id === 'terminal')).toBe(false);
    expect(buildNav(ro).places.some((p) => p.id === 'help')).toBe(true);
  });
});

// ---- the bridge over a fake window ----
const ORIGIN = 'https://cloud.example';
function fakeWindow(search = '?embed=1&theme=dark') {
  const target = new EventTarget();
  const posted: { msg: any; origin: string }[] = [];
  const parent = { postMessage: (msg: unknown, origin: string) => { posted.push({ msg: structuredClone(msg), origin }); } };
  const win = {
    addEventListener: target.addEventListener.bind(target),
    removeEventListener: target.removeEventListener.bind(target),
    parent,
    location: { origin: ORIGIN, search },
  };
  const send = (data: unknown, { origin = ORIGIN, source = parent as unknown } = {}) => {
    const e = new Event('message');
    Object.defineProperties(e, { data: { value: data }, origin: { value: origin }, source: { value: source } });
    target.dispatchEvent(e);
  };
  return { win: win as any, parent, posted, send };
}

describe('the bridge', () => {
  let root: HTMLElement;
  beforeEach(() => { root = document.createElement('html'); });

  it('applies the query\'s theme, posts yawble.ready, then yawble.nav once (to its own origin)', () => {
    const f = fakeWindow();
    const { h } = fakeHandlers();
    const b = startEmbedBridge({ state: base, handlers: h, win: f.win, root });
    expect(root.getAttribute('data-theme')).toBe('dark');
    expect(root.style.colorScheme).toBe('dark');
    expect(f.posted.map((p) => p.msg.type)).toEqual(['yawble.ready', 'yawble.nav']);
    expect(f.posted[0].msg).toEqual({ type: 'yawble.ready', version: 1 });
    expect(f.posted[1].msg).toEqual({ type: 'yawble.nav', version: 1, nav: buildNav(base()) });
    expect(f.posted.every((p) => p.origin === ORIGIN)).toBe(true);
    b.dispose();
  });
  it('posts yawble.nav again on each change, and not when nothing in it changed', async () => {
    const f = fakeWindow('?embed=1');
    const state = reactive(base()) as EmbedNavState & { conversations: ConversationSummary[]; teams: TeamSummary[] };
    const b = startEmbedBridge({ state: () => state, handlers: fakeHandlers().h, win: f.win, root });
    expect(root.hasAttribute('data-theme')).toBe(false);
    const navs = () => f.posted.filter((p) => p.msg.type === 'yawble.nav').map((p) => p.msg.nav as EmbedNav);
    expect(navs()).toHaveLength(1);
    state.view = 'board';
    await nextTick();
    expect(navs()).toHaveLength(2);
    expect(navs()[1].places.find((p) => p.active)!.id).toBe('board');
    state.teams[0].running = 0;
    await nextTick();
    expect(navs()[2].teams[0]).toMatchObject({ dot: 'idle', badge: null });
    state.conversations.push(chat('c3', 'New one'));
    await nextTick();
    expect(navs()[3].chats.map((c) => c.id)).toEqual(['c1', 'c2', 'c3']);
    // Something the nav does not show (a chat's unread count): no new message.
    state.conversations[0].unread = 5;
    await nextTick();
    expect(navs()).toHaveLength(4);
    b.dispose();
    state.view = 'monitor';
    await nextTick();
    expect(navs()).toHaveLength(4);
  });
  it('navigate and theme messages from the parent at the same origin are followed', async () => {
    const f = fakeWindow();
    const { h, calls } = fakeHandlers();
    const b = startEmbedBridge({ state: base, handlers: h, win: f.win, root });
    f.send({ type: 'yawble.navigate', to: { kind: 'place', id: 'monitor' } });
    f.send({ type: 'yawble.navigate', to: { kind: 'team', id: 1 } });
    f.send({ type: 'yawble.navigate', to: { kind: 'chat', id: 'c2' } });
    f.send({ type: 'yawble.theme', theme: 'light' });
    expect(calls).toEqual([['go', 'monitor'], ['openTeam', 1], ['openConversation', 'c2']]);
    expect(root.getAttribute('data-theme')).toBe('light');
    f.send({ type: 'yawble.theme', theme: 'dark' });
    expect(root.getAttribute('data-theme')).toBe('dark');
    b.dispose();
  });
  it('the dialogs passed in are used for chat actions', async () => {
    const f = fakeWindow();
    const { h, calls } = fakeHandlers();
    const b = startEmbedBridge({ state: base, handlers: h, win: f.win, root, dialogs: { confirm: async () => true, askTitle: async () => 'Renamed' } });
    f.send({ type: 'yawble.navigate', to: { kind: 'chatAction', id: 'c1', action: 'delete' } });
    f.send({ type: 'yawble.navigate', to: { kind: 'chatAction', id: 'c2', action: 'rename' } });
    await flushPromises();
    expect(calls).toEqual([['remove', 'c1'], ['rename', 'c2', 'Renamed']]);
    b.dispose();
  });
  it('ignores another origin, another source, malformed messages, and anything after dispose', () => {
    const f = fakeWindow('?embed=1&theme=light');
    const { h, calls } = fakeHandlers();
    const b = startEmbedBridge({ state: base, handlers: h, win: f.win, root });
    const nav = { type: 'yawble.navigate', to: { kind: 'place', id: 'board' } };
    const dark = { type: 'yawble.theme', theme: 'dark' };
    for (const m of [nav, dark]) {
      f.send(m, { origin: 'https://evil.example' });
      f.send(m, { origin: 'null' });
      f.send(m, { origin: 'https://cloud.example:8443' });
      f.send(m, { source: {} });
      f.send(m, { source: null });
      f.send(m, { source: f.win });
    }
    f.send({ type: 'yawble.navigate', to: { kind: 'place', id: 'settings' } });
    f.send({ type: 'yawble.theme', theme: 'blue' });
    f.send('yawble.theme');
    f.send(null);
    expect(calls).toEqual([]);
    expect(root.getAttribute('data-theme')).toBe('light');
    b.dispose();
    f.send(nav);
    f.send(dark);
    expect(calls).toEqual([]);
    expect(root.getAttribute('data-theme')).toBe('light');
  });
  it('a page not in a frame (its parent is itself) posts and listens to nothing', () => {
    const posted: unknown[] = [];
    const added = vi.fn();
    const win: any = { addEventListener: added, removeEventListener: vi.fn(), location: { origin: ORIGIN, search: '?embed=1&theme=dark' }, postMessage: (m: unknown) => posted.push(m) };
    win.parent = win;
    const b = startEmbedBridge({ state: base, handlers: fakeHandlers().h, win, root });
    expect(posted).toEqual([]);
    expect(added).not.toHaveBeenCalled();
    expect(root.getAttribute('data-theme')).toBe('dark');
    b.dispose();
  });
});

describe('embed mode never reads or writes the remote theme key', () => {
  afterEach(() => { vi.unstubAllGlobals(); });
  it('no storage call at all while starting, posting, and following theme messages', async () => {
    // A fake localStorage (and sessionStorage) holding the viewer's own remote theme, every call recorded.
    const kept = new Map<string, string>([[REMOTE_THEME_KEY, 'light']]);
    const get = vi.fn((k: string) => kept.get(k) ?? null);
    const set = vi.fn((k: string, v: string) => { kept.set(k, v); });
    const remove = vi.fn((k: string) => { kept.delete(k); });
    const fake = { getItem: get, setItem: set, removeItem: remove, clear: vi.fn(), key: vi.fn(), get length() { return kept.size; } };
    vi.stubGlobal('localStorage', fake);
    vi.stubGlobal('sessionStorage', fake);
    const f = fakeWindow('?embed=1&theme=dark');
    const root = document.createElement('html');
    const state = reactive(base());
    const b = startEmbedBridge({ state: () => state, handlers: fakeHandlers().h, win: f.win, root });
    f.send({ type: 'yawble.theme', theme: 'light' });
    f.send({ type: 'yawble.theme', theme: 'dark' });
    applyEmbedTheme('light', root);
    state.view = 'board';
    await nextTick();
    b.dispose();
    expect(get).not.toHaveBeenCalled();
    expect(set).not.toHaveBeenCalled();
    expect(remove).not.toHaveBeenCalled();
    expect(kept.get(REMOTE_THEME_KEY)).toBe('light');
    // The fake is the page's storage (so the checks above would catch a read or a write).
    expect(window.localStorage.getItem(REMOTE_THEME_KEY)).toBe('light');
    expect(get).toHaveBeenCalledWith(REMOTE_THEME_KEY);
  });
  it('embed.ts does not import theme.ts or use localStorage', () => {
    const src = readFileSync(resolve(__dirname, '../../src/renderer/src/embed.ts'), 'utf8');
    expect(src).not.toMatch(/from '\.\/theme'/);
    expect(src).not.toMatch(/localStorage|sessionStorage|REMOTE_THEME_KEY|yawble\.remoteTheme/);
  });
});
