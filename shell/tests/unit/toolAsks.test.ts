// @vitest-environment happy-dom
import { flushPromises, mount } from '@vue/test-utils';
import { describe, expect, it } from 'vitest';
import ToolAskCard from '../../src/renderer/src/components/ToolAskCard.vue';
import ToolAsks from '../../src/renderer/src/components/ToolAsks.vue';
import type { CoreEvent, ToolAsk, YawbleApi } from '../../src/shared/api';

// A fake API only: no core, no winget. Each method answers from `answers`; anything else fails the call.
function fakeApi(answers: Record<string, (params: any) => unknown>, extra: Partial<YawbleApi> = {}) {
  const calls: { method: string; params: any }[] = [];
  const listeners: ((e: CoreEvent) => void)[] = [];
  const api: YawbleApi = {
    getInfo: async () => { throw new Error('no'); },
    getAvailableBackends: async () => [],
    call: async (method: string, params?: Record<string, unknown>) => {
      calls.push({ method, params });
      const answer = answers[method];
      if (!answer) throw new Error(`unexpected ${method}`);
      return answer(params) as any;
    },
    onEvent: (l) => { listeners.push(l); return () => { listeners.splice(listeners.indexOf(l), 1); }; },
    openTerminal: async () => {},
    pickFolder: async () => null,
    ...extra,
  };
  const emit = (event: string, data: unknown = null) => { for (const l of [...listeners]) l({ event, data }); };
  return { api, calls, emit };
}

const WINGET = 'winget install --id Microsoft.DotNet.SDK.10 --exact --source winget --silent --accept-package-agreements --accept-source-agreements --disable-interactivity';
const ask = (over: Partial<ToolAsk> = {}): ToolAsk => ({
  id: 4, at: '2026-11-13T21:00:00Z', teamId: 1, team: 'Alpha', member: 'Alpha/DeveloperInes', memberName: 'DeveloperInes',
  tool: 'dotnet-sdk-10', displayName: '.NET SDK 10', publisher: 'Microsoft', wingetId: 'Microsoft.DotNet.SDK.10', scope: 'machine',
  reason: 'build the API project', count: 2, lastSeen: '2026-11-13T21:05:00Z', workflow: 12, state: 'pending', attempts: 0, error: null,
  decidedBy: null, decidedVia: null, decidedAt: null, canInstall: true, command: WINGET, choices: ['install', 'notNow'], ...over,
});
const installed = { done: true, id: 4, tool: 'dotnet-sdk-10', message: '.NET SDK 10 is installed; runs that start from now on have it on PATH.', manual: false, closed: [4], told: ['Alpha/Manager'] };

describe('ToolAskCard', () => {
  it('Asking_ShowsMemberToolReasonPublisherWingetIdScopeCountAndCommand', () => {
    const { api } = fakeApi({});
    const w = mount(ToolAskCard, { props: { ask: ask(), api } });
    expect(w.get('[data-test=tool-ask-member]').text()).toBe('Developer Ines');
    expect(w.text()).toContain('Alpha');
    expect(w.get('[data-test=tool-ask-tool]').text()).toBe('.NET SDK 10');
    expect(w.get('[data-test=tool-ask-reason]').text()).toContain('build the API project');
    const details = w.get('[data-test=tool-ask-details]').text();
    expect(details).toContain('Microsoft');
    expect(details).toContain('Microsoft.DotNet.SDK.10');
    expect(w.get('[data-test=tool-ask-scope]').text()).toContain('Windows may ask for permission');
    expect(w.get('[data-test=tool-ask-count]').text()).toContain('2 times');
    expect(w.get('[data-test=tool-ask-command] code').text()).toBe(WINGET);
    expect(w.findAll('button:not(.discuss)').map((b) => b.text())).toEqual(['Install', 'Not now']);
    expect(w.attributes('data-state')).toBe('asking');
    expect(w.find('[data-test=tool-ask-remote]').exists()).toBe(false);
  });

  it('APerUserTool_SaysItInstallsForYouOnly_AndTheReasonIsPlainText', () => {
    const { api } = fakeApi({});
    const w = mount(ToolAskCard, { props: { ask: ask({ scope: 'user', displayName: 'Python 3', reason: '<b>run</b> the scripts', count: 1 }), api } });
    expect(w.get('[data-test=tool-ask-scope]').text()).toContain('for you only');
    expect(w.get('[data-test=tool-ask-scope]').text()).not.toContain('permission');
    expect(w.get('[data-test=tool-ask-reason]').text()).toContain('<b>run</b> the scripts');
    expect(w.find('[data-test=tool-ask-reason] b').exists()).toBe(false);
    expect(w.get('[data-test=tool-ask-count]').text()).toContain('once');
  });

  it('Installing_ShowsProgressAndHoldsTheButtons_ThenDoneEmitsInstalled', async () => {
    let finish: (v: unknown) => void = () => {};
    const { api, calls, emit } = fakeApi({ 'tools.install': () => new Promise((r) => { finish = r; }) });
    const w = mount(ToolAskCard, { props: { ask: ask(), api } });
    await w.get('[data-test=tool-ask-install]').trigger('click');
    await flushPromises();
    expect(calls).toEqual([{ method: 'tools.install', params: { id: 4 } }]);
    expect(w.attributes('data-state')).toBe('installing');
    expect(w.get('[data-test=tool-ask-install]').text()).toContain('Installing…');
    expect(w.findAll('button:not(.discuss)').every((b) => b.attributes('disabled') !== undefined)).toBe(true);
    expect(w.get('[data-test=tool-ask-progress]').text()).toContain('Installing .NET SDK 10');

    emit('tools.installProgress', { id: 99, tool: 'nodejs-lts', phase: 'installing', message: 'Installing Node.js LTS', command: '' });
    emit('tools.installProgress', { id: 4, tool: 'dotnet-sdk-10', phase: 'checking', message: 'Looking for dotnet again...', command: WINGET });
    await flushPromises();
    expect(w.get('[data-test=tool-ask-progress]').text()).toBe('Looking for dotnet again...');

    await w.get('[data-test=tool-ask-notNow]').trigger('click');
    expect(calls).toHaveLength(1);
    finish(installed);
    await flushPromises();
    expect(w.emitted('installed')?.[0]).toEqual([installed]);
    expect(w.emitted('answered')?.[0]).toEqual([installed]);
    expect(w.find('[data-test=tool-ask-error]').exists()).toBe(false);
  });

  it('AnAskInstallingInAnotherView_ShowsInstallingWithoutButtons', () => {
    const { api } = fakeApi({});
    const w = mount(ToolAskCard, { props: { ask: ask({ state: 'installing', attempts: 1 }), api } });
    expect(w.attributes('data-state')).toBe('installing');
    expect(w.get('[data-test=tool-ask-progress]').exists()).toBe(true);
    expect(w.findAll('button:not(.discuss)').every((b) => b.attributes('disabled') !== undefined)).toBe(true);
  });

  it('Failed_ShowsWingetsMessageAndTryAgain_WhichInstallsAgain', async () => {
    const failedMsg = 'Installing .NET SDK 10 did not finish (winget exit code 1: No applicable installer found).';
    const { api, calls } = fakeApi({ 'tools.install': () => ({ done: false, id: 4, tool: 'dotnet-sdk-10', message: failedMsg, manual: false }) });
    const w = mount(ToolAskCard, { props: { ask: ask({ error: failedMsg, attempts: 1 }), api } });
    expect(w.attributes('data-state')).toBe('failed');
    expect(w.get('[data-test=tool-ask-failed]').text()).toBe(failedMsg);
    expect(w.get('[data-test=tool-ask-install]').text()).toBe('Try again');
    await w.get('[data-test=tool-ask-install]').trigger('click');
    await flushPromises();
    expect(calls).toEqual([{ method: 'tools.install', params: { id: 4 } }]);
    expect(w.emitted('installed')).toBeUndefined();
    expect(w.emitted('answered')).toHaveLength(1);
    expect(w.get('[data-test=tool-ask-failed]').text()).toBe(failedMsg);
  });

  it('AnInstallThatDidNotRun_ShowsItsMessage', async () => {
    const { api } = fakeApi({ 'tools.install': () => ({ done: false, id: 4, tool: 'dotnet-sdk-10', message: 'Another tool is being installed; wait for it to finish, then try again.', manual: false }) });
    const w = mount(ToolAskCard, { props: { ask: ask(), api } });
    await w.get('[data-test=tool-ask-install]').trigger('click');
    await flushPromises();
    expect(w.get('[data-test=tool-ask-failed]').text()).toContain('Another tool is being installed');
  });

  it('NotNow_CallsToolsNotNowWithTheNote', async () => {
    const { api, calls } = fakeApi({ 'tools.notNow': (p) => ({ ok: true, id: p.id, tool: 'dotnet-sdk-10', told: 'Alpha/DeveloperInes' }) });
    const w = mount(ToolAskCard, { props: { ask: ask(), api } });
    await w.get('[data-test=tool-ask-note]').setValue('  use the SDK 9 for now  ');
    await w.get('[data-test=tool-ask-notNow]').trigger('click');
    await flushPromises();
    expect(calls).toEqual([{ method: 'tools.notNow', params: { id: 4, note: 'use the SDK 9 for now' } }]);
    expect(w.emitted('answered')).toHaveLength(1);
  });

  it('ARemoteView_HasNoInstall_SaysToAnswerItInTheWindow_AndCanSayNotNow', async () => {
    const { api, calls } = fakeApi({ 'tools.notNow': (p) => ({ ok: true, id: p.id, tool: 'dotnet-sdk-10', told: null }) }, { remote: true } as Partial<YawbleApi>);
    const w = mount(ToolAskCard, { props: { ask: ask({ error: 'winget failed' }), api } });
    expect(w.find('[data-test=tool-ask-install]').exists()).toBe(false);
    expect(w.findAll('button:not(.discuss)').map((b) => b.text())).toEqual(['Not now']);
    expect(w.get('[data-test=tool-ask-remote]').text()).toContain('Yawble window');
    await w.get('[data-test=tool-ask-notNow]').trigger('click');
    await flushPromises();
    expect(calls[0].method).toBe('tools.notNow');
  });

  it('OffWindows_ShowsTheCommandToRunAndOnlyNotNow', () => {
    const { api } = fakeApi({});
    const w = mount(ToolAskCard, { props: { ask: ask({ canInstall: false, command: 'sudo apt install dotnet-sdk-10.0', choices: ['notNow'], wingetId: 'Microsoft.DotNet.SDK.10' }), api } });
    expect(w.find('[data-test=tool-ask-install]').exists()).toBe(false);
    const cmd = w.get('[data-test=tool-ask-command]');
    expect(cmd.text()).toContain('Run this on the computer');
    expect(cmd.get('code').text()).toBe('sudo apt install dotnet-sdk-10.0');
    expect(w.find('[data-test=tool-ask-remote]').exists()).toBe(false);
  });

  it('ShowsTheCoresRefusal', async () => {
    const { api } = fakeApi({ 'tools.install': () => { throw new Error('forbidden: Install runs only from the Yawble window on this computer'); } });
    const w = mount(ToolAskCard, { props: { ask: ask(), api } });
    await w.get('[data-test=tool-ask-install]').trigger('click');
    await flushPromises();
    expect(w.get('[data-test=tool-ask-error]').text()).toContain('only from the Yawble window');
    expect(w.emitted('answered')).toBeUndefined();
    expect(w.findAll('button:not(.discuss)').every((b) => b.attributes('disabled') === undefined)).toBe(true);
  });
});

const PKEXEC = 'pkexec /usr/bin/dnf install -y dotnet-sdk-10.0';
const MANUAL = 'sudo dnf install -y dotnet-sdk-10.0';
const PASSWORD = 'Your system asks for your password';
const CANCELLED = 'You closed the password window, so nothing was installed. Choose Install to try again.';
const NOT_AUTHORIZED = 'Your system did not allow the install: the password was not accepted, or this account may not install software. Nothing was installed; choose Install to try again, or run the command yourself and choose Check again.';
const HOME_ONLY = '.NET SDK 10 was found at /home/ines/.dotnet/dotnet, inside your home folder, where the team\'s sandbox cannot see it. Install it for the whole system (Install, or the command shown), then choose Check again.';
const NOT_FOUND = '.NET SDK 10 is not found yet where the team\'s runs look for it. Install it, then choose Check again.';
/** A Linux ask as the core sends it: Install through pkexec, or (canInstall false) the command to run yourself. */
const linuxAsk = (over: Partial<ToolAsk> = {}): ToolAsk => ask({
  wingetId: null, canInstall: true, installer: 'pkexec', passwordPrompt: PASSWORD, command: PKEXEC, manualCommand: MANUAL,
  homeOnly: null, choices: ['install', 'checkAgain', 'notNow'], ...over,
});
const manualAsk = (over: Partial<ToolAsk> = {}): ToolAsk => linuxAsk({
  canInstall: false, installer: null, passwordPrompt: null, command: MANUAL, choices: ['checkAgain', 'notNow'], ...over,
});
const result = (code: string, message: string, done = false) => ({ done, id: 4, tool: 'dotnet-sdk-10', message, manual: code === 'manual', result: code });

describe('ToolAskCard on Linux', () => {
  it('WithInstall_ShowsInstallCheckAgainNotNow_ThePasswordPromptAndTheExactCommand', () => {
    const { api } = fakeApi({});
    const w = mount(ToolAskCard, { props: { ask: linuxAsk(), api } });
    expect(w.findAll('button:not(.discuss)').map((b) => b.text())).toEqual(['Install', 'Check again', 'Not now']);
    expect(w.get('[data-test=tool-ask-password]').text()).toBe(PASSWORD);
    const cmd = w.findAll('[data-test=tool-ask-command]');
    expect(cmd).toHaveLength(1);
    expect(cmd[0].text()).toContain('Install runs:');
    expect(cmd[0].get('code').text()).toBe(PKEXEC);
    expect(w.find('[data-test=tool-ask-homeOnly]').exists()).toBe(false);
  });

  it('Installing_ShowsThePasswordPromptThenEachProgressPhase_ThenInstalledEmits', async () => {
    let finish: (v: unknown) => void = () => {};
    const { api, calls, emit } = fakeApi({ 'tools.install': () => new Promise((r) => { finish = r; }) });
    const w = mount(ToolAskCard, { props: { ask: linuxAsk(), api } });
    await w.get('[data-test=tool-ask-install]').trigger('click');
    await flushPromises();
    expect(calls).toEqual([{ method: 'tools.install', params: { id: 4 } }]);
    expect(w.get('[data-test=tool-ask-progress]').text()).toContain(PASSWORD);
    expect(w.findAll('button:not(.discuss)').every((b) => b.attributes('disabled') !== undefined)).toBe(true);

    emit('tools.installProgress', { id: 4, tool: 'dotnet-sdk-10', phase: 'installing', message: `Installing .NET SDK 10 with dnf. ${PASSWORD}...`, command: PKEXEC });
    await flushPromises();
    expect(w.get('[data-test=tool-ask-progress]').attributes('data-phase')).toBe('installing');
    expect(w.get('[data-test=tool-ask-progress]').text()).toBe(`Installing .NET SDK 10 with dnf. ${PASSWORD}...`);
    emit('tools.installProgress', { id: 4, tool: 'dotnet-sdk-10', phase: 'checking', message: 'Looking for dotnet again...', command: PKEXEC });
    await flushPromises();
    expect(w.get('[data-test=tool-ask-progress]').attributes('data-phase')).toBe('checking');
    expect(w.get('[data-test=tool-ask-progress]').text()).toBe('Looking for dotnet again...');

    const done = { ...result('installed', '.NET SDK 10 is installed; runs that start from now on have it on PATH.', true), closed: [4], told: ['Alpha/Manager'] };
    finish(done);
    await flushPromises();
    expect(w.emitted('installed')?.[0]).toEqual([done]);
    expect(w.find('[data-test=tool-ask-progress]').exists()).toBe(false);
  });

  it('StillNotFound_ShowsTheOutputAndTryAgain', async () => {
    const failedMsg = 'Installing .NET SDK 10 did not finish (exit code 1: No match for argument: dotnet-sdk-10.0).';
    const { api, calls } = fakeApi({ 'tools.install': () => result('failed', failedMsg) });
    const w = mount(ToolAskCard, { props: { ask: linuxAsk(), api } });
    await w.get('[data-test=tool-ask-install]').trigger('click');
    await flushPromises();
    expect(w.get('[data-test=tool-ask-failed]').text()).toBe(failedMsg);
    expect(w.get('[data-test=tool-ask-failed]').attributes('data-result')).toBe('failed');
    // The list reloads the ask with the core's error: Try again.
    await w.setProps({ ask: linuxAsk({ error: failedMsg, attempts: 1 }) });
    expect(w.get('[data-test=tool-ask-install]').text()).toBe('Try again');
    await w.get('[data-test=tool-ask-install]').trigger('click');
    await flushPromises();
    expect(calls.map((c) => c.method)).toEqual(['tools.install', 'tools.install']);
  });

  it.each([
    ['cancelled', CANCELLED],
    ['notAuthorized', NOT_AUTHORIZED],
  ])('%s_ShowsTheCoresPlainWords_TheItemStaysOpenWithInstall', async (code, words) => {
    const { api } = fakeApi({ 'tools.install': () => result(code, words) });
    const w = mount(ToolAskCard, { props: { ask: linuxAsk(), api } });
    await w.get('[data-test=tool-ask-install]').trigger('click');
    await flushPromises();
    await w.setProps({ ask: linuxAsk({ error: words, attempts: 1 }) });
    const shown = w.get('[data-test=tool-ask-failed]');
    expect(shown.text()).toBe(words);
    expect(shown.attributes('data-result')).toBe(code);
    expect(shown.classes()).not.toContain('error');
    expect(w.emitted('installed')).toBeUndefined();
    expect(w.findAll('button:not(.discuss)').map((b) => b.text())).toEqual(['Install', 'Check again', 'Not now']);
    expect(w.findAll('button:not(.discuss)').every((b) => b.attributes('disabled') === undefined)).toBe(true);
  });

  it('WithoutInstall_ShowsTheManualCommandCheckAgainAndNotNow', () => {
    const { api } = fakeApi({});
    const w = mount(ToolAskCard, { props: { ask: manualAsk(), api } });
    expect(w.findAll('button:not(.discuss)').map((b) => b.text())).toEqual(['Check again', 'Not now']);
    const cmd = w.get('[data-test=tool-ask-command]');
    expect(cmd.text()).toContain('then choose Check again');
    expect(cmd.get('code').text()).toBe(MANUAL);
    expect(w.find('[data-test=tool-ask-password]').exists()).toBe(false);
  });

  it('OnMacOS_ShowsTheBrewCommandAndCheckAgain', () => {
    const { api } = fakeApi({});
    const w = mount(ToolAskCard, { props: { ask: manualAsk({ command: 'brew install --cask dotnet-sdk', manualCommand: 'brew install --cask dotnet-sdk' }), api } });
    expect(w.get('[data-test=tool-ask-command] code').text()).toBe('brew install --cask dotnet-sdk');
    expect(w.findAll('button:not(.discuss)').map((b) => b.text())).toEqual(['Check again', 'Not now']);
  });

  it('CheckAgain_Found_EmitsInstalled_SoTheItemCloses', async () => {
    const found = { ...result('installed', '.NET SDK 10 is installed; runs that start from now on have it on PATH.', true), closed: [4], told: ['Alpha/Manager'] };
    const { api, calls } = fakeApi({ 'tools.checkAgain': () => found });
    const w = mount(ToolAskCard, { props: { ask: manualAsk(), api } });
    await w.get('[data-test=tool-ask-checkAgain]').trigger('click');
    await flushPromises();
    expect(calls).toEqual([{ method: 'tools.checkAgain', params: { id: 4 } }]);
    expect(w.emitted('installed')?.[0]).toEqual([found]);
    expect(w.emitted('answered')?.[0]).toEqual([found]);
  });

  it('CheckAgain_NotFound_ShowsTheResultsMessage_AndStaysOpen', async () => {
    const { api } = fakeApi({ 'tools.checkAgain': () => result('notFound', NOT_FOUND) });
    const w = mount(ToolAskCard, { props: { ask: manualAsk(), api } });
    await w.get('[data-test=tool-ask-checkAgain]').trigger('click');
    await flushPromises();
    expect(w.get('[data-test=tool-ask-failed]').text()).toBe(NOT_FOUND);
    expect(w.get('[data-test=tool-ask-failed]').attributes('data-result')).toBe('notFound');
    expect(w.emitted('installed')).toBeUndefined();
    expect(w.get('[data-test=tool-ask-checkAgain]').attributes('disabled')).toBeUndefined();
  });

  it('CheckAgain_Refused_ShowsTheError', async () => {
    const { api } = fakeApi({ 'tools.checkAgain': () => { throw new Error('bad_params: tool request 4 is not waiting'); } });
    const w = mount(ToolAskCard, { props: { ask: manualAsk(), api } });
    await w.get('[data-test=tool-ask-checkAgain]').trigger('click');
    await flushPromises();
    expect(w.get('[data-test=tool-ask-error]').text()).toContain('not waiting');
    expect(w.emitted('answered')).toBeUndefined();
  });

  it('HomeOnly_ShowsTheCoresNote_OnceEvenWhenItIsAlsoTheError', () => {
    const { api } = fakeApi({});
    const w = mount(ToolAskCard, { props: { ask: linuxAsk({ homeOnly: HOME_ONLY }), api } });
    expect(w.get('[data-test=tool-ask-homeOnly]').text()).toBe(HOME_ONLY);
    const again = mount(ToolAskCard, { props: { ask: linuxAsk({ homeOnly: HOME_ONLY, error: HOME_ONLY }), api } });
    expect(again.find('[data-test=tool-ask-homeOnly]').exists()).toBe(false);
    expect(again.get('[data-test=tool-ask-failed]').text()).toBe(HOME_ONLY);
  });

  it('ARemoteView_ShowsCheckAgainAndNotNow_ButNoInstall', async () => {
    const { api, calls } = fakeApi({ 'tools.checkAgain': () => result('notFound', NOT_FOUND) }, { remote: true } as Partial<YawbleApi>);
    const w = mount(ToolAskCard, { props: { ask: linuxAsk(), api } });
    expect(w.find('[data-test=tool-ask-install]').exists()).toBe(false);
    expect(w.findAll('button:not(.discuss)').map((b) => b.text())).toEqual(['Check again', 'Not now']);
    expect(w.get('[data-test=tool-ask-remote]').text()).toContain('Check again or Not now');
    await w.get('[data-test=tool-ask-checkAgain]').trigger('click');
    await flushPromises();
    expect(calls).toEqual([{ method: 'tools.checkAgain', params: { id: 4 } }]);
    expect(w.get('[data-test=tool-ask-failed]').text()).toBe(NOT_FOUND);
  });

  it('ARemoteView_ManualAsk_ShowsCheckAgain', () => {
    const { api } = fakeApi({}, { remote: true } as Partial<YawbleApi>);
    const w = mount(ToolAskCard, { props: { ask: manualAsk(), api } });
    expect(w.findAll('button:not(.discuss)').map((b) => b.text())).toEqual(['Check again', 'Not now']);
  });

  // ---------------------------------------------------------------- B017 gap tests (card 84, TesterWren)

  it('Gap_APackageFailure_IsInErrorRed_AndTheButtonBecomesTryAgain', async () => {
    const failedMsg = 'Installing .NET SDK 10 did not finish (exit code 1: No match for argument: dotnet-sdk-10.0).';
    const { api } = fakeApi({ 'tools.install': () => result('failed', failedMsg) });
    const w = mount(ToolAskCard, { props: { ask: linuxAsk(), api } });
    await w.get('[data-test=tool-ask-install]').trigger('click');
    await flushPromises();
    await w.setProps({ ask: linuxAsk({ error: failedMsg, attempts: 1 }) });
    expect(w.get('[data-test=tool-ask-failed]').classes()).toContain('error');
    expect(w.get('[data-test=tool-ask-failed]').classes()).not.toContain('hint');
    expect(w.findAll('button:not(.discuss)').map((b) => b.text())).toEqual(['Try again', 'Check again', 'Not now']);
  });

  it.each([
    ['cancelled', CANCELLED],
    ['notAuthorized', NOT_AUTHORIZED],
  ])('Gap_%s_ThenAnotherInstallFailsForReal_TurnsRedWithTryAgain', async (code, words) => {
    const failedMsg = 'Installing .NET SDK 10 did not finish (exit code 1: boom).';
    let answer = result(code, words);
    const { api } = fakeApi({ 'tools.install': () => answer });
    const w = mount(ToolAskCard, { props: { ask: linuxAsk(), api } });
    await w.get('[data-test=tool-ask-install]').trigger('click');
    await flushPromises();
    await w.setProps({ ask: linuxAsk({ error: words, attempts: 1 }) });
    expect(w.get('[data-test=tool-ask-failed]').classes()).toContain('hint');
    answer = result('failed', failedMsg);
    await w.get('[data-test=tool-ask-install]').trigger('click');
    await flushPromises();
    await w.setProps({ ask: linuxAsk({ error: failedMsg, attempts: 2 }) });
    expect(w.get('[data-test=tool-ask-failed]').classes()).toContain('error');
    expect(w.get('[data-test=tool-ask-install]').text()).toBe('Try again');
  });

  it('Gap_InstallingWithoutAProgressEventYet_HasDataPhaseInstalling_AndThePasswordLine', async () => {
    const { api } = fakeApi({ 'tools.install': () => new Promise(() => {}) });
    const w = mount(ToolAskCard, { props: { ask: linuxAsk(), api } });
    await w.get('[data-test=tool-ask-install]').trigger('click');
    await flushPromises();
    const p = w.get('[data-test=tool-ask-progress]');
    expect(p.attributes('data-phase')).toBe('installing');
    expect(p.text()).toBe(`Installing .NET SDK 10… ${PASSWORD}`);
    expect(p.text()).not.toContain('winget');
  });

  it('Gap_TheManualCommandIsNotShownBesideTheExactPkexecCommand', () => {
    const { api } = fakeApi({});
    const w = mount(ToolAskCard, { props: { ask: linuxAsk(), api } });
    expect(w.text()).toContain(PKEXEC);
    expect(w.text()).not.toContain(MANUAL);
  });

  it('Gap_ARemoteView_HasNoPasswordInstallButton_AndTheNoteIsShownOnce', () => {
    const { api } = fakeApi({}, { remote: true } as Partial<YawbleApi>);
    const w = mount(ToolAskCard, { props: { ask: linuxAsk(), api } });
    expect(w.find('[data-test=tool-ask-install]').exists()).toBe(false);
    expect(w.findAll('[data-test=tool-ask-remote]')).toHaveLength(1);
  });

  it('Gap_CheckAgain_HomeOnlyResult_ShowsTheHomeTextOnce', async () => {
    const { api } = fakeApi({ 'tools.checkAgain': () => result('homeOnly', HOME_ONLY) });
    const w = mount(ToolAskCard, { props: { ask: manualAsk({ homeOnly: HOME_ONLY }), api } });
    await w.get('[data-test=tool-ask-checkAgain]').trigger('click');
    await flushPromises();
    await w.setProps({ ask: manualAsk({ homeOnly: HOME_ONLY, error: HOME_ONLY }) });
    expect(w.text().split('sandbox cannot see it')).toHaveLength(2);
  });

  // FAULT (B017, card 84): a failed install whose program is only under the home folder returns the output AND the home
  // note as one message (ToolAsks.cs InstallAsync, result homeOnly with exit code != 0); the card hides its homeOnly note
  // only when the note equals the message, so the home text is shown twice.
  // Fixed (card 85): the card hides its note when the message already contains it. Passes as plain it (card 84 re-check).
  it('Gap_FAULT_HomeOnlyAfterAFailedInstall_ShowsTheHomeTextOnce', () => {
    const failedMsg = 'Installing .NET SDK 10 did not finish (exit code 1: No match for argument: dotnet-sdk-10.0).';
    const { api } = fakeApi({});
    const w = mount(ToolAskCard, { props: { ask: linuxAsk({ homeOnly: HOME_ONLY, error: `${failedMsg} ${HOME_ONLY}`, attempts: 1 }), api } });
    expect(w.text().split('sandbox cannot see it')).toHaveLength(2);
  });

  // FAULT (B017, card 84): "cancelled"/"notAuthorized" is known only from the click's reply in this card. The core keeps
  // the plain words as the ask's error but sends no result code with tools.asks, so a fresh card (window reloaded, the
  // other view, a remote view) shows the cancelled words in error red with Try again.
  // Fixed (card 85): tools.asks sends the ask's lastResult, and the card reads it when it has no reply of its own.
  it.each([
    ['cancelled', CANCELLED],
    ['notAuthorized', NOT_AUTHORIZED],
  ] as const)('Gap_FAULT_%s_AfterAReload_IsStillAHintWithInstall', (code, words) => {
    const { api } = fakeApi({});
    const w = mount(ToolAskCard, { props: { ask: linuxAsk({ error: words, attempts: 1, lastResult: code }), api } });
    expect(w.get('[data-test=tool-ask-failed]').attributes('data-result')).toBe(code);
    expect(w.get('[data-test=tool-ask-failed]').classes()).not.toContain('error');
    expect(w.get('[data-test=tool-ask-install]').text()).toBe('Install');
  });

  // ---------------------------------------------------------------- card 85 re-check (card 84, TesterWren)

  it('Gap_HomeOnly_WithNoMessage_StillShowsTheNoteOnce', () => {
    const { api } = fakeApi({});
    const w = mount(ToolAskCard, { props: { ask: linuxAsk({ homeOnly: HOME_ONLY, lastResult: null }), api } });
    expect(w.get('[data-test=tool-ask-homeOnly]').text()).toBe(HOME_ONLY);
    expect(w.find('[data-test=tool-ask-failed]').exists()).toBe(false);
    expect(w.text().split('sandbox cannot see it')).toHaveLength(2);
  });

  it('Gap_HomeOnly_WithAnUnrelatedError_ShowsBoth', () => {
    const failedMsg = 'Installing .NET SDK 10 did not finish (exit code 1: boom).';
    const { api } = fakeApi({});
    const w = mount(ToolAskCard, { props: { ask: linuxAsk({ homeOnly: HOME_ONLY, error: failedMsg, attempts: 1, lastResult: 'failed' }), api } });
    expect(w.get('[data-test=tool-ask-failed]').text()).toBe(failedMsg);
    expect(w.get('[data-test=tool-ask-homeOnly]').text()).toBe(HOME_ONLY);
  });

  it('Gap_HomeOnlyAfterAFailedInstall_ClickedHere_ShowsTheHomeTextOnce', async () => {
    const failedMsg = 'Installing .NET SDK 10 did not finish (exit code 1: No match for argument: dotnet-sdk-10.0).';
    const words = `${failedMsg} ${HOME_ONLY}`;
    const { api } = fakeApi({ 'tools.install': () => result('homeOnly', words) });
    const w = mount(ToolAskCard, { props: { ask: linuxAsk({ homeOnly: HOME_ONLY }), api } });
    await w.get('[data-test=tool-ask-install]').trigger('click');
    await flushPromises();
    expect(w.text().split('sandbox cannot see it')).toHaveLength(2);
    await w.setProps({ ask: linuxAsk({ homeOnly: HOME_ONLY, error: words, attempts: 1, lastResult: 'homeOnly' }) });
    expect(w.text().split('sandbox cannot see it')).toHaveLength(2);
    expect(w.find('[data-test=tool-ask-homeOnly]').exists()).toBe(false);
  });

  it.each([
    ['cancelled', CANCELLED],
    ['notAuthorized', NOT_AUTHORIZED],
  ] as const)('Gap_%s_InARemoteView_IsAHint_NotRed_AndHasNoTryAgain', (code, words) => {
    const { api } = fakeApi({}, { remote: true } as Partial<YawbleApi>);
    const w = mount(ToolAskCard, { props: { ask: linuxAsk({ error: words, attempts: 1, lastResult: code }), api } });
    const shown = w.get('[data-test=tool-ask-failed]');
    expect(shown.classes()).toContain('hint');
    expect(shown.classes()).not.toContain('error');
    expect(shown.attributes('data-result')).toBe(code);
    expect(w.findAll('button:not(.discuss)').map((b) => b.text())).toEqual(['Check again', 'Not now']);
  });

  it.each([
    ['cancelled', CANCELLED],
    ['notAuthorized', NOT_AUTHORIZED],
  ] as const)('Gap_%s_AfterAReload_ShowsAllThreeButtonsEnabled_WithInstall', (code, words) => {
    const { api } = fakeApi({});
    const w = mount(ToolAskCard, { props: { ask: linuxAsk({ error: words, attempts: 1, lastResult: code }), api } });
    expect(w.get('[data-test=tool-ask-failed]').classes()).toContain('hint');
    expect(w.findAll('button:not(.discuss)').map((b) => b.text())).toEqual(['Install', 'Check again', 'Not now']);
    expect(w.findAll('button:not(.discuss)').every((b) => b.attributes('disabled') === undefined)).toBe(true);
  });

  it.each(['failed', 'notFound', 'homeOnly'] as const)('Gap_AfterAReload_%s_IsRedWithTryAgain', (code) => {
    const { api } = fakeApi({});
    const w = mount(ToolAskCard, { props: { ask: linuxAsk({ error: 'Installing .NET SDK 10 did not finish (exit code 1: boom).', attempts: 1, lastResult: code }), api } });
    expect(w.get('[data-test=tool-ask-failed]').classes()).toContain('error');
    expect(w.get('[data-test=tool-ask-failed]').attributes('data-result')).toBe(code);
    expect(w.get('[data-test=tool-ask-install]').text()).toBe('Try again');
  });

  it('Gap_AnOlderCoreWithoutLastResult_ShowsTheErrorAsBefore', () => {
    const { api } = fakeApi({});
    const w = mount(ToolAskCard, { props: { ask: linuxAsk({ error: CANCELLED, attempts: 1 }), api } });
    expect(w.get('[data-test=tool-ask-failed]').attributes('data-result')).toBeUndefined();
    expect(w.get('[data-test=tool-ask-install]').text()).toBe('Try again');
  });

  it('Gap_ALastResultWithoutAnError_ShowsNoMessage_AndInstall', () => {
    const { api } = fakeApi({});
    const w = mount(ToolAskCard, { props: { ask: linuxAsk({ error: null, lastResult: 'cancelled' }), api } });
    expect(w.find('[data-test=tool-ask-failed]').exists()).toBe(false);
    expect(w.get('[data-test=tool-ask-install]').text()).toBe('Install');
  });

  it('Gap_ThisCardsOwnReply_ComesBeforeTheAsksLastResult', async () => {
    const failedMsg = 'Installing .NET SDK 10 did not finish (exit code 1: boom).';
    const { api } = fakeApi({ 'tools.install': () => result('failed', failedMsg) });
    // the list still holds the old cancel when the reply comes back
    const w = mount(ToolAskCard, { props: { ask: linuxAsk({ error: CANCELLED, attempts: 1, lastResult: 'cancelled' }), api } });
    expect(w.get('[data-test=tool-ask-install]').text()).toBe('Install');
    await w.get('[data-test=tool-ask-install]').trigger('click');
    await flushPromises();
    expect(w.get('[data-test=tool-ask-failed]').attributes('data-result')).toBe('failed');
    expect(w.get('[data-test=tool-ask-failed]').classes()).toContain('error');
    expect(w.get('[data-test=tool-ask-install]').text()).toBe('Try again');
  });
});

describe('ToolAsks', () => {
  it('CheckAgain_Found_TheItemCloses_AndSaysItIsInstalled', async () => {
    let pending = [manualAsk()];
    const found = { ...result('installed', '.NET SDK 10 is installed; runs that start from now on have it on PATH.', true), closed: [4], told: ['Alpha/Manager'] };
    const { api } = fakeApi({
      'tools.asks': () => pending,
      'tools.checkAgain': () => { pending = []; return found; },
    });
    const w = mount(ToolAsks, { props: { api, pollMs: 60_000, banner: true } });
    await flushPromises();
    await w.get('[data-test=tool-ask-checkAgain]').trigger('click');
    await flushPromises();
    expect(w.find('[data-test=tool-ask]').exists()).toBe(false);
    expect(w.get('[data-test=tool-ask-done]').text()).toContain('.NET SDK 10 is installed');
    w.unmount();
  });

  it('ListsTheAsks_AnInstallRemovesItAndSaysItIsInstalled_AndAsksChangedRefreshes', async () => {
    let pending = [ask(), ask({ id: 5, tool: 'nodejs-lts', displayName: 'Node.js LTS', memberName: 'TesterJo', member: 'Alpha/TesterJo' })];
    const { api, calls, emit } = fakeApi({
      'tools.asks': () => pending,
      'tools.install': (p) => { pending = pending.filter((a) => a.id !== p.id); return installed; },
    });
    const w = mount(ToolAsks, { props: { api, pollMs: 60_000, banner: true } });
    await flushPromises();
    expect(calls[0]).toEqual({ method: 'tools.asks', params: {} });
    expect(w.findAll('[data-test=tool-ask]')).toHaveLength(2);
    expect(w.text()).toContain('2 agents ask for a developer tool');
    expect(w.emitted('count')?.at(-1)).toEqual([2]);

    await w.get('[data-test=tool-ask][data-id="4"] [data-test=tool-ask-install]').trigger('click');
    await flushPromises();
    expect(w.findAll('[data-test=tool-ask]').map((c) => c.attributes('data-id'))).toEqual(['5']);
    expect(w.get('[data-test=tool-ask-done]').text()).toContain('.NET SDK 10 is installed');

    pending = [];
    emit('tools.asksChanged');
    await flushPromises();
    expect(w.emitted('count')?.at(-1)).toEqual([0]);
    expect(w.find('[data-test=tool-ask]').exists()).toBe(false);
    await w.get('[data-test=tool-ask-done-close]').trigger('click');
    expect(w.find('[data-test=tool-asks]').exists()).toBe(false);
    w.unmount();
  });

  it('FiltersByTheBoardsTeamAndMember', async () => {
    const { api } = fakeApi({ 'tools.asks': () => [ask(), ask({ id: 9, team: 'Beta', teamId: 2, member: 'Beta/Ana', memberName: 'Ana' })] });
    const w = mount(ToolAsks, { props: { api, pollMs: 60_000, team: 'Beta' } });
    await flushPromises();
    expect(w.findAll('[data-test=tool-ask]').map((c) => c.attributes('data-id'))).toEqual(['9']);
    await w.setProps({ team: '', member: 'DeveloperInes' });
    expect(w.findAll('[data-test=tool-ask]').map((c) => c.attributes('data-id'))).toEqual(['4']);
    w.unmount();
  });

  it('AnOlderCoreWithoutToolsAsks_ShowsNothing', async () => {
    const { api } = fakeApi({});
    const w = mount(ToolAsks, { props: { api, pollMs: 60_000, banner: true } });
    await flushPromises();
    expect(w.find('[data-test=tool-asks]').exists()).toBe(false);
    expect(w.emitted('count')?.at(-1)).toEqual([0]);
    w.unmount();
  });
});
