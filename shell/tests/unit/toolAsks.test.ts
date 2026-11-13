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
    expect(w.findAll('button').map((b) => b.text())).toEqual(['Install', 'Not now']);
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
    expect(w.findAll('button').every((b) => b.attributes('disabled') !== undefined)).toBe(true);
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
    expect(w.findAll('button').every((b) => b.attributes('disabled') !== undefined)).toBe(true);
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
    expect(w.findAll('button').map((b) => b.text())).toEqual(['Not now']);
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
    expect(w.findAll('button').every((b) => b.attributes('disabled') === undefined)).toBe(true);
  });
});

describe('ToolAsks', () => {
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
