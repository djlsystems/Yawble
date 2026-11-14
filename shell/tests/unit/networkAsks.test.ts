// @vitest-environment happy-dom
import { flushPromises, mount } from '@vue/test-utils';
import { describe, expect, it } from 'vitest';
import NetworkAskCard from '../../src/renderer/src/components/NetworkAskCard.vue';
import NetworkAsks from '../../src/renderer/src/components/NetworkAsks.vue';
import type { CoreEvent, NetworkAsk, YawbleApi } from '../../src/shared/api';

// A fake API only: no core, no network. Each method answers from `answers`; anything else fails the call.
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
  const emit = (event: string) => { for (const l of [...listeners]) l({ event, data: null }); };
  return { api, calls, emit };
}

const ask = (over: Partial<NetworkAsk> = {}): NetworkAsk => ({
  id: 7, at: '2026-11-13T21:00:00Z', teamId: 1, team: 'Alpha', member: 'Alpha/DeveloperInes', memberName: 'DeveloperInes',
  host: 'api.nuget.org', reason: null, method: 'CONNECT', pathSample: null, count: 3, lastSeen: '2026-11-13T21:05:00Z',
  hint: 'NuGet: .NET packages (dotnet restore, dotnet test)', workflow: 12, state: 'pending', scope: null,
  decidedBy: null, decidedAt: null, choices: ['agent', 'team', 'everywhere', 'deny'], ...over,
});
const answered = (choice: string) => ({ ok: true, id: 7, host: 'api.nuget.org', choice, closed: [7], told: ['Alpha/Manager'] });

describe('NetworkAskCard', () => {
  it('ShowsMemberTeamHostWhatWasRefusedCountAndHint', () => {
    const { api } = fakeApi({});
    const w = mount(NetworkAskCard, { props: { ask: ask({ pathSample: '/v3/index.json', method: 'GET' }), api } });
    expect(w.get('[data-test=network-ask-member]').text()).toBe('Developer Ines');
    expect(w.text()).toContain('Alpha');
    expect(w.get('[data-test=network-ask-host]').text()).toBe('api.nuget.org');
    expect(w.get('[data-test=network-ask-refused]').text()).toContain('GET /v3/index.json');
    expect(w.find('[data-test=network-ask-reason]').exists()).toBe(false);
    expect(w.get('[data-test=network-ask-count]').text()).toContain('3 times');
    expect(w.get('[data-test=network-ask-hint]').text()).toContain('NuGet');
    expect(w.findAll('button').map((b) => b.text())).toEqual(['Allow for this agent', 'Allow for this team', 'Allow everywhere', 'Deny']);
  });

  it('ShowsTheAgentsReasonInsteadOfWhatWasRefused_AsPlainText', () => {
    const { api } = fakeApi({});
    const w = mount(NetworkAskCard, { props: { ask: ask({ reason: '<b>restore</b> the packages', hint: null, count: 1 }), api } });
    expect(w.get('[data-test=network-ask-reason]').text()).toContain('<b>restore</b> the packages');
    expect(w.find('[data-test=network-ask-reason] b').exists()).toBe(false);
    expect(w.find('[data-test=network-ask-refused]').exists()).toBe(false);
    expect(w.find('[data-test=network-ask-hint]').exists()).toBe(false);
    expect(w.get('[data-test=network-ask-count]').text()).toContain('once');
  });

  it.each(['agent', 'team', 'everywhere', 'deny'])('Button_%s_CallsNetworkAnswerWithThatChoice', async (choice) => {
    const { api, calls } = fakeApi({ 'network.answer': (p) => answered(p.choice) });
    const w = mount(NetworkAskCard, { props: { ask: ask(), api } });
    await w.get(`[data-test=network-ask-${choice}]`).trigger('click');
    await flushPromises();
    expect(calls).toEqual([{ method: 'network.answer', params: { id: 7, choice } }]);
    expect(w.emitted('answered')?.[0]).toEqual([answered(choice)]);
    expect(w.find('[data-test=network-ask-error]').exists()).toBe(false);
  });

  it('Deny_SendsTheNote', async () => {
    const { api, calls } = fakeApi({ 'network.answer': (p) => answered(p.choice) });
    const w = mount(NetworkAskCard, { props: { ask: ask(), api } });
    await w.get('[data-test=network-ask-note]').setValue('  use the local feed  ');
    await w.get('[data-test=network-ask-deny]').trigger('click');
    await flushPromises();
    expect(calls[0].params).toEqual({ id: 7, choice: 'deny', note: 'use the local feed' });
  });

  it('HoldsAllFourButtonsWhileTheAnswerRuns', async () => {
    let finish: (v: unknown) => void = () => {};
    const { api, calls } = fakeApi({ 'network.answer': () => new Promise((r) => { finish = r; }) });
    const w = mount(NetworkAskCard, { props: { ask: ask(), api } });
    await w.get('[data-test=network-ask-team]').trigger('click');
    await flushPromises();
    expect(w.findAll('button').every((b) => b.attributes('disabled') !== undefined)).toBe(true);
    expect(w.get('[data-test=network-ask-team]').text()).toContain('Allowing…');
    await w.get('[data-test=network-ask-agent]').trigger('click');
    expect(calls).toHaveLength(1);
    finish(answered('team'));
    await flushPromises();
    expect(w.findAll('button').every((b) => b.attributes('disabled') === undefined)).toBe(true);
  });

  it('ShowsTheCoresRefusalInTheWindow', async () => {
    const { api } = fakeApi({ 'network.answer': () => { throw new Error('policy_refused: the host is not allowed'); } });
    const w = mount(NetworkAskCard, { props: { ask: ask(), api } });
    await w.get('[data-test=network-ask-everywhere]').trigger('click');
    await flushPromises();
    const err = w.get('[data-test=network-ask-error]').text();
    expect(err).toContain('the host is not allowed');
    expect(err).not.toContain('in the window');
    expect(w.emitted('answered')).toBeUndefined();
  });

  it('ARemoteViewWhoseAnswerTheCloudLinkRefuses_SaysToAnswerItInTheWindow', async () => {
    const refusal = Object.assign(new Error("'network.answer' is not available through the Cloud Link"), { code: 'remote_refused' });
    const { api } = fakeApi({ 'network.answer': () => { throw refusal; } }, { remote: true } as Partial<YawbleApi>);
    const w = mount(NetworkAskCard, { props: { ask: ask(), api } });
    await w.get('[data-test=network-ask-agent]').trigger('click');
    await flushPromises();
    const err = w.get('[data-test=network-ask-error]').text();
    expect(err).toContain('This view cannot change the network policy: answer it in the window');
    expect(err).toContain('not available through the Cloud Link');
  });

  it('ARemoteViewWhoseAnswerFailsOtherwise_ShowsTheRealErrorOnly', async () => {
    const failed = Object.assign(new Error('the ask is already answered'), { code: 'invalid_params' });
    const { api } = fakeApi({ 'network.answer': () => { throw failed; } }, { remote: true } as Partial<YawbleApi>);
    const w = mount(NetworkAskCard, { props: { ask: ask(), api } });
    await w.get('[data-test=network-ask-team]').trigger('click');
    await flushPromises();
    const err = w.get('[data-test=network-ask-error]').text();
    expect(err).toBe('the ask is already answered');
    expect(err).not.toContain('cannot change the network policy');
  });

  it('ARemoteViewWhoseConnectionDrops_ShowsTheRealError', async () => {
    const { api } = fakeApi({ 'network.answer': () => { throw new Error('the connection to the instance closed'); } }, { remote: true } as Partial<YawbleApi>);
    const w = mount(NetworkAskCard, { props: { ask: ask(), api } });
    await w.get('[data-test=network-ask-deny]').trigger('click');
    await flushPromises();
    expect(w.get('[data-test=network-ask-error]').text()).toBe('the connection to the instance closed');
  });

  it('TheWindowNeverSaysTheViewCannotChangeThePolicy_EvenForTheCode', async () => {
    const odd = Object.assign(new Error('refused'), { code: 'remote_refused' });
    const { api } = fakeApi({ 'network.answer': () => { throw odd; } });
    const w = mount(NetworkAskCard, { props: { ask: ask(), api } });
    await w.get('[data-test=network-ask-agent]').trigger('click');
    await flushPromises();
    expect(w.get('[data-test=network-ask-error]').text()).toBe('refused');
  });

  it.each([['GET', '/index.html'], ['post', '/upload']])('APlainHttpRefusal_%s_SaysOnlyHttpsIsForwarded', (method, pathSample) => {
    const { api } = fakeApi({});
    const w = mount(NetworkAskCard, { props: { ask: ask({ method, pathSample, host: 'example.com', hint: null }), api } });
    expect(w.get('[data-test=network-ask-http]').text()).toContain('only https is forwarded, even once the host is allowed');
  });

  it.each([['CONNECT', 'an https refusal'], [null, 'a request_host ask']])('NoHttpHintFor_%s (%s)', (method) => {
    const { api } = fakeApi({});
    const w = mount(NetworkAskCard, { props: { ask: ask({ method, reason: method ? null : 'restore packages' }), api } });
    expect(w.find('[data-test=network-ask-http]').exists()).toBe(false);
  });

  it('TheCoresPlainHttpFlag_WinsOverTheMethod', () => {
    const { api } = fakeApi({});
    const flagged = mount(NetworkAskCard, { props: { ask: { ...ask({ method: 'CONNECT' }), plainHttp: true } as NetworkAsk, api } });
    expect(flagged.find('[data-test=network-ask-http]').exists()).toBe(true);
    const unflagged = mount(NetworkAskCard, { props: { ask: { ...ask({ method: 'GET' }), plainHttp: false } as NetworkAsk, api } });
    expect(unflagged.find('[data-test=network-ask-http]').exists()).toBe(false);
  });
});

describe('NetworkAsks', () => {
  it('ListsThePendingAsks_AnAnswerReloads_AndAsksChangedRefreshes', async () => {
    let pending = [ask(), ask({ id: 8, host: 'registry.npmjs.org', memberName: 'TesterJo', member: 'Alpha/TesterJo' })];
    const { api, calls, emit } = fakeApi({
      'network.asks': () => pending,
      'network.answer': (p) => { pending = pending.filter((a) => a.id !== p.id); return answered(p.choice); },
    });
    const w = mount(NetworkAsks, { props: { api, pollMs: 60_000, banner: true } });
    await flushPromises();
    expect(calls[0]).toEqual({ method: 'network.asks', params: {} });
    expect(w.findAll('[data-test=network-ask]')).toHaveLength(2);
    expect(w.text()).toContain('2 agents ask');
    expect(w.emitted('count')?.at(-1)).toEqual([2]);

    await w.get('[data-test=network-ask][data-id="7"] [data-test=network-ask-agent]').trigger('click');
    await flushPromises();
    expect(w.findAll('[data-test=network-ask]')).toHaveLength(1);

    pending = [];
    emit('network.asksChanged');
    await flushPromises();
    expect(w.find('[data-test=network-asks]').exists()).toBe(false);
    expect(w.emitted('count')?.at(-1)).toEqual([0]);
    w.unmount();
  });

  it('FiltersByTheBoardsTeamAndMember', async () => {
    const { api } = fakeApi({ 'network.asks': () => [ask(), ask({ id: 9, team: 'Beta', teamId: 2, member: 'Beta/Ana', memberName: 'Ana' })] });
    const w = mount(NetworkAsks, { props: { api, pollMs: 60_000, team: 'Beta' } });
    await flushPromises();
    expect(w.findAll('[data-test=network-ask]').map((c) => c.attributes('data-id'))).toEqual(['9']);
    await w.setProps({ team: '', member: 'DeveloperInes' });
    expect(w.findAll('[data-test=network-ask]').map((c) => c.attributes('data-id'))).toEqual(['7']);
    w.unmount();
  });

  it('AnOlderCoreWithoutNetworkAsks_ShowsNothing', async () => {
    const { api } = fakeApi({});
    const w = mount(NetworkAsks, { props: { api, pollMs: 60_000, banner: true } });
    await flushPromises();
    expect(w.find('[data-test=network-asks]').exists()).toBe(false);
    w.unmount();
  });
});
