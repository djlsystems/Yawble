// @vitest-environment happy-dom
import { flushPromises, mount } from '@vue/test-utils';
import { describe, expect, it } from 'vitest';
import MemberSettingsDialog from '../../src/renderer/src/components/MemberSettingsDialog.vue';
import type { TeamHost, YawbleApi } from '../../src/shared/api';

// B014 test (card 73): the team's network allows next to the member policy (member settings, Policy tab).
// A fake API only: no core, no network. Calls the test does not care about answer an empty object.
function fakeApi(answers: Record<string, (params: any) => unknown>) {
  const calls: { method: string; params: any }[] = [];
  const api: YawbleApi = {
    getInfo: async () => { throw new Error('no'); },
    getAvailableBackends: async () => [],
    call: async (method: string, params?: Record<string, unknown>) => {
      calls.push({ method, params });
      const answer = answers[method];
      return (answer ? answer(params) : {}) as any;
    },
    onEvent: () => () => {},
    openTerminal: async () => {},
    pickFolder: async () => null,
  };
  return { api, calls };
}

const host = (h: string): TeamHost => ({ host: h, addedBy: 'micro', addedVia: 'app', addedAt: '2026-11-13T10:00:00Z' });
const member = { id: 3, teamId: 7, team: 'payments', name: 'Ada', role: 'member', cli: 'claude', instructions: '', model: '', credential: 'default' };
const view = (teamHosts: string[]) => ({
  memberId: 3, backend: 'x', machineMemoryMb: null, loosenApprovedAt: null,
  global: {
    filesystem: { readonly: [], readwrite: [], denied: [] }, network: { allowHosts: [] },
    compute: { cpuPercent: 50, memoryMb: 2048, wallTimeoutSec: 3600 }, ui: { clipboard: 'none' }, app: { maxConcurrentAgents: 2 },
  },
  override: {}, loosen: {}, widening: [], violations: [], teamHosts, loosenSummary: [],
  effective: {
    readonly: [], readwrite: [], denied: [], allowHosts: teamHosts, compute: { cpuPercent: 50, memoryMb: 2048, wallTimeoutSec: 3600 },
    clipboard: 'none', notes: [], loosened: teamHosts.map((h) => `network.allowHosts + ${h} (team)`),
  },
  implicit: { readwrite: [], readonly: [], denied: [] },
});

describe('the team network allows on the member Policy tab', () => {
  it('ListsTheMembersTeamsHosts_AndRemoveReadsTheMemberPolicyAgain', async () => {
    let listed = ['pypi.org', 'registry.npmjs.org'];
    const { api, calls } = fakeApi({
      'members.get': () => member,
      'policy.getMember': () => view(listed),
      'policy.teamHosts': (p) => ({ teamId: p.teamId, hosts: listed.map(host) }),
      'policy.removeTeamHost': (p) => { listed = listed.filter((h) => h !== p.host); return { teamId: p.teamId, hosts: listed.map(host) }; },
    });
    const w = mount(MemberSettingsDialog, { props: { memberId: 3, initialTab: 'policy', api } });
    await flushPromises();

    const section = w.get('[data-test=policy-team-hosts]');
    expect(calls.find((c) => c.method === 'policy.teamHosts')!.params).toEqual({ teamId: 7 });
    expect(section.findAll('.row .host').map((r) => r.text())).toEqual(['pypi.org', 'registry.npmjs.org']);

    const reads = calls.filter((c) => c.method === 'policy.getMember').length;
    await section.get('[data-test="team-host-remove-pypi.org"]').trigger('click');
    await flushPromises();

    expect(calls.find((c) => c.method === 'policy.removeTeamHost')!.params).toEqual({ teamId: 7, host: 'pypi.org' });
    expect(w.get('[data-test=policy-team-hosts]').findAll('.row .host').map((r) => r.text())).toEqual(['registry.npmjs.org']);
    expect(calls.filter((c) => c.method === 'policy.getMember').length).toBe(reads + 1);
    expect(calls.filter((c) => c.method === 'policy.getMember').at(-1)!.params).toEqual({ memberId: 3 });
  });
});
