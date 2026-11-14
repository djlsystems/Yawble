// @vitest-environment happy-dom
// B023 (card 113): "Environment from a Connection" in the host task editor, and the in-use note in Connections.
import { flushPromises, mount } from '@vue/test-utils';
import { describe, expect, it, vi } from 'vitest';
import ConnectionsPanel from '../../src/renderer/src/components/ConnectionsPanel.vue';
import TasksPanel from '../../src/renderer/src/components/TasksPanel.vue';
import type { Connection, HostTaskInfo, YawbleApi } from '../../src/shared/api';

const SECRET = 'github_pat_SECRET_VALUE_never_shown';

function fakeApi(answers: Record<string, (params: any) => unknown>, remote = false) {
  const calls: { method: string; params: any }[] = [];
  const api: YawbleApi = {
    remote,
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
// The core never sends a secret in full; `shown` is masked. The test still checks the masked text does not leak into a task.
const release: Connection = {
  id: 7, name: 'GitHub release (Yawble)', kind: 'secret', status: 'works', statusReason: null, testedAt: null, usedBy: [],
  fields: [{ key: 'token', label: 'Token', secret: true, shown: 'gith••••••hown', set: true }, { key: 'account', label: 'Account', secret: false, shown: 'me', set: true }],
  usedByTasks: [{ teamId: 3, taskName: 'linux-release' }],
};
const twoSecrets: Connection = {
  id: 9, name: 'Cloud keys', kind: 'secret', status: 'works', statusReason: null, testedAt: null, usedBy: [],
  fields: [{ key: 'key_id', label: 'Key id', secret: true, shown: 'AKIA••••', set: true }, { key: 'secret', label: 'Secret', secret: true, shown: '••••', set: true }],
};
const task: HostTaskInfo = {
  id: 1,
  def: { name: 'linux-release', repo: 'main', program: 'bash', args: ['packaging/release.sh'], workdir: '.', timeoutSeconds: 900, envNames: [], watch: [], pinMode: 'files', commandLineOnly: false,
    // As the core's tasks.list sends it: connectionId is a string.
    envFromConnections: [{ name: 'GH_TOKEN', connectionId: '7', field: 'token', connectionName: 'GitHub release (Yawble)' }] },
  commandLine: 'bash packaging/release.sh', approval: 'asks', approvedAt: null, approvedCommit: null, approvedBy: null, programResolved: '/usr/bin/bash', commit: 'abcdef12',
  pinned: ['packaging/release.sh'], warnings: [], problem: null, runningRunId: null, pendingAskId: null, lastRun: null, updatedAt: '', updatedBy: null,
};
const preview = () => ({ problem: null, programResolved: '/usr/bin/bash', commit: 'abcdef12', pinned: [], warnings: [], commandLineOnly: false });

describe('host task editor: Environment from a Connection', () => {
  it('shows a binding as variable ← Connection (field), never a value', async () => {
    const { api } = fakeApi({ 'tasks.list': () => ({ tasks: [task], repos: ['main'] }), 'tasks.runs': () => [] });
    const w = mount(TasksPanel, { props: { api, teams } });
    await flushPromises();
    expect(w.find('[data-test=task-linux-release] [data-test=task-binding-text]').text()).toBe("GH_TOKEN ← Connection 'GitHub release (Yawble)' (token)");
    expect(w.html()).not.toContain(SECRET);
    expect(w.html()).not.toContain('gith••');
  });

  it('adds a row, offers Connections and only their secret fields, and saves the binding with the Connection name', async () => {
    const { api, calls } = fakeApi({
      'tasks.list': () => ({ tasks: [], repos: ['main'] }), 'tasks.runs': () => [], 'tasks.preview': preview, 'tasks.save': () => ({ ok: true, id: 1 }),
      'integrations.connections': () => [release, twoSecrets],
    });
    const w = mount(TasksPanel, { props: { api, teams } });
    await flushPromises();
    await w.find('[data-test=task-add]').trigger('click');
    await flushPromises();
    await w.find('[data-test=task-name]').setValue('linux-release');
    await w.find('[data-test=task-program]').setValue('bash');
    await w.find('[data-test=binding-add]').trigger('click');
    const row = () => w.find('[data-test=task-binding-0]');
    expect(row().exists()).toBe(true);
    // an incomplete row holds Save
    await row().find('[data-test=binding-name]').setValue('GH_TOKEN');
    expect(row().find('[data-test=binding-problem]').exists()).toBe(true);
    expect(w.find('[data-test=task-save]').attributes('disabled')).toBeDefined();
    const conns = row().findAll('[data-test=binding-connection] option').map((o) => o.text());
    expect(conns).toEqual(['Connection…', 'GitHub release (Yawble)', 'Cloud keys']);
    // a Connection with one secret field takes it; the non-secret field is never offered
    await row().find('[data-test=binding-connection]').setValue('7');
    await flushPromises();
    expect(row().findAll('[data-test=binding-field] option').map((o) => o.text())).toEqual(['field…', 'Token']);
    expect((row().find('[data-test=binding-field]').element as HTMLSelectElement).value).toBe('token');
    expect(row().find('[data-test=binding-problem]').exists()).toBe(false);
    await w.find('[data-test=task-save]').trigger('click');
    await flushPromises();
    const save = calls.find((c) => c.method === 'tasks.save')!;
    expect(save.params.def.envFromConnections).toEqual([{ name: 'GH_TOKEN', connectionId: 7, field: 'token', connectionName: 'GitHub release (Yawble)' }]);
    expect(JSON.stringify(save.params)).not.toContain('gith');
  });

  it('a Connection with several secret fields leaves the field to choose; a row can be removed', async () => {
    const { api, calls } = fakeApi({
      'tasks.list': () => ({ tasks: [task], repos: ['main'] }), 'tasks.runs': () => [], 'tasks.preview': preview, 'tasks.save': () => ({ ok: true, id: 1 }),
      'integrations.connections': () => [release, twoSecrets],
    });
    const w = mount(TasksPanel, { props: { api, teams } });
    await flushPromises();
    await w.findAll('[data-test=task-linux-release] button').find((b) => b.text() === 'Edit')!.trigger('click');
    await flushPromises();
    // the stored binding is in the form
    expect((w.find('[data-test=task-binding-0] [data-test=binding-name]').element as HTMLInputElement).value).toBe('GH_TOKEN');
    await w.find('[data-test=binding-add]').trigger('click');
    const row = w.find('[data-test=task-binding-1]');
    await row.find('[data-test=binding-name]').setValue('CLOUD_SECRET');
    await row.find('[data-test=binding-connection]').setValue('9');
    await flushPromises();
    expect((row.find('[data-test=binding-field]').element as HTMLSelectElement).value).toBe('');
    expect(row.findAll('[data-test=binding-field] option').map((o) => o.text())).toEqual(['field…', 'Key id', 'Secret']);
    await row.find('[data-test=binding-field]').setValue('secret');
    expect(w.find('[data-test=task-save]').attributes('disabled')).toBeUndefined();
    // remove the first (stored) row: only the new one is saved
    await w.find('[data-test=task-binding-0] [data-test=binding-remove]').trigger('click');
    await w.find('[data-test=task-save]').trigger('click');
    await flushPromises();
    expect(calls.find((c) => c.method === 'tasks.save')!.params.def.envFromConnections).toEqual([{ name: 'CLOUD_SECRET', connectionId: 9, field: 'secret', connectionName: 'Cloud keys' }]);
  });

  it('refuses a name bound twice or also passed on from the environment', async () => {
    const { api } = fakeApi({
      'tasks.list': () => ({ tasks: [task], repos: ['main'] }), 'tasks.runs': () => [], 'tasks.preview': preview, 'integrations.connections': () => [release],
    });
    const w = mount(TasksPanel, { props: { api, teams } });
    await flushPromises();
    await w.findAll('[data-test=task-linux-release] button').find((b) => b.text() === 'Edit')!.trigger('click');
    await flushPromises();
    await w.find('[data-test=task-env]').setValue('GH_TOKEN');
    expect(w.find('[data-test=binding-problem]').text()).toContain('also passed on from your environment');
    expect(w.find('[data-test=task-save]').attributes('disabled')).toBeDefined();
  });

  it('over the Cloud Link the bindings show by name only and cannot be changed', async () => {
    const { api, calls } = fakeApi({ 'tasks.list': () => ({ tasks: [task], repos: ['main'] }), 'tasks.runs': () => [], 'tasks.preview': preview }, true);
    const w = mount(TasksPanel, { props: { api, teams } });
    await flushPromises();
    await w.findAll('[data-test=task-linux-release] button').find((b) => b.text() === 'Edit')!.trigger('click');
    await flushPromises();
    const box = w.find('[data-test=task-bindings]');
    expect(box.find('[data-test=task-binding-text]').text()).toBe("GH_TOKEN ← Connection 'GitHub release (Yawble)' (token)");
    expect(box.find('[data-test=task-bindings-remote]').exists()).toBe(true);
    expect(box.find('[data-test=binding-add]').exists()).toBe(false);
    expect(box.find('select').exists()).toBe(false);
    expect(calls.some((c) => c.method === 'integrations.connections')).toBe(false);
  });
});

describe('Connections: in use by a host task', () => {
  it('says which host tasks use a Connection, and removing it warns they will not start', async () => {
    const confirm = vi.fn((_: string) => false);
    window.confirm = confirm as unknown as typeof window.confirm;
    const { api, calls } = fakeApi({ 'integrations.connections': () => [release, twoSecrets] });
    const w = mount(ConnectionsPanel, { props: { api, teams } });
    await flushPromises();
    expect(w.find('[data-test=connection-usedby-tasks-7]').text()).toBe('Used by host task linux-release (team Linux Worker): removing it stops it from starting');
    expect(w.find('[data-test=connection-7] [data-test=connection-usedby]').text()).toBe('');
    expect(w.find('[data-test=connection-usedby-tasks-9]').exists()).toBe(false);
    expect(w.find('[data-test=connection-9] [data-test=connection-usedby]').text()).toBe('not used yet');
    await w.find('[data-test=connection-remove-7]').trigger('click');
    await flushPromises();
    expect(confirm.mock.calls[0]![0]).toContain('linux-release (team Linux Worker)');
    expect(confirm.mock.calls[0]![0]).toContain('will not start');
    expect(calls.some((c) => c.method === 'integrations.connectionRemove')).toBe(false);
  });
});
