// @vitest-environment happy-dom
import { flushPromises, mount } from '@vue/test-utils';
import { describe, expect, it, vi } from 'vitest';
import AboutSection from '../../src/renderer/src/components/AboutSection.vue';
import type { AppVersion, CoreEvent, YawbleApi } from '../../src/shared/api';

// Settings > System > About: the core's app.version, the cloud's while linked, and the four links. A fake API: no
// core, no network, no browser (the links are only checked, never followed).
function fakeApi(answer: () => Promise<AppVersion>, extra: Partial<YawbleApi> = {}) {
  const listeners = new Set<(e: CoreEvent) => void>();
  const calls: string[] = [];
  const api: YawbleApi = {
    getInfo: async () => { throw new Error('not used'); },
    getAvailableBackends: async () => [],
    call: (async (method: string) => { calls.push(method); return answer(); }) as YawbleApi['call'],
    onEvent: (l) => { listeners.add(l); return () => { listeners.delete(l); }; },
    openTerminal: async () => {},
    pickFolder: async () => null,
    ...extra,
  };
  return { api, calls, push: (e: CoreEvent) => { for (const l of listeners) l(e); } };
}

const dev: AppVersion = { version: '2026.11.12.1+dev.ee55691', commit: 'ee556914e714c2dc22dfec7a6b5bfbcd30627e70', dirty: false, released: false, cloudVersion: null };
const SHAPE = /^(\d{4}\.\d{2}\.\d{2}\.\d+|(\d{4}\.\d{2}\.\d{2}\.\d+|0\.0\.0)\+dev\.([0-9a-f]{7}(\.dirty)?|unknown))$/;

describe('About (Settings > System)', () => {
  it('shows Yawble Desktop, the version, the commit and that it is a development build', async () => {
    const f = fakeApi(async () => dev);
    const w = mount(AboutSection, { props: { api: f.api } });
    await flushPromises();
    expect(f.calls).toEqual(['app.version']);
    expect(w.text()).toContain('Yawble Desktop');
    expect(w.get('[data-test=about-version]').text()).toBe('2026.11.12.1+dev.ee55691');
    expect(w.get('[data-test=about-kind]').text()).toBe('development build');
    expect(w.get('[data-test=about-commit]').text()).toBe('ee556914e714');
    expect(w.find('[data-test=about-cloud]').exists()).toBe(false);
  });

  it('a release says release; a dirty build says so', async () => {
    let answer: AppVersion = { version: '2026.11.12.1', commit: dev.commit, dirty: false, released: true };
    const f = fakeApi(async () => answer);
    const w = mount(AboutSection, { props: { api: f.api } });
    await flushPromises();
    expect(w.get('[data-test=about-kind]').text()).toBe('release');
    answer = { ...dev, version: `${dev.version}.dirty`, dirty: true };
    f.push({ event: 'link.state', data: null });
    await flushPromises();
    expect(w.get('[data-test=about-kind]').text()).toBe('development build (uncommitted changes)');
  });

  it("shows the cloud's version while the Cloud Link is connected, and drops it when it is not", async () => {
    let answer: AppVersion = { ...dev, cloudVersion: '2026.11.12.1' };
    const f = fakeApi(async () => answer);
    const w = mount(AboutSection, { props: { api: f.api } });
    await flushPromises();
    expect(w.get('[data-test=about-cloud]').text()).toBe('Cloud: 2026.11.12.1');
    answer = { ...dev, cloudVersion: null };
    f.push({ event: 'link.state', data: { state: 'offline' } });
    await flushPromises();
    expect(w.find('[data-test=about-cloud]').exists()).toBe(false);
  });

  it('Copy puts the version on the clipboard', async () => {
    const writeText = vi.fn(async (_text: string) => {});
    Object.defineProperty(navigator, 'clipboard', { value: { writeText }, configurable: true });
    const f = fakeApi(async () => dev);
    const w = mount(AboutSection, { props: { api: f.api } });
    await flushPromises();
    await w.get('[data-test=about-copy]').trigger('click');
    await flushPromises();
    expect(writeText).toHaveBeenCalledWith('2026.11.12.1+dev.ee55691');
    expect(w.get('[data-test=about-copy]').text()).toBe('Copied');
  });

  it('links to the website, GitHub, YouTube and X, opened outside the app, each with an icon', async () => {
    const f = fakeApi(async () => dev);
    const w = mount(AboutSection, { props: { api: f.api } });
    await flushPromises();
    const links = w.findAll('[data-test=about-links] a');
    expect(links.map((a) => [a.text(), a.attributes('href'), a.attributes('target')])).toEqual([
      ['Website', 'https://yawble.ai/', '_blank'],
      ['GitHub', 'https://github.com/djlsystems/Yawble', '_blank'],
      ['YouTube', 'https://www.youtube.com/@Yawble', '_blank'],
      ['X', 'https://x.com/_Yawble', '_blank'],
    ]);
    for (const a of links) expect(a.find('svg').exists()).toBe(true);
  });

  it("an older core without app.version: the window shows its own build's version", async () => {
    const f = fakeApi(async () => { throw new Error("no method named 'app.version'"); });
    const w = mount(AboutSection, { props: { api: f.api } });
    await flushPromises();
    expect(w.get('[data-test=about-version]').text()).toMatch(SHAPE);
  });

  it("a remote view never shows its own build as the computer's: it says unknown", async () => {
    const f = fakeApi(async () => { throw new Error('remote_refused'); }, { remote: true });
    const w = mount(AboutSection, { props: { api: f.api } });
    await flushPromises();
    expect(w.get('[data-test=about-version]').text()).toBe('unknown');
    expect(w.find('[data-test=about-copy]').exists()).toBe(false);
  });
});
