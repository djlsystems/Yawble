// @vitest-environment happy-dom
import { flushPromises, mount } from '@vue/test-utils';
import { describe, expect, it } from 'vitest';
import TeamManageDialog from '../../src/renderer/src/components/TeamManageDialog.vue';
import TeamView from '../../src/renderer/src/components/TeamView.vue';
import IntegrationDialog from '../../src/renderer/src/components/IntegrationDialog.vue';
import type { YawbleApi } from '../../src/shared/api';

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

const team = { id: 3, name: 'ops', repoPath: null, members: 2, running: 0, queued: 0, paused: false, archived: false } as any;
const counts = { allowed: 4, ask: 2, off: 1, undecided: 0 };
const manageApi = () => fakeApi({
  'teams.quiet': () => ({ quiet: true, working: [], openWorkflows: [] }),
  'teams.env': () => ({}),
  'teams.agents': () => [],
  'teams.push': () => ({ on: false, origin: null }),
  'teams.repos': () => ({ main: null, extras: [] }),
  'teams.approvals': () => [],
  'integrations.list': () => ({
    integrations: [
      { name: 'gh', configured: true, all: false, teams: [3], counts, login: null, label: 'GitHub', path: null },
      { name: 'aws', configured: true, all: false, teams: [9], counts, login: null, label: 'AWS', path: null },
      { name: 'jq', configured: true, all: true, teams: [], counts: { allowed: 1, ask: 0, off: 0, undecided: 0 }, login: null, label: 'jq', path: null },
    ], pendingAsks: 0,
  }),
  'integrations.mcpList': () => [
    { name: 'files', all: false, teams: [3], counts: { allowed: 1, ask: 0, off: 0, undecided: 2 } },
    { name: 'other', all: false, teams: [], counts: null },
  ],
});

describe('team Integrations tab', () => {
  it('the team tab lists only integrations offered to the team and opens the dialog scoped to it', async () => {
    const { api } = manageApi();
    const w = mount(TeamManageDialog, { props: { team, api }, global: { stubs: { IntegrationDialog: true, Modal: { template: '<div><slot /></div>' } } } });
    await flushPromises();
    await w.find('[data-test=manage-tab-integrations]').trigger('click');
    expect(w.find('[data-test=team-int-gh]').exists()).toBe(true);
    expect(w.find('[data-test=team-int-jq]').exists()).toBe(true);
    expect(w.find('[data-test=team-int-aws]').exists()).toBe(false);
    expect(w.find('[data-test=team-int-files]').exists()).toBe(true);
    expect(w.find('[data-test=team-int-other]').exists()).toBe(false);
    expect(w.find('[data-test=team-int-gh]').text()).toContain('7 actions: 4 allowed, 2 ask first');
    await w.find('[data-test=team-int-edit-gh]').trigger('click');
    const d = w.findComponent(IntegrationDialog);
    expect(d.exists()).toBe(true);
    expect(d.props()).toMatchObject({ kind: 'cli', name: 'gh', initialScope: 3 });
  });

  it('says so when nothing is offered to the team', async () => {
    const { api } = fakeApi({ 'teams.quiet': () => ({ quiet: true, working: [], openWorkflows: [] }), 'teams.env': () => ({}), 'teams.agents': () => [],
      'teams.push': () => ({ on: false, origin: null }), 'teams.repos': () => ({ main: null, extras: [] }), 'teams.approvals': () => [],
      'integrations.list': () => ({ integrations: [], pendingAsks: 0 }), 'integrations.mcpList': () => [] });
    const w = mount(TeamManageDialog, { props: { team, api }, global: { stubs: { IntegrationDialog: true, Modal: { template: '<div><slot /></div>' } } } });
    await flushPromises();
    await w.find('[data-test=manage-tab-integrations]').trigger('click');
    expect(w.find('[data-test=team-int-empty]').text()).toBe('No integrations are offered to this team. Offer one in Settings → Integrations.');
  });
});

describe('remember-choice approvals', () => {
  const ask = (o: Record<string, unknown> = {}) => ({ id: 12, at: 'now', integration: 'gh', teamId: 3, member: 'ops/dev', command: 'gh pr merge 4', reason: 'wants to merge', workflow: 1, repo: null, action: 'pr merge', canAlways: true, ...o });
  const mountView = (a: unknown) => {
    const { api, calls } = fakeApi({
      'teams.agents': () => [], 'bus.decisions': () => [], 'integrations.asks': () => [a],
      'integrations.approve': () => ({ remembered: true }), 'integrations.decline': () => ({}),
    });
    const w = mount(TeamView, { props: { team, api, pollMs: 100000 }, global: { stubs: { TaskAskCard: true } } });
    return { w, calls };
  };

  it('always allow sends remember always with the chosen scope', async () => {
    const { w, calls } = mountView(ask());
    await flushPromises();
    expect(w.find('[data-test=ask-action]').text()).toContain('remembers as pr merge');
    await w.find('[data-test=ask-scope]').setValue('all');
    await w.find('[data-test=ask-always]').trigger('click');
    await flushPromises();
    expect(calls.find((c) => c.method === 'integrations.approve')!.params).toEqual({ id: 12, remember: 'always', scope: 'all' });
  });

  it('approve once and keep asking send their remember values; scope defaults to this team', async () => {
    const { w, calls } = mountView(ask());
    await flushPromises();
    await w.find('[data-test=ask-keep-asking]').trigger('click');
    await flushPromises();
    expect(calls.filter((c) => c.method === 'integrations.approve')[0].params).toEqual({ id: 12, remember: 'ask', scope: 'team' });
    await w.find('[data-test=ask-approve]').trigger('click');
    await flushPromises();
    expect(calls.filter((c) => c.method === 'integrations.approve')[1].params).toEqual({ id: 12, remember: 'once' });
  });

  it('never declines with remember never', async () => {
    const { w, calls } = mountView(ask());
    await flushPromises();
    await w.find('[data-test=ask-never]').trigger('click');
    await flushPromises();
    expect(calls.find((c) => c.method === 'integrations.decline')!.params).toEqual({ id: 12, note: undefined, remember: 'never', scope: 'team' });
  });

  it('always allow is hidden when the ask cannot be always-allowed', async () => {
    const { w } = mountView(ask({ canAlways: false }));
    await flushPromises();
    expect(w.find('[data-test=ask-approve]').exists()).toBe(true);
    expect(w.find('[data-test=ask-always]').exists()).toBe(false);
  });
});
