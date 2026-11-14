// @vitest-environment happy-dom
// B029 verify (card 142): the theme switch as the whole window uses it. App.vue mounted with a fake API and a faked
// prefers-color-scheme: the toggle sits right of the Cloud Link icon, one click flips <html data-theme> and is kept,
// View > Theme drives the same state (and "Use system setting" follows the OS again), and a remote view shows the
// toggle without the Cloud Link icon and keeps its own choice. Fakes only.
import { flushPromises, mount } from '@vue/test-utils';
import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { YawbleApi } from '../../src/shared/api';

function fakeOs(dark: boolean) {
  const listeners = new Set<() => void>();
  const media = { matches: dark, addEventListener: (_: string, l: () => void) => listeners.add(l), removeEventListener: (_: string, l: () => void) => listeners.delete(l) };
  return { matchMedia: () => media as any, set(d: boolean) { media.matches = d; for (const l of listeners) l(); } };
}

function api(remote = false): YawbleApi {
  return {
    ...(remote ? { remote: true } : { platform: 'win32' }),
    getInfo: async () => { throw new Error('no'); },
    getAvailableBackends: async () => [],
    call: async (method: string) => {
      switch (method) {
        case 'app.overview': return { slots: { running: 0, limit: 8, queued: 0 }, teams: [] } as any;
        case 'link.status': return { state: 'off', viewers: [] } as any;
        default: return [] as any;
      }
    },
    onEvent: () => () => {},
    openTerminal: async () => {},
  } as unknown as YawbleApi;
}

const html = () => document.documentElement;

/** A fresh page: the theme module is set up on import, so it is imported after the OS and window.yawble are faked. */
async function page(opts: { osDark: boolean; remote?: boolean; stored?: Record<string, string> }) {
  const os = fakeOs(opts.osDark);
  vi.stubGlobal('matchMedia', os.matchMedia);
  for (const [k, v] of Object.entries(opts.stored ?? {})) localStorage.setItem(k, v);
  const setTheme = vi.fn();
  (window as any).yawble = opts.remote ? { remote: true } : { setTheme };
  const App = (await import('../../src/renderer/src/App.vue')).default;
  const w = mount(App, { props: { api: api(opts.remote), pollMs: 60_000 }, attachTo: document.body });
  await flushPromises();
  return { w, os, setTheme };
}

async function menu(w: Awaited<ReturnType<typeof page>>['w'], id: string) {
  await w.get('[data-test="menu-view"]').trigger('click');
  await w.get(`[data-test="menuitem-${id}"]`).trigger('click');
  await flushPromises();
}

describe('B029 theme switch in the window (App.vue)', () => {
  beforeEach(() => { vi.resetModules(); localStorage.clear(); sessionStorage.clear(); html().removeAttribute('data-theme'); });
  afterEach(() => { document.body.innerHTML = ''; delete (window as any).yawble; vi.unstubAllGlobals(); });

  it('the toggle is the element right after the Cloud Link icon in the title bar', async () => {
    const { w } = await page({ osDark: false });
    const cloud = w.get('[data-test="cloud-link-indicator"]').element;
    expect(cloud.nextElementSibling?.getAttribute('data-test')).toBe('theme-toggle');
    expect(cloud.parentElement?.tagName).toBe('HEADER');
    w.unmount();
  });

  it('first load follows a dark OS, live; a click flips it to light, keeps it, and tells main', async () => {
    const { w, os, setTheme } = await page({ osDark: true });
    expect(html().getAttribute('data-theme')).toBe('dark');
    const btn = w.get('[data-test="theme-toggle"]');
    expect(btn.attributes('aria-label')).toBe('Switch to light theme');
    expect(w.find('[data-test="theme-icon-sun"]').exists()).toBe(true);
    os.set(false);
    await flushPromises();
    expect(html().getAttribute('data-theme')).toBe('light');
    expect(btn.attributes('title')).toBe('Switch to dark theme');
    await btn.trigger('click');
    expect(html().getAttribute('data-theme')).toBe('dark');
    expect(localStorage.getItem('yawble.theme')).toBe('dark');
    os.set(false);
    expect(html().getAttribute('data-theme')).toBe('dark'); // the choice holds, the OS no longer applies
    expect(setTheme.mock.calls.map((c) => c[0])).toEqual(['dark', 'light', 'dark']);
    w.unmount();
  });

  it('View > Theme: the radio shows the mode, Light/Dark pick it, Use system setting follows the OS again', async () => {
    const { w, os } = await page({ osDark: false, stored: { 'yawble.theme': 'dark' } });
    expect(html().getAttribute('data-theme')).toBe('dark');
    await w.get('[data-test="menu-view"]').trigger('click');
    expect(w.get('[data-test="menuitem-theme:dark"]').attributes('aria-checked')).toBe('true');
    expect(w.get('[data-test="menuitem-theme:system"]').attributes('aria-checked')).toBe('false');
    expect(w.get('[data-test="menuitem-toggleTheme"]').text()).toContain('Toggle theme');
    await w.get('[data-test="menu-view"]').trigger('click'); // close
    await menu(w, 'theme:system');
    expect(localStorage.getItem('yawble.theme')).toBeNull();
    expect(html().getAttribute('data-theme')).toBe('light');
    os.set(true);
    expect(html().getAttribute('data-theme')).toBe('dark');
    await menu(w, 'toggleTheme');
    expect(html().getAttribute('data-theme')).toBe('light');
    expect(localStorage.getItem('yawble.theme')).toBe('light');
    await menu(w, 'theme:dark');
    expect(html().getAttribute('data-theme')).toBe('dark');
    expect(w.get('[data-test="theme-toggle"]').attributes('aria-label')).toBe('Switch to light theme');
    w.unmount();
  });

  it('blocked storage: the window still loads, follows the OS and a click still flips it', async () => {
    const blocked = { getItem: () => { throw new DOMException('blocked', 'SecurityError'); }, setItem: () => { throw new DOMException('blocked', 'SecurityError'); }, removeItem: () => { throw new DOMException('blocked', 'SecurityError'); }, clear: () => {} };
    vi.stubGlobal('localStorage', blocked);
    const { w } = await page({ osDark: true });
    expect(html().getAttribute('data-theme')).toBe('dark');
    await w.get('[data-test="theme-toggle"]').trigger('click');
    expect(html().getAttribute('data-theme')).toBe('light');
    w.unmount();
  });

  it('a remote view has the toggle without the Cloud Link icon, and its own choice', async () => {
    const { w } = await page({ osDark: false, remote: true, stored: { 'yawble.theme': 'dark' } });
    expect(w.find('[data-test="cloud-link-indicator"]').exists()).toBe(false);
    expect(html().getAttribute('data-theme')).toBe('light'); // the desktop's choice is not this viewer's
    await w.get('[data-test="theme-toggle"]').trigger('click');
    expect(localStorage.getItem('yawble.remoteTheme')).toBe('dark');
    expect(localStorage.getItem('yawble.theme')).toBe('dark');
    w.unmount();
  });
});

describe('B029 toggle looks like the Cloud Link icon', () => {
  it('same padding, radius, colour and hover as .cloud-ind', () => {
    const src = (rel: string) => readFileSync(resolve(__dirname, '../../src/renderer/src', rel), 'utf8');
    const rule = (text: string, sel: string) => new RegExp(`\\${sel}\\s*\\{([^}]*)\\}`).exec(text)?.[1] ?? '';
    const decl = (body: string, prop: string) => new RegExp(`(?:^|[;\\s])${prop}\\s*:\\s*([^;]+)`).exec(body)?.[1].trim();
    const app = src('App.vue'); const toggle = src('components/ThemeToggle.vue');
    for (const p of ['padding', 'border-radius', 'color']) {
      expect(decl(rule(toggle, '.theme-toggle'), p), p).toBe(decl(rule(app, '.cloud-ind'), p));
    }
    expect(rule(toggle, '.theme-toggle:hover')).toBe(rule(app, '.cloud-ind:hover'));
  });
});
