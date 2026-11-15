// @vitest-environment happy-dom
import { flushPromises, mount } from '@vue/test-utils';
import { describe, expect, it } from 'vitest';
import { defineComponent, h, reactive } from 'vue';
import { domainAllowNote, useHostProblems } from '../../src/renderer/src/hostChecks';
import TeamNetworkAllows from '../../src/renderer/src/components/TeamNetworkAllows.vue';
import type { YawbleApi } from '../../src/shared/api';

// A fake API only: the core's policy.checkHosts answered from a fixed table (the window never judges a host itself).
function fakeApi(problems: Record<string, string>, extra: Record<string, (p: any) => unknown> = {}) {
  const calls: { method: string; params: any }[] = [];
  const api = {
    getInfo: async () => { throw new Error('no'); },
    getAvailableBackends: async () => [],
    call: async (method: string, params?: any) => {
      calls.push({ method, params });
      if (method === 'policy.checkHosts') return params.hosts.map((host: string) => ({ host, problem: problems[host] ?? null }));
      if (extra[method]) return extra[method](params);
      throw new Error(`unexpected ${method}`);
    },
    openTerminal: async () => {},
    pickFolder: async () => null,
  } as unknown as YawbleApi;
  return { api, calls };
}

function harness(api: YawbleApi, list: { hosts: string[] }) {
  return defineComponent({
    setup() {
      const problems = useHostProblems(api, () => list.hosts);
      return () => h('ul', problems.value.map((p) => h('li', { 'data-test': 'problem' }, p.problem ?? '')));
    },
  });
}

describe('useHostProblems', () => {
  it('ShowsTheCoresWordsForTheEntriesItRefuses_AcceptsADomainAllow', async () => {
    const { api, calls } = fakeApi({ '*.com': "'*.com': com is a public suffix" });
    const list = reactive({ hosts: ['api.nuget.org', '*.digicert.com'] });
    const w = mount(harness(api, list));
    await flushPromises();
    expect(calls).toEqual([{ method: 'policy.checkHosts', params: { hosts: ['api.nuget.org', '*.digicert.com'] } }]);
    expect(w.findAll('[data-test=problem]')).toHaveLength(0);

    list.hosts = [...list.hosts, ' *.com '];
    await flushPromises();
    expect(calls.at(-1)?.params).toEqual({ hosts: ['api.nuget.org', '*.digicert.com', '*.com'] });
    expect(w.findAll('[data-test=problem]').map((p) => p.text())).toEqual(["'*.com': com is a public suffix"]);

    list.hosts = [];
    await flushPromises();
    expect(w.findAll('[data-test=problem]')).toHaveLength(0);
  });

  it('AnOlderCoreWithoutTheCheck_ShowsNoProblems', async () => {
    const api = { call: async () => { throw new Error("no method named 'policy.checkHosts'"); } } as unknown as YawbleApi;
    const w = mount(harness(api, reactive({ hosts: ['*.com'] })));
    await flushPromises();
    expect(w.findAll('[data-test=problem]')).toHaveLength(0);
  });

  it('SaysWhatADomainAllowCovers', () => {
    expect(domainAllowNote('*.digicert.com')).toBe('every subdomain of digicert.com');
    expect(domainAllowNote('api.nuget.org')).toBeNull();
  });
});

describe('TeamNetworkAllows', () => {
  it('ShowsADomainAllow_WithWhatItCovers', async () => {
    const { api } = fakeApi({}, {
      'policy.teamHosts': () => ({ teamId: 4, hosts: [
        { host: '*.digicert.com', addedBy: 'me', addedVia: 'app', addedAt: '2026-11-15T10:00:00Z' },
        { host: 'api.nuget.org', addedBy: 'me', addedVia: 'app', addedAt: '2026-11-15T10:00:00Z' },
      ] }),
    });
    const w = mount(TeamNetworkAllows, { props: { teamId: 4, api } });
    await flushPromises();
    expect(w.get('[data-test="team-host-*.digicert.com"] [data-test=team-host-domain]').text()).toBe('every subdomain of digicert.com');
    expect(w.find('[data-test="team-host-api.nuget.org"] [data-test=team-host-domain]').exists()).toBe(false);
  });
});
