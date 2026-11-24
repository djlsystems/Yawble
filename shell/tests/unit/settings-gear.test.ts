// @vitest-environment happy-dom
// B115 Part A (card 516): Settings is a gear at the top right, the element right after the theme switch in the title
// bar, in the desktop window, the remote view and the phone top bar. A click opens Settings; Ctrl+, and File > Settings
// still do. The sidebar (wide and folded) has no Settings item any more, and its run-slots line stays at the bottom.
// App.vue mounted with a fake API. Fakes only.
import { flushPromises, mount } from '@vue/test-utils';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { YawbleApi } from '../../src/shared/api';

function api(remote = false): YawbleApi {
  return {
    ...(remote ? { remote: true } : { platform: 'win32' }),
    getInfo: async () => { throw new Error('no'); },
    getAvailableBackends: async () => [],
    call: async (method: string) => {
      switch (method) {
        case 'app.overview': return { slots: { running: 1, limit: 8, queued: 2 }, teams: [] } as any;
        case 'link.status': return { state: 'off', viewers: [] } as any;
        default: return [] as any;
      }
    },
    onEvent: () => () => {},
    openTerminal: async () => {},
  } as unknown as YawbleApi;
}

/** The window's width as phone.ts sees it (its max-width media query); every other query (the OS theme) is false. */
function setWidth(w: number) {
  vi.stubGlobal('matchMedia', (q: string) => ({ matches: q.includes('max-width') && w < 760, addEventListener: () => {}, removeEventListener: () => {} }));
}

async function page(opts: { remote?: boolean; width?: number } = {}) {
  setWidth(opts.width ?? 1280);
  (window as any).yawble = opts.remote ? { remote: true } : { setTheme: vi.fn() };
  const App = (await import('../../src/renderer/src/App.vue')).default;
  const w = mount(App, { props: { api: api(opts.remote), pollMs: 60_000 }, attachTo: document.body });
  await flushPromises();
  return w;
}

describe('B115 Part A: the Settings gear at the top right (App.vue)', { timeout: 30_000 }, () => {
  beforeEach(() => { vi.resetModules(); localStorage.clear(); sessionStorage.clear(); });
  afterEach(() => { document.body.innerHTML = ''; delete (window as any).yawble; vi.unstubAllGlobals(); });

  for (const [name, opts] of [['desktop window', {}], ['remote view', { remote: true }], ['phone remote view', { remote: true, width: 375 }], ['phone window', { width: 375 }]] as const) {
    it(`${name}: the gear is the last element of the title bar, right after the theme switch, named Settings`, async () => {
      const w = await page(opts);
      const gear = w.get('[data-testid="settings-gear"]');
      expect(gear.element.parentElement?.tagName).toBe('HEADER');
      expect(gear.element.previousElementSibling?.getAttribute('data-test')).toBe('theme-toggle');
      expect(gear.element.nextElementSibling).toBeNull();
      expect(gear.attributes('title')).toBe('Settings');
      expect(gear.attributes('aria-label')).toBe('Settings');
      expect(gear.find('svg').exists()).toBe(true);
      expect(w.find('[data-test=open-app-settings]').exists()).toBe(false);
      expect(w.get('[data-test=slots]').text()).toBe('Run slots: 1 / 8 in use · 2 queued');
      w.unmount();
    });
  }

  it('a click opens Settings, and the gear shows it is open', async () => {
    const w = await page();
    expect(w.find('[data-test=settings]').exists()).toBe(false);
    await w.get('[data-testid="settings-gear"]').trigger('click');
    await flushPromises();
    expect(w.find('[data-test=settings]').exists()).toBe(true);
    expect(w.get('[data-testid="settings-gear"]').classes()).toContain('on');
    expect(w.get('[data-testid="settings-gear"]').attributes('aria-current')).toBe('page');
    await w.get('[data-test=nav-concierge]').trigger('click');
    await flushPromises();
    expect(w.find('[data-test=settings]').exists()).toBe(false);
    expect(w.get('[data-testid="settings-gear"]').classes()).not.toContain('on');
    w.unmount();
  });

  it('Ctrl+, and File > Settings still open Settings', async () => {
    const w = await page();
    window.dispatchEvent(new KeyboardEvent('keydown', { key: ',', ctrlKey: true }));
    await flushPromises();
    expect(w.find('[data-test=settings]').exists()).toBe(true);
    await w.get('[data-test=nav-concierge]').trigger('click');
    await flushPromises();
    expect(w.find('[data-test=settings]').exists()).toBe(false);
    await w.get('[data-test="menu-file"]').trigger('click');
    await w.get('[data-test="menuitem-settings"]').trigger('click');
    await flushPromises();
    expect(w.find('[data-test=settings]').exists()).toBe(true);
    w.unmount();
  });

  it('on a phone the gear opens Settings with the drawer closed (no menu to open first)', async () => {
    const w = await page({ remote: true, width: 375 });
    expect(w.get('[data-test=sidebar]').attributes('data-drawer')).toBe('closed');
    await w.get('[data-testid="settings-gear"]').trigger('click');
    await flushPromises();
    expect(w.get('[data-test=settings]').attributes('data-phone-screen')).toBe('list');
    w.unmount();
  });

  it('the folded sidebar (the rail) has no Settings button either', async () => {
    const w = await page();
    await w.get('[data-test=sidebar-collapse]').trigger('click');
    await flushPromises();
    expect(w.find('[data-test=sidebar-rail]').exists()).toBe(true);
    expect(w.find('[data-test=rail-settings]').exists()).toBe(false);
    expect(w.find('[data-testid="settings-gear"]').exists()).toBe(true);
    w.unmount();
  });
});
