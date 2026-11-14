// @vitest-environment happy-dom
import { flushPromises, mount } from '@vue/test-utils';
import { describe, expect, it } from 'vitest';
import TeamNetworkAllows from '../../src/renderer/src/components/TeamNetworkAllows.vue';
import TeamManageDialog from '../../src/renderer/src/components/TeamManageDialog.vue';
import LogsDialog from '../../src/renderer/src/components/LogsDialog.vue';
import type { LogSourceInfo, NetworkAsk, TeamHost, TeamSummary, YawbleApi } from '../../src/shared/api';

// A fake API only: no core, no network. Each method answers from `answers`; anything else fails the call.
function fakeApi(answers: Record<string, (params: any) => unknown>) {
  const calls: { method: string; params: any }[] = [];
  const api: YawbleApi = {
    getInfo: async () => { throw new Error('no'); },
    getAvailableBackends: async () => [],
    call: async (method: string, params?: Record<string, unknown>) => {
      calls.push({ method, params });
      const answer = answers[method];
      if (!answer) throw new Error(`unexpected ${method}`);
      return answer(params) as any;
    },
    onEvent: () => () => {},
    openTerminal: async () => {},
    pickFolder: async () => null,
  };
  return { api, calls };
}

const host = (h: string, over: Partial<TeamHost> = {}): TeamHost => ({ host: h, addedBy: 'micro', addedVia: 'app', addedAt: '2026-11-13T10:00:00Z', ...over });
// Rejects as the core's RpcException does: the message, with its code riding on the Error.
const rpcError = (code: string, message: string) => Object.assign(new Error(message), { code });

describe('TeamNetworkAllows', () => {
  it('ListsTheTeamsHosts_OldestFirst_WithWhoAddedItHowAndWhen', async () => {
    const { api, calls } = fakeApi({ 'policy.teamHosts': (p) => ({ teamId: p.teamId, hosts: [host('api.nuget.org'), host('registry.npmjs.org', { addedBy: 'ines', addedVia: 'cloud' })] }) });
    const w = mount(TeamNetworkAllows, { props: { teamId: 4, api } });
    await flushPromises();
    expect(calls).toEqual([{ method: 'policy.teamHosts', params: { teamId: 4 } }]);
    expect(w.findAll('.row .host').map((r) => r.text())).toEqual(['api.nuget.org', 'registry.npmjs.org']);
    const second = w.get('[data-test="team-host-registry.npmjs.org"] [data-test=team-host-added]').text();
    expect(second).toContain('ines');
    expect(second).toContain('via cloud');
    expect(w.find('[data-test="team-host-remove-api.nuget.org"]').text()).toBe('Remove');
  });

  it('SaysSoWhenTheListIsEmpty', async () => {
    const { api } = fakeApi({ 'policy.teamHosts': () => ({ teamId: 4, hosts: [] }) });
    const w = mount(TeamNetworkAllows, { props: { teamId: 4, api } });
    await flushPromises();
    expect(w.find('[data-test=team-hosts-empty]').exists()).toBe(true);
  });

  it('Remove_CallsRemoveTeamHostWithTeamAndHost_AndShowsTheListItAnswers', async () => {
    let list = [host('api.nuget.org'), host('registry.npmjs.org')];
    const { api, calls } = fakeApi({
      'policy.teamHosts': () => ({ teamId: 4, hosts: list }),
      'policy.removeTeamHost': (p) => { list = list.filter((h) => h.host !== p.host); return { teamId: 4, hosts: list }; },
    });
    const w = mount(TeamNetworkAllows, { props: { teamId: 4, api } });
    await flushPromises();
    await w.get('[data-test="team-host-remove-api.nuget.org"]').trigger('click');
    await flushPromises();
    expect(calls.filter((c) => c.method === 'policy.removeTeamHost').map((c) => c.params)).toEqual([{ teamId: 4, host: 'api.nuget.org' }]);
    expect(w.find('[data-test="team-host-api.nuget.org"]').exists()).toBe(false);
    expect(w.find('[data-test="team-host-registry.npmjs.org"]').exists()).toBe(true);
    expect(w.emitted('changed')).toHaveLength(1);
    expect(w.find('[data-test=team-hosts-error]').exists()).toBe(false);
  });

  it('NotFound_ShowsItsOwnErrorText_AndReadsTheListAgain', async () => {
    let list = [host('api.nuget.org')];
    const { api, calls } = fakeApi({
      'policy.teamHosts': () => ({ teamId: 4, hosts: list }),
      // Removed meanwhile in another view: the core refuses with not_found.
      'policy.removeTeamHost': () => { list = []; throw rpcError('not_found', "'api.nuget.org' is not on this team's network allow list"); },
    });
    const w = mount(TeamNetworkAllows, { props: { teamId: 4, api } });
    await flushPromises();
    await w.get('[data-test="team-host-remove-api.nuget.org"]').trigger('click');
    await flushPromises();
    expect(w.get('[data-test=team-hosts-error]').text()).toBe("'api.nuget.org' is not on this team's network allow list");
    expect(calls.filter((c) => c.method === 'policy.teamHosts')).toHaveLength(2);
    expect(w.find('[data-test=team-hosts-empty]').exists()).toBe(true);
    expect(w.emitted('changed')).toBeUndefined();
  });

  it('IsTheTeamSettingsNetworkTab', async () => {
    const { api } = fakeApi({
      'policy.teamHosts': () => ({ teamId: 4, hosts: [host('api.nuget.org')] }),
      'teams.quiet': () => ({ quiet: true, working: [], openWorkflows: [] }), 'teams.env': () => ({}), 'teams.agents': () => [],
      'teams.push': () => ({ on: false, origin: null }), 'teams.syncToBase': () => ({ on: true, note: null }),
      'teams.repos': () => ({ main: null, extras: [] }), 'teams.approvals': () => [],
      'integrations.list': () => ({ integrations: [] }), 'integrations.mcpList': () => [], 'integrations.apiList': () => [],
    });
    const team = { id: 4, name: 'Alpha', paused: false, archived: false } as TeamSummary;
    const w = mount(TeamManageDialog, { props: { team, api } });
    await flushPromises();
    await w.get('[data-test=manage-tab-network]').trigger('click');
    await flushPromises();
    expect(w.find('[data-test=team-network] [data-test="team-host-api.nuget.org"]').exists()).toBe(true);
  });
});

describe('closed network asks', () => {
  it('TheTypeHasClosed_AndTheApprovalsLogShowsIt', async () => {
    const closed: NetworkAsk['state'] = 'closed';
    const col = (key: string, label: string, kind: 'time' | 'text' = 'text') => ({ key, label, kind, filter: false });
    const sources: LogSourceInfo[] = [{ id: 'approvals', title: 'Approvals', group: 'Accountability', about: 'Asks and answers.', columns: [col('at', 'Time', 'time'), col('host', 'Host'), col('state', 'State')], prunable: false, loss: null, keptBecause: 'test', defaultDays: null, retentionDays: null, team: true, member: true }];
    const { api } = fakeApi({
      'logs.sources': () => sources,
      'logs.who': () => ({ teams: [], members: [] }),
      'logs.values': () => [],
      'logs.query': () => ({ rows: [{ _id: '1', at: '2026-11-13T12:00:00Z', host: 'api.nuget.org', state: closed }], total: 1, asOf: '2026-11-13T12:00:00Z', next: null }),
    });
    const w = mount(LogsDialog, { props: { api }, attachTo: document.body });
    await flushPromises();
    expect(w.get('[data-test=logs-row]').text()).toContain('closed');
    w.unmount();
  });
});
