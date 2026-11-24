// @vitest-environment happy-dom
// The policy editors take "*.domain" and show only the core's refusals. Fakes only:
// policy.checkHosts answered from a fixed table (the window never judges a host itself).
import { flushPromises, mount } from '@vue/test-utils';
import { describe, expect, it } from 'vitest';
import GlobalPolicyDialog from '../../src/renderer/src/components/GlobalPolicyDialog.vue';
import MemberSettingsDialog from '../../src/renderer/src/components/MemberSettingsDialog.vue';
import NetworkAskCard from '../../src/renderer/src/components/NetworkAskCard.vue';
import { groupAsks } from '../../src/renderer/src/networkAskGroups';
import type { GlobalPolicyDoc, NetworkAsk, YawbleApi } from '../../src/shared/api';

const REFUSED: Record<string, string> = { '*.com': "'*.com': com is a public suffix", '*.github.io': "'*.github.io': github.io is a public suffix" };

function fakeApi(answers: Record<string, (p: any) => unknown>) {
  const calls: { method: string; params: any }[] = [];
  const api = {
    getInfo: async () => { throw new Error('no'); },
    getAvailableBackends: async () => [],
    call: async (method: string, params: any = {}) => {
      calls.push({ method, params });
      if (method === 'policy.checkHosts') return params.hosts.map((host: string) => ({ host, problem: REFUSED[host] ?? null }));
      if (answers[method]) return answers[method](params);
      return {};
    },
    onEvent: () => () => {},
    openTerminal: async () => {},
    pickFolder: async () => null,
  } as unknown as YawbleApi;
  return { api, calls };
}

const settle = async () => { for (let i = 0; i < 5; i++) await flushPromises(); };

describe('Settings > Guardrails: allowed hosts take a domain allow', () => {
  it('AddsStarDotDomain_ShowsOnlyTheCoresRefusals_AndSavesTheDomainAllow', async () => {
    const doc: GlobalPolicyDoc = {
      filesystem: { grantableRoots: [], readonly: [], readwrite: [], denied: [] }, network: { allowHosts: ['api.nuget.org'] },
      compute: { idleTimeoutSec: 1800, wallTimeoutSec: 7200 }, ui: { clipboard: 'none' }, app: { maxConcurrentAgents: 4, backend: 'default' },
    };
    const { api, calls } = fakeApi({
      'policy.getGlobal': () => doc,
      'policy.saveGlobal': (p) => ({ ...doc, ...p.policy }),
      'guardrail.get': () => ({ state: 'none', error: null }),
    });
    const w = mount(GlobalPolicyDialog, { props: { api, inline: true } });
    await settle();
    expect(w.get('[data-test=global-allowHosts] [data-test=list-input]').attributes('placeholder')).toContain('*.digicert.com');

    await w.get('[data-test=global-allowHosts] [data-test=list-input]').setValue('*.digicert.com, *.com');
    await w.get('[data-test=global-allowHosts] [data-test=list-add]').trigger('click');
    await settle();

    expect(w.findAll('[data-test=global-allowHosts] [data-test=list-value]').map((v) => v.text())).toEqual(['api.nuget.org', '*.digicert.com', '*.com']);
    expect(calls.filter((c) => c.method === 'policy.checkHosts').at(-1)!.params.hosts).toEqual(['api.nuget.org', '*.digicert.com', '*.com']);
    expect(w.findAll('[data-test=global-allowHosts-problem]').map((p) => p.text())).toEqual([REFUSED['*.com']]);

    // remove the refused one: no problem left, and the save carries the domain allow as typed (the apex is not added)
    await w.findAll('[data-test=global-allowHosts] [data-test=list-remove]')[2].trigger('click');
    await settle();
    expect(w.findAll('[data-test=global-allowHosts-problem]')).toHaveLength(0);
    await w.get('[data-test=save-global]').trigger('click');
    await settle();
    expect(calls.filter((c) => c.method === 'policy.saveGlobal').at(-1)!.params.policy.network.allowHosts).toEqual(['api.nuget.org', '*.digicert.com']);
    w.unmount();
  });
});

describe("a member's Loosen… block takes a domain allow", () => {
  it('ShowsTheCoresRefusalUnderTheLoosenHosts_NotForTheDomainAllow', async () => {
    const view = () => ({
      memberId: 3,
      global: { filesystem: { grantableRoots: [], readonly: [], readwrite: [], denied: [] }, network: { allowHosts: [] },
        compute: { idleTimeoutSec: 1800, wallTimeoutSec: 7200 }, ui: { clipboard: 'none' }, app: { maxConcurrentAgents: 4, backend: 'default' } },
      override: {}, loosen: {}, loosenSummary: [], loosenApprovedAt: null,
      effective: { readonly: [], readwrite: [], denied: [], allowHosts: [], compute: { idleTimeoutSec: 1800, wallTimeoutSec: 7200, memoryMb: null, cpuPercent: null, maxProcesses: null }, clipboard: 'none', loosened: [], notes: [] },
      implicit: { readwrite: [], readonly: [], denied: [] }, widening: [], violations: [], backend: 'bubblewrap', machineMemoryMb: 16000,
    });
    const member = { id: 3, teamId: 1, team: 'T', name: 'Ada', role: 'member', cli: 'claude', instructions: '', model: '', credential: 'default' };
    const { api, calls } = fakeApi({
      'members.get': () => member,
      'agentProfiles.list': () => [],
      'agents.commandLine': () => ({ cli: 'claude', model: { model: 'opus', source: 'agent', label: 'Agent default (opus)' }, headless: { argv: [], env: [] }, interactive: { argv: [], env: [] } }),
      'agents.models': () => ({ models: [], source: 'app' }),
      'credentials.status': () => ({ claude: { mode: 'subscription' } }),
      'policy.getMember': view,
      'policy.teamHosts': () => ({ teamId: 1, hosts: [] }),
      'policy.previewLoosen': (p) => ({ loosened: (p.loosen.network?.allowHosts ?? []).map((h: string) => `network.allowHosts + ${h}`) }),
    });
    const w = mount(MemberSettingsDialog, { props: { memberId: 3, api, initialTab: 'policy' } });
    await settle();
    await w.get('[data-test=loosen-network-allowHosts]').setValue('*.digicert.com\n*.github.io');
    await settle();
    expect(calls.filter((c) => c.method === 'policy.checkHosts').at(-1)!.params.hosts).toEqual(['*.digicert.com', '*.github.io']);
    expect(w.findAll('[data-test=loosen-host-problem]').map((p) => p.text())).toEqual([REFUSED['*.github.io']]);

    await w.get('[data-test=loosen-network-allowHosts]').setValue('*.digicert.com');
    await settle();
    expect(w.findAll('[data-test=loosen-host-problem]')).toHaveLength(0);
    await w.get('[data-test=preview-loosen]').trigger('click');
    await settle();
    expect(calls.filter((c) => c.method === 'policy.previewLoosen').at(-1)!.params.loosen).toEqual({ network: { allowHosts: ['*.digicert.com'] } });
    w.unmount();
  });
});

describe('grouped asks: apex and Deny', () => {
  const ask = (id: number, host: string, over: Partial<NetworkAsk> = {}): NetworkAsk => ({
    id, at: '2026-11-15T10:00:00Z', teamId: 1, team: 'Alpha', member: 'Alpha/Priya', memberName: 'Priya', host, reason: null, method: 'CONNECT',
    pathSample: null, count: 1, lastSeen: '2026-11-15T10:00:00Z', hint: null, workflow: null, state: 'pending', scope: null, decidedBy: null,
    decidedAt: null, plainHttp: false, choices: ['agent', 'team', 'everywhere', 'deny'], domain: 'digicert.com',
    domainPattern: host === 'digicert.com' ? null : '*.digicert.com', ...over,
  });

  it('TheApexJoinsItsSubdomainsGroup_AndTheGroupStillOffersTheDomain', () => {
    const groups = groupAsks([ask(2, 'digicert.com'), ask(3, 'crl3.digicert.com'), ask(4, 'evil-digicert.com', { domain: 'evil-digicert.com', domainPattern: '*.evil-digicert.com' })]);
    expect(groups.map((g) => g.asks.map((a) => a.id))).toEqual([[2, 3], [4]]);
    expect(groups[0].domainPattern).toBe('*.digicert.com');
  });

  it.each(['agent', 'deny'])('DenyNeverSendsDomain_EvenAfterChoosingTheDomain (%s first)', async (first) => {
    const { api, calls } = fakeApi({ 'network.answer': (p) => ({ ok: true, id: p.id, host: 'digicert.com', choice: p.choice, closed: [], told: [] }) });
    const group = [ask(2, 'digicert.com'), ask(3, 'crl3.digicert.com')];
    const w = mount(NetworkAskCard, { props: { ask: group[0], group, api } });
    await w.get('[data-test=network-ask-target-domain]').setValue(true);
    await w.get(`[data-test=network-ask-${first}]`).trigger('click');
    await settle();
    await w.get('[data-test=network-ask-deny]').trigger('click');
    await settle();
    const answers = calls.filter((c) => c.method === 'network.answer').map((c) => c.params);
    for (const p of answers.filter((a) => a.choice === 'deny')) expect(p).not.toHaveProperty('domain');
    if (first === 'agent') expect(answers[0]).toEqual({ id: 2, choice: 'agent', ids: [2, 3], domain: true });
  });
});
