// @vitest-environment happy-dom
// Embed mode wired into the window. App.vue mounted at remote.html?embed=1 with a fake API
// and a fake parent window: no sidebar (wide or phone), no drawer or menu button, no theme switch, the Settings gear kept;
// the bridge posts yawble.ready then yawble.nav (once, and again on a change); the parent's yawble.navigate goes through
// the sidebar's handlers, its yawble.theme sets data-theme. theme.ts and theme-boot.js in embed mode neither read nor
// write yawble.remoteTheme, and ?theme= sets the theme at load. Without embed=1 nothing changes. Fakes only.
import { flushPromises, mount } from '@vue/test-utils';
import { readFileSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { YawbleApi } from '../../src/shared/api';
import { REMOTE_THEME_KEY, createTheme, pageThemeEnv } from '../../src/renderer/src/theme';

const OVERVIEW = {
  slots: { running: 1, limit: 4, queued: 0 },
  teams: [{ id: 7, name: 'Alpha', archived: false, running: 0, queued: 0, paused: false, members: 2 }],
};
const CHATS = [{ id: 'c1', title: 'First chat', updatedAt: '2026-11-20T00:00:00Z' }];

function api(calls: { method: string; params: unknown }[] = []): YawbleApi {
  return {
    remote: true,
    getInfo: async () => { throw new Error('no'); },
    getAvailableBackends: async () => [],
    call: async (method: string, params?: unknown) => {
      calls.push({ method, params });
      switch (method) {
        case 'app.overview': return OVERVIEW as any;
        case 'concierge.list': return CHATS as any;
        case 'app.version': return { version: '1.2.3', released: true, dirty: false, commit: 'abcdef0123456789', cloudVersion: null } as any;
        default: return [] as any;
      }
    },
    onEvent: () => () => {},
    openTerminal: async () => {},
  } as unknown as YawbleApi;
}

/** prefers-color-scheme is light; the phone query (phone.ts, max-width) matches below 760 px. */
function fakeMedia(width: number) {
  return (q: string) => ({ matches: q.includes('max-width') ? width < 760 : false, addEventListener() {}, removeEventListener() {} }) as any;
}

const realParent = Object.getOwnPropertyDescriptor(window, 'parent');

/** A page at `search`, framed by a fake parent that records what is posted to it. */
async function page(search: string, opts: { width?: number; stored?: string } = {}) {
  history.replaceState(null, '', `/remote.html${search}`);
  vi.stubGlobal('matchMedia', fakeMedia(opts.width ?? 1280));
  vi.stubGlobal('innerWidth', opts.width ?? 1280);
  if (opts.stored) localStorage.setItem(REMOTE_THEME_KEY, opts.stored);
  const posted: { msg: any; origin: string }[] = [];
  const parent = { postMessage: (msg: unknown, origin: string) => posted.push({ msg: JSON.parse(JSON.stringify(msg)), origin }) };
  Object.defineProperty(window, 'parent', { configurable: true, get: () => parent });
  (window as any).yawble = { remote: true };
  const calls: { method: string; params: unknown }[] = [];
  const App = (await import('../../src/renderer/src/App.vue')).default;
  const w = mount(App, { props: { api: api(calls), pollMs: 60_000 }, attachTo: document.body });
  await flushPromises();
  const send = async (data: unknown, from: unknown = parent, origin = location.origin) => {
    window.dispatchEvent(new MessageEvent('message', { data, origin, source: from as any }));
    await flushPromises();
  };
  return { w, posted, send, calls, navs: () => posted.filter((p) => p.msg.type === 'yawble.nav').map((p) => p.msg.nav) };
}

describe('embed mode in the window (App.vue)', { timeout: 30_000 }, () => {
  beforeEach(() => { vi.resetModules(); localStorage.clear(); sessionStorage.clear(); document.documentElement.removeAttribute('data-theme'); });
  afterEach(() => {
    document.body.innerHTML = '';
    delete (window as any).yawble;
    vi.unstubAllGlobals();
    if (realParent) Object.defineProperty(window, 'parent', realParent); else delete (window as any).parent;
    history.replaceState(null, '', '/');
  });

  for (const width of [1280, 375]) {
    it(`at ${width}px: no sidebar, drawer, menu button or theme switch; the gear stays; the screens take the full width`, async () => {
      const { w } = await page('?embed=1&theme=dark', { width });
      expect(w.find('[data-test=sidebar]').exists()).toBe(false);
      expect(w.find('#app-drawer').exists()).toBe(false);
      expect(w.find('[data-test=drawer-toggle]').exists()).toBe(false);
      expect(w.find('[data-test=drawer-scrim]').exists()).toBe(false);
      expect(w.find('[data-test=theme-toggle]').exists()).toBe(false);
      expect(w.find('[data-test=settings-gear]').exists()).toBe(true);
      const app = w.get('.app');
      expect(app.classes().includes('phone')).toBe(width < 760);
      expect(app.classes()).toContain('embed');
      expect(app.classes()).not.toContain('collapsed');
      expect(app.attributes('data-embed')).toBe('1');
      // The grid has a single column in embed mode (the stylesheet's rule for .app.embed).
      const css = readFileSync(resolve(__dirname, '../../src/renderer/src/App.vue'), 'utf8');
      expect(css).toMatch(/\.app\.embed\s*\{\s*grid-template-columns:\s*minmax\(0,\s*1fr\);\s*\}/);
    });
  }

  it('the View menu has no Sidebar, Theme, Terminal or Developer tools items, and Ctrl+B does nothing', async () => {
    const { w } = await page('?embed=1&theme=dark', { width: 1280 });
    await w.get('[data-test="menu-view"]').trigger('click');
    for (const id of ['sidebar', 'theme:system', 'theme:light', 'theme:dark', 'toggleTheme']) expect(w.find(`[data-test="menuitem-${id}"]`).exists(), id).toBe(false);
    expect(w.find('[data-test="menuitem-terminal"]').exists()).toBe(false);
    expect(w.find('[data-test="menuitem-devtools"]').exists()).toBe(false);
    window.dispatchEvent(new KeyboardEvent('keydown', { key: 'b', ctrlKey: true }));
    await flushPromises();
    expect(w.find('[data-test=sidebar]').exists()).toBe(false);
    expect(w.get('.app').classes()).not.toContain('collapsed');
  });

  it('posts yawble.ready, then yawble.nav once, and again on a change, to its own origin', async () => {
    const { posted, navs, send } = await page('?embed=1&theme=dark');
    expect(posted[0]!.msg).toEqual({ type: 'yawble.ready', version: 1 });
    expect(posted.every((p) => p.origin === location.origin)).toBe(true);
    const nav = navs().at(-1)!;
    expect(nav.teams.map((t: any) => t.name)).toEqual(['Alpha']);
    expect(nav.chats.map((c: any) => c.title)).toEqual(['First chat']);
    expect(nav.runSlots).toBe('Run slots: 1 / 4 in use · 0 queued');
    const before = navs().length;
    await send({ type: 'yawble.navigate', to: { kind: 'place', id: 'board' } });
    expect(navs().length).toBe(before + 1);
    expect(navs().at(-1)!.places.find((p: any) => p.active)?.id).toBe('board');
    // Nothing changed: nothing posted.
    await send({ type: 'yawble.navigate', to: { kind: 'place', id: 'board' } });
    expect(navs().length).toBe(before + 1);
  });

  it('navigate messages go through the sidebar\'s handlers: places, a team, a chat, New chat, Delete with the in-frame confirm', async () => {
    const { w, send, navs, calls } = await page('?embed=1&theme=dark');
    await send({ type: 'yawble.navigate', to: { kind: 'place', id: 'documents' } });
    expect(navs().at(-1)!.places.find((p: any) => p.active)?.id).toBe('documents');
    await send({ type: 'yawble.navigate', to: { kind: 'team', id: 7 } });
    expect(navs().at(-1)!.teams[0].active).toBe(true);
    await send({ type: 'yawble.navigate', to: { kind: 'team', id: 999 } }); // not in the state: ignored
    expect(navs().at(-1)!.teams[0].active).toBe(true);
    await send({ type: 'yawble.navigate', to: { kind: 'chat', id: 'c1' } });
    expect(navs().at(-1)!.chats[0].active).toBe(true);
    await send({ type: 'yawble.navigate', to: { kind: 'place', id: 'newChat' } });
    expect(navs().at(-1)!.chats[0].active).toBe(false);
    expect(navs().at(-1)!.places.find((p: any) => p.active)?.id).toBe('concierge');
    await send({ type: 'yawble.navigate', to: { kind: 'place', id: 'logs' } });
    expect(document.querySelector('[data-test=logs]')).not.toBeNull();
    // The Scripts place opens the Scripts dialog drawn as the page, and lights the place.
    await send({ type: 'yawble.navigate', to: { kind: 'place', id: 'scripts' } });
    expect(document.querySelector('[data-test=scripts]')).not.toBeNull();
    expect(navs().at(-1)!.places.find((p: any) => p.active)?.id).toBe('scripts');
    expect(calls.some((c) => c.method === 'scripts.list')).toBe(true);
    await send({ type: 'yawble.navigate', to: { kind: 'chatAction', id: 'c1', action: 'delete' } });
    const ok = document.querySelector<HTMLButtonElement>('[data-test=confirm-ok]');
    expect(document.querySelector('[data-test=confirm-message]')?.textContent).toBe('Delete this chat?');
    ok!.click();
    await flushPromises();
    expect(calls.some((c) => c.method === 'concierge.delete' && (c.params as any).id === 'c1')).toBe(true);
  });

  it('no Terminal and no Help place, at any width; a terminal or help message does nothing', async () => {
    for (const width of [375, 1280]) {
      const { w, send, navs } = await page('?embed=1&theme=dark', { width });
      const ids = navs().at(-1)!.places.map((p: any) => p.id);
      expect(ids, String(width)).not.toContain('terminal');
      expect(ids, String(width)).not.toContain('help');
      await send({ type: 'yawble.navigate', to: { kind: 'place', id: 'terminal' } });
      await send({ type: 'yawble.navigate', to: { kind: 'place', id: 'help' } });
      expect(w.find('[data-test=terminal-pane]').exists()).toBe(false);
      expect(w.find('[data-test=about]').exists()).toBe(false);
      w.unmount();
    }
  });

  it('the phone drawer (not embedded) has no Terminal; About is still there', async () => {
    const { w } = await page('', { width: 375 });
    const items = w.findAll('[data-test=drawer-menu-items] button').map((b) => b.attributes('data-test'));
    expect(items).not.toContain('drawer-terminal');
    expect(items).toContain('drawer-about');
  });

  it('Rename asks for the title in the frame and renames through the sidebar\'s handler', async () => {
    const { send, calls } = await page('?embed=1');
    await send({ type: 'yawble.navigate', to: { kind: 'chatAction', id: 'c1', action: 'rename' } });
    const input = document.querySelector<HTMLInputElement>('[data-test=embed-rename-input]')!;
    expect(input.value).toBe('First chat');
    input.value = '  Renamed  ';
    input.dispatchEvent(new Event('input'));
    await flushPromises();
    document.querySelector<HTMLFormElement>('[data-test=embed-rename]')!.dispatchEvent(new Event('submit'));
    await flushPromises();
    expect(calls.find((c) => c.method === 'concierge.rename')?.params).toEqual({ id: 'c1', title: 'Renamed' });
    expect(document.querySelector('[data-test=embed-rename]')).toBeNull();
  });

  it('?theme= sets the theme at load and theme messages set data-theme; yawble.remoteTheme is never read or written', async () => {
    const get = vi.spyOn(Storage.prototype, 'getItem');
    const set = vi.spyOn(Storage.prototype, 'setItem');
    const { send } = await page('?embed=1&theme=dark', { stored: 'light' });
    const html = document.documentElement;
    expect(html.getAttribute('data-theme')).toBe('dark');
    await send({ type: 'yawble.theme', theme: 'light' });
    expect(html.getAttribute('data-theme')).toBe('light');
    await send({ type: 'yawble.theme', theme: 'dark' });
    expect(html.getAttribute('data-theme')).toBe('dark');
    await send({ type: 'yawble.theme', theme: 'purple' }); // not a theme: ignored
    expect(html.getAttribute('data-theme')).toBe('dark');
    // From elsewhere (another window, another origin): ignored.
    await send({ type: 'yawble.theme', theme: 'light' }, {});
    await send({ type: 'yawble.theme', theme: 'light' }, window.parent, 'https://elsewhere.example');
    expect(html.getAttribute('data-theme')).toBe('dark');
    expect(get.mock.calls.filter(([k]) => k === REMOTE_THEME_KEY)).toEqual([]);
    expect(set.mock.calls.filter(([k]) => k === REMOTE_THEME_KEY)).toEqual([]);
    expect(localStorage.getItem(REMOTE_THEME_KEY)).toBe('light'); // the viewer's own choice, untouched
    get.mockRestore(); set.mockRestore();
  });

  it('without embed=1 nothing changes: the sidebar, the theme switch and the gear; nothing is posted', async () => {
    const { w, posted } = await page('', { width: 1280, stored: 'dark' });
    expect(w.find('[data-test=sidebar]').exists()).toBe(true);
    expect(w.find('[data-test=theme-toggle]').exists()).toBe(true);
    expect(w.find('[data-test=settings-gear]').exists()).toBe(true);
    expect(w.get('.app').classes()).not.toContain('embed');
    expect(w.get('.app').attributes('data-embed')).toBeUndefined();
    expect(document.documentElement.getAttribute('data-theme')).toBe('dark'); // the kept remote theme
    expect(posted).toEqual([]);
    await w.get('[data-test="menu-view"]').trigger('click');
    expect(w.find('[data-test="menuitem-theme:dark"]').exists()).toBe(true);
    expect(w.find('[data-test="menuitem-sidebar"]').exists()).toBe(true);
  });

  it('without embed=1 at 375px the phone keeps its drawer and menu button', async () => {
    const { w } = await page('', { width: 375 });
    expect(w.find('[data-test=drawer-toggle]').exists()).toBe(true);
    expect(w.find('[data-test=sidebar]').exists()).toBe(true);
  });
});

describe('theme.ts in embed mode (pageThemeEnv)', () => {
  const win = (search: string, remote = true) => ({
    location: { search }, yawble: remote ? { remote: true } : {}, document,
    matchMedia: () => ({ matches: true, addEventListener() {}, removeEventListener() {} }),
    addEventListener: vi.fn(), removeEventListener: vi.fn(),
  }) as unknown as Window & typeof globalThis;

  it('embed: no storage and no storage events; ?theme= is the starting mode', () => {
    const env = pageThemeEnv(win('?embed=1&theme=light'));
    expect(env.storage).toBeUndefined();
    expect(env.events).toBeUndefined();
    expect(env.initialMode).toBe('light');
    expect(pageThemeEnv(win('?embed=1&theme=dark')).initialMode).toBe('dark');
    expect(pageThemeEnv(win('?embed=1&theme=purple')).initialMode).toBe('system');
    expect(pageThemeEnv(win('?embed=1')).initialMode).toBe('system');
  });

  it('embed: setMode shows the theme and keeps nothing', () => {
    const spies = (['getItem', 'setItem', 'removeItem'] as const).map((m) => vi.spyOn(Storage.prototype, m));
    const root = document.createElement('html');
    const t = createTheme({ ...pageThemeEnv(win('?embed=1&theme=dark')), root });
    expect(t.state).toEqual({ mode: 'dark', current: 'dark' });
    expect(root.getAttribute('data-theme')).toBe('dark');
    t.setMode('light');
    t.toggle();
    t.setMode('system');
    expect(root.getAttribute('data-theme')).toBe('dark'); // the (fake) OS
    expect(spies.every((s) => s.mock.calls.length === 0)).toBe(true);
    for (const s of spies) s.mockRestore();
  });

  it('not embedded: storage, storage events and the remote key, as before (embed=0, theme= alone change nothing)', () => {
    for (const s of ['', '?embed=0&theme=dark', '?theme=dark']) {
      const env = pageThemeEnv(win(s));
      expect(typeof env.storage, s).toBe('function');
      expect(env.events, s).toBeDefined();
      expect(env.key, s).toBe(REMOTE_THEME_KEY);
      expect(env.initialMode, s).toBeUndefined();
    }
  });
});

describe('theme-boot.js in embed mode', () => {
  const source = readFileSync(join(resolve(__dirname, '../../src/renderer'), 'public/theme-boot.js'), 'utf8');
  function boot(search: string | null, stored: Record<string, string>, dark = false) {
    const attrs = new Map<string, string>();
    const reads: string[] = [];
    const root = { setAttribute: (n: string, v: string) => void attrs.set(n, v), style: { colorScheme: '' } };
    const doc = { documentElement: root, currentScript: { getAttribute: (n: string) => (n === 'data-key' ? REMOTE_THEME_KEY : null) } };
    const w: Record<string, unknown> = {
      matchMedia: () => ({ matches: dark }),
      localStorage: { getItem: (k: string) => { reads.push(k); return stored[k] ?? null; } },
    };
    if (search !== null) w.location = { search };
    new Function('document', 'window', source)(doc, w);
    return { theme: attrs.get('data-theme'), reads };
  }

  it('embed=1: ?theme= is shown, else the OS; the kept remote theme is not read', () => {
    expect(boot('?embed=1&theme=dark', { [REMOTE_THEME_KEY]: 'light' })).toEqual({ theme: 'dark', reads: [] });
    expect(boot('?embed=1&theme=light', { [REMOTE_THEME_KEY]: 'dark' }, true)).toEqual({ theme: 'light', reads: [] });
    expect(boot('?embed=1', { [REMOTE_THEME_KEY]: 'light' }, true)).toEqual({ theme: 'dark', reads: [] });
    expect(boot('?embed=1&theme=purple', { [REMOTE_THEME_KEY]: 'dark' }, false)).toEqual({ theme: 'light', reads: [] });
  });

  it('not embedded: the kept choice as before (also with no location at all)', () => {
    expect(boot('?theme=dark', { [REMOTE_THEME_KEY]: 'light' })).toEqual({ theme: 'light', reads: [REMOTE_THEME_KEY] });
    expect(boot('', { [REMOTE_THEME_KEY]: 'dark' })).toEqual({ theme: 'dark', reads: [REMOTE_THEME_KEY] });
    expect(boot(null, { [REMOTE_THEME_KEY]: 'dark' })).toEqual({ theme: 'dark', reads: [REMOTE_THEME_KEY] });
  });
});
