// @vitest-environment happy-dom
import { flushPromises, mount } from '@vue/test-utils';
import { afterEach, describe, expect, it } from 'vitest';
import UpdateBanner from '../../src/renderer/src/components/UpdateBanner.vue';
import UpdatesSection from '../../src/renderer/src/components/UpdatesSection.vue';
import type { UpdateStatus, YawbleApi } from '../../src/shared/api';

// A fake core and window only: nothing is downloaded, installed or run.
const status = (state: UpdateStatus['state'], extra: Partial<UpdateStatus> = {}): UpdateStatus => ({
  current: '2026.11.12.1', state, latest: '2026.11.20.1', available: state !== 'upToDate', notesUrl: 'https://github.com/djlsystems/Yawble/releases/tag/v2026.11.20.1',
  canInstall: true, auto: true, checkedAt: '2026-11-13T12:00:00Z', error: null, downloaded: 0, size: 0, ...extra,
});

function fakeApi(answers: Record<string, (p: any) => unknown>, opts: { remote?: boolean; packaged?: boolean; running?: number } = {}) {
  const calls: string[] = [];
  let installed = 0;
  const api: YawbleApi = {
    getInfo: async () => { throw new Error('no'); },
    getAvailableBackends: async () => [],
    call: async (method: string, params?: Record<string, unknown>) => {
      calls.push(method);
      if (method === 'app.overview') return { slots: { running: opts.running ?? 0, limit: 4, queued: 0 }, teams: [] } as any;
      const a = answers[method];
      if (!a) throw new Error(`unexpected ${method}`);
      return a(params) as any;
    },
    onEvent: () => () => {},
    openTerminal: async () => {},
    pickFolder: async () => null,
    ...(opts.remote ? { remote: true } : { shellSettings: async () => ({ runInBackground: false, startAtLogin: false, packaged: opts.packaged ?? true }), installUpdate: async () => { installed++; } }),
  };
  return { api, calls, installs: () => installed };
}

afterEach(() => { try { localStorage.clear(); } catch { /* none */ } });

describe('UpdateBanner', () => {
  it('ReadyToInstall_RestartsToUpdate', async () => {
    const f = fakeApi({ 'updates.status': () => status('ready') });
    const w = mount(UpdateBanner, { props: { api: f.api } });
    await flushPromises();
    expect(w.find('[data-test=update-banner]').text()).toContain('Yawble 2026.11.20.1 is ready to install');
    expect(w.find('[data-test=update-notes]').attributes('href')).toContain('/tag/v2026.11.20.1');
    await w.find('[data-test=update-install]').trigger('click');
    await flushPromises();
    expect(f.installs()).toBe(1);
  });

  it('WhileAgentsWork_AsksBeforeRestarting', async () => {
    const f = fakeApi({ 'updates.status': () => status('ready') }, { running: 2 });
    const w = mount(UpdateBanner, { props: { api: f.api } });
    await flushPromises();
    await w.find('[data-test=update-install]').trigger('click');
    await flushPromises();
    expect(w.find('[data-test=update-confirm]').text()).toContain('2 agents are working');
    expect(f.installs()).toBe(0);
    await w.find('[data-test=update-anyway]').trigger('click');
    await flushPromises();
    expect(f.installs()).toBe(1);
  });

  it('AvailableWithoutAutomaticDownload_OffersDownload_AndLaterHidesThatVersion', async () => {
    let s = status('available', { auto: false });
    const f = fakeApi({ 'updates.status': () => s, 'updates.download': () => (s = status('ready')) });
    const w = mount(UpdateBanner, { props: { api: f.api } });
    await flushPromises();
    await w.find('[data-test=update-download]').trigger('click');
    await flushPromises();
    expect(f.calls).toContain('updates.download');
    expect(w.find('[data-test=update-install]').exists()).toBe(true);
    await w.find('[data-test=update-later]').trigger('click');
    expect(w.find('[data-test=update-banner]').exists()).toBe(false);
  });

  it('ShowsDownloadProgress', async () => {
    const f = fakeApi({ 'updates.status': () => status('downloading', { downloaded: 50, size: 200 }) });
    const w = mount(UpdateBanner, { props: { api: f.api } });
    await flushPromises();
    expect(w.find('[data-test=update-downloading]').text()).toContain('25%');
  });

  it('ADevelopmentRunShowsNothing_ACloudViewerIsOnlyTold', async () => {
    const dev = mount(UpdateBanner, { props: { api: fakeApi({ 'updates.status': () => status('ready') }, { packaged: false }).api } });
    await flushPromises();
    expect(dev.find('[data-test=update-banner]').exists()).toBe(false);

    const viewer = mount(UpdateBanner, { props: { api: fakeApi({ 'updates.status': () => status('ready') }, { remote: true }).api } });
    await flushPromises();
    expect(viewer.find('[data-test=update-banner]').text()).toContain('installed on the computer itself');
    expect(viewer.find('[data-test=update-install]').exists()).toBe(false);
  });

  it('CheckNow_SaysWhenThisIsTheLatest', async () => {
    const f = fakeApi({ 'updates.status': () => status('upToDate', { latest: '2026.11.12.1' }), 'updates.check': () => status('upToDate', { latest: '2026.11.12.1' }) });
    const w = mount(UpdateBanner, { props: { api: f.api } });
    await flushPromises();
    await (w.vm as any).checkNow();
    await flushPromises();
    expect(w.find('[data-test=update-told]').text()).toContain('2026.11.12.1 is the latest version');
  });

  it('AnOlderCoreWithoutUpdatesShowsNothing', async () => {
    const w = mount(UpdateBanner, { props: { api: fakeApi({}).api } });
    await flushPromises();
    expect(w.html()).not.toContain('update-banner');
  });
});

describe('Settings > General, Updates', () => {
  it('ShowsTheVersion_TurnsAutomaticChecksOff_AndChecksNow', async () => {
    let s = status('upToDate', { latest: '2026.11.12.1' });
    const params: any[] = [];
    const f = fakeApi({
      'updates.status': () => s,
      'updates.setAuto': (p) => { params.push(p); s = { ...s, auto: p.on }; return s; },
      'updates.check': () => (s = status('available')),
    });
    const w = mount(UpdatesSection, { props: { api: f.api } });
    await flushPromises();
    expect(w.find('[data-test=updates-line]').text()).toContain('Yawble 2026.11.12.1. This is the latest version.');
    await w.find('[data-test=updates-auto]').setValue(false);
    await flushPromises();
    expect(params).toEqual([{ on: false }]);
    await w.find('[data-test=updates-check]').trigger('click');
    await flushPromises();
    expect(w.find('[data-test=updates-line]').text()).toContain('Yawble 2026.11.20.1 is available.');
  });

  it('FollowsTheAutomaticCheckWhileOpen', async () => {
    let s = status('upToDate', { latest: '2026.11.12.1', checkedAt: null });
    const w = mount(UpdatesSection, { props: { api: fakeApi({ 'updates.status': () => s }).api, pollMs: 10 } });
    await flushPromises();
    expect(w.find('[data-test=updates-line]').text()).toContain('Yawble 2026.11.12.1.');
    s = status('downloading'); // the automatic check found a newer version meanwhile
    await new Promise((r) => setTimeout(r, 40));
    await flushPromises();
    expect(w.find('[data-test=updates-line]').text()).toContain('Downloading Yawble 2026.11.20.1');
    w.unmount();
  });
});
