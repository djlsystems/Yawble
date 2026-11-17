// @vitest-environment happy-dom
// B066 (card 359): a host task's optional "Description" and "What to check after" fields: saved, edited, cleared, shown on the row.
import { flushPromises, mount } from '@vue/test-utils';
import { describe, expect, it } from 'vitest';
import TasksPanel from '../../src/renderer/src/components/TasksPanel.vue';
import type { HostTaskDef, HostTaskInfo, YawbleApi } from '../../src/shared/api';

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

const teams = [{ id: 3, name: 'ops', repoPath: null, members: 2, running: 0, queued: 0 }];
const baseDef: HostTaskDef = { name: 'deploy', repo: 'yawble-cloud', program: 'pwsh', args: ['-File', 'scripts/deploy.ps1'], workdir: '.', timeoutSeconds: 1800, envNames: [], watch: [], pinMode: 'files', commandLineOnly: false };
const info = (def: HostTaskDef): HostTaskInfo => ({
  id: 1, def, commandLine: 'pwsh -File scripts/deploy.ps1', approval: 'asks', approvedAt: null, approvedCommit: null, approvedBy: null,
  programResolved: 'C:\\pwsh.exe', commit: 'abcdef1234567890', pinned: ['scripts/deploy.ps1'], warnings: [], problem: null, runningRunId: null, pendingAskId: null, lastRun: null,
  updatedAt: '2026-11-17T10:00:00Z', updatedBy: 'me',
});
const preview = () => ({ problem: null, programResolved: 'C:\\pwsh.exe', commit: 'abcdef1234567890', pinned: [], warnings: [], commandLineOnly: false });

function setup(tasks: HostTaskInfo[]) {
  return fakeApi({
    'tasks.list': () => ({ tasks, repos: ['main', 'yawble-cloud'] }),
    'tasks.runs': () => [],
    'tasks.preview': preview,
    'tasks.save': () => ({ ok: true, id: 1 }),
    'integrations.connections': () => [],
  });
}
const lastSave = (calls: { method: string; params: any }[]) => calls.filter((c) => c.method === 'tasks.save').at(-1)!.params.def;
const valueOf = (w: ReturnType<typeof mount>, sel: string) => (w.find(sel).element as HTMLInputElement).value;

describe('host task description and check-after (B066)', () => {
  it('a new task saves both fields, trimmed and on one line', async () => {
    const { api, calls } = setup([]);
    const w = mount(TasksPanel, { props: { api, teams } });
    await flushPromises();
    await w.find('[data-test=task-add]').trigger('click');
    await flushPromises();
    expect(valueOf(w, '[data-test=task-description]')).toBe('');
    expect(valueOf(w, '[data-test=task-check-after]')).toBe('');
    await w.find('[data-test=task-name]').setValue('release');
    await w.find('[data-test=task-program]').setValue('pwsh');
    await w.find('[data-test=task-description]').setValue('  Builds, signs and publishes the desktop release  ');
    await w.find('[data-test=task-check-after]').setValue(' The GitHub release page lists the new version ');
    await w.find('[data-test=task-save]').trigger('click');
    await flushPromises();
    expect(lastSave(calls)).toMatchObject({ name: 'release', description: 'Builds, signs and publishes the desktop release', checkAfter: 'The GitHub release page lists the new version' });
  });

  it('empty stays empty: a task saved without them sends neither field', async () => {
    const { api, calls } = setup([]);
    const w = mount(TasksPanel, { props: { api, teams } });
    await flushPromises();
    await w.find('[data-test=task-draft-e2e]').trigger('click');
    await flushPromises();
    await w.find('[data-test=task-description]').setValue('   ');
    await w.find('[data-test=task-save]').trigger('click');
    await flushPromises();
    const def = lastSave(calls);
    expect('description' in def).toBe(false);
    expect('checkAfter' in def).toBe(false);
  });

  it('Edit loads both fields, a change is saved, and clearing them leaves them out', async () => {
    const def = { ...baseDef, description: 'Deploys the cloud', checkAfter: 'https://cloud.example/health answers 200' };
    const { api, calls } = setup([info(def)]);
    const w = mount(TasksPanel, { props: { api, teams } });
    await flushPromises();
    await w.find('[data-test=task-deploy]').findAll('button').find((b) => b.text() === 'Edit')!.trigger('click');
    await flushPromises();
    expect(valueOf(w, '[data-test=task-description]')).toBe('Deploys the cloud');
    expect(valueOf(w, '[data-test=task-check-after]')).toBe('https://cloud.example/health answers 200');

    await w.find('[data-test=task-description]').setValue('Builds the remote-UI image and deploys the cloud');
    await w.find('[data-test=task-save]').trigger('click');
    await flushPromises();
    expect(lastSave(calls)).toMatchObject({ description: 'Builds the remote-UI image and deploys the cloud', checkAfter: 'https://cloud.example/health answers 200' });

    await w.find('[data-test=task-deploy]').findAll('button').find((b) => b.text() === 'Edit')!.trigger('click');
    await flushPromises();
    await w.find('[data-test=task-description]').setValue('');
    await w.find('[data-test=task-check-after]').setValue('');
    await w.find('[data-test=task-save]').trigger('click');
    await flushPromises();
    const cleared = lastSave(calls);
    expect('description' in cleared).toBe(false);
    expect('checkAfter' in cleared).toBe(false);
    expect(cleared).toMatchObject({ name: 'deploy', program: 'pwsh', args: ['-File', 'scripts/deploy.ps1'] });
  });

  it('the task row shows the description and what to check after, and nothing when they are empty', async () => {
    const withBoth = info({ ...baseDef, description: 'Deploys the cloud', checkAfter: 'https://cloud.example/health answers 200' });
    const without = { ...info({ ...baseDef, name: 'plain' }), id: 2 };
    const { api } = setup([withBoth, without]);
    const w = mount(TasksPanel, { props: { api, teams } });
    await flushPromises();
    const row = w.find('[data-test=task-deploy]');
    expect(row.find('[data-test=task-description-text]').text()).toBe('Deploys the cloud');
    expect(row.find('[data-test=task-check-after-text]').text()).toBe('Check after: https://cloud.example/health answers 200');
    const plain = w.find('[data-test=task-plain]');
    expect(plain.find('[data-test=task-description-text]').exists()).toBe(false);
    expect(plain.find('[data-test=task-check-after-text]').exists()).toBe(false);
  });
});
