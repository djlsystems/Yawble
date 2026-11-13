// @vitest-environment happy-dom
import { flushPromises, mount } from '@vue/test-utils';
import { describe, expect, it } from 'vitest';
import GitBanner from '../../src/renderer/src/components/GitBanner.vue';
import type { GitStatus, YawbleApi } from '../../src/shared/api';

// A fake API only: no core, nothing installed. Each method answers from `answers`; anything else fails the call.
function fakeApi(answers: Record<string, (params: any) => unknown>, remote = false) {
  const calls: string[] = [];
  const api: YawbleApi = {
    getInfo: async () => { throw new Error('no'); },
    getAvailableBackends: async () => [],
    call: async (method: string, params?: Record<string, unknown>) => {
      calls.push(method);
      const answer = answers[method];
      if (!answer) throw new Error(`unexpected ${method}`);
      return answer(params) as any;
    },
    onEvent: () => () => {},
    openTerminal: async () => {},
    pickFolder: async () => null,
    ...(remote ? { remote: true } : {}),
  };
  return { api, calls };
}

const status = (state: GitStatus['state'], extra: Partial<GitStatus> = {}): GitStatus => ({
  state, version: state === 'missing' ? null : '2.45.1', path: state === 'missing' ? null : 'C:\\Git\\cmd\\git.exe', minimum: '2.38',
  canInstall: true, detail: null, downloadUrl: 'https://git-scm.com/downloads', ...extra,
});

describe('GitBanner', () => {
  it('ShowsNothingWhenGitIsFine', async () => {
    const { api } = fakeApi({ 'git.check': () => status('ok') });
    const w = mount(GitBanner, { props: { api } });
    await flushPromises();
    expect(w.find('[data-test=git-banner]').exists()).toBe(false);
  });

  it('MissingGit_SaysTeamsNeedIt_InstallsIt_AndTheBannerGoes', async () => {
    let installed = false;
    const { api, calls } = fakeApi({
      'git.check': () => status(installed ? 'ok' : 'missing'),
      'git.install': () => { installed = true; return { done: true, message: 'git 2.56.0 is installed (C:\\Git\\cmd\\git.exe).', status: status('ok') }; },
    });
    const w = mount(GitBanner, { props: { api } });
    await flushPromises();
    const banner = w.find('[data-test=git-banner]');
    expect(banner.attributes('data-state')).toBe('missing');
    expect(banner.text()).toContain('teams need git 2.38 or later');
    expect(banner.text()).toContain('Any repository host works, or none');
    expect(w.find('[data-test=git-download]').attributes('href')).toBe('https://git-scm.com/downloads');
    expect(w.find('[data-test=git-install]').text()).toBe('Install Git');

    await w.find('[data-test=git-install]').trigger('click');
    await flushPromises();
    expect(calls).toEqual(['git.check', 'git.install']);
    expect(w.find('[data-test=git-banner]').exists()).toBe(false);
    expect(w.find('[data-test=git-done]').text()).toContain('git 2.56.0 is installed');
  });

  it('AnOldGitOffersAnUpdate_AndAFailedInstallSaysWhy', async () => {
    const { api } = fakeApi({
      'git.check': () => status('old', { version: '2.30.1' }),
      'git.install': () => ({ done: false, message: 'Updating Git for Windows did not finish (winget exit code 1).', status: status('old', { version: '2.30.1' }) }),
    });
    const w = mount(GitBanner, { props: { api } });
    await flushPromises();
    expect(w.find('[data-test=git-banner]').text()).toContain('Git 2.30.1 is too old');
    expect(w.find('[data-test=git-install]').text()).toBe('Update Git');
    await w.find('[data-test=git-install]').trigger('click');
    await flushPromises();
    expect(w.find('[data-test=git-error]').text()).toContain('did not finish');
    expect(w.find('[data-test=git-banner]').exists()).toBe(true);
  });

  it('NoInstallButtonWhereTheComputerCannotInstall_OrForACloudViewer', async () => {
    const cannot = fakeApi({ 'git.check': () => status('missing', { canInstall: false }) });
    const a = mount(GitBanner, { props: { api: cannot.api } });
    await flushPromises();
    expect(a.find('[data-test=git-install]').exists()).toBe(false);
    expect(a.find('[data-test=git-download]').exists()).toBe(true);

    const viewer = fakeApi({ 'git.check': () => status('missing') }, true);
    const b = mount(GitBanner, { props: { api: viewer.api } });
    await flushPromises();
    expect(b.find('[data-test=git-install]').exists()).toBe(false);
    expect(b.find('[data-test=git-remote-note]').text()).toContain('on the computer itself');
  });

  it('CheckAgainAsksTheCoreAgain', async () => {
    let n = 0;
    const { api } = fakeApi({ 'git.check': () => status(n++ === 0 ? 'missing' : 'ok') });
    const w = mount(GitBanner, { props: { api } });
    await flushPromises();
    await w.find('[data-test=git-recheck]').trigger('click');
    await flushPromises();
    expect(w.find('[data-test=git-banner]').exists()).toBe(false);
  });

  it('AnOlderCoreWithoutTheCheckShowsNothing', async () => {
    const { api } = fakeApi({});
    const w = mount(GitBanner, { props: { api } });
    await flushPromises();
    expect(w.find('[data-test=git-banner]').exists()).toBe(false);
  });
});
