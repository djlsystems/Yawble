// @vitest-environment happy-dom
// B023 verify (card 114): gap tests on cards 112 (core) + 113 (UI). Fakes only. The data is shaped exactly as the core sends it
// (HostTaskRpc.DefJson: connectionId is a STRING; HostTaskService ask detail: env_from_connections is a list of shown lines;
// a refused run puts the binding problem in missingEnv with state 'refused').
import { flushPromises, mount } from '@vue/test-utils';
import { describe, expect, it, vi } from 'vitest';
import ConnectionsPanel from '../../src/renderer/src/components/ConnectionsPanel.vue';
import TaskAskCard from '../../src/renderer/src/components/TaskAskCard.vue';
import TasksPanel from '../../src/renderer/src/components/TasksPanel.vue';
import type { Connection, HostTaskInfo, IntegrationAsk, TaskRun, YawbleApi } from '../../src/shared/api';

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
  } as YawbleApi;
  return { api, calls };
}

const teams = [{ id: 3, name: 'Linux Worker', repoPath: null, members: 2, running: 0, queued: 0 }];
const SHOWN = "GH_TOKEN ← Connection 'GitHub release (Yawble)' (token)";
const release: Connection = {
  id: 7, name: 'GitHub release (Yawble)', kind: 'secret', status: 'works', statusReason: null, testedAt: null, usedBy: [],
  fields: [{ key: 'token', label: 'Token', secret: true, shown: 'gith••••', set: true }],
  usedByTasks: [{ teamId: 3, taskName: 'linux-release' }],
};
// As tasks.list sends it: connectionId is a string ("7"), not a number.
const coreTask = {
  id: 1,
  def: { name: 'linux-release', repo: 'main', program: 'bash', args: ['packaging/release.sh'], workdir: '.', timeoutSeconds: 900, envNames: [], watch: [], pinMode: 'files', commandLineOnly: false,
    envFromConnections: [{ name: 'GH_TOKEN', connectionId: '7', field: 'token', connectionName: 'GitHub release (Yawble)' }] },
  envFromConnectionsShown: [SHOWN],
  commandLine: 'bash packaging/release.sh', approval: 'always', approvedAt: null, approvedCommit: null, approvedBy: null, programResolved: '/usr/bin/bash', commit: 'abcdef12',
  pinned: ['packaging/release.sh'], warnings: [], problem: null, runningRunId: null, pendingAskId: null, lastRun: null, updatedAt: '', updatedBy: null,
} as unknown as HostTaskInfo;
const preview = () => ({ problem: null, programResolved: '/usr/bin/bash', commit: 'abcdef12', pinned: [], warnings: [], commandLineOnly: false });

describe('B023 gap: the UI reads the core JSON as sent', () => {
  it('editing a stored task finds its Connection and field when connectionId comes as a string (core sends a string)', async () => {
    const { api, calls } = fakeApi({
      'tasks.list': () => ({ tasks: [coreTask], repos: ['main'] }), 'tasks.runs': () => [], 'tasks.preview': preview,
      'tasks.save': () => ({ ok: true, id: 1 }), 'integrations.connections': () => [release],
    });
    const w = mount(TasksPanel, { props: { api, teams } });
    await flushPromises();
    await w.findAll('[data-test=task-linux-release] button').find((b) => b.text() === 'Edit')!.trigger('click');
    await flushPromises();
    const row = w.find('[data-test=task-binding-0]');
    // The bound Connection exists: it must not be offered as "(not found)", and its field must be a real choice.
    expect(row.find('[data-test=binding-connection]').text()).not.toContain('(not found)');
    expect(row.find('[data-test=binding-field]').text()).not.toContain('(not found)');
    expect(row.findAll('[data-test=binding-field] option').map((o) => o.text())).toContain('Token');
    // Saving unchanged sends the same binding back.
    await w.find('[data-test=task-save]').trigger('click');
    await flushPromises();
    const sent = calls.find((c) => c.method === 'tasks.save')!.params.def.envFromConnections[0];
    expect(String(sent.connectionId)).toBe('7');
    expect(sent.field).toBe('token');
  });
});

describe('B023 gap: the approval ask shows the bindings (spec point 2)', () => {
  it('TaskAskCard shows each env_from_connections line from the ask detail', () => {
    const ask = {
      id: 31, at: '', integration: 'task:linux-release', teamId: 3, member: 'linux/Manager', command: 'task linux-release', reason: 'new release v1.2', workflow: 9,
      task: true, taskName: 'linux-release', commit: 'abcdef1234567890',
      detail: {
        commit: 'abcdef1234567890', program: '/usr/bin/bash', workdir: '/repo', args: ['packaging/release.sh'], timeout_s: 900, env_names: [], pin_mode: 'files',
        env_from_connections: [SHOWN], pinned: ['packaging/release.sh'], warnings: [], standingApproval: false,
      },
    } as unknown as IntegrationAsk;
    const w = mount(TaskAskCard, { props: { ask } });
    expect(w.text()).toContain(SHOWN);
  });
});

describe('B023 gap: a run refused for a binding (spec point 3)', () => {
  it('says the run was refused and why, without calling a Connection problem "not set in your environment"', async () => {
    const problem = "GH_TOKEN ← Connection 'GitHub release (Yawble)' (token): the Connection was removed";
    const run: TaskRun = {
      id: 21, task: 'linux-release', teamId: 3, state: 'refused', startedAt: '2026-11-14T10:00:00Z', endedAt: '2026-11-14T10:00:00Z', commit: 'abcdef1234567890', headAfter: null,
      definitionHash: 'aaaa', filesHash: 'bbbb', program: '/usr/bin/bash', exitCode: null, timedOut: false, killedBy: null, outputBytes: null, truncated: false,
      logPath: null, dirtyAfter: false, askId: null, requestedBy: 'linux/Manager', missingEnv: problem,
    } as unknown as TaskRun;
    const { api } = fakeApi({ 'tasks.list': () => ({ tasks: [{ ...coreTask, lastRun: run }], repos: ['main'] }), 'tasks.runs': () => [run] });
    const w = mount(TasksPanel, { props: { api, teams } });
    await flushPromises();
    await w.find('[data-test=task-run-21] td').trigger('click');
    expect(w.text()).toContain('refused at start');
    expect(w.text()).toContain('the Connection was removed');
    expect(w.text()).not.toContain('Not set in your environment: GH_TOKEN ← Connection');
  });
});

describe('B023 gap: removing a Connection only host tasks use (spec point 5)', () => {
  it('usedBy empty + usedByTasks non-empty: warns about the task, removes on OK with confirm, reloads', async () => {
    const confirm = vi.fn((_: string) => true);
    window.confirm = confirm as unknown as typeof window.confirm;
    let list: Connection[] = [release];
    const { api, calls } = fakeApi({
      'integrations.connections': () => list,
      'integrations.connectionRemove': (p) => { list = []; return p.confirm ? { ok: true } : { ok: false, usedBy: [], usedByTasks: release.usedByTasks }; },
    });
    const w = mount(ConnectionsPanel, { props: { api, teams } });
    await flushPromises();
    await w.find('[data-test=connection-remove-7]').trigger('click');
    await flushPromises();
    const msg = confirm.mock.calls[0]![0];
    expect(msg).toContain('linux-release (team Linux Worker)');
    expect(msg).not.toContain('is used by :'); // no empty API list
    expect(calls.find((c) => c.method === 'integrations.connectionRemove')!.params).toEqual({ id: 7, confirm: true });
    expect(w.find('[data-test=connection-7]').exists()).toBe(false);
  });

  it('a team the panel does not know is shown by its id, not left blank', async () => {
    const { api } = fakeApi({ 'integrations.connections': () => [{ ...release, usedByTasks: [{ teamId: 42, taskName: 'publish' }] }] });
    const w = mount(ConnectionsPanel, { props: { api, teams } });
    await flushPromises();
    expect(w.find('[data-test=connection-usedby-tasks-7]').text()).toContain('publish (team 42)');
  });
});
