// @vitest-environment happy-dom
import { flushPromises, mount } from '@vue/test-utils';
import { describe, expect, it } from 'vitest';
import AgentsDialog from '../../src/renderer/src/components/AgentsDialog.vue';
import MemberSettingsDialog from '../../src/renderer/src/components/MemberSettingsDialog.vue';
import TeamManageDialog from '../../src/renderer/src/components/TeamManageDialog.vue';
import TeamSyncBanner from '../../src/renderer/src/components/TeamSyncBanner.vue';
import type { YawbleApi } from '../../src/shared/api';

// Agent settings that only read, a member's preview on a custom agent, and the team's sync to its base, against a scripted core: a fake API, no core, no CLI, no network port.
function fakeApi(answers: Record<string, (params: any) => unknown>) {
  const calls: { method: string; params: any }[] = [];
  const terminals: unknown[] = [];
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
    openTerminal: async (...a: unknown[]) => { terminals.push(a); },
    pickFolder: async () => null,
  };
  return { api, calls, terminals };
}

const builtin = (name: string, defaultModel = '') => ({ id: 0, name, baseCli: name, custom: false, defaultModel, argsBefore: [], argsAfter: [], removedArgs: [] });
const mine = { id: 7, name: 'Claude - Opus long runs', baseCli: 'claude', custom: true, defaultModel: 'opus', argsBefore: [], argsAfter: ['--foo=1'], removedArgs: [] };
const lines = (cli: string, model: string, label: string, extra: string[] = []) => ({
  cli: 'claude', agent: cli, model: { model, source: 'agent', label },
  headless: { argv: ['claude', '-p', '<prompt>', ...(model ? ['--model', model] : []), ...extra], env: [] },
  interactive: { argv: ['claude', ...(model ? ['--model', model] : []), ...extra], env: [] },
});

describe('opening agent settings runs nothing on the host', () => {
  it('Settings > Agents and Member settings only read: no update, no terminal, the model list is the app\'s', async () => {
    const member = { id: 3, teamId: 1, team: 'T', name: 'Ada', role: 'member', cli: 'grok', instructions: '', model: '', credential: 'default' };
    const { api, calls, terminals } = fakeApi({
      'agents.detect': () => [],
      'agentProfiles.list': () => [builtin('claude'), builtin('grok'), builtin('codex')],
      'agentProfiles.setDefaultModel': () => ({}),
      'agents.commandLine': () => lines('claude', '', "The CLI's default"),
      'agents.models': () => ({ models: ['grok-4'], source: 'app' }),
      'agents.versionStatus': (p: any) => ({ cli: p.cli, detected: '1', lastRun: '1', changed: false }),
      'members.get': () => member,
      'policy.getMember': () => ({ backend: 'x', global: {}, override: {}, loosen: {}, widening: [], violations: [], effective: { notes: [] }, implicit: { readwrite: [], readonly: [], denied: [] }, loosenSummary: [] }),
      'credentials.status': () => ({ claude: { mode: 'subscription' }, grok: { mode: 'login' } }),
    });
    const panel = mount(AgentsDialog, { props: { api, inline: true } });
    await flushPromises();
    await panel.get('[data-test=agent-cli-grok] [data-test=tile-model]').trigger('click');
    await flushPromises();
    expect(panel.text()).toContain("the app's list");
    await panel.get('[data-test=model-dialog-pick]').setValue('grok-4');
    await panel.get('[data-test=model-dialog-save]').trigger('click'); // a save reloads the list
    await flushPromises();
    expect(calls.filter((c) => c.method === 'agentProfiles.setDefaultModel').at(-1)!.params).toEqual({ baseCli: 'grok', model: 'grok-4' });
    panel.unmount();
    const dialog = mount(MemberSettingsDialog, { props: { memberId: 3, api } });
    await flushPromises();
    await dialog.get('[data-test=member-cli]').setValue('codex');
    await dialog.get('[data-test=member-cli]').trigger('change');
    await flushPromises();
    expect(calls.map((c) => c.method)).not.toContain('agents.checkForUpdates');
    expect(terminals).toEqual([]);
    expect(calls.filter((c) => c.method === 'agents.models').length).toBeGreaterThan(0);
  });
});

describe('member preview uses the member\'s actual agent', () => {
  it('a member on a custom agent is shown that agent\'s argv and default model', async () => {
    const member = { id: 3, teamId: 1, team: 'T', name: 'Ada', role: 'member', cli: mine.name, instructions: '', model: '', credential: 'default' };
    const { api, calls } = fakeApi({
      'agentProfiles.list': () => [builtin('claude', 'sonnet'), mine],
      'agents.models': () => ({ models: ['opus', 'sonnet', 'haiku'], source: 'app' }),
      'agents.commandLine': (p: any) => p.cli === mine.name
        ? lines(mine.name, p.model ?? 'opus', p.model ?? 'Agent default (opus)', ['--foo=1'])
        : lines('claude', p.model ?? 'sonnet', p.model ?? 'Agent default (sonnet)'),
      'members.get': () => member,
      'policy.getMember': () => ({ backend: 'x', global: {}, override: {}, loosen: {}, widening: [], violations: [], effective: { notes: [] }, implicit: { readwrite: [], readonly: [], denied: [] }, loosenSummary: [] }),
      'credentials.status': () => ({ claude: { mode: 'subscription' }, grok: { mode: 'login' } }),
    });
    const w = mount(MemberSettingsDialog, { props: { memberId: 3, api } });
    await flushPromises();
    expect(calls.filter((c) => c.method === 'agents.commandLine').at(-1)!.params).toEqual({ cli: mine.name });
    expect(w.get('[data-test=member-cmdline-headless]').text()).toContain('--model opus --foo=1');
    expect(w.get('[data-test=member-cmdline]').text()).toContain('Agent default (opus)');
    expect(w.get('[data-test=member-model-pick]').findAll('option')[0].text()).toBe('Agent default (opus)');
    // The models offered are the base CLI's.
    expect(calls.filter((c) => c.method === 'agents.models').at(-1)!.params).toEqual({ cli: 'claude' });
  });
});

const team = { id: 3, name: 'ops', repoPath: null, members: 2, running: 0, queued: 0, paused: false, archived: false } as any;

describe('the team\'s sync setting, behind-count, Sync now and the note on the team page', () => {
  it('team settings carry "Bring branches up to <base> before new work", and turning it off saves it', async () => {
    let on = true;
    const { api, calls } = fakeApi({
      'teams.quiet': () => ({ quiet: true, working: [], openWorkflows: [] }), 'teams.env': () => ({}), 'teams.agents': () => [],
      'teams.push': () => ({ on: false, origin: null }), 'teams.approvals': () => [],
      'teams.repos': () => ({ main: { path: 'C:/r', baseBranch: 'trunk', managed: true, origin: null, name: null }, extras: [] }),
      'teams.syncToBase': (p: any) => { if (p.on !== undefined) on = p.on; return { on, note: null, baseBranch: 'trunk', behind: null }; },
      'integrations.list': () => ({ integrations: [], pendingAsks: 0 }), 'integrations.mcpList': () => [],
    });
    const w = mount(TeamManageDialog, { props: { team, api }, global: { stubs: { IntegrationDialog: true, Modal: { template: '<div><slot /></div>' } } } });
    await flushPromises();
    await w.get('[data-test=manage-tab-repos]').trigger('click');
    const box = w.get('[data-test=team-sync-to-base]');
    expect((box.element as HTMLInputElement).checked).toBe(true);
    expect(box.element.parentElement!.textContent).toContain('Bring branches up to trunk before new work');
    await box.setValue(false);
    await flushPromises();
    expect(calls.filter((c) => c.method === 'teams.syncToBase').at(-1)!.params).toEqual({ teamId: 3, on: false });
    expect(on).toBe(false);
  });

  it('the team page shows how far behind the team branch is, and Sync now runs the sync', async () => {
    let behind: any = { teamBranch: 'team/ops', count: 4, text: 'team/ops is 4 commits behind main' };
    const { api, calls } = fakeApi({
      'teams.syncToBase': () => ({ on: true, note: null, baseBranch: 'main', behind }),
      'git.sync': () => { behind = null; return { outcome: 'synced', baseBranch: 'main', baseSha: 'abc', behind: 0, moved: ['team/ops', 'members/ada'], conflicts: [], note: null }; },
    });
    const w = mount(TeamSyncBanner, { props: { team, api } });
    await flushPromises();
    expect(w.get('[data-test=team-behind]').text()).toContain('team/ops is 4 commits behind main');
    await w.get('[data-test=team-sync-now]').trigger('click');
    await flushPromises();
    expect(calls.find((c) => c.method === 'git.sync')!.params).toEqual({ teamId: 3 });
    expect(w.get('[data-test=team-sync-done]').text()).toContain('Brought the branches up to main (team/ops, members/ada).');
    expect(w.find('[data-test=team-behind]').exists()).toBe(false);
  });

  it('a sync or fetch-back that could not be done is a Needs you banner with the files', async () => {
    const note = 'team/ops is 2 commits behind main and could not be synced automatically (conflicts in: a.txt, b/c.txt). Merge main into it first and resolve the conflicts before other work.\n'
      + 'Ada\'s new work on members/ada does not merge cleanly with the synced members/ada (conflicts in: d.txt), so members/ada was left as it was.';
    const { api } = fakeApi({ 'teams.syncToBase': () => ({ on: true, note, baseBranch: 'main', behind: { teamBranch: 'team/ops', count: 2, text: 'team/ops is 2 commits behind main' } }) });
    const w = mount(TeamSyncBanner, { props: { team, api } });
    await flushPromises();
    const banner = w.get('[data-test=team-sync-note]');
    expect(banner.text()).toContain('Needs you');
    expect(w.findAll('[data-test=team-sync-note-line]')).toHaveLength(2);
    expect(w.findAll('[data-test=team-sync-files] code').map((c) => c.text())).toEqual(['a.txt', 'b/c.txt', 'd.txt']);
  });

  it('shows nothing when the team is in step', async () => {
    const { api } = fakeApi({ 'teams.syncToBase': () => ({ on: true, note: null, baseBranch: 'main', behind: null }) });
    const w = mount(TeamSyncBanner, { props: { team, api } });
    await flushPromises();
    expect(w.find('[data-test=team-sync]').exists()).toBe(false);
  });
});
